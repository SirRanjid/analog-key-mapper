using System;
using Tk75.Mapping;

public static class ProfileHotkeysHarness
{
    static int checks;
    const string Prefix = "{\"Version\":1,\"Name\":\"Shortcut test\",\"Bindings\":[]";
    static void Check(bool value, string reason) { checks++; if (!value) throw new Exception(reason); }
    static void Reject(Action action, string reason)
    { bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } Check(rejected, reason); }
    static bool Modifier(int key) { return key == 0x10 || key == 0x11 || key == 0x12 || key == 0x5B || key == 0x5C || key >= 0xA0 && key <= 0xA5; }
    static void CheckImpact(Profile before, Profile after, bool requiresReset, string reason)
    {
        string originalBefore = ProfileJson.Serialize(before), originalAfter = ProfileJson.Serialize(after);
        var beforeList = before.SuppressedKeyboardKeys; var afterList = after.SuppressedKeyboardKeys;
        Check(ProfileChangeImpact.RequiresControllerReset(before, after) == requiresReset, reason);
        Check(ProfileChangeImpact.RequiresControllerReset(after, before) == requiresReset, "Undo has the same impact: " + reason);
        Check(ProfileJson.Serialize(before) == originalBefore && ProfileJson.Serialize(after) == originalAfter &&
            Object.ReferenceEquals(before.SuppressedKeyboardKeys, beforeList) && Object.ReferenceEquals(after.SuppressedKeyboardKeys, afterList),
            "Impact assessment does not mutate either profile or replace its selected-key list");
    }
    static void SuppressionChangeImpact()
    {
        var original = new Profile();
        original.Bindings.Add(new Binding { BindingId = "w", KeyIndex = 14, Target = OutputTarget.LeftYPositive });
        original.Inputs.Add(new KeyInputSettings { KeyIndex = 14 });
        original.SuppressedKeyboardKeys.Add(14);
        CheckImpact(original, original, false, "An unchanged profile does not disconnect its controller");
        CheckImpact(original, ProfileJson.Clone(original), false, "An equivalent detached profile does not reset output");
        var marker = ProfileJson.Clone(original); marker.ModeSwitchLightingEnabled = true;
        CheckImpact(original, marker, false, "Enabling only the shortcut marker keeps the controller connected");
        marker = ProfileJson.Clone(original); marker.ModeSwitchRgbColor = 0x123456;
        CheckImpact(original, marker, false, "Choosing only a shortcut color keeps the controller connected");
        marker.ModeSwitchLightingEnabled = true;
        CheckImpact(original, marker, false, "Changing shortcut lighting and color together keeps the controller connected");
        marker.KeyboardSuppressionMode = KeyboardSuppressionMode.AllMapped; marker.SuppressedKeyboardKeys.Add(9);
        CheckImpact(original, marker, false, "Combined hook and shortcut-lighting preferences do not reset live output");
        var lighting = ProfileJson.Clone(original); lighting.RgbOverrideEnabled = true;
        CheckImpact(original, lighting, false, "Enabling controller key colors keeps the output that supplies their eligibility connected");
        lighting = ControllerRouting.SetRgbColor(original, "main", 0x123456);
        CheckImpact(original, lighting, false, "First color materializes the legacy route without disconnecting it");
        lighting.RgbOverrideEnabled = true; lighting.ModeSwitchLightingEnabled = true;
        lighting.KeyboardSuppressionMode = KeyboardSuppressionMode.AllMapped;
        CheckImpact(original, lighting, false, "Combined lighting and suppression changes preserve active output");
        var multicolor = ControllerRouting.Add(lighting, "second", "Second", ControllerKind.DualSense);
        var recolored = ControllerRouting.SetRgbColor(multicolor, "main", 0xFFFFFF);
        recolored = ControllerRouting.SetRgbColor(recolored, "second", 0);
        CheckImpact(multicolor, recolored, false, "Changing colors on multiple existing routes does not reset output");
        CheckImpact(recolored, ControllerRouting.SetRgbColor(recolored, "second", null), false, "Resetting a controller to palette color preserves the connection");
        CheckImpact(lighting, multicolor, true, "Adding a controller remains a structural output change");
        CheckImpact(multicolor, ControllerRouting.Rename(recolored, "second", "Changed"), true, "A simultaneous color edit cannot conceal changed controller metadata");
        CheckImpact(multicolor, ControllerRouting.SetKind(recolored, "second", ControllerKind.Xbox360), true, "A simultaneous color edit cannot conceal a changed controller kind");
        var reorderedControllers = ProfileJson.Clone(recolored); reorderedControllers.Controllers.Reverse();
        CheckImpact(multicolor, reorderedControllers, true, "A simultaneous color edit cannot conceal changed controller order");
        foreach (KeyboardSuppressionMode mode in Enum.GetValues(typeof(KeyboardSuppressionMode)))
        {
            var changed = ProfileJson.Clone(original); changed.KeyboardSuppressionMode = mode;
            CheckImpact(original, changed, false, "Changing only suppression mode keeps the live controller connected");
            changed.SuppressedKeyboardKeys.Add(9);
            CheckImpact(original, changed, false, "Changing mode and selected keys together keeps output connected");
            var changedList = ProfileJson.Clone(changed); changedList.SuppressedKeyboardKeys.Clear();
            CheckImpact(changed, changedList, false, "Removing only selected keys does not require output reconfiguration");
            var reordered = ProfileJson.Clone(changed); reordered.SuppressedKeyboardKeys.Reverse();
            CheckImpact(changed, reordered, false, "Reordering the selection is still only a suppression change");
        }
        var modifications = new Action<Profile>[] {
            delegate(Profile p) { p.Bindings[0].Target = OutputTarget.A; },
            delegate(Profile p) { p.Bindings[0].KeyIndex = 9; },
            delegate(Profile p) { p.Bindings[0].Enabled = false; },
            delegate(Profile p) { p.Bindings.Add(new Binding { BindingId = "s", KeyIndex = 9, Target = OutputTarget.LeftYNegative }); },
            delegate(Profile p) { p.Bindings[0].Processing.Curve = CurveKind.Bezier; },
            delegate(Profile p) { p.Bindings[0].Processing.CustomPoints.Insert(1, new CurvePoint(.5, .75)); },
            delegate(Profile p) { p.Bindings[0].Processing.TopDeadzone = .2; },
            delegate(Profile p) { p.Inputs[0].RapidTriggerEnabled = true; },
            delegate(Profile p) { p.Inputs[0].ActuationPoint = .3; },
            delegate(Profile p) { p.Controllers.Add(new ControllerDefinition { Id = "main", Name = "Renamed controller", Kind = ControllerKind.Xbox360, RgbColor = 0x123456 }); },
            delegate(Profile p) { p.ModeSwitchHotkey.Modifiers = 2; },
            delegate(Profile p) { p.EmergencyStopHotkey.Enabled = false; },
            delegate(Profile p) { p.Controller = ControllerKind.DualSense; },
            delegate(Profile p) { p.ControllerInputEnabled = false; },
            delegate(Profile p) { p.StickShape = StickShape.Square; },
            delegate(Profile p) { p.Name = "Other profile"; }
        };
        foreach (Action<Profile> change in modifications)
        {
            var changed = ProfileJson.Clone(original); change(changed);
            CheckImpact(original, changed, true, "Actual output, input behavior, route or shortcut changes retain normal controller reset behavior");
            changed.KeyboardSuppressionMode = KeyboardSuppressionMode.SelectedMapped; changed.SuppressedKeyboardKeys.Add(9);
            changed.ModeSwitchLightingEnabled = true; changed.ModeSwitchRgbColor = 0xABCDEF;
            changed.RgbOverrideEnabled = true;
            CheckImpact(original, changed, true, "Concurrent suppression and lighting edits cannot hide a real controller-affecting change");
        }
        Reject(delegate { ProfileChangeImpact.RequiresControllerReset(null, original); }, "Missing previous profile is rejected");
        Reject(delegate { ProfileChangeImpact.RequiresControllerReset(original, null); }, "Missing next profile is rejected");
        var invalid = ProfileJson.Clone(original); invalid.KeyboardSuppressionMode = (KeyboardSuppressionMode)3;
        Reject(delegate { ProfileChangeImpact.RequiresControllerReset(original, invalid); }, "An invalid next suppression mode is validated before masking suppression fields");
        Reject(delegate { ProfileChangeImpact.RequiresControllerReset(invalid, original); }, "An invalid previous suppression mode is also rejected");
        invalid = ProfileJson.Clone(original); invalid.SuppressedKeyboardKeys.Add(14);
        Reject(delegate { ProfileChangeImpact.RequiresControllerReset(original, invalid); }, "An invalid next selected-key list is not silently ignored");
        invalid = ProfileJson.Clone(original); invalid.ModeSwitchRgbColor = -1;
        Reject(delegate { ProfileChangeImpact.RequiresControllerReset(original, invalid); }, "Invalid marker color is validated before visual fields are ignored");
        invalid = ProfileJson.Clone(lighting); invalid.Controllers[0].RgbColor = -1;
        Reject(delegate { ProfileChangeImpact.RequiresControllerReset(lighting, invalid); }, "Invalid controller color is validated before color-only edits are ignored");
        Reject(delegate { ProfileChangeImpact.RequiresControllerReset(invalid, lighting); }, "Invalid previous controller color is rejected too");
    }
    public static string Run()
    {
        checks = 0;
        SuppressionChangeImpact();
        Profile legacy = ProfileJson.Deserialize(Prefix + "}");
        Check(legacy.ModeSwitchHotkey.Enabled && legacy.ModeSwitchHotkey.KeyCode == 0x78 && legacy.ModeSwitchHotkey.Modifiers == 0, "Legacy mode default is plain F9");
        Check(legacy.EmergencyStopHotkey.Enabled && legacy.EmergencyStopHotkey.KeyCode == 0x77 && legacy.EmergencyStopHotkey.Modifiers == 0, "Legacy off-switch default is plain F8");
        Check(legacy.ControllerInputEnabled && legacy.Inputs.Count == 0 && legacy.SuppressedKeyboardKeys.Count == 0, "Legacy controller input stays enabled without added activation or suppression settings");
        Check(legacy.KeyboardSuppressionMode == KeyboardSuppressionMode.Off && new Profile().KeyboardSuppressionMode == KeyboardSuppressionMode.Off, "Legacy and new profiles leave keyboard suppression off by default");
        Check(!ProfileJson.Serialize(legacy).Contains("\"KeyboardSuppressionMode\""), "Default suppression mode is omitted for backwards-compatible profile JSON");
        var oldSelection = ProfileJson.Deserialize(Prefix + ",\"SuppressedKeyboardKeys\":[14,9]}");
        Check(oldSelection.KeyboardSuppressionMode == KeyboardSuppressionMode.Off && oldSelection.SuppressedKeyboardKeys.Count == 2, "An older saved key selection does not silently enable suppression");
        foreach (KeyboardSuppressionMode mode in Enum.GetValues(typeof(KeyboardSuppressionMode)))
        {
            var selected = ProfileJson.Clone(oldSelection); selected.KeyboardSuppressionMode = mode;
            string serialized = ProfileJson.Serialize(selected);
            var restored = ProfileJson.Deserialize(serialized);
            Check(restored.KeyboardSuppressionMode == mode && restored.SuppressedKeyboardKeys[0] == 14 && restored.SuppressedKeyboardKeys[1] == 9, "Every suppression mode roundtrips without discarding the separate selected-key list");
            Check(serialized.Contains("\"KeyboardSuppressionMode\":" + (int)mode) == (mode != KeyboardSuppressionMode.Off), "Only nondefault suppression modes are emitted");
            restored.SuppressedKeyboardKeys.Clear(); restored.KeyboardSuppressionMode = KeyboardSuppressionMode.Off;
            Check(selected.SuppressedKeyboardKeys.Count == 2 && selected.KeyboardSuppressionMode == mode, "Suppression mode and selected keys are detached when cloned");
            var edits = new EditHistory(legacy); edits.Commit(selected); edits.Undo();
            Check(edits.Current.KeyboardSuppressionMode == KeyboardSuppressionMode.Off && edits.Current.SuppressedKeyboardKeys.Count == 0, "Undo restores prior suppression preference and selection together");
            edits.Redo(); Check(edits.Current.KeyboardSuppressionMode == mode && edits.Current.SuppressedKeyboardKeys.Count == 2, "Redo restores the chosen suppression preference and selection");
            Check(ProfileJson.Deserialize(Prefix + ",\"KeyboardSuppressionMode\":" + (int)mode + "}").KeyboardSuppressionMode == mode, "Every explicit valid integer suppression mode is accepted");
        }
        foreach (int invalid in new[] { -1, 3, 256, Int32.MinValue, Int32.MaxValue })
        {
            var badMode = new Profile { KeyboardSuppressionMode = (KeyboardSuppressionMode)invalid };
            Check(MappingValidation.ValidateProfile(badMode).Count != 0, "Invalid in-memory suppression enum is rejected");
            Reject(delegate { ProfileJson.Serialize(badMode); }, "Invalid suppression enum cannot be exported");
        }
        foreach (string invalid in new[] { "-1", "3", "2147483648", "1.0", "1e0", "null", "true", "\"AllMapped\"", "\"1\"", "[]", "{}" })
        {
            string json = Prefix + ",\"KeyboardSuppressionMode\":" + invalid + "}";
            Reject(delegate { ProfileJson.Deserialize(json); }, "Suppression mode requires one exact supported integer");
        }
        Reject(delegate { ProfileJson.Deserialize(Prefix + ",\"KeyboardSuppressionMode\":1,\"KeyboardSuppressionMode\":2}"); }, "Duplicate suppression mode fields are rejected");
        for (int key = 1; key <= 255; key++)
        {
            var shortcut = new HotkeySettings { KeyCode = key };
            Check((MappingValidation.ValidateHotkey(shortcut).Count == 0) == !Modifier(key), "Only plain modifiers are excluded from virtual key range");
        }
        foreach (int invalid in new[] { -1, 0, 256, Int32.MaxValue })
        { int key = invalid; Reject(delegate { var p = new Profile(); p.ModeSwitchHotkey.KeyCode = key; ProfileJson.Serialize(p); }, "Invalid virtual keys cannot be serialized"); }
        for (int modifiers = 0; modifiers <= 15; modifiers++)
        {
            var p = new Profile();
            p.ModeSwitchHotkey = new HotkeySettings { Enabled = true, KeyCode = 0x4D, Modifiers = modifiers };
            p.EmergencyStopHotkey = new HotkeySettings { Enabled = true, KeyCode = 0x57, Modifiers = modifiers };
            p.ControllerInputEnabled = false;
            var copy = ProfileJson.Clone(p);
            Check(copy.ModeSwitchHotkey.Modifiers == modifiers && copy.EmergencyStopHotkey.Modifiers == modifiers && !copy.ControllerInputEnabled, "Every modifier combination roundtrips with disabled default input");
            copy.EmergencyStopHotkey.KeyCode = copy.ModeSwitchHotkey.KeyCode;
            Reject(delegate { ProfileJson.Serialize(copy); }, "Duplicate exact enabled hotkeys are rejected");
            copy.EmergencyStopHotkey.Modifiers = (modifiers + 1) & 15;
            Check(MappingValidation.ValidateProfile(copy).Count == 0, "Same key with different modifiers remains distinct");
            copy.EmergencyStopHotkey.Modifiers = modifiers; copy.ModeSwitchHotkey.Enabled = false;
            Check(MappingValidation.ValidateProfile(copy).Count == 0, "A disabled hotkey does not reserve its combination");
            copy.ModeSwitchHotkey.Enabled = true; copy.EmergencyStopHotkey.Enabled = false;
            Check(MappingValidation.ValidateProfile(copy).Count == 0, "Either disabled hotkey removes the duplicate restriction");
        }
        foreach (int bad in new[] { -1, 16, 0x4000, Int32.MaxValue })
        { int modifiers = bad; Reject(delegate { var p = new Profile(); p.ModeSwitchHotkey.Modifiers = modifiers; ProfileJson.Serialize(p); }, "Unsupported modifier bits are rejected"); }
        var free = new Profile(); free.ModeSwitchHotkey.KeyCode = 0x77; free.EmergencyStopHotkey.KeyCode = 0x78;
        Check(MappingValidation.ValidateProfile(free).Count == 0, "F8 and F9 can be swapped; neither is permanently reserved");
        free.ModeSwitchHotkey.Enabled = false; free.EmergencyStopHotkey.Enabled = false; free.ControllerInputEnabled = false;
        var roundtrip = ProfileJson.Clone(free);
        Check(!roundtrip.ModeSwitchHotkey.Enabled && !roundtrip.EmergencyStopHotkey.Enabled && !roundtrip.ControllerInputEnabled, "All shortcuts and controller input can be off together");
        roundtrip.ModeSwitchHotkey.KeyCode = 0x4B; roundtrip.EmergencyStopHotkey.Modifiers = 7;
        Check(free.ModeSwitchHotkey.KeyCode == 0x77 && free.EmergencyStopHotkey.Modifiers == 0, "Shortcut objects are deeply cloned");
        var history = new EditHistory(legacy); history.Commit(free); history.Undo();
        Check(history.Current.ControllerInputEnabled && history.Current.ModeSwitchHotkey.KeyCode == 0x78, "Undo restores profile input and shortcut defaults");
        history.Redo(); Check(!history.Current.ControllerInputEnabled && !history.Current.ModeSwitchHotkey.Enabled, "Redo restores the full optional configuration");
        foreach (string field in new[] { "ModeSwitchHotkey", "EmergencyStopHotkey" })
        {
            foreach (string bad in new[] { "null", "true", "[]", "120", "\"F9\"", "{}", "{\"Enabled\":true,\"KeyCode\":120}",
                "{\"Enabled\":1,\"KeyCode\":120,\"Modifiers\":0}", "{\"Enabled\":true,\"KeyCode\":\"120\",\"Modifiers\":0}",
                "{\"Enabled\":true,\"KeyCode\":120.5,\"Modifiers\":0}", "{\"Enabled\":true,\"KeyCode\":1.2e2,\"Modifiers\":0}",
                "{\"Enabled\":true,\"KeyCode\":120,\"Modifiers\":0.0}", "{\"Enabled\":true,\"KeyCode\":120,\"Modifiers\":null}",
                "{\"Enabled\":true,\"KeyCode\":120,\"Modifiers\":0,\"Unknown\":false}", "{\"Enabled\":true,\"KeyCode\":120,\"KeyCode\":121,\"Modifiers\":0}" })
            { string json = Prefix + ",\"" + field + "\":" + bad + "}"; Reject(delegate { ProfileJson.Deserialize(json); }, "Malformed or incomplete shortcut object is rejected strictly"); }
        }
        foreach (string bad in new[] { "null", "1", "\"false\"", "[]", "{}" })
        { string json = Prefix + ",\"ControllerInputEnabled\":" + bad + "}"; Reject(delegate { ProfileJson.Deserialize(json); }, "Default input preference must be a JSON boolean"); }
        Reject(delegate { var p = new Profile(); p.ModeSwitchHotkey = null; ProfileJson.Serialize(p); }, "Mode shortcut metadata cannot be null");
        Reject(delegate { var p = new Profile(); p.EmergencyStopHotkey = null; ProfileJson.Serialize(p); }, "Stop shortcut metadata cannot be null");
        return "Profile hotkeys: " + checks + " checks PASS";
    }
}
