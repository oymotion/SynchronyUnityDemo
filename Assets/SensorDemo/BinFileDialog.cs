using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
#if UNITY_STANDALONE_OSX
using System.Diagnostics;
#endif
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

namespace SensorSdk.ExampleUnity
{
    // ------------------------------------------------------------------
    // Native bin file picker (editor / Windows / macOS standalone)
    // ------------------------------------------------------------------
    static class BinFileDialog
    {
        public static bool IsSupported
        {
            get
            {
#if UNITY_EDITOR || UNITY_STANDALONE_WIN || UNITY_STANDALONE_OSX
                return true;
#else
                return false;
#endif
            }
        }

        // Picked file path (';'-joined for multi-select), null on cancel.
        // initialDir is the dialog's starting folder ("" = system default).
        public static string OpenBin(bool multi, string initialDir)
        {
#if UNITY_EDITOR
            string p = UnityEditor.EditorUtility.OpenFilePanel(
                multi ? "Select bin files" : "Select bin file", initialDir, "bin");
            return p.Length == 0 ? null : p;
#elif UNITY_STANDALONE_WIN
            return OpenBinWin(multi, initialDir);
#elif UNITY_STANDALONE_OSX
            return OpenBinMac(multi, initialDir);
#else
            return null;
#endif
        }

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        // ------------------------------------------------------------------
        // Windows: comdlg32 GetOpenFileName
        // ------------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private class OpenFileName
        {
            public int StructSize;
            public IntPtr Owner;
            public IntPtr Instance;
            public string Filter;
            public string CustomFilter;
            public int MaxCustFilter;
            public int FilterIndex;
            public StringBuilder File;
            public int MaxFile;
            public IntPtr FileTitle;
            public int MaxFileTitle;
            public string InitialDir;
            public string Title;
            public int Flags;
            public short FileOffset;
            public short FileExtension;
            public string DefExt;
            public IntPtr CustData;
            public IntPtr Hook;
            public string TemplateName;
            public IntPtr Reserved;
            public int Reserved2;
            public int FlagsEx;
        }

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetOpenFileName(OpenFileName ofn);

        private const int OfnExplorer = 0x00080000;
        private const int OfnFileMustExist = 0x00001000;
        private const int OfnAllowMultiSelect = 0x00000200;
        private const int FileBufferChars = 8192;

        private static string OpenBinWin(bool multi, string initialDir)
        {
            var ofn = new OpenFileName
            {
                StructSize = Marshal.SizeOf(typeof(OpenFileName)),
                Filter = "Bin Files (*.bin)\0*.bin\0All Files (*.*)\0*.*\0",
                File = new StringBuilder(new char[FileBufferChars]),
                MaxFile = FileBufferChars,
                Flags = OfnExplorer | OfnFileMustExist | (multi ? OfnAllowMultiSelect : 0),
                Title = multi ? "Select bin files" : "Select bin file",
                InitialDir = initialDir.Length > 0 ? initialDir : null,
            };
            if (!GetOpenFileName(ofn))
                return null;
            if (!multi)
                return ofn.File.ToString();

            // Multi-select buffer: dir\0file1\0file2\0\0
            var parts = new List<string>();
            int i = 0;
            while (i < ofn.File.Length && ofn.File[i] != '\0')
            {
                var one = new StringBuilder();
                while (i < ofn.File.Length && ofn.File[i] != '\0')
                    one.Append(ofn.File[i++]);
                ++i;
                if (one.Length > 0)
                    parts.Add(one.ToString());
            }
            if (parts.Count == 0)
                return null;
            if (parts.Count == 1)
                return parts[0];
            string dir = parts[0];
            var files = new List<string>();
            for (int k = 1; k < parts.Count; ++k)
                files.Add(Path.Combine(dir, parts[k]));
            return string.Join(";", files.ToArray());
        }
#endif

#if UNITY_STANDALONE_OSX && !UNITY_EDITOR
        // ------------------------------------------------------------------
        // macOS: osascript choose file
        // ------------------------------------------------------------------

        private static string OpenBinMac(bool multi, string initialDir)
        {
            string location = initialDir.Length > 0
                ? " default location (POSIX file \"" + initialDir + "\")"
                : "";
            string script = multi
                ? "set fs to choose file with multiple selections allowed" + location + "\n"
                  + "set out to \"\"\n"
                  + "repeat with f in fs\n"
                  + "set out to out & (POSIX path of f) & \";\"\n"
                  + "end repeat\n"
                  + "return out"
                : "POSIX path of (choose file" + location + ")";
            string scpt = Path.Combine(Path.GetTempPath(), "sensor_pick_bin.scpt");
            File.WriteAllText(scpt, script);
            var psi = new ProcessStartInfo
            {
                FileName = "/usr/bin/osascript",
                Arguments = "\"" + scpt + "\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (Process proc = Process.Start(psi))
            {
                string outText = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit();
                if (proc.ExitCode != 0 || outText.Length == 0)
                    return null;
                return outText.TrimEnd(';');
            }
        }
#endif
    }
}
