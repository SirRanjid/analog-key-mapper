using System;
using System.Collections.Generic;
using System.Linq;
using Tk75.Mapping;

public static class ControllerRoutingHarness
{
    static int assertions;
    static void Check(bool condition, string message)
    { assertions++; if (!condition) throw new Exception(message); }
    static void Reject(Action action, string message)
    { bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } Check(rejected, message); }
    static ControllerFrame Compose(Profile profile, IDictionary<int, double> raw)
    { return MappingEngine.Compose(raw, DefaultCalibration.Resolve(null), profile, new Dictionary<string, SignalState>(), new Dictionary<int, KeyInputState>(), .01); }
    static ControllerDefinition Definition(string id, string name, ControllerKind kind)
    { return new ControllerDefinition { Id = id, Name = name, Kind = kind }; }

    public static string Run()
    {
        assertions = 0;
        const string legacyJson = "{\"Version\":1,\"Name\":\"Legacy\",\"Bindings\":[{\"BindingId\":\"old\",\"KeyIndex\":14,\"Target\":2}],\"Controller\":1}";
        Profile legacy = ProfileJson.Deserialize(legacyJson);
        Check(legacy.Controllers.Count == 0 && legacy.Bindings[0].ControllerId == "main", "Old omitted members retain legacy defaults");
        var effective = ControllerRouting.EffectiveControllers(legacy);
        Check(effective.Count == 1 && effective[0].Id == "main" && effective[0].Kind == ControllerKind.DualSense, "Old controller kind becomes the effective main route");
        Profile oldSingle = ControllerRouting.ForController(legacy, "main");
        Check(oldSingle.Controller == ControllerKind.DualSense && oldSingle.Bindings.Count == 1, "Legacy routing keeps the original controller behavior");
        Check(Math.Abs(Compose(oldSingle, new Dictionary<int, double> { { 14, 192.5 } }).LeftY - .5) < 1e-10, "Legacy binding still maps proportionally");
        effective[0].Name = "Changed outside";
        Check(ControllerRouting.EffectiveControllers(legacy)[0].Name == "Controller 1", "Effective legacy definitions are detached");
        Check(legacy.Controllers.Count == 0, "Reading legacy routes does not materialize stored entries");
        Profile legacyRoundtrip = ProfileJson.Deserialize(ProfileJson.Serialize(legacy));
        Check(legacyRoundtrip.Controllers.Count == 0 && legacyRoundtrip.Bindings[0].ControllerId == "main", "Legacy profile roundtrips with defaults intact");

        var original = new Profile();
        original = MappingAssignments.Add(original, new[] { 14 }, OutputTarget.A);
        string originalJson = ProfileJson.Serialize(original);
        var two = ControllerRouting.Add(original, "player2", "Player 2", ControllerKind.DualSense);
        Check(two.Controllers.Count == 2 && two.Controllers[0].Id == "main", "Adding materializes the legacy main slot");
        Check(ProfileJson.Serialize(original) == originalJson, "Add leaves the input profile unchanged");
        two = MappingAssignments.Add(two, new[] { 14 }, OutputTarget.B, "player2");
        two = MappingAssignments.Add(two, new[] { 30 }, OutputTarget.LeftTrigger, "main");
        two = MappingAssignments.Add(two, new[] { 31 }, OutputTarget.RightTrigger, "player2");
        var main = ControllerRouting.ForController(two, "main");
        var second = ControllerRouting.ForController(two, "player2");
        Check(main.Bindings.Count == 2 && main.Bindings.All(b => b.ControllerId == "main"), "Main route receives only its assignments");
        Check(second.Bindings.Count == 2 && second.Bindings.All(b => b.ControllerId == "player2"), "Second route retains its route IDs");
        Check(second.Controllers.Count == 1 && second.Controllers[0].Id == "player2" && second.Controller == ControllerKind.DualSense, "Routed profile carries one matching controller definition");
        var mainFrame = Compose(main, new Dictionary<int, double> { { 14, 385 }, { 30, 96.25 } });
        var secondFrame = Compose(second, new Dictionary<int, double> { { 14, 385 }, { 31, 288.75 } });
        Check(mainFrame.Errors.Count == 0 && mainFrame.Buttons == 0x1000 && mainFrame.LeftTrigger == .25, "Player one gets independent A and trigger output");
        Check(secondFrame.Errors.Count == 0 && secondFrame.Buttons == 0x2000 && secondFrame.RightTrigger == .75, "Same physical key feeds independent B on player two");
        var unroutedStates = new Dictionary<string, SignalState> { { "prior", new SignalState { IsPressed = true, SmoothedValue = .8 } } };
        var unroutedInputs = new Dictionary<int, KeyInputState> { { 14, new KeyInputState { Active = true, HasActuated = true, PressOrder = 1, Peak = 1 } } };
        var unrouted = MappingEngine.Compose(new Dictionary<int, double> { { 14, 385 }, { 30, 385 }, { 31, 385 } }, DefaultCalibration.Resolve(null), two, unroutedStates, unroutedInputs, .01);
        Check(unrouted.Errors.Count > 0 && unrouted.Buttons == 0 && unrouted.LeftTrigger == 0 && unrouted.RightTrigger == 0, "Unrouted multiple controllers fail closed instead of merging players");
        Check(!unroutedStates["prior"].IsPressed && !unroutedInputs[14].Active, "Unrouted input resets previous processing state");
        var anotherSlotSameTarget = MappingAssignments.Add(two, new[] { 14 }, OutputTarget.A, "player2");
        Check(anotherSlotSameTarget.Bindings.Count == two.Bindings.Count + 1, "Same key and target on another controller is a distinct assignment");
        Check(ProfileJson.Serialize(MappingAssignments.Add(anotherSlotSameTarget, new[] { 14 }, OutputTarget.A, "player2")) == ProfileJson.Serialize(anotherSlotSameTarget), "Repeated Add is a no-op within that route");
        Check(Compose(main, new Dictionary<int, double> { { 14, 385 } }).Errors.Count > 0, "Missing local raw data remains an error");
        Check(Compose(second, new Dictionary<int, double> { { 14, 385 }, { 31, 288.75 } }).Errors.Count == 0, "Missing player-one sample cannot invalidate player two");
        main.Bindings[0].Processing.Scale = .2;
        main.Controllers[0].Name = "Changed route";
        Check(two.Bindings[0].Processing.Scale == 1 && two.Controllers[0].Name == "Controller 1", "ForController deeply detaches bindings and definitions");
        string json = ProfileJson.Serialize(two);
        Check(ProfileJson.Serialize(ProfileJson.Deserialize(json)) == json, "Multiple routes roundtrip exactly");

        var paired = KeyInputEditing.Configure(two, new KeyInputSettings { KeyIndex = 30, OppositeKeyIndex = 32, OppositePolicy = InputOpposedPolicy.LastPressed });
        paired = KeyInputEditing.Configure(paired, new KeyInputSettings { KeyIndex = 80, OppositeKeyIndex = 81, OppositePolicy = InputOpposedPolicy.Neutral });
        var pairRoute = ControllerRouting.ForController(paired, "main");
        Check(pairRoute.Inputs.Count == 2 && pairRoute.Inputs.Any(i => i.KeyIndex == 30) && pairRoute.Inputs.Any(i => i.KeyIndex == 32), "Needed physical settings include the unbound SOCD partner");
        var cleanRoute = ControllerRouting.ForController(paired, "player2");
        Check(cleanRoute.Inputs.Count == 0, "Unrelated player settings and inactive pairs are filtered out");
        Check(Compose(cleanRoute, new Dictionary<int, double> { { 14, 385 }, { 31, 385 } }).Errors.Count == 0, "Other routes do not demand another player's SOCD samples");
        Check(Compose(pairRoute, new Dictionary<int, double> { { 14, 385 }, { 30, 385 } }).Errors.Count > 0, "An actually needed SOCD partner still needs a real sample");
        Check(Compose(pairRoute, new Dictionary<int, double> { { 14, 385 }, { 30, 385 }, { 32, 0 } }).Errors.Count == 0, "Own complete SOCD sample set works");

        string before = ProfileJson.Serialize(two);
        var renamed = ControllerRouting.Rename(two, "player2", "Spieler: 2 / Couch");
        Check(renamed.Controllers[1].Name == "Spieler: 2 / Couch", "Names are app labels, not Windows device names");
        var changedKind = ControllerRouting.SetKind(two, "main", ControllerKind.DualSense);
        Check(changedKind.Controller == ControllerKind.DualSense && changedKind.Controllers[0].Kind == ControllerKind.DualSense, "Changing main keeps the legacy kind synchronized");
        var changedSecond = ControllerRouting.SetKind(two, "player2", ControllerKind.Xbox360);
        Check(changedSecond.Controller == two.Controller && changedSecond.Controllers[1].Kind == ControllerKind.Xbox360, "Changing another slot leaves the main kind alone");
        var removed = ControllerRouting.Remove(two, "player2");
        Check(removed.Controllers.Count == 1 && removed.Bindings.All(b => b.ControllerId == "main"), "Removing a slot removes its mappings");
        var withoutMain = ControllerRouting.Remove(two, "main");
        Check(withoutMain.Controllers.Count == 1 && withoutMain.Controllers[0].Id == "player2" && withoutMain.Bindings.All(b => b.ControllerId == "player2"), "Removing main preserves the remaining explicit slot");
        Reject(delegate { ControllerRouting.Remove(withoutMain, "player2"); }, "Cannot remove the final slot and accidentally restore legacy main");
        Reject(delegate { MappingAssignments.Add(withoutMain, new[] { 20 }, OutputTarget.A); }, "Legacy Add cannot silently route to an absent main");
        Check(MappingAssignments.Add(withoutMain, new[] { 20 }, OutputTarget.A, "player2").Bindings.Count == 3, "Explicit Add works when main was removed");
        Reject(delegate { ControllerRouting.ForController(two, "missing"); }, "Unknown route selection fails");
        Reject(delegate { ControllerRouting.Add(two, "player2", "Other", ControllerKind.Xbox360); }, "Duplicate IDs fail");
        Reject(delegate { ControllerRouting.Add(two, "third", "PLAYER 2", ControllerKind.Xbox360); }, "Names are unique ignoring case");
        Reject(delegate { ControllerRouting.Add(two, "bad route", "Third", ControllerKind.Xbox360); }, "Malformed IDs fail");
        Reject(delegate { ControllerRouting.Add(two, new String('x', 65), "Third", ControllerKind.Xbox360); }, "IDs longer than 64 fail");
        Reject(delegate { ControllerRouting.Rename(two, "main", new String('x', 65)); }, "Names longer than 64 fail");
        Reject(delegate { ControllerRouting.Rename(two, "main", "name\nother"); }, "Control characters in names fail");
        Reject(delegate { ControllerRouting.SetKind(two, "main", (ControllerKind)123); }, "Unknown controller kinds fail");
        Check(ProfileJson.Serialize(two) == before, "Successful and failed edits leave the source profile untouched");
        var limit = new Profile();
        for (int i = 1; i < 32; i++) limit = ControllerRouting.Add(limit, "slot" + i, "Player " + i, ControllerKind.Xbox360);
        Check(ControllerRouting.EffectiveControllers(limit).Count == 32, "Exactly 32 slots are supported");
        Reject(delegate { ControllerRouting.Add(limit, "overflow", "Overflow", ControllerKind.Xbox360); }, "Slot count is bounded");
        var invalid = ProfileJson.Clone(two); invalid.Bindings[0].ControllerId = "missing";
        Reject(delegate { ProfileJson.Serialize(invalid); }, "Unknown binding route is rejected");
        invalid = ProfileJson.Clone(two); invalid.Controllers[0].Kind = ControllerKind.DualSense;
        Reject(delegate { ProfileJson.Serialize(invalid); }, "Main/legacy kind disagreement is rejected");
        invalid = ProfileJson.Clone(two); invalid.Controllers = null;
        Reject(delegate { ProfileJson.Serialize(invalid); }, "Explicit null controllers list is not an empty legacy list");
        Reject(delegate { ProfileJson.Deserialize(json.Replace("\"Id\":\"player2\"", "\"Id\":\"player2\",\"Hidden\":1")); }, "Unknown controller JSON fields are rejected");
        Reject(delegate { ProfileJson.Deserialize(json.Replace("\"ControllerId\":\"main\"", "\"ControllerId\":42")); }, "Route ID JSON type is strict");
        Reject(delegate { ProfileJson.Deserialize(json.Replace("\"ControllerId\":\"main\"", "\"ControllerId\":\"main\",\"ControllerId\":\"main\"")); }, "Duplicate route JSON fields are rejected");
        var duplicate = ProfileJson.Clone(two);
        duplicate.Bindings.Add(new Binding { BindingId = "different-id", KeyIndex = 14, Target = OutputTarget.A, ControllerId = "main", Enabled = false });
        Reject(delegate { ProfileJson.Serialize(duplicate); }, "Duplicate physical key/output/slot is rejected even with a different ID or Enabled value");
        var duplicateFrame = Compose(duplicate, new Dictionary<int, double> { { 14, 385 }, { 30, 385 }, { 31, 385 } });
        Check(duplicateFrame.Errors.Count > 0 && duplicateFrame.Buttons == 0, "Direct invalid construction cannot emit duplicate output");
        const string duplicateJson = "{\"Version\":1,\"Name\":\"Duplicate\",\"Bindings\":[{\"BindingId\":\"a\",\"KeyIndex\":14,\"Target\":10},{\"BindingId\":\"b\",\"KeyIndex\":14,\"Target\":10}]}";
        Reject(delegate { ProfileJson.Deserialize(duplicateJson); }, "Duplicate assignments are rejected on import");

        var clipboard = KeyEditing.CopyKey(two, 14);
        Check(clipboard.Count == 2 && clipboard[0].ControllerId == "main" && clipboard[1].ControllerId == "player2", "Copy retains every source route by default");
        var sourceOne = KeyEditing.CopyKey(two, 14, "main");
        Check(sourceOne.Count == 1 && sourceOne[0].ControllerId == "main", "Explicit copy scopes to one route");
        var pasted = KeyEditing.Paste(two, new[] { 50 }, clipboard, CopyPart.All);
        Check(pasted.Bindings.Count(b => b.KeyIndex == 50) == 2 && pasted.Bindings.Any(b => b.KeyIndex == 50 && b.ControllerId == "player2"), "Paste without override preserves copied routes");
        var scoped = KeyEditing.Paste(two, new[] { 14 }, sourceOne, CopyPart.All, "player2");
        Check(scoped.Bindings.Count(b => b.KeyIndex == 14) == 2 && scoped.Bindings.Any(b => b.KeyIndex == 14 && b.ControllerId == "main" && b.Target == OutputTarget.A), "Scoped paste preserves another player's existing mapping");
        Check(scoped.Bindings.Any(b => b.KeyIndex == 14 && b.ControllerId == "player2" && b.Target == OutputTarget.A), "Scoped paste overrides the source route with the selected destination");
        sourceOne[0].Processing.Scale = .4;
        var partial = KeyEditing.Paste(two, new[] { 14 }, sourceOne, CopyPart.OutputRange, "player2");
        Check(partial.Bindings.Single(b => b.KeyIndex == 14 && b.ControllerId == "player2").Processing.Scale == .4 && partial.Bindings.Single(b => b.KeyIndex == 14 && b.ControllerId == "main").Processing.Scale == 1, "Scoped partial paste changes only the selected player's processing");
        var overrideMissing = KeyEditing.Paste(withoutMain, new[] { 60 }, sourceOne, CopyPart.All, "player2");
        Check(overrideMissing.Bindings.Any(b => b.KeyIndex == 60 && b.ControllerId == "player2"), "Explicit override can paste across profiles without the source slot");
        Reject(delegate { KeyEditing.Paste(withoutMain, new[] { 60 }, sourceOne, CopyPart.All); }, "Unresolved clipboard routes fail instead of silently remapping");
        Reject(delegate { KeyEditing.Paste(two, new[] { 60 }, sourceOne, CopyPart.All, "unknown"); }, "Unknown override route fails");
        Check(ProfileJson.Serialize(two) == before, "Copy/paste keeps the source profile unchanged");
        var sameTargetClipboard = KeyEditing.CopyKey(anotherSlotSameTarget, 14);
        Reject(delegate { KeyEditing.Paste(two, new[] { 60 }, sameTargetClipboard, CopyPart.All, "player2"); }, "Collapsing multiple source routes onto the same output/slot is rejected atomically");
        var preservedSlots = KeyEditing.Paste(two, new[] { 60 }, sameTargetClipboard, CopyPart.All);
        Check(preservedSlots.Bindings.Count(b => b.KeyIndex == 60 && b.Target == OutputTarget.A) == 2, "Paste can preserve same output on two separate controller slots");
        var duplicateClipboard = new List<Binding> { sourceOne[0], new Binding { KeyIndex = sourceOne[0].KeyIndex, Target = sourceOne[0].Target, ControllerId = sourceOne[0].ControllerId } };
        Reject(delegate { KeyEditing.Paste(two, new[] { 60 }, duplicateClipboard, CopyPart.All); }, "A duplicate source clipboard is rejected before editing destinations");
        Check(ProfileJson.Serialize(two) == before, "Rejected duplicate pastes preserve existing destination IDs/settings");
        foreach (KeyboardSuppressionMode mode in Enum.GetValues(typeof(KeyboardSuppressionMode)))
        {
            Profile suppressed = ProfileJson.Clone(two); suppressed.KeyboardSuppressionMode = mode; suppressed.SuppressedKeyboardKeys.Add(14);
            foreach (Profile edited in new[] {
                ControllerRouting.ForController(suppressed, "main"), ControllerRouting.ForController(suppressed, "player2"),
                ControllerRouting.Add(suppressed, "third", "Player 3", ControllerKind.Xbox360),
                ControllerRouting.Remove(suppressed, "player2"), ControllerRouting.Rename(suppressed, "main", "Renamed"),
                ControllerRouting.SetKind(suppressed, "player2", ControllerKind.Xbox360), ControllerRouting.SetRgbColor(suppressed, "main", 0x123456) })
                Check(edited.KeyboardSuppressionMode == mode && edited.SuppressedKeyboardKeys.SequenceEqual(new[] { 14 }), "Route selection and controller edits preserve the complete suppression preference");
        }
        return "PASS: " + assertions + " pure controller-routing checks; no devices started.";
    }
}
