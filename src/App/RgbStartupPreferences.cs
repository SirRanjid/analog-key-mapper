using System;
using System.IO;
using System.Text;

namespace Tk75.App
{
    // Application-level consent, deliberately separate from mapping profiles.
    // Missing or invalid content must never implicitly enable automatic repair.
    internal static class RgbStartupPreferences
    {
        internal const string FileName = "rgb-startup-auto-repair.txt";
        internal static bool Load(string root)
        {
            string path = Path.Combine(root, FileName);
            if (!File.Exists(path)) return false;
            string value;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > 32) throw new InvalidDataException("Invalid automatic startup lighting repair preference.");
                using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), false)) value = reader.ReadToEnd().Trim();
            }
            if (value == "true") return true;
            if (value == "false") return false;
            throw new InvalidDataException("Invalid automatic startup lighting repair preference.");
        }

        internal static void Save(string root, bool enabled)
        {
            Directory.CreateDirectory(root);
            string path = Path.Combine(root, FileName);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes((enabled ? "true" : "false") + Environment.NewLine);
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
