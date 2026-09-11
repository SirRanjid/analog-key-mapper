using System;
using System.Drawing;
using Tk75.App;

public static class HotkeyPhysicalKeyResolverHarness
{
    static int checks;
    static void Check(bool condition, string message)
    { checks++; if (!condition) throw new Exception(message); }
    static int? Map(KeyboardLayout layout, int virtualKey, uint scan)
    { return HotkeyPhysicalKeyResolver.ResolveMappedScan(layout, virtualKey, scan); }

    public static string Run()
    {
        KeyboardLayout iso = KeyboardLayout.Tk75Iso(), ansi = KeyboardLayout.Tk75Ansi();
        foreach (KeyboardLayout layout in new[] { iso, ansi })
        {
            Check(Map(layout, 0x78, 0x43) == 54, "F9 uses physical index 54");
            Check(Map(layout, 0x20, 0x39) == 41, "Space uses physical index 41");
            // Supply the results that Windows produces for US and German HKLs.
            Check(Map(layout, 0x59, 0x15) == 38, "US Y resolves the upper letter row");
            Check(Map(layout, 0x5a, 0x2c) == 16, "US Z resolves the lower letter row");
            Check(Map(layout, 0x59, 0x2c) == 16, "German Y resolves the lower letter row");
            Check(Map(layout, 0x5a, 0x15) == 38, "German Z resolves the upper letter row");
            Check(Map(layout, 0x25, 0xe04b) == 77, "extended Left arrow resolves");
            Check(Map(layout, 0x25, 0x4b) == null, "numpad Left is not the drawn arrow");
            Check(Map(layout, 0x0d, 0xe01c) == null, "numpad Enter is not main Enter");
            Check(Map(layout, 0xdb, 0x1a) == 68, "OEM key uses physical scan");
            Check(Map(layout, 0x13, 0xe145) == null, "E1 Pause is not truncated");
            foreach (uint invalid in new uint[] { 0, 0xe000, 0x10043, 0xe20043, 0xffffffff })
                Check(Map(layout, 0x78, invalid) == null, "malformed mapped scan stays unknown");
            foreach (int modifier in new[] { 0x10, 0x11, 0x12, 0x5b, 0x5c, 0xa0, 0xa1, 0xa2, 0xa3, 0xa4, 0xa5 })
                Check(Map(layout, modifier, 0x43) == null, "modifier VK is never the main action");
            foreach (int invalid in new[] { -1, 0, 255, 256, Int32.MaxValue })
                Check(Map(layout, invalid, 0x43) == null, "invalid VK is not a physical key");
            foreach (string modifier in new[] { "ShiftLeft", "ShiftRight", "ControlLeft", "ControlRight", "AltLeft", "AltRight", "MetaLeft", "MetaRight" })
            {
                SuppressionKey key;
                Check(KeyboardScanCodes.TryGet(modifier, out key), "modifier scan exists");
                Check(HotkeyPhysicalKeyResolver.FindIndex(layout, key) == null, "modifier scan cannot be highlighted as the action");
            }
            Check(Map(layout, 0x78, 0x7f) == null, "unknown scan has no guessed Fn index");
            Check(HotkeyPhysicalKeyResolver.FindIndex(layout, new SuppressionKey()) == null, "unset physical key stays unknown");
        }
        Check(Map(iso, 0x0d, 0x1c) == 80, "ISO Enter index");
        Check(Map(ansi, 0x0d, 0x1c) == 81, "ANSI Enter index");
        Check(Map(iso, 0xdc, 0x2b) == 81, "ISO OEM backslash position");
        Check(Map(ansi, 0xdc, 0x2b) == 80, "ANSI OEM backslash position");
        Check(Map(iso, 0xe2, 0x56) == 10, "ISO extra backslash position");
        Check(Map(ansi, 0xe2, 0x56) == null, "ANSI does not invent ISO extra key");
        Check(Map(null, 0x78, 0x43) == null, "missing layout stays unknown");
        Check(Map(iso.WithKeyIndex("F9", null, false), 0x78, 0x43) == null, "unlearned key does not get default index");
        Check(Map(iso.WithKeyIndex("F9", 200, true), 0x78, 0x43) == 200, "learned key index is respected");
        var ambiguous = new KeyboardLayout("Ambiguous", new SizeF(2, 1), new[] {
            new KeyboardKeyDefinition("Backslash", 1, new RectangleF(0, 0, 1, 1), false, false),
            new KeyboardKeyDefinition("IntlRo", 2, new RectangleF(1, 0, 1, 1), false, false)
        }, "test");
        Check(Map(ambiguous, 0xdc, 0x2b) == null, "ambiguous scan does not choose arbitrarily");
        return "PASS " + checks + " pure physical-hotkey checks; no Windows API invoked.";
    }
}
