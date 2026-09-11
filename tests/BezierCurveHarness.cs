using System;
using System.Collections.Generic;
using Tk75.Mapping;

public static class BezierCurveHarness
{
    static int checks;
    static void Check(bool value, string message)
    { checks++; if (!value) throw new Exception(message); }
    static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
    static void Near(double expected, double actual, string message, double tolerance = 1e-10)
    { Check(Finite(actual) && Math.Abs(expected - actual) <= tolerance, message + " (actual " + actual + ")"); }
    static void Reject(Action action, string message)
    {
        bool rejected = false;
        try { action(); } catch (ArgumentException) { rejected = true; }
        Check(rejected, message);
    }
    static List<CurvePoint> Copy(IList<CurvePoint> points)
    {
        var result = new List<CurvePoint>();
        foreach (CurvePoint p in points) result.Add(new CurvePoint(p.X, p.Y, p.Tangent));
        return result;
    }
    static void Unchanged(IList<CurvePoint> before, IList<CurvePoint> after, string message)
    {
        Check(before.Count == after.Count, message + " count");
        for (int i = 0; i < before.Count; i++)
            Check(before[i].X == after[i].X && before[i].Y == after[i].Y && before[i].Tangent == after[i].Tangent, message + " point " + i);
    }
    static SignalSettings Settings(List<CurvePoint> points)
    { return new SignalSettings { Curve = CurveKind.Bezier, CustomPoints = points }; }
    static Profile ProfileFor(List<CurvePoint> points)
    {
        var p = new Profile();
        p.Bindings.Add(new Binding { BindingId = "first", KeyIndex = 1, Target = OutputTarget.LeftTrigger, Processing = Settings(Copy(points)) });
        p.Bindings.Add(new Binding { BindingId = "second", KeyIndex = 2, Target = OutputTarget.RightTrigger, Processing = Settings(Copy(points)) });
        return p;
    }
    static void ShapeChecks(List<CurvePoint> points, bool exactHandles)
    {
        var before = Copy(points);
        double[] tangents = BezierCurve.EffectiveTangents(points);
        Near(0, BezierCurve.Evaluate(points, -5), "below input range");
        Near(1, BezierCurve.Evaluate(points, 5), "above input range");
        double previous = -1;
        for (int i = 0; i < points.Count; i++)
        {
            CurvePoint p = points[i];
            Near(p.Y, BezierCurve.Evaluate(points, p.X), "interpolates knot");
            Check(Finite(tangents[i]) && tangents[i] >= 0, "finite nonnegative effective slope");
            Near(tangents[i], BezierCurve.EffectiveTangent(points, i), "single and batch slope match");
            foreach (bool outgoing in new[] { false, true })
            {
                CurvePoint handle = BezierCurve.GetHandle(points, i, outgoing);
                if (!outgoing && i == 0 || outgoing && i == points.Count - 1)
                { Check(handle == null, "outer handle absent"); continue; }
                CurvePoint left = points[outgoing ? i : i - 1], right = points[outgoing ? i + 1 : i];
                Check(handle != p && Finite(handle.X) && Finite(handle.Y) && handle.X >= left.X && handle.X <= right.X &&
                    handle.Y >= left.Y && handle.Y <= right.Y, "handle finite and inside segment range");
                if (exactHandles)
                {
                    double fromHandle = 3 * (outgoing ? handle.Y - p.Y : p.Y - handle.Y) / (right.X - left.X);
                    Near(tangents[i], fromHandle, "both handle sides use exact shared derivative", 2e-9);
                }
                handle.Y = -100;
            }
            if (i == points.Count - 1) continue;
            double width = points[i + 1].X - p.X;
            for (int step = 0; step <= 64; step++)
            {
                double x = p.X + width * (step / 64.0);
                double value = BezierCurve.Evaluate(points, x);
                Check(Finite(value) && value >= 0 && value <= 1 && value + 2e-14 >= previous, "monotone bounded finite cubic samples");
                previous = value;
            }
        }
        Unchanged(before, points, "math APIs leave stored point and tangent data unchanged");
        tangents[0] = -10;
        Check(BezierCurve.EffectiveTangent(points, 0) >= 0, "effective tangent array is detached");
    }
    static void Arithmetic()
    {
        Check((int)CurveKind.Custom == 4 && (int)CurveKind.Bezier == 5, "legacy enum values unchanged");
        var line = new List<CurvePoint> { new CurvePoint(0, 0), new CurvePoint(.2, .2), new CurvePoint(.75, .75), new CurvePoint(1, 1) };
        for (int i = 0; i <= 100; i++) Near(i / 100.0, BezierCurve.Evaluate(line, i / 100.0), "automatic collinear points remain linear");
        var smooth = new List<CurvePoint> { new CurvePoint(0, 0, 0), new CurvePoint(1, 1, 0) };
        for (int i = 0; i <= 100; i++)
        { double x = i / 100.0; Near(x * x * (3 - 2 * x), BezierCurve.Evaluate(smooth, x), "zero handles produce actual cubic smoothstep"); }
        ShapeChecks(smooth, true);
        // All four vertices of the allowed endpoint-slope box have a
        // nonnegative derivative; random interior slopes are tested below.
        foreach (double first in new[] { 0.0, 3.0 }) foreach (double last in new[] { 0.0, 3.0 })
            ShapeChecks(new List<CurvePoint> { new CurvePoint(0, 0, first), new CurvePoint(1, 1, last) }, true);
        var knots = new List<CurvePoint> { new CurvePoint(0, 0, .4), new CurvePoint(.2, .08), new CurvePoint(.55, .72, .5), new CurvePoint(1, 1, 0) };
        ShapeChecks(knots, true);
        for (int i = 1; i < knots.Count - 1; i++)
        {
            double h = Math.Min(knots[i].X - knots[i - 1].X, knots[i + 1].X - knots[i].X) * 1e-6;
            double at = BezierCurve.Evaluate(knots, knots[i].X);
            double left = (at - BezierCurve.Evaluate(knots, knots[i].X - h)) / h;
            double right = (BezierCurve.Evaluate(knots, knots[i].X + h) - at) / h;
            Near(left, right, "C1 derivative from actual evaluation", 3e-5);
            Near(BezierCurve.EffectiveTangent(knots, i), left, "evaluation derivative matches shared slope", 3e-5);
        }
        var flat = new List<CurvePoint> { new CurvePoint(0, 0), new CurvePoint(.2, .4, Double.MaxValue), new CurvePoint(.6, .4, Double.MaxValue), new CurvePoint(1, 1) };
        Near(0, BezierCurve.EffectiveTangent(flat, 1), "flat segment forces incoming shared slope zero");
        Near(0, BezierCurve.EffectiveTangent(flat, 2), "flat segment forces outgoing shared slope zero");
        for (int i = 0; i <= 100; i++) Near(.4, BezierCurve.Evaluate(flat, .2 + .4 * i / 100), "flat remains exactly flat");
        ShapeChecks(flat, true);
        var boundaryFlats = new List<CurvePoint> { new CurvePoint(0, 0, Double.MaxValue), new CurvePoint(.15, 0, Double.MaxValue), new CurvePoint(.7, 1, Double.MaxValue), new CurvePoint(1, 1, Double.MaxValue) };
        ShapeChecks(boundaryFlats, true);
        var extremes = new List<CurvePoint> { new CurvePoint(0, 0, Double.MaxValue), new CurvePoint(Double.Epsilon, .2, Double.MaxValue), new CurvePoint(1e-300, .25), new CurvePoint(1e-12, .4), new CurvePoint(.5, .5), new CurvePoint(1 - 1e-15, .8, Double.MaxValue), new CurvePoint(1, 1, Double.MaxValue) };
        ShapeChecks(extremes, false);
        ShapeChecks(new List<CurvePoint> { new CurvePoint(0, 0), new CurvePoint(.2, Double.Epsilon), new CurvePoint(.5, Double.Epsilon * 2), new CurvePoint(1, 1) }, false);
        double clamped = BezierCurve.ClampTangent(knots, 1, Double.MaxValue);
        var edited = Copy(knots); edited[1].Tangent = clamped;
        Near(clamped, BezierCurve.EffectiveTangent(edited, 1), "clamped drag is stable after storage");
        Check(!knots[1].Tangent.HasValue, "clamp preserves automatic source");
        edited[1].Tangent = null;
        Near(BezierCurve.EffectiveTangent(knots, 1), BezierCurve.EffectiveTangent(edited, 1), "null restores auto tangent");

        var random = new Random(58231);
        for (int example = 0; example < 90; example++)
        {
            int count = 3 + random.Next(12);
            var xs = new List<double>(); var ys = new List<double>();
            for (int i = 1; i < count - 1; i++) { xs.Add(random.NextDouble()); ys.Add(random.NextDouble()); }
            xs.Sort(); ys.Sort();
            var points = new List<CurvePoint> { new CurvePoint(0, 0) };
            for (int i = 0; i < xs.Count; i++) points.Add(new CurvePoint(xs[i], ys[i]));
            points.Add(new CurvePoint(1, 1));
            for (int i = 0; i < points.Count; i++)
            {
                int mode = random.Next(4);
                if (mode == 0) points[i].Tangent = 0;
                if (mode == 1) points[i].Tangent = random.NextDouble() * 30;
                if (mode == 2) points[i].Tangent = Double.MaxValue;
                if (i > 0 && i < points.Count - 1 && random.Next(5) == 0) points[i].Y = points[i - 1].Y;
            }
            ShapeChecks(points, true);
        }
    }
    static void InvalidData()
    {
        var points = new List<CurvePoint> { new CurvePoint(0, 0), new CurvePoint(.5, .3), new CurvePoint(1, 1) };
        foreach (double bad in new[] { Double.NaN, Double.PositiveInfinity, Double.NegativeInfinity })
        {
            Reject(() => BezierCurve.Evaluate(points, bad), "nonfinite input rejected");
            Reject(() => BezierCurve.ClampTangent(points, 1, bad), "nonfinite drag rejected");
            var invalid = Copy(points); invalid[1].Tangent = bad;
            Reject(() => BezierCurve.EffectiveTangents(invalid), "nonfinite stored tangent rejected");
            Check(MappingValidation.ValidateSettings(Settings(invalid)).Count > 0, "validation rejects nonfinite tangent");
            var state = new SignalState { IsPressed = true, SmoothedValue = .7 };
            var result = SignalProcessor.Process(.5, new Calibration(0, 1), Settings(invalid), state, .01);
            Check(!result.IsValid && result.Final == 0 && !state.IsPressed && state.SmoothedValue == 0, "invalid tangent fails closed in signal pipeline");
        }
        Reject(() => BezierCurve.ClampTangent(points, 1, -1), "negative drag rejected");
        var negative = Copy(points); negative[1].Tangent = -1;
        Reject(() => BezierCurve.Evaluate(negative, .3), "negative stored slope rejected");
        Check(MappingValidation.ValidateSettings(Settings(negative)).Count > 0, "negative slope invalid in profile");
        Reject(() => BezierCurve.Evaluate(null, .2), "missing points rejected");
        Reject(() => BezierCurve.Evaluate(new List<CurvePoint>(), .2), "empty points rejected");
        Reject(() => BezierCurve.EffectiveTangent(points, -1), "negative index rejected");
        Reject(() => BezierCurve.GetHandle(points, 3, true), "missing knot index rejected");
        var unordered = Copy(points); unordered[1].X = 0;
        Reject(() => BezierCurve.Evaluate(unordered, .2), "duplicate X rejected");
        var falling = Copy(points); falling[1].Y = 1.1;
        Reject(() => BezierCurve.Evaluate(falling, .2), "out of range Y rejected");
        var badEndpoint = Copy(points); badEndpoint[0].Y = .01;
        Reject(() => BezierCurve.Evaluate(badEndpoint, .2), "nonfixed endpoint rejected");
        var missingPoint = Copy(points); missingPoint[1] = null;
        Reject(() => BezierCurve.Evaluate(missingPoint, .2), "null point rejected");
    }
    static void ProfilesAndPipeline()
    {
        const string legacy = "{\"Version\":1,\"Name\":\"old\",\"Bindings\":[{\"BindingId\":\"legacy\",\"KeyIndex\":1,\"Target\":8,\"Processing\":{\"Curve\":4,\"CustomPoints\":[{\"X\":0,\"Y\":0},{\"X\":0.5,\"Y\":0.25},{\"X\":1,\"Y\":1}]}}]}";
        Profile old = ProfileJson.Deserialize(legacy);
        Check(old.Bindings[0].Processing.Curve == CurveKind.Custom && !old.Bindings[0].Processing.CustomPoints[1].Tangent.HasValue, "legacy piecewise-linear profile and omitted auto tangent preserved");
        string oldRoundtrip = ProfileJson.Serialize(old);
        Check(!oldRoundtrip.Contains("Tangent"), "null tangents do not add fields to legacy JSON");
        Near(.125, SignalProcessor.Process(.25, new Calibration(0, 1), old.Bindings[0].Processing, new SignalState(), .01).Final, "legacy custom output remains linear between knots");
        var points = new List<CurvePoint> { new CurvePoint(0, 0, 0), new CurvePoint(.4, .2), new CurvePoint(1, 1, 2) };
        var profile = ProfileFor(points);
        string original = ProfileJson.Serialize(profile);
        Profile roundtrip = ProfileJson.Deserialize(original);
        Check(roundtrip.Bindings[0].Processing.Curve == CurveKind.Bezier && roundtrip.Bindings[0].Processing.CustomPoints[0].Tangent == 0 &&
            !roundtrip.Bindings[0].Processing.CustomPoints[1].Tangent.HasValue && roundtrip.Bindings[0].Processing.CustomPoints[2].Tangent == 2, "manual zero/manual positive/auto slopes roundtrip");
        Check(ProfileJson.Serialize(roundtrip) == original, "new profile roundtrip stable");
        roundtrip.Bindings[0].Processing.CustomPoints[0].Tangent = 1;
        Check(profile.Bindings[0].Processing.CustomPoints[0].Tangent == 0, "profile clone detaches tangents");
        var nullable = ProfileJson.Deserialize(original.Replace("\"Tangent\":0", "\"Tangent\":null"));
        Check(!nullable.Bindings[0].Processing.CustomPoints[0].Tangent.HasValue, "explicit JSON null means auto");
        Reject(() => ProfileJson.Deserialize(original.Replace("\"Tangent\":0", "\"Tangent\":\"0\"")), "string tangent rejected without coercion");
        Reject(() => ProfileJson.Deserialize(original.Replace("\"Tangent\":0", "\"Tangent\":-1")), "negative JSON tangent rejected");
        Reject(() => ProfileJson.Deserialize(original.Replace("\"Tangent\":0", "\"Tangent\":0,\"Tangent\":1")), "duplicate tangent JSON rejected");
        Reject(() => ProfileJson.Deserialize(original.Replace("\"Tangent\":0", "\"HandleSlope\":0")), "unknown point member still rejected");
        Reject(() => ProfileJson.Deserialize(original.Replace("\"Tangent\":0", "\"Tangent\":1e999")), "overflowing JSON tangent rejected");

        var ids = new[] { "first", "second" };
        var shared = (List<CurvePoint>)KeyEditing.MixedValue(profile, ids, "CustomPoints");
        Check(shared != null && shared[0].Tangent == 0, "shared custom points include tangent");
        shared[0].Tangent = 9;
        Check(profile.Bindings[0].Processing.CustomPoints[0].Tangent == 0, "shared result deep copy");
        var mixed = ProfileJson.Clone(profile); mixed.Bindings[1].Processing.CustomPoints[0].Tangent = null;
        Check(KeyEditing.MixedValue(mixed, ids, "CustomPoints") == null, "auto and explicit zero are different edit values");
        var applied = KeyEditing.ApplyProperty(profile, ids, "CustomPoints", shared);
        Check(applied.Bindings[0].Processing.CustomPoints[0].Tangent == 9 && applied.Bindings[1].Processing.CustomPoints[0].Tangent == 9, "property edit copies manual tangents");
        applied.Bindings[0].Processing.CustomPoints[0].Tangent = 1;
        Check(applied.Bindings[1].Processing.CustomPoints[0].Tangent == 9 && shared[0].Tangent == 9, "property copies detached per binding and caller");
        var clipboard = KeyEditing.CopyKey(profile, 1);
        var pasted = KeyEditing.Paste(profile, new[] { 2 }, clipboard, CopyPart.Curve);
        Check(pasted.Bindings[1].Processing.Curve == CurveKind.Bezier && pasted.Bindings[1].Processing.CustomPoints[2].Tangent == 2, "curve paste carries Bezier kind and slopes");
        pasted.Bindings[1].Processing.CustomPoints[2].Tangent = 0;
        Check(clipboard[0].Processing.CustomPoints[2].Tangent == 2, "curve paste does not alias clipboard");
        var all = KeyEditing.Paste(profile, new[] { 3 }, clipboard, CopyPart.All);
        Check(all.Bindings[2].Processing.Curve == CurveKind.Bezier && all.Bindings[2].Processing.CustomPoints[0].Tangent == 0, "all paste carries slopes to unmapped key");
        var appliedSettings = ProfileEditing.ApplySettings(profile, ids, Settings(points));
        appliedSettings.Bindings[0].Processing.CustomPoints[2].Tangent = 0;
        Check(appliedSettings.Bindings[1].Processing.CustomPoints[2].Tangent == 2 && points[2].Tangent == 2, "ApplySettings deep copies tangent data per binding");
        Check(ProfileJson.Serialize(profile) == original, "all editing operations preserve original profile");
        var history = new EditHistory(profile);
        history.Commit(applied);
        Check(history.Undo() && ProfileJson.Serialize(history.Current) == original, "undo restores tangent values and modes");
        Check(history.Redo() && history.Current.Bindings[0].Processing.CustomPoints[0].Tangent == 1, "redo restores manual tangent edit");

        var s = Settings(points);
        for (int i = 1; i <= 100; i++)
        {
            double x = i / 100.0;
            SignalResult actual = SignalProcessor.Process(x * 385, new Calibration(0, 385), s, new SignalState(), .01);
            Check(actual.IsValid, "Bezier signal pipeline valid");
            Near(BezierCurve.Evaluate(points, x), actual.AfterCurve, "Bezier drives real signal processor curve stage");
            Near(actual.AfterCurve, actual.Final, "unscaled controller signal matches Bezier");
        }
        var state = new SignalState { IsPressed = true, SmoothedValue = .9 };
        s.MinOutput = .2; s.Scale = 5; s.SmoothingTimeConstant = .5;
        var release = SignalProcessor.Process(0, new Calibration(0, 385), s, state, .01);
        Check(release.IsValid && release.Final == 0 && !state.IsPressed && state.SmoothedValue == 0, "full release still bypasses Bezier/minimum/filter");
        var missing = SignalProcessor.Process(Double.NaN, new Calibration(0, 385), s, state, .01);
        Check(!missing.IsValid && missing.Final == 0, "Bezier never invents missing input");
    }
    public static string Run()
    {
        checks = 0; Arithmetic(); InvalidData(); ProfilesAndPipeline();
        return "PASS: " + checks + " pure Bezier arithmetic, monotonicity, C1, validation, JSON and copy/edit checks; no native/GUI/HID access.";
    }
}
