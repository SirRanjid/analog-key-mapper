using System;
using System.Collections;
using System.Collections.Generic;
using Tk75.Mapping;

public static class MappingAssignmentsHarness
{
    static int checks;
    static void Check(bool condition, string message)
    { checks++; if (!condition) throw new InvalidOperationException("FAIL: " + message); }
    static void Reject(Action action, string message)
    { bool rejected = false; try { action(); } catch (Exception) { rejected = true; } Check(rejected, message); }
    sealed class Keys : IEnumerable<int>
    {
        readonly bool fail;
        public int Enumerations;
        public Keys(bool fail) { this.fail = fail; }
        public IEnumerator<int> GetEnumerator()
        {
            if (++Enumerations != 1) throw new InvalidOperationException("Synthetic source was enumerated twice");
            yield return 14;
            if (fail) throw new InvalidOperationException("Synthetic source interrupted");
            yield return 9; yield return 14;
        }
        IEnumerator IEnumerable.GetEnumerator() { return GetEnumerator(); }
    }
    public static string Run()
    {
        checks = 0;
        var original = new Profile { Controller = ControllerKind.DualSense, Name = "Assignment source" };
        original.Bindings.Add(new Binding { BindingId = "existing", KeyIndex = 14, Target = OutputTarget.A, Enabled = false });
        original.Bindings[0].Processing.Scale = .7;
        original.Inputs.Add(new KeyInputSettings { KeyIndex = 14, RapidTriggerEnabled = true, OppositeKeyIndex = 9, OppositePolicy = InputOpposedPolicy.LastPressed });
        original.Inputs.Add(new KeyInputSettings { KeyIndex = 9, OppositeKeyIndex = 14, OppositePolicy = InputOpposedPolicy.LastPressed });
        string before = ProfileJson.Serialize(original);
        var keys = new Keys(false);
        Profile added = MappingAssignments.Add(original, keys, OutputTarget.RightTrigger);
        Check(keys.Enumerations == 1, "Enumerates one-shot source exactly once");
        Check(added.Bindings.Count == 3, "Repeated source key contributes one new binding per gesture");
        Check(added.Bindings[1].KeyIndex == 14 && added.Bindings[2].KeyIndex == 9, "Distinct keys preserve first occurrence order");
        Check(added.Bindings[0].BindingId == "existing" && added.Bindings[0].Target == OutputTarget.A && !added.Bindings[0].Enabled && added.Bindings[0].Processing.Scale == .7, "Existing assignments stay intact");
        Check(added.Controller == ControllerKind.DualSense && added.Name == original.Name, "Assignment keeps profile identity and selected controller");
        Check(added.Inputs.Count == 2 && added.Inputs[0].RapidTriggerEnabled && added.Inputs[0].OppositeKeyIndex == 9 && added.Inputs[1].OppositeKeyIndex == 14 && added.Inputs[0].OppositePolicy == InputOpposedPolicy.LastPressed, "Assignment preserves per-key activation and reciprocal SOCD");
        Check(ProfileJson.Serialize(original) == before, "Successful assignment does not mutate source");
        foreach (Binding binding in added.Bindings.GetRange(1, 2))
        {
            Check(binding.Target == OutputTarget.RightTrigger && binding.Enabled, "Each appended binding targets the requested output and starts enabled");
            Check(binding.Processing.TopDeadzone == 0 && binding.Processing.BottomDeadzone == 0 && binding.Processing.MinOutput == 0 && binding.Processing.MaxOutput == 1 && binding.Processing.Scale == 1 && binding.Processing.Curve == CurveKind.Linear && binding.Processing.ButtonThreshold == .5, "New binding uses default signal settings");
        }
        Check(!object.ReferenceEquals(added, original) && !object.ReferenceEquals(added.Inputs[0], original.Inputs[0]) && !object.ReferenceEquals(added.Bindings[0], original.Bindings[0]) && !object.ReferenceEquals(added.Bindings[0].Processing.CustomPoints[0], original.Bindings[0].Processing.CustomPoints[0]), "Result deeply detaches inputs, bindings and curve points");
        Check(!object.ReferenceEquals(added.Bindings[1].Processing, added.Bindings[2].Processing) && !object.ReferenceEquals(added.Bindings[1].Processing.CustomPoints[1], added.Bindings[2].Processing.CustomPoints[1]), "New bindings do not share mutable signal settings");
        Profile repeated = MappingAssignments.Add(added, new[] { 14, 9 }, OutputTarget.RightTrigger);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (Binding binding in repeated.Bindings) ids.Add(binding.BindingId);
        Check(repeated.Bindings.Count == 3 && ids.Count == 3 && ProfileJson.Serialize(repeated) == ProfileJson.Serialize(added), "Repeated gesture is a no-op retaining every ID and setting");
        Profile disabledRepeat = MappingAssignments.Add(original, new[] { 14 }, OutputTarget.A);
        Check(ProfileJson.Serialize(disabledRepeat) == before, "Disabled existing assignment also makes repeated Add a no-op");
        Profile mixedRepeat = MappingAssignments.Add(original, new[] { 14, 9 }, OutputTarget.A);
        Check(mixedRepeat.Bindings.Count == 2 && mixedRepeat.Bindings[0].BindingId == "existing" && !mixedRepeat.Bindings[0].Enabled && mixedRepeat.Bindings[1].KeyIndex == 9, "Mixed gesture keeps an existing binding and adds only the missing key");
        var noopHistory = new EditHistory(original);
        noopHistory.Commit(disabledRepeat);
        Check(!noopHistory.CanUndo, "Duplicate Add does not create an undo entry");
        var history = new EditHistory(original);
        history.Commit(added);
        Check(history.Undo() && history.Current.Bindings.Count == 1 && history.Current.Controller == ControllerKind.DualSense, "Undo removes the entire assignment gesture and preserves controller choice");
        Check(history.Redo() && history.Current.Bindings.Count == 3 && history.Current.Inputs[0].RapidTriggerEnabled, "Redo restores entire gesture with per-key settings");
        added.Inputs[0].ActuationPoint = .8; added.Bindings[0].Processing.CustomPoints[1].Y = .8;
        added.Bindings[1].Processing.Scale = .4;
        Check(ProfileJson.Serialize(original) == before && added.Bindings[2].Processing.Scale == 1, "Editing the result cannot alter source or sibling settings");

        foreach (OutputTarget target in Enum.GetValues(typeof(OutputTarget)))
        {
            var result = MappingAssignments.Add(original, new[] { 0, 255 }, target);
            Check(result.Bindings.Count == 3 && result.Bindings[1].KeyIndex == 0 && result.Bindings[2].KeyIndex == 255 && result.Bindings[1].Target == target && result.Bindings[2].Target == target, "Every output accepts boundary physical key indices: " + target);
        }
        Reject(delegate { MappingAssignments.Add(original, null, OutputTarget.A); }, "Null source keys rejected");
        Reject(delegate { MappingAssignments.Add(original, new int[0], OutputTarget.A); }, "Empty gesture rejected");
        Reject(delegate { MappingAssignments.Add(original, new[] { 14, -1 }, OutputTarget.A); }, "Negative key rejects entire gesture");
        Reject(delegate { MappingAssignments.Add(original, new[] { 14, 256 }, OutputTarget.A); }, "Out-of-range key rejects entire gesture");
        Reject(delegate { MappingAssignments.Add(original, new[] { 14 }, (OutputTarget)99); }, "Unknown output rejected");
        Reject(delegate { MappingAssignments.Add(null, new[] { 14 }, OutputTarget.A); }, "Missing source profile rejected");
        var invalid = ProfileJson.Clone(original); invalid.Controller = (ControllerKind)99;
        Reject(delegate { MappingAssignments.Add(invalid, new[] { 14 }, OutputTarget.A); }, "Invalid source controller rejected");
        invalid = ProfileJson.Clone(original); invalid.Bindings[0].Processing.Scale = double.NaN;
        Reject(delegate { MappingAssignments.Add(invalid, new[] { 14 }, OutputTarget.A); }, "Invalid source signal processing rejected");
        Reject(delegate { MappingAssignments.Add(original, new Keys(true), OutputTarget.A); }, "Interrupted enumeration rejects gesture atomically");
        Check(ProfileJson.Serialize(original) == before, "All rejected gestures leave source unchanged");

        var full = new Profile { Controller = ControllerKind.DualSense };
        for (int i = 0; i < 4095; i++) full.Bindings.Add(new Binding { BindingId = "limit-" + i, KeyIndex = i % 256, Target = (OutputTarget)(i / 256) });
        var atLimit = MappingAssignments.Add(full, new[] { 255, 255, 255 }, OutputTarget.RB);
        Check(atLimit.Bindings.Count == 4096 && full.Bindings.Count == 4095, "Exactly 4096 bindings allowed after deduplicating the gesture");
        string beforeFull = ProfileJson.Serialize(full);
        Reject(delegate { MappingAssignments.Add(full, new[] { 14, 9 }, OutputTarget.Back); }, "Gesture crossing binding limit rejected as a whole");
        Check(ProfileJson.Serialize(full) == beforeFull, "Limit rejection never partially appends keys");
        Check(ProfileJson.Serialize(MappingAssignments.Add(atLimit, new[] { 255 }, OutputTarget.RB)) == ProfileJson.Serialize(atLimit), "Full profile still permits a duplicate no-op");
        Reject(delegate { MappingAssignments.Add(atLimit, new[] { 14 }, OutputTarget.Back); }, "Already full profile rejects a genuinely new assignment");
        Check(atLimit.Bindings.Count == 4096 && atLimit.Controller == ControllerKind.DualSense, "Full profile remains intact after rejection");
        return "PASS: " + checks + " pure MappingAssignments checks; no UI, files, native code or controller.";
    }
}
