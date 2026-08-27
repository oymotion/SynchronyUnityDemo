#undef RUN_IN_START

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using SensorSdk;
using SensorSdk.Capi;
using SensorSdk.ExampleUnity;
using System.Collections;


#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif


/// <summary>Multi-device sensor demo behaviour.</summary>
public sealed partial class SensorDemoBehaviour : MonoBehaviour
{
#if RUN_IN_START
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (FindObjectOfType<SensorDemoBehaviour>() == null)
            new GameObject("SensorDemo").AddComponent<SensorDemoBehaviour>();
    }
#endif

    private const int ScanDevicePeriodMs = 3000;
    private const int PackageCount = 32;
    private const int CmdTimeoutMs = 5000;
    private const int PlotUpdateIntervalMs = 50;
    private const int FftUpdateIntervalMs = 200;
    private const string DemoVersion = "0.1.14";
    private const int PowerRefreshPeriodMs = 60000;
    private const int PowerStableBand = 4;
    private const uint ReplayDelegateTimeoutMs = 5000;

    private static readonly string[] NtfKeys = { "NTF_EEG", "NTF_EMG", "NTF_GEST", "NTF_PPG", "NTF_SPO2", "NTF_IMU" };
    private static readonly string[] FilterKeys = { "FILTER_50HZ", "FILTER_60HZ", "FILTER_HPF", "FILTER_LPF" };
    private static readonly int[] SampleRateCandidates = { 250, 500, 1000, 2000 };
    private static readonly Dictionary<string, string> NtfLabels = new Dictionary<string, string>
    {
        ["NTF_EEG"] = "EEG", ["NTF_EMG"] = "EMG", ["NTF_GEST"] = "GESTURE",
        ["NTF_PPG"] = "PPG", ["NTF_SPO2"] = "SpO2", ["NTF_IMU"] = "IMU",
    };
    private static readonly Dictionary<string, string> FilterLabels = new Dictionary<string, string>
    {
        ["FILTER_50HZ"] = "50Hz", ["FILTER_60HZ"] = "60Hz",
        ["FILTER_HPF"] = "HPF", ["FILTER_LPF"] = "LPF",
    };
    // Display Data Type entries
    private static readonly string[] TypeLabels =
    {
        "Acceleration (ACC)", "Gyroscope (GYRO)", "Quaternion (Quat)", "Euler Angle (Euler)",
    };

    private SensorController _ctrl;
    private string _sdkVersion = "";

    private readonly ConcurrentQueue<Action> _uiQueue = new ConcurrentQueue<Action>();

    private sealed class DeviceEntry
    {
        public string Name = string.Empty;
        public string Mac = string.Empty;
        public int Rssi;
    }

    private sealed class DeviceRow
    {
        public string Mac;
        public string Text = string.Empty;
        public DeviceRow(string mac, string text) { Mac = mac; Text = text; }
    }

    private readonly List<DeviceEntry> _discovered = new List<DeviceEntry>();
    private readonly List<DeviceRow> _rows = new List<DeviceRow>();  // RSSI-sorted
    private readonly Dictionary<string, DeviceState> _deviceStates = new Dictionary<string, DeviceState>();
    private readonly object _statesMutex = new object();
    // Successful user setParam history per device (insertion order, one entry
    // per key), replayed by the app-driven auto-reconnect recovery.
    private readonly Dictionary<string, List<KeyValuePair<string, string>>> _savedParamsByMac = new Dictionary<string, List<KeyValuePair<string, string>>>();
    // Devices whose next successful stream start should be followed by the
    // saved-param restore replay.
    private readonly HashSet<string> _restoreParamsMacs = new HashSet<string>();
    private readonly HashSet<string> _streamingMacs = new HashSet<string>();
    private string _currentMac = string.Empty;
    private string _selectedMac = string.Empty;

    // Replay state
    private readonly List<string> _replayMacs = new List<string>();
    private bool _replayStopRequested;
    private bool _replayPaused;
    private string _binPath = string.Empty;

    // Per-device log/bin export paths reused across reconnects.
    private readonly Dictionary<string, string> _lastLogPaths = new Dictionary<string, string>();
    private readonly Dictionary<string, string> _lastDataPaths = new Dictionary<string, string>();
    private bool _debugLogEnabled = true;
    private bool _binDataEnabled = true;
    private bool _autoReconnect = true;

    private bool _updatingControls;
    private bool _shuttingDown;
    private bool _scanning;
    private bool _analyzeRunning;
    // Cached Clone Data switch state
    private volatile bool _cloneData;

    // Data queue + worker
    private readonly struct QueuedItem
    {
        public readonly string Mac;
        public readonly SensorData Data;
        public QueuedItem(string mac, SensorData data) { Mac = mac; Data = data; }
    }
    private readonly Queue<QueuedItem> _dataQueue = new Queue<QueuedItem>();
    private readonly AutoResetEvent _dataQueueEvent = new AutoResetEvent(false);
    private Thread _dataWorker;
    private volatile bool _dataWorkerStop;

    // FFT spectrum
    private readonly object _fftMutex = new object();
    private bool _fftBusy;
    private bool _fftReady;
    private int _fftTypeIndex = -1;
    private string _fftMac = string.Empty;
    private float[] _fftFreqs = new float[0];
    private List<float[]> _fftMags = new List<float[]>();
    private long _fftLastSubmitMs;

    // Per-channel spectra in the EMG/EEG bio rows: shares the FFT worker
    // above; _bioFftChannels is the current row -> ring channel binding
    // (-1 = no spectrum on that row), _bioFftEpoch invalidates results
    // computed before the latest LayoutBio.
    private bool _bioFftReady;
    private int _bioFftResultEpoch = -1;
    private string _bioFftMac = string.Empty;
    private float[] _bioFftFreqs = new float[0];
    private List<float[]> _bioFftMags = new List<float[]>();
    private long _bioFftLastSubmitMs;
    private int _bioFftEpoch;
    private int[] _bioFftChannels = { -1, -1, -1, -1, -1, -1, -1, -1 };

    // Views (created in Start).
    private readonly List<WaveformView> _bioWaves = new List<WaveformView>();
    private readonly List<SpectrumView> _bioSpectra = new List<SpectrumView>();
    private WaveformView _wave2d;
    private SpectrumView _spectrum;

    // Bio panel state
    private ImpedanceTarget[] _bioTargets = new ImpedanceTarget[8];
    private int _bioPage;

    private struct ImpedanceTarget
    {
        public List<float> Impedance;
        public int Channel;
        public ImpedanceTarget(List<float> impedance, int channel)
        {
            Impedance = impedance; Channel = channel;
        }
    }

    // Page / selector state.
    private int _page;              // 0 = Device, 1 = Bio, 2 = IMU
    private int _typeIndex;         // Display Data Type selector
    private int _filterBand;        // Live Filter selector
    private readonly string[] _filterLabels = LiveFilter.BandLabels();
    private Vector2 _deviceScroll;
    private Vector2 _bioScroll;
    private Vector2 _pageScroll;

    // Cached UI texts
    private string _statusText = "Not Connected";
    private string _rateText = string.Empty;
    private string _lostPacketText = "Packet Loss Stats: None";
    private string _gestureText = "Gesture:\n  gesture: -- (0-8)\n  raw gesture: -- (0-8)" +
                                  "\n  possiblity: -- (0-100)\n  strength: -- (0-100)";
    private string _powerText = "Power: --%";
    private string _modelText = "Model: --";
    private string _hwText = "HW Version: --";
    private string _fwText = "FW Version: --";
    private string _linkText = "Link: --";
    private string _mtuText = "MTU: --";
    private string _bioTitle = "EMG / EEG Waveform";
    private string _pageText = "Page 1 / 1";
    private bool _pageControlsVisible;
    // Warning dialog
    private string _warningTitle;
    private string _warningMessage;
    private Rect _warningRect;

    // NTF/FILTER/sample-rate control state
    private readonly Dictionary<string, Bool2> _ntfUi = new Dictionary<string, Bool2>();
    private readonly Dictionary<string, Bool2> _filterUi = new Dictionary<string, Bool2>();
    private List<int> _rateOptionsUi = new List<int>();
    private int _rateCurrentUi;
    private bool _ntfHasInfo;

    // 3D quaternion cube (created in Start).
    private GameObject _cube;
    private Camera _cubeCamera;
    private Rect _cubeGuiRect;      // last OnGUI rect reserved for the cube
    private bool _cubeHasQuat;

    private float _nextPlotTick;

    private bool _permissionsGranted;

    private void Start()
    {
        // get permissions on Android
        StartCoroutine(RequestPermissionsCoroutine());

        _binPath = DefaultBins.FirstOrDefault(File.Exists) ?? string.Empty;

        for (int i = 0; i < 8; i++)
        {
            _bioWaves.Add(new WaveformView());
            _bioSpectra.Add(new SpectrumView());
        }
        _wave2d = new WaveformView();
        _spectrum = new SpectrumView();

        _ctrl = SensorController.Instance;
        _sdkVersion = _ctrl.GetVersion();

        _ctrl.EnableChanged += enabled => Post(() => OnBtEnableChanged(enabled));
        _ctrl.DeviceFound += devices => Post(() => OnScanResults(devices));

        // Data worker
        _dataWorkerStop = false;
        _dataWorker = new Thread(DrainDataQueue) { IsBackground = true, Name = "DataWorker" };
        _dataWorker.Start();

        if (_debugLogEnabled)
            ApplySdkDebugLog();

        PlatformInit();

        BuildCube();
        RetargetWaveforms();
        _statusText = "sdk " + _sdkVersion + " | BLE " + (_ctrl.IsEnable ? "on" : "OFF");
    }

    private void OnDestroy()
    {
        _shuttingDown = true;
        StopAll();
        if (_dataWorker != null)
        {
            _dataWorkerStop = true;
            _dataQueueEvent.Set();
            _dataWorker.Join(2000);
            _dataWorker = null;
        }
    }

    private IEnumerator RequestPermissionsCoroutine()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
    string[] permissions = {
        Permission.FineLocation,
        Permission.CoarseLocation,
        "android.permission.BLUETOOTH_SCAN",
        "android.permission.BLUETOOTH_CONNECT",
        "android.permission.BLUETOOTH",
        "android.permission.BLUETOOTH_ADMIN"
    };

    var needRequest = new List<string>();
    foreach (var perm in permissions)
    {
        if (!Permission.HasUserAuthorizedPermission(perm))
            needRequest.Add(perm);
    }

    if (needRequest.Count > 0)
    {
        Permission.RequestUserPermissions(needRequest.ToArray());

        float timeout = 10f;
        while (timeout > 0)
        {
            bool allGranted = true;
            foreach (var perm in needRequest)
            {
                if (!Permission.HasUserAuthorizedPermission(perm))
                {
                    allGranted = false;
                    break;
                }
            }
            if (allGranted) break;
            yield return new WaitForSeconds(0.2f);
            timeout -= 0.2f;
        }
    }

    _permissionsGranted = true;
#else
        _permissionsGranted = true;
#endif
        yield return null;
    }


    // Android BLE bridge
    private void PlatformInit()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using (var up = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            {
                var appContext = up.GetStatic<AndroidJavaObject>("currentActivity")
                                     .Call<AndroidJavaObject>("getApplicationContext");
                using (var bridge = new AndroidJavaObject("com.oymotion.sensor.ble.SensorBleBridge", appContext))
                {
                    bridge.Call("setBridge");
                    bridge.Call("requestBlePermissions");
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("BLE bridge init failed: " + e.Message);
        }
#endif
    }

    private void StopAll()
    {
        if (_ctrl == null)
            return;
        try
        {
            if (_ctrl.IsScanning)
                _ctrl.StopScan();
        }
        catch { }
        List<DeviceState> states = SnapshotStates();
        foreach (DeviceState st in states)
        {
            try
            {
                SensorProfile p = st.Profile;
                if (p == null || st.IsReplay)
                    continue;
                if (p.IsDataTransfering)
                    p.StopDataNotificationAsync(5000).Wait(6000);
                p.DisconnectAsync().Wait(6000);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SensorDemo] StopAll " + st.Mac + ": " + ex.Message);
            }
        }
        try
        {
            if (_replayMacs.Count > 0)
            {
                foreach (string mac in _replayMacs.ToArray())
                    _ctrl.StopBinReplay(mac);
                _replayMacs.Clear();
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SensorDemo] StopAll replay: " + ex.Message);
        }
        lock (_statesMutex)
            _deviceStates.Clear();
    }

    private void OnApplicationQuit()
    {
        AppLog("App: demo quitting");
#if !UNITY_EDITOR
        _ctrl?.TearDown();
#endif
    }

    // Recorded captures on the dev machine
    private static readonly string[] DefaultBins =
    {
        @"C:/Users/Lenovo/Documents/sensorsdklog/20260804_143330_0_5_0/OB6000C(1AD6)_data_20260804_143428.bin",
        @"C:/Users/Lenovo/Documents/sensorsdklog/20260804_143330_0_5_0/gForcePro+(4CC3)_data_20260804_143343.bin",
        @"C:/Users/Lenovo/Documents/sensorsdklog/20260730_155522/gForcePro__4CC3__data_20260730_155531.bin",
    };

    // ------------------------------------------------------------------
    // Main-thread plumbing
    // ------------------------------------------------------------------

    private void Post(Action a) => _uiQueue.Enqueue(a);

    private DeviceState StateFor(string mac)
    {
        lock (_statesMutex)
        {
            DeviceState st;
            return _deviceStates.TryGetValue(mac, out st) ? st : null;
        }
    }

    private DeviceState CurrentState() => StateFor(_currentMac);

    private List<DeviceState> SnapshotStates()
    {
        lock (_statesMutex)
            return _deviceStates.Values.ToList();
    }

    /// <summary>Writes one app event line into the SDK log.</summary>
    private void AppLog(string msg, string level = "I", DeviceState st = null)
    {
        try
        {
            DeviceState target = st ?? CurrentState();
            if (target != null)
                target.Profile.Log(msg, level);
            else if (_ctrl != null)
                _ctrl.Log(msg, level);
        }
        catch { }
    }

    private void ShowWarning(string title, string message)
    {
        _warningTitle = title;
        _warningMessage = message;
    }

    // ------------------------------------------------------------------
    // Update: drains the UI queue + the periodic plot tick
    // ------------------------------------------------------------------

    private void Update()
    {
        while (_uiQueue.TryDequeue(out Action a))
        {
            try { a(); }
            catch (Exception ex) { Debug.LogWarning("[SensorDemo] ui action: " + ex.Message); }
        }

        if (_shuttingDown || Time.unscaledTime < _nextPlotTick)
            return;
        _nextPlotTick = Time.unscaledTime + PlotUpdateIntervalMs / 1000.0f;
        OnPlotTick();
    }

    private void OnPlotTick()
    {
        _wave2d.MarkDirty();
        _spectrum.MarkDirty();
        foreach (WaveformView w in _bioWaves)
            w.MarkDirty();
        for (int i = 0; i < _bioSpectra.Count; i++)
        {
            if (i < _bioFftChannels.Length && _bioFftChannels[i] >= 0)
                _bioSpectra[i].MarkDirty();
        }

        DeviceState st = CurrentState();
        PollFftResult();
        MaybeSubmitFft(st);
        PollBioFftResult();
        MaybeSubmitBioFft(st);

        bool bioReady = st != null && ((st.GetBioKind() == DeviceState.BioKind.EMG && st.Emg.Allocated)
                                       || (st.GetBioKind() == DeviceState.BioKind.EEG && st.Eeg.Allocated)
                                       || (st.GetBioKind() == DeviceState.BioKind.PPG && st.Ppg.Allocated));
        if (bioReady && _bioWaves.Count > 0 && !_bioWaves[0].HasSource)
            RetargetBio(st);

        RefreshValueLabels();
        RefreshBioSideTexts();
        RefreshGestureLabel();

        // 3D cube follows the latest quaternion sample.
        if (st != null && st.Quat.Allocated && st.Quat.Channels >= 4)
        {
            lock (st.BufMutex)
            {
                _cube.transform.rotation = new Quaternion(
                    st.Quat.Latest(1), st.Quat.Latest(2), st.Quat.Latest(3), st.Quat.Latest(0));
                _cubeHasQuat = true;
            }
        }
        else if (st == null && _cubeHasQuat)
        {
            _cube.transform.rotation = Quaternion.identity;
            _cubeHasQuat = false;
        }

        // Once-per-second settle: actual rates, lost-packet label, status/rate
        // texts.
        _tickAccumMs += PlotUpdateIntervalMs;
        if (_tickAccumMs >= 1000)
        {
            _tickAccumMs = 0;
            if (st != null)
            {
                st.UpdateActualRates();
                UpdateLostPacketLabel();
                if (st.FlowStarted)
                {
                    _statusText = st.BuildStatusText();
                    _rateText = st.BuildRateText();
                }
            }
        }
    }

    private int _tickAccumMs;

    // ------------------------------------------------------------------
    // Scan
    // ------------------------------------------------------------------

    private void UiStartScan()
    {
        if (_ctrl == null)
            return;
        if (!_ctrl.IsEnable)
        {
            AppLog("User: start scan rejected (Bluetooth disabled)", "W");
            _statusText = "Please enable Bluetooth first";
            return;
        }
        AppLog("User: start scan");
        if (!_ctrl.IsScanning)
            _ctrl.StartScan(ScanDevicePeriodMs);
        _scanning = true;
    }

    private void UiStopScan()
    {
        AppLog("User: stop scan");
        _ctrl.StopScan();
        _scanning = false;
    }

    private void OnBtEnableChanged(bool enabled)
    {
        if (!enabled)
            _statusText = "Please enable Bluetooth first";
    }

    private void OnScanResults(List<BleDevice> devices)
    {
        foreach (BleDevice d in devices)
        {
            int found = _discovered.FindIndex(x => x.Mac == d.Mac);
            if (found < 0)
            {
                _discovered.Add(new DeviceEntry { Name = d.Name, Mac = d.Mac, Rssi = d.Rssi });
                InsertDeviceRowSorted(new DeviceRow(d.Mac,
                    $"RSSI: {d.Rssi}, Name: {d.Name}, Address: {d.Mac}"), d.Rssi);
            }
            else
            {
                _discovered[found].Rssi = d.Rssi;
                lock (_statesMutex)
                    UpdateDeviceItemText(d.Mac, _deviceStates.ContainsKey(d.Mac));
            }
        }
    }

    // ------------------------------------------------------------------
    // Device list
    // ------------------------------------------------------------------

    private void UpdateDeviceItemText(string mac, bool connected)
    {
        DeviceRow row = _rows.FirstOrDefault(r => r.Mac == mac);
        if (row == null)
            return;
        if (_replayMacs.Contains(mac))
        {
            UpdateReplayItemText(mac);
            return;
        }
        string name = mac;
        int rssi = 0;
        foreach (DeviceEntry d in _discovered)
        {
            if (d.Mac == mac) { name = d.Name; rssi = d.Rssi; break; }
        }
        string text = $"RSSI: {rssi}, Name: {name}, Address: {mac}";
        if (_streamingMacs.Contains(mac))
            text = "[Streaming] " + text;
        else if (connected)
            text = "[Connected] " + text;
        row.Text = text;
    }

    private void InsertDeviceRowSorted(DeviceRow row, int rssi)
    {
        int RssiOf(DeviceRow r)
        {
            foreach (DeviceEntry d in _discovered)
                if (d.Mac == r.Mac) return d.Rssi;
            return int.MinValue;
        }
        int pos = _rows.Count;
        for (int i = 0; i < _rows.Count; i++)
        {
            if (RssiOf(_rows[i]) < rssi)
            {
                pos = i;
                break;
            }
        }
        _rows.Insert(pos, row);
    }

    private void UiSelectDevice(string mac)
    {
        if (_replayMacs.Count > 0)
            return;
        DeviceState st = StateFor(mac);
        _selectedMac = mac;
        _currentMac = st != null ? mac : string.Empty;
        RetargetWaveforms();
        RefreshInfoPanel();
    }

    // ------------------------------------------------------------------
    // Connect chain
    // ------------------------------------------------------------------

    private void UiConnectSelected()
    {
        string mac = _selectedMac;
        if (mac.Length == 0)
        {
            AppLog("User: connect rejected (no device selected)", "W");
            _statusText = "Please select a device in the list first";
            return;
        }
        lock (_statesMutex)
        {
            if (_deviceStates.ContainsKey(mac))
                return;
        }
        ConnectDevice(mac);
    }

    private async void ConnectDevice(string mac)
    {
        string name = mac;
        foreach (DeviceEntry d in _discovered)
            if (d.Mac == mac) { name = d.Name; break; }
        AppLog($"User: connect {name} ({mac})");

        SensorProfile profile = _ctrl.RequireSensor(mac);
        HookProfileEvents(profile);
        profile.SetAutoReconnect(_autoReconnect);

        var st = new DeviceState(profile) { Name = name };
        st.LiveFilterState.SetBand(_filterBand);
        lock (_statesMutex)
            _deviceStates[mac] = st;

        _currentMac = mac;
        _selectedMac = mac;
        RetargetWaveforms();
        RefreshInfoPanel();
        _statusText = $"Connecting: {st.Name} ...";

        await RunConnectChain(st);
    }

    private void HookProfileEvents(SensorProfile profile)
    {
        string mac = profile.Device.Mac;
        profile.DataReceived += (_, dataList) => EnqueueData(mac, dataList);
        profile.StateChanged += (_, state) => Post(() => OnStateChanged(mac, state));
        profile.ErrorReceived += (_, msg) => Post(() => OnError(mac, msg));
        profile.PowerChanged += (_, power) => Post(() => OnPowerChanged(mac, power));
        profile.DeviceInfoUpdated += (_, __) => Post(() => OnDeviceInfoUpdate(mac));
        profile.DataTransferStateChanged += (_, on) => Post(() => OnDataTransferStateChanged(mac, on));
        profile.OnAutoReconnect = (p, hasLastSession, answer) =>
        {
            p.Log("App: auto reconnect callback received, restore=" + (hasLastSession ? "True" : "False"));
            Post(() => RecoverDevice(mac, hasLastSession));
            answer(true);
        };
    }

    private async void RecoverDevice(string mac, bool restore)
    {
        // App-driven recovery: re-select the row and re-run the connect
        // chain; the recorded setParam history replays after the stream
        // start.
        lock (_statesMutex)
        {
            if (_deviceStates.ContainsKey(mac))
                return;
        }
        SensorProfile profile = _ctrl.GetSensor(mac);
        if (profile == null)
            return;
        if (restore)
            _restoreParamsMacs.Add(mac);
        string name = mac;
        foreach (DeviceEntry d in _discovered)
            if (d.Mac == mac) { name = d.Name; break; }
        var st = new DeviceState(profile) { Name = name };
        st.LiveFilterState.SetBand(_filterBand);
        lock (_statesMutex)
            _deviceStates[mac] = st;
        _currentMac = mac;
        _selectedMac = mac;
        RetargetWaveforms();
        RefreshInfoPanel();
        await RunConnectChain(st);
    }

    private async Task RunConnectChain(DeviceState st)
    {
        try
        {
            if (!st.Profile.IsReady)
            {
                bool ok = await st.Profile.ConnectAsync();
                if (!ok)
                {
                    AppLog($"App: failed to connect {st.Name} ({st.Mac})", "E", st);
                    _statusText = $"Failed to connect {st.Name}";
                    return;
                }
            }
            if (!st.Profile.HasInited)
            {
                _statusText = $"Initializing {st.Name} ...";
                try
                {
                    await st.Profile.InitAsync(PackageCount, PowerRefreshPeriodMs, CmdTimeoutMs);
                }
                catch (Exception ex)
                {
                    AppLog($"App: failed to initialize {st.Name} ({st.Mac})", "E", st);
                    _statusText = $"Failed to initialize {st.Name}: {ex.Message}";
                    return;
                }
            }
            try
            {
                st.Info = await st.Profile.FetchDeviceInfoAsync(CmdTimeoutMs);
                st.HasInfo = true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SensorDemo] fetchDeviceInfo failed: " + ex.Message);
            }
            if (!st.Profile.IsDataTransfering)
            {
                try
                {
                    await st.Profile.StartDataNotificationAsync(CmdTimeoutMs);
                }
                catch (Exception ex)
                {
                    AppLog($"App: failed to start data stream on {st.Mac}", "E", st);
                    _statusText = "Failed to start data stream: " + ex.Message;
                    return;
                }
            }
            st.FlowStarted = true;
            AppLog($"App: device connected and streaming: {st.Name} ({st.Mac})", "I", st);
            UpdateDeviceItemText(st.Mac, true);
            await ApplySessionParams(st);
            if (_restoreParamsMacs.Remove(st.Mac))
                await RestoreSavedParams(st);
            else
                RefreshControlStates(st);
            if (_currentMac == st.Mac)
            {
                RefreshInfoPanel();
                RetargetWaveforms();
            }

            try
            {
                int result = await st.Profile.GetBatteryLevelAsync(CmdTimeoutMs);
                if (result >= 0
                    && (st.LastPower < 0 || result - st.LastPower >= PowerStableBand
                        || st.LastPower - result >= PowerStableBand))
                {
                    st.LastPower = result;
                    if (st.Mac == _currentMac)
                        _powerText = $"Power: {result}%";
                }
            }
            catch { }
        }
        catch (Exception ex)
        {
            AppLog("App: connect chain failed: " + ex.Message, "E", st);
            _statusText = "Connect failed: " + ex.Message;
        }
    }

    private void UiDisconnectCurrent()
    {
        DeviceState st = CurrentState();
        if (st == null || st.IsReplay)
            return;
        AppLog($"User: disconnect {st.Mac}", "I", st);
        DisableAllControlBoxes();
        if (st.Profile.DeviceState == SenDeviceState.Disconnected)
        {
            OnStateChanged(st.Mac, SenDeviceState.Disconnected);
            return;
        }
        _statusText = "Disconnecting...";
        _ = st.Profile.DisconnectAsync();
    }

    private void DisableAllControlBoxes()
    {
        foreach (string key in NtfKeys)
            _ntfUi[key] = new Bool2(false, _ntfUi.TryGetValue(key, out Bool2 b) && b.Check);
        foreach (string key in FilterKeys)
            _filterUi[key] = new Bool2(false, _filterUi.TryGetValue(key, out Bool2 b) && b.Check);
        _rateOptionsUi = new List<int>();
    }

    // ------------------------------------------------------------------
    // SDK events
    // ------------------------------------------------------------------

    private void EnqueueData(string mac, List<SensorData> dataList)
    {
        bool clone = _cloneData;
        foreach (SensorData d in dataList)
        {
            if (d.SampleCount <= 0 || d.ChannelCount <= 0)
                continue;
            SensorData item = clone ? d.Clone() : d;
            lock (_dataQueue)
            {
                while (_dataQueue.Count >= 1000)
                    _dataQueue.Dequeue();
                _dataQueue.Enqueue(new QueuedItem(mac, item));
            }
        }
        _dataQueueEvent.Set();
    }

    private void DrainDataQueue()
    {
        var pending = new List<QueuedItem>();
        while (true)
        {
            _dataQueueEvent.WaitOne();
            lock (_dataQueue)
            {
                if (_dataWorkerStop && _dataQueue.Count == 0)
                    return;
                while (_dataQueue.Count > 0)
                    pending.Add(_dataQueue.Dequeue());
            }
            foreach (QueuedItem item in pending)
            {
                try
                {
                    DeviceState st = StateFor(item.Mac);
                    st?.AppendData(item.Data);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[SensorDemo] data append: " + ex.Message);
                }
            }
            pending.Clear();
        }
    }

    private void OnStateChanged(string mac, SenDeviceState state)
    {
        DeviceState st = StateFor(mac);
        if (state == SenDeviceState.Ready)
        {
            if (st != null && st.FlowStarted && mac == _currentMac)
                _statusText = st.BuildStatusText();
            return;
        }
        if (state != SenDeviceState.Disconnected || st == null || st.IsReplay)
            return;
        // Every disconnect tears the UI state down; the auto-reconnect
        // recovery (RecoverDevice) rebuilds it once the link is back.
        _restoreParamsMacs.Remove(mac);
        st.NtfStates.Clear();
        st.FilterStates.Clear();
        st.SampleRateOptions.Clear();
        st.SampleRateCurrent = 0;
        lock (_statesMutex)
            _deviceStates.Remove(mac);
        AppLog($"App: device disconnected, removed from UI: {mac}");
        _streamingMacs.Remove(mac);
        UpdateDeviceItemText(mac, false);
        if (_currentMac == mac)
        {
            _currentMac = string.Empty;
            RetargetWaveforms();
            RefreshInfoPanel();
            _statusText = "Disconnected (device)";
            _rateText = string.Empty;
        }
    }

    private void OnError(string mac, string message)
    {
        Debug.LogWarning($"[SensorDemo] error from {mac}: {message}");
        DeviceState st = StateFor(mac);
        AppLog($"App: error callback: {message}", "E", st);
        if (st != null && mac == _currentMac)
            _statusText = "Error: " + message;
    }

    private void OnPowerChanged(string mac, int power)
    {
        DeviceState st = StateFor(mac);
        if (st == null || power < 0)
            return;
        st.LastPower = power;
        if (mac == _currentMac)
            _powerText = $"Power: {power}%";
    }

    private void OnDeviceInfoUpdate(string mac)
    {
        DeviceState st = StateFor(mac);
        if (st == null)
            return;
        st.Info = st.Profile.GetDeviceInfo();
        st.HasInfo = true;
        st.SyncSampleRates();
        if (st.Info.EEGSampleRate > 0 && st.Info.EEGSampleRate != st.SampleRateCurrent)
        {
            st.SampleRateCurrent = st.Info.EEGSampleRate;
            if (mac == _currentMac)
                _rateCurrentUi = st.SampleRateCurrent;
        }
        if (mac == _currentMac)
        {
            _linkText = LinkTextOf(st.Info);
            _mtuText = MtuTextOf(st.Info);
            if (st.FlowStarted)
            {
                _statusText = st.BuildStatusText();
                _rateText = st.BuildRateText();
            }
        }
    }

    private void OnDataTransferStateChanged(string mac, bool isTransferring)
    {
        DeviceState st = StateFor(mac);
        if (isTransferring)
            _streamingMacs.Add(mac);
        else
            _streamingMacs.Remove(mac);
        AppLog($"App: data stream {(isTransferring ? "ON" : "OFF")} {mac}", "I", st);
        if (_replayMacs.Contains(mac))
        {
            UpdateReplayItemText(mac);
            if (!isTransferring)
            {
                // Replay EOF (or a user stop): finish the member here.
                OnReplayDone(mac, _replayStopRequested ? "Replay stopped" : "Replay finished");
            }
            return;
        }
        UpdateDeviceItemText(mac, st != null);
    }

    private void UpdateReplayItemText(string mac)
    {
        DeviceState st = StateFor(mac);
        if (st == null)
            return;
        string prefix = _streamingMacs.Contains(mac) ? "[Streaming] [Replay] " : "[Replay] ";
        DeviceRow row = _rows.FirstOrDefault(r => r.Mac == mac);
        if (row != null)
            row.Text = $"{prefix}{st.Name}, Address: {mac}";
    }

    // ------------------------------------------------------------------
    // setParam / getParam controls
    // ------------------------------------------------------------------

    private static bool IsSetParamError(string result)
    {
        return result.StartsWith("Error") || result.StartsWith("ERROR:");
    }

    private async Task<string> SendSetParam(SensorProfile profile, string key, string value)
    {
        try
        {
            return await profile.SetParamAsync(key, value, CmdTimeoutMs);
        }
        catch (Exception ex)
        {
            return "Error: " + ex.Message;
        }
    }

    private async void OnNtfToggled(string key, bool isOn)
    {
        if (_updatingControls)
            return;
        DeviceState st = CurrentState();
        if (st == null || !st.Profile.IsReady)
            return;
        string value = isOn ? "ON" : "OFF";
        string msg = await SendSetParam(st.Profile, key, value);
        AppLog($"User: setParam({key}, {value}) -> {msg}");
        RecordSavedParam(st.Mac, key, value, msg);
        if (IsSetParamError(msg))
        {
            ShowWarning("Set Parameter Failed", $"Failed to set {key}:\n{msg}");
            RefreshControlStates(st);
            return;
        }
        RefreshControlStates(CurrentState());
        ClearUiData();
    }

    private async void OnFilterToggled(string key, bool isOn)
    {
        if (_updatingControls)
            return;
        DeviceState st = CurrentState();
        if (st == null || !st.Profile.IsReady)
            return;
        string value = isOn ? "ON" : "OFF";
        string msg = await SendSetParam(st.Profile, key, value);
        AppLog($"User: setParam({key}, {value}) -> {msg}");
        RecordSavedParam(st.Mac, key, value, msg);
        if (IsSetParamError(msg))
        {
            ShowWarning("Set Parameter Failed", $"Failed to set {key}:\n{msg}");
            RefreshControlStates(st);
            return;
        }
        RefreshControlStates(CurrentState());
        ClearUiData();
    }

    private async void OnSampleRateChecked(int rate)
    {
        if (_updatingControls)
            return;
        DeviceState st = CurrentState();
        if (st == null || !st.Profile.IsReady)
            return;
        string value = rate.ToString();
        string msg = await SendSetParam(st.Profile, "EEG_SAMPLE_RATE", value);
        AppLog($"User: setParam(EEG_SAMPLE_RATE, {value}) -> {msg}");
        RecordSavedParam(st.Mac, "EEG_SAMPLE_RATE", value, msg);
        if (IsSetParamError(msg))
        {
            ShowWarning("Set Parameter Failed", $"Failed to set EEG_SAMPLE_RATE:\n{msg}");
            RefreshControlStates(st);
            return;
        }
        RefreshControlStates(CurrentState());
        ClearUiData();
    }

    private static async Task<string> SafeGetParam(SensorProfile profile, string key)
    {
        try
        {
            return await profile.GetParamAsync(key, 5000);
        }
        catch (Exception ex)
        {
            return "Error: " + ex.Message;
        }
    }

    private async void RefreshControlStates(DeviceState st)
    {
        if (st == null)
            return;
        string mac = st.Mac;
        string ntfResult = await SafeGetParam(st.Profile, "NTF");
        string filterResult = await SafeGetParam(st.Profile, "FILTER");
        string rateListResult = await SafeGetParam(st.Profile, "EEG_SAMPLE_RATE_LIST");
        string rateResult = await SafeGetParam(st.Profile, "EEG_SAMPLE_RATE");
        st = StateFor(mac);
        if (st == null)
            return;
        ApplyRefreshedControlStates(st, ntfResult, filterResult, rateListResult, rateResult);
    }

    private void ApplyRefreshedControlStates(DeviceState st, string ntfResult, string filterResult,
                                             string rateListResult, string rateResult)
    {
        int emgCh = st.HasInfo ? st.Info.EMGChannelCount : 0;
        int eegCh = st.HasInfo ? st.Info.EEGChannelCount : 0;
        int imuCh = st.HasInfo ? Math.Max(st.Info.AccChannelCount, st.Info.GyroChannelCount) : 0;
        int ppgCh = st.HasInfo ? st.Info.PpgChannelCount : 0;
        int spo2Ch = st.HasInfo ? st.Info.Spo2ChannelCount : 0;
        var channelMap = new Dictionary<string, int>
        {
            ["NTF_EEG"] = eegCh,
            ["NTF_EMG"] = emgCh,
            ["NTF_GEST"] = emgCh,
            ["NTF_PPG"] = ppgCh,
            ["NTF_SPO2"] = spo2Ch,
            ["NTF_IMU"] = imuCh,
        };

        var ntf = new Dictionary<string, Bool2>();
        if (!ntfResult.StartsWith("Error"))
        {
            string[] items = ntfResult.Split('|');
            for (int i = 0; i + 1 < items.Length; i += 2)
            {
                string key = items[i];
                int ch;
                bool enabled = channelMap.TryGetValue(key, out ch) && ch > 0;
                ntf[key] = new Bool2(enabled, enabled && items[i + 1] == "ON");
            }
        }

        var filters = new Dictionary<string, Bool2>();
        bool hasFilter = filterResult.Length > 0 && !filterResult.StartsWith("Error");
        var parsed = new Dictionary<string, string>();
        if (hasFilter)
        {
            string[] items = filterResult.Split('|');
            for (int i = 0; i + 1 < items.Length; i += 2)
                parsed[items[i]] = items[i + 1];
        }
        foreach (string key in FilterKeys)
        {
            string v;
            filters[key] = new Bool2(hasFilter, hasFilter && parsed.TryGetValue(key, out v) && v == "ON");
        }

        // EEG Sample Rate radios
        var rateOptions = new List<int>();
        if (!rateListResult.StartsWith("Error"))
        {
            foreach (string item in rateListResult.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int rate;
                if (int.TryParse(item, out rate))
                    rateOptions.Add(rate);
            }
        }
        int rateCurrent = 0;
        int rc;
        if (!rateResult.StartsWith("Error") && int.TryParse(rateResult, out rc))
            rateCurrent = rc;

        st.NtfStates = ntf;
        st.FilterStates = filters;
        st.SampleRateOptions = rateOptions;
        st.SampleRateCurrent = rateCurrent;
        if (st == CurrentState())
            ApplyControlStates(ntf, filters, rateOptions, rateCurrent);
    }

    private void ApplyControlStates(Dictionary<string, Bool2> ntf,
                                    Dictionary<string, Bool2> filters,
                                    List<int> rateOptions, int rateCurrent)
    {
        _updatingControls = true;
        try
        {
            _ntfHasInfo = ntf.Count > 0;
            foreach (string key in NtfKeys)
            {
                Bool2 b;
                _ntfUi[key] = ntf.TryGetValue(key, out b) ? b : new Bool2(false, false);
            }
            foreach (string key in FilterKeys)
            {
                Bool2 b;
                _filterUi[key] = filters.TryGetValue(key, out b) ? b : new Bool2(false, false);
            }
            _rateOptionsUi = rateOptions;
            _rateCurrentUi = rateCurrent;
        }
        finally
        {
            _updatingControls = false;
        }
    }

    private void ClearUiData()
    {
        DeviceState st = CurrentState();
        if (st != null)
            st.ClearBuffers();
    }

    // ------------------------------------------------------------------
    // Debug log / bin data toggles
    // ------------------------------------------------------------------

    private void ApplySdkDebugLog()
    {
        string version = _sdkVersion.Replace('.', '_');
        string dir = Path.Combine(
            Environment.GetEnvironmentVariable("USERPROFILE")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents",
            "sensorsdklog",
            DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + version);
        _ctrl.SetLogPath(true, dir);
        _ctrl.SetDebugEnabled(true);
        Debug.Log("[SensorDemo] setLogPath -> " + dir);
    }

    private void OnDebugLogToggled(bool enabled)
    {
        _debugLogEnabled = enabled;
        AppLog($"User: SDK debug log {(enabled ? "ON" : "OFF")}");
        if (enabled)
            ApplySdkDebugLog();
        else
            _ctrl.SetDebugEnabled(false);
        string value = enabled ? "True" : "False";
        foreach (DeviceState st in SnapshotStates())
        {
            if (!st.Profile.IsReady || !st.Profile.HasInited)
                continue;
            _ = PushDebugPathParam(st, "DEBUG_LOG_PATH", value);
        }
    }

    private void OnBinDataToggled(bool enabled)
    {
        _binDataEnabled = enabled;
        AppLog($"User: data debug log {(enabled ? "ON" : "OFF")}");
        string value = enabled ? "True" : "False";
        foreach (DeviceState st in SnapshotStates())
        {
            if (!st.Profile.IsReady || !st.Profile.HasInited)
                continue;
            _ = PushDebugPathParam(st, "DEBUG_BLE_DATA_PATH", value);
        }
    }

    private async Task PushDebugPathParam(DeviceState st, string key, string value)
    {
        string msg = await SendSetParam(st.Profile, key, value);
        st.Profile.Log($"App: setParam({key}, {value}) -> {msg}");
        if (IsSetParamError(msg))
            ShowWarning("Set Parameter Failed", $"Failed to set {key}:\n{msg}");
    }

    private async Task ApplySessionParams(DeviceState st)
    {
        // One setParam at a time (the SDK serializes setParam per profile).
        if (_debugLogEnabled)
        {
            string path;
            if (!_lastLogPaths.TryGetValue(st.Mac, out path))
                path = "True";
            await ApplySessionParam(st, "DEBUG_LOG_PATH", path, _lastLogPaths);
        }
        if (_binDataEnabled)
        {
            string path;
            if (!_lastDataPaths.TryGetValue(st.Mac, out path))
                path = "True";
            await ApplySessionParam(st, "DEBUG_BLE_DATA_PATH", path, _lastDataPaths);
        }
    }

    private async Task ApplySessionParam(DeviceState st, string key, string value,
                                         Dictionary<string, string> cache)
    {
        string msg = await SendSetParam(st.Profile, key, value);
        if (IsSetParamError(msg))
            return;
        string cur = await SafeGetParam(st.Profile, key);
        if (cur.Length > 0 && !cur.StartsWith("Error"))
            cache[st.Mac] = cur;
    }

    private void RecordSavedParam(string mac, string key, string value, string result)
    {
        if (mac.Length == 0 || result.StartsWith("Error"))
            return;
        List<KeyValuePair<string, string>> saved;
        if (!_savedParamsByMac.TryGetValue(mac, out saved))
            _savedParamsByMac[mac] = saved = new List<KeyValuePair<string, string>>();
        for (int i = 0; i < saved.Count; i++)
        {
            if (saved[i].Key == key)
            {
                saved[i] = new KeyValuePair<string, string>(key, value);
                return;
            }
        }
        saved.Add(new KeyValuePair<string, string>(key, value));
    }

    private async Task RestoreSavedParams(DeviceState st)
    {
        string mac = st.Mac;
        List<KeyValuePair<string, string>> saved;
        if (!_savedParamsByMac.TryGetValue(mac, out saved))
            return;
        foreach (KeyValuePair<string, string> kv in saved)
        {
            string msg = await SendSetParam(st.Profile, kv.Key, kv.Value);
            AppLog($"App: restore setParam({kv.Key}, {kv.Value}) -> {msg}", "I", StateFor(mac));
        }
        RefreshControlStates(StateFor(mac));
        ClearUiData();
    }

    private void OnAutoReconnectToggled(bool enabled)
    {
        _autoReconnect = enabled;
        AppLog($"User: auto reconnect {(enabled ? "ON" : "OFF")}");
        foreach (DeviceState st in SnapshotStates())
            st.Profile.SetAutoReconnect(enabled);
    }

    // ------------------------------------------------------------------
    // Status texts
    // ------------------------------------------------------------------

    private static string LinkTextOf(DeviceInfo info)
    {
        if (info.PeripheralLatency < 0 || info.ConnectionIntervalMs <= 0)
            return "Link: --";
        return $"Link: {info.ConnectionIntervalMs}ms / latency {info.PeripheralLatency} / timeout {info.SupervisionTimeoutMs}ms";
    }

    private static string MtuTextOf(DeviceInfo info)
        => info.MTUSize <= 0 ? "MTU: --" : $"MTU: {info.MTUSize}";

    private void RefreshInfoPanel()
    {
        DeviceState st = CurrentState();
        if (st != null && st.HasInfo)
        {
            _modelText = "Model: " + st.Info.ModelName;
            _hwText = "HW Version: " + st.Info.HardwareVersion;
            _fwText = "FW Version: " + st.Info.FirmwareVersion;
            _linkText = LinkTextOf(st.Info);
            _mtuText = MtuTextOf(st.Info);
        }
        else
        {
            _modelText = "Model: --";
            _hwText = "HW Version: --";
            _fwText = "FW Version: --";
            _linkText = "Link: --";
            _mtuText = "MTU: --";
        }
        _powerText = st != null && st.LastPower >= 0 ? $"Power: {st.LastPower}%" : "Power: --%";
        _statusText = st != null ? st.BuildStatusText() : "Not Connected";
        _rateText = st != null ? st.BuildRateText() : string.Empty;
        UpdateLostPacketLabel();
        RefreshGestureLabel();
        if (st == null && _cubeHasQuat)
        {
            _cube.transform.rotation = Quaternion.identity;
            _cubeHasQuat = false;
        }
        ApplyControlStates(st != null ? st.NtfStates : new Dictionary<string, Bool2>(),
                           st != null ? st.FilterStates : new Dictionary<string, Bool2>(),
                           st != null ? st.SampleRateOptions : new List<int>(),
                           st != null ? st.SampleRateCurrent : 0);
    }

    private void UpdateLostPacketLabel()
    {
        DeviceState st = CurrentState();
        if (st != null)
        {
            lock (st.RateMutex)
            {
                if (st.LostCounts.Count > 0)
                {
                    _lostPacketText = "Packet Loss Stats: "
                        + string.Join("  ", st.LostCounts.Select(kv => $"{kv.Key}: {kv.Value}").ToArray());
                    return;
                }
            }
        }
        _lostPacketText = "Packet Loss Stats: None";
    }

    private void RefreshGestureLabel()
    {
        DeviceState st = CurrentState();
        if (st != null && st.Gesture >= 0)
        {
            lock (st.BufMutex)
            {
                _gestureText =
                    $"Gesture:\n  gesture: {st.Gesture} (0-8)\n  raw gesture: {st.RawGesture} (0-8)" +
                    $"\n  possiblity: {st.Possibility} (0-100)\n  strength: {st.Strength} (0-100)";
            }
        }
        else
        {
            _gestureText =
                "Gesture:\n  gesture: -- (0-8)\n  raw gesture: -- (0-8)" +
                "\n  possiblity: -- (0-100)\n  strength: -- (0-100)";
        }
    }

    private void RefreshBioSideTexts()
    {
        DeviceState st = CurrentState();
        if (st == null || _bioTargets.Length != _bioWaves.Count)
            return;
        lock (st.BufMutex)
        {
            for (int i = 0; i < _bioWaves.Count; i++)
            {
                ImpedanceTarget t = _bioTargets[i];
                if (t.Impedance == null || t.Channel >= t.Impedance.Count || t.Impedance[t.Channel] < 0)
                    continue;
                double kOhm = t.Impedance[t.Channel] / 1000.0;
                Color color = kOhm <= 500 ? new Color(60/255f, 200/255f, 60/255f)
                            : kOhm <= 999 ? new Color(230/255f, 160/255f, 40/255f)
                                          : new Color(220/255f, 60/255f, 60/255f);
                _bioWaves[i].SetSideText($"{kOhm:F2} KOhm", color);
            }
        }
    }

    // Real-time value labels
    private string[] _valueTexts = new string[0];

    private void RefreshValueLabels()
    {
        DeviceState st = CurrentState();
        if (st == null)
            return;
        RingBuffer buf = BufForType(st, _typeIndex);
        string[] labels = LabelsForType(_typeIndex);
        lock (st.BufMutex)
        {
            if (!buf.Allocated)
                return;
            if (_valueTexts.Length != labels.Length)
                _valueTexts = new string[labels.Length];
            for (int row = 0; row < labels.Length && row < buf.Channels; row++)
                _valueTexts[row] = $"{labels[row]}: {buf.Latest(row):+0.0000;-0.0000;0.0000}";
        }
    }

    private static RingBuffer BufForType(DeviceState st, int typeIndex)
    {
        switch (Math.Max(0, typeIndex))
        {
            case 0: return st.Acc;
            case 1: return st.Gyro;
            case 2: return st.Quat;
            default: return st.Euler;
        }
    }

    private static string[] LabelsForType(int typeIndex)
    {
        switch (Math.Max(0, typeIndex))
        {
            case 0: return new[] { "ACC-X", "ACC-Y", "ACC-Z" };
            case 1: return new[] { "GYRO-X", "GYRO-Y", "GYRO-Z" };
            case 2: return new[] { "W", "X", "Y", "Z" };
            default: return new[] { "Pitch(Y)", "Roll(X)", "Yaw(Z)" };
        }
    }

    // ------------------------------------------------------------------
    // FFT spectrum
    // ------------------------------------------------------------------

    private void MaybeSubmitFft(DeviceState st)
    {
        if (st == null || _fftBusy)
            return;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (now - _fftLastSubmitMs < FftUpdateIntervalMs)
            return;
        RingBuffer buf = BufForType(st, _typeIndex);
        List<float[]> snapshot;
        float rate;
        lock (st.BufMutex)
        {
            if (!buf.Allocated || buf.Length < 16 || buf.SampleRate <= 0)
                return;
            rate = buf.SampleRate;
            snapshot = new List<float[]>(buf.Channels);
            for (int ch = 0; ch < buf.Channels; ch++)
            {
                var row = new float[buf.Length];
                float[] src = buf.Samples[ch];
                for (int i = 0; i < buf.Length; i++)
                    row[i] = src[(buf.WriteIndex + i) % buf.Length];
                snapshot.Add(row);
            }
        }
        _fftLastSubmitMs = now;
        _fftBusy = true;
        int typeIndex = Math.Max(0, _typeIndex);
        string mac = st.Mac;
        Task.Run(() =>
        {
            float[] freqs;
            List<float[]> mags;
            SpectrumCompute.Compute(snapshot, rate, out freqs, out mags);
            lock (_fftMutex)
            {
                _fftFreqs = freqs;
                _fftMags = mags;
                _fftTypeIndex = typeIndex;
                _fftMac = mac;
                _fftReady = true;
            }
            _fftBusy = false;
        });
    }

    private void PollFftResult()
    {
        lock (_fftMutex)
        {
            if (!_fftReady)
                return;
            _fftReady = false;
            DeviceState st = CurrentState();
            if (st == null || _fftTypeIndex != Math.Max(0, _typeIndex) || _fftMac != st.Mac)
                return;
            _spectrum.SetResult(_fftFreqs, _fftMags);
        }
    }

    // Per-channel spectra of the EMG/EEG bio rows: the bound rows' ring
    // channels are snapshotted oldest -> newest and computed on the shared
    // FFT worker; results whose device or bio layout no longer match are
    // dropped.
    private void MaybeSubmitBioFft(DeviceState st)
    {
        if (st == null || _fftBusy)
            return;
        DeviceState.BioKind kind = st.GetBioKind();
        if (kind != DeviceState.BioKind.EMG && kind != DeviceState.BioKind.EEG)
            return;
        var channels = new List<int>();
        foreach (int c in _bioFftChannels)
        {
            if (c >= 0)
                channels.Add(c);
        }
        if (channels.Count == 0)
            return;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (now - _bioFftLastSubmitMs < FftUpdateIntervalMs)
            return;
        RingBuffer buf = kind == DeviceState.BioKind.EMG ? st.Emg : st.Eeg;
        List<float[]> snapshot;
        float rate;
        lock (st.BufMutex)
        {
            if (!buf.Allocated || buf.Length < 16 || buf.SampleRate <= 0)
                return;
            foreach (int c in channels)
            {
                if (c >= buf.Channels)
                    return;
            }
            rate = buf.SampleRate;
            // Reassemble the circular buffer oldest -> newest.
            snapshot = new List<float[]>(channels.Count);
            foreach (int c in channels)
            {
                var row = new float[buf.Length];
                float[] src = buf.Samples[c];
                for (int i = 0; i < buf.Length; i++)
                    row[i] = src[(buf.WriteIndex + i) % buf.Length];
                snapshot.Add(row);
            }
        }
        _bioFftLastSubmitMs = now;
        _fftBusy = true;
        int epoch = _bioFftEpoch;
        string mac = st.Mac;
        Task.Run(() =>
        {
            float[] freqs;
            List<float[]> mags;
            SpectrumCompute.Compute(snapshot, rate, out freqs, out mags);
            lock (_fftMutex)
            {
                _bioFftFreqs = freqs;
                _bioFftMags = mags;
                _bioFftResultEpoch = epoch;
                _bioFftMac = mac;
                _bioFftReady = true;
            }
            _fftBusy = false;
        });
    }

    private void PollBioFftResult()
    {
        lock (_fftMutex)
        {
            if (!_bioFftReady)
                return;
            _bioFftReady = false;
            // Drop stale results: the device or the bio layout may have
            // changed while the worker was computing.
            DeviceState st = CurrentState();
            if (st == null || _bioFftMac != st.Mac || _bioFftResultEpoch != _bioFftEpoch)
                return;
            int row = 0;
            for (int i = 0; i < _bioSpectra.Count; i++)
            {
                if (i >= _bioFftChannels.Length || _bioFftChannels[i] < 0)
                    continue;
                if (row < _bioFftMags.Count)
                    _bioSpectra[i].SetResult(_bioFftFreqs, new List<float[]> { _bioFftMags[row] });
                ++row;
            }
        }
    }

    // ------------------------------------------------------------------
    // Waveform targeting / bio panel layout
    // ------------------------------------------------------------------

    private void RetargetWaveforms()
    {
        DeviceState st = CurrentState();
        string[] labels = LabelsForType(_typeIndex);
        double yLow, yHigh;
        switch (Math.Max(0, _typeIndex))
        {
            case 0: yLow = -8; yHigh = 8; break;
            case 1: yLow = -2000; yHigh = 2000; break;
            case 2: yLow = -1; yHigh = 1; break;
            default: yLow = -180; yHigh = 180; break;
        }
        if (st != null)
        {
            _wave2d.SetSource(BufForType(st, _typeIndex), st.BufMutex, -1);
            _wave2d.SetPlaceholder("Waiting for data ...");
        }
        else
        {
            _wave2d.SetSource(null, null, -1);
            _wave2d.SetPlaceholder("Not connected");
        }
        _wave2d.SetLabels(labels);
        _wave2d.SetFixedYRange(yLow, yHigh);

        _spectrum.SetLabels(labels);
        _spectrum.SetPlaceholder(_wave2d.HasSource ? "Waiting for data ..." : "Not connected");
        _spectrum.ClearResult();

        _valueTexts = new string[labels.Length];
        for (int i = 0; i < labels.Length; i++)
            _valueTexts[i] = labels[i] + ": --";

        RetargetBio(st);
    }

    private void RetargetBio(DeviceState st)
    {
        _bioPage = 0;
        LayoutBio(st);
    }

    private int BioPageCount(DeviceState st)
    {
        if (st == null || st.GetBioKind() != DeviceState.BioKind.EEG)
            return 1;
        int extras = (st.Info.ECGChannelCount > 0 ? 1 : 0)
                   + (st.Info.BRTHChannelCount > 0 ? 1 : 0);
        int perPage = _bioWaves.Count - extras;
        int total = st.Info.EEGChannelCount > 0 ? st.Info.EEGChannelCount
                  : st.Eeg.Allocated ? st.Eeg.Channels : 0;
        return Math.Max(1, (total + perPage - 1) / perPage);
    }

    private void UpdatePageControls(DeviceState st)
    {
        int pages = BioPageCount(st);
        _pageControlsVisible = pages > 1;
        _pageText = $"Page {_bioPage + 1} / {pages}";
    }

    private void LayoutBio(DeviceState st)
    {
        _bioTargets = new ImpedanceTarget[_bioWaves.Count];
        ++_bioFftEpoch;
        _bioFftChannels = new int[_bioWaves.Count];
        for (int i = 0; i < _bioFftChannels.Length; i++)
            _bioFftChannels[i] = -1;
        DeviceState.BioKind kind = st != null ? st.GetBioKind() : DeviceState.BioKind.None;
        string waiting = st != null ? "Waiting for data ..." : "Not connected";

        if (kind == DeviceState.BioKind.EMG && st != null)
        {
            // EMG device
            _bioTitle = "EMG Waveform";
            int emgCh = st.Emg.Allocated ? Math.Min(st.Emg.Channels, _bioWaves.Count) : 0;
            for (int i = 0; i < _bioWaves.Count; i++)
            {
                if (i < emgCh)
                {
                    _bioWaves[i].SetSource(st.Emg, st.BufMutex, i);
                    _bioWaves[i].SetLabels(new[] { $"EMG-{i + 1}" });
                    _bioWaves[i].SetPlaceholder(string.Empty);
                    SetBioSpectrum(i, i, $"EMG-{i + 1}");
                    _bioTargets[i] = new ImpedanceTarget(st.EmgImpedance, i);
                }
                else
                {
                    ClearBioSlot(i, waiting);
                }
            }
        }
        else if (kind == DeviceState.BioKind.EEG && st != null)
        {
            // EEG device
            _bioTitle = "EEG + ECG + BRTH Waveform";
            bool hasECG = st.Info.ECGChannelCount > 0 || st.Ecg.Allocated;
            bool hasBRTH = st.Info.BRTHChannelCount > 0 || st.Brth.Allocated;
            int perPage = _bioWaves.Count - (hasECG ? 1 : 0) - (hasBRTH ? 1 : 0);
            int total = st.Info.EEGChannelCount > 0 ? st.Info.EEGChannelCount
                      : st.Eeg.Allocated ? st.Eeg.Channels : 0;
            int pages = Math.Max(1, (total + perPage - 1) / perPage);
            _bioPage = Mathf.Clamp(_bioPage, 0, pages - 1);
            int startCh = _bioPage * perPage;
            int ecgIndex = _bioWaves.Count - 1 - (hasBRTH ? 1 : 0);
            int brthIndex = _bioWaves.Count - 1;
            for (int i = 0; i < _bioWaves.Count; i++)
            {
                int eegCh = startCh + i;
                if (i < perPage && eegCh < total && st.Eeg.Allocated)
                {
                    _bioWaves[i].SetSource(st.Eeg, st.BufMutex, eegCh);
                    _bioWaves[i].SetLabels(new[] { $"EEG-{eegCh + 1}" });
                    _bioWaves[i].SetPlaceholder(string.Empty);
                    SetBioSpectrum(i, eegCh, $"EEG-{eegCh + 1}");
                    _bioTargets[i] = new ImpedanceTarget(st.EegImpedance, eegCh);
                }
                else if (hasECG && i == ecgIndex && st.Ecg.Allocated)
                {
                    _bioWaves[i].SetSource(st.Ecg, st.BufMutex, 0);
                    _bioWaves[i].SetLabels(new[] { "ECG" });
                    _bioWaves[i].SetPlaceholder(string.Empty);
                    SetBioSpectrum(i, -1, string.Empty);
                    _bioTargets[i] = new ImpedanceTarget(st.EcgImpedance, 0);
                }
                else if (hasBRTH && i == brthIndex && st.Brth.Allocated)
                {
                    _bioWaves[i].SetSource(st.Brth, st.BufMutex, 0);
                    _bioWaves[i].SetLabels(new[] { "BRTH" });
                    _bioWaves[i].SetPlaceholder(string.Empty);
                    SetBioSpectrum(i, -1, string.Empty);
                    _bioTargets[i] = new ImpedanceTarget(st.BrthImpedance, 0);
                }
                else
                {
                    bool noSuchChannel = i < perPage && eegCh >= total;
                    ClearBioSlot(i, noSuchChannel ? string.Empty : waiting);
                }
            }
        }
        else if (kind == DeviceState.BioKind.PPG && st != null)
        {
            // PPG device: fixed 6 plots
            _bioTitle = "EEG + PPG + SpO2 Waveform";
            var plotConfig = new PpgPlot[]
            {
                new PpgPlot(st.Eeg, 0, "fp1", true),
                new PpgPlot(st.Eeg, 1, "fp2", true),
                new PpgPlot(st.Ppg, 0, "red_led", false),
                new PpgPlot(st.Ppg, 1, "ir_led", false),
                new PpgPlot(st.Spo2, 0, "spo2", false),
                new PpgPlot(st.Spo2, 1, "heart_rate", false),
            };
            for (int i = 0; i < _bioWaves.Count; i++)
            {
                bool bound = false;
                if (i < plotConfig.Length)
                {
                    PpgPlot cfg = plotConfig[i];
                    if (cfg.Buffer.Allocated && cfg.Channel < cfg.Buffer.Channels)
                    {
                        _bioWaves[i].SetSource(cfg.Buffer, st.BufMutex, cfg.Channel, i);
                        _bioWaves[i].SetLabels(new[] { cfg.Label });
                        _bioWaves[i].SetPlaceholder(string.Empty);
                        SetBioSpectrum(i, -1, string.Empty);
                        _bioTargets[i] = cfg.IsEeg
                            ? new ImpedanceTarget(st.EegImpedance, cfg.Channel)
                            : new ImpedanceTarget(null, 0);
                        bound = true;
                    }
                }
                if (!bound)
                {
                    ClearBioSlot(i, i < plotConfig.Length ? waiting : string.Empty);
                }
            }
        }
        else
        {
            _bioTitle = "EMG / EEG Waveform";
            for (int i = 0; i < _bioWaves.Count; i++)
                ClearBioSlot(i, waiting);
        }
        UpdatePageControls(st);
    }

    private struct PpgPlot
    {
        public RingBuffer Buffer;
        public int Channel;
        public string Label;
        public bool IsEeg;
        public PpgPlot(RingBuffer buffer, int channel, string label, bool isEeg)
        {
            Buffer = buffer; Channel = channel; Label = label; IsEeg = isEeg;
        }
    }

    // Row spectrum binding: channel >= 0 shows the row's spectrum, -1 hides
    // it (the waveform then spans the full row width).
    private void SetBioSpectrum(int row, int channel, string label)
    {
        if (channel >= 0)
        {
            _bioFftChannels[row] = channel;
            _bioSpectra[row].SetColorIndex(channel);
            _bioSpectra[row].SetLabels(new[] { label });
            _bioSpectra[row].SetPlaceholder(string.Empty);
        }
        else
        {
            _bioSpectra[row].ClearResult();
        }
    }

    private void ClearBioSlot(int i, string placeholder)
    {
        _bioWaves[i].SetSource(null, null, i);
        _bioWaves[i].SetLabels(new string[0]);
        _bioWaves[i].SetPlaceholder(placeholder);
        _bioWaves[i].SetSideText(string.Empty, Color.white);
        SetBioSpectrum(i, -1, string.Empty);
        _bioTargets[i] = new ImpedanceTarget(null, 0);
    }

    // ------------------------------------------------------------------
    // 3D quaternion cube
    // ------------------------------------------------------------------

    private void BuildCube()
    {
        _cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        _cube.name = "SensorDemoCube";
        _cube.layer = 8;
        _cube.transform.localScale = Vector3.one * 2;

        // Six faces, one submesh + one flat color each
        var mf = _cube.GetComponent<MeshFilter>();
        var mr = _cube.GetComponent<MeshRenderer>();
        if (mf != null && mr != null)
        {
            mf.mesh = BuildColoredCubeMesh();
            mr.material = CreateFlatMaterial(Color.white);
        }

        // Body axes (X red / Y green / Z blue)
        AddCubeAxis(Vector3.right, new Color(0.9f, 0.25f, 0.25f));
        AddCubeAxis(Vector3.up, new Color(0.3f, 0.85f, 0.35f));
        AddCubeAxis(Vector3.forward, new Color(0.3f, 0.5f, 0.95f));

        var camGo = new GameObject("SensorDemoCubeCamera");
        _cubeCamera = camGo.AddComponent<Camera>();
        _cubeCamera.clearFlags = CameraClearFlags.SolidColor;
        _cubeCamera.backgroundColor = WaveformView.BackgroundColor;
        _cubeCamera.cullingMask = 1 << 8;
        _cubeCamera.orthographic = true;
        _cubeCamera.orthographicSize = 1.7f;
        camGo.transform.position = new Vector3(0f, 0f, -4f);
        _cubeCamera.enabled = false;    // only on the IMU page
    }

    private static readonly Color32[] CubeFaceColors =
    {
        new Color32(0, 180, 180, 255),     // -Z
        new Color32(200, 60, 200, 255),    // +Z
        new Color32(220, 200, 40, 255),    // -Y
        new Color32(200, 60, 60, 255),     // +Y
        new Color32(60, 160, 60, 255),     // -X
        new Color32(60, 100, 220, 255),    // +X
    };
    
    private static Material CreateFlatMaterial(Color32 tint)
    {
        Shader shader = Shader.Find("SensorDemo/CubeFace");
        if (shader == null)
            shader = Shader.Find("Unlit/Color");
        if (shader == null)
            shader = Shader.Find("Standard");
        var mat = new Material(shader);
        if (mat.HasProperty("_BaseColor"))
            mat.SetColor("_BaseColor", tint);
        else
            mat.color = tint;
        return mat;
    }

    private static Mesh BuildColoredCubeMesh()
    {
        const float s = 0.5f;
        Vector3[][] faceCorners =
        {
            new[] { new Vector3(-s, -s, -s), new Vector3(s, -s, -s), new Vector3(s, s, -s), new Vector3(-s, s, -s) },
            new[] { new Vector3(s, -s, s), new Vector3(-s, -s, s), new Vector3(-s, s, s), new Vector3(s, s, s) },
            new[] { new Vector3(-s, -s, s), new Vector3(s, -s, s), new Vector3(s, -s, -s), new Vector3(-s, -s, -s) },
            new[] { new Vector3(-s, s, -s), new Vector3(s, s, -s), new Vector3(s, s, s), new Vector3(-s, s, s) },
            new[] { new Vector3(-s, -s, s), new Vector3(-s, -s, -s), new Vector3(-s, s, -s), new Vector3(-s, s, s) },
            new[] { new Vector3(s, -s, -s), new Vector3(s, -s, s), new Vector3(s, s, s), new Vector3(s, s, -s) },
        };
        Vector3[] faceNormals =
        {
            Vector3.back, Vector3.forward, Vector3.down, Vector3.up, Vector3.left, Vector3.right,
        };

        var verts = new List<Vector3>();
        var norms = new List<Vector3>();
        var cols = new List<Color32>();
        var tris = new List<int>();
        for (int f = 0; f < 6; ++f)
        {
            int start = verts.Count;
            for (int k = 0; k < 4; ++k)
            {
                verts.Add(faceCorners[f][k]);
                norms.Add(faceNormals[f]);
                cols.Add(CubeFaceColors[f]);
            }
            // Unity front faces are clockwise seen from outside; the corner
            // lists run counter-clockwise, so the triangle winding is flipped.
            tris.Add(start + 0); tris.Add(start + 2); tris.Add(start + 1);
            tris.Add(start + 0); tris.Add(start + 3); tris.Add(start + 2);
        }
        var mesh = new Mesh { name = "SensorDemoColoredCube" };
        mesh.SetVertices(verts);
        mesh.SetNormals(norms);
        mesh.SetColors(cols);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateBounds();
        return mesh;
    }

    private void AddCubeAxis(Vector3 dir, Color color)
    {
        var axis = GameObject.CreatePrimitive(PrimitiveType.Cube);
        axis.name = "SensorDemoCubeAxis";
        axis.layer = 8;
        Vector3 parentScale = _cube.transform.localScale;
        const float len = 0.9f;
        const float thick = 0.06f;
        Vector3 size = new Vector3(
            (dir.x != 0 ? len : thick) / parentScale.x,
            (dir.y != 0 ? len : thick) / parentScale.y,
            (dir.z != 0 ? len : thick) / parentScale.z);
        axis.transform.SetParent(_cube.transform, false);
        axis.transform.localScale = size;
        float halfLen = (dir.x != 0 ? size.x : dir.y != 0 ? size.y : size.z) * 0.5f;
        axis.transform.localPosition = dir * (0.5f + halfLen);
        var mr = axis.GetComponent<MeshRenderer>();
        if (mr != null)
            mr.material = CreateFlatMaterial(color);
    }

    private void UpdateCubeCamera()
    {
        if (_cubeCamera == null)
            return;
        bool visible = _page == 2 && _cubeGuiRect.width > 4 && !_shuttingDown;
        _cubeCamera.enabled = visible;
        if (visible)
        {
            _cubeCamera.pixelRect = new Rect(
                _cubeGuiRect.x,
                Screen.height - _cubeGuiRect.yMax,
                _cubeGuiRect.width,
                _cubeGuiRect.height);
        }
    }

    // ------------------------------------------------------------------
    // Synchronized multi-device controls
    // ------------------------------------------------------------------

    private List<DeviceState> MultiParticipants()
    {
        var list = new List<DeviceState>();
        foreach (DeviceState st in SnapshotStates())
        {
            if (st.Profile.IsReady && st.Profile.HasInited)
                list.Add(st);
        }
        return list;
    }

    private static List<SensorProfile> ProfilesOf(List<DeviceState> states)
        => states.Select(st => st.Profile).ToList();

    private static List<string> FailedMacs(Dictionary<string, bool> result)
        => result.Where(kv => !kv.Value).Select(kv => kv.Key).ToList();

    private void UiMultiSync()
    {
        if (SnapshotStates().Any(st => st.Profile.IsDataTransfering))
            UiMultiStop();
        else
            UiMultiStart();
    }

    private async void UiMultiStart()
    {
        List<DeviceState> participants = MultiParticipants();
        if (participants.Count == 0)
        {
            AppLog("User: multi start rejected (no connected device)", "W");
            _statusText = "No connected device to sync-start";
            return;
        }
        AppLog($"User: multi start on {participants.Count} device(s)");
        try
        {
            var transferring = participants.Where(st => st.Profile.IsDataTransfering).ToList();
            if (transferring.Count > 0)
            {
                Dictionary<string, bool> stopResult =
                    await _ctrl.MultiStopDataNotificationAsync(ProfilesOf(transferring));
                List<string> stopFailed = FailedMacs(stopResult);
                if (stopFailed.Count > 0)
                {
                    string stopMacs = string.Join(", ", stopFailed.ToArray());
                    AppLog($"App: multi stop failed on: {stopMacs}", "W");
                    _statusText = "Multi stop failed on: " + stopMacs;
                    return;
                }
            }
            Dictionary<string, bool> result = await MultiStartWithModelParams(participants);
            List<string> failed = FailedMacs(result);
            if (failed.Count > 0)
            {
                string macs = string.Join(", ", failed.ToArray());
                AppLog($"App: multi start failed on: {macs}", "W");
                _statusText = "Multi start failed on: " + macs;
                return;
            }
            AppLog($"App: multi start OK: {result.Count} device(s) started");
            _statusText = $"Multi start: {result.Count} device(s) started";
        }
        catch (Exception ex)
        {
            _statusText = "Multi start failed: " + ex.Message;
            AppLog("App: " + _statusText, "W");
        }
    }

    // Model-aware start parameters
    private Task<Dictionary<string, bool>> MultiStartWithModelParams(List<DeviceState> participants)
    {
        var modelNames = new HashSet<string>();
        foreach (DeviceState st in participants)
            modelNames.Add(st.HasInfo ? st.Info.ModelName : null);
        if (modelNames.Count == 1 && !modelNames.Contains(null))
            return _ctrl.MultiStartDataNotificationAsync(ProfilesOf(participants));
        return _ctrl.MultiStartDataNotificationAsync(ProfilesOf(participants), 60000, -1, 5);
    }

    private async void UiMultiStop()
    {
        List<DeviceState> participants = MultiParticipants();
        if (participants.Count == 0)
        {
            AppLog("User: multi stop rejected (no connected device)", "W");
            _statusText = "No connected device to sync-stop";
            return;
        }
        AppLog($"User: multi stop on {participants.Count} device(s)");
        try
        {
            Dictionary<string, bool> result =
                await _ctrl.MultiStopDataNotificationAsync(ProfilesOf(participants));
            List<string> failed = FailedMacs(result);
            if (failed.Count > 0)
            {
                string macs = string.Join(", ", failed.ToArray());
                AppLog($"App: multi stop failed on: {macs}", "W");
                _statusText = "Multi stop failed on: " + macs;
                return;
            }
            AppLog($"App: multi stop OK: {result.Count} device(s) stopped");
            _statusText = $"Multi stop: {result.Count} device(s) stopped";
        }
        catch (Exception ex)
        {
            _statusText = "Multi stop failed: " + ex.Message;
            AppLog("App: " + _statusText, "W");
        }
    }

    // ------------------------------------------------------------------
    // Bin replay / analyze
    // ------------------------------------------------------------------

    private void UiStartReplay()
    {
        if (BinFileDialog.IsSupported)
        {
            string picked = BinFileDialog.OpenBin(false, DefaultBinDir());
            if (picked == null)
                return;
            _binPath = picked;
        }
        if (_binPath.Contains(';'))
        {
            _statusText = "Use Multi Replay for multiple files";
            return;
        }
        StartReplay();
    }

    // Multi Replay Bin button
    private void UiMultiReplay()
    {
        if (BinFileDialog.IsSupported)
        {
            string picked = BinFileDialog.OpenBin(true, DefaultBinDir());
            if (picked == null)
                return;
            _binPath = picked;
        }
        StartReplay();
    }

    // The SDK debug-log root, where session bin captures are exported.
    private static string DefaultBinDir()
    {
        string dir = Path.Combine(
            Environment.GetEnvironmentVariable("USERPROFILE")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents",
            "sensorsdklog");
        return Directory.Exists(dir) ? dir : string.Empty;
    }

    private void StartReplay()
    {
        lock (_statesMutex)
        {
            if (_deviceStates.Count > 0)
            {
                _statusText = "Please disconnect all devices before replaying a bin file";
                return;
            }
        }
        if (_replayMacs.Count > 0)
            return;
        if (_ctrl.IsScanning)
        {
            _ctrl.StopScan();
            _scanning = false;
        }
        var paths = new List<string>();
        foreach (string part in _binPath.Split(';'))
        {
            string p = part.Trim();
            if (p.Length > 0)
                paths.Add(p);
        }
        if (paths.Count == 0)
        {
            _statusText = "Type the bin path into the bin field first";
            return;
        }
        if (paths.Count > 1)
        {
            StartMultiReplay(paths);
            return;
        }
        string path = paths[0];
        if (!File.Exists(path))
        {
            _statusText = "bin not found: " + path;
            return;
        }
        AppLog($"User: replay bin file: {path}");
        BinFileInfo info = _ctrl.GetBinFileInfo(path);
        if (info == null || !info.Valid || info.Mac.Length == 0)
        {
            AppLog($"App: invalid bin file (no config record): {path}", "W");
            _statusText = "Invalid bin file: no config record found";
            return;
        }
        SensorProfile profile = _ctrl.ReplayBinFile(path, info.Mac, true, ReplayDelegateTimeoutMs);
        if (profile == null)
        {
            _statusText = "Replay failed to start";
            return;
        }
        HookProfileEvents(profile);

        _replayMacs.Add(info.Mac);
        _replayStopRequested = false;
        _replayPaused = false;

        var st = new DeviceState(profile)
        {
            IsReplay = true,
            FlowStarted = true,
            Name = info.DeviceName,
        };
        st.LiveFilterState.SetBand(_filterBand);
        lock (_statesMutex)
            _deviceStates[info.Mac] = st;

        var row = new DeviceRow(info.Mac, $"[Replay] {st.Name}, Address: {info.Mac}");
        _rows.Add(row);
        _selectedMac = info.Mac;
        _currentMac = info.Mac;

        // Sync the sample-rate radio checked state
        if (info.DeviceInfo.EEGSampleRate > 0)
        {
            st.SampleRateCurrent = info.DeviceInfo.EEGSampleRate;
            _rateCurrentUi = st.SampleRateCurrent;
        }

        RetargetWaveforms();
        RefreshInfoPanel();
        _statusText = $"Replaying: {Path.GetFileName(path)} (duration {info.DurationSec:F1}s, realtime) ...";
    }

    // Multi-bin synchronized replay
    private void StartMultiReplay(List<string> paths)
    {
        AppLog($"User: replay {paths.Count} bin files: {string.Join("; ", paths.ToArray())}");
        var infos = new List<BinFileInfo>();
        var macs = new List<string>();
        foreach (string path in paths)
        {
            BinFileInfo info = _ctrl.GetBinFileInfo(path);
            if (info == null || !info.Valid || info.Mac.Length == 0)
            {
                AppLog($"App: invalid bin file (no config record): {path}", "W");
                _statusText = "Invalid bin file: no config record found";
                return;
            }
            if (macs.Contains(info.Mac))
            {
                AppLog($"App: duplicate replay mac {info.Mac}: {path}", "W");
                _statusText = "Duplicate device in bin files: " + info.Mac;
                return;
            }
            infos.Add(info);
            macs.Add(info.Mac);
        }
        SensorProfile[] profiles = _ctrl.MultiReplayBinFile(
            paths.ToArray(), macs.ToArray(), true, ReplayDelegateTimeoutMs);
        BinFileInfo firstInfo = null;
        for (int i = 0; i < profiles.Length; i++)
        {
            SensorProfile member = profiles[i];
            if (member == null)
            {
                AppLog($"App: replay failed to start: {paths[i]}", "W");
                continue;
            }
            HookProfileEvents(member);

            BinFileInfo info = infos[i];
            _replayMacs.Add(info.Mac);
            if (firstInfo == null)
                firstInfo = info;

            var st = new DeviceState(member)
            {
                IsReplay = true,
                FlowStarted = true,
                Name = info.DeviceName,
            };
            st.LiveFilterState.SetBand(_filterBand);
            lock (_statesMutex)
                _deviceStates[info.Mac] = st;

            var row = new DeviceRow(info.Mac, $"[Replay] {st.Name}, Address: {info.Mac}");
            _rows.Add(row);
        }
        if (_replayMacs.Count == 0)
        {
            _statusText = "Replay failed to start";
            return;
        }
        _replayStopRequested = false;
        _replayPaused = false;
        _selectedMac = _replayMacs[0];
        _currentMac = _replayMacs[0];

        // Sync the sample-rate radio checked state
        DeviceState first = StateFor(_replayMacs[0]);
        if (firstInfo.DeviceInfo.EEGSampleRate > 0)
        {
            first.SampleRateCurrent = firstInfo.DeviceInfo.EEGSampleRate;
            _rateCurrentUi = first.SampleRateCurrent;
        }

        RetargetWaveforms();
        RefreshInfoPanel();
        _statusText = $"Replaying: {_replayMacs.Count} bin files (realtime) ...";
    }

    private void UiReplayPauseResume()
    {
        if (_replayMacs.Count == 0)
            return;
        string action = _replayPaused ? "resume" : "pause";
        string result = "OK";
        foreach (string mac in _replayMacs)
        {
            string r = _replayPaused
                ? _ctrl.ResumeBinReplay(mac)
                : _ctrl.PauseBinReplay(mac);
            if (r != "OK")
                result = r;
        }
        AppLog($"User: {action} replay -> {result}", result == "OK" ? "I" : "W", StateFor(_replayMacs[0]));
        if (result != "OK")
        {
            _statusText = "Replay pause/resume failed: " + result;
            return;
        }
        _replayPaused = !_replayPaused;
        _statusText = _replayPaused ? "Replay paused" : "Replaying ...";
    }

    private void UiReplayStop()
    {
        if (_replayMacs.Count == 0)
            return;
        _replayStopRequested = true;
        string result = "OK";
        foreach (string mac in _replayMacs.ToArray())
        {
            string r = _ctrl.StopBinReplay(mac);
            AppLog($"User: stop replay -> {r}", r == "OK" ? "I" : "W", StateFor(mac));
            if (r != "OK")
            {
                result = r;
                continue;
            }
            OnReplayDone(mac, "Replay stopped");
        }
        if (result != "OK")
        {
            _statusText = "Stop replay failed: " + result;
            _replayStopRequested = false;
        }
    }

    private void OnReplayDone(string mac, string message)
    {
        if (!_replayMacs.Contains(mac))
            return;
        AppLog($"App: replay done: {message} ({mac})", "I", StateFor(mac));
        bool wasCurrent = _currentMac == mac;
        lock (_statesMutex)
            _deviceStates.Remove(mac);
        _streamingMacs.Remove(mac);
        DeviceRow row = _rows.FirstOrDefault(r => r.Mac == mac);
        if (row != null)
            _rows.Remove(row);
        _replayMacs.Remove(mac);
        if (_currentMac == mac)
            _currentMac = _replayMacs.Count > 0 ? _replayMacs[0] : string.Empty;
        if (_selectedMac == mac)
            _selectedMac = _replayMacs.Count > 0 ? _replayMacs[0] : string.Empty;
        if (_replayMacs.Count > 0)
        {
            if (wasCurrent)
            {
                RetargetWaveforms();
                RefreshInfoPanel();
            }
            return;
        }
        _replayStopRequested = false;
        _replayPaused = false;
        RetargetWaveforms();
        RefreshInfoPanel();
        _statusText = message;
    }

    private async void UiAnalyzeBin()
    {
        if (_analyzeRunning)
            return;
        string path = _binPath;
        if (!File.Exists(path))
        {
            _statusText = "bin not found: " + path;
            return;
        }
        AppLog($"User: analyze bin file: {path}");
        _analyzeRunning = true;
        _statusText = $"Analyzing: {Path.GetFileName(path)} ...";

        string csv = path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
            ? path.Substring(0, path.Length - 4) + ".csv"
            : path + ".csv";
        string result = await Task.Run(() => _ctrl.ParseBinToCsv(path, csv));
        _analyzeRunning = false;
        if (result.StartsWith("Error"))
        {
            AppLog($"App: analyze failed: {result}", "E");
            _statusText = "Analyze failed: " + result;
            return;
        }
        AppLog($"App: CSV saved: {result}");
        _statusText = "CSV saved: " + result;
    }

    // ------------------------------------------------------------------
    // Smoke-test entry (editor PlaySmoke harness)
    // ------------------------------------------------------------------

    private async void ScanAndStream()
    {
        try
        {
            UiStartScan();
            await Task.Delay(ScanDevicePeriodMs + 500);
            UiStopScan();
            List<DeviceEntry> targets;
            lock (_statesMutex)
                targets = _discovered
                    .OrderByDescending(d => d.Rssi)
                    .Take(4)
                    .ToList();
            foreach (DeviceEntry t in targets)
            {
                if (StateFor(t.Mac) != null)
                    continue;
                ConnectDevice(t.Mac);
                await Task.Delay(2500);
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SensorDemo] ScanAndStream: " + ex.Message);
        }
    }
}

