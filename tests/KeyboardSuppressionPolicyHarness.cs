using System;
using System.Collections.Generic;
using Tk75.App;

public static class KeyboardSuppressionPolicyHarness
{
    static int checks;
    static readonly SuppressionKey W = new SuppressionKey(0x11, false);
    static readonly SuppressionKey A = new SuppressionKey(0x1e, false);
    static void Check(bool condition, string message)
    { checks++; if (!condition) throw new Exception(message); }
    static void Throws<T>(Action action, string message) where T : Exception
    { bool caught = false; try { action(); } catch (T) { caught = true; } Check(caught, message); }
    static bool Event(KeyboardSuppressionPolicy policy, SuppressionKey key, int vk, bool down, long time, bool injected, bool ownForeground)
    { return policy.ShouldSuppress(key.ScanCode, key.Extended, vk, down, injected, time, ownForeground); }
    static bool WEvent(KeyboardSuppressionPolicy policy, bool down, long time)
    { return Event(policy, W, 0x57, down, time, false, false); }
    static KeyboardSuppressionPolicy Active(long time)
    { var policy = new KeyboardSuppressionPolicy(); policy.PublishEligibility(new[] { W, A }, true, false, time); return policy; }
    static void Scan(string name, int expected, bool extended)
    {
        SuppressionKey key; Check(KeyboardScanCodes.TryGet(name, out key), "scan present: " + name);
        Check(key.ScanCode == expected && key.Extended == extended, "scan value: " + name);
    }
    static void ShiftChecks()
    {
        var left = new SuppressionKey(0x2a, false);
        var right = new SuppressionKey(0x36, false);
        foreach (string name in new[] { "ShiftLeft", "ShiftRight" })
        { SuppressionKey key; Check(KeyboardScanCodes.TryGet(name, out key) && KeyboardSuppressionPolicy.CanSuppress(key), "Shift is selectable: " + name); }
        foreach (SuppressionKey side in new[] { left, right }) foreach (int vk in new[] { 0x10, side.ScanCode == 0x2a ? 0xa0 : 0xa1 })
        {
            var policy = new KeyboardSuppressionPolicy(); policy.PublishEligibility(new[] { side }, true, false, 0);
            Check(Event(policy, side, vk, true, 1, false, false), "generic and side-specific Shift down suppress");
            Check(!Event(policy, side, vk, false, 2, true, false), "injected Shift up does not clear physical ownership");
            Check(Event(policy, side, vk, true, 300, false, true), "held Shift repeat preserves ownership despite expired lease and focus");
            Check(Event(policy, side, vk, false, 301, false, true), "held Shift release preserves ownership");
            Check(!Event(policy, side, vk, true, 302, false, false), "fresh Shift after lease expiry passes");
        }
        var independent = new KeyboardSuppressionPolicy(); independent.PublishEligibility(new[] { left, right }, true, false, 0);
        Check(Event(independent, left, 0x10, true, 1, false, false) && Event(independent, right, 0x10, true, 2, false, false), "both generic Shift sides tracked independently");
        Check(Event(independent, left, 0xa0, false, 3, false, false), "generic down and side-specific up share left ownership");
        Check(Event(independent, right, 0xa1, true, 4, false, false) && Event(independent, right, 0x10, false, 5, false, false), "left release leaves right held");

        for (int modeMods = 0; modeMods <= 15; modeMods++) for (int stopMods = 0; stopMods <= 15; stopMods++)
        foreach (bool modeEnabled in new[] { false, true }) foreach (bool stopEnabled in new[] { false, true })
        {
            var policy = new KeyboardSuppressionPolicy();
            policy.SetProtectedHotkeys(0x4d, modeMods, modeEnabled, 0x4b, stopMods, stopEnabled);
            policy.PublishEligibility(new[] { left, right }, true, false, 0);
            bool suppress = !(modeEnabled && (modeMods & 4) != 0 || stopEnabled && (stopMods & 4) != 0);
            Check(Event(policy, left, 0x10, true, 1, false, false) == suppress && Event(policy, left, 0xa0, false, 2, false, false) == suppress, "mode/stop Shift modifier protection truth table left");
            Check(Event(policy, right, 0xa1, true, 3, false, false) == suppress && Event(policy, right, 0x10, false, 4, false, false) == suppress, "mode/stop Shift modifier protection truth table right");
        }
        var changed = new KeyboardSuppressionPolicy(); changed.PublishEligibility(new[] { left }, true, false, 0);
        Check(Event(changed, left, 0x10, true, 1, false, false), "Shift initially suppressed");
        changed.SetProtectedHotkeys(0x4d, 4, true, 0x4b, 0, false);
        Check(Event(changed, left, 0xa0, true, 2, false, false) && Event(changed, left, 0x10, false, 3, false, false), "adding Shift shortcut while held preserves suppressed pair");
        Check(!Event(changed, left, 0x10, true, 4, false, false), "new Shift press passes for registered shortcut");
        changed.SetProtectedHotkeys(0x4d, 4, false, 0x4b, 0, false);
        Check(!Event(changed, left, 0xa0, true, 5, false, false) && !Event(changed, left, 0x10, false, 6, false, false), "disabling Shift shortcut while held preserves forwarded pair");
        Check(Event(changed, left, 0x10, true, 7, false, false) && Event(changed, left, 0xa0, false, 8, false, false), "fresh Shift suppresses when shortcut disabled");

        var seeded = new KeyboardSuppressionPolicy(); seeded.PublishEligibility(new[] { left, right }, true, false, 0);
        seeded.SeedHeldVirtualKeys(new[] { 0x10, 0xa0 });
        Check(!Event(seeded, left, 0x10, true, 1, false, false), "held left Shift at installation passes even generic event");
        Check(Event(seeded, right, 0x10, true, 2, false, false), "left Shift initial seed does not exempt fresh right Shift");
        Check(!Event(seeded, left, 0xa0, false, 3, false, false) && Event(seeded, right, 0xa1, false, 4, false, false), "seeded and fresh sides finish independently");
        Check(Event(seeded, left, 0x10, true, 5, false, false) && Event(seeded, left, 0x10, false, 6, false, false), "released initial Shift becomes eligible");
        var genericSeed = new KeyboardSuppressionPolicy(); genericSeed.PublishEligibility(new[] { left }, true, false, 0); genericSeed.SeedHeldVirtualKeys(new[] { 0x10 });
        Check(!Event(genericSeed, left, 0xa0, true, 1, false, false) && !Event(genericSeed, left, 0x10, false, 2, false, false), "generic-only seed conservatively passes until release");
        Check(Event(genericSeed, left, 0x10, true, 3, false, false) && Event(genericSeed, left, 0xa0, false, 4, false, false), "generic-only seed clears on release");
        var focus = new KeyboardSuppressionPolicy(); focus.PublishEligibility(new[] { left }, true, false, 0);
        Check(!Event(focus, left, 0x10, true, 1, false, true) && !Event(focus, left, 0xa0, false, 2, false, false), "Shift typing in mapper keeps forwarded pair");
        foreach (int vk in new[] { 0x10, 0xa0, 0xa1 })
            Throws<ArgumentOutOfRangeException>(() => new KeyboardSuppressionPolicy().SetProtectedHotkey(vk, 0, true), "Shift alone remains invalid as shortcut primary key");
    }
    public static string Run()
    {
        checks = 0;
        var off = new KeyboardSuppressionPolicy();
        Check(!WEvent(off, true, 0) && !WEvent(off, false, 1), "default off");
        foreach (bool active in new[] { false, true }) foreach (bool suspended in new[] { false, true })
        {
            var policy = new KeyboardSuppressionPolicy(); policy.PublishEligibility(new[] { W }, active, suspended, 10);
            Check(WEvent(policy, true, 10) == (active && !suspended), "controller and suspension gate");
            Check(WEvent(policy, false, 11) == (active && !suspended), "matching gated release");
        }
        foreach (long time in new long[] { -1, 99, 100, 101, 349, 350, 351, Int64.MaxValue })
        {
            var policy = Active(100); bool eligible = time >= 100 && time < 350;
            Check(WEvent(policy, true, time) == eligible, "exact lease boundary " + time);
            Check(WEvent(policy, false, time) == eligible, "lease paired release " + time);
        }
        var maxClock = Active(Int64.MaxValue - 100);
        Check(WEvent(maxClock, true, Int64.MaxValue), "monotonic clock near max has no deadline overflow");
        Check(WEvent(maxClock, false, Int64.MaxValue), "release near max");

        var pairing = Active(0);
        Check(WEvent(pairing, true, 1), "initial suppression");
        pairing.PublishEligibility(new SuppressionKey[0], false, true, 2);
        Check(Event(pairing, W, 0x57, true, 1000, false, true), "suppressed repeat completes despite inactive/focus/expiry");
        Check(Event(pairing, W, 0x57, false, 1001, false, true), "suppressed up completes while suspended");
        Check(!WEvent(pairing, true, 1002) && !WEvent(pairing, false, 1003), "future down forwarded after deactivation");
        pairing.PublishEligibility(new[] { W }, true, false, 1004);
        Check(WEvent(pairing, true, 1004) && WEvent(pairing, false, 1005), "fresh press after reactivation");

        var forwarded = new KeyboardSuppressionPolicy();
        Check(!WEvent(forwarded, true, 0), "down before activation forwarded");
        forwarded.PublishEligibility(new[] { W }, true, false, 1);
        Check(!WEvent(forwarded, true, 2) && !WEvent(forwarded, false, 3), "forwarded repeat/up never swallowed");
        Check(WEvent(forwarded, true, 4) && WEvent(forwarded, false, 5), "next fresh down now eligible");
        var seeded = Active(0); seeded.SeedHeldVirtualKeys(new[] { 0x57, 0x41 });
        Check(!WEvent(seeded, true, 1) && !WEvent(seeded, true, 2) && !WEvent(seeded, false, 3), "held on installation forwarded through release");
        Check(WEvent(seeded, true, 4) && WEvent(seeded, false, 5), "seed cleared on release");
        Check(!Event(seeded, A, 0x41, false, 6, false, false), "seeded unobserved down release forwards");
        Check(Event(seeded, A, 0x41, true, 7, false, false) && Event(seeded, A, 0x41, false, 8, false, false), "up alone clears seed");

        var injected = Active(0);
        Check(!Event(injected, W, 0x57, true, 0, true, false), "injected down passes");
        Check(WEvent(injected, true, 1), "injected down did not seed a physical down");
        Check(!Event(injected, W, 0x57, false, 2, true, false), "injected up passes");
        Check(WEvent(injected, true, 3) && WEvent(injected, false, 4), "injected up did not clear physical pair");
        injected.SeedHeldVirtualKeys(new[] { 0x57 });
        Check(!Event(injected, W, 0x57, false, 5, true, false) && !WEvent(injected, true, 6), "injected release cannot clear physical held seed");
        Check(!WEvent(injected, false, 7), "seeded physical up stays forwarded");

        var focus = Active(0);
        Check(!Event(focus, W, 0x57, true, 1, false, true), "own process foreground passes");
        Check(!WEvent(focus, true, 2) && !WEvent(focus, false, 3), "foreground change preserves forwarded pair");
        Check(WEvent(focus, true, 4), "outside foreground fresh press suppresses");
        Check(Event(focus, W, 0x57, false, 5, false, true), "own foreground still completes swallowed pair");

        var renewed = Active(0);
        Check(!WEvent(renewed, true, 250), "expired down passes");
        renewed.PublishEligibility(new[] { W }, true, false, 251);
        Check(!WEvent(renewed, true, 252) && !WEvent(renewed, false, 253), "lease renewal does not steal an existing down");
        Check(WEvent(renewed, true, 254) && WEvent(renewed, false, 255), "renewal affects next pair");
        var selection = Active(0);
        Check(WEvent(selection, true, 0), "selected W suppressed");
        selection.PublishEligibility(new[] { A }, true, false, 1);
        Check(WEvent(selection, false, 2), "deselected held W up still suppressed");
        Check(!WEvent(selection, true, 3) && !WEvent(selection, false, 4), "deselected W future pair passes");
        Check(Event(selection, A, 0x41, true, 5, false, false) && Event(selection, A, 0x41, false, 6, false, false), "different assigned key remains active");
        var mutable = new List<SuppressionKey> { W };
        var detached = new KeyboardSuppressionPolicy(); detached.PublishEligibility(mutable, true, false, 0);
        mutable.Clear(); mutable.Add(A);
        Check(WEvent(detached, true, 1) && WEvent(detached, false, 2), "published selection detached from caller mutations");
        Check(!Event(detached, A, 0x41, true, 3, false, false), "caller mutation cannot add a key");
        var extended = Active(0);
        Check(!Event(extended, new SuppressionKey(0x11, true), 0x57, true, 0, false, false), "extended scan distinct");
        Check(WEvent(extended, true, 1) && WEvent(extended, false, 2), "extended down does not alter base scan pair");

        int[] reservedScans = { 0x1d, 0x38 };
        foreach (int scan in reservedScans) foreach (bool ext in new[] { false, true })
        {
            var key = new SuppressionKey(scan, ext); Check(!KeyboardSuppressionPolicy.CanSuppress(key), "reserved scan not selectable");
            var policy = new KeyboardSuppressionPolicy(); policy.PublishEligibility(new[] { key }, true, false, 0);
            Check(!Event(policy, key, 0x57, true, 0, false, false) && !Event(policy, key, 0x57, false, 1, false, false), "reserved scan always passes");
        }
        foreach (int vk in new[] { 0x77, 0x78, 0x11, 0x12, 0xa2, 0xa3, 0xa4, 0xa5, 0x5b, 0x5c })
        {
            var policy = Active(0); Check(!Event(policy, W, vk, true, 0, false, false) && !Event(policy, W, vk, false, 1, false, false), "reserved VK passes even remapped scan " + vk);
        }
        foreach (string name in new[] { "ControlLeft", "ControlRight", "AltLeft", "AltRight", "MetaLeft", "MetaRight" })
        {
            SuppressionKey key; Check(KeyboardScanCodes.TryGet(name, out key) && !KeyboardSuppressionPolicy.CanSuppress(key), "scan map reserved " + name);
        }
        Scan("KeyW", 0x11, false); Scan("KeyA", 0x1e, false); Scan("KeyS", 0x1f, false); Scan("KeyD", 0x20, false);
        Scan("IntlBackslash", 0x56, false); Scan("AltRight", 0x38, true); Scan("F8", 0x42, false); Scan("F9", 0x43, false);
        SuppressionKey unknown; Check(!KeyboardScanCodes.TryGet("Fn", out unknown) && !KeyboardSuppressionPolicy.CanSuppress(unknown), "unknown Fn not invented");
        Check(!KeyboardScanCodes.TryGet(null, out unknown), "null physical code not invented");

        foreach (int scan in new[] { -1, 0, 256, Int32.MaxValue })
        {
            Check(!Active(0).ShouldSuppress(scan, false, 0x57, true, false, 0, false), "invalid event scan passes");
            Throws<ArgumentOutOfRangeException>(() => new SuppressionKey(scan, false), "invalid constructor scan rejected");
        }
        foreach (int vk in new[] { -1, 0, 256, Int32.MaxValue })
            Check(!Active(0).ShouldSuppress(0x11, false, vk, true, false, 0, false), "invalid virtual key passes");
        Throws<ArgumentNullException>(() => Active(0).PublishEligibility(null, true, false, 0), "null selection rejected");
        Throws<ArgumentException>(() => Active(0).PublishEligibility(new[] { new SuppressionKey() }, true, false, 0), "default struct selection rejected");
        Throws<ArgumentOutOfRangeException>(() => Active(0).PublishEligibility(new[] { W }, true, false, -1), "negative publication rejected");
        Throws<ArgumentNullException>(() => Active(0).SeedHeldVirtualKeys(null), "null seed rejected");
        Throws<ArgumentOutOfRangeException>(() => Active(0).SeedHeldVirtualKeys(new[] { 256 }), "invalid seed rejected");
        var many = new List<SuppressionKey>(); for (int i = 0; i < 4097; i++) many.Add(W);
        Throws<ArgumentException>(() => Active(0).PublishEligibility(many, true, false, 0), "unbounded selection rejected");
        var duplicates = new KeyboardSuppressionPolicy(); duplicates.PublishEligibility(new[] { W, W, W }, true, false, 0);
        Check(WEvent(duplicates, true, 0) && WEvent(duplicates, false, 1), "duplicate selection has one physical pair");

        var modeKey = new SuppressionKey(0x32, false); // M
        var f9 = new SuppressionKey(0x43, false);
        Check(KeyboardSuppressionPolicy.CanSuppress(f9), "F9 is only dynamically reserved by the current shortcut");
        var defaultMode = new KeyboardSuppressionPolicy(); defaultMode.PublishEligibility(new[] { f9 }, true, false, 0);
        Check(!defaultMode.ShouldSuppress(0x43, false, 0x78, true, false, 0, false, 0) &&
            !defaultMode.ShouldSuppress(0x43, false, 0x78, false, false, 1, false, 0), "default plain F9 shortcut passes");
        for (int desired = 0; desired <= 15; desired++) for (int pressed = 0; pressed <= 15; pressed++)
        {
            var mode = new KeyboardSuppressionPolicy(); mode.SetProtectedHotkey(0x4d, desired, true);
            mode.PublishEligibility(new[] { modeKey }, true, false, 0);
            bool blocked = desired != pressed;
            Check(mode.ShouldSuppress(0x32, false, 0x4d, true, false, 1, false, pressed) == blocked, "only exact modifier shortcut passes");
            Check(mode.ShouldSuppress(0x32, false, 0x4d, false, false, 2, false, pressed ^ 15) == blocked, "shortcut release keeps pair despite modifier changes");
        }
        var ctrlAltM = new KeyboardSuppressionPolicy(); ctrlAltM.SetProtectedHotkey(0x4d, 3, true);
        ctrlAltM.PublishEligibility(new[] { modeKey, f9 }, true, false, 0);
        Check(ctrlAltM.ShouldSuppress(0x32, false, 0x4d, true, false, 1, false, 0), "plain M remains suppressible with CtrlAltM shortcut");
        Check(ctrlAltM.ShouldSuppress(0x32, false, 0x4d, true, false, 2, false, 3), "adding modifiers mid-held M preserves blocked repeat");
        Check(ctrlAltM.ShouldSuppress(0x32, false, 0x4d, false, false, 3, false, 3), "blocked held M up is not orphaned");
        Check(!ctrlAltM.ShouldSuppress(0x32, false, 0x4d, true, false, 4, false, 3), "fresh CtrlAltM down passes");
        ctrlAltM.SetProtectedHotkey(0x57, 4, true);
        Check(!ctrlAltM.ShouldSuppress(0x32, false, 0x4d, true, false, 5, false, 0) &&
            !ctrlAltM.ShouldSuppress(0x32, false, 0x4d, false, false, 6, false, 0), "reconfigure held forwarded shortcut never swallows its repeat/up");
        Check(ctrlAltM.ShouldSuppress(0x32, false, 0x4d, true, false, 7, false, 3), "previous shortcut becomes ordinary on next fresh down");
        ctrlAltM.SetProtectedHotkey(0x4d, 3, true);
        Check(ctrlAltM.ShouldSuppress(0x32, false, 0x4d, true, false, 8, false, 3) &&
            ctrlAltM.ShouldSuppress(0x32, false, 0x4d, false, false, 9, false, 3), "reconfigure to held blocked key does not split physical pair");
        Check(ctrlAltM.ShouldSuppress(0x43, false, 0x78, true, false, 10, false, 0) &&
            ctrlAltM.ShouldSuppress(0x43, false, 0x78, false, false, 11, false, 0), "F9 available as mapping after custom shortcut");
        ctrlAltM.SetProtectedHotkey(0x4d, 3, false);
        Check(ctrlAltM.ShouldSuppress(0x32, false, 0x4d, true, false, 12, false, 3), "disabled shortcut has no exemption");
        Check(!ctrlAltM.ShouldSuppress(0x32, false, 0x4d, false, true, 13, false, 3), "injected shortcut key-up still passes");
        Check(ctrlAltM.ShouldSuppress(0x32, false, 0x4d, false, false, 14, false, 3), "injected shortcut up cannot clear physical pair");
        var noRenewal = Active(0); noRenewal.SetProtectedHotkey(0x4d, 3, true);
        Check(!WEvent(noRenewal, true, 250), "hotkey reconfiguration does not renew controller lease");
        Throws<ArgumentException>(() => Active(0).SetProtectedHotkey(0x77, 0, true), "mode cannot duplicate the default active emergency shortcut");
        Throws<ArgumentOutOfRangeException>(() => Active(0).SetProtectedHotkey(0x11, 0, true), "modifier alone cannot be mode shortcut");
        Throws<ArgumentOutOfRangeException>(() => Active(0).SetProtectedHotkey(0, 0, false), "invalid shortcut key rejected even disabled");
        Throws<ArgumentOutOfRangeException>(() => Active(0).SetProtectedHotkey(0x4d, 16, true), "unsupported shortcut modifier flags rejected");
        Throws<ArgumentOutOfRangeException>(() => Active(0).SetProtectedHotkey(0x4d, -1, true), "negative shortcut modifier flags rejected");
        var f8 = new SuppressionKey(0x42, false);
        Check(KeyboardSuppressionPolicy.CanSuppress(f8), "F8 has no permanent reservation");
        var bothOff = new KeyboardSuppressionPolicy(); bothOff.SetProtectedHotkeys(0x78, 0, false, 0x77, 0, false);
        bothOff.PublishEligibility(new[] { f8, f9 }, true, false, 0);
        Check(Event(bothOff, f8, 0x77, true, 1, false, false) && Event(bothOff, f8, 0x77, false, 2, false, false), "F8 mapping works when emergency shortcut disabled");
        Check(Event(bothOff, f9, 0x78, true, 3, false, false) && Event(bothOff, f9, 0x78, false, 4, false, false), "F9 mapping works when mode shortcut disabled");
        var customStop = new KeyboardSuppressionPolicy(); customStop.SetProtectedHotkeys(0x4d, 3, true, 0x57, 8, true);
        customStop.PublishEligibility(new[] { W, modeKey }, true, false, 0);
        Check(customStop.ShouldSuppress(0x11, false, 0x57, true, false, 0, false, 0) && customStop.ShouldSuppress(0x11, false, 0x57, false, false, 1, false, 8), "plain W remains suppressible despite WinW emergency shortcut");
        Check(!customStop.ShouldSuppress(0x11, false, 0x57, true, false, 2, false, 8), "fresh WinW emergency shortcut passes");
        customStop.SetProtectedHotkeys(0x78, 0, false, 0x77, 0, false);
        Check(!customStop.ShouldSuppress(0x11, false, 0x57, true, false, 3, false, 0) && !customStop.ShouldSuppress(0x11, false, 0x57, false, false, 4, false, 0), "disabling held emergency shortcut preserves its forwarded pair");
        Check(customStop.ShouldSuppress(0x11, false, 0x57, true, false, 5, false, 8), "disabled emergency shortcut is suppressible on fresh press");
        Check(!customStop.ShouldSuppress(0x11, false, 0x57, false, true, 6, false, 8) && customStop.ShouldSuppress(0x11, false, 0x57, false, false, 7, false, 8), "injected emergency up does not clear suppressed physical pair");
        Throws<ArgumentException>(() => Active(0).SetProtectedHotkeys(0x4d, 3, true, 0x4d, 3, true), "same enabled combination rejected");
        bothOff.SetProtectedHotkeys(0x4d, 3, false, 0x4d, 3, false);
        Check(!WEvent(bothOff, true, 500), "both identical disabled shortcuts accepted without extending eligibility");

        ShiftChecks();

        // Exhaust every valid scan in both namespaces: eligible pairs must be
        // independent, reserved keys must pass, and releases consume once only.
        for (int scan = 1; scan <= 255; scan++) foreach (bool ext in new[] { false, true })
        {
            var key = new SuppressionKey(scan, ext); var policy = new KeyboardSuppressionPolicy();
            policy.PublishEligibility(new[] { key }, true, false, 0);
            bool allowed = KeyboardSuppressionPolicy.CanSuppress(key);
            Check(Event(policy, key, 0x57, true, 0, false, false) == allowed, "all scan down");
            Check(Event(policy, key, 0x57, true, 1, false, false) == allowed, "all scan repeat");
            Check(Event(policy, key, 0x57, false, 1000, false, false) == allowed, "all scan late paired up");
            Check(!Event(policy, key, 0x57, false, 1001, false, false), "all scan duplicate up passes");
            Check(!Event(policy, key, 0x57, true, 1002, false, false), "all scan expired next down passes");
        }
        return "PASS: " + checks + " pure keyboard suppression checks; native hook service not loaded or started.";
    }
}
