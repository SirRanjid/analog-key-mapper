using System;
using System.IO;
using System.Text;

namespace Tk75.App
{
    internal static class TrayPreferences
    {
        internal const string FileName = "ui-minimize-to-tray.txt";

        internal static bool Load(string dataPath)
        {
            string path = Path.Combine(dataPath, FileName);
            if (!File.Exists(path)) return false;
            string value = File.ReadAllText(path).Trim();
            if (value == "true") return true;
            if (value == "false") return false;
            throw new InvalidDataException("Invalid minimize-to-tray preference.");
        }

        internal static void Save(string dataPath, bool enabled)
        {
            Directory.CreateDirectory(dataPath);
            string path = Path.Combine(dataPath, FileName);
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes((enabled ? "true" : "false") + Environment.NewLine);
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
