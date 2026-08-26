# Sensor SDK — Unity demo (sen_capi C# bindings)

OYMotion Sensor SDK demo for Unity.

## Brief

Full-featured multi-device demo over the C# bindings in
`bindings/csharp/` (`SensorCapi.cs` + `Sensor.cs`, installed into
`Assets/SensorSdk/`). Everything is code-built: no scene setup, no prefabs,
no Unity packages. The UI is IMGUI with three pages (Device / Bio / IMU, Qt
mobile layout parity), drawn by a self-spawning `MonoBehaviour`
(`RuntimeInitializeOnLoadMethod`) — press Play and the demo appears.
Feature parity with the Qt demo (`example_qt`) via the WinUI3 demo
(`example_cs_winui3`); the header row carries the SDK version and the
demo's own version (bump +0.0.1 per demo change).

Unity 2021.3+ (.NET Standard 2.1 API compatibility level — the default).
Targets Windows (x86_64 / x86), Android (arm64-v8a / x86 / x86_64), iOS,
macOS and Linux (x86_64 / x86 / arm64) players; Mono editor and IL2CPP standalone
builds both work.

## Setup

This repository **is** the Unity project, with everything pre-installed:

- `Assets/SensorSdk/SensorCapi.cs` + `Sensor.cs` — the C# bindings
- `Assets/SensorDemo/` — the demo (`SensorDemoBehaviour.cs` logic +
  `SensorDemoUi.cs` IMGUI layout, `DeviceState.cs`, `LiveFilter.cs`,
  `SpectrumCompute.cs`, `WaveformView.cs`, `SpectrumView.cs`, plus the
  `Editor/` iOS build post-processor)
- `Assets/Plugins/Windows/x86_64/sensor.dll` and
  `Assets/Plugins/Windows/x86/sensor.dll` — the Windows SDK runtimes
  (64-bit / 32-bit, Release), each with a prepared `.meta` pinning the
  PluginImporter Platform/CPU settings
- `Assets/Plugins/Android/sensorcapi-release.aar` + a BLE-permission
  `AndroidManifest.xml` — the AAR bundles the Java BLE bridge and
  libsensor.so for arm64-v8a / x86 / x86_64
- `Assets/Plugins/iOS/sensor.xcframework` — iOS device + simulator slices
- `Assets/Plugins/Linux/x86_64/libsensor.so`,
  `Assets/Plugins/Linux/x86/libsensor.so` and
  `Assets/Plugins/Linux/arm64/libsensor.so` — the Linux runtimes (64-bit /
  32-bit / ARM64)

Just open the project with Unity **2021.3+** (.NET Standard 2.1 API
compatibility level — the default) and press **Play**. SDK file logs land
in `%USERPROFILE%\Documents\sensorsdklog\<yyyyMMdd_HHmmss>_<sdk version>`
(the demo sets the path via `SetLogPath` before `SetDebugEnabled(true)`).

## 1. Permission

- **Windows / Linux editor and players**: no capability declaration or
  permission prompt; the OS Bluetooth stack is used through `sensor.dll` /
  `libsensor.so`.
- **Android**: the installed `AndroidManifest.xml` declares the BLE
  permissions; the demo requests them at startup (the system permission
  dialog shows on first run).
- **iOS**: the Editor post-processor (`Assets/SensorDemo/Editor/`) adds
  CoreBluetooth and the Bluetooth usage description to the generated Xcode
  project.
- **macOS**: the player needs the Bluetooth usage description (Player
  Settings -> Other Settings -> `NSBluetoothAlwaysUsageDescription`) and,
  when sandboxed, the Bluetooth entitlement.

## 2. Import SDK

```csharp
using SensorSdk;
```

The binding is thin: all behavior (error strings, subscription masks,
session recovery) lives inside the SDK and matches the Python SDK verbatim.
The binding's reverse-P/Invoke callbacks are all static and marked
`[MonoPInvokeCallback]`, so an IL2CPP standalone build works; the Mono
editor/player needs nothing special. On iOS the SDK is statically linked
and the binding P/Invokes `__Internal` (handled by a `UNITY_IOS` compile
conditional).

## SensorController methods

### 1. Initialize

```csharp
// singleton
var controller = SensorController.Instance;

// register scan listener: the deduped device list, delivered every scan round
controller.DeviceFound += (List<BleDevice> deviceList) =>
{
    // all discovered devices (repeats refresh the entry in place)
};

// bluetooth enable state changes
controller.EnableChanged += (bool enabled) => { };
```

Use `GetVersion()` to get the SDK version string:

```csharp
string version = controller.GetVersion();
```

`SensorController.CapiVersion` returns the native library's C API version,
so an app can detect a binding/library mismatch at runtime (a mismatch is
also traced as a warning at controller creation).

### 2. Start scan

Use `bool StartScan(int periodInMs)` to start scanning; `DeviceFound` fires
every `periodInMs`:

```csharp
bool success = controller.StartScan(6000);
```

Use `Task<List<BleDevice>> ScanAsync(int periodInMs)` to scan once:

```csharp
List<BleDevice> bleDevices = await controller.ScanAsync(6000);
```

### 3. Stop scan

```csharp
controller.StopScan();
```

### 4. Check scanning

```csharp
bool isScanning = controller.IsScanning;
```

### 5. Check if bluetooth is enabled

```csharp
bool isEnable = controller.IsEnable;
```

### 6. Create SensorProfile

Use `RequireSensor` to get (creating and registering when the MAC is
unknown) the profile of a device:

```csharp
SensorProfile sensorProfile = controller.RequireSensor(bleDevice);   // or RequireSensor(mac)
```

### 7. Get SensorProfile

```csharp
SensorProfile sensorProfile = controller.GetSensor(bleDevice.Mac);  // null when never registered
```

### 8. Get connected SensorProfiles

```csharp
List<SensorProfile> sensorProfiles = controller.GetConnectedSensors();
```

### 9. Terminate

Call `TearDown()` (or `Dispose()`) once at application shutdown: every scan
and connection stops and the whole native SDK is terminated. Repeated calls
are safe.

```csharp
controller.TearDown();

// in this demo: TearDown() runs only in a standalone player's
// OnApplicationQuit -- in the editor, Play/Stop cycles share the editor
// process, so teardown is skipped there to keep the next Play working
```

Please MAKE SURE to call TearDown when a standalone player exits.

## SensorProfile methods

### 10. Register callbacks

```csharp
SensorProfile sensorProfile = controller.RequireSensor(bleDevice);

sensorProfile.StateChanged += (SensorProfile sensor, SenDeviceState newState) =>
{
    // device state transitions (Connecting/Connected/Ready/Disconnected/...)
    // do the unexpected-disconnect logic here
};

sensorProfile.ErrorReceived += (SensorProfile sensor, string reason) =>
{
    // dongle unplugged, reconnect budget exhausted, ...
};

sensorProfile.PowerChanged += (SensorProfile sensor, int power) =>
{
    // battery 0-100; invalid readings are never reported, and the value is
    // stabilized with a hysteresis band so ADC jitter is filtered out
};

sensorProfile.DataReceived += (SensorProfile sensor, List<SensorData> dataList) =>
{
    // after startDataNotification: each invocation delivers the whole batch
    // of SensorData objects parsed together (loop over it)
    foreach (SensorData data in dataList) { }
};

sensorProfile.DeviceInfoUpdated += (SensorProfile sensor, DeviceInfo info) =>
{
    // the cached DeviceInfo was patched in place (link parameters reported
    // after connect, EEG_SAMPLE_RATE applied, replay config switch, ...)
};

sensorProfile.DataTransferStateChanged += (SensorProfile sensor, bool isTransferring) =>
{
    // real data-stream on/off changes only
};
```

Callback threading model: all events fire on internal SDK threads, never on
Unity's main thread. UnityEngine APIs must not be touched there — this demo
only posts `Action`s into a queue drained by `Update()`, and the data
callback only enqueues batches into a bounded queue drained by a worker
thread into the display rings. Do not call blocking SDK functions from
inside an event handler.

A seventh hook, `OnAutoReconnect`, customizes stream recovery after an
abnormal disconnect — see section 14.1.

### 11. Connect device

```csharp
bool success = await sensorProfile.ConnectAsync();
```

### 12. Disconnect

```csharp
bool success = await sensorProfile.DisconnectAsync();
```

If data notification is currently active, `DisconnectAsync()` stops it first
before closing the BLE connection.

### 13. Get device status

```csharp
SenDeviceState deviceState = sensorProfile.DeviceState;

// enum SenDeviceState:
//   Disconnected = 0, Connecting = 1, Connected = 2, Ready = 3,
//   Disconnecting = 4, Invalid = 5
```

Send commands only in the `Ready` state. `IsReady` is the shortcut:

```csharp
if (sensorProfile.IsReady) { }
```

### 14. Get BLE device of SensorProfile

```csharp
BleDevice bleDevice = sensorProfile.Device;   // Name / Mac / Rssi
```

### 14.1 Auto reconnect and resume data stream

Auto reconnect is enabled by default. While enabled and the device is
streaming, an abnormal disconnect (remote link loss, or a long no-data
half-dead link) is followed by automatic reconnect -> `InitAsync` with the
previous init arguments -> re-applying the previous session's `SetParam`
values in order -> `StartDataNotificationAsync`. Explicit user calls
(`ConnectAsync()` / `DisconnectAsync()` / `StopDataNotificationAsync()`)
cancel the pending resume.

```csharp
sensorProfile.SetAutoReconnect(true);   // default; false to opt out
```

**Custom recovery via `OnAutoReconnect`**: when the auto-reconnect finds the
disconnected device again (back in `Ready`, about to resume), this delegate
is invoked instead of the default flow:

```csharp
sensorProfile.OnAutoReconnect = (SensorProfile sensor, bool hasLastSession, Action<bool> answer) =>
{
    // hasLastSession=true  -> a previous session exists (init args + setParam
    //                         values can be preserved and restored)
    // answer(true)  -> the app handled recovery itself; the SDK skips the
    //                  default recovery
    // answer(false) -> fall back to the default flow
    // Answer exactly once, from any thread; if no answer arrives within 10 s
    // the SDK runs the default recovery.
    answer(false);
};
```

### 15. Get device info of SensorProfile

Call after the device is `Ready` and init has succeeded:

```csharp
DeviceInfo deviceInfo = sensorProfile.GetDeviceInfo();

// fields: DeviceName, ModelName, HardwareVersion, FirmwareVersion, MTUSize
// plus a ChannelCount / SampleRate field pair per modality:
//   Ppg, Spo2, Impe, Emg, Eeg, Ecg, Acc, Gyro, Brth, MagAngle, Euler, Quat
// plus EmgMaxSampleRate / EegMaxSampleRate / EcgMaxSampleRate (maximum rate
//   from the capability query, 0 = not reported)
// plus ImuChannelCount / ImuSampleRate (aggregated IMU stream; 0 = none)
// plus ConnectionIntervalMs / PeripheralLatency / SupervisionTimeoutMs
//   (negotiated BLE link parameters; 0 / -1 / 0 = unknown)
```

Or fetch explicitly:

```csharp
DeviceInfo info = await sensorProfile.FetchDeviceInfoAsync();
```

### 16. Init data transfer

```csharp
await sensorProfile.InitAsync(packageSampleCount: 15, powerRefreshIntervalMs: 60 * 1000);
```

- `packageSampleCount`: sample count of each `SensorData` batch delivered by
  `DataReceived`
- `powerRefreshIntervalMs`: poll period for the `PowerChanged` push (0 = one
  initial reading only)

A non-successful init throws `SensorException` with the SDK's error string.

### 17. Check if init data transfer succeed

```csharp
bool hasInited = sensorProfile.HasInited;
```

### 18. DataNotify

#### 18.1 Start data transfer

```csharp
await sensorProfile.StartDataNotificationAsync();
```

#### 18.2 Data type list

```csharp
// enum SenDataType:
//   Acc = 0x1            // acceleration, unit is g
//   Gyro = 0x2           // gyroscope, unit is degree/s
//   Euler = 0x4          // euler angle, unit is degree
//   Quaternion = 0x5     // quaternion (w, x, y, z)
//   Gest = 0x07          // gesture id
//   Emg = 0x8            // unit is uV
//   MagAngle = 0x0D
//   Eeg = 0x10           // unit is uV
//   Ecg = 0x11           // unit is uV
//   Impedance = 0x12     // electrode impedance
//   Imu = 0x13           // aggregated IMU batch (acc 0-2 / gyro 3-5 /
//                        // euler 6-8 / quat 9-12; see DeviceInfo.ImuChannelCount)
//   Ads = 0x14
//   Brth = 0x15          // respiration, unit is uV
//   ImpedanceExt = 0x16
//   Spo2 = 0x17          // SpO2 percentage
//   Ppg = 0x18           // PPG raw samples
```

Process data in `DataReceived`. Each `SensorData` exposes:

- metadata properties: `DeviceMac` / `DeviceName` / `DataType` /
  `SampleRate` / `ChannelCount` / `SampleCount` / `ChannelMask` /
  `LostPackageCount` / `StartSampleIndex` / `StartTimeStamp` /
  `StartTimeSec` (wall-clock stream-start anchor in LSL-style Unix seconds,
  0.0 when unknown) / `Delay`
- batch access: `ChannelSamples` (lazy `Sample[][]` matrix) and
  `IsChannelEnabled(channel)` (false for channels masked out of
  `ChannelMask`)
- single-point accessors (`channel`, `sampleIndex`):
  `GetChannelSample` / `GetData` / `GetRawData` / `GetImpedance` /
  `GetSaturation` / `GetSampleIndex` / `GetTimeStampInMs` /
  `GetAbsTimeStampInSec` / `IsLost`; out-of-range indices throw
  `ArgumentOutOfRangeException`
- staleness probe: `IsDataValid(channel = 0, sampleIndex = 0)` — false on
  out-of-range, a stale/overwritten slot, or a view from a previous stream
  session. One probe per batch is enough.

A `SensorData` delivered by `DataReceived` is a **borrowed view** over
SDK-owned memory. It stays readable after the handler returns, but a slot is
eventually overwritten by newer data (detectable with `IsDataValid`). To
hold a batch across threads or time, call `Clone()` at the boundary — one
block copy into an owned buffer; every accessor works identically on the
clone.

```csharp
sensorProfile.DataReceived += (sensor, dataList) =>
{
    foreach (SensorData data in dataList)
    {
        if (!data.IsDataValid())   // one probe per batch
            continue;

        if (data.DataType == SenDataType.Eeg)
        {
            for (int ch = 0; ch < data.ChannelCount; ch++)
            {
                if (!data.IsChannelEnabled(ch))
                    continue;   // masked-out channel
                for (int i = 0; i < data.SampleCount; i++)
                {
                    if (data.IsLost(ch, i))
                        continue;   // loss-compensation placeholder
                    float uv = data.GetData(ch, i);
                    // draw with uv & ch
                }
            }
        }
    }
};
```

#### 18.3 Stop data transfer

```csharp
await sensorProfile.StopDataNotificationAsync();
```

#### 18.4 Check if it's data transfering

```csharp
bool isTransfering = sensorProfile.IsDataTransfering;
```

### 19. Get battery level

```csharp
int batteryPower = await sensorProfile.GetBatteryLevelAsync();
// 0-100; -1 means no valid reading is available yet
// (PowerChanged never reports -1). Explicit queries are unfiltered.
```

### Async model

All profile operations are Task-returning async methods backed by the SDK's
completion callbacks; a non-empty SDK error string becomes a
`SensorException`. There are no synchronous blocking variants:

- `SensorController`: `ScanAsync`
- `SensorProfile`: `ConnectAsync`, `DisconnectAsync`, `InitAsync`,
  `StartDataNotificationAsync`, `StopDataNotificationAsync`,
  `SetParamAsync`, `GetParamAsync`, `GetBatteryLevelAsync`,
  `FetchDeviceInfoAsync`

### setParam method

Use `Task<string> SetParamAsync(string key, string value)` to set a
parameter. Call after the device reaches the `Ready` state; the result is
`"OK"` on success or an error string otherwise. If the device is already
streaming when you change an `NTF_*` key, the SDK stops and restarts the data
notification so the new setting takes effect immediately. `FILTER_*` keys are
applied on the fly without interrupting the stream.

```csharp
// Data stream toggles ("ON" / "OFF")
string result = await sensorProfile.SetParamAsync("NTF_GEST", "ON");
await sensorProfile.SetParamAsync("NTF_EMG", "ON");
await sensorProfile.SetParamAsync("NTF_EEG", "ON");
await sensorProfile.SetParamAsync("NTF_ECG", "ON");
await sensorProfile.SetParamAsync("NTF_IMU", "ON");
await sensorProfile.SetParamAsync("NTF_BRTH", "ON");
await sensorProfile.SetParamAsync("NTF_IMPEDANCE", "ON");
await sensorProfile.SetParamAsync("NTF_MAG_ANGLE", "ON");
await sensorProfile.SetParamAsync("NTF_PPG", "ON");
await sensorProfile.SetParamAsync("NTF_PPG_RAW", "ON");   // alias of NTF_PPG
await sensorProfile.SetParamAsync("NTF_SPO2", "ON");
await sensorProfile.SetParamAsync("NTF_GFORCE_EULER", "ON");
await sensorProfile.SetParamAsync("NTF_GFORCE_QUAT", "ON");
await sensorProfile.SetParamAsync("NTF_GFORCE_ACC", "ON");
await sensorProfile.SetParamAsync("NTF_GFORCE_GYRO", "ON");
// NTF_IMU is the master switch of the four NTF_GFORCE_* streams: toggling it
// updates all four, and toggling any of the four updates the aggregated
// NTF_IMU state. On legacy EMG devices NTF_GEST and NTF_EMG are mutually
// exclusive.

// Firmware filter toggles
await sensorProfile.SetParamAsync("FILTER_50HZ", "ON");   // 50Hz notch
await sensorProfile.SetParamAsync("FILTER_60HZ", "ON");   // 60Hz notch
await sensorProfile.SetParamAsync("FILTER_HPF", "ON");    // 0.5Hz high-pass
await sensorProfile.SetParamAsync("FILTER_LPF", "ON");    // 80Hz low-pass

// EEG/ECG sample rate (bound together on devices that have both)
await sensorProfile.SetParamAsync("EEG_SAMPLE_RATE", "500");
// Validated against the device-reported capability list (see
// getParam("EEG_SAMPLE_RATE_LIST")); an unsupported value returns
// "Error: unsupported sample rate ...".

// NeuCir remote control (NeuCir devices only)
await sensorProfile.SetParamAsync("NEUCIR_SET_MODE", "APP_REMOTE");
await sensorProfile.SetParamAsync("NEUCIR_APP_CONTROL", "OPEN");   // OPEN / CLOSE / STOP

// Debug outputs
await sensorProfile.SetParamAsync("DEBUG_BLE_DATA_PATH", "True");
// export the session's raw BLE capture: "True" exports
// {DeviceName}_data_YYYYMMDD_HHMMSS.bin into the SDK log directory (see
// SetLogPath), or pass an absolute .bin path; "False" / "" disables export.
await sensorProfile.SetParamAsync("DEBUG_LOG_PATH", "True");
// enable this profile's log file ({DeviceName}_log_YYYYMMDD_HHMMSS.txt in
// the SDK log directory), or pass an absolute path; "False" / "" disables.
```

### getParam method

Use `Task<string> GetParamAsync(string key)` to query the current parameter
state. If the key is not supported, the result starts with `"Error"`.

```csharp
string result = await sensorProfile.GetParamAsync("FILTER");
// "FILTER_50HZ|ON|FILTER_60HZ|ON|FILTER_HPF|ON|FILTER_LPF|ON"

result = await sensorProfile.GetParamAsync("NTF");
// "NTF_BRTH|ON|NTF_ECG|ON|NTF_EEG|ON|NTF_EMG|ON|..."
// The aggregate lists every known key regardless of device capability —
// gate UI visibility by the DeviceInfo channel counts, not by presence here.

result = await sensorProfile.GetParamAsync("EEG_SAMPLE_RATE");       // e.g. "250"
result = await sensorProfile.GetParamAsync("EEG_SAMPLE_RATE_LIST");  // e.g. "250|500"
```

## Bin file recording and replay

On every successful connect the SDK records all raw BLE packets of the
session into a temp `.bin` file; `SetParamAsync("DEBUG_BLE_DATA_PATH", ...)`
exports it on stream stop / disconnect. Bin files can be replayed offline
for debugging and packet-loss analysis.

### Get bin file info

```csharp
BinFileInfo info = controller.GetBinFileInfo("path/to/session.bin");
// fields: Mac, DeviceName, DurationSec, Valid, DeviceInfo
// null when the file does not exist or has no config record
```

### Replay a bin file

Replays a capture through the normal parsing pipeline on a background
thread; parsed batches arrive via `DataReceived` on the returned profile,
same as live data:

```csharp
SensorProfile replay = controller.ReplayBinFile("path/to/session.bin", deviceMac: "");
replay.DataReceived += (sensor, dataList) => { /* same handler as live */ };
```

- `deviceMac`: profile identity to replay through; pass `""` to use the
  MAC stored in the bin's config record
- `realtime`: `true` replays at the recorded pace; `false` as fast as
  possible
- Returns null when the file has no config record

### Pause / resume / stop replay

```csharp
string result = controller.PauseBinReplay(deviceMac);
result = controller.ResumeBinReplay(deviceMac);
result = controller.StopBinReplay(deviceMac);
// Each returns "OK" on success or an error string otherwise.
```

### Synchronized multi-bin replay

Replays several captures concurrently on one shared clock aligned by their
recorded timestamps: the earliest record in the group is t=0, so captures
recorded at the same time keep their original relative offsets (a capture
whose data starts 45 s later delivers its first packet 45 s into the
replay). Pausing/resuming any member freezes/resumes the whole group;
stopping stays per device:

```csharp
SensorProfile[] replays = controller.MultiReplayBinFile(
    new[] { "d:/temp/dev1.bin", "d:/temp/dev2.bin" },
    new[] { "AA:BB:CC:DD:EE:01", "AA:BB:CC:DD:EE:02" });
// Input-order aligned; a null entry marks a member that failed
// (bad/duplicate mac, mac busy, unreadable file).
```

Each started profile delivers data through the same callbacks as a single
replay. In the demo UI, the **Multi Replay Bin** button accepts a
`;`-separated path list (a multi-select file dialog fills it in the editor
and on Windows / macOS standalone); the single **Replay** button takes one
path.

### Parse a bin file to CSV

Offline full-speed conversion through the real parsing pipeline; blocks the
caller:

```csharp
string csvPath = controller.ParseBinToCsv("d:/temp/test.bin", "d:/temp/test.csv");
// Returns the CSV file path, or an "Error: ..." string.
```

CSV header row:

```
timestamp,mac,type,raw_hex,data_type,sample_rate,channel_count,lost_count,samples_info,first_sample
```

Row kinds in record order: `raw` (one per data record, raw bytes as hex),
`cmd_send` / `cmd_recv` (command bytes as hex; `data_type` names the decoded
command / `NAME:CODE` response), `event` (`connect` / `disconnect` /
`stream_start` / `stream_stop`), and `parsed` (one per parsed batch;
`data_type` is the type name, e.g. `NTF_EEG`; `samples_info` the per-channel
sample counts; `first_sample` the first sample's field summary). A bin
without a config record yields `raw` rows only.

## Logging controls

`SetLogPath` sets the SDK log **directory** (it must be a directory). All
default file outputs live in it: the controller log, the default per-profile
logs (`DEBUG_LOG_PATH=True`) and the default bin exports
(`DEBUG_BLE_DATA_PATH=True`).

```csharp
controller.SetDebugEnabled(true);
// enable SDK debug logs; creates the controller log
// (sensor_controller_log_YYYYMMDD_HHMMSS.txt) in the log directory.
// SetDebugEnabled(false) closes it and drops all file output.

controller.SetLogPath(true, "d:/temp/sdklogs");
// set the log directory (created if missing). SetLogPath(false) disables
// file output; SetLogPath(true) resets to the default
// (Documents/sensorsdklog).
```

### Application log entries

Applications can write their own events into the same SDK log files, keeping
one shared timeline with the SDK's internal logs:

```csharp
controller.Log("User clicked start", "I");        // controller log
sensorProfile.Log("User toggled filter 50Hz", "I"); // profile log when enabled,
                                                    // else the controller log
```

`level` is judged by its first character, case-insensitive `d` / `i` / `w` /
`e` (anything else is `Info`); `d` follows the `SetDebugEnabled` switch.
Entries are tagged `[App]` in the log files. Never throws. This demo routes
key UI events (scan start/stop, connect lifecycle, setParam results, live
filter switch, replay start/pause/resume/stop/EOF, bin analyze, toggles,
quit) through its `AppLog` helper — a device's line lands in its profile
log, the rest in the controller log.

---

## What this demo does

- **Device page**: Start/Stop Scan, an RSSI-sorted device list with
  in-place `[Connected]` / `[Streaming]` row marks. The selected row is the
  *current* device whose pages and setParam controls are bound to; Connect /
  Disconnect affects only the selected device. Any number of devices stream
  at once. Status/rate lines (per-type nominal + measured rates, stream
  start time, first-packet delay), Model / HW / FW / Link / MTU / Power
  labels (battery pushed by the SDK polling), Packet Loss Stats and Gesture
  boxes.
- **Toggles**: **Auto Reconnect** (default ON; the demo takes over recovery
  itself — the `OnAutoReconnect` query is answered with `true`, and once
  the link is back the demo re-selects the device row, re-runs the
  connect/init/stream chain, and re-applies the recorded setParam history
  one command at a time when a last session was restored; every disconnect
  tears the device UI state down unconditionally) and **Clone Data** (safe
  deep-copy vs zero-copy batch queueing; default off = zero-copy).
- **Settings**: Enable SDK Debug Log / Enable Debug Bin Data (the session's
  logs and .bin exports land in one timestamped subdir of
  `Documents/sensorsdklog`), Data Notification switches (EEG/EMG/GESTURE/
  PPG/SpO2/IMU, capability-gated by the device info), Filter switches
  (50Hz/60Hz/HPF/LPF, never hidden), EEG Sample Rate radios
  (250/500/1000/2000 Hz, capability-gated by `EEG_SAMPLE_RATE_LIST`;
  unsupported rates and the whole group on EEG-less devices are hidden).
- **Bio page**: 8 waveform slots, auto-selected by device capability — EMG
  channels, or paged EEG channels plus ECG/BRTH slots (Prev/Next +
  "Page x / y"), or the PPG fixed plot set (EEG fp1/fp2 + PPG red/ir + SpO2
  spo2/heart_rate). Impedance side texts on the EMG/EEG plots (green <=
  500 / orange <= 999 / red above, KOhm). The **Live Filter** band selector
  (Off / delta / theta / alpha / beta / gamma) band-passes the bio
  waveforms of every device.
- **IMU page**: ACC / GYRO / Quat / Euler 2D waveform (fixed Y ranges) with
  a Display Data Type selector, an FFT spectrum strip (recomputed every
  500 ms on a worker thread), real-time value labels, and a true-3D
  quaternion cube (a GameObject rendered by a dedicated camera whose
  viewport sits on the page; it follows the latest quaternion sample).
- **Replay Bin File** / **Analyze Bin**: replay a `.bin` capture through
  the normal parse pipeline (realtime; Pause/Resume/Stop, EOF via the
  data-transfer OFF push) or parse it to CSV offline. Replay and Multi
  Replay Bin open the system file-open dialog in the editor and on Windows
  / macOS standalone (multi-select for Multi Replay Bin), starting at the
  SDK debug-log directory when it exists; on platforms without a picker the
  path is typed instead (`;`-separated for a group). Replay is exclusive
  (refused while devices are connected); each member gets its own
  `[Replay]` row.
- **Multi Start/Stop**: a toggle button in the header row right of the SDK
  version label — synchronized stream start/stop across all connected
  devices (reads "Multi Stop" while any device is streaming); next to it,
  **Multi Replay Bin** starts a synchronized group replay.

SDK callbacks arrive on SDK threads; the demo never touches UnityEngine
APIs there — event callbacks only post `Action`s into a queue drained by
`Update()`, and the data callback only enqueues batches (a worker thread
feeds the per-device display rings; views repaint at most once per 50 ms).

### Platform notes

- **Windows**: the install drops a prepared `.meta` next to every native
  plugin (`Plugins/Windows/x86_64` / `Plugins/Windows/x86` /
  `Plugins/Linux/...`), so Platform/CPU is pinned (x86_64 dll -> Editor +
  Win64, x86 dll -> 32-bit Windows standalone only). A Windows standalone
  build needs the same DLL next to the player (Unity copies it from
  `Assets/Plugins`).
- **Android**: the AAR (Java BLE bridge + libsensor.so for arm64-v8a /
  x86 / x86_64) and the BLE-permission manifest are installed by the setup
  script. At startup the demo instantiates `SensorBleBridge` over
  `AndroidJavaObject`, calls `setBridge()` and `requestBlePermissions()`.
  `OnApplicationQuit` teardown applies to Android players the same as
  desktop.
- **iOS**: `sensor.xcframework` (device + simulator slices) is statically
  linked, so the binding P/Invokes `__Internal` there. iOS Build Support
  must be installed for the Editor post-processor script to compile.
- **macOS**: copy a Mac-built `libsensor.dylib` into `Assets/Plugins/OSX/`
  (the setup script does this when the file exists).
- **Linux**: the plugin is the WSL-built `libsensor_x86.so` renamed to
  `libsensor.so`; it links glib-2.0/gio-2.0 dynamically, so the target
  machine needs the standard glib runtime (preinstalled on desktop Linux).
- **Editor hangs entering Play mode**: on some machines Unity 2021.3
  deadlocks while entering Play (the log stops at "Setting up N worker
  threads for Enlighten", in batch and GUI mode alike, even with an empty
  project). Workaround: Project Settings -> Editor -> Enter Play Mode
  Options, enable it and tick Disable Domain Reload (the demo keeps no
  static state across plays, so this is safe for it).

## License

Same as the SDK (see the repository root).
