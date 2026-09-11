using System;
using System.Collections.Generic;

namespace Tk75.Mapping
{
    public static class SignalProcessor
    {
        internal static double Clamp(double value) { return value <= 0 ? 0 : value >= 1 ? 1 : value; }
        private static SignalResult Fail(SignalState state, string message)
        { if (state != null) state.Reset(); return new SignalResult { Error = message }; }

        // No clock, input generation or hardware access: dt is supplied by the caller.
        public static SignalResult Process(double raw, Calibration calibration, SignalSettings settings, SignalState state, double dtSeconds)
        {
            double normalized; string error;
            if (!Normalize(raw, calibration, out normalized, out error)) return Fail(state, error);
            return ProcessNormalized(normalized, settings, state, dtSeconds);
        }

        internal static bool Normalize(double raw, Calibration calibration, out double normalized, out string error)
        {
            normalized = 0; error = null;
            if (!MappingValidation.IsFinite(raw)) { error = "Rohwert fehlt oder ist nicht endlich."; return false; }
            var errors = MappingValidation.ValidateCalibration(calibration);
            if (errors.Count != 0) { error = string.Join(" ", errors.ToArray()); return false; }
            if (calibration.UsableMin.HasValue) raw = Math.Max(calibration.UsableMin.Value, Math.Min(calibration.UsableMax.Value, raw));
            normalized = Clamp((raw - calibration.Rest) / (calibration.Bottom - calibration.Rest));
            return true;
        }

        internal static SignalResult ProcessNormalized(double normalized, SignalSettings settings, SignalState state, double dtSeconds)
        {
            if (state == null) return Fail(null, "Signalzustand fehlt.");
            if (!MappingValidation.IsFinite(dtSeconds) || dtSeconds < 0) return Fail(state, "Zeitdifferenz muss endlich und nichtnegativ sein.");
            var errors = MappingValidation.ValidateSettings(settings);
            if (errors.Count != 0) return Fail(state, string.Join(" ", errors.ToArray()));
            if (!MappingValidation.IsFinite(state.SmoothedValue) || state.SmoothedValue < 0 || state.SmoothedValue > 1)
                return Fail(state, "Ungueltiger vorheriger Signalzustand.");
            var result = new SignalResult { Normalized = normalized };
            // Schmitt gate: enter above rest deadzone + epsilon; release at the
            // rest deadzone itself. Release always bypasses smoothing and min output.
            if (normalized <= settings.TopDeadzone || (!state.IsPressed && normalized <= settings.TopDeadzone + settings.Hysteresis))
            { state.Reset(); return result; }
            state.IsPressed = true;
            result.AfterDeadzone = Clamp((normalized - settings.TopDeadzone) / (1 - settings.TopDeadzone - settings.BottomDeadzone));
            result.AfterCurve = Curve(result.AfterDeadzone, settings);
            double scaledCurve = Clamp(result.AfterCurve * settings.Scale);
            if (scaledCurve <= settings.OutputDeadzone)
            { state.SmoothedValue = 0; return result; }
            double activeCurve = (scaledCurve - settings.OutputDeadzone) / (1 - settings.OutputDeadzone);
            double target = settings.MinOutput + activeCurve * (settings.MaxOutput - settings.MinOutput);
            double smoothed = target;
            if (settings.SmoothingTimeConstant > 0)
            {
                double alpha = 1 - Math.Exp(-dtSeconds / settings.SmoothingTimeConstant);
                smoothed = state.SmoothedValue + alpha * (target - state.SmoothedValue);
            }
            // MaxOutput remains a hard binding limit, including a previous filter
            // state above a newly lowered limit. Minimum is the active target only.
            state.SmoothedValue = result.Final = Math.Min(settings.MaxOutput, Clamp(smoothed));
            return result;
        }

        private static double Curve(double x, SignalSettings settings)
        {
            if (x <= 0) return 0;
            if (x >= 1) return 1;
            switch (settings.Curve)
            {
                case CurveKind.Exponential: return Clamp(Math.Pow(x, settings.Exponent));
                // Stable logarithmic family log(1 + a*x) / log(1+a).
                // For extremely small a, the analytic limit is linear.
                case CurveKind.Logarithmic:
                    if (settings.Exponent < 0.00000001) return x;
                    return Clamp(Math.Log(1 + settings.Exponent * x) / Math.Log(1 + settings.Exponent));
                case CurveKind.Smoothstep: return x * x * (3 - 2 * x);
                case CurveKind.Bezier: return BezierCurve.Evaluate(settings.CustomPoints, x);
                case CurveKind.Custom:
                    for (int i = 1; i < settings.CustomPoints.Count; i++)
                    {
                        CurvePoint right = settings.CustomPoints[i], left = settings.CustomPoints[i - 1];
                        if (x <= right.X) return Clamp(left.Y + (x - left.X) / (right.X - left.X) * (right.Y - left.Y));
                    }
                    return 1;
                default: return x;
            }
        }
    }

    public static partial class MappingEngine
    {
        private static double Merge(double oldValue, double value, AggregationMode mode)
        { return mode == AggregationMode.Maximum ? Math.Max(oldValue, value) : Math.Min(1, oldValue + value); }
        private static double Axis(double positive, double negative, OpposedPolicy policy)
        { return policy == OpposedPolicy.Neutral && positive > 0 && negative > 0 ? 0 : positive - negative; }
        private static void Shape(ref double x, ref double y, StickShape shape)
        {
            double length = Math.Sqrt(x * x + y * y);
            if (shape == StickShape.Circle && length > 1) { x /= length; y /= length; }
        }
        private static ushort ButtonMask(OutputTarget target)
        {
            switch (target)
            {
                case OutputTarget.DpadUp: return 0x0001; case OutputTarget.DpadDown: return 0x0002;
                case OutputTarget.DpadLeft: return 0x0004; case OutputTarget.DpadRight: return 0x0008;
                case OutputTarget.Start: return 0x0010; case OutputTarget.Back: return 0x0020;
                case OutputTarget.LeftThumb: return 0x0040; case OutputTarget.RightThumb: return 0x0080;
                case OutputTarget.LB: return 0x0100; case OutputTarget.RB: return 0x0200;
                case OutputTarget.A: return 0x1000; case OutputTarget.B: return 0x2000;
                case OutputTarget.X: return 0x4000; case OutputTarget.Y: return 0x8000;
                default: return 0;
            }
        }
        public static void ResetStates(IDictionary<string, SignalState> states)
        { if (states != null) foreach (SignalState state in states.Values) if (state != null) state.Reset(); }
        public static void ResetInputStates(IDictionary<int, KeyInputState> states)
        { if (states != null) foreach (KeyInputState state in states.Values) if (state != null) state.Reset(); }

        private static KeyInputResult ProcessInput(double normalized, KeyInputSettings settings, KeyInputState state, long order)
        {
            bool wasActive = state.Active;
            if (normalized <= 0) state.Reset();
            else if (settings == null) state.Active = true; // Legacy continuous travel.
            else if (!settings.RapidTriggerEnabled) state.Active = normalized >= settings.ActuationPoint;
            else if (!state.HasActuated)
            {
                if (normalized >= settings.ActuationPoint)
                { state.HasActuated = state.Active = true; state.Peak = normalized; }
            }
            else if (state.Active)
            {
                state.Peak = Math.Max(state.Peak, normalized);
                if (normalized < state.Peak && normalized <= state.Peak - settings.ReleaseMovement + 1e-12)
                { state.Active = false; state.Valley = normalized; }
            }
            else
            {
                state.Valley = Math.Min(state.Valley, normalized);
                if (normalized > state.Valley && normalized + 1e-12 >= state.Valley + settings.PressMovement)
                { state.Active = true; state.Peak = normalized; }
            }
            // Retain the last activation order across partial RT releases so the
            // next movement receives a newer order even if no other key is active.
            if (state.Active && !wasActive) state.PressOrder = order;
            return new KeyInputResult { Normalized = normalized, Active = state.Active, Allowed = state.Active };
        }

        private static void Inputs(IDictionary<int, double> raw, IDictionary<int, Calibration> calibrations,
            Profile profile, IDictionary<int, KeyInputState> states, ControllerFrame frame)
        {
            var settings = new Dictionary<int, KeyInputSettings>();
            foreach (KeyInputSettings input in profile.Inputs) settings.Add(input.KeyIndex, input);
            var keys = new HashSet<int>();
            foreach (Binding binding in profile.Bindings) if (binding.Enabled) keys.Add(binding.KeyIndex);
            var bound = new List<int>(keys);
            foreach (int key in bound)
            {
                KeyInputSettings input;
                if (settings.TryGetValue(key, out input) && input.OppositeKeyIndex.HasValue) keys.Add(input.OppositeKeyIndex.Value);
            }
            var removed = new List<int>();
            foreach (int key in states.Keys) if (!keys.Contains(key)) removed.Add(key);
            foreach (int key in removed) states.Remove(key);
            long order = 0;
            foreach (KeyInputState state in states.Values)
                if (state != null) order = Math.Max(order, state.PressOrder);
            if (order == long.MaxValue) { frame.Errors.Add("Die Reihenfolge der Tastendrücke muss zurückgesetzt werden."); return; }
            order++;
            foreach (int key in keys)
            {
                KeyInputState state;
                if (!states.TryGetValue(key, out state) || state == null) { state = new KeyInputState(); states[key] = state; }
                double value = 0, normalized = 0; Calibration calibration = null; string error = null;
                if (raw == null || !raw.TryGetValue(key, out value)) error = "Kein aktueller Rohwert fuer Tastenindex " + key + ".";
                else if (calibrations == null || !calibrations.TryGetValue(key, out calibration)) error = "Keine Kalibrierung fuer Tastenindex " + key + ".";
                else SignalProcessor.Normalize(value, calibration, out normalized, out error);
                if (error == null && (!MappingValidation.IsFinite(state.Peak) || state.Peak < 0 || state.Peak > 1 ||
                    !MappingValidation.IsFinite(state.Valley) || state.Valley < 0 || state.Valley > 1 || state.PressOrder < 0 || (state.Active && state.PressOrder == 0)))
                    error = "Ungültiger vorheriger Zustand der physischen Taste.";
                KeyInputSettings input; settings.TryGetValue(key, out input);
                frame.InputResults.Add(key, error == null ? ProcessInput(normalized, input, state, order) : new KeyInputResult { Error = error });
            }
            // Evaluate each symmetric pair only once, after all physical gates have
            // advanced. Same-frame activations have the same order and remain neutral.
            foreach (KeyInputSettings input in profile.Inputs)
            {
                if (!input.OppositeKeyIndex.HasValue || input.KeyIndex >= input.OppositeKeyIndex.Value) continue;
                KeyInputResult a, b;
                if (!frame.InputResults.TryGetValue(input.KeyIndex, out a) || !frame.InputResults.TryGetValue(input.OppositeKeyIndex.Value, out b) || !a.Active || !b.Active) continue;
                long first = states[input.KeyIndex].PressOrder, second = states[input.OppositeKeyIndex.Value].PressOrder;
                if (input.OppositePolicy == InputOpposedPolicy.Neutral || first == second) a.Allowed = b.Allowed = false;
                else
                {
                    bool firstWins = input.OppositePolicy == InputOpposedPolicy.FirstPressed ? first < second : first > second;
                    a.Allowed = firstWins; b.Allowed = !firstWins;
                }
            }
        }

        public static ControllerFrame Compose(IDictionary<int, double> raw, IDictionary<int, Calibration> calibrations,
            Profile profile, IDictionary<string, SignalState> states, double dtSeconds)
        {
            if (profile != null && profile.Inputs != null && profile.Inputs.Count > 0)
            {
                ResetStates(states);
                var unavailable = new ControllerFrame(); unavailable.Errors.Add("Dauerhafter Zustand für physische Tasteinstellungen fehlt."); return unavailable;
            }
            return Compose(raw, calibrations, profile, states, new Dictionary<int, KeyInputState>(), dtSeconds);
        }

        public static ControllerFrame Compose(IDictionary<int, double> raw, IDictionary<int, Calibration> calibrations,
            Profile profile, IDictionary<string, SignalState> states, IDictionary<int, KeyInputState> inputStates, double dtSeconds)
        {
            var frame = new ControllerFrame();
            frame.Errors.AddRange(MappingValidation.ValidateProfile(profile));
            if (profile != null && profile.Controllers != null && profile.Controllers.Count > 1)
                frame.Errors.Add("Mehrere Controller müssen vor der Verarbeitung einzeln zugeordnet werden.");
            if (states == null) frame.Errors.Add("Zustandsverzeichnis fehlt.");
            if (inputStates == null) frame.Errors.Add("Zustandsverzeichnis für physische Tasten fehlt.");
            if (!MappingValidation.IsFinite(dtSeconds) || dtSeconds < 0) frame.Errors.Add("Ungueltige Zeitdifferenz.");
            if (frame.Errors.Count != 0) { ResetStates(states); ResetInputStates(inputStates); return frame; }
            double[] targets = new double[10];
            // Removed bindings must not retain state and memory indefinitely.
            var activeIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (Binding binding in profile.Bindings) activeIds.Add(binding.BindingId);
            var obsolete = new List<string>();
            foreach (string id in states.Keys) if (!activeIds.Contains(id)) obsolete.Add(id);
            foreach (string id in obsolete) states.Remove(id);
            Inputs(raw, calibrations, profile, inputStates, frame);
            string inputError = null;
            foreach (KeyInputResult result in frame.InputResults.Values) if (result.Error != null) { inputError = result.Error; break; }
            if (frame.Errors.Count > 0) inputError = frame.Errors[0];
            foreach (Binding binding in profile.Bindings)
            {
                SignalState state;
                if (!states.TryGetValue(binding.BindingId, out state) || state == null) { state = new SignalState(); states[binding.BindingId] = state; }
                if (!binding.Enabled) { state.Reset(); continue; }
                SignalResult processed;
                KeyInputResult physical; frame.InputResults.TryGetValue(binding.KeyIndex, out physical);
                if (inputError != null) { state.Reset(); processed = new SignalResult { Error = physical != null && physical.Error != null ? physical.Error : inputError }; }
                else if (!physical.Allowed) { state.Reset(); processed = new SignalResult { Normalized = physical.Normalized }; }
                else processed = SignalProcessor.ProcessNormalized(physical.Normalized, binding.Processing, state, dtSeconds);
                frame.BindingResults.Add(binding.BindingId, processed);
                if (!processed.IsValid) { frame.Errors.Add(binding.BindingId + ": " + processed.Error); continue; }
                int target = (int)binding.Target;
                if (target < targets.Length) targets[target] = Merge(targets[target], processed.Final, profile.Aggregation);
                else if (processed.Final > 0 && processed.Final >= binding.Processing.ButtonThreshold) frame.Buttons |= ButtonMask(binding.Target);
            }
            frame.LeftX = Axis(targets[0], targets[1], profile.OpposedPolicy);
            frame.LeftY = Axis(targets[2], targets[3], profile.OpposedPolicy);
            frame.RightX = Axis(targets[4], targets[5], profile.OpposedPolicy);
            frame.RightY = Axis(targets[6], targets[7], profile.OpposedPolicy);
            Shape(ref frame.LeftX, ref frame.LeftY, profile.StickShape);
            Shape(ref frame.RightX, ref frame.RightY, profile.StickShape);
            frame.LeftTrigger = targets[8]; frame.RightTrigger = targets[9];
            // A D-pad cannot represent two opposing directions coherently.
            if ((frame.Buttons & 3) == 3) frame.Buttons = (ushort)(frame.Buttons & ~3);
            if ((frame.Buttons & 12) == 12) frame.Buttons = (ushort)(frame.Buttons & ~12);
            if (frame.Errors.Count > 0)
            {
                frame.LeftX = frame.LeftY = frame.RightX = frame.RightY = frame.LeftTrigger = frame.RightTrigger = 0; frame.Buttons = 0;
                foreach (SignalResult result in frame.BindingResults.Values) result.Final = 0;
                foreach (KeyInputResult result in frame.InputResults.Values) result.Active = result.Allowed = false;
                ResetStates(states); ResetInputStates(inputStates);
            }
            return frame;
        }
    }
}
