using System;
using System.Collections.Generic;
using UnityEngine;
using SensorSdk.ExampleUnity;

public sealed partial class SensorDemoBehaviour
{
    private static readonly string[] PageNames = { "Device", "Bio", "IMU" };

    private void OnGUI()
    {
        UpdateCubeCamera();
        if (_ctrl == null)
            return;

        Rect area = new Rect(8, 8, Screen.width - 16, Screen.height - 16);
        GUILayout.BeginArea(area);

        // Header row: version + multi-device controls
        bool multiReplaying = _replayMacs.Count > 0;
        bool anyConnected;
        bool anyStreaming = false;
        lock (_statesMutex)
        {
            anyConnected = _deviceStates.Count > 0;
            foreach (DeviceState st in _deviceStates.Values)
            {
                if (st.Profile.IsDataTransfering)
                {
                    anyStreaming = true;
                    break;
                }
            }
        }
        GUILayout.BeginHorizontal();
        GUILayout.Label($"<b>SensorSDKCXX Unity Demo (Multi)</b>   SDK: {_sdkVersion}   demo v{DemoVersion}",
                        Rich());
        string multiLabel = anyStreaming ? "Multi Stop" : "Multi Start";
        if (UiButton(multiLabel, !multiReplaying && anyConnected, 90)) UiMultiSync();
        if (UiButton("Multi Replay Bin", !multiReplaying, 110)) UiMultiReplay();
        GUILayout.EndHorizontal();

        int newPage = GUILayout.Toolbar(_page, PageNames);
        if (newPage != _page)
            _page = newPage;
        GUILayout.Space(4);

        switch (_page)
        {
            case 0: DrawDevicePage(); break;
            case 1: DrawBioPage(area); break;
            case 2: DrawImuPage(area); break;
        }
        GUILayout.EndArea();

        // Modal warning
        if (_warningTitle != null)
        {
            float w = Mathf.Min(420, Screen.width - 60);
            float h = 170;
            _warningRect = new Rect((Screen.width - w) / 2, (Screen.height - h) / 2, w, h);
            GUI.ModalWindow(7491, _warningRect, DrawWarningWindow, _warningTitle);
        }
    }

    private void DrawWarningWindow(int id)
    {
        GUILayout.Label(_warningMessage, Wrap());
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("OK", GUILayout.Width(90)))
            _warningTitle = null;
    }

    // ------------------------------------------------------------------
    // Device page
    // ------------------------------------------------------------------

    private void DrawDevicePage()
    {
        _pageScroll = GUILayout.BeginScrollView(_pageScroll);

        // Toggles row + version.
        GUILayout.BeginHorizontal();
        UiToggle("Auto Reconnect", _autoReconnect, true, OnAutoReconnectToggled);
        UiToggle("Clone Data", _cloneData, true, v => _cloneData = v);
        GUILayout.EndHorizontal();

        GUILayout.Label("Discovered Devices:", Rich());

        bool replaying = _replayMacs.Count > 0;
        bool selConnected;
        lock (_statesMutex)
            selConnected = _selectedMac.Length > 0 && _deviceStates.ContainsKey(_selectedMac);
        bool canScan = !replaying && !_scanning;
        bool canConnect = !replaying && _selectedMac.Length > 0 && !selConnected;
        bool canDisconnect = !replaying && selConnected;

        GUILayout.BeginHorizontal();
        // Left: scan/connect buttons.
        GUILayout.BeginVertical(GUILayout.Width(120));
        if (UiButton("Start Scan", canScan)) UiStartScan();
        if (UiButton("Stop Scan", _scanning && !replaying)) UiStopScan();
        if (UiButton("Connect", canConnect)) UiConnectSelected();
        if (UiButton("Disconnect", canDisconnect)) UiDisconnectCurrent();
        GUILayout.EndVertical();

        // Middle: the device list
        _deviceScroll = GUILayout.BeginScrollView(_deviceScroll,
                                                  GUILayout.Height(110), GUILayout.ExpandWidth(true));
        DeviceRow[] rows;
        lock (_statesMutex)
            rows = _rows.ToArray();
        bool prevListEnabled = GUI.enabled;
        GUI.enabled = !replaying;
        foreach (DeviceRow row in rows)
        {
            bool selected = row.Mac == _selectedMac;
            if (GUILayout.Toggle(selected, row.Text, RowStyle(selected)) && !selected)
                UiSelectDevice(row.Mac);
        }
        GUI.enabled = prevListEnabled;
        if (rows.Length == 0)
            GUILayout.Label("(scan to discover devices)", Wrap());
        GUILayout.EndScrollView();

        // Right: replay/analyze buttons.
        GUILayout.BeginVertical(GUILayout.Width(120));
        if (UiButton("Replay Bin File", !replaying)) UiStartReplay();
        if (UiButton("Analyze Bin", !replaying && !_analyzeRunning)) UiAnalyzeBin();
        if (UiButton(_replayPaused ? "Resume Replay" : "Pause Replay", replaying && !_replayStopRequested)) UiReplayPauseResume();
        if (UiButton("Stop Replay", replaying && !_replayStopRequested)) UiReplayStop();
        GUILayout.EndVertical();
        GUILayout.EndHorizontal();

        // Replay / analyze source path
        GUILayout.BeginHorizontal();
        GUILayout.Label("bin:", GUILayout.Width(26));
        _binPath = GUILayout.TextField(_binPath, GUILayout.MinWidth(260));
        GUILayout.EndHorizontal();

        // Status / info.
        GUILayout.Label(_statusText, Wrap());
        if (_rateText.Length > 0)
            GUILayout.Label(_rateText, Wrap());
        GUILayout.BeginHorizontal();
        GUILayout.Label(_modelText, Wrap());
        GUILayout.Label(_hwText, Wrap());
        GUILayout.Label(_fwText, Wrap());
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        GUILayout.Label(_linkText, Wrap());
        GUILayout.Label(_mtuText, Wrap());
        GUILayout.Label(_powerText, Wrap());
        GUILayout.EndHorizontal();

        // Packet loss + gesture boxes side by side.
        GUILayout.BeginHorizontal();
        GUILayout.BeginVertical("box");
        GUILayout.Label("<b>Packet Loss Stats</b>", Rich());
        GUILayout.Label(_lostPacketText, Wrap());
        GUILayout.EndVertical();
        GUILayout.BeginVertical("box");
        GUILayout.Label("<b>Gesture</b>", Rich());
        GUILayout.Label(_gestureText, Wrap());
        GUILayout.EndVertical();
        GUILayout.EndHorizontal();

        // Settings row: Debug Log / Data Notification / Filter / EEG rate.
        GUILayout.BeginHorizontal();

        GUILayout.BeginVertical("box");
        GUILayout.Label("<b>Debug Log</b>", Rich());
        UiToggle("Enable SDK Debug Log", _debugLogEnabled, !replaying, OnDebugLogToggled);
        UiToggle("Enable Debug Bin Data", _binDataEnabled, !replaying, OnBinDataToggled);
        GUILayout.EndVertical();

        GUILayout.BeginVertical("box", GUILayout.ExpandWidth(true));
        GUILayout.Label("<b>Data Notification</b>", Rich());
        GUILayout.BeginHorizontal();
        foreach (string key in NtfKeys)
        {
            Bool2 b;
            bool enabled = _ntfUi.TryGetValue(key, out b) && b.Enabled;
            bool check = b.Check;
            if (!enabled && _ntfHasInfo)
                continue;
            string label = NtfLabels[key];
            UiToggle(label, check, enabled, v => OnNtfToggled(key, v));
        }
        GUILayout.EndHorizontal();
        GUILayout.EndVertical();

        GUILayout.BeginVertical("box");
        GUILayout.Label("<b>Filter</b>", Rich());
        GUILayout.BeginHorizontal();
        foreach (string key in FilterKeys)
        {
            Bool2 b;
            bool enabled = _filterUi.TryGetValue(key, out b) && b.Enabled;
            UiToggle(FilterLabels[key], b.Check, enabled, v => OnFilterToggled(key, v));
        }
        GUILayout.EndHorizontal();
        GUILayout.EndVertical();

        DeviceState cur = CurrentState();
        if (cur == null || !cur.HasInfo || cur.Info.EEGChannelCount > 0)
        {
            GUILayout.BeginVertical("box");
            GUILayout.Label("<b>EEG Sample Rate</b>", Rich());
            GUILayout.BeginHorizontal();
            foreach (int rate in SampleRateCandidates)
            {
                bool enabled = _rateOptionsUi.Contains(rate);
                if (!enabled && _rateOptionsUi.Count > 0)
                    continue;
                bool check = _rateCurrentUi == rate;
                bool prev = GUI.enabled;
                GUI.enabled = enabled;
                bool nv = GUILayout.Toggle(check, rate + " Hz", "toggle");
                GUI.enabled = prev;
                if (nv && !check && enabled && !_updatingControls)
                    OnSampleRateChecked(rate);
            }
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
        }

        GUILayout.EndHorizontal();

        GUILayout.EndScrollView();
    }

    // ------------------------------------------------------------------
    // Bio page
    // ------------------------------------------------------------------

    private void DrawBioPage(Rect area)
    {
        // Live Filter band
        GUILayout.BeginHorizontal();
        GUILayout.Label("Live Filter:", GUILayout.Width(120));
        int newBand = GUILayout.SelectionGrid(_filterBand, _filterLabels, _filterLabels.Length);
        GUILayout.EndHorizontal();
        if (newBand != _filterBand)
        {
            _filterBand = newBand;
            AppLog($"User: live filter -> {_filterLabels[_filterBand]}");
            foreach (DeviceState st in SnapshotStates())
                st.LiveFilterState.SetBand(_filterBand);
        }

        if (_pageControlsVisible)
        {
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (UiButton("Prev", _bioPage > 0, 60))
            {
                --_bioPage;
                AppLog($"User: prev page -> {_bioPage}", "D");
                LayoutBio(CurrentState());
            }
            GUILayout.Label(_pageText, Centered(), GUILayout.Width(90));
            DeviceState st = CurrentState();
            if (UiButton("Next", _bioPage < BioPageCount(st) - 1, 60))
            {
                ++_bioPage;
                AppLog($"User: next page -> {_bioPage}", "D");
                LayoutBio(st);
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }
        GUILayout.Label(_bioTitle, Rich());

        _bioScroll = GUILayout.BeginScrollView(_bioScroll);
        // Rows with a bound spectrum (EMG/EEG channels) split 50/50:
        // spectrum left, waveform right; other rows stay full-width.
        for (int i = 0; i < _bioWaves.Count; i++)
        {
            Rect row = GUILayoutUtility.GetRect(area.width - 30, 90, GUILayout.ExpandWidth(true));
            if (i < _bioFftChannels.Length && _bioFftChannels[i] >= 0)
            {
                float half = (row.width - 4) / 2;
                _bioSpectra[i].Draw(new Rect(row.x, row.y, half, row.height));
                _bioWaves[i].Draw(new Rect(row.x + half + 4, row.y, half, row.height));
            }
            else
            {
                _bioWaves[i].Draw(row);
            }
        }
        GUILayout.EndScrollView();
    }

    // ------------------------------------------------------------------
    // IMU page
    // ------------------------------------------------------------------

    private void DrawImuPage(Rect area)
    {
        _pageScroll = GUILayout.BeginScrollView(_pageScroll);

        GUILayout.Label("3D Quaternion Visualization", Rich());
        Rect cubeRect = GUILayoutUtility.GetRect(area.width - 30, 200, GUILayout.ExpandWidth(true));
        if (Event.current.type == EventType.Repaint)
        {
            _cubeGuiRect = new Rect(cubeRect.x + area.x, cubeRect.y + area.y,
                                    cubeRect.width, cubeRect.height);
        }
        GUI.Box(cubeRect, string.Empty);
        if (!_cubeHasQuat)
            GUI.Label(cubeRect, "Not connected", Centered());

        GUILayout.Label("2D Waveform + FFT Spectrum (ACC/GYRO/Quat/Euler)", Rich());

        // Display Data Type
        GUILayout.BeginHorizontal();
        GUILayout.Label("Display Data Type:", GUILayout.Width(120));
        int newType = GUILayout.SelectionGrid(_typeIndex, TypeLabels, 4);
        GUILayout.EndHorizontal();
        if (newType != _typeIndex)
        {
            _typeIndex = newType;
            AppLog($"User: display data type -> {TypeLabels[_typeIndex]}");
            RetargetWaveforms();
        }

        // Waveform/spectrum pair: spectrum left, waveform right, 50/50.
        Rect waveRow = GUILayoutUtility.GetRect(area.width - 30, 140, GUILayout.ExpandWidth(true));
        float waveHalf = (waveRow.width - 4) / 2;
        _spectrum.Draw(new Rect(waveRow.x, waveRow.y, waveHalf, waveRow.height));
        _wave2d.Draw(new Rect(waveRow.x + waveHalf + 4, waveRow.y, waveHalf, waveRow.height));

        GUILayout.BeginVertical("box");
        GUILayout.Label("<b>Real-time Values</b>", Rich());
        foreach (string t in _valueTexts)
            GUILayout.Label(t, Wrap());
        GUILayout.EndVertical();

        GUILayout.EndScrollView();
    }

    // ------------------------------------------------------------------
    // Control helpers
    // ------------------------------------------------------------------

    private static bool UiButton(string label, bool enabled, float width = 0)
    {
        bool prev = GUI.enabled;
        GUI.enabled = enabled;
        bool clicked = width > 0
            ? GUILayout.Button(label, GUILayout.Width(width))
            : GUILayout.Button(label);
        GUI.enabled = prev;
        return clicked;
    }

    private void UiToggle(string label, bool value, bool enabled, Action<bool> onChanged)
    {
        bool prev = GUI.enabled;
        GUI.enabled = enabled;
        bool nv = GUILayout.Toggle(value, label);
        GUI.enabled = prev;
        if (nv != value && enabled && !_updatingControls)
            onChanged(nv);
    }

    // ------------------------------------------------------------------
    // Styles
    // ------------------------------------------------------------------

    private static GUIStyle _rich;
    private static GUIStyle Rich()
    {
        if (_rich == null)
            _rich = new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true };
        return _rich;
    }

    private static GUIStyle _wrap;
    private static GUIStyle Wrap()
    {
        if (_wrap == null)
            _wrap = new GUIStyle(GUI.skin.label) { wordWrap = true };
        return _wrap;
    }

    private static GUIStyle _centered;
    private static GUIStyle Centered()
    {
        if (_centered == null)
            _centered = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter };
        return _centered;
    }

    private static GUIStyle _rowSelected;
    private static GUIStyle _rowNormal;

    private static GUIStyle RowStyle(bool selected)
    {
        if (_rowNormal == null)
        {
            _rowNormal = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(6, 6, 2, 2),
            };
            _rowSelected = new GUIStyle(_rowNormal);
            _rowSelected.normal.textColor = new Color(0.4f, 0.8f, 1f);
            _rowSelected.normal.background = Texture2D.whiteTexture;
            _rowSelected.normal.textColor = new Color(0.1f, 0.35f, 0.6f);
        }
        return selected ? _rowSelected : _rowNormal;
    }
}
