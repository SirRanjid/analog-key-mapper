using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Tk75.Diagnostics
{
    // Windows ends processes from higher shutdown levels to lower levels.
    // The editor uses the default 0x280; this HID-owning child must remain
    // available while the editor restores lighting in WM_ENDSESSION.
    // CREATE_NO_WINDOW hides a console; it does not establish parent/child
    // shutdown ordering or make Console.CancelKeyPress a session-end handler.
    // https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-setprocessshutdownparameters
    public static class MonitorShutdownOrder
    {
        public const uint HelperShutdownLevel = 0x100;

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetProcessShutdownParameters(uint level, uint flags);

        internal static void ConfigureNative()
        {
            Configure(delegate(uint level, uint flags) {
                if (!SetProcessShutdownParameters(level, flags))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "The keyboard helper could not establish its cleanup order for Windows shutdown.");
                return true;
            });
        }

        // The injected setter keeps tests independent of the local process's
        // real shutdown policy. Failure prevents opening HID or enabling input;
        // the stream host reports it through its ordinary connection-error path.
        public static void Configure(Func<uint, uint, bool> setOrder)
        {
            if (setOrder == null) throw new ArgumentNullException("setOrder");
            // 0x100..0x1FF is the documented application-last range. No system
            // reserved level and no SHUTDOWN_NORETRY/forced-termination flag.
            if (!setOrder(HelperShutdownLevel, 0))
                throw new InvalidOperationException("The keyboard helper could not establish its cleanup order for Windows shutdown.");
        }
    }
}
