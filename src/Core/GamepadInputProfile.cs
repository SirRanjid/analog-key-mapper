using System;
using System.Collections.Generic;

namespace Tk75.Mapping
{
    // Controller inputs reuse the existing logical-input processing and output
    // routes. Switching source families creates a profile, never reinterprets
    // the keyboard's saved physical key indices.
    public static class GamepadInputProfile
    {
        public const int ControlCount = 24;
        public static readonly string UnselectedDeviceId = new string('0', 64);

        public static OutputTarget Target(int keyIndex)
        {
            if (keyIndex < 0 || keyIndex >= ControlCount) throw new ArgumentOutOfRangeException("keyIndex");
            return (OutputTarget)keyIndex;
        }
        public static int KeyIndex(OutputTarget target)
        {
            if (!Enum.IsDefined(typeof(OutputTarget), target)) throw new ArgumentOutOfRangeException("target");
            return (int)target;
        }
        public static string ControlId(int keyIndex) { return Target(keyIndex).ToString(); }
        public static bool IsAnalog(int keyIndex) { return (int)Target(keyIndex) <= (int)OutputTarget.RightTrigger; }
        public static ControllerKind DisplayKind(InputMode mode)
        {
            if (mode == InputMode.XboxController) return ControllerKind.Xbox360;
            if (mode == InputMode.PlayStationController) return ControllerKind.DualSense;
            throw new ArgumentOutOfRangeException("mode", "Ein Controller-Eingabemodus ist erforderlich.");
        }

        public static Profile Create(InputMode mode, string name)
        {
            var result = new Profile { Name = name, InputMode = mode, Controller = DisplayKind(mode), StickShape = StickShape.Square };
            for (int key = 0; key < ControlCount; key++) result.Bindings.Add(new Binding { KeyIndex = key, Target = Target(key) });
            SetDevice(result, null, null);
            MappingValidation.RequireValid(result);
            return result;
        }
        public static Profile WithDevice(Profile profile, string deviceId, string name)
        {
            if (profile == null) throw new ArgumentNullException("profile");
            DisplayKind(profile.InputMode);
            Profile result = ProfileJson.Clone(profile);
            SetDevice(result, deviceId, name);
            MappingValidation.RequireValid(result);
            return result;
        }
        static void SetDevice(Profile profile, string deviceId, string name)
        {
            if (deviceId == UnselectedDeviceId) throw new ArgumentException("Diese Gerätekennung ist für nicht ausgewählte Controller reserviert.", "deviceId");
            profile.InputDeviceId = deviceId; profile.InputDeviceName = name;
            profile.LearnedInputs = new List<LearnedKeyBinding>();
            for (int key = 0; key < ControlCount; key++)
                profile.LearnedInputs.Add(new LearnedKeyBinding {
                    KeyIndex = key, Backend = "gamepad", SourceDeviceId = deviceId ?? UnselectedDeviceId,
                    SourceName = ControlId(key), ControlId = ControlId(key), Kind = IsAnalog(key) ? 1 : 0,
                    Minimum = 0, Maximum = 1, Rest = 0, Active = 1, Direction = 1
                });
        }
    }
}
