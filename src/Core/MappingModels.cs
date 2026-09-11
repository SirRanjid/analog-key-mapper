using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Tk75.Mapping
{
    public enum OutputTarget
    {
        LeftXPositive, LeftXNegative, LeftYPositive, LeftYNegative,
        RightXPositive, RightXNegative, RightYPositive, RightYNegative,
        LeftTrigger, RightTrigger, A, B, X, Y, LB, RB, Back, Start,
        LeftThumb, RightThumb, DpadUp, DpadDown, DpadLeft, DpadRight
    }
    public enum CurveKind { Linear, Exponential, Logarithmic, Smoothstep, Custom, Bezier }
    public enum StickShape { Circle, Square }
    public enum OpposedPolicy { Neutral, Subtract }
    public enum AggregationMode { Maximum, ClampedSum }
    public enum InputOpposedPolicy { Neutral, LastPressed, FirstPressed }
    public enum ControllerKind { Xbox360, DualSense }
    public enum KeyboardSuppressionMode { Off = 0, AllMapped = 1, SelectedMapped = 2 }

    [DataContract]
    public sealed class HotkeySettings
    {
        [DataMember(IsRequired = true, Order = 0)] public bool Enabled;
        [DataMember(IsRequired = true, Order = 1)] public int KeyCode;
        // Windows hotkey bits: Alt=1, Ctrl=2, Shift=4, Win=8.
        [DataMember(IsRequired = true, Order = 2)] public int Modifiers;
        public HotkeySettings() { Defaults(); }
        [OnDeserializing] private void OnReading(StreamingContext context) { Defaults(); }
        private void Defaults() { Enabled = true; KeyCode = 0x78; Modifiers = 0; }
    }

    [DataContract]
    public sealed class ControllerDefinition
    {
        [DataMember(IsRequired = true, Order = 0)] public string Id;
        [DataMember(IsRequired = true, Order = 1)] public string Name;
        [DataMember(IsRequired = true, Order = 2)] public ControllerKind Kind;
        [DataMember(Order = 3, EmitDefaultValue = false)] public int? RgbColor;
    }

    [DataContract]
    public sealed class KeyInputSettings
    {
        [DataMember(IsRequired = true, Order = 0)] public int KeyIndex;
        [DataMember(Order = 1)] public bool RapidTriggerEnabled;
        [DataMember(Order = 2)] public double ActuationPoint;
        [DataMember(Order = 3)] public double PressMovement;
        [DataMember(Order = 4)] public double ReleaseMovement;
        [DataMember(Order = 5)] public int? OppositeKeyIndex;
        [DataMember(Order = 6)] public InputOpposedPolicy OppositePolicy;
        public KeyInputSettings() { Defaults(); }
        [OnDeserializing] private void OnReading(StreamingContext context) { Defaults(); }
        private void Defaults()
        {
            RapidTriggerEnabled = false; ActuationPoint = .1; PressMovement = ReleaseMovement = .02;
            OppositeKeyIndex = null; OppositePolicy = InputOpposedPolicy.Neutral;
        }
    }

    [DataContract]
    public sealed class CurvePoint
    {
        [DataMember(IsRequired = true, Order = 0)] public double X;
        [DataMember(IsRequired = true, Order = 1)] public double Y;
        // One slope shared by the incoming and outgoing Bezier handles. Omitted
        // in legacy profiles; null requests an automatic monotone tangent.
        [DataMember(Order = 2, EmitDefaultValue = false)] public double? Tangent;
        public CurvePoint() { }
        public CurvePoint(double x, double y) { X = x; Y = y; }
        public CurvePoint(double x, double y, double? tangent) { X = x; Y = y; Tangent = tangent; }
    }

    [DataContract]
    public sealed class SignalSettings
    {
        [DataMember(Order = 0)] public double TopDeadzone;
        [DataMember(Order = 1)] public double BottomDeadzone;
        [DataMember(Order = 2)] public CurveKind Curve;
        [DataMember(Order = 3)] public double Exponent;
        [DataMember(Order = 4)] public List<CurvePoint> CustomPoints;
        [DataMember(Order = 5)] public double MinOutput;
        [DataMember(Order = 6)] public double MaxOutput;
        [DataMember(Order = 7)] public double Scale;
        [DataMember(Order = 8)] public double Hysteresis;
        [DataMember(Order = 9)] public double SmoothingTimeConstant;
        [DataMember(Order = 10)] public double ButtonThreshold;
        [DataMember(Order = 11)] public double OutputDeadzone;
        public SignalSettings() { Defaults(); }
        [OnDeserializing] private void OnReading(StreamingContext context) { Defaults(); }
        private void Defaults()
        {
            TopDeadzone = BottomDeadzone = MinOutput = Hysteresis = SmoothingTimeConstant = OutputDeadzone = 0;
            Curve = CurveKind.Linear; Exponent = 2; MaxOutput = Scale = 1; ButtonThreshold = 0.5;
            CustomPoints = new List<CurvePoint> { new CurvePoint(0, 0), new CurvePoint(1, 1) };
        }
    }

    [DataContract]
    public sealed class Binding
    {
        [DataMember(IsRequired = true, Order = 0)] public string BindingId;
        [DataMember(IsRequired = true, Order = 1)] public int KeyIndex;
        [DataMember(IsRequired = true, Order = 2)] public OutputTarget Target;
        [DataMember(Order = 3)] public bool Enabled;
        [DataMember(Order = 4)] public SignalSettings Processing;
        [DataMember(Order = 5)] public string ControllerId;
        public Binding() { Defaults(); BindingId = Guid.NewGuid().ToString("N"); }
        [OnDeserializing] private void OnReading(StreamingContext context) { Defaults(); }
        private void Defaults() { Enabled = true; Processing = new SignalSettings(); ControllerId = "main"; }
    }

    // Hardware calibration and runtime state are intentionally absent from profiles.
    [DataContract]
    public sealed class Profile
    {
        [DataMember(IsRequired = true, Order = 0)] public int Version;
        [DataMember(IsRequired = true, Order = 1)] public string Name;
        [DataMember(IsRequired = true, Order = 2)] public List<Binding> Bindings;
        [DataMember(Order = 3)] public StickShape StickShape;
        [DataMember(Order = 4)] public OpposedPolicy OpposedPolicy;
        [DataMember(Order = 5)] public AggregationMode Aggregation;
        [DataMember(Order = 6)] public List<KeyInputSettings> Inputs;
        [DataMember(Order = 7)] public ControllerKind Controller;
        // Empty retains the original one-controller representation. Explicit
        // routes are used only after adding/configuring controller slots.
        [DataMember(Order = 8)] public List<ControllerDefinition> Controllers;
        // Independent opt-in metadata: choosing suppression must not create an
        // input gate or change the existing analog activation threshold.
        [DataMember(Order = 9)] public List<int> SuppressedKeyboardKeys;
        [DataMember(Order = 10, EmitDefaultValue = false)] public bool RgbOverrideEnabled;
        [DataMember(Order = 11)] public HotkeySettings ModeSwitchHotkey;
        [DataMember(Order = 12)] public HotkeySettings EmergencyStopHotkey;
        [DataMember(Order = 13)] public bool ControllerInputEnabled;
        [DataMember(Order = 14, EmitDefaultValue = false)] public KeyboardSuppressionMode KeyboardSuppressionMode;
        [DataMember(Order = 15, EmitDefaultValue = false)] public bool ModeSwitchLightingEnabled;
        [DataMember(Order = 16)] public int ModeSwitchRgbColor;
        public Profile() { Defaults(); Version = 1; Name = "Neues Profil"; Bindings = new List<Binding>(); }
        [OnDeserializing] private void OnReading(StreamingContext context) { Defaults(); }
        private void Defaults() { StickShape = StickShape.Circle; OpposedPolicy = OpposedPolicy.Neutral; Aggregation = AggregationMode.Maximum; Inputs = new List<KeyInputSettings>(); Controller = ControllerKind.Xbox360; Controllers = new List<ControllerDefinition>(); SuppressedKeyboardKeys = new List<int>(); RgbOverrideEnabled = false; ModeSwitchHotkey = new HotkeySettings(); EmergencyStopHotkey = new HotkeySettings { KeyCode = 0x77 }; ControllerInputEnabled = true; KeyboardSuppressionMode = Tk75.Mapping.KeyboardSuppressionMode.Off; ModeSwitchLightingEnabled = false; ModeSwitchRgbColor = 0xFFC65C; }
    }

    public sealed class Calibration
    {
        public double Rest, Bottom;
        public double? UsableMin, UsableMax, MeasuredTravel;
        public Calibration() { Rest = Bottom = double.NaN; }
        public Calibration(double rest, double bottom) { Rest = rest; Bottom = bottom; }
    }

    // One instance per BindingId. The owner must serialize access and reset on
    // profile/calibration changes, lost input, disconnect, suspend and output disable.
    public sealed class SignalState
    {
        public bool IsPressed;
        public double SmoothedValue;
        public void Reset() { IsPressed = false; SmoothedValue = 0; }
    }
    public sealed class SignalResult
    {
        public double Normalized, AfterDeadzone, AfterCurve, Final;
        public string Error;
        public bool IsValid { get { return Error == null; } }
    }
    // One instance per physical key, shared by all its bindings. Runtime owners
    // reset these together with binding states on every invalidation/config change.
    public sealed class KeyInputState
    {
        public bool Active, HasActuated;
        public double Peak, Valley;
        public long PressOrder;
        public void Reset() { Active = HasActuated = false; Peak = Valley = 0; PressOrder = 0; }
    }
    public sealed class KeyInputResult
    {
        public double Normalized;
        public bool Active, Allowed;
        public string Error;
    }
    public sealed class ControllerFrame
    {
        public double LeftX, LeftY, RightX, RightY, LeftTrigger, RightTrigger;
        public ushort Buttons;
        public readonly List<string> Errors = new List<string>();
        public readonly Dictionary<string, SignalResult> BindingResults = new Dictionary<string, SignalResult>(StringComparer.Ordinal);
        public readonly Dictionary<int, KeyInputResult> InputResults = new Dictionary<int, KeyInputResult>();
    }
}
