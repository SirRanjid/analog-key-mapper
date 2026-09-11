using System;
using System.Collections.Generic;
using Tk75.Mapping;

public static class RgbOverridePlanHarness
{
    static int checks;
    static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    static void Reject(Action action, string message)
    { bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } Check(rejected, message); }
    static Binding Binding(int index, string id, string controller, OutputTarget target, bool enabled)
    { return new Binding { KeyIndex = index, BindingId = id, ControllerId = controller, Target = target, Enabled = enabled }; }
    static void ModeSwitchMarker()
    {
        var profile = ProfileJson.Deserialize("{\"Version\":1,\"Name\":\"Marker\",\"Bindings\":[]}");
        Check(!profile.ModeSwitchLightingEnabled && profile.ModeSwitchRgbColor == 0xFFC65C && !new Profile().ModeSwitchLightingEnabled, "Old and new profiles leave the marker off with a warm default color");
        profile.Bindings.Add(Binding(14, "w", "main", OutputTarget.LeftYPositive, true));
        profile.ModeSwitchRgbColor = 0x345678;
        int controllerColor = RgbOverridePlan.GetControllerColor(profile, "main");
        foreach (bool keyboardMode in new[] { false, true })
        foreach (bool rgbEnabled in new[] { false, true })
        foreach (bool lightingEnabled in new[] { false, true })
        foreach (bool shortcutEnabled in new[] { false, true })
        foreach (bool shortcutActive in new[] { false, true })
        foreach (bool connected in new[] { false, true })
        foreach (bool resolved in new[] { false, true })
        {
            profile.RgbOverrideEnabled = rgbEnabled; profile.ModeSwitchLightingEnabled = lightingEnabled; profile.ModeSwitchHotkey.Enabled = shortcutEnabled;
            string original = ProfileJson.Serialize(profile);
            string[] active = connected ? new[] { "main" } : new string[0];
            bool controller = !keyboardMode && rgbEnabled && connected;
            bool marker = lightingEnabled && shortcutEnabled && shortcutActive && resolved;
            var result = RgbOverridePlan.Build(profile, active, keyboardMode, resolved ? (int?)54 : null, shortcutActive);
            Check(result.Count == (controller ? 1 : 0) + (marker ? 1 : 0) && result.ContainsKey(14) == controller && result.ContainsKey(54) == marker,
                "Only explicit controller and functioning marker conditions contribute colors");
            if (marker) Check(result[54] == 0x345678, "Marker color survives keyboard mode, no controller, and disabled controller RGB");
            var shared = RgbOverridePlan.Build(profile, active, keyboardMode, resolved ? (int?)14 : null, shortcutActive);
            Check(shared.Count == (controller || marker ? 1 : 0), "Shared mode/assignment key appears only once");
            if (controller || marker) Check(shared[14] == (marker ? 0x345678 : controllerColor), "Explicit marker wins over any controller color on the same key");
            Check(ProfileJson.Serialize(profile) == original, "Planning does not alter profile metadata or shortcut registration preferences");
        }
        profile.ModeSwitchLightingEnabled = true; profile.ModeSwitchHotkey.Enabled = true; profile.RgbOverrideEnabled = false;
        Check(RgbOverridePlan.Build(profile, new string[0], true).Count == 0, "Legacy three-argument planner does not invent a physical shortcut location");
        foreach (int index in new[] { 0, 255 })
            Check(RgbOverridePlan.Build(profile, new string[0], true, index, true)[index] == profile.ModeSwitchRgbColor, "Planner accepts sensor-index boundaries for the adapter to verify");
        profile.ModeSwitchLightingEnabled = false;
        foreach (int index in new[] { -1, 256, Int32.MaxValue })
            Reject(delegate { RgbOverridePlan.Build(profile, new string[0], true, index, false); }, "Supplied invalid physical index is rejected even when marker is disabled");
        Reject(delegate { RgbOverridePlan.Build(profile, null, true, 54, true); }, "Extended planner still rejects missing controller-state collection");
        foreach (int color in new[] { 0, 0xFFC65C, 0xFFFFFF })
        {
            profile.ModeSwitchLightingEnabled = true; profile.ModeSwitchRgbColor = color;
            var copy = ProfileJson.Clone(profile);
            Check(copy.ModeSwitchLightingEnabled && copy.ModeSwitchRgbColor == color, "Black, default and white marker colors roundtrip exactly");
            Check(RgbOverridePlan.Build(copy, new string[0], true, 54, true)[54] == color, "Every valid marker color reaches its resolved key exactly");
            var route = ControllerRouting.ForController(profile, "main");
            Check(route.ModeSwitchLightingEnabled && route.ModeSwitchRgbColor == color, "Controller routing preserves independent shortcut lighting metadata");
        }
        foreach (int color in new[] { -1, 0x1000000, Int32.MaxValue })
        {
            var invalid = ProfileJson.Clone(profile); invalid.ModeSwitchRgbColor = color; invalid.ModeSwitchLightingEnabled = false;
            Reject(delegate { ProfileJson.Serialize(invalid); }, "Invalid marker color is rejected even when lighting is off");
        }
        foreach (string value in new[] { "\"1\"", "true", "null", "[]", "{}", "-1", "16777216", "1.5", "1.0", "1e1" })
        {
            string json = "{\"Version\":1,\"Name\":\"Bad\",\"Bindings\":[],\"ModeSwitchRgbColor\":" + value + "}";
            Reject(delegate { ProfileJson.Deserialize(json); }, "Marker RGB JSON requires an exact in-range integer");
        }
        foreach (string value in new[] { "1", "\"true\"", "null", "[]", "{}" })
        {
            string json = "{\"Version\":1,\"Name\":\"Bad\",\"Bindings\":[],\"ModeSwitchLightingEnabled\":" + value + "}";
            Reject(delegate { ProfileJson.Deserialize(json); }, "Marker enable JSON requires a Boolean");
        }
        var before = new Profile(); var history = new EditHistory(before); history.Commit(profile);
        Check(history.Undo() && !history.Current.ModeSwitchLightingEnabled && history.Current.ModeSwitchRgbColor == 0xFFC65C, "Undo restores disabled marker and its default color");
        Check(history.Redo() && history.Current.ModeSwitchLightingEnabled && history.Current.ModeSwitchRgbColor == 0xFFFFFF, "Redo restores the chosen marker color and enabled preference");
    }

    public static string Run()
    {
        checks = 0;
        ModeSwitchMarker();
        Profile legacy = ProfileJson.Deserialize("{\"Version\":1,\"Name\":\"Legacy\",\"Bindings\":[]}");
        Check(!legacy.RgbOverrideEnabled && ControllerRouting.EffectiveControllers(legacy)[0].RgbColor == null, "Old profiles default RGB off and unspecified controller color");
        int defaultColor = RgbOverridePlan.GetControllerColor(legacy, "main");
        Check(defaultColor >= 0 && defaultColor <= 0xFFFFFF && legacy.Controllers.Count == 0, "Palette lookup is valid and does not materialize legacy metadata");
        legacy.Bindings.Add(Binding(14, "w", "main", OutputTarget.LeftYPositive, true));
        Check(RgbOverridePlan.Build(legacy, new[] { "main" }, false).Count == 0, "Connected controller cannot color keys before explicit RGB opt-in");
        legacy.RgbOverrideEnabled = true;
        Check(RgbOverridePlan.Build(legacy, new[] { "main" }, true).Count == 0, "Keyboard mode requests no override even with connected controllers");
        Check(RgbOverridePlan.Build(legacy, new string[0], false).Count == 0, "No real active controller means no override");
        Check(RgbOverridePlan.Build(legacy, new[] { "missing", "MAIN" }, false).Count == 0, "Unknown and differently cased active IDs cannot contribute keys");
        var desired = RgbOverridePlan.Build(legacy, new[] { "main", "main" }, false);
        Check(desired.Count == 1 && desired[14] == defaultColor, "Legacy active route receives its palette color exactly once");
        desired[14] = 42;
        Check(RgbOverridePlan.Build(legacy, new[] { "main" }, false)[14] == defaultColor, "Planner results are detached");
        var profile = ControllerRouting.Add(legacy, "second", "Player 2", ControllerKind.DualSense);
        profile.Bindings.Add(Binding(14, "second-w", "second", OutputTarget.A, true));
        profile.Bindings.Add(Binding(21, "second-s", "second", OutputTarget.B, true));
        profile.Bindings.Add(Binding(22, "off", "second", OutputTarget.X, false));
        profile.Bindings.Add(Binding(14, "also", "main", OutputTarget.RightTrigger, true));
        profile.Inputs.Add(new KeyInputSettings { KeyIndex = 14, OppositeKeyIndex = 23 });
        profile.Inputs.Add(new KeyInputSettings { KeyIndex = 23, OppositeKeyIndex = 14 });
        profile.SuppressedKeyboardKeys.Add(24);
        int secondDefault = RgbOverridePlan.GetControllerColor(profile, "second");
        Check(defaultColor != secondDefault, "First two slots have distinct default palette colors");
        string before = ProfileJson.Serialize(profile);
        var colored = ControllerRouting.SetRgbColor(profile, "second", 0x123456);
        Check(ProfileJson.Serialize(profile) == before, "Color edit leaves the source profile unchanged");
        Check(RgbOverridePlan.GetControllerColor(colored, "second") == 0x123456 && colored.RgbOverrideEnabled, "Explicit color is independent of the saved RGB opt-in");
        desired = RgbOverridePlan.Build(colored, new[] { "second", "main" }, false);
        Check(desired.Count == 2 && desired[14] == defaultColor && desired[21] == 0x123456, "First controller in profile order wins a shared key independent of active-ID enumeration");
        Check(!desired.ContainsKey(22) && !desired.ContainsKey(23) && !desired.ContainsKey(24), "Disabled bindings, unbound SOCD partners and suppression-only keys are excluded");
        desired = RgbOverridePlan.Build(colored, new[] { "second" }, false);
        Check(desired[14] == 0x123456 && desired[21] == 0x123456, "Next active controller takes over when the earlier owner disconnects");
        colored.Controllers.Reverse();
        Check(RgbOverridePlan.Build(colored, new[] { "main", "second" }, false)[14] == 0x123456, "A changed profile order changes the documented shared-key winner");
        var roundtrip = ProfileJson.Clone(colored);
        Check(roundtrip.RgbOverrideEnabled && roundtrip.Controllers[0].RgbColor == 0x123456, "RGB option and color survive strict roundtrip");
        var effective = ControllerRouting.EffectiveControllers(roundtrip); effective[0].RgbColor = 7;
        Check(roundtrip.Controllers[0].RgbColor == 0x123456, "Effective route color metadata is copied");
        var routed = ControllerRouting.ForController(roundtrip, "second");
        Check(routed.Controllers[0].RgbColor == 0x123456 && routed.RgbOverrideEnabled, "Selected route retains explicit RGB metadata");
        var renamed = ControllerRouting.Rename(roundtrip, "second", "Guest");
        renamed = ControllerRouting.SetKind(renamed, "second", ControllerKind.Xbox360);
        Check(renamed.Controllers[0].RgbColor == 0x123456, "Rename and kind changes preserve explicit color");
        var cleared = ControllerRouting.SetRgbColor(renamed, "second", null);
        Check(cleared.Controllers[0].RgbColor == null && RgbOverridePlan.GetControllerColor(cleared, "second") == defaultColor, "Clearing color uses the current profile-order palette");
        foreach (int valid in new[] { 0, 0xFFFFFF })
        {
            var edge = ControllerRouting.SetRgbColor(legacy, "main", valid);
            Check(ProfileJson.Clone(edge).Controllers[0].RgbColor == valid, "Black and white are valid explicit RGB colors");
        }
        foreach (int bad in new[] { -1, 0x1000000, Int32.MaxValue })
        { int candidate = bad; Reject(delegate { ControllerRouting.SetRgbColor(legacy, "main", candidate); }, "Out-of-range RGB edit is rejected atomically"); }
        Reject(delegate { ControllerRouting.SetRgbColor(legacy, "absent", 1); }, "Unknown color-edit route is rejected");
        Reject(delegate { RgbOverridePlan.GetControllerColor(legacy, "absent"); }, "Unknown color lookup is rejected");
        Reject(delegate { RgbOverridePlan.Build(legacy, null, false); }, "Missing active-ID collection is rejected");
        Reject(delegate { RgbOverridePlan.Build(null, new string[0], false); }, "Invalid profile cannot produce an override plan");
        foreach (string value in new[] { "\"1\"", "true", "[]", "{}", "-1", "16777216", "1.5", "1.0", "1e1" })
        {
            string json = "{\"Version\":1,\"Name\":\"Bad\",\"Bindings\":[],\"Controllers\":[{\"Id\":\"main\",\"Name\":\"Main\",\"Kind\":0,\"RgbColor\":" + value + "}]}";
            Reject(delegate { ProfileJson.Deserialize(json); }, "RGB JSON requires null or an in-range integer");
        }
        foreach (string value in new[] { "1", "\"true\"", "null", "[]", "{}" })
        {
            string json = "{\"Version\":1,\"Name\":\"Bad\",\"Bindings\":[],\"RgbOverrideEnabled\":" + value + "}";
            Reject(delegate { ProfileJson.Deserialize(json); }, "RGB enable JSON requires a Boolean");
        }
        var explicitNull = ProfileJson.Deserialize("{\"Version\":1,\"Name\":\"Null\",\"Bindings\":[],\"Controllers\":[{\"Id\":\"main\",\"Name\":\"Main\",\"Kind\":0,\"RgbColor\":null}]}");
        Check(explicitNull.Controllers[0].RgbColor == null && !explicitNull.RgbOverrideEnabled, "Explicit null color requests the palette without enabling override");
        var history = new EditHistory(legacy); history.Commit(ControllerRouting.SetRgbColor(legacy, "main", 0xABCDEF));
        Check(history.Undo() && history.Current.Controllers.Count == 0, "RGB edit Undo preserves legacy representation");
        Check(history.Redo() && history.Current.Controllers[0].RgbColor == 0xABCDEF, "RGB edit Redo restores exact color");
        return "PASS: " + checks + " pure RGB metadata/plan checks; no hardware or lighting writes.";
    }
}
