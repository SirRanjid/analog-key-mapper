using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Tk75.App
{
    // Optional, global legacy-keyboard suppression. WH_KEYBOARD_LL has no
    // physical-device identity and does not guarantee suppression of Raw Input.
    // Installation is asynchronous; construction/eligibility updates never
    // install a hook. No SendInput, injected events, or other-process code.
    public sealed class KeyboardSuppression : IDisposable
    {
        readonly object lifecycle = new object();
        readonly Stopwatch clock = Stopwatch.StartNew();
        HookRun current;
        volatile bool enabled;
        volatile bool disposed;
        int protectedKeyCode = 0x78, protectedModifiers;
        bool protectedEnabled = true;
        int protectedStopKeyCode = 0x77, protectedStopModifiers;
        bool protectedStopEnabled = true;

        public static bool IsVirtualKeyDown(int virtualKey)
        {
            if (virtualKey < 1 || virtualKey > 255) throw new ArgumentOutOfRangeException("virtualKey");
            return (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
        }
        public void SetProtectedHotkey(int keyCode, int modifiers, bool value)
        {
            lock (lifecycle) SetProtectedHotkeys(keyCode, modifiers, value, protectedStopKeyCode, protectedStopModifiers, protectedStopEnabled);
        }
        public void SetProtectedHotkeys(int modeKey, int modeModifiers, bool modeEnabled, int stopKey, int stopModifiers, bool stopEnabled)
        {
            KeyboardSuppressionPolicy.ValidateProtectedHotkeys(modeKey, modeModifiers, modeEnabled, stopKey, stopModifiers, stopEnabled);
            lock (lifecycle)
            {
                if (disposed) throw new ObjectDisposedException("KeyboardSuppression");
                protectedKeyCode = modeKey; protectedModifiers = modeModifiers; protectedEnabled = modeEnabled;
                protectedStopKeyCode = stopKey; protectedStopModifiers = stopModifiers; protectedStopEnabled = stopEnabled;
                HookRun run = Volatile.Read(ref current);
                if (run != null && !run.StopRequested) run.Policy.SetProtectedHotkeys(modeKey, modeModifiers, modeEnabled, stopKey, stopModifiers, stopEnabled);
            }
        }

        public bool IsEnabled { get { return enabled && !disposed; } }
        // Windows can silently remove a timed-out low-level hook. This reports
        // confirmed installation, not a claim of ongoing hook health.
        public bool IsInstalled
        {
            get { HookRun run = Volatile.Read(ref current); return IsEnabled && run != null && run.Installed; }
        }
        public string Status
        {
            get
            {
                HookRun run = Volatile.Read(ref current);
                if (!IsEnabled) return run != null && run.RemovalError != null ? run.RemovalError : "Aus";
                if (run == null) return "Startet";
                if (run.Error != null) return run.Error;
                if (run.Completed) return "Tastensperre beendet";
                return run.Installed ? "Tastensperre bereit" : "Startet";
            }
        }

        public void SetEnabled(bool value)
        {
            lock (lifecycle)
            {
                if (disposed) { if (value) throw new ObjectDisposedException("KeyboardSuppression"); return; }
                if (enabled == value) return;
                enabled = value;
                if (value)
                {
                    var run = new HookRun(clock, protectedKeyCode, protectedModifiers, protectedEnabled, protectedStopKeyCode, protectedStopModifiers, protectedStopEnabled);
                    Volatile.Write(ref current, run);
                    run.Start();
                }
                else
                {
                    HookRun run = Volatile.Read(ref current);
                    if (run != null) run.RequestStop();
                }
            }
        }

        public void UpdateEligibility(IEnumerable<SuppressionKey> keys, bool controllerActive, bool suspended)
        {
            if (!IsEnabled) return;
            HookRun run = Volatile.Read(ref current);
            if (run != null && !run.StopRequested)
                run.Policy.PublishEligibility(keys, controllerActive, suspended, clock.ElapsedMilliseconds);
        }

        public void Dispose()
        {
            HookRun run;
            lock (lifecycle)
            {
                if (disposed) return;
                disposed = true; enabled = false; run = Volatile.Read(ref current);
                if (run != null) run.RequestStop();
            }
            if (run != null) run.Join(500);
        }

        sealed class HookRun
        {
            // Retain delegates until unhook is confirmed. If Windows rejects
            // removal, an inert callback must remain rooted rather than become
            // a native pointer into collected memory.
            static readonly object rootsGate = new object();
            static readonly HashSet<HookRun> roots = new HashSet<HookRun>();
            readonly object nativeGate = new object();
            readonly Stopwatch clock;
            readonly Thread worker;
            readonly HookProc callback;
            internal readonly KeyboardSuppressionPolicy Policy = new KeyboardSuppressionPolicy();
            internal volatile bool StopRequested;
            internal volatile bool Completed;
            internal volatile string Error;
            internal volatile string RemovalError;
            uint threadId;
            uint ownProcessId;
            IntPtr hook;
            internal bool Installed { get { return !StopRequested && !Completed && Interlocked.CompareExchange(ref hook, IntPtr.Zero, IntPtr.Zero) != IntPtr.Zero; } }
            internal HookRun(Stopwatch clock, int keyCode, int modifiers, bool hotkeyEnabled, int stopKey, int stopModifiers, bool stopEnabled)
            {
                this.clock = clock; callback = OnKeyboard;
                Policy.SetProtectedHotkeys(keyCode, modifiers, hotkeyEnabled, stopKey, stopModifiers, stopEnabled);
                worker = new Thread(Pump); worker.IsBackground = true; worker.Name = "Optional keyboard suppression";
            }
            internal void Start()
            {
                try { worker.Start(); }
                catch (Exception ex) { Error = "Tastensperre konnte nicht starten: " + ex.Message; Completed = true; }
            }
            internal void Join(int milliseconds)
            { if (Thread.CurrentThread != worker && worker.IsAlive) worker.Join(milliseconds); }
            internal void RequestStop()
            {
                StopRequested = true;
                RemoveHook();
                uint id = Volatile.Read(ref threadId);
                if (id != 0) PostThreadMessage(id, 0x0012, UIntPtr.Zero, IntPtr.Zero);
            }
            void RemoveHook()
            {
                lock (nativeGate)
                {
                    if (hook == IntPtr.Zero) return;
                    if (UnhookWindowsHookEx(hook))
                    {
                        Interlocked.Exchange(ref hook, IntPtr.Zero); RemovalError = null;
                        lock (rootsGate) roots.Remove(this);
                    }
                    else
                    {
                        RemovalError = "Tastensperre inaktiv; Entfernen nicht bestätigt: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
                        Error = RemovalError;
                    }
                }
            }
            void Pump()
            {
                try
                {
                    Volatile.Write(ref threadId, GetCurrentThreadId()); ownProcessId = GetCurrentProcessId();
                    Message message;
                    // Establish a queue before publishing/processing WM_QUIT.
                    PeekMessage(out message, IntPtr.Zero, 0, 0, 0);
                    lock (nativeGate)
                    {
                        if (StopRequested) return;
                        lock (rootsGate) roots.Add(this);
                        hook = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
                        if (hook == IntPtr.Zero)
                        {
                            int error = Marshal.GetLastWin32Error();
                            lock (rootsGate) roots.Remove(this);
                            throw new Win32Exception(error, "Windows konnte die Tastensperre nicht einrichten.");
                        }
                    }
                    // Install before sampling held keys. Until the message pump
                    // starts, this conservatively forwards keys already down.
                    var held = new List<int>();
                    for (int key = 1; key <= 255; key++) if ((GetAsyncKeyState(key) & 0x8000) != 0) held.Add(key);
                    Policy.SeedHeldVirtualKeys(held);
                    while (!StopRequested)
                    {
                        int result = GetMessage(out message, IntPtr.Zero, 0, 0);
                        if (result == 0) break;
                        if (result < 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Tastensperre-Nachrichten konnten nicht gelesen werden.");
                        TranslateMessage(ref message); DispatchMessage(ref message);
                    }
                }
                catch (Exception ex) { Error = "Tastensperre nicht verfügbar: " + ex.Message; }
                finally { StopRequested = true; RemoveHook(); Completed = true; }
            }
            IntPtr OnKeyboard(int code, IntPtr message, IntPtr data)
            {
                if (code != 0 || StopRequested) return CallNextHookEx(IntPtr.Zero, code, message, data);
                long kind = message.ToInt64();
                if (kind != 0x0100 && kind != 0x0101 && kind != 0x0104 && kind != 0x0105)
                    return CallNextHookEx(IntPtr.Zero, code, message, data);
                try
                {
                    int flags = Marshal.ReadInt32(data, 8);
                    if ((flags & 0x12) == 0)
                    {
                        uint foregroundProcess;
                        IntPtr foreground = GetForegroundWindow();
                        uint foregroundThread = GetWindowThreadProcessId(foreground, out foregroundProcess);
                        bool suspended = foreground == IntPtr.Zero || foregroundThread == 0 || foregroundProcess == 0 || foregroundProcess == ownProcessId;
                        // Ctrl/Alt/Win and Shift needed by a protected shortcut
                        // pass through. Other mapped Shift keys may be blocked.
                        // Read Windows' current state of the four modifier groups.
                        int modifiers = (IsVirtualKeyDown(0x12) ? 1 : 0) | (IsVirtualKeyDown(0x11) ? 2 : 0) |
                            (IsVirtualKeyDown(0x10) ? 4 : 0) | (IsVirtualKeyDown(0x5b) || IsVirtualKeyDown(0x5c) ? 8 : 0);
                        if (Policy.ShouldSuppress(Marshal.ReadInt32(data, 4), (flags & 1) != 0, Marshal.ReadInt32(data, 0),
                            kind == 0x0100 || kind == 0x0104, false, clock.ElapsedMilliseconds, suspended, modifiers)) return new IntPtr(1);
                    }
                }
                catch (Exception ex)
                {
                    Error = "Tastensperre abgebrochen: " + ex.Message; StopRequested = true;
                    PostThreadMessage(Volatile.Read(ref threadId), 0x0012, UIntPtr.Zero, IntPtr.Zero);
                }
                return CallNextHookEx(IntPtr.Zero, code, message, data);
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
        [StructLayout(LayoutKind.Sequential)]
        struct Message
        {
            internal IntPtr Window;
            internal uint Kind;
            internal UIntPtr WParam;
            internal IntPtr LParam;
            internal uint Time;
            internal int X;
            internal int Y;
            internal uint Private;
        }
        [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
        static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint thread);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int virtualKey);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", SetLastError = true)] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll", EntryPoint = "PeekMessageW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] static extern bool PeekMessage(out Message message, IntPtr window, uint min, uint max, uint remove);
        [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)] static extern int GetMessage(out Message message, IntPtr window, uint min, uint max);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool TranslateMessage(ref Message message);
        [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] static extern IntPtr DispatchMessage(ref Message message);
        [DllImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] static extern bool PostThreadMessage(uint thread, uint message, UIntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll")] static extern uint GetCurrentProcessId();
    }
}
