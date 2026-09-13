using System;
using System.Collections.Generic;
using Tk75.Mapping;

public static class CurveResponsePreviewHarness
{
    static int checks;
    static void Check(bool condition, string reason) { checks++; if (!condition) throw new Exception(reason); }
    static void Near(double expected, double actual, string reason) { Check(Math.Abs(expected - actual) < 1e-10, reason + ": " + actual); }
    public static void Run()
    {
        var settings = new SignalSettings { TopDeadzone = .1, BottomDeadzone = .2, Scale = .8, OutputDeadzone = .1, MinOutput = .15, MaxOutput = .85, Hysteresis = .05, SmoothingTimeConstant = .05 };
        var response = CurveResponsePreview.Create(settings, 1000);
        Near(0, response.Press[100], "Released input bypasses min output");
        Near(0, response.Press[150], "Hysteresis enters strictly above the press boundary");
        double expected = .15 + (((.4 - .1) / .7 * .8 - .1) / .9) * .7;
        Near(expected, response.Press[400], "Preview combines calibrated travel, input deadzones, scale, output deadzone and output limits");
        Near(.15 + (.8 - .1) / .9 * .7, response.Press[800], "Bottom deadzone reaches the true scaled output early");
        Near(response.Press[800], response.Press[1000], "Bottom deadzone plateau is retained");
        Near(.05, settings.SmoothingTimeConstant, "Steady preview never rewrites the user's smoothing");
        var noFilter = CurveResponsePreview.Copy(settings); noFilter.SmoothingTimeConstant = 0;
        var direct = CurveResponsePreview.Create(noFilter, 1000);
        for (int i = 0; i <= 1000; i++) Near(direct.Press[i], response.Press[i], "Steady response does not invent a key movement speed");

        var gate = new SignalSettings { TopDeadzone = .1, Hysteresis = .2, MinOutput = .2 };
        response = CurveResponsePreview.Create(gate, 1000);
        Near(0, response.Press[200], "Press sweep remains inactive inside hysteresis gap");
        Check(response.Release[200] > .2, "Release sweep retains held-key history inside hysteresis gap");
        Near(0, response.Release[100], "Release sweep switches off at rest deadzone");
        Near(response.Press[500], response.Release[500], "Steady responses meet beyond hysteresis");

        foreach (CurveKind kind in Enum.GetValues(typeof(CurveKind)))
        {
            settings.Curve = kind; settings.Exponent = 2.3;
            settings.CustomPoints = new List<CurvePoint> { new CurvePoint(0, 0), new CurvePoint(.4, .22, .6), new CurvePoint(1, 1) };
            var copied = CurveResponsePreview.Copy(settings);
            Check(CurveResponsePreview.Same(settings, copied), "Every response parameter survives detached copy for " + kind);
            copied.CustomPoints[1].Y = .3;
            Near(.22, settings.CustomPoints[1].Y, "Preview custom points do not alias the profile");
            Check(!CurveResponsePreview.Same(settings, copied), "Custom point change invalidates the preview cache");
            response = CurveResponsePreview.Create(settings, 256);
            for (int i = 0; i <= 256; i++)
            { Check(response.Press[i] >= 0 && response.Press[i] <= settings.MaxOutput, "Preview respects output bounds"); if (i > 0) Check(response.Press[i] >= response.Press[i - 1], "Press response remains monotone"); }
        }
        settings = new SignalSettings();
        foreach (var field in typeof(SignalSettings).GetFields())
        {
            if (field.FieldType != typeof(double)) continue;
            var copy = CurveResponsePreview.Copy(settings); field.SetValue(copy, (double)field.GetValue(copy) + .01);
            Check(!CurveResponsePreview.Same(settings, copy), "Every numeric setting invalidates cached output or guide: " + field.Name);
            Check(CurveResponsePreview.SameResponse(settings, copy) == (field.Name == "SmoothingTimeConstant" || field.Name == "ButtonThreshold"),
                "Only guide/time fields can reuse steady samples: " + field.Name);
        }
        var disabled = new SignalSettings { Scale = 0, MinOutput = .5 };
        response = CurveResponsePreview.Create(disabled, 100);
        for (int i = 0; i <= 100; i++) Near(0, response.Press[i], "Zero scale suppresses output despite nonzero minimum");
        Dynamics();
        Console.WriteLine("CURVE RESPONSE PASS: " + checks + " pure checks; full output processing, press/release history, settled smoothing, cache identity and detached points.");
    }
    static void Dynamics()
    {
        var source = new SignalSettings { MinOutput = .2, MaxOutput = .8, Scale = .75, SmoothingTimeConstant = .1 };
        var step = SmoothingStepPreview.Create(source, 1, 1000);
        double target = .2 + .75 * .6;
        Near(target, step.SettledOutput, "Time preview includes the true output target");
        Near(0, step.Output[0], "Filtered step begins at zero before time advances");
        Near(target * (1 - Math.Exp(-1)), step.Output[100], "One time constant reaches 63 percent of the actual target");
        Near(target * (1 - Math.Exp(-2)), step.Output[200], "Time preview follows the actual exponential filter");
        var slower = CurveResponsePreview.Copy(source); slower.SmoothingTimeConstant = .2;
        var slowStep = SmoothingStepPreview.Create(slower, 1, 1000);
        Check(slowStep.Output[100] < step.Output[100], "Increasing smoothing visibly slows the response on the unchanged one-second axis");
        Near(.1, source.SmoothingTimeConstant, "Step illustration leaves real settings unchanged");
        slower.SmoothingTimeConstant = 0;
        Near(target, SmoothingStepPreview.Create(slower, 1, 100).Output[0], "Zero smoothing is immediate");
        foreach (double release in new[] { .01, .2, .75, 1.0 }) foreach (double press in new[] { .01, .25, .8, 1.0 })
        {
            var input = new KeyInputSettings { RapidTriggerEnabled = true, ActuationPoint = press, ReleaseMovement = release, PressMovement = .037 };
            var up = RapidTriggerMovementPreview.Create(input, InputActivationFields.Release);
            Near(release, up.FromPressure - up.ToPressure, "Release arrow is a relative travel distance");
            Check(up.FromPressure >= input.ActuationPoint && up.ToPressure >= 0, "Release example first actuates and stays in the key's range");
            var down = RapidTriggerMovementPreview.Create(input, InputActivationFields.Press);
            Near(input.ActuationPoint, down.ToPressure - down.FromPressure, "Retrigger illustration reuses actuation and ignores a differing legacy press value");
            Check(down.FromPressure >= 0 && down.ToPressure <= 1, "Repress example stays inside calibrated travel");
            Check(down.RequiresFreshActuation == (release == 1 || press == 1), "Full-release cases explicitly reset RT instead of fabricating a retrigger");
            if (!down.RequiresFreshActuation)
                Check(down.FromPressure > 0 && 1 - down.FromPressure >= release, "Repress example releases far enough to deactivate without reaching rest");
        }
    }
}
