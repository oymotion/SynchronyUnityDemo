using System;
using System.Collections.Generic;
using UnityEngine;

namespace SensorSdk.ExampleUnity
{
    /// <summary>Waveform view; pulls from a RingBuffer during Draw.</summary>
    public sealed class WaveformView
    {
        // Channel colors
        public static readonly Color[] ChannelColors =
        {
            new Color(31/255f, 119/255f, 180/255f), new Color(255/255f, 127/255f, 14/255f),
            new Color(44/255f, 160/255f, 44/255f),  new Color(214/255f, 39/255f, 40/255f),
            new Color(148/255f, 103/255f, 189/255f), new Color(140/255f, 86/255f, 75/255f),
            new Color(227/255f, 119/255f, 194/255f), new Color(127/255f, 127/255f, 127/255f),
        };
        public static readonly Color BackgroundColor = new Color(28/255f, 28/255f, 30/255f);
        public static readonly Color BorderColor = new Color(90/255f, 90/255f, 90/255f);
        public static readonly Color MidlineColor = new Color(60/255f, 60/255f, 60/255f);
        public static readonly Color PlaceholderColor = new Color(150/255f, 150/255f, 150/255f);

        private const float SideMargin = 66;
        public const int RepaintIntervalMs = 50;

        private RingBuffer _buffer;
        private object _mutex;
        private int _channel = -1;
        private int _colorIndex = -1;
        private string[] _labels = new string[0];
        private bool _fixedRange;
        private double _fixedLow = -1.0;
        private double _fixedHigh = 1.0;
        private string _placeholder = "Not connected";
        private string _sideText = string.Empty;
        private Color _sideColor = Color.white;

        private Texture2D _tex;
        private Color32[] _pixels;
        private bool _dirty = true;
        private long _lastRepaintMs;

        /// <summary>channel == -1 draws all channels; colorIndex picks the curve color.</summary>
        public void SetSource(RingBuffer buffer, object mutex, int channel, int colorIndex = -1)
        {
            _buffer = buffer;
            _mutex = mutex;
            _channel = channel;
            _colorIndex = colorIndex;
            _dirty = true;
        }

        public bool HasSource => _buffer != null;

        public void SetLabels(string[] labels)
        {
            _labels = labels ?? new string[0];
        }

        public void SetFixedYRange(double low, double high)
        {
            _fixedRange = true;
            _fixedLow = low;
            _fixedHigh = high;
            _dirty = true;
        }

        public void SetAutoYRange()
        {
            _fixedRange = false;
            _dirty = true;
        }

        public void SetPlaceholder(string text)
        {
            _placeholder = text;
        }

        /// <summary>Right-margin text, drawn in the given color.</summary>
        public void SetSideText(string text, Color color)
        {
            _sideText = text;
            _sideColor = color;
        }

        /// <summary>Marks the view dirty.</summary>
        public void MarkDirty()
        {
            _dirty = true;
        }

        /// <summary>Draws the view inside the given rect (call from OnGUI).</summary>
        public void Draw(Rect rect)
        {
            int w = Mathf.Max(2, (int)rect.width);
            int h = Mathf.Max(2, (int)rect.height);
            EnsureTexture(w, h);

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (_dirty && now - _lastRepaintMs >= RepaintIntervalMs)
            {
                _lastRepaintMs = now;
                _dirty = false;
                Repaint();
            }
            GUI.DrawTexture(rect, _tex);

            // Text overlays (labels / placeholder / side text).
            Color prev = GUI.color;
            bool drawn = _buffer != null && _buffer.Allocated && _buffer.Length >= 2;
            if (!drawn && _placeholder.Length > 0)
            {
                GUI.color = PlaceholderColor;
                GUI.Label(rect, _placeholder, CenterStyle());
            }
            GUI.color = prev;
            for (int i = 0; i < _labels.Count0(); i++)
            {
                int colorIdx = _colorIndex >= 0 ? _colorIndex : i;
                GUI.color = ChannelColors[colorIdx % ChannelColors.Length];
                GUI.Label(new Rect(rect.x + 6, rect.y + 1 + i * 13, 120, 14), _labels[i], SmallStyle());
            }
            GUI.color = prev;
            if (_sideText.Length > 0)
            {
                GUI.color = _sideColor;
                GUI.Label(new Rect(rect.xMax - SideMargin + 4, rect.y + 1, SideMargin - 6, 16),
                          _sideText, SmallStyle());
            }
            GUI.color = prev;
        }

        private void EnsureTexture(int w, int h)
        {
            if (_tex != null && _tex.width == w && _tex.height == h)
                return;
            if (_tex != null)
                UnityEngine.Object.Destroy(_tex);
            _tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            _pixels = new Color32[w * h];
            _dirty = true;
            _lastRepaintMs = 0;
        }

        private void Repaint()
        {
            int w = _tex.width, h = _tex.height;
            Clear(BackgroundColor);

            // Plot rect
            int px = 2, py = 2;
            int pw = w - 2 - (int)SideMargin - 2;
            int ph = h - 4;
            if (pw < 2 || ph < 2)
            {
                Flush();
                return;
            }
            RectBorder(px, py, pw, ph, BorderColor);
            HLine(px, px + pw, py + ph / 2, MidlineColor);

            RingBuffer buf = _buffer;
            object mutex = _mutex;
            if (buf != null && mutex != null)
            {
                lock (mutex)
                {
                    if (buf.Allocated && buf.Length >= 2)
                        DrawChannels(buf, px, py, pw, ph);
                }
            }
            Flush();
        }

        private void DrawChannels(RingBuffer buf, int px, int py, int pw, int ph)
        {
            var channels = new List<int>();
            if (_channel >= 0)
            {
                if (_channel < buf.Channels)
                    channels.Add(_channel);
            }
            else
            {
                for (int ch = 0; ch < buf.Channels; ch++)
                    channels.Add(ch);
            }
            if (channels.Count == 0)
                return;

            int len = buf.Length;

            // Y range
            double low = _fixedLow;
            double high = _fixedHigh;
            if (!_fixedRange)
            {
                double mn = double.MaxValue;
                double mx = double.MinValue;
                foreach (int ch in channels)
                {
                    float[] samples = buf.Samples[ch];
                    int step = Math.Max(1, len / (pw * 2));
                    for (int i = 0; i < len; i += step)
                    {
                        double v = samples[(buf.WriteIndex + i) % len];
                        mn = Math.Min(mn, v);
                        mx = Math.Max(mx, v);
                    }
                }
                if (mn > mx)
                {
                    mn = -1.0;
                    mx = 1.0;
                }
                double margin = Math.Max((mx - mn) * 0.1, 0.01);
                if (mn == mx)
                {
                    mn -= 1.0;
                    mx += 1.0;
                    margin = 0.0;
                }
                low = mn - margin;
                high = mx + margin;
            }
            double span = high - low;
            if (span <= 0)
                return;

            foreach (int ch in channels)
            {
                int colorIdx = _colorIndex >= 0 ? _colorIndex : ch;
                Color32 color = ChannelColors[colorIdx % ChannelColors.Length];
                float[] samples = buf.Samples[ch];
                int prevY = -1;
                for (int x = 0; x < pw; x++)
                {
                    int si = (int)((long)x * len / pw);
                    double v = samples[(buf.WriteIndex + si) % len];
                    int ty = py + ph - 1 - (int)((v - low) / span * (ph - 2));
                    if (prevY >= 0)
                        VLine(px + x, prevY, ty, color);
                    else
                        SetPixel(px + x, ty, color);
                    prevY = ty;
                }
            }
        }

        // ---- pixel helpers ---------------------------------------------------

        private void SetPixel(int x, int yFromTop, Color32 c)
        {
            int w = _tex.width, h = _tex.height;
            if (x < 0 || x >= w || yFromTop < 0 || yFromTop >= h)
                return;
            _pixels[(h - 1 - yFromTop) * w + x] = c;
        }

        private void VLine(int x, int y0, int y1, Color32 c)
        {
            if (y0 > y1) { int t = y0; y0 = y1; y1 = t; }
            for (int y = y0; y <= y1; y++)
                SetPixel(x, y, c);
        }

        private void HLine(int x0, int x1, int y, Color32 c)
        {
            for (int x = x0; x < x1; x++)
                SetPixel(x, y, c);
        }

        private void RectBorder(int x, int y, int w, int h, Color32 c)
        {
            HLine(x, x + w, y, c);
            HLine(x, x + w, y + h - 1, c);
            for (int yy = y; yy < y + h; yy++)
            {
                SetPixel(x, yy, c);
                SetPixel(x + w - 1, yy, c);
            }
        }

        private void Clear(Color c)
        {
            Color32 c32 = c;
            for (int i = 0; i < _pixels.Length; i++)
                _pixels[i] = c32;
        }

        private void Flush()
        {
            _tex.SetPixels32(_pixels);
            _tex.Apply(false);
        }

        private static GUIStyle _center;
        private static GUIStyle CenterStyle()
        {
            if (_center == null)
                _center = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 12,
                };
            return _center;
        }

        private static GUIStyle _small;
        private static GUIStyle SmallStyle()
        {
            if (_small == null)
                _small = new GUIStyle(GUI.skin.label) { fontSize = 11 };
            return _small;
        }
    }

    internal static class WaveformViewExt
    {
        public static int Count0(this string[] a) => a == null ? 0 : a.Length;
    }
}
