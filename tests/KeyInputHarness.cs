using System;
using System.Collections.Generic;
using Tk75.Mapping;

public static class KeyInputHarness
{
    static int checks;
    static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    static void Near(double expected, double actual, string message) { Check(Math.Abs(expected - actual) < 1e-10, message + ": " + actual); }
    static void Reject(Action action, string message)
    { bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } Check(rejected, message); }
    sealed class Session
    {
        public Profile Profile = new Profile();
        public Dictionary<int, double> Raw = new Dictionary<int, double>();
        public Dictionary<int, Calibration> Calibrations = new Dictionary<int, Calibration>();
        public Dictionary<string, SignalState> Signals = new Dictionary<string, SignalState>();
        public Dictionary<int, KeyInputState> Inputs = new Dictionary<int, KeyInputState>();
        public void Add(int key, string id, OutputTarget target)
        {
            Profile.Bindings.Add(new Binding { BindingId = id, KeyIndex = key, Target = target });
            Raw[key] = 0; Calibrations[key] = new Calibration(0, 1);
        }
        public ControllerFrame Frame()
        { return MappingEngine.Compose(Raw, Calibrations, Profile, Signals, Inputs, .01); }
        public ControllerFrame At(int key, double value) { Raw[key] = value; return Frame(); }
    }
    static Session Pair(InputOpposedPolicy policy)
    {
        var s = new Session(); s.Add(14, "left-trigger", OutputTarget.LeftTrigger); s.Add(14, "a", OutputTarget.A);
        s.Add(21, "right-trigger", OutputTarget.RightTrigger); s.Add(21, "b", OutputTarget.B);
        s.Profile = KeyInputEditing.Configure(s.Profile, new KeyInputSettings { KeyIndex = 14, OppositeKeyIndex = 21, OppositePolicy = policy });
        return s;
    }
    sealed class OneShotKeys : IEnumerable<int>
    {
        readonly int[] keys; bool used;
        public OneShotKeys(params int[] values) { keys = values; }
        public IEnumerator<int> GetEnumerator()
        {
            if (used) throw new InvalidOperationException("Selection was enumerated more than once.");
            used = true; return ((IEnumerable<int>)keys).GetEnumerator();
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() { return GetEnumerator(); }
    }
    static KeyInputSettings Input(Profile profile, int key)
    { return profile.Inputs.Find(delegate(KeyInputSettings input) { return input.KeyIndex == key; }); }
    static void SameActivation(KeyInputSettings expected, KeyInputSettings actual, string message)
    {
        Check(actual != null && expected.RapidTriggerEnabled == actual.RapidTriggerEnabled, message + ": rapid trigger");
        Near(expected.ActuationPoint, actual.ActuationPoint, message + ": actuation");
        Near(expected.PressMovement, actual.PressMovement, message + ": press movement");
        Near(expected.ReleaseMovement, actual.ReleaseMovement, message + ": release movement");
    }
    static void Unpaired(KeyInputSettings input, string message)
    { Check(input != null && !input.OppositeKeyIndex.HasValue && input.OppositePolicy == InputOpposedPolicy.Neutral, message); }
    static void BulkInputEditingChecks()
    {
        var original = new Profile();
        original.Bindings.Add(new Binding { BindingId = "unchanged-binding", KeyIndex = 1, Target = OutputTarget.LeftTrigger });
        original.SuppressedKeyboardKeys.Add(1);
        original.Inputs.Add(new KeyInputSettings { KeyIndex = 1, RapidTriggerEnabled = true, ActuationPoint = .11,
            PressMovement = .03, ReleaseMovement = .04, OppositeKeyIndex = 2, OppositePolicy = InputOpposedPolicy.FirstPressed });
        original.Inputs.Add(new KeyInputSettings { KeyIndex = 2, ActuationPoint = .22,
            PressMovement = .05, ReleaseMovement = .06, OppositeKeyIndex = 1, OppositePolicy = InputOpposedPolicy.FirstPressed });
        original.Inputs.Add(new KeyInputSettings { KeyIndex = 3, ActuationPoint = .33,
            PressMovement = .07, ReleaseMovement = .08, OppositeKeyIndex = 4, OppositePolicy = InputOpposedPolicy.LastPressed });
        original.Inputs.Add(new KeyInputSettings { KeyIndex = 4, RapidTriggerEnabled = true, ActuationPoint = .44,
            PressMovement = .09, ReleaseMovement = .10, OppositeKeyIndex = 3, OppositePolicy = InputOpposedPolicy.LastPressed });
        original.Inputs.Add(new KeyInputSettings { KeyIndex = 5, RapidTriggerEnabled = true, ActuationPoint = .55,
            PressMovement = .11, ReleaseMovement = .12 });
        string baseline = ProfileJson.Serialize(original);
        var source = new KeyInputSettings { KeyIndex = 70, RapidTriggerEnabled = false, ActuationPoint = .6,
            PressMovement = .13, ReleaseMovement = .14, OppositeKeyIndex = 71, OppositePolicy = InputOpposedPolicy.Neutral };
        foreach (InputActivationFields fields in new[] { InputActivationFields.RapidTrigger, InputActivationFields.Actuation,
            InputActivationFields.Press, InputActivationFields.Release, InputActivationFields.Actuation | InputActivationFields.Release,
            InputActivationFields.All })
        {
            Profile edited = KeyInputEditing.ApplyActivation(original, new OneShotKeys(1, 3), source, fields);
            foreach (int key in new[] { 1, 3 })
            {
                KeyInputSettings before = Input(original, key), after = Input(edited, key);
                Check(after.RapidTriggerEnabled == (((fields & InputActivationFields.RapidTrigger) != 0) ? source.RapidTriggerEnabled : before.RapidTriggerEnabled), "Selected activation flags preserve mixed rapid-trigger values");
                Near(((fields & InputActivationFields.Actuation) != 0) ? source.ActuationPoint : before.ActuationPoint, after.ActuationPoint, "Only selected actuation changes");
                Near(((fields & InputActivationFields.Press) != 0) ? source.PressMovement : before.PressMovement, after.PressMovement, "Only selected press movement changes");
                Near(((fields & InputActivationFields.Release) != 0) ? source.ReleaseMovement : before.ReleaseMovement, after.ReleaseMovement, "Only selected release movement changes");
                Check(after.OppositeKeyIndex == before.OppositeKeyIndex && after.OppositePolicy == before.OppositePolicy, "Activation editing retains each destination's own pair and policy");
            }
            foreach (int key in new[] { 2, 4, 5 }) SameActivation(Input(original, key), Input(edited, key), "Unselected activation remains unchanged");
            Check(Input(edited, 2).OppositeKeyIndex == 1 && Input(edited, 4).OppositeKeyIndex == 3, "External pairs remain reciprocal after activation edits");
            Check(ProfileJson.Serialize(original) == baseline, "Every activation edit is atomic and leaves source untouched");
        }
        Profile all = KeyInputEditing.ApplyActivation(original, new[] { 1, 3 }, source);
        Check(ProfileJson.Serialize(all) == ProfileJson.Serialize(KeyInputEditing.ApplyActivation(original, new[] { 1, 3 }, source, InputActivationFields.All)), "Existing activation API retains all-fields behavior");
        source.ActuationPoint = .9;
        Near(.6, Input(all, 1).ActuationPoint, "Returned activation settings do not alias their source");
        Input(all, 1).PressMovement = .8;
        Near(.13, Input(all, 3).PressMovement, "Edited destinations are independent of each other");
        var mixedSource = new KeyInputSettings { KeyIndex = -1, ActuationPoint = .7, PressMovement = Double.NaN,
            ReleaseMovement = 0, OppositeKeyIndex = -1, OppositePolicy = (InputOpposedPolicy)999 };
        Profile partial = KeyInputEditing.ApplyActivation(original, new[] { 1, 6 }, mixedSource, InputActivationFields.Actuation);
        Near(.7, Input(partial, 1).ActuationPoint, "Unselected mixed/invalid draft fields are not copied or validated");
        Near(.7, Input(partial, 6).ActuationPoint, "A selected missing input receives only explicit activation changes");
        Near(new KeyInputSettings().PressMovement, Input(partial, 6).PressMovement, "New input retains defaults for untouched activation values");
        Check(Double.IsNaN(mixedSource.PressMovement) && mixedSource.KeyIndex == -1 && mixedSource.OppositeKeyIndex == -1, "Reading mixed source never mutates it");
        Profile none = KeyInputEditing.ApplyActivation(original, new OneShotKeys(1, 6), mixedSource, InputActivationFields.None);
        Check(ProfileJson.Serialize(none) == baseline && Input(none, 6) == null, "No selected fields is a detached no-op without new physical gates");
        Input(none, 1).ActuationPoint = .9;
        Check(ProfileJson.Serialize(original) == baseline, "No-op result does not alias original profile");
        foreach (IEnumerable<int> invalid in new IEnumerable<int>[] { null, new int[0], new[] { 1, 1 }, new[] { 1, -1 }, new[] { 1, 256 } })
        {
            IEnumerable<int> selected = invalid;
            Reject(delegate { KeyInputEditing.ApplyActivation(original, selected, source); }, "Activation rejects invalid selection atomically");
            Reject(delegate { KeyInputEditing.Remove(original, selected); }, "Bulk removal rejects invalid selection atomically");
            Reject(delegate { KeyInputEditing.SetPairing(original, selected, null, InputOpposedPolicy.Neutral); }, "Pairing rejects invalid selection atomically");
        }
        Reject(delegate { KeyInputEditing.ApplyActivation(original, new[] { 1, 3 }, null); }, "Missing activation source rejected");
        Reject(delegate { KeyInputEditing.ApplyActivation(original, new[] { 1, 3 }, source, (InputActivationFields)16); }, "Unknown activation flags rejected");
        Reject(delegate { KeyInputEditing.ApplyActivation(original, new[] { 1, 3 }, mixedSource, InputActivationFields.Press); }, "Invalid selected activation value rejects complete edit");

        Profile removed = KeyInputEditing.Remove(original, new OneShotKeys(1, 3));
        Check(removed.Inputs.Count == 3 && Input(removed, 1) == null && Input(removed, 3) == null, "Bulk removal removes every chosen physical setting");
        foreach (int key in new[] { 2, 4, 5 })
        { SameActivation(Input(original, key), Input(removed, key), "Removal preserves other activation values"); Unpaired(Input(removed, key), "Removal unpairs external partners"); }
        Check(removed.Bindings.Count == 1 && removed.Bindings[0].BindingId == "unchanged-binding" && removed.SuppressedKeyboardKeys.Count == 1 && removed.SuppressedKeyboardKeys[0] == 1, "Removing physical settings preserves mappings and keyboard suppression");
        Check(ProfileJson.Serialize(KeyInputEditing.Remove(original, 1)) == ProfileJson.Serialize(KeyInputEditing.Remove(original, new[] { 1 })), "Scalar removal remains compatible");
        removed = KeyInputEditing.Remove(original, new[] { 1, 2 });
        Check(removed.Inputs.Count == 3 && Input(removed, 3).OppositeKeyIndex == 4, "Removing an entire pair leaves unrelated pair intact");

        Profile paired = KeyInputEditing.SetPairing(original, new OneShotKeys(1, 3), 3, InputOpposedPolicy.Neutral);
        Check(Input(paired, 1).OppositeKeyIndex == 3 && Input(paired, 3).OppositeKeyIndex == 1 && Input(paired, 1).OppositePolicy == InputOpposedPolicy.Neutral && Input(paired, 3).OppositePolicy == InputOpposedPolicy.Neutral, "Two selected keys become one reciprocal pair");
        Unpaired(Input(paired, 2), "First previous external partner is detached"); Unpaired(Input(paired, 4), "Second previous external partner is detached");
        foreach (int key in new[] { 1, 2, 3, 4, 5 }) SameActivation(Input(original, key), Input(paired, key), "Pairing preserves all affected activation parameters");
        Profile singlePair = KeyInputEditing.SetPairing(original, new OneShotKeys(1), 3, InputOpposedPolicy.LastPressed);
        Check(Input(singlePair, 1).OppositeKeyIndex == 3 && Input(singlePair, 3).OppositeKeyIndex == 1 && Input(singlePair, 1).OppositePolicy == InputOpposedPolicy.LastPressed, "One selected key may pair with a distinct external key");
        SameActivation(Input(original, 3), Input(singlePair, 3), "Single-key pairing preserves existing external partner activation");
        Profile reverse = KeyInputEditing.SetPairing(original, new OneShotKeys(3, 1), 1, InputOpposedPolicy.FirstPressed);
        Check(Input(reverse, 3).OppositeKeyIndex == 1 && Input(reverse, 1).OppositeKeyIndex == 3, "Two-key pairing honors selected order without sorting");
        Profile newPair = KeyInputEditing.SetPairing(original, new[] { 6, 7 }, 7, InputOpposedPolicy.LastPressed);
        SameActivation(new KeyInputSettings(), Input(newPair, 6), "Previously unconfigured first partner gets defaults");
        SameActivation(new KeyInputSettings(), Input(newPair, 7), "Previously unconfigured second partner gets defaults");
        Check(Input(newPair, 6).OppositeKeyIndex == 7 && Input(newPair, 7).OppositeKeyIndex == 6, "Pairing may create both reciprocal inputs");
        Profile unpaired = KeyInputEditing.SetPairing(original, new[] { 1, 3 }, null, InputOpposedPolicy.Neutral);
        foreach (int key in new[] { 1, 2, 3, 4 })
        { SameActivation(Input(original, key), Input(unpaired, key), "Unpairing preserves all activation parameters"); Unpaired(Input(unpaired, key), "Null opposite unpairs both selected keys and their partners"); }
        Check(ProfileJson.Serialize(KeyInputEditing.SetPairing(original, new[] { 6, 7 }, null, InputOpposedPolicy.Neutral)) == baseline, "Unpairing absent keys does not introduce physical gates");
        Reject(delegate { KeyInputEditing.SetPairing(original, new[] { 1, 3, 5 }, null, InputOpposedPolicy.Neutral); }, "More than two selected keys cannot silently unpair");
        Reject(delegate { KeyInputEditing.SetPairing(original, new[] { 1, 3, 5 }, 3, InputOpposedPolicy.Neutral); }, "Many keys cannot share one partner");
        Reject(delegate { KeyInputEditing.SetPairing(original, new[] { 1 }, 1, InputOpposedPolicy.Neutral); }, "Single-key self pairing rejected");
        Reject(delegate { KeyInputEditing.SetPairing(original, new[] { 1, 3 }, 1, InputOpposedPolicy.Neutral); }, "Two-key pairing requires the second selected key as opposite");
        Reject(delegate { KeyInputEditing.SetPairing(original, new[] { 1, 3 }, 5, InputOpposedPolicy.Neutral); }, "Two-key selection rejects an external opposite");
        Reject(delegate { KeyInputEditing.SetPairing(original, new[] { 1 }, 256, InputOpposedPolicy.Neutral); }, "Out-of-range opposite rejected");
        Reject(delegate { KeyInputEditing.SetPairing(original, new[] { 1 }, -1, InputOpposedPolicy.Neutral); }, "Negative opposite rejected");
        Reject(delegate { KeyInputEditing.SetPairing(original, new[] { 1, 3 }, 3, (InputOpposedPolicy)999); }, "Unknown pairing policy rejected");
        Check(ProfileJson.Serialize(original) == baseline, "Successful and rejected bulk input edits all leave original unchanged");
        var history = new EditHistory(original); history.Commit(paired);
        Check(history.Undo() && ProfileJson.Serialize(history.Current) == baseline, "One undo restores both selected keys and old external partners");
        Check(history.Redo() && ProfileJson.Serialize(history.Current) == ProfileJson.Serialize(paired), "One redo restores the entire pairing change");
    }
    static void SuppressionMetadataChecks()
    {
        var legacy = ProfileJson.Deserialize("{\"Version\":1,\"Name\":\"Legacy\",\"Bindings\":[]}");
        Check(legacy.SuppressedKeyboardKeys != null && legacy.SuppressedKeyboardKeys.Count == 0, "Legacy profile defaults suppression metadata to an empty list");
        var session = new Session(); session.Add(14, "suppression-check", OutputTarget.LeftTrigger);
        session.Profile.SuppressedKeyboardKeys.Add(14);
        var cloned = ProfileJson.Clone(session.Profile);
        Check(cloned.SuppressedKeyboardKeys.Count == 1 && cloned.SuppressedKeyboardKeys[0] == 14 && cloned.Inputs.Count == 0, "Suppression roundtrip creates no analog input gate");
        cloned.SuppressedKeyboardKeys[0] = 21;
        Check(session.Profile.SuppressedKeyboardKeys[0] == 14, "Suppression list is deeply cloned");
        foreach (double pressure in new[] { 0, .001, .05, .099, .1, .4, 1.0 })
        {
            Near(pressure, session.At(14, pressure).LeftTrigger, "Suppression metadata preserves the legacy analog response, including below ten percent");
            Near(pressure, MappingEngine.Compose(session.Raw, session.Calibrations, session.Profile, session.Signals, .01).LeftTrigger, "Suppression metadata remains compatible with the legacy Compose overload");
        }
        var edited = KeyInputEditing.Configure(session.Profile, new KeyInputSettings { KeyIndex = 14, ActuationPoint = .2 });
        Check(edited.SuppressedKeyboardKeys.Count == 1 && edited.SuppressedKeyboardKeys[0] == 14, "Editing physical behavior retains independent suppression metadata");
        edited = KeyInputEditing.Remove(edited, 14);
        Check(edited.SuppressedKeyboardKeys.Count == 1 && edited.Inputs.Count == 0, "Removing analog input settings does not silently remove suppression choice");
        var history = new EditHistory(legacy); history.Commit(session.Profile);
        Check(history.Undo() && history.Current.SuppressedKeyboardKeys.Count == 0, "Undo restores independent suppression metadata");
        Check(history.Redo() && history.Current.SuppressedKeyboardKeys[0] == 14, "Redo restores independent suppression metadata");
        foreach (string value in new[] { "null", "{}", "true", "\"14\"", "[\"14\"]", "[true]", "[null]", "[{}]", "[[]]", "[1.5]", "[1.0]", "[1e1]", "[-1]", "[256]", "[14,14]", "[2147483648]" })
        {
            string json = "{\"Version\":1,\"Name\":\"Bad\",\"Bindings\":[],\"SuppressedKeyboardKeys\":" + value + "}";
            Reject(delegate { ProfileJson.Deserialize(json); }, "Suppression JSON rejects wrong types, duplicate and out-of-range indices");
        }
        var complete = new Profile(); for (int index = 0; index < 256; index++) complete.SuppressedKeyboardKeys.Add(index);
        Check(ProfileJson.Clone(complete).SuppressedKeyboardKeys.Count == 256, "All 256 distinct permitted indices roundtrip");
        complete.SuppressedKeyboardKeys.Add(0);
        Reject(delegate { ProfileJson.Serialize(complete); }, "In-memory oversized suppression list is rejected");
        foreach (int invalid in new[] { -1, 256 })
        {
            var profile = new Profile(); profile.SuppressedKeyboardKeys.Add(invalid);
            Reject(delegate { ProfileJson.Serialize(profile); }, "In-memory invalid suppression index is rejected");
        }
        var duplicate = new Profile(); duplicate.SuppressedKeyboardKeys.Add(14); duplicate.SuppressedKeyboardKeys.Add(14);
        Reject(delegate { ProfileJson.Serialize(duplicate); }, "In-memory duplicate suppression index is rejected");
        var missing = new Profile { SuppressedKeyboardKeys = null };
        Reject(delegate { ProfileJson.Serialize(missing); }, "Explicitly null suppression list is rejected");
    }
    public static string Run()
    {
        checks = 0;
        BulkInputEditingChecks();
        SuppressionMetadataChecks();
        var s = new Session(); s.Add(14, "one", OutputTarget.LeftTrigger); s.Add(14, "two", OutputTarget.RightTrigger);
        s.Profile = KeyInputEditing.Configure(s.Profile, new KeyInputSettings { KeyIndex = 14, RapidTriggerEnabled = true,
            ActuationPoint = .1, PressMovement = .05, ReleaseMovement = .15 });
        foreach (Binding binding in s.Profile.Bindings) { binding.Processing.MinOutput = .6; binding.Processing.SmoothingTimeConstant = .4; }
        var f = s.At(14, .09); Near(0, f.LeftTrigger, "Before initial actuation"); Check(!f.InputResults[14].Active, "Gate inactive below initial point");
        f = s.At(14, .1); Check(f.LeftTrigger > 0 && f.RightTrigger > 0, "Initial threshold enables every target");
        Check(s.Inputs.Count == 1 && s.Signals.Count == 2, "One physical state shared by two independent target filters");
        long initialOrder = s.Inputs[14].PressOrder;
        f = s.At(14, .8); Check(f.InputResults[14].Active, "Deeper movement stays active");
        s.At(14, .7); Check(s.Inputs[14].Active, "Upward movement smaller than release distance holds");
        f = s.At(14, .65); Near(0, f.LeftTrigger, "Release at exact peak distance bypasses minimum/filter"); Near(0, f.RightTrigger, "Release reaches every mapping");
        Check(!s.Signals["one"].IsPressed && s.Signals["one"].SmoothedValue == 0, "Release resets target filter");
        f = s.At(14, .69); Near(0, f.LeftTrigger, "Insufficient repress movement remains released");
        f = s.At(14, .7); Check(f.InputResults[14].Active && f.LeftTrigger > 0, "Exact repress distance triggers");
        Check(s.Inputs[14].PressOrder > initialOrder, "Repress receives a new activation order");
        s.At(14, .4); s.At(14, .02); f = s.At(14, .07);
        Check(f.InputResults[14].Active, "Continuous RT can repress below original actuation point");
        f = s.At(14, 0); Near(0, f.LeftTrigger, "Full release immediately neutral"); Check(!s.Inputs[14].HasActuated, "Full rest resets RT cycle");
        f = s.At(14, .07); Check(!f.InputResults[14].Active, "After full release initial point applies again");
        f = s.At(14, .1); Check(f.InputResults[14].Active, "Initial point restarts cycle");
        long heldOrder = s.Inputs[14].PressOrder;
        for (int i = 0; i < 200; i++) { f = s.Frame(); Check(f.InputResults[14].Active && s.Inputs[14].PressOrder == heldOrder, "Unchanged physical hold does not retrigger"); }

        foreach (InputOpposedPolicy policy in Enum.GetValues(typeof(InputOpposedPolicy)))
        {
            s = Pair(policy); s.At(14, .6); f = s.At(21, .8);
            Check(f.InputResults[14].Active && f.InputResults[21].Active, "SOCD retains physical activation evidence");
            if (policy == InputOpposedPolicy.Neutral) { Near(0, f.LeftTrigger, "Neutral suppresses first key"); Near(0, f.RightTrigger, "Neutral suppresses second key"); Check(f.Buttons == 0, "Neutral applies to buttons too"); }
            if (policy == InputOpposedPolicy.LastPressed) { Near(0, f.LeftTrigger, "Last suppresses earlier key"); Near(.8, f.RightTrigger, "Last retains latest physical value"); Check(f.Buttons == 0x2000, "Last affects all targets of both keys"); }
            if (policy == InputOpposedPolicy.FirstPressed) { Near(.6, f.LeftTrigger, "First retains earlier key"); Near(0, f.RightTrigger, "First suppresses later key"); Check(f.Buttons == 0x1000, "First affects all targets"); }
            // Releasing the winner/opponent exposes an already physically held key.
            if (policy == InputOpposedPolicy.FirstPressed) { f = s.At(14, 0); Near(.8, f.RightTrigger, "Release first winner exposes held second key"); }
            else { f = s.At(21, 0); Near(.6, f.LeftTrigger, "Release last winner exposes held first key"); }
            Check(f.Errors.Count == 0, "Valid paired sequence has no errors");
            s = Pair(policy); s.Raw[14] = .6; s.Raw[21] = .8; f = s.Frame();
            Near(0, f.LeftTrigger, "Same-frame activation is neutral"); Near(0, f.RightTrigger, "Same-frame activation has no enumeration winner");
            Check(f.Buttons == 0, "Same-frame button tie is neutral");
            f = s.At(21, 0); Near(.6, f.LeftTrigger, "Tie resolves only from remaining physical input");
        }
        s = Pair(InputOpposedPolicy.LastPressed);
        s.At(14, .6); s.At(21, .8); s.At(21, 0); f = s.At(21, .7); Near(.7, f.RightTrigger, "Physical repress wins again");
        var rapidPartner = KeyInputEditing.GetOrDefault(s.Profile, 21); rapidPartner.RapidTriggerEnabled = true;
        s.Profile = KeyInputEditing.Configure(s.Profile, rapidPartner); s.Inputs.Clear(); s.Signals.Clear();
        s.Raw[21] = 0; s.At(14, .6); s.At(21, .8); f = s.At(21, .77);
        Near(.6, f.LeftTrigger, "RT release of SOCD winner restores still-held opponent"); Near(0, f.RightTrigger, "RT release stays neutral despite positive raw depth");
        f = s.At(21, .8); Near(.8, f.RightTrigger, "RT repress participates in physical last-pressed priority");
        // Opposite input is required even when it has no controller bindings.
        s.Profile.Bindings.RemoveAll(delegate(Binding binding) { return binding.KeyIndex == 21; });
        f = s.Frame(); Near(0, f.LeftTrigger, "Unmapped configured opponent can suppress mapped input");
        s.Raw.Remove(21); f = s.Frame(); Check(f.Errors.Count > 0 && f.LeftTrigger == 0, "Missing opponent fails neutral");
        Check(!s.Inputs[14].Active && !s.Inputs[21].Active, "Missing input resets all physical states");
        foreach (SignalState state in s.Signals.Values) Check(!state.IsPressed && state.SmoothedValue == 0, "Input failure resets every target filter");

        s = new Session(); s.Add(4, "reverse", OutputTarget.LeftTrigger);
        s.Profile = KeyInputEditing.Configure(s.Profile, new KeyInputSettings { KeyIndex = 4, ActuationPoint = .2 });
        s.Calibrations[4] = new Calibration(900, -100); f = s.At(4, 700); Near(.2, f.LeftTrigger, "Input activation uses calibrated reversed travel");
        f = s.At(4, 710); Near(0, f.LeftTrigger, "Non-RT settings use fixed actuation point");
        f = s.At(4, Double.NaN); Check(f.Errors.Count > 0 && f.LeftTrigger == 0, "NaN fails neutral");
        s.At(4, 700); s.Inputs[4].Peak = Double.NaN; f = s.Frame(); Check(f.Errors.Count > 0 && !s.Inputs[4].Active, "Invalid previous input state fails neutral");
        s.At(4, 700); MappingEngine.ResetInputStates(s.Inputs); Check(!s.Inputs[4].Active && s.Inputs[4].PressOrder == 0, "Explicit input reset");
        f = MappingEngine.Compose(s.Raw, s.Calibrations, s.Profile, s.Signals, .01); Check(f.Errors.Count > 0, "Stateful settings cannot use legacy stateless overload");
        f = MappingEngine.Compose(s.Raw, s.Calibrations, s.Profile, s.Signals, null, .01); Check(f.Errors.Count > 0, "Missing physical-state dictionary rejected");
        s = Pair(InputOpposedPolicy.FirstPressed); s.At(14, .7); s.Signals["a"].SmoothedValue = Double.PositiveInfinity;
        f = s.Frame(); Check(f.Errors.Count > 0 && f.LeftTrigger == 0 && f.Buttons == 0, "Late binding-state error neutralizes composed targets");
        foreach (KeyInputState input in s.Inputs.Values) Check(!input.Active && input.PressOrder == 0, "Late binding error resets all physical gates");

        var legacy = ProfileJson.Deserialize("{\"Version\":1,\"Name\":\"Legacy\",\"Bindings\":[]}");
        Check(legacy.Inputs != null && legacy.Inputs.Count == 0, "Version1 legacy profile defaults to no input gates");
        var minimal = ProfileJson.Deserialize("{\"Version\":1,\"Name\":\"Input\",\"Bindings\":[],\"Inputs\":[{\"KeyIndex\":0}]}");
        Near(.1, minimal.Inputs[0].ActuationPoint, "Optional input defaults restored"); Near(.02, minimal.Inputs[0].PressMovement, "Press distance default restored");
        foreach (string bad in new [] {
            "{\"KeyIndex\":0,\"Unknown\":1}", "{\"KeyIndex\":0,\"KeyIndex\":1}", "{\"KeyIndex\":0,\"RapidTriggerEnabled\":1}",
            "{\"KeyIndex\":0,\"ActuationPoint\":\"0.1\"}", "{\"KeyIndex\":0,\"ActuationPoint\":null}", "{\"KeyIndex\":0,\"OppositeKeyIndex\":\"1\"}",
            "{\"KeyIndex\":0,\"OppositeKeyIndex\":0}", "{\"KeyIndex\":0,\"OppositeKeyIndex\":1}", "{}" })
        { string input = bad; Reject(delegate { ProfileJson.Deserialize("{\"Version\":1,\"Name\":\"Bad\",\"Bindings\":[],\"Inputs\":[" + input + "]}"); }, "Malformed input JSON rejected"); }
        foreach (double bad in new [] {0, -.01, 1.01, Double.NaN, Double.PositiveInfinity})
        {
            var input = new KeyInputSettings { KeyIndex = 3, ActuationPoint = bad }; Reject(delegate { KeyInputEditing.Configure(legacy, input); }, "Invalid actuation value");
            input = new KeyInputSettings { KeyIndex = 3, PressMovement = bad }; Reject(delegate { KeyInputEditing.Configure(legacy, input); }, "Invalid press distance");
            input = new KeyInputSettings { KeyIndex = 3, ReleaseMovement = bad }; Reject(delegate { KeyInputEditing.Configure(legacy, input); }, "Invalid release distance");
        }
        var original = Pair(InputOpposedPolicy.FirstPressed).Profile;
        var cloned = ProfileJson.Clone(original); cloned.Inputs[0].ActuationPoint = .7;
        Near(.1, original.Inputs[0].ActuationPoint, "Deep clone does not alias input settings");
        var history = new EditHistory(original); history.Commit(cloned); Check(history.Undo(), "Input edit undo"); Near(.1, history.Current.Inputs[0].ActuationPoint, "Undo restores input activation");
        Check(history.Redo(), "Input edit redo"); Near(.7, history.Current.Inputs[0].ActuationPoint, "Redo restores input activation");
        var changed = KeyInputEditing.Configure(original, new KeyInputSettings { KeyIndex = 14, OppositeKeyIndex = 4, OppositePolicy = InputOpposedPolicy.LastPressed });
        Check(KeyInputEditing.GetOrDefault(changed, 21).OppositeKeyIndex == null, "Changing partner unpairs old key");
        Check(KeyInputEditing.GetOrDefault(changed, 4).OppositeKeyIndex == 14, "New pair is reciprocal");
        Check(KeyInputEditing.GetOrDefault(original, 21).OppositeKeyIndex == 14, "Atomic input edits preserve source");
        changed = KeyInputEditing.Remove(changed, 14); Check(changed.Inputs.Count == 2 && KeyInputEditing.GetOrDefault(changed, 4).OppositeKeyIndex == null, "Remove unpairs partner without deleting its activation settings");
        var copy = new KeyInputSettings { KeyIndex = 14, RapidTriggerEnabled = true, ActuationPoint = .3, ReleaseMovement = .08 };
        changed = KeyInputEditing.ApplyActivation(original, new [] {14,21}, copy);
        Check(KeyInputEditing.GetOrDefault(changed, 21).OppositeKeyIndex == 14, "Activation copy preserves destination pair");
        Check(KeyInputEditing.GetOrDefault(changed, 14).RapidTriggerEnabled && KeyInputEditing.GetOrDefault(changed, 21).RapidTriggerEnabled, "Activation copy applies all selected inputs");
        copy.ActuationPoint = .9; Near(.3, KeyInputEditing.GetOrDefault(changed, 14).ActuationPoint, "Activation copy is deep");
        Reject(delegate { KeyInputEditing.ApplyActivation(original, new [] {14,14}, copy); }, "Duplicate activation copy selection rejected");
        cloned = ProfileJson.Clone(original); cloned.Inputs[0].OppositePolicy = InputOpposedPolicy.LastPressed;
        Check(MappingValidation.ValidateProfile(cloned).Count > 0, "Mismatched partner policies rejected");
        cloned = ProfileJson.Clone(original); cloned.Inputs.Add(KeyInputEditing.Clone(cloned.Inputs[0]));
        Check(MappingValidation.ValidateProfile(cloned).Count > 0, "Duplicate physical settings rejected");
        return "PASS: " + checks + " physical-input assertions; synthetic values only; no native APIs, hardware or controller output.";
    }
}
