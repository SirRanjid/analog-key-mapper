using System;
using Tk75.App;

public static class KeyboardLearningControlsHarness
{
    static int checks;
    static void Check(bool condition, string name) { checks++; if (!condition) throw new Exception(name); }
    public static string Run()
    {
        string id; bool down;
        Check(KeyboardLearningControls.TryDecode(0x1d, 0, 0x11, out id, out down) && id == "scan:1d" && down, "Left Ctrl make.");
        Check(KeyboardLearningControls.TryDecode(0x1d, 3, 0x11, out id, out down) && id == "scan:1d:e0" && !down, "Right Ctrl break.");
        Check(KeyboardLearningControls.TryDecode(0x38, 2, 0x12, out id, out down) && id == "scan:38:e0" && down, "Right Alt identity.");
        Check(KeyboardLearningControls.TryDecode(0x36, 1, 0x10, out id, out down) && id == "scan:36" && !down, "Right Shift break.");
        Check(KeyboardLearningControls.TryDecode(0x48, 0, 0x68, out id, out down) && id == "scan:48", "Keypad code is ordinary.");
        Check(KeyboardLearningControls.TryDecode(0x48, 2, 0x26, out id, out down) && id == "scan:48:e0", "Arrow code is extended.");
        Check(!KeyboardLearningControls.TryDecode(0x45, 4, 0x13, out id, out down), "E1 Pause cannot get stuck.");
        Check(!KeyboardLearningControls.TryDecode(0x2a, 2, 0x10, out id, out down), "Fake printscreen shift excluded.");
        Check(!KeyboardLearningControls.TryDecode(0, 0, 0x41, out id, out down), "Missing physical scan not guessed from virtual key.");
        Check(!KeyboardLearningControls.TryDecode(0xff, 0, 0xff, out id, out down), "Overrun ignored by decoder.");
        Check(!KeyboardLearningControls.TryDecode(0x1e, 0x80, 0x41, out id, out down), "Unknown flags rejected.");
        for (int scan = 1; scan <= 127; scan++)
            for (int ext = 0; ext < 2; ext++)
            {
                int parsed; bool extended;
                string control = KeyboardLearningControls.ControlId(scan, ext != 0);
                Check(KeyboardLearningControls.TryParseControlId(control, out parsed, out extended) && parsed == scan && extended == (ext != 0), "Canonical scan roundtrip.");
                if (ext == 1 && (scan == 0x2a || scan == 0x36)) continue;
                Check(KeyboardLearningControls.TryDecode((ushort)scan, (ushort)(ext * 2), 0x41, out id, out down) && down && id == control, "Make identity.");
                Check(KeyboardLearningControls.TryDecode((ushort)scan, (ushort)(ext * 2 + 1), 0x41, out id, out down) && !down && id == control, "Break identity.");
            }
        foreach (string invalid in new[] { "scan:00", "scan:80", "scan:1D", "scan:01:E0", "scan:01:e1", "scan:01", "anything", "scan: 1", null })
        {
            if (invalid == "scan:01") continue;
            int scan; bool extended;
            Check(!KeyboardLearningControls.TryParseControlId(invalid, out scan, out extended), "Reject noncanonical scan ID.");
        }
        return checks + " pure keyboard input identity checks passed.";
    }
}
