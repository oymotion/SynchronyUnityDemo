using System;
using System.Collections.Generic;
using System.Numerics;

namespace SensorSdk.ExampleUnity
{
    /// <summary>Band filter for the bio waveforms.</summary>
    public sealed class LiveFilter
    {
        private readonly struct BandEntry
        {
            public readonly string Label;
            public readonly double Lo;
            public readonly double Hi;
            public BandEntry(string label, double lo, double hi)
            {
                Label = label; Lo = lo; Hi = hi;
            }
        }

        // Index 0 = Off
        private static readonly BandEntry[] Bands =
        {
            new BandEntry("Off", 0.0, 0.0),
            new BandEntry("\u03B4 0.5-4Hz", 0.5, 4.0),
            new BandEntry("\u03B8 4-8Hz", 4.0, 8.0),
            new BandEntry("\u03B1 8-13Hz", 8.0, 13.0),
            new BandEntry("\u03B2 13-30Hz", 13.0, 30.0),
            new BandEntry("\u03B3 30-45Hz", 30.0, 45.0),
        };
        private const int Order = 4;

        /// <summary>Band index 0 is Off.</summary>
        public static string[] BandLabels()
        {
            var labels = new string[Bands.Length];
            for (int i = 0; i < Bands.Length; i++)
                labels[i] = Bands[i].Label;
            return labels;
        }

        private struct Biquad
        {
            public double B0, B1, B2, A1, A2;
        }

        private sealed class StreamState
        {
            public int Band;
            public float Rate;
            public Biquad[] Sections = new Biquad[0];
            public double[][] Zi0 = new double[0][];
            public readonly Dictionary<int, double[][]> Channels = new Dictionary<int, double[][]>();
        }

        private readonly object _mutex = new object();
        private int _band;
        private readonly Dictionary<int, StreamState> _streams = new Dictionary<int, StreamState>();  // key: SenDataType value

        public void SetBand(int bandIndex)
        {
            lock (_mutex)
            {
                _band = (bandIndex > 0 && bandIndex < Bands.Length) ? bandIndex : 0;
                _streams.Clear();
            }
        }

        public int Band()
        {
            lock (_mutex)
                return _band;
        }

        /// <summary>Drops all filter state.</summary>
        public void Reset()
        {
            lock (_mutex)
                _streams.Clear();
        }

        /// <summary>Filters one channel batch in place; pass-through when Off.</summary>
        public void Apply(int dataType, int channel, float[] vals, float sampleRate)
        {
            if (vals.Length == 0)
                return;
            lock (_mutex)
            {
                if (_band == 0)
                    return;
                StreamState st;
                if (!_streams.TryGetValue(dataType, out st))
                {
                    st = new StreamState();
                    _streams[dataType] = st;
                }
                if (st.Band != _band || st.Rate != sampleRate)
                {
                    st.Sections = new Biquad[0];
                    st.Zi0 = new double[0][];
                    st.Channels.Clear();
                    st.Band = _band;
                    st.Rate = sampleRate;
                    Biquad[] sections;
                    if (!Design(_band, sampleRate, out sections))
                    {
                        return;
                    }
                    st.Sections = sections;
                    var zi0 = new List<double[]>();
                    double u = 1.0;
                    foreach (Biquad s in sections)
                    {
                        double g = (s.B0 + s.B1 + s.B2) / (1.0 + s.A1 + s.A2);
                        double y = g * u;
                        zi0.Add(new[] { y - s.B0 * u, s.B2 * u - s.A2 * y });
                        u = y;
                    }
                    st.Zi0 = zi0.ToArray();
                }
                if (st.Sections.Length == 0)
                    return;
                double[][] chState;
                if (!st.Channels.TryGetValue(channel, out chState))
                {
                    chState = new double[st.Zi0.Length][];
                    for (int i = 0; i < chState.Length; i++)
                        chState[i] = (double[])st.Zi0[i].Clone();
                    st.Channels[channel] = chState;
                }
                for (int i = 0; i < vals.Length; i++)
                {
                    double x = vals[i];
                    for (int s = 0; s < st.Sections.Length; s++)
                    {
                        Biquad q = st.Sections[s];
                        double[] z = chState[s];
                        double y = q.B0 * x + z[0];
                        z[0] = q.B1 * x - q.A1 * y + z[1];
                        z[1] = q.B2 * x - q.A2 * y;
                        x = y;
                    }
                    vals[i] = (float)x;
                }
            }
        }

        /// <summary>Bandpass design; false when the band is invalid for the rate.</summary>
        private static bool Design(int bandIndex, double fs, out Biquad[] sections)
        {
            sections = new Biquad[0];
            if (bandIndex <= 0 || bandIndex >= Bands.Length || fs <= 0)
                return false;
            double lo = Bands[bandIndex].Lo;
            double hi = Bands[bandIndex].Hi;
            if (hi >= fs / 2.0)
                return false;

            double w1 = 2.0 * fs * Math.Tan(Math.PI * lo / fs);
            double w2 = 2.0 * fs * Math.Tan(Math.PI * hi / fs);
            double bw = w2 - w1;
            double wo = Math.Sqrt(w1 * w2);

            // Prototype poles and zeros
            var poles = new List<Complex>();
            var zeros = new List<Complex>();
            for (int k = 0; k < Order; k++)
            {
                double ang = Math.PI * (2.0 * k + 1 + Order) / (2.0 * Order);
                Complex p = Complex.FromPolarCoordinates(1.0, ang);
                Complex mid = 0.5 * bw * p;
                Complex disc = Complex.Sqrt(mid * mid - wo * wo);
                Complex sp1 = mid + disc;
                Complex sp2 = mid - disc;
                double fs2 = 2.0 * fs;
                poles.Add((fs2 + sp1) / (fs2 - sp1));
                poles.Add((fs2 + sp2) / (fs2 - sp2));
            }
            for (int k = 0; k < Order; k++)
            {
                zeros.Add(Complex.One);
                zeros.Add(-Complex.One);
            }
            // Gain
            double fs2v = 2.0 * fs;
            Complex gain = Math.Pow(bw, Order);
            gain *= Math.Pow(fs2v, Order);
            Complex denom = Complex.One;
            for (int k = 0; k < Order; k++)
            {
                double ang = Math.PI * (2.0 * k + 1 + Order) / (2.0 * Order);
                Complex p = Complex.FromPolarCoordinates(1.0, ang);
                Complex mid = 0.5 * bw * p;
                Complex disc = Complex.Sqrt(mid * mid - wo * wo);
                denom *= (fs2v - (mid + disc)) * (fs2v - (mid - disc));
            }
            double kz = (gain / denom).Real;

            // Biquad grouping
            var result = new List<Biquad>();
            while (poles.Count > 0)
            {
                Complex p = poles[poles.Count - 1];
                poles.RemoveAt(poles.Count - 1);
                int best = 0;
                for (int i = 1; i < poles.Count; i++)
                {
                    if (Complex.Abs(poles[i] - Complex.Conjugate(p))
                        < Complex.Abs(poles[best] - Complex.Conjugate(p)))
                        best = i;
                }
                Complex pc = poles[best];
                poles.RemoveAt(best);
                var zpair = new Complex[2];
                for (int j = 0; j < 2; j++)
                {
                    int bz = 0;
                    for (int i = 1; i < zeros.Count; i++)
                    {
                        double d = Math.Min(Complex.Abs(zeros[i] - p), Complex.Abs(zeros[i] - pc));
                        double db = Math.Min(Complex.Abs(zeros[bz] - p), Complex.Abs(zeros[bz] - pc));
                        if (d < db)
                            bz = i;
                    }
                    zpair[j] = zeros[bz];
                    zeros.RemoveAt(bz);
                }
                result.Add(new Biquad
                {
                    A1 = -(p + pc).Real,
                    A2 = (p * pc).Real,
                    B0 = 1.0,
                    B1 = -(zpair[0] + zpair[1]).Real,
                    B2 = (zpair[0] * zpair[1]).Real,
                });
            }
            Biquad first = result[0];
            first.B0 *= kz;
            first.B1 *= kz;
            first.B2 *= kz;
            result[0] = first;
            sections = result.ToArray();
            return true;
        }
    }
}
