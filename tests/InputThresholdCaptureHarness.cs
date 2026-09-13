using System;
using Tk75.Mapping;

public static class InputThresholdCaptureHarness
{
    static int checks;
    static void Check(bool passed, string message) { checks++; if (!passed) throw new InvalidOperationException(message); }
    public static int Run()
    {
        checks = 0;
        var first = new InputThresholdCapture(9, 20, 420, null);
        first.Feed(9, 180); first.Feed(9, 20);
        Check(first.Completed && first.Maximum == 180, "No prior snapshot is needed: the first press/release captures its peak.");
        first.Feed(9, 420);
        Check(first.Maximum == 180, "A completed measurement never absorbs later samples.");

        var held = new InputThresholdCapture(14, 20, 420, 240);
        Check(held.WaitingForRelease && !held.Pressing, "A key already held at start waits for release.");
        held.Feed(14, 320); held.Feed(14, 100);
        Check(held.WaitingForRelease && !held.Completed && held.Maximum == 20, "An existing hold is never measured as the new desired distance.");
        held.Feed(14, 20);
        Check(!held.WaitingForRelease && !held.Completed && !held.Pressing, "Releasing the initial hold only arms the measurement.");
        held.Feed(14, 180); held.Feed(14, 260); held.Feed(14, 20);
        Check(held.Completed && held.Maximum == 260, "Only the next deliberate press/release is saved.");

        var isolated = new InputThresholdCapture(9, 0, 385, 0);
        isolated.Feed(14, 250); isolated.Feed(14, 0);
        Check(!isolated.Completed && !isolated.Pressing, "Unselected source samples cannot contaminate a measurement.");
        isolated.Feed(9, 1); isolated.Feed(9, 0);
        Check(!isolated.Pressing && !isolated.Completed, "Small released-level noise does not complete calibration.");
        isolated.Feed(9, -1); isolated.Feed(9, 65536);
        Check(!isolated.Pressing && isolated.Maximum == 0, "Invalid pressure samples are ignored.");
        isolated.Feed(9, 60); isolated.Feed(9, 90);
        Check(isolated.Pressing && !isolated.Completed && isolated.Maximum == 90, "A held measurement previews its peak without committing.");
        isolated.Feed(9, 0);
        Check(isolated.Completed && isolated.Maximum == 90, "Release confirms exactly one completed measurement.");
        return checks;
    }
}
