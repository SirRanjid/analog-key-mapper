using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Tk75.App
{
    // One interactive UI per installation and Windows session. Diagnostic and
    // output-host entry points never acquire this mutex, so their normal child
    // processes can still run while the UI owns it.
    internal sealed class SingleInstanceWindow : IDisposable
    {
        readonly Mutex mutex;
        readonly string executablePath;
        bool owns, disposed;
        internal bool IsOwner { get { return owns; } }

        SingleInstanceWindow(Mutex mutex, bool owns, string executablePath)
        { this.mutex = mutex; this.owns = owns; this.executablePath = executablePath; }

        internal static SingleInstanceWindow Acquire(string baseDirectory)
        {
            string installation = Path.GetFullPath(baseDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToUpperInvariant();
            string identity;
            using (SHA256 hash = SHA256.Create())
                identity = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(installation))).Replace("-", "");
            string path = Path.GetFullPath(Application.ExecutablePath);
            Mutex mutex = new Mutex(false, "Local\\AnalogKeyMapper.UI." + identity);
            try
            {
                bool owns;
                try { owns = mutex.WaitOne(0, false); }
                catch (AbandonedMutexException) { owns = true; }
                return new SingleInstanceWindow(mutex, owns, path);
            }
            catch { mutex.Dispose(); throw; }
        }

        internal void BringExistingToFront()
        {
            if (owns || disposed) return;
            // MainWindowHandle can still be zero during the first instance's
            // startup; that instance will show its own window normally. Do not
            // open a second UI or wait indefinitely for it to become responsive.
            try
            {
                int currentId;
                using (Process current = Process.GetCurrentProcess()) currentId = current.Id;
                foreach (Process candidate in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executablePath)))
                using (candidate)
                {
                    try
                    {
                        if (candidate.Id == currentId || candidate.HasExited || candidate.MainModule == null) continue;
                        string path = Path.GetFullPath(candidate.MainModule.FileName);
                        if (!String.Equals(path, executablePath, StringComparison.OrdinalIgnoreCase)) continue;
                        IntPtr window = candidate.MainWindowHandle;
                        if (window == IntPtr.Zero) continue;
                        if (IsIconic(window)) ShowWindowAsync(window, 9); // SW_RESTORE
                        SetForegroundWindow(window);
                        return;
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { if (owns) { owns = false; mutex.ReleaseMutex(); } }
            finally { mutex.Dispose(); }
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool ShowWindowAsync(IntPtr window, int command);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetForegroundWindow(IntPtr window);
    }
}
