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
        readonly string activationName;
        ActivationWindow activationWindow;
        bool owns, disposed;
        internal bool IsOwner { get { return owns; } }

        SingleInstanceWindow(Mutex mutex, bool owns, string executablePath, string activationName)
        {
            this.mutex = mutex; this.owns = owns; this.executablePath = executablePath; this.activationName = activationName;
            if (owns) activationWindow = new ActivationWindow(activationName);
        }

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
                return new SingleInstanceWindow(mutex, owns, path, "AnalogKeyMapper.Activate." + identity);
            }
            catch { mutex.Dispose(); throw; }
        }

        internal void BringExistingToFront()
        {
            if (owns || disposed) return;
            // A tray application has no visible MainWindowHandle. A tiny hidden
            // top-level window remains addressable without polling or a thread.
            IntPtr activation = FindWindow(null, activationName);
            if (activation != IntPtr.Zero)
            {
                uint processId; GetWindowThreadProcessId(activation, out processId);
                AllowSetForegroundWindow(processId);
                PostMessage(activation, ActivationWindow.ShowMessage, IntPtr.Zero, IntPtr.Zero);
                return;
            }
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

        internal void OnShowRequested(Action show)
        { if (activationWindow != null) activationWindow.SetShowAction(show); }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try
            {
                if (activationWindow != null) { activationWindow.Dispose(); activationWindow = null; }
                if (owns) { owns = false; mutex.ReleaseMutex(); }
            }
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
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr FindWindow(string className, string windowName);
        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool AllowSetForegroundWindow(uint processId);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

        sealed class ActivationWindow : NativeWindow, IDisposable
        {
            internal const int ShowMessage = 0x8000 + 73;
            Action show;
            bool pending;
            internal ActivationWindow(string name)
            { CreateHandle(new CreateParams { Caption = name, Style = unchecked((int)0x80000000), ExStyle = 0x80 }); }
            internal void SetShowAction(Action action)
            { show = action; if (pending && show != null) { pending = false; show(); } }
            protected override void WndProc(ref Message message)
            {
                if (message.Msg == ShowMessage)
                { if (show == null) pending = true; else show(); return; }
                base.WndProc(ref message);
            }
            public void Dispose() { show = null; if (Handle != IntPtr.Zero) DestroyHandle(); }
        }
    }
}
