using System;
using System.Runtime.InteropServices;

namespace Tk75.App
{
    // Resolves the main shortcut key through the active Windows keyboard layout.
    // This uses the drawn keyboard's known default scan positions; it does not
    // infer Fn keys or inspect a custom firmware remapping layer.
    public static class HotkeyPhysicalKeyResolver
    {
        static readonly object cacheLock = new object();
        static KeyboardLayout cachedLayout;
        static IntPtr cachedWindowsLayout;
        static int cachedVirtualKey;
        static int? cachedIndex;

        public static int? ResolveIndex(KeyboardLayout layout, int virtualKey)
        {
            if (layout == null || !IsPrimaryVirtualKey(virtualKey)) return null;
            IntPtr windowsLayout = ActiveWindowsLayout();
            if (windowsLayout == IntPtr.Zero) return null;
            lock (cacheLock)
            {
                if (Object.ReferenceEquals(cachedLayout, layout) && cachedVirtualKey == virtualKey &&
                    cachedWindowsLayout == windowsLayout) return cachedIndex;
                uint mappedScan = MapVirtualKeyExW((uint)virtualKey, 4, windowsLayout);
                cachedIndex = ResolveMappedScan(layout, virtualKey, mappedScan);
                cachedLayout = layout; cachedVirtualKey = virtualKey; cachedWindowsLayout = windowsLayout;
                return cachedIndex;
            }
        }

        // Kept pure so physical positions, extended keys and unmapped cases can
        // be checked without invoking Windows APIs or installing an input hook.
        internal static int? ResolveMappedScan(KeyboardLayout layout, int virtualKey, uint mappedScan)
        {
            if (!IsPrimaryVirtualKey(virtualKey)) return null;
            uint prefix = mappedScan & 0xffffff00U;
            int scan = (int)(mappedScan & 0xffU);
            // E1 sequences (for example Pause) are not represented by the
            // scan+extended model. Never truncate them into an unrelated key.
            if (scan == 0 || (prefix != 0 && prefix != 0xe000U)) return null;
            return FindIndex(layout, new SuppressionKey(scan, prefix == 0xe000U));
        }

        public static int? FindIndex(KeyboardLayout layout, SuppressionKey physicalKey)
        {
            if (layout == null || physicalKey.ScanCode < 1 || physicalKey.ScanCode > 255 ||
                IsModifierScan(physicalKey)) return null;
            bool found = false; int? result = null;
            foreach (KeyboardKeyDefinition definition in layout.Keys)
            {
                SuppressionKey candidate;
                if (!KeyboardScanCodes.TryGet(definition.Code, out candidate) || !candidate.Equals(physicalKey)) continue;
                if (found || definition.IsKnob || !definition.KeyIndex.HasValue) return null;
                found = true; result = definition.KeyIndex;
            }
            return result;
        }

        static bool IsPrimaryVirtualKey(int virtualKey)
        {
            return virtualKey > 0 && virtualKey < 255 && virtualKey != 0x10 && virtualKey != 0x11 &&
                virtualKey != 0x12 && virtualKey != 0x5b && virtualKey != 0x5c &&
                (virtualKey < 0xa0 || virtualKey > 0xa5);
        }

        static bool IsModifierScan(SuppressionKey key)
        {
            return key.ScanCode == 0x2a || key.ScanCode == 0x36 || key.ScanCode == 0x1d || key.ScanCode == 0x38 ||
                (key.Extended && (key.ScanCode == 0x5b || key.ScanCode == 0x5c));
        }

        static IntPtr ActiveWindowsLayout()
        {
            IntPtr foreground = GetForegroundWindow();
            uint processId;
            uint thread = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out processId);
            IntPtr layout = GetKeyboardLayout(thread);
            return layout == IntPtr.Zero && thread != 0 ? GetKeyboardLayout(0) : layout;
        }

        [DllImport("user32.dll", ExactSpelling = true)]
        static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", ExactSpelling = true)]
        static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll", ExactSpelling = true)]
        static extern IntPtr GetKeyboardLayout(uint threadId);
        [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        static extern uint MapVirtualKeyExW(uint virtualKey, uint mapType, IntPtr keyboardLayout);
    }
}
