using System;
using System.Collections.Generic;
using System.IO;
using Tk75.App;
using Tk75.Diagnostics;

public static class RgbStartupMarkersHarness
{
    static int checks;
    const int Yellow = 0xf1c232, Blue = 0x126bff, Background = 0x334455;
    static void Check(bool value, string reason) { checks++; if (!value) throw new Exception(reason); }
    static Tk75RgbSnapshot Picture(uint model, int background)
    {
        byte[] settings = new byte[64], picture = new byte[384];
        for (int i = 0; i < picture.Length; i++) picture[i] = (byte)(i * 17 + 41);
        settings[0] = 0x87; settings[1] = 13; settings[2] = 2; settings[3] = 4; settings[4] = 0x40;
        settings[5] = 23; settings[6] = 84; settings[7] = 198; settings[50] = 173;
        var colors = new Dictionary<int, int>();
        foreach (int key in Tk75RgbProtocol.GetSupportedKeyIndices(model)) colors.Add(key, background);
        return new Tk75RgbSnapshot(model, 2, 4, settings, Tk75RgbProtocol.Overlay(model, picture, colors));
    }
    static Tk75RgbSnapshot Paint(Tk75RgbSnapshot value, int key, int color)
    { return new Tk75RgbSnapshot(value.ModelId, value.Profile, value.Layer, value.RawSettings, Tk75RgbProtocol.Overlay(value.ModelId, value.Picture, new Dictionary<int, int> { { key, color } })); }
    static Dictionary<int, HashSet<int>> Config()
    { return new Dictionary<int, HashSet<int>> { { Yellow, new HashSet<int> { 60 } }, { Blue, new HashSet<int> { 14, 9, 15, 21 } } }; }
    static void NoMarkers(RgbStartupMarkerDecision result, string reason)
    { Check(!result.HasMarkers && !result.CanRepair && result.Repaired == null && result.MarkerKeys.Length == 0, reason); }
    static void Restores(Tk75RgbSnapshot current, Tk75RgbSnapshot original, Dictionary<int, HashSet<int>> config, int count, string reason)
    {
        RgbStartupMarkerDecision result = RgbStartupMarkers.Assess(current, config);
        Check(result.HasMarkers && result.CanRepair && result.MarkerKeys.Length == count, reason);
        Check(Tk75RgbExchange.Equivalent(result.Repaired, original), reason + ": complete expected picture and settings");
        Check(Tk75RgbProtocol.Equal(result.Repaired.RawSettings, current.RawSettings), reason + ": all raw settings preserved");
        Tk75RgbExchange.Validate(current, result.Repaired);
    }
    static void Rejects(Action action, string reason)
    {
        bool rejected = false;
        try { action(); } catch (ArgumentException) { rejected = true; } catch (InvalidDataException) { rejected = true; }
        Check(rejected, reason);
    }
    public static void Run()
    {
        checks = 0;
        foreach (uint model in new uint[] { 3590, 3591 })
        {
            var config = Config();
            Tk75RgbSnapshot original = Picture(model, Background), stale = Paint(original, 60, Yellow);
            Restores(stale, original, config, 1, "Switch-only marker is cleaned without enabled-option inputs");
            Restores(Paint(Paint(stale, 14, Blue), 9, Blue), original, config, 3, "Switch and partial controller markers are cleaned together");
            NoMarkers(RgbStartupMarkers.Assess(original, config), "Unmarked keyboard is unchanged");
            NoMarkers(RgbStartupMarkers.Assess(Picture(model, Yellow), config), "Uniform configured yellow is an external overall color");
            NoMarkers(RgbStartupMarkers.Assess(Paint(stale, 1, Yellow), config), "Same switch color on an unrelated key prevents its cleanup");
            NoMarkers(RgbStartupMarkers.Assess(Paint(Paint(original, 14, Blue), 60, Blue), config), "Controller color on the switch key is outside its own group");
            Restores(Paint(Picture(model, 0x090807), 60, Yellow), Picture(model, 0x090807), config, 1, "Current changed overall color is retained");
            Restores(Paint(Picture(model, 0), 60, Yellow), Picture(model, 0), config, 1, "Dark unchanged keys establish a dark background");
            var sameColor = new Dictionary<int, HashSet<int>> { { Yellow, new HashSet<int> { 14, 60 } } };
            Restores(Paint(stale, 14, Yellow), original, sameColor, 2, "Shared switch and controller color uses the combined candidate group");
            var varied = Paint(stale, 1, 0x563412);
            var uncertain = RgbStartupMarkers.Assess(varied, config);
            Check(uncertain.HasMarkers && !uncertain.CanRepair && uncertain.Repaired == null && uncertain.Reason != null, "Varied background is detected without guessing replacement colors");
            var variedOriginal = Paint(original, 1, 0x563412);
            var trusted = RgbStartupMarkers.Assess(varied, config, variedOriginal);
            Check(trusted.CanRepair && Tk75RgbExchange.Equivalent(trusted.Repaired, variedOriginal), "Compatible trusted original supplies exact per-key background");
            Check(!RgbStartupMarkers.Assess(varied, config, original).CanRepair, "An outdated original is rejected when the current background differs");
            var wrongProfile = new Tk75RgbSnapshot(model, 3, 4, variedOriginal.RawSettings, variedOriginal.Picture);
            Check(!RgbStartupMarkers.Assess(varied, config, wrongProfile).CanRepair, "A different onboard profile cannot supply a baseline");
            var changedReserved = variedOriginal.Picture; changedReserved[91 * 3] ^= 1;
            Check(!RgbStartupMarkers.Assess(varied, config, new Tk75RgbSnapshot(model, 2, 4, variedOriginal.RawSettings, changedReserved)).CanRepair, "Changed unknown LED bytes reject trusted baseline reuse");
            var changedBrightness = variedOriginal.RawSettings; changedBrightness[3] = 2;
            Check(!RgbStartupMarkers.Assess(varied, config, new Tk75RgbSnapshot(model, 2, 4, changedBrightness, variedOriginal.Picture)).CanRepair, "Changed brightness rejects trusted baseline reuse");
            int[] keys = trusted.MarkerKeys; keys[0] = 0;
            Check(trusted.MarkerKeys[0] == 60, "Returned marker keys cannot mutate the decision");
            foreach (int mode in new int[] { 0, 1, 4, 25 })
            {
                byte[] hidden = stale.RawSettings; hidden[1] = (byte)mode;
                NoMarkers(RgbStartupMarkers.Assess(new Tk75RgbSnapshot(model, 2, 4, hidden, stale.Picture), config), "Stored picture under another visible mode is not a marker");
            }
            foreach (int selection in new int[] { 0x00, 0x10, 0x30, 0x41, 0x50 })
            {
                byte[] hidden = stale.RawSettings; hidden[4] = (byte)selection;
                NoMarkers(RgbStartupMarkers.Assess(new Tk75RgbSnapshot(model, 2, 4, hidden, stale.Picture), config), "Only exact layer-4 custom selection is visible");
            }
            byte[] off = stale.RawSettings; off[3] = 0;
            NoMarkers(RgbStartupMarkers.Assess(new Tk75RgbSnapshot(model, 2, 4, off, stale.Picture), config), "Brightness zero does not expose stale stored marker colors");
            NoMarkers(RgbStartupMarkers.Assess(new Tk75RgbSnapshot(model, 2, 3, stale.RawSettings, stale.Picture), config), "Another snapshot layer cannot be inspected as app artwork");
            NoMarkers(RgbStartupMarkers.Assess(stale, new Dictionary<int, HashSet<int>>()), "Empty configuration makes no claims");
            NoMarkers(RgbStartupMarkers.Assess(stale, new Dictionary<int, HashSet<int>> { { Yellow, new HashSet<int>(Tk75RgbProtocol.GetSupportedKeyIndices(model)) } }), "All-key candidate set lacks an independent comparison key");
            NoMarkers(RgbStartupMarkers.Assess(stale, new Dictionary<int, HashSet<int>> { { Yellow, new HashSet<int> { 90, 91, 92, 126 } } }), "Unverified LED positions cannot establish markers");
            var wrongColor = Paint(original, 60, Yellow + 1);
            NoMarkers(RgbStartupMarkers.Assess(wrongColor, config), "Similar but nonidentical colors are preserved");
            byte[] before = Tk75RgbProtocol.EncodeSnapshot(stale);
            RgbStartupMarkers.Assess(stale, config);
            Check(Tk75RgbProtocol.Equal(before, Tk75RgbProtocol.EncodeSnapshot(stale)), "Assessment is read-only");
            Rejects(delegate { RgbStartupMarkers.Assess(null, config); }, "Missing snapshot rejected");
            Rejects(delegate { RgbStartupMarkers.Assess(stale, null); }, "Missing marker map rejected");
            Rejects(delegate { RgbStartupMarkers.Assess(stale, new Dictionary<int, HashSet<int>> { { -1, new HashSet<int> { 60 } } }); }, "Out-of-range color rejected");
            Rejects(delegate { RgbStartupMarkers.Assess(stale, new Dictionary<int, HashSet<int>> { { Yellow, null } }); }, "Null physical key set rejected");
        }
        Console.WriteLine("PASS: " + checks + " startup RGB marker checks (offline only).");
    }
}
