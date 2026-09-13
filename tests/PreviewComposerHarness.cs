using System;
using System.Collections.Generic;
using Tk75.Mapping;

public static class PreviewComposerHarness
{
    static int checks;
    static void Check(bool value, string message)
    { checks++; if (!value) throw new Exception(message); }
    static void Near(double expected, double actual, string message)
    { Check(!Double.IsNaN(actual) && !Double.IsInfinity(actual) && Math.Abs(expected - actual) < 1e-10, message + ": " + actual); }
    static Binding Bind(string id, int key, OutputTarget target)
    { return new Binding { BindingId = id, KeyIndex = key, Target = target }; }
    static Profile ProfileFor(params Binding[] bindings)
    { var profile = new Profile(); profile.Bindings.AddRange(bindings); return profile; }
    static Dictionary<int, Calibration> Cals()
    { var result = new Dictionary<int, Calibration>(); for (int key = 0; key <= 255; key++) result.Add(key, new Calibration(0, 1)); return result; }
    static PreviewSnapshot Preview(Profile profile, IDictionary<int, double> raw)
    { return MappingEngine.ComposePreview(raw, Cals(), profile, new Dictionary<string, SignalState>(), new Dictionary<int, KeyInputState>(), .01); }
    static void Neutral(PreviewSnapshot value, string message)
    {
        Check(!value.HasValidInput && value.AvailableBindingCount == 0 && value.LeftX == 0 && value.LeftY == 0 && value.RightX == 0 &&
            value.RightY == 0 && value.LeftTrigger == 0 && value.RightTrigger == 0 && value.Buttons == 0, message);
    }
    static void StrictNeutral(ControllerFrame value, string message)
    {
        Check(value.Errors.Count > 0 && value.LeftX == 0 && value.LeftY == 0 && value.RightX == 0 && value.RightY == 0 &&
            value.LeftTrigger == 0 && value.RightTrigger == 0 && value.Buttons == 0, message);
        foreach (SignalResult result in value.BindingResults.Values) Check(result.Final == 0, message + " binding final zero");
    }
    static void PartialInputs()
    {
        var p = ProfileFor(Bind("a", 1, OutputTarget.LeftTrigger), Bind("b", 2, OutputTarget.RightTrigger));
        var raw = new Dictionary<int, double> { { 1, .75 } };
        var preview = Preview(p, raw);
        Check(preview.HasValidInput && preview.AvailableBindingCount == 1, "one missing key does not invalidate healthy binding");
        Near(.75, preview.LeftTrigger, "known trigger remains visible");
        Near(0, preview.RightTrigger, "unknown trigger absent");
        Check(preview.Errors.Count == 1 && preview.UnavailableKeys.Count == 1 && preview.UnavailableKeys[0] == 2, "unknown key reported explicitly");
        Check(preview.BindingResults["a"].IsValid && !preview.BindingResults["b"].IsValid && preview.InputResults[1].Allowed, "healthy per-binding and physical results preserved");
        Check(!raw.ContainsKey(2), "no invented raw sample written into source");
        var strictStates = new Dictionary<string, SignalState>(); var strictInputs = new Dictionary<int, KeyInputState>();
        StrictNeutral(MappingEngine.Compose(raw, Cals(), p, strictStates, strictInputs, .01), "real output still fails closed on identical partial snapshot");
        foreach (SignalState state in strictStates.Values) Check(!state.IsPressed && state.SmoothedValue == 0, "strict state reset remains");
        foreach (KeyInputState state in strictInputs.Values) Check(!state.Active && state.PressOrder == 0, "strict input reset remains");
        raw[2] = 0;
        var released = Preview(p, raw);
        Check(released.HasValidInput && released.AvailableBindingCount == 2 && released.Errors.Count == 0, "confirmed release counts as valid input");
        Near(.75, released.LeftTrigger, "release does not suppress other trigger");
        raw[2] = .8;
        var both = Preview(p, raw);
        Near(.75, both.LeftTrigger, "first simultaneous trigger"); Near(.8, both.RightTrigger, "second simultaneous trigger");
        raw.Remove(1); // Represents an expired positive omitted by ReaderSession.
        var expired = Preview(p, raw);
        Near(0, expired.LeftTrigger, "expired input immediately removed"); Near(.8, expired.RightTrigger, "other fresh input remains");
        Check(expired.UnavailableKeys.Count == 1 && expired.UnavailableKeys[0] == 1, "expired key remains unknown rather than released");
        raw.Clear();
        Neutral(Preview(p, raw), "all unavailable inputs give neutral waiting snapshot");
        Neutral(Preview(p, null), "null raw dictionary produces no made-up releases");
        raw[1] = Double.NaN; raw[2] = .6;
        Near(.6, Preview(p, raw).RightTrigger, "nonfinite raw isolated from valid input");
        var badCal = Cals(); badCal[1] = new Calibration(1, 1); raw[1] = .7;
        var invalidCal = MappingEngine.ComposePreview(raw, badCal, p, new Dictionary<string, SignalState>(), new Dictionary<int, KeyInputState>(), .01);
        Near(.6, invalidCal.RightTrigger, "invalid calibration isolated from other key");
        Check(invalidCal.UnavailableKeys.Contains(1), "bad calibration key marked unavailable");
        p.Bindings[1].Enabled = false;
        raw.Remove(2);
        var disabled = Preview(p, raw);
        Check(disabled.AvailableBindingCount == 1 && disabled.Errors.Count == 0 && !disabled.InputResults.ContainsKey(2), "disabled binding does not require raw input");
    }
    static void PersistentStates()
    {
        var a = Bind("a", 1, OutputTarget.LeftTrigger); a.Processing.SmoothingTimeConstant = .5;
        var p = ProfileFor(a, Bind("missing", 2, OutputTarget.RightTrigger));
        var raw = new Dictionary<int, double> { { 1, .9 } };
        var states = new Dictionary<string, SignalState>(); var inputs = new Dictionary<int, KeyInputState>();
        double previous = 0;
        for (int i = 0; i < 50; i++)
        {
            var value = MappingEngine.ComposePreview(raw, Cals(), p, states, inputs, .01);
            Check(value.LeftTrigger > previous && value.LeftTrigger < .9, "smoothing progresses despite permanently unavailable other key");
            previous = value.LeftTrigger;
            Near(.9 * (1 - Math.Exp(-.01 * (i + 1) / .5)), value.LeftTrigger, "smoothing matches independent analytic filter");
            Check(inputs[1].Active && inputs[1].PressOrder == 1, "healthy press order is not reset on unrelated missing data");
        }
        raw.Remove(1);
        Neutral(MappingEngine.ComposePreview(raw, Cals(), p, states, inputs, .01), "missing previously held key removes its preview");
        Check(!states["a"].IsPressed && states["a"].SmoothedValue == 0 && !inputs[1].Active, "missing key clears its own persistent state");
        raw[1] = .9;
        Near(.9 * (1 - Math.Exp(-.01 / .5)), MappingEngine.ComposePreview(raw, Cals(), p, states, inputs, .01).LeftTrigger, "returning key starts its own filter fresh");
        raw[1] = 0;
        Near(0, MappingEngine.ComposePreview(raw, Cals(), p, states, inputs, .01).LeftTrigger, "release remains immediate despite smoothing");

        a.Processing.SmoothingTimeConstant = 0;
        p.Inputs.Add(new KeyInputSettings { KeyIndex = 1, RapidTriggerEnabled = true, ActuationPoint = .2, PressMovement = .1, ReleaseMovement = .1 });
        states.Clear(); inputs.Clear(); raw[1] = .6;
        Near(.6, MappingEngine.ComposePreview(raw, Cals(), p, states, inputs, .01).LeftTrigger, "RT actuates with unrelated missing key");
        raw[1] = .48;
        Near(0, MappingEngine.ComposePreview(raw, Cals(), p, states, inputs, .01).LeftTrigger, "RT remembers peak and releases on partial movement");
        Check(inputs[1].HasActuated && !inputs[1].Active, "RT history persists after partial release");
        raw[1] = .53;
        Near(0, MappingEngine.ComposePreview(raw, Cals(), p, states, inputs, .01).LeftTrigger, "RT waits for actual repress distance");
        raw[1] = .59;
        Near(0, MappingEngine.ComposePreview(raw, Cals(), p, states, inputs, .01).LeftTrigger, "A shorter legacy repress value cannot activate the preview prematurely");
        raw[1] = .68;
        Near(.68, MappingEngine.ComposePreview(raw, Cals(), p, states, inputs, .01).LeftTrigger, "RT preview reuses actuation as its movement from the saved valley");

        var two = ProfileFor(Bind("first", 1, OutputTarget.LeftTrigger), Bind("second", 1, OutputTarget.RightTrigger));
        states.Clear(); inputs.Clear(); states.Add("first", new SignalState { IsPressed = true, SmoothedValue = Double.NaN });
        raw[1] = .7;
        var corruptBinding = MappingEngine.ComposePreview(raw, Cals(), two, states, inputs, .01);
        Near(0, corruptBinding.LeftTrigger, "invalid binding state is neutral");
        Near(.7, corruptBinding.RightTrigger, "invalid binding state does not invalidate another binding of same key");
        Check(corruptBinding.AvailableBindingCount == 1 && corruptBinding.Errors.Count > 0, "invalid binding state reported separately");
        states.Add("removed", new SignalState { IsPressed = true }); inputs.Add(200, new KeyInputState());
        MappingEngine.ComposePreview(raw, Cals(), two, states, inputs, .01);
        Check(!states.ContainsKey("removed") && !inputs.ContainsKey(200), "obsolete preview states pruned");
    }
    static Profile Paired(InputOpposedPolicy policy)
    {
        var p = ProfileFor(Bind("left", 1, OutputTarget.LeftXNegative), Bind("right", 2, OutputTarget.LeftXPositive), Bind("other", 3, OutputTarget.RightTrigger));
        p.Inputs.Add(new KeyInputSettings { KeyIndex = 1, OppositeKeyIndex = 2, OppositePolicy = policy });
        p.Inputs.Add(new KeyInputSettings { KeyIndex = 2, OppositeKeyIndex = 1, OppositePolicy = policy });
        return p;
    }
    static void SocdAndRoutes()
    {
        foreach (InputOpposedPolicy policy in Enum.GetValues(typeof(InputOpposedPolicy)))
        {
            var p = Paired(policy);
            var states = new Dictionary<string, SignalState>(); var inputs = new Dictionary<int, KeyInputState>();
            var raw = new Dictionary<int, double> { { 1, .8 }, { 3, .7 } };
            var unknownPartner = MappingEngine.ComposePreview(raw, Cals(), p, states, inputs, .01);
            Near(0, unknownPartner.LeftX, "unknown SOCD partner suppresses the whole pair");
            Near(.7, unknownPartner.RightTrigger, "unknown SOCD pair does not suppress unrelated key");
            Check(unknownPartner.UnavailableKeys.Count == 2 && unknownPartner.UnavailableKeys.Contains(1) && unknownPartner.UnavailableKeys.Contains(2), "both members unavailable as policy pair");
            Check(unknownPartner.AvailableBindingCount == 1 && !inputs[1].Active && !inputs[2].Active && inputs[1].PressOrder == 0, "unknown pair history cleared");
            raw[2] = 0;
            Near(-.8, MappingEngine.ComposePreview(raw, Cals(), p, states, inputs, .01).LeftX, "pair works immediately when confirmed partner release arrives");
            raw[2] = .9;
            double expected = policy == InputOpposedPolicy.Neutral ? 0 : policy == InputOpposedPolicy.LastPressed ? .9 : -.8;
            Near(expected, MappingEngine.ComposePreview(raw, Cals(), p, states, inputs, .01).LeftX, "SOCD policy uses persistent press order");
            raw.Remove(2);
            var stalePartner = MappingEngine.ComposePreview(raw, Cals(), p, states, inputs, .01);
            Near(0, stalePartner.LeftX, "stale partner never becomes an inferred release");
            Near(.7, stalePartner.RightTrigger, "fresh independent input survives stale pair");
            raw[2] = .9;
            Near(0, MappingEngine.ComposePreview(raw, Cals(), p, states, inputs, .01).LeftX, "returning held pair begins together and stays neutral on tie");
        }
        var missingUnboundPartner = Paired(InputOpposedPolicy.LastPressed);
        missingUnboundPartner.Bindings.RemoveAt(1);
        var unbound = Preview(missingUnboundPartner, new Dictionary<int, double> { { 1, .8 }, { 3, .7 } });
        Near(0, unbound.LeftX, "required partner still needed without its own output binding");
        Near(.7, unbound.RightTrigger, "unbound missing partner isolated from unrelated key");

        var multiple = ControllerRouting.Add(ProfileFor(Bind("main", 1, OutputTarget.LeftTrigger)), "other", "Second", ControllerKind.Xbox360);
        multiple.Bindings.Add(new Binding { BindingId = "remote", KeyIndex = 2, Target = OutputTarget.LeftTrigger, ControllerId = "other" });
        var rawSlots = new Dictionary<int, double> { { 1, .6 } };
        Neutral(Preview(multiple, rawSlots), "unrouted multi-controller profile rejected by preview");
        var main = Preview(ControllerRouting.ForController(multiple, "main"), rawSlots);
        Check(main.Errors.Count == 0 && main.AvailableBindingCount == 1, "route does not require another player raw keys");
        Near(.6, main.LeftTrigger, "routed healthy player remains visible");
        Neutral(Preview(ControllerRouting.ForController(multiple, "other"), rawSlots), "other route remains unknown without its input");
    }
    static void AggregationAndValidation()
    {
        var p = ProfileFor(Bind("a", 1, OutputTarget.LeftXPositive), Bind("b", 2, OutputTarget.LeftXPositive),
            Bind("c", 3, OutputTarget.LeftYPositive), Bind("up", 4, OutputTarget.DpadUp), Bind("down", 5, OutputTarget.DpadDown),
            Bind("button", 6, OutputTarget.A), Bind("missing", 7, OutputTarget.RightTrigger));
        var raw = new Dictionary<int, double> { { 1, .3 }, { 2, .4 }, { 3, .5 }, { 4, 1 }, { 5, 1 }, { 6, .7 } };
        var preview = Preview(p, raw);
        Near(.4, preview.LeftX, "partial preview preserves maximum aggregation"); Near(.5, preview.LeftY, "independent healthy axis");
        Check(preview.Buttons == 0x1000, "opposed Dpad neutralized without dropping healthy A button");
        p.Aggregation = AggregationMode.ClampedSum;
        Near(.7, Preview(p, raw).LeftX, "partial preview preserves summed aggregation");
        raw[1] = 1; raw[2] = 1; raw[3] = 1;
        Near(1 / Math.Sqrt(2), Preview(p, raw).LeftX, "partial preview preserves circle stick shaping");
        p.StickShape = StickShape.Square;
        Near(1, Preview(p, raw).LeftX, "partial preview preserves square stick range");
        p.Bindings[0].Processing.Curve = CurveKind.Bezier;
        p.Bindings[0].Processing.CustomPoints = new List<CurvePoint> { new CurvePoint(0, 0, 0), new CurvePoint(1, 1, 0) };
        raw[1] = .25; raw[2] = 0; raw[3] = 0;
        Near(.15625, Preview(p, raw).LeftX, "partial preview uses actual Bezier stage");
        raw[7] = 0;
        var complete = Preview(p, raw);
        var output = MappingEngine.Compose(raw, Cals(), p, new Dictionary<string, SignalState>(), new Dictionary<int, KeyInputState>(), .01);
        Check(complete.Errors.Count == 0 && output.Errors.Count == 0, "complete snapshot valid for both independent calculations");
        Near(output.LeftX, complete.LeftX, "complete preview and strict output calculations match");
        Check(output.Buttons == complete.Buttons, "complete preview button composition matches strict");

        var states = new Dictionary<string, SignalState> { { "a", new SignalState { IsPressed = true, SmoothedValue = .8 } } };
        var inputs = new Dictionary<int, KeyInputState> { { 1, new KeyInputState { Active = true, PressOrder = 1 } } };
        var invalid = ProfileJson.Clone(p); invalid.Bindings[0].Processing.CustomPoints[1].Tangent = Double.NaN;
        var invalidSnapshot = MappingEngine.ComposePreview(raw, Cals(), invalid, states, inputs, .01);
        Neutral(invalidSnapshot, "structurally invalid profile makes entire preview neutral");
        Check(invalidSnapshot.Errors.Count > 0 && !states["a"].IsPressed && !inputs[1].Active, "structural failure resets preview states");
        Neutral(MappingEngine.ComposePreview(raw, Cals(), p, null, inputs, .01), "missing state dictionary rejected");
        Neutral(MappingEngine.ComposePreview(raw, Cals(), p, states, null, .01), "missing physical state dictionary rejected");
        Neutral(MappingEngine.ComposePreview(raw, Cals(), p, states, inputs, Double.NaN), "invalid time rejected");
        Neutral(Preview(null, raw), "null profile rejected");
        Neutral(Preview(new Profile(), raw), "no bindings gives no valid-input claim");
        Check(!typeof(ControllerFrame).IsAssignableFrom(typeof(PreviewSnapshot)), "partial snapshot cannot be submitted as ControllerFrame");
        foreach (var field in typeof(PreviewSnapshot).GetFields()) Check(field.FieldType != typeof(ControllerFrame), "snapshot exposes no output frame field");
    }
    static void DetachedSnapshot()
    {
        var p = ProfileFor(Bind("a", 1, OutputTarget.LeftTrigger), Bind("missing", 2, OutputTarget.RightTrigger));
        var snapshot = Preview(p, new Dictionary<int, double> { { 1, .65 } });
        var copy = snapshot.Copy();
        Near(.65, copy.LeftTrigger, "copy retains scalar values");
        Check(copy.HasValidInput && copy.AvailableBindingCount == 1 && copy.Errors.Count == 1 && copy.UnavailableKeys.Count == 1, "copy retains count and diagnostic information");
        copy.BindingResults["a"].Final = 0; copy.InputResults[1].Normalized = 0; copy.Errors.Clear(); copy.UnavailableKeys.Clear();
        copy.LeftTrigger = 0; copy.AvailableBindingCount = 0;
        Near(.65, snapshot.BindingResults["a"].Final, "copy binding results detached");
        Near(.65, snapshot.InputResults[1].Normalized, "copy physical results detached");
        Check(snapshot.Errors.Count == 1 && snapshot.UnavailableKeys.Count == 1 && snapshot.AvailableBindingCount == 1, "copy lists and counts detached");
        Neutral(new PreviewSnapshot().Copy(), "empty snapshot copies as neutral");
    }
    public static string Run()
    {
        checks = 0; PartialInputs(); PersistentStates(); SocdAndRoutes(); AggregationAndValidation(); DetachedSnapshot();
        return "PASS: " + checks + " pure partial-preview, persistent RT/SOCD/filter, routing, strict-output and detached-snapshot checks; no workers/native/GUI/HID.";
    }
}
