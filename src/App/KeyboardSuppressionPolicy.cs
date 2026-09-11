using System;
using System.Collections.Generic;
using System.Threading;

namespace Tk75.App
{
    public struct SuppressionKey : IEquatable<SuppressionKey>
    {
        public readonly int ScanCode;
        public readonly bool Extended;
        public SuppressionKey(int scanCode, bool extended)
        {
            if (scanCode < 1 || scanCode > 255) throw new ArgumentOutOfRangeException("scanCode");
            ScanCode = scanCode; Extended = extended;
        }
        public bool Equals(SuppressionKey other) { return ScanCode == other.ScanCode && Extended == other.Extended; }
        public override bool Equals(object value) { return value is SuppressionKey && Equals((SuppressionKey)value); }
        public override int GetHashCode() { return ScanCode | (Extended ? 256 : 0); }
    }

    // Pure transition policy. PublishEligibility may run on the UI thread; only
    // the hook thread calls SeedHeldVirtualKeys/ShouldSuppress. No native calls,
    // callbacks, locks, key logging or synthetic input are used by this policy.
    public sealed class KeyboardSuppressionPolicy
    {
        public const int LeaseMilliseconds = 250;
        sealed class ProtectedHotkey
        {
            internal readonly int KeyCode, Modifiers;
            internal readonly bool Enabled;
            internal ProtectedHotkey(int keyCode, int modifiers, bool enabled)
            { KeyCode = keyCode; Modifiers = modifiers; Enabled = enabled; }
        }
        sealed class Eligibility
        {
            internal readonly bool[] Keys;
            internal readonly bool Active;
            internal readonly long RenewedAt;
            internal readonly ProtectedHotkey Hotkey, StopHotkey;
            internal Eligibility(bool[] keys, bool active, long renewedAt, ProtectedHotkey hotkey, ProtectedHotkey stopHotkey)
            { Keys = keys; Active = active; RenewedAt = renewedAt; Hotkey = hotkey; StopHotkey = stopHotkey; }
        }
        Eligibility eligibility = new Eligibility(new bool[512], false, 0, new ProtectedHotkey(0x78, 0, true), new ProtectedHotkey(0x77, 0, true));
        // 0: no observed down; 1: forwarded down; 2: suppressed down.
        readonly byte[] presses = new byte[512];
        readonly bool[] heldAtInstallation = new bool[256];

        public static bool CanSuppress(SuppressionKey key)
        {
            int scan = key.ScanCode;
            if (scan < 1 || scan > 255) return false;
            // Ctrl/Alt/Windows always pass. Configured mode/emergency shortcuts are
            // handled separately, including their exact modifiers. F8 and F9
            // have no fixed reservation when those shortcuts are disabled.
            // This also avoids suppressing the physical half of AltGr's pair.
            return scan != 0x1d && scan != 0x38 && !(key.Extended && (scan == 0x5b || scan == 0x5c));
        }

        public void PublishEligibility(IEnumerable<SuppressionKey> keys, bool controllerActive, bool suspended, long nowMilliseconds)
        {
            if (keys == null) throw new ArgumentNullException("keys");
            if (nowMilliseconds < 0) throw new ArgumentOutOfRangeException("nowMilliseconds");
            var selected = new bool[512]; int count = 0;
            foreach (SuppressionKey key in keys)
            {
                if (++count > 4096) throw new ArgumentException("Too many suppression entries.", "keys");
                if (key.ScanCode < 1 || key.ScanCode > 255) throw new ArgumentException("Invalid scan code.", "keys");
                if (CanSuppress(key)) selected[key.GetHashCode()] = true;
            }
            while (true)
            {
                Eligibility previous = Volatile.Read(ref eligibility);
                var next = new Eligibility(selected, controllerActive && !suspended, nowMilliseconds, previous.Hotkey, previous.StopHotkey);
                if (Object.ReferenceEquals(Interlocked.CompareExchange(ref eligibility, next, previous), previous)) break;
            }
        }

        public void SetProtectedHotkey(int keyCode, int modifiers, bool enabled)
        {
            ValidateProtectedHotkey(keyCode, modifiers);
            var hotkey = new ProtectedHotkey(keyCode, modifiers, enabled);
            while (true)
            {
                Eligibility previous = Volatile.Read(ref eligibility);
                ValidateProtectedHotkeys(keyCode, modifiers, enabled, previous.StopHotkey.KeyCode, previous.StopHotkey.Modifiers, previous.StopHotkey.Enabled);
                var next = new Eligibility(previous.Keys, previous.Active, previous.RenewedAt, hotkey, previous.StopHotkey);
                if (Object.ReferenceEquals(Interlocked.CompareExchange(ref eligibility, next, previous), previous)) break;
            }
        }
        public void SetProtectedHotkeys(int modeKey, int modeModifiers, bool modeEnabled, int stopKey, int stopModifiers, bool stopEnabled)
        {
            ValidateProtectedHotkeys(modeKey, modeModifiers, modeEnabled, stopKey, stopModifiers, stopEnabled);
            var mode = new ProtectedHotkey(modeKey, modeModifiers, modeEnabled);
            var stop = new ProtectedHotkey(stopKey, stopModifiers, stopEnabled);
            while (true)
            {
                Eligibility previous = Volatile.Read(ref eligibility);
                var next = new Eligibility(previous.Keys, previous.Active, previous.RenewedAt, mode, stop);
                if (Object.ReferenceEquals(Interlocked.CompareExchange(ref eligibility, next, previous), previous)) break;
            }
        }
        public static void ValidateProtectedHotkeys(int modeKey, int modeModifiers, bool modeEnabled, int stopKey, int stopModifiers, bool stopEnabled)
        {
            ValidateProtectedHotkey(modeKey, modeModifiers); ValidateProtectedHotkey(stopKey, stopModifiers);
            if (modeEnabled && stopEnabled && modeKey == stopKey && modeModifiers == stopModifiers)
                throw new ArgumentException("Enabled mode and emergency shortcuts must differ.");
        }
        public static void ValidateProtectedHotkey(int keyCode, int modifiers)
        {
            if (keyCode < 1 || keyCode > 255 || ReservedVirtualKey(keyCode) || IsShiftVirtualKey(keyCode)) throw new ArgumentOutOfRangeException("keyCode");
            if (modifiers < 0 || modifiers > 15) throw new ArgumentOutOfRangeException("modifiers");
        }

        public void SeedHeldVirtualKeys(IEnumerable<int> virtualKeys)
        {
            if (virtualKeys == null) throw new ArgumentNullException("virtualKeys");
            foreach (int key in virtualKeys)
            {
                if (key < 1 || key > 255) throw new ArgumentOutOfRangeException("virtualKeys");
                heldAtInstallation[key] = true;
            }
            // Windows reports both the generic Shift group and its held side.
            // Prefer the side information so holding left Shift at installation
            // does not exempt a later fresh right-Shift press as well.
            if (heldAtInstallation[0xa0] || heldAtInstallation[0xa1]) heldAtInstallation[0x10] = false;
        }

        static bool ReservedVirtualKey(int key)
        {
            return key == 0x11 || key == 0x12 ||
                (key >= 0xa2 && key <= 0xa5) || key == 0x5b || key == 0x5c;
        }
        static bool IsShiftVirtualKey(int key) { return key == 0x10 || key == 0xa0 || key == 0xa1; }
        static bool NeedsShift(ProtectedHotkey hotkey) { return hotkey.Enabled && (hotkey.Modifiers & 4) != 0; }

        public bool ShouldSuppress(int scanCode, bool extended, int virtualKey, bool isDown,
            bool injected, long nowMilliseconds, bool foregroundSuspended)
        { return ShouldSuppress(scanCode, extended, virtualKey, isDown, injected, nowMilliseconds, foregroundSuspended, 0); }

        public bool ShouldSuppress(int scanCode, bool extended, int virtualKey, bool isDown,
            bool injected, long nowMilliseconds, bool foregroundSuspended, int currentModifiers)
        {
            // Injected ups must not consume/reset a held physical press.
            if (injected || scanCode < 1 || scanCode > 255 || virtualKey < 1 || virtualKey > 255) return false;
            if (virtualKey == 0x10 && !extended)
            {
                if (scanCode == 0x2a) virtualKey = 0xa0;
                else if (scanCode == 0x36) virtualKey = 0xa1;
            }
            int index = scanCode | (extended ? 256 : 0);
            byte previous = presses[index];
            bool reserved = ReservedVirtualKey(virtualKey) || !CanSuppress(new SuppressionKey(scanCode, extended));
            if (!isDown)
            {
                heldAtInstallation[virtualKey] = false; presses[index] = 0;
                if (IsShiftVirtualKey(virtualKey) && !heldAtInstallation[0xa0] && !heldAtInstallation[0xa1]) heldAtInstallation[0x10] = false;
                return !reserved && previous == 2;
            }
            if (reserved) { presses[index] = 1; return false; }
            // Finish observed physical pairs even if selection/focus/lease
            // changes. Explicit service disable instead removes the hook.
            if (previous != 0) return previous == 2;
            if (heldAtInstallation[virtualKey] || (IsShiftVirtualKey(virtualKey) && heldAtInstallation[0x10])) { presses[index] = 1; return false; }
            Eligibility current = Volatile.Read(ref eligibility);
            // RegisterHotKey requires its Shift modifier to reach Windows. Only
            // new presses use this exception; an existing down/up pair remains
            // owned even if the user changes shortcuts while Shift is held.
            if (IsShiftVirtualKey(virtualKey) && (NeedsShift(current.Hotkey) || NeedsShift(current.StopHotkey)))
            { presses[index] = 1; return false; }
            if (currentModifiers < 0 || currentModifiers > 15 || (current.Hotkey.Enabled &&
                virtualKey == current.Hotkey.KeyCode && currentModifiers == current.Hotkey.Modifiers) ||
                (current.StopHotkey.Enabled && virtualKey == current.StopHotkey.KeyCode && currentModifiers == current.StopHotkey.Modifiers))
            { presses[index] = 1; return false; }
            bool suppress = current.Active && !foregroundSuspended && nowMilliseconds >= current.RenewedAt &&
                nowMilliseconds - current.RenewedAt < LeaseMilliseconds && current.Keys[index];
            presses[index] = suppress ? (byte)2 : (byte)1;
            return suppress;
        }
    }
}
