using System;
using System.Collections.Generic;
using Tk75.Mapping;

public static class GamepadInputProfilesHarness
{
    static int assertions;
    static void Check(bool condition, string message)
    { assertions++; if (!condition) throw new InvalidOperationException("FAIL: " + message); }
    static void Near(double expected, double actual, string message)
    { Check(Math.Abs(expected - actual) < 1e-9, message + ": " + actual); }
    static void Reject(Action action, string message)
    { bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } Check(rejected, message); }
    static ControllerFrame Compose(Profile profile, Dictionary<int, double> raw)
    {
        var calibration = new Dictionary<int, Calibration>();
        foreach (int key in raw.Keys) calibration.Add(key, new Calibration(0, 1));
        var result = MappingEngine.Compose(raw, calibration, profile, new Dictionary<string, SignalState>(), .005);
        Check(result.Errors.Count == 0, "Mapping has no errors: " + String.Join(" ", result.Errors.ToArray()));
        return result;
    }
    static double Value(ControllerFrame frame, OutputTarget target)
    {
        switch (target)
        {
            case OutputTarget.LeftXPositive: return Math.Max(0, frame.LeftX);
            case OutputTarget.LeftXNegative: return Math.Max(0, -frame.LeftX);
            case OutputTarget.LeftYPositive: return Math.Max(0, frame.LeftY);
            case OutputTarget.LeftYNegative: return Math.Max(0, -frame.LeftY);
            case OutputTarget.RightXPositive: return Math.Max(0, frame.RightX);
            case OutputTarget.RightXNegative: return Math.Max(0, -frame.RightX);
            case OutputTarget.RightYPositive: return Math.Max(0, frame.RightY);
            case OutputTarget.RightYNegative: return Math.Max(0, -frame.RightY);
            case OutputTarget.LeftTrigger: return frame.LeftTrigger;
            case OutputTarget.RightTrigger: return frame.RightTrigger;
        }
        ushort[] masks = { 0x1000, 0x2000, 0x4000, 0x8000, 0x0100, 0x0200, 0x0020, 0x0010, 0x0040, 0x0080, 0x0001, 0x0002, 0x0004, 0x0008 };
        return (frame.Buttons & masks[(int)target - (int)OutputTarget.A]) == 0 ? 0 : 1;
    }
    public static void Run()
    {
        const string legacy = "{\"Version\":1,\"Name\":\"Legacy\",\"Bindings\":[{\"BindingId\":\"wasd\",\"KeyIndex\":73,\"Target\":0}]}";
        Profile keyboard = ProfileJson.Deserialize(legacy);
        Check(keyboard.InputMode == InputMode.Keyboard && keyboard.InputDeviceId == null && keyboard.InputDeviceName == null, "Old profiles default to keyboard input");
        Check(keyboard.Bindings[0].KeyIndex == 73 && keyboard.Bindings[0].Target == OutputTarget.LeftXPositive, "Old key mappings retain meaning");
        string oldSnapshot = ProfileJson.Serialize(keyboard);
        Check(!oldSnapshot.Contains("\"InputMode\"") && !oldSnapshot.Contains("InputDevice"), "Keyboard serialization omits new metadata");
        foreach (InputMode mode in new[] { InputMode.XboxController, InputMode.PlayStationController })
        {
            Profile initial = GamepadInputProfile.Create(mode, "Controller profile");
            Check(initial.Bindings.Count == 24 && initial.LearnedInputs.Count == 24 && initial.Inputs.Count == 0, "New controller profile covers all standard controls");
            Check(initial.Controller == GamepadInputProfile.DisplayKind(mode) && initial.StickShape == StickShape.Square, "Identity default preserves source family and diagonal range");
            foreach (LearnedKeyBinding route in initial.LearnedInputs)
            {
                Check(route.Backend == "gamepad" && route.SourceDeviceId == GamepadInputProfile.UnselectedDeviceId && route.SourceKeyIndex == null, "Unselected routes cannot claim keyboard keys");
                Check(route.ControlId == GamepadInputProfile.ControlId(route.KeyIndex), "Stable control identities match logical controller diagram");
                var interpretation = new LearnedInputRoute(route.SourceDeviceId, route.ControlId, (InputControlKind)route.Kind, route.Minimum, route.Maximum, route.Rest, route.Active, route.Direction, route.HatValue);
                Near(1, interpretation.Normalize(1), "Each route accepts full activity");
                Near(0, interpretation.Normalize(0), "Each route has a neutral release");
            }
            Profile connected = GamepadInputProfile.WithDevice(initial, new string('a', 64), "Physical controller");
            Check(initial.InputDeviceId == null && connected.InputDeviceId == new string('a', 64), "Selecting hardware leaves source profile untouched");
            Check(connected.LearnedInputs.TrueForAll(delegate(LearnedKeyBinding route) { return route.SourceDeviceId == connected.InputDeviceId; }), "All inputs use selected hardware identity");
            Profile clone = ProfileJson.Clone(connected);
            Check(clone.InputMode == mode && clone.InputDeviceId == connected.InputDeviceId && clone.InputDeviceName == "Physical controller", "Controller identity round-trips");
            clone.Bindings[0].Target = OutputTarget.RightTrigger;
            clone.Bindings[0].Processing.TopDeadzone = .1;
            clone = GamepadInputProfile.WithDevice(clone, new string('b', 64), "Replacement controller");
            Check(clone.Bindings[0].Target == OutputTarget.RightTrigger && clone.Bindings[0].Processing.TopDeadzone == .1, "Changing source preserves output remaps and processing");
            Check(ProfileChangeImpact.RequiresControllerReset(connected, clone), "Changed source/remap invalidates runtime state");
            clone = GamepadInputProfile.WithDevice(clone, null, null);
            Check(clone.InputDeviceId == null && clone.LearnedInputs[0].SourceDeviceId == GamepadInputProfile.UnselectedDeviceId && clone.Bindings[0].Target == OutputTarget.RightTrigger, "Clearing hardware selection preserves edits and marks input unavailable");

            // The same route pipeline supports every input/output pair. Binary
            // sources remain exactly 0/1 when mapped to a trigger or stick.
            for (int source = 0; source < 24; source++)
                for (int target = 0; target < 24; target++)
                {
                    Profile mapping = GamepadInputProfile.Create(mode, "Any-to-any");
                    mapping.Bindings.Clear(); mapping.Bindings.Add(new Binding { KeyIndex = source, Target = (OutputTarget)target });
                    double activity = GamepadInputProfile.IsAnalog(source) ? .375 : 1;
                    var frame = Compose(mapping, new Dictionary<int, double> { { source, activity } });
                    double expected = target < 10 ? activity : activity >= .5 ? 1 : 0;
                    Near(expected, Value(frame, (OutputTarget)target), "Any input maps to any output");
                    frame = Compose(mapping, new Dictionary<int, double> { { source, 0 } });
                    Near(0, Value(frame, (OutputTarget)target), "Release stays exactly neutral");
                }
        }
        Profile swapped = GamepadInputProfile.Create(InputMode.XboxController, "Axis swap");
        swapped.Bindings.Clear();
        swapped.Bindings.Add(new Binding { KeyIndex = (int)OutputTarget.LeftXPositive, Target = OutputTarget.LeftYPositive });
        swapped.Bindings.Add(new Binding { KeyIndex = (int)OutputTarget.LeftXNegative, Target = OutputTarget.LeftYNegative });
        swapped.Bindings.Add(new Binding { KeyIndex = (int)OutputTarget.LeftYPositive, Target = OutputTarget.LeftXPositive });
        swapped.Bindings.Add(new Binding { KeyIndex = (int)OutputTarget.LeftYNegative, Target = OutputTarget.LeftXNegative });
        var swappedFrame = Compose(swapped, new Dictionary<int, double> { { 0, .8 }, { 1, 0 }, { 2, 0 }, { 3, .9 } });
        Near(-.9, swappedFrame.LeftX, "Y negative can become X negative on the same stick");
        Near(.8, swappedFrame.LeftY, "X positive can become Y positive without diagonal compression");
        Profile threshold = GamepadInputProfile.Create(InputMode.PlayStationController, "Threshold");
        threshold.Bindings.Clear();
        threshold.Bindings.Add(new Binding { KeyIndex = (int)OutputTarget.RightTrigger, Target = OutputTarget.A, Processing = new SignalSettings { ButtonThreshold = .75 } });
        Near(0, Value(Compose(threshold, new Dictionary<int, double> { { 9, .74 } }), OutputTarget.A), "Analog-to-button respects threshold");
        Near(1, Value(Compose(threshold, new Dictionary<int, double> { { 9, .75 } }), OutputTarget.A), "Analog-to-button actuates at threshold");

        Check(ProfileJson.Serialize(keyboard) == oldSnapshot, "Creating controller profiles never changes keyboard mappings");
        Reject(delegate { GamepadInputProfile.Create(InputMode.Keyboard, "Invalid"); }, "Controller helper does not reinterpret keyboard mode");
        Reject(delegate { GamepadInputProfile.WithDevice(keyboard, new string('a', 64), "Wrong"); }, "Hardware cannot be attached by reinterpreting keyboard profile");
        Reject(delegate { GamepadInputProfile.WithDevice(swapped, "invalid", "Bad identity"); }, "Source identity is validated");
        Reject(delegate { GamepadInputProfile.WithDevice(swapped, null, "Incomplete"); }, "Source identity and name are an atomic pair");
        Reject(delegate { ProfileJson.Deserialize(oldSnapshot.Replace("\"Version\":1", "\"InputMode\":0.5,\"Version\":1")); }, "Fractional input modes are rejected");
        Profile invalid = ProfileJson.Clone(swapped); invalid.LearnedInputs.RemoveAt(0);
        Reject(delegate { ProfileJson.Serialize(invalid); }, "Missing controller source routes are rejected");
        invalid = ProfileJson.Clone(swapped); invalid.LearnedInputs[0].ControlId = "LeftXNegative";
        Reject(delegate { ProfileJson.Serialize(invalid); }, "Input diagram identity cannot silently drift from hardware routing");
        invalid = ProfileJson.Clone(swapped); invalid.LearnedInputs[0].SourceKeyIndex = 0;
        Reject(delegate { ProfileJson.Serialize(invalid); }, "Controller input never claims keyboard lighting identity");
        invalid = ProfileJson.Clone(swapped); invalid.Bindings[0].KeyIndex = 42;
        Reject(delegate { ProfileJson.Serialize(invalid); }, "Controller remaps cannot refer to invisible keyboard indices");
        Console.WriteLine("PASS: " + assertions + " controller input profile assertions.");
    }
}
