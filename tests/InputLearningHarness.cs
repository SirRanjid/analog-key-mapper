using System;
using System.Collections.Generic;
using Tk75.Mapping;

public static class InputLearningHarness
{
    static int assertions;
    static void Check(bool value, string message)
    { assertions++; if (!value) throw new Exception("Input learning: " + message); }
    static void Near(double value, double expected, string message)
    { Check(Math.Abs(value - expected) < 1e-10, message + ": " + value + " != " + expected); }
    static void Throws(Action action, string message)
    { try { action(); } catch (ArgumentException) { assertions++; return; } throw new Exception("Input learning: " + message); }
    static InputControlDescriptor Descriptor(string id, InputControlKind kind, double minimum, double maximum, double? neutral)
    { return new InputControlDescriptor(id, id, kind, minimum, maximum, neutral, null); }
    static InputControlSample Sample(string id, double value, long timestamp)
    { return new InputControlSample(id, value, timestamp); }
    static InputLearningCapture New(InputControlDescriptor descriptor, double initial)
    { return new InputLearningCapture("device-a", new[] { descriptor }, new[] { Sample(descriptor.ControlId, initial, 0) }, 0); }
    static void Feed(InputLearningCapture capture, double value, long timestamp)
    { capture.Feed(Sample("a", value, timestamp)); }
    static void State(InputLearningCapture capture, InputLearningCaptureState expected, string message)
    { Check(capture.State == expected, message + "; state=" + capture.State); }
    static void Release(InputLearningCapture capture, double rest, long timestamp)
    { Feed(capture, rest, timestamp); capture.Advance(timestamp + InputLearningCapture.ReleaseSettleMilliseconds); }

    public static void Run()
    {
        assertions = 0;
        var button = Descriptor("a", InputControlKind.Button, 0, 1, 0);
        var analog = Descriptor("a", InputControlKind.Absolute, 0, 1000, 0);
        var capture = New(button, 0);
        State(capture, InputLearningCaptureState.WaitingForNeutral, "Opening never assigns an input");
        capture.Advance(149);
        State(capture, InputLearningCaptureState.WaitingForNeutral, "Quiet arming does not finish early");
        capture.Advance(150);
        State(capture, InputLearningCaptureState.Ready, "Known idle input arms after quiet period");
        Feed(capture, 1, 151);
        State(capture, InputLearningCaptureState.Capturing, "A fresh button press starts one gesture");
        Near(capture.LiveValue, 1, "Button preview is binary");
        Check(capture.Result == null, "A held momentary button is not an accepted assignment");
        Feed(capture, 0, 152);
        capture.Advance(231);
        State(capture, InputLearningCaptureState.Capturing, "Release waits for competing report fields");
        capture.Advance(232);
        State(capture, InputLearningCaptureState.Completed, "Release completes without per-key confirmation");
        Check(capture.Result.SourceDeviceId == "device-a" && capture.Result.ControlId == "a", "Detached result retains exact source identity");
        Near(capture.Result.Normalize(1), 1, "Learned button retains active value");
        Near(capture.Result.Normalize(.5), 0, "Intermediate invalid button values are not analog pressure");
        var saved = capture.Result;
        Feed(capture, 1, 250); capture.Advance(999);
        Check(Object.ReferenceEquals(saved, capture.Result), "Completed result cannot be silently edited by later reports");

        capture = New(button, 1);
        capture.Advance(1000); Feed(capture, 1, 1100);
        State(capture, InputLearningCaptureState.WaitingForNeutral, "A button held before capture must first be released");
        Feed(capture, 0, 1200); capture.Advance(1350);
        State(capture, InputLearningCaptureState.Ready, "Releasing an initially held button only arms capture");
        Check(capture.Result == null && capture.Control == null, "Initial hold is never staged as an assignment");
        Feed(capture, 1, 1400); Release(capture, 0, 1450);
        State(capture, InputLearningCaptureState.Completed, "The next deliberate press is accepted");

        capture = New(analog, 0);
        Feed(capture, 500, 200); // There need not be a background timer.
        State(capture, InputLearningCaptureState.Capturing, "First fresh event can arm and begin after a quiet interval");
        Release(capture, 0, 300);
        Check(capture.Result.Kind == InputControlKind.Absolute, "Two observed values never turn metadata-declared analog input into a button");
        Near(capture.Result.Active, 500, "Observed peak is retained for review");
        Near(capture.Result.Normalize(500), .5, "Shallow learning does not shrink the declared scale");
        Near(capture.Result.Normalize(1000), 1, "Full declared travel remains full output");
        Near(capture.Result.Normalize(-1), 0, "Out-of-range reports are neutralized");
        Near(capture.Result.Normalize(Double.NaN), 0, "Non-finite input never propagates into controller output");

        // The supported travel decoder has a full unsigned wire domain but a
        // known two-raw-unit activity threshold. Noise thresholds are adapter
        // metadata, not calibration inferred from one shallow gesture.
        var travelDescriptor = new InputControlDescriptor("a", "Travel channel", InputControlKind.Absolute, 0, 65535, 0, null, 2);
        capture = New(travelDescriptor, 0);
        for (int tick = 1; tick <= 200; tick++) Feed(capture, tick % 2, tick);
        Check(capture.Control == null, "Known one-unit travel noise cannot start a capture");
        Feed(capture, 2, 210); Feed(capture, 385, 220); capture.Advance(1000);
        State(capture, InputLearningCaptureState.Capturing, "385 raw units is held activity despite the unsigned65535 wire limit");
        Release(capture, 0, 1100);
        Check(capture.Result != null && capture.Result.Active == 385, "Known travel activity override captures normal keyboard travel");
        Near(capture.Result.LogicalMaximum, 65535, "Activity threshold does not shrink the declared wire range");
        capture = New(travelDescriptor, 385); capture.Advance(1000);
        State(capture, InputLearningCaptureState.WaitingForNeutral, "Travel override also prevents an initially held key from looking neutral");
        Feed(capture, 0, 1100); capture.Advance(1250); Feed(capture, 385, 1300); Release(capture, 1, 1400);
        Check(capture.Result != null, "Travel release accepts one raw unit of known idle noise");
        var signedTravel = new InputControlDescriptor("a", "Signed channel", InputControlKind.Absolute, -32768, 32767, 0, null, 2);
        capture = New(signedTravel, 0); Feed(capture, -1, 200);
        Check(capture.Control == null, "The override treats signed idle noise symmetrically");
        Feed(capture, -2, 210); Feed(capture, -385, 220); Release(capture, -1, 300);
        Check(capture.Result.Direction == -1 && capture.Result.Active == -385, "The activity override preserves descending gestures");

        capture = New(Descriptor("a", InputControlKind.Absolute, 0, 255, 255), 255);
        Feed(capture, 220, 160); Feed(capture, 100, 170); Feed(capture, 140, 180); Release(capture, 255, 200);
        Check(capture.Result.Direction == -1, "Descending pressure is determined relative to rest");
        Near(capture.Result.Active, 100, "Descending gesture retains its deepest observed value");
        Near(capture.Result.Normalize(127.5), .5, "Descending normalization uses the declared endpoint");
        Near(capture.Result.Normalize(0), 1, "Descending maximum pressure is correct");

        capture = New(Descriptor("a", InputControlKind.Absolute, -100, 100, 0), 0);
        Feed(capture, -50, 200); Release(capture, 0, 300);
        Near(capture.Result.Normalize(-25), .25, "A centered axis learns the intended negative half");
        Near(capture.Result.Normalize(75), 0, "The other half-axis remains inactive");
        capture = New(Descriptor("a", InputControlKind.Absolute, -100, 100, 0), 0);
        Feed(capture, 50, 200); Feed(capture, -50, 210);
        State(capture, InputLearningCaptureState.Ambiguous, "Crossing through both directions requires a repeat");
        Check(capture.Problem == InputLearningProblem.DirectionChanged && !capture.Finish(300), "Ambiguous direction cannot be forced into a route");

        var other = Descriptor("b", InputControlKind.Absolute, 0, 1000, 0);
        capture = new InputLearningCapture("device-a", new[] { analog, other },
            new[] { Sample("a", 0, 0), Sample("b", 0, 0) }, 0);
        Feed(capture, 300, 200); Feed(capture, 0, 300);
        capture.Feed(Sample("b", 400, 300));
        State(capture, InputLearningCaptureState.Ambiguous, "A competing field in the release report prevents auto-accept");
        Check(capture.Result == null && capture.Problem == InputLearningProblem.CompetingControls, "Competing controls produce a visible retry state without a result");
        capture.Retry(new[] { Sample("a", 0, 400), Sample("b", 400, 400) }, 400);
        capture.Advance(1000);
        State(capture, InputLearningCaptureState.Ready, "Retry can arm the resting channel while a competing input is still held");
        Check(capture.Result == null && capture.Control == null, "Retry never learns the competing initial hold");
        capture.Feed(Sample("b", 0, 1100)); capture.Advance(1250);
        Feed(capture, 800, 1300); Release(capture, 0, 1400);
        Check(capture.Result.ControlId == "a", "A clean repeated gesture resolves ambiguity");

        capture = New(analog, 0);
        for (int tick = 1; tick <= 1000; tick++)
        {
            Feed(capture, tick % 9, tick);
            Check(capture.State != InputLearningCaptureState.Capturing && capture.Result == null, "Small idle noise cannot start a capture");
        }
        Feed(capture, 100, 1100); Feed(capture, 800, 1110); Release(capture, 7, 1200);
        Check(capture.Result.Kind == InputControlKind.Absolute, "A noisy analog release still completes near neutral");
        Near(capture.Result.Rest, 0, "Noise never overwrites declared neutral");

        capture = New(Descriptor("a", InputControlKind.Absolute, 0, 1000, null), 300);
        Feed(capture, 400, 100); capture.Advance(249);
        State(capture, InputLearningCaptureState.WaitingForNeutral, "Unknown rest must settle again after drift");
        capture.Advance(250); Feed(capture, 600, 300); Release(capture, 400, 400);
        Near(capture.Result.Rest, 400, "Without neutral metadata the stable observed baseline is explicit");
        Near(capture.Result.Normalize(700), .5, "Observed baseline does not replace the logical active endpoint");

        var wheel = Descriptor("a", InputControlKind.Relative, -127, 127, 0);
        capture = New(wheel, 0); Feed(capture, 2, 200); Feed(capture, 5, 210); Feed(capture, 0, 220); capture.Advance(10000);
        State(capture, InputLearningCaptureState.Capturing, "A relative wheel never pretends to complete on zero or timeout");
        Check(capture.CanFinish && capture.Finish(10000), "Relative control has explicit completion");
        Near(capture.Result.Normalize(1), 1, "Positive wheel tick is a directional event pulse");
        Near(capture.Result.Normalize(-1), 0, "Opposite wheel direction does not trigger the learned action");
        Near(capture.Result.Normalize(0), 0, "No delta has no pressure");
        capture = New(wheel, 0); Feed(capture, -2, 200); Feed(capture, 1, 210);
        State(capture, InputLearningCaptureState.Ambiguous, "Opposite relative steps require an explicit clean direction");
        capture = New(wheel, 0); Feed(capture, 3, 100); capture.Advance(249);
        State(capture, InputLearningCaptureState.WaitingForNeutral, "Relative movement before arming restarts quiet time");
        capture.Advance(250); Feed(capture, -1, 300); Check(capture.Finish(310), "Negative wheel direction can be explicitly learned");
        Check(capture.Result.Direction == -1, "Relative direction is preserved");

        capture = New(button, 0); Feed(capture, 1, 200); capture.Advance(20000);
        State(capture, InputLearningCaptureState.Capturing, "A latching switch is not auto-confirmed by a timeout");
        Check(capture.Finish(20000), "An intentional non-returning switch can be completed explicitly");
        capture = New(analog, 0); Feed(capture, 700, 200); Check(capture.Finish(210), "A non-returning absolute slider supports explicit completion");
        Near(capture.Result.Normalize(700), .7, "Explicit completion does not reinterpret the slider as a button");

        var hat = Descriptor("a", InputControlKind.Hat, 0, 7, 8);
        capture = New(hat, 8); Feed(capture, 0, 200); Release(capture, 8, 300);
        Check(capture.Result.HatValue == 0 && capture.Result.Kind == InputControlKind.Hat, "Hat direction zero is active, not a universal release value");
        Near(capture.Result.Normalize(0), 1, "Learned hat direction activates");
        Near(capture.Result.Normalize(1), 0, "Neighboring hat direction is a different discrete input");
        Near(capture.Result.Normalize(8), 0, "Out-of-range declared null state is neutral");
        capture = New(hat, 8); Feed(capture, 0, 200); Feed(capture, 1, 210);
        State(capture, InputLearningCaptureState.Ambiguous, "Changing hat direction during a gesture requests a repeat");

        capture = New(button, 0);
        capture.Feed(null); capture.Feed(Sample(null, 1, 100)); capture.Feed(Sample("missing", 1, 100));
        Feed(capture, Double.NaN, 100); Feed(capture, Double.PositiveInfinity, 100);
        Feed(capture, .25, 100); Feed(capture, -1, 100); Feed(capture, 1, -1);
        Check(capture.IgnoredSampleCount == 8, "Invalid samples are ignored without constructing fake controls");
        capture.Advance(200); Feed(capture, 1, 150);
        State(capture, InputLearningCaptureState.Ready, "Stale press cannot begin learning");
        Feed(capture, 1, 210); Feed(capture, 0, 209); capture.Advance(220);
        State(capture, InputLearningCaptureState.Capturing, "An older release cannot complete a newer press");
        Feed(capture, 0, 220); capture.Advance(300);
        Check(capture.Result != null, "Fresh valid data remains usable after malformed samples");
        capture = New(button, 0); Feed(capture, 1, 200); Feed(capture, 0, 200); capture.Advance(280);
        State(capture, InputLearningCaptureState.Completed, "Ordered reports within one clock millisecond do not lose a valid release");

        capture = new InputLearningCapture("device-a", new[] { button }, null, 100);
        capture.Advance(1000);
        State(capture, InputLearningCaptureState.WaitingForNeutral, "A missing initial report is not assumed to be released");
        Feed(capture, 1, 1100); Feed(capture, 0, 1200); capture.Advance(1350);
        State(capture, InputLearningCaptureState.Ready, "The first observed press is baseline state until actually released");
        Check(!capture.Finish(1350), "Finish without a deliberate gesture cannot invent an assignment");
        capture = new InputLearningCapture("device-a", new[] { button }, new[] { Sample("a", 1, 10) }, 1000);
        capture.Advance(2000);
        State(capture, InputLearningCaptureState.WaitingForNeutral, "An old event snapshot retains a currently held button");

        // Travel and Raw Input streams are sparse: descriptors can enumerate
        // a whole keyboard while only a few controls have ever reported state.
        var keyboard = new List<InputControlDescriptor>();
        for (int key = 0; key < 256; key++) keyboard.Add(Descriptor("key-" + key, InputControlKind.Button, 0, 1, 0));
        capture = new InputLearningCapture("sparse-keyboard", keyboard, new[] { Sample("key-0", 0, 0) }, 0);
        capture.Advance(150);
        State(capture, InputLearningCaptureState.Ready, "One observed idle key can arm while 255 descriptors never report");
        capture.Feed(Sample("key-0", 1, 200)); capture.Feed(Sample("key-0", 0, 220)); capture.Advance(300);
        Check(capture.Result != null && capture.Result.ControlId == "key-0", "Unused keyboard controls never block a valid learned gesture");
        capture.Retry(new[] { Sample("key-0", 0, 300) }, 300); capture.Advance(450);
        capture.Feed(Sample("key-1", 1, 500)); capture.Feed(Sample("key-1", 0, 550));
        Check(capture.Control == null && capture.Result == null, "An unobserved key's first active report and release only establish its baseline");
        capture.Advance(700); capture.Feed(Sample("key-1", 1, 710)); capture.Feed(Sample("key-1", 0, 720)); capture.Advance(800);
        Check(capture.Result != null && capture.Result.ControlId == "key-1", "A second deliberate gesture learns the independently armed key");
        capture.Retry(new[] { Sample("key-0", 0, 800) }, 800); capture.Advance(950);
        capture.Feed(Sample("key-2", 0, 1000)); capture.Feed(Sample("key-2", 1, 1010));
        Check(capture.Control == null, "Another key's long quiet period cannot prematurely arm a newly observed channel");
        capture.Feed(Sample("key-2", 0, 1100)); capture.Advance(1250);
        capture.Feed(Sample("key-2", 1, 1300)); capture.Feed(Sample("key-2", 0, 1400)); capture.Advance(1480);
        Check(capture.Result != null && capture.Result.ControlId == "key-2", "A newly observed channel uses its own full neutral settling period");
        capture = new InputLearningCapture("sparse-keyboard", keyboard, null, 0);
        capture.Advance(1000);
        State(capture, InputLearningCaptureState.WaitingForNeutral, "A completely unseen event keyboard waits for its first observed state");
        capture.Feed(Sample("key-3", 0, 1100)); capture.Advance(1250);
        capture.Feed(Sample("key-3", 1, 1300)); capture.Feed(Sample("key-3", 0, 1400)); capture.Advance(1480);
        Check(capture.Result != null && capture.Result.ControlId == "key-3", "An initially empty keyboard can start learning without touching every key");
        capture.Retry(new[] { Sample("key-0", 0, 1500) }, 1500); capture.Advance(1650);
        capture.Feed(Sample("key-0", 1, 1700)); capture.Feed(Sample("key-4", 1, 1700));
        State(capture, InputLearningCaptureState.Ambiguous, "A newly observed active competing channel cannot make an overlapping gesture look unique");
        capture.Retry(new[] { Sample("key-0", 0, 1800) }, 1800); capture.Advance(1950);
        capture.Feed(Sample("key-0", 1, 2000)); capture.Feed(Sample("key-4", 0, 2000));
        capture.Feed(Sample("key-0", 0, 2100)); capture.Advance(2180);
        Check(capture.Result != null && capture.Result.ControlId == "key-0", "A newly observed idle channel does not invalidate a valid ongoing gesture");

        var reversedButton = Descriptor("a", InputControlKind.Button, 0, 1, 1);
        capture = New(reversedButton, 1); Feed(capture, 0, 200); Release(capture, 1, 300);
        Check(capture.Result.Direction == -1, "Metadata-defined active-low buttons are supported");
        Near(capture.Result.Normalize(0), 1, "Active-low button does not become a released signal");

        for (int sign = -1; sign <= 1; sign += 2)
        for (int depth = 3; depth <= 100; depth++)
        {
            capture = New(Descriptor("a", InputControlKind.Absolute, -100, 100, 0), 0);
            // At least five units exceeds 2.5% of a centered range.
            double active = sign * Math.Max(5, depth);
            Feed(capture, active, 200); Release(capture, 0, 300);
            Check(capture.Result != null && capture.Result.Direction == sign, "A returned single half-axis gesture is learned consistently");
            double previous = 0;
            for (int travel = 0; travel <= 100; travel++)
            {
                double normalized = capture.Result.Normalize(sign * travel);
                Check(normalized >= previous && normalized >= 0 && normalized <= 1, "Normalized travel remains bounded and monotonic across learned depths");
                Near(normalized, travel / 100.0, "Learning depth never changes a declared pressure scale");
                previous = normalized;
            }
        }

        Throws(delegate { Descriptor("", InputControlKind.Button, 0, 1, 0); }, "Empty control identity rejected");
        Throws(delegate { Descriptor("a", InputControlKind.Absolute, 1, 1, 1); }, "Zero range rejected");
        Throws(delegate { Descriptor("a", InputControlKind.Absolute, 0, Double.PositiveInfinity, 0); }, "Infinite range rejected");
        Throws(delegate { Descriptor("a", InputControlKind.Absolute, 0, 1, 2); }, "Out-of-range analog neutral rejected");
        Throws(delegate { Descriptor("a", InputControlKind.Button, 0, 1, .5); }, "Half-pressed button neutral rejected");
        Throws(delegate { Descriptor("a", InputControlKind.Hat, 0, 7, 8.5); }, "Non-discrete hat neutral rejected");
        Throws(delegate { Descriptor("a", InputControlKind.Relative, 1, 127, null); }, "Relative range must contain zero");
        foreach (double invalidThreshold in new[] { 0, -1, Double.NaN, Double.PositiveInfinity, 1001 })
            Throws(delegate { new InputControlDescriptor("a", "Axis", InputControlKind.Absolute, 0, 1000, 0, null, invalidThreshold); }, "Invalid activity thresholds are rejected");
        Throws(delegate { new InputControlDescriptor("a", "Button", InputControlKind.Button, 0, 1, 0, null, .5); }, "Activity threshold cannot silently change binary semantics");
        Throws(delegate { new InputLearningCapture("", new[] { button }, null, 0); }, "Empty device identity rejected");
        Throws(delegate { new InputLearningCapture("device", new[] { button, button }, null, 0); }, "Duplicate control identities rejected");
        Throws(delegate { new InputLearningCapture("device", new InputControlDescriptor[0], null, 0); }, "Empty capabilities rejected");
        var many = new List<InputControlDescriptor>();
        for (int index = 0; index <= InputLearningCapture.MaximumControls; index++) many.Add(Descriptor("control-" + index, InputControlKind.Button, 0, 1, 0));
        Throws(delegate { new InputLearningCapture("device", many, null, 0); }, "Device metadata memory is bounded");
        Throws(delegate { new LearnedInputRoute("d", "c", InputControlKind.Absolute, 0, 1, 0, .5, -1, null); }, "Incoherent stored direction rejected");
        Throws(delegate { new LearnedInputRoute("d", "c", InputControlKind.Hat, 0, 7, 8, 2, -1, 3); }, "Incoherent hat identity rejected");
        Console.WriteLine("PASS: " + assertions + " input learning assertions.");
    }
}
