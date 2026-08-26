using System;
using System.Collections.Generic;
using UnityEngine;

namespace SensorSdk.ExampleUnity
{
    /// <summary>FFT spectrum strip view.</summary>
    public sealed class SpectrumView
    {
        private float[] _freqs = new float[0];
        private List<float[]> _mags = new List<float[]>();
        private string[] _labels = new string[0];
        private string _placeholder = "Not connected";
        private int _colorIndex = -1;

        private Texture2D _tex;
        private Color32[] _pixels;
        private bool _dirty = true;
        private long _lastRepaintMs;

        public void SetResult(float[] freqs, List<float[]> mags)
        {
            _freqs = freqs ?? new float[0];
            _mags = mags ?? new List<float[]>();
            _dirty = true;
        }

        public void ClearResult()
        {
            _freqs = new float[0];
            _mags = new List<float[]>();
            _dirty = true;
        }

        public void SetLabels(string[] labels)
        {
            _labels = labels ?? new string[0];
            _dirty = true;
        }

        public void SetPlaceholder(string text)
        {
            _placeholder = text;
            _dirty = true;
        }

        // colorIndex >= 0 pins the curve color (single-channel rows); -1
        // colors each curve by its row index.
        public void SetColorIndex(int colorIndex)
        {
            _colorIndex = colorIndex;
            _dirty = true;
        }

        public void MarkDirty()
        {
            _dirty = true;
        }

        /// <summary>Draws the strip inside the given rect (call from OnGUI).</summary>
        public void Draw(Rect rect)
        {
            int w = Mathf.Max(2, (int)rect.width);
            int h = Mathf.Max(2, (int)rect.height);
            EnsureTexture(w, h);

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (_dirty && now - _lastRepaintMs >= WaveformView.RepaintIntervalMs)
            {
                _lastRepaintMs = now;
                _dirty = false;
                Repaint();
            }
            GUI.DrawTexture(rect, _tex);

            Color prev = GUI.color;
            bool drawn = _freqs.Length >= 2 && _mags.Count > 0;
            if (!drawn && _placeholder.Length > 0)
            {
                GUI.color = WaveformView.PlaceholderColor;
                GUI.Label(rect, _placeholder, CenterStyle());
            }
            else if (drawn)
            {
                for (int ch = 0; ch < _mags.Count; ch++)
                {
                    int colorIdx = _colorIndex >= 0 ? _colorIndex : ch;
                    GUI.color = WaveformView.ChannelColors[colorIdx % WaveformView.ChannelColors.Length];
                    string label = ch < _labels.Length ? _labels[ch] : $"ch{ch}";
                    GUI.Label(new Rect(rect.x + 6, rect.y + 1 + ch * 13, 120, 14), label, SmallStyle());
                }
                // Frequency axis labels
                GUI.color = WaveformView.PlaceholderColor;
                GUI.Label(new Rect(rect.x + 4, rect.yMax - 14, 40, 12), "0", AxisStyle());
                double fMax = _freqs[_freqs.Length - 1];
                var r = new Rect(rect.xMax - 102, rect.yMax - 14, 100, 12);
                GUI.Label(r, $"{fMax:F1} Hz", AxisRightStyle());
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
            Clear(WaveformView.BackgroundColor);

            // Bottom axis-text strip
            int px = 2, py = 2;
            int pw = w - 4, ph = h - 2 - 14 - 2;
            if (pw < 2 || ph < 2)
            {
                Flush();
                return;
            }
            RectBorder(px, py, pw, ph, WaveformView.BorderColor);

            if (_freqs.Length >= 2 && _mags.Count > 0)
            {
                double fMax = _freqs[_freqs.Length - 1];
                if (fMax > 0.0)
                {
                    // Y range
                    double peak = 0.0;
                    foreach (float[] row in _mags)
                        foreach (float v in row)
                            peak = Math.Max(peak, v);
                    double yMax = peak > 0.0 ? peak * 1.1 : 1.0;

                    for (int ch = 0; ch < _mags.Count; ch++)
                    {
                        float[] row = _mags[ch];
                        int colorIdx = _colorIndex >= 0 ? _colorIndex : ch;
                        Color32 color = WaveformView.ChannelColors[colorIdx % WaveformView.ChannelColors.Length];
                        int prevX = -1, prevY = -1;
                        for (int i = 0; i < row.Length && i < _freqs.Length; i++)
                        {
                            int x = px + (int)(_freqs[i] / fMax * (pw - 1));
                            int y = py + ph - 1 - (int)(row[i] / yMax * (ph - 2));
                            if (prevX >= 0)
                                Line(prevX, prevY, x, y, color);
                            else
                                SetPixel(x, y, color);
                            prevX = x;
                            prevY = y;
                        }
                    }
                }
            }
            Flush();
        }

        // ---- pixel helpers ---------------------------------------------------

        private void SetPixel(int x, int yFromTop, Color32 c)
        {
            int w = _tex.width, h = _tex.height;
            if (x < 0 || x >= w || yFromTop < 0 || yFromTop >= h)
                return;
            _pixels[(h - 1 - yFromTop) * w + x] = c;
        }

        private void Line(int x0, int y0, int x1, int y1, Color32 c)
        {
            // Bresenham.
            int dx = Math.Abs(x1 - x0), dy = Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
            int err = dx - dy;
            while (true)
            {
                SetPixel(x0, y0, c);
                if (x0 == x1 && y0 == y1)
                    break;
                int e2 = 2 * err;
                if (e2 > -dy) { err -= dy; x0 += sx; }
                if (e2 < dx) { err += dx; y0 += sy; }
            }
        }

        private void RectBorder(int x, int y, int w, int h, Color32 c)
        {
            for (int xx = x; xx < x + w; xx++)
            {
                SetPixel(xx, y, c);
                SetPixel(xx, y + h - 1, c);
            }
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

        private static GUIStyle _axis;
        private static GUIStyle AxisStyle()
        {
            if (_axis == null)
                _axis = new GUIStyle(GUI.skin.label) { fontSize = 10 };
            return _axis;
        }

        private static GUIStyle _axisRight;
        private static GUIStyle AxisRightStyle()
        {
            if (_axisRight == null)
                _axisRight = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 10,
                    alignment = TextAnchor.MiddleRight,
                };
            return _axisRight;
        }
    }
}
