using System;
using System.Collections.Generic;
using Tk75.Mapping;

public static class DefaultCalibrationHarness
{
    static int assertions;
    static void Check(bool condition, string message)
    { assertions++; if (!condition) throw new Exception(message); }
    static void Near(double expected, double actual, string message)
    { Check(!Double.IsNaN(actual) && Math.Abs(expected - actual) < 1e-10, message + ": " + actual); }
    static Profile ProfileFor(params Binding[] bindings)
    { var profile = new Profile(); profile.Bindings.AddRange(bindings); return profile; }
    static Binding Bind(string id, int key, OutputTarget target)
    { return new Binding { BindingId = id, KeyIndex = key, Target = target }; }
    static ControllerFrame Frame(IDictionary<int, double> raw, IDictionary<int, Calibration> calibrations, Profile profile)
    { return MappingEngine.Compose(raw, calibrations, profile, new Dictionary<string, SignalState>(), new Dictionary<int, KeyInputState>(), .01); }
    static void Neutral(ControllerFrame frame, string message)
    {
        Check(frame.Errors.Count > 0, message + " reports missing/invalid input");
        Check(frame.LeftX == 0 && frame.LeftY == 0 && frame.RightX == 0 && frame.RightY == 0 &&
            frame.LeftTrigger == 0 && frame.RightTrigger == 0 && frame.Buttons == 0, message + " stays neutral");
    }

    public static string Run()
    {
        assertions = 0;
        var defaults = DefaultCalibration.Resolve(null);
        Check(defaults.Count == 256, "Every valid index has a standard range");
        bool allStandard = true, independent = true;
        var objects = new HashSet<Calibration>();
        for (int key = 0; key <= 255; key++)
        {
            Calibration value;
            allStandard &= defaults.TryGetValue(key, out value) && value.Rest == 0 && value.Bottom == 385 &&
                !value.UsableMin.HasValue && !value.UsableMax.HasValue && !value.MeasuredTravel.HasValue;
            independent &= objects.Add(value);
        }
        Check(allStandard, "All 256 ranges are 0..385, without fabricated physical travel");
        Check(independent, "Each key owns its range object");
        var profile = ProfileFor(Bind("last-key", 255, OutputTarget.LeftTrigger));
        var raw = new Dictionary<int, double> { { 255, 192.5 } };
        Neutral(Frame(raw, new Dictionary<int, Calibration>(), profile), "The original missing-calibration path");
        var half = Frame(raw, defaults, profile);
        Check(half.Errors.Count == 0, "An uncalibrated valid key now maps with the standard range");
        Near(.5, half.LeftTrigger, "Half travel remains analog");
        foreach (double sample in new[] { -20.0, 0.0, 385.0, 700.0 })
        {
            raw[255] = sample;
            Near(sample <= 0 ? 0 : 1, Frame(raw, defaults, profile).LeftTrigger, "Standard endpoints and clamping");
        }

        var measured = new Calibration(50, 450) { UsableMin = 0, UsableMax = 500, MeasuredTravel = 3.6 };
        var stored = new Dictionary<int, Calibration> { { 255, measured }, { 0, new Calibration(385, 0) } };
        var resolved = DefaultCalibration.Resolve(stored);
        raw[255] = 150;
        Near(.25, Frame(raw, resolved, profile).LeftTrigger, "Measured range overrides the standard");
        Check(resolved[255].UsableMin == 0 && resolved[255].UsableMax == 500 && resolved[255].MeasuredTravel == 3.6,
            "All measured calibration fields survive");
        var reverseProfile = ProfileFor(Bind("reverse", 0, OutputTarget.RightTrigger));
        Near(1, Frame(new Dictionary<int, double> { { 0, 0 } }, resolved, reverseProfile).RightTrigger,
            "Measured reverse sensor direction has priority");
        Check(stored.Count == 2 && Object.ReferenceEquals(measured, stored[255]) && measured.Rest == 50 && measured.Bottom == 450,
            "Resolving does not write defaults or changes to stored entries");
        Check(!Object.ReferenceEquals(measured, resolved[255]), "Measured entries are detached copies");
        resolved[255].Bottom = 999; resolved[1].Bottom = 999; resolved.Remove(0);
        Near(450, measured.Bottom, "Changing a resolved calibration leaves stored calibration untouched");
        Near(385, resolved[2].Bottom, "Changing one default leaves other keys untouched");
        var resolvedAgain = DefaultCalibration.Resolve(stored);
        Near(450, resolvedAgain[255].Bottom, "Next resolve uses the original stored value");
        Near(385, resolvedAgain[1].Bottom, "Next resolve creates fresh defaults");
        Check(resolvedAgain.ContainsKey(0), "Removing a resolved entry does not remove a stored entry");

        foreach (int index in new[] { -1, 256 })
        {
            bool rejected = false;
            try { DefaultCalibration.Resolve(new Dictionary<int, Calibration> { { index, new Calibration(0, 385) } }); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "Invalid calibration index is rejected: " + index);
        }
        foreach (Calibration invalid in new[] { (Calibration)null, new Calibration(1, 1), new Calibration(Double.NaN, 385) })
        {
            var invalidResolved = DefaultCalibration.Resolve(new Dictionary<int, Calibration> { { 255, invalid } });
            Neutral(Frame(raw, invalidResolved, profile), "Existing invalid calibration is not hidden");
        }

        var multiple = ProfileFor(Bind("left", 0, OutputTarget.LeftTrigger), Bind("right", 255, OutputTarget.RightTrigger),
            Bind("button", 37, OutputTarget.A), Bind("duplicate", 255, OutputTarget.LeftYPositive));
        var simultaneous = new Dictionary<int, double> { { 0, 96.25 }, { 255, 288.75 }, { 37, 385 } };
        var several = Frame(simultaneous, defaults, multiple);
        Check(several.Errors.Count == 0, "Simultaneous keys work without measured calibration");
        Near(.25, several.LeftTrigger, "First key keeps independent depth");
        Near(.75, several.RightTrigger, "Second key keeps independent depth");
        Near(.75, several.LeftY, "One key can still feed a second target");
        Check(several.Buttons == 0x1000, "Third key activates a digital controller button");
        Check(simultaneous.Count == 3 && simultaneous[0] == 96.25 && simultaneous[255] == 288.75 && simultaneous[37] == 385,
            "Defaults and mapping never populate or modify the raw sample dictionary");

        var opposed = ProfileFor(Bind("a", 14, OutputTarget.LeftXNegative), Bind("d", 16, OutputTarget.LeftXPositive));
        opposed.Inputs.Add(new KeyInputSettings { KeyIndex = 14, OppositeKeyIndex = 16, OppositePolicy = InputOpposedPolicy.LastPressed });
        opposed.Inputs.Add(new KeyInputSettings { KeyIndex = 16, OppositeKeyIndex = 14, OppositePolicy = InputOpposedPolicy.LastPressed });
        var signals = new Dictionary<string, SignalState>(); var inputs = new Dictionary<int, KeyInputState>();
        var opposedRaw = new Dictionary<int, double> { { 14, 385 }, { 16, 0 } };
        var before = MappingEngine.Compose(opposedRaw, defaults, opposed, signals, inputs, .01);
        Near(-1, before.LeftX, "SOCD sees the first default-calibrated key");
        opposedRaw[16] = 192.5;
        var after = MappingEngine.Compose(opposedRaw, defaults, opposed, signals, inputs, .01);
        Check(after.Errors.Count == 0, "SOCD works with standard ranges");
        Near(.5, after.LeftX, "Last pressed wins while retaining analog magnitude");

        // Expired/unknown keys arrive as absent after reader freshness filtering.
        // Defaults supply ranges only: even a previously active state must release.
        opposedRaw.Remove(16);
        var missing = MappingEngine.Compose(opposedRaw, defaults, opposed, signals, inputs, .01);
        Neutral(missing, "Missing/expired raw sample after an active frame");
        Check(!inputs[14].Active && !inputs[16].Active, "Missing current data resets previous input state");
        Check(!opposedRaw.ContainsKey(16), "No release or positive sample is fabricated for the missing key");
        Neutral(Frame(new Dictionary<int, double>(), defaults, profile), "Empty raw snapshot");
        Neutral(Frame(null, defaults, profile), "Missing raw snapshot");
        raw[255] = Double.NaN; Neutral(Frame(raw, defaults, profile), "Nonfinite raw sample");
        return "PASS: " + assertions + " pure default-calibration checks; no device, GUI, driver or file access.";
    }
}
