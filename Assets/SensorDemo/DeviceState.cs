using SensorSdk.Capi;
using System;
using System.Collections.Generic;
using System.Text;

namespace SensorSdk.ExampleUnity
{
    /// <summary>Multi-channel circular sample buffer.</summary>
    public sealed class RingBuffer
    {
        public float SampleRate { get; private set; }
        public int Channels { get; private set; }
        public int Length { get; private set; }
        public int WriteIndex { get; private set; }
        public bool Allocated { get; private set; }
        public float[][] Samples = new float[0][];

        /// <summary>Allocates once; later calls are ignored.</summary>
        public void Ensure(int channelCount, float rate, double bufferSeconds)
        {
            if (Allocated || channelCount <= 0 || rate <= 0)
                return;
            Channels = channelCount;
            SampleRate = rate;
            Length = Math.Max(1, (int)(rate * bufferSeconds));
            Samples = new float[Channels][];
            for (int ch = 0; ch < Channels; ch++)
                Samples[ch] = new float[Length];
            WriteIndex = 0;
            Allocated = true;
        }

        /// <summary>Rebuilds at a new sample rate; returns true when rebuilt.</summary>
        public bool Reallocate(float rate, double bufferSeconds)
        {
            if (!Allocated || rate <= 0 || rate == SampleRate)
                return false;
            SampleRate = rate;
            Length = Math.Max(1, (int)(rate * bufferSeconds));
            Samples = new float[Channels][];
            for (int ch = 0; ch < Channels; ch++)
                Samples[ch] = new float[Length];
            WriteIndex = 0;
            return true;
        }

        /// <summary>Writes one batch.</summary>
        public void AppendBatch(List<float[]> channelValues)
        {
            if (!Allocated || channelValues.Count == 0)
                return;
            int n = 0;
            foreach (float[] vals in channelValues)
                n = Math.Max(n, vals.Length);
            n = Math.Min(n, Length);
            if (n <= 0)
                return;
            for (int ch = 0; ch < Math.Min(channelValues.Count, Channels); ch++)
            {
                float[] vals = channelValues[ch];
                int count = Math.Min(vals.Length, n);
                int start = vals.Length - count;
                float[] dst = Samples[ch];
                for (int i = 0; i < count; i++)
                    dst[(WriteIndex + i) % Length] = vals[start + i];
            }
            WriteIndex = (WriteIndex + n) % Length;
        }

        public float Latest(int channel)
        {
            if (!Allocated || channel < 0 || channel >= Channels || Length <= 0)
                return 0.0f;
            return Samples[channel][(WriteIndex + Length - 1) % Length];
        }

        public void Clear()
        {
            foreach (float[] ch in Samples)
                Array.Clear(ch, 0, ch.Length);
            WriteIndex = 0;
        }
    }

    /// <summary>Per-device display state.</summary>
    public sealed class DeviceState
    {
        private const double BioBufferSeconds = 1.0;
        private const double ImuBufferSeconds = 5.0;

        public DeviceState(SensorProfile p)
        {
            Profile = p;
            Name = p.Device.Name;
            Mac = p.Device.Mac;
            RateWindowStartMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        public SensorProfile Profile { get; }
        public string Name;
        public string Mac;
        public bool IsReplay;
        public bool FlowStarted;

        public DeviceInfo Info = new DeviceInfo();
        public bool HasInfo;
        public int LastPower = -1;

        public readonly object BufMutex = new object();
        public readonly RingBuffer Acc = new RingBuffer();
        public readonly RingBuffer Gyro = new RingBuffer();
        public readonly RingBuffer Emg = new RingBuffer();
        public readonly RingBuffer Eeg = new RingBuffer();
        public readonly RingBuffer Ecg = new RingBuffer();
        public readonly RingBuffer Brth = new RingBuffer();
        public readonly RingBuffer Ppg = new RingBuffer();
        public readonly RingBuffer Spo2 = new RingBuffer();
        public readonly RingBuffer Quat = new RingBuffer();
        public readonly RingBuffer Euler = new RingBuffer();
        public readonly List<float> EmgImpedance = new List<float>();
        public readonly List<float> EegImpedance = new List<float>();
        public readonly List<float> EcgImpedance = new List<float>();
        public readonly List<float> BrthImpedance = new List<float>();

        /// <summary>Bio panel mode.</summary>
        public enum BioKind { None, EMG, EEG, PPG }

        // Latest gesture record
        public int Gesture = -1;
        public int RawGesture = -1;
        public int Possibility = -1;
        public int Strength = -1;

        // Actual-rate accounting
        public readonly object RateMutex = new object();
        public readonly Dictionary<int, long> RateCounts = new Dictionary<int, long>();
        public readonly Dictionary<int, double> ActualRates = new Dictionary<int, double>();
        public readonly Dictionary<int, float> NominalRates = new Dictionary<int, float>();
        public readonly Dictionary<int, int> NominalChannels = new Dictionary<int, int>();
        public long RateWindowStartMs;
        // Stream-start wall clock and first-packet delay
        public double StreamStartTimeSec;
        public uint StreamDelayMs;

        public readonly Dictionary<string, int> LostCounts = new Dictionary<string, int>();

        public readonly LiveFilter LiveFilterState = new LiveFilter();

        // Cached switch states: key -> (enabled, checked)
        public Dictionary<string, Bool2> NtfStates = new Dictionary<string, Bool2>();
        public Dictionary<string, Bool2> FilterStates = new Dictionary<string, Bool2>();
        // Cached sample-rate control state
        public List<int> SampleRateOptions = new List<int>();
        public int SampleRateCurrent;

        public BioKind GetBioKind()
        {
            if (Info.PpgSampleRate > 0 || Ppg.Allocated)
                return BioKind.PPG;
            if (Info.EEGChannelCount > 0 || Eeg.Allocated)
                return BioKind.EEG;
            if (Info.EMGChannelCount > 0 || Emg.Allocated)
                return BioKind.EMG;
            return BioKind.None;
        }

        /// <summary>Data entry.</summary>
        public void AppendData(SensorData data)
        {
            bool fresh = data.IsDataValid();
            lock (RateMutex)
            {
                if (data.LostPackageCount > 0)
                    LostCounts[SensorTypeName((int)data.DataType)] = data.LostPackageCount;
                if (data.SampleCount > 0 && data.ChannelCount > 0)
                {
                    long valid = 0;
                    for (int i = 0; i < data.SampleCount; i++)
                    {
                        if (fresh && data.IsChannelEnabled(0) && !data.IsLost(0, i))
                            valid++;
                    }
                    if (valid > 0)
                        RateCounts[(int)data.DataType] = GetOrDefault(RateCounts, (int)data.DataType) + valid;
                }
                if (data.SampleRate > 0)
                    NominalRates[(int)data.DataType] = data.SampleRate;
                if (data.ChannelCount > 0)
                    NominalChannels[(int)data.DataType] = data.ChannelCount;
                if (data.StartTimeSec > 0)
                    StreamStartTimeSec = data.StartTimeSec;
                if (data.Delay > 0)
                    StreamDelayMs = data.Delay;
            }

            if (data.DataType == SenDataType.Imu)
            {
                AppendImuSegments(data);
                return;
            }

            RingBuffer target = null;
            List<float> impedance = null;
            double seconds = ImuBufferSeconds;
            switch (data.DataType)
            {
                case SenDataType.Acc: target = Acc; break;
                case SenDataType.Gyro: target = Gyro; break;
                case SenDataType.Quaternion: target = Quat; break;
                case SenDataType.Euler: target = Euler; break;
                case SenDataType.Gest:
                    if (fresh && data.IsChannelEnabled(0) && data.SampleCount > 0)
                    {
                        Sample s = data.GetChannelSample(0, data.SampleCount - 1);
                        lock (BufMutex)
                        {
                            Gesture = (int)s.Data;
                            RawGesture = s.RawData;
                            Possibility = (int)s.Impedance;
                            Strength = (int)s.Saturation;
                        }
                    }
                    return;
                case SenDataType.Emg:
                    target = Emg; impedance = EmgImpedance; seconds = BioBufferSeconds; break;
                case SenDataType.Eeg:
                    target = Eeg; impedance = EegImpedance;
                    seconds = GetBioKind() == BioKind.PPG ? ImuBufferSeconds : BioBufferSeconds;
                    break;
                case SenDataType.Ppg:
                    target = Ppg; seconds = ImuBufferSeconds; break;
                case SenDataType.Spo2:
                    target = Spo2; seconds = ImuBufferSeconds; break;
                case SenDataType.Ecg:
                    target = Ecg; impedance = EcgImpedance; seconds = BioBufferSeconds; break;
                case SenDataType.Brth:
                    target = Brth; impedance = BrthImpedance; seconds = BioBufferSeconds; break;
            }
            if (target == null)
                return;

            lock (BufMutex)
            {
                target.Ensure(data.ChannelCount, data.SampleRate, seconds);
                if (!target.Allocated)
                    return;
                bool bioTarget = impedance != null || target == Ppg || target == Spo2;
                var channelValues = new List<float[]>(data.ChannelCount);
                for (int ch = 0; ch < data.ChannelCount; ch++)
                {
                    bool maskedIn = data.IsChannelEnabled(ch);
                    var vals = new float[data.SampleCount];
                    for (int i = 0; i < data.SampleCount; i++)
                        vals[i] = (fresh && maskedIn) ? data.GetData(ch, i) : 0.0f;
                    if (bioTarget)
                        LiveFilterState.Apply((int)data.DataType, ch, vals, data.SampleRate);
                    channelValues.Add(vals);
                }
                target.AppendBatch(channelValues);

                if (impedance != null)
                {
                    for (int ch = 0; ch < data.ChannelCount; ch++)
                    {
                        if (!fresh || !data.IsChannelEnabled(ch))
                            continue;
                        while (impedance.Count <= ch)
                            impedance.Add(-1.0f);
                        impedance[ch] = data.GetImpedance(ch, data.SampleCount - 1);
                    }
                }
            }
        }

        private static readonly ImuSeg[] ImuSegs =
        {
            new ImuSeg(SenDataType.Acc, 0, 3),
            new ImuSeg(SenDataType.Gyro, 3, 3),
            new ImuSeg(SenDataType.Euler, 6, 3),
            new ImuSeg(SenDataType.Quaternion, 9, 4),
        };

        private readonly struct ImuSeg
        {
            public readonly SenDataType Type;
            public readonly int Offset;
            public readonly int Count;
            public ImuSeg(SenDataType type, int offset, int count)
            {
                Type = type; Offset = offset; Count = count;
            }
        }

        private void AppendImuSegments(SensorData data)
        {
            int sampleCount = data.SampleCount;
            bool fresh = data.IsDataValid();
            foreach (ImuSeg seg in ImuSegs)
            {
                if (data.ChannelCount < seg.Offset + seg.Count)
                    continue;
                lock (RateMutex)
                {
                    long valid = 0;
                    for (int i = 0; i < sampleCount; i++)
                    {
                        if (fresh && data.IsChannelEnabled(0) && !data.IsLost(seg.Offset, i))
                            valid++;
                    }
                    if (valid > 0)
                        RateCounts[(int)seg.Type] = GetOrDefault(RateCounts, (int)seg.Type) + valid;
                    if (data.SampleRate > 0)
                        NominalRates[(int)seg.Type] = data.SampleRate;
                    NominalChannels[(int)seg.Type] = seg.Count;
                }

                RingBuffer target;
                switch (seg.Type)
                {
                    case SenDataType.Acc: target = Acc; break;
                    case SenDataType.Gyro: target = Gyro; break;
                    case SenDataType.Quaternion: target = Quat; break;
                    default: target = Euler; break;
                }
                lock (BufMutex)
                {
                    target.Ensure(seg.Count, data.SampleRate, ImuBufferSeconds);
                    if (!target.Allocated)
                        continue;
                    var channelValues = new List<float[]>(seg.Count);
                    for (int ch = 0; ch < seg.Count; ch++)
                    {
                        bool maskedIn = data.IsChannelEnabled(ch);
                        var vals = new float[sampleCount];
                        for (int i = 0; i < sampleCount; i++)
                            vals[i] = (fresh && maskedIn) ? data.GetData(seg.Offset + ch, i) : 0.0f;
                        channelValues.Add(vals);
                    }
                    target.AppendBatch(channelValues);
                }
            }
        }

        /// <summary>Rebuilds rings whose nominal rate changed; returns true when any was rebuilt.</summary>
        public bool SyncSampleRates()
        {
            if (!HasInfo)
                return false;
            lock (BufMutex)
            {
                bool changed = false;
                changed |= Eeg.Reallocate(Info.EEGSampleRate,
                    GetBioKind() == BioKind.PPG ? ImuBufferSeconds : BioBufferSeconds);
                changed |= Ecg.Reallocate(Info.ECGSampleRate, BioBufferSeconds);
                changed |= Acc.Reallocate(Info.AccSampleRate, ImuBufferSeconds);
                changed |= Gyro.Reallocate(Info.GyroSampleRate, ImuBufferSeconds);
                changed |= Euler.Reallocate(Info.EulerSampleRate, ImuBufferSeconds);
                changed |= Quat.Reallocate(Info.QuatSampleRate, ImuBufferSeconds);
                return changed;
            }
        }

        public void ClearBuffers()
        {
            lock (BufMutex)
            {
                Acc.Clear(); Gyro.Clear(); Emg.Clear(); Eeg.Clear(); Ecg.Clear();
                Brth.Clear(); Ppg.Clear(); Spo2.Clear(); Quat.Clear(); Euler.Clear();
                Fill(EmgImpedance, -1.0f);
                Fill(EegImpedance, -1.0f);
                Fill(EcgImpedance, -1.0f);
                Fill(BrthImpedance, -1.0f);
                Gesture = RawGesture = Possibility = Strength = -1;
            }
        }

        private static void Fill(List<float> list, float v)
        {
            for (int i = 0; i < list.Count; i++)
                list[i] = v;
        }

        public void UpdateActualRates()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (RateMutex)
            {
                double elapsed = (now - RateWindowStartMs) / 1000.0;
                if (elapsed <= 0.0)
                    return;
                ActualRates.Clear();
                foreach (var kv in RateCounts)
                    ActualRates[kv.Key] = kv.Value / elapsed;
                RateCounts.Clear();
                RateWindowStartMs = now;
            }
        }

        // Display order
        private static readonly TypeLabel[] DisplayOrder =
        {
            new TypeLabel((int)SenDataType.Acc, "ACC"),
            new TypeLabel((int)SenDataType.Gyro, "Gyro"),
            new TypeLabel((int)SenDataType.Imu, "IMU"),
            new TypeLabel((int)SenDataType.Quaternion, "Quat"),
            new TypeLabel((int)SenDataType.Euler, "Euler"),
            new TypeLabel((int)SenDataType.Emg, "EMG"),
            new TypeLabel((int)SenDataType.Eeg, "EEG"),
            new TypeLabel((int)SenDataType.Ppg, "PPG"),
            new TypeLabel((int)SenDataType.Spo2, "SpO2"),
            new TypeLabel((int)SenDataType.Ecg, "ECG"),
            new TypeLabel((int)SenDataType.Brth, "BRTH"),
            new TypeLabel((int)SenDataType.Gest, "GEST"),
        };

        private readonly struct TypeLabel
        {
            public readonly int Type;
            public readonly string Label;
            public TypeLabel(int type, string label) { Type = type; Label = label; }
        }

        public string BuildStatusText()
        {
            string head = IsReplay ? $"Replaying: {Name}"
                        : FlowStarted ? $"Connected: {Name}"
                        : "Not Connected";
            lock (RateMutex)
            {
                var parts = new List<string> { head };
                foreach (TypeLabel tl in DisplayOrder)
                {
                    float rate = GetOrDefault(NominalRates, tl.Type);
                    int ch = GetOrDefault(NominalChannels, tl.Type);
                    if (rate <= 0 && ch <= 0)
                        continue;
                    parts.Add(ch > 0 ? $"{tl.Label} {ch}ch @ {rate}Hz" : $"{tl.Label} @ {rate}Hz");
                }
                return string.Join(" | ", parts);
            }
        }

        public string BuildRateText()
        {
            lock (RateMutex)
            {
                var entries = new List<string>();
                foreach (TypeLabel tl in DisplayOrder)
                {
                    double actual;
                    if (!ActualRates.TryGetValue(tl.Type, out actual))
                        continue;
                    float nominal = GetOrDefault(NominalRates, tl.Type);
                    entries.Add($"{tl.Label} {actual:F1} / {(nominal > 0 ? nominal.ToString() : "--")}Hz");
                }
                if (StreamStartTimeSec > 0)
                {
                    var dt = DateTimeOffset.FromUnixTimeMilliseconds((long)(StreamStartTimeSec * 1000.0))
                                           .ToLocalTime();
                    entries.Add("start " + dt.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                }
                if (StreamDelayMs > 0)
                    entries.Add($"delay {StreamDelayMs}ms");
                return entries.Count == 0 ? string.Empty : "Actual: " + string.Join(" | ", entries);
            }
        }

        /// <summary>Type label.</summary>
        public static string SensorTypeName(int type)
        {
            switch ((SenDataType)type)
            {
                case SenDataType.Acc: return "ACC";
                case SenDataType.Gyro: return "GYRO";
                case SenDataType.Euler: return "EULER";
                case SenDataType.Quaternion: return "QUAT";
                case SenDataType.Gest: return "GEST";
                case SenDataType.Emg: return "EMG";
                case SenDataType.MagAngle: return "MAG";
                case SenDataType.Eeg: return "EEG";
                case SenDataType.Ppg: return "PPG";
                case SenDataType.Spo2: return "SPO2";
                case SenDataType.Ecg: return "ECG";
                case SenDataType.Impedance: return "IMP";
                case SenDataType.Imu: return "IMU";
                case SenDataType.Ads: return "ADS";
                case SenDataType.Brth: return "BRTH";
                case SenDataType.ImpedanceExt: return "IMP_EXT";
                default: return $"TYPE_{type}";
            }
        }

        private static long GetOrDefault(Dictionary<int, long> d, int k)
        {
            long v;
            return d.TryGetValue(k, out v) ? v : 0;
        }

        private static float GetOrDefault(Dictionary<int, float> d, int k)
        {
            float v;
            return d.TryGetValue(k, out v) ? v : 0.0f;
        }

        private static int GetOrDefault(Dictionary<int, int> d, int k)
        {
            int v;
            return d.TryGetValue(k, out v) ? v : 0;
        }
    }

    /// <summary>Mutable (enabled, checked) pair for the cached switch states.</summary>
    public struct Bool2
    {
        public bool Enabled;
        public bool Check;
        public Bool2(bool enabled, bool check) { Enabled = enabled; Check = check; }
    }
}
