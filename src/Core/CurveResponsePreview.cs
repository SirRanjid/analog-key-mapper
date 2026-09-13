using System;
using System.Collections.Generic;

namespace Tk75.Mapping
{
    // Presentation only. Use the same processor as the controller, but disable
    // the time filter to show the settled response instead of inventing a speed
    // for the user's movement. Separate sweeps retain real Schmitt-gate history.
    public sealed class CurveResponsePreview
    {
        public double[] Press { get; private set; }
        public double[] Release { get; private set; }
        public static CurveResponsePreview Create(SignalSettings source, int intervals)
        {
            if (intervals < 2 || intervals > 4096) throw new ArgumentOutOfRangeException("intervals");
            var errors = MappingValidation.ValidateSettings(source);
            if (errors.Count != 0) throw new ArgumentException(string.Join(" ", errors.ToArray()), "source");
            var steady = Copy(source); steady.SmoothingTimeConstant = 0;
            var preview = new CurveResponsePreview { Press = new double[intervals + 1], Release = new double[intervals + 1] };
            var state = new SignalState();
            for (int i = 0; i <= intervals; i++)
                preview.Press[i] = SignalProcessor.ProcessNormalized(i / (double)intervals, steady, state, 0).Final;
            for (int i = intervals; i >= 0; i--)
                preview.Release[i] = SignalProcessor.ProcessNormalized(i / (double)intervals, steady, state, 0).Final;
            return preview;
        }
        public static SignalSettings Copy(SignalSettings value)
        {
            if (value == null) return null;
            var result = new SignalSettings { Curve = value.Curve, Exponent = value.Exponent,
                TopDeadzone = value.TopDeadzone, BottomDeadzone = value.BottomDeadzone,
                MinOutput = value.MinOutput, MaxOutput = value.MaxOutput, Scale = value.Scale,
                OutputDeadzone = value.OutputDeadzone, Hysteresis = value.Hysteresis,
                SmoothingTimeConstant = value.SmoothingTimeConstant, ButtonThreshold = value.ButtonThreshold,
                CustomPoints = value.CustomPoints == null ? null : new List<CurvePoint>() };
            if (value.CustomPoints != null) foreach (CurvePoint point in value.CustomPoints)
                result.CustomPoints.Add(point == null ? null : new CurvePoint(point.X, point.Y, point.Tangent));
            return result;
        }
        public static bool Same(SignalSettings first, SignalSettings second)
        {
            return SameResponse(first, second) && (first == null ||
                first.SmoothingTimeConstant == second.SmoothingTimeConstant && first.ButtonThreshold == second.ButtonThreshold);
        }
        // Guide/filter readouts must repaint, but do not change settled samples.
        public static bool SameResponse(SignalSettings first, SignalSettings second)
        {
            return first == null || second == null ? first == null && second == null :
                first.Curve == second.Curve && first.Exponent == second.Exponent &&
                first.TopDeadzone == second.TopDeadzone && first.BottomDeadzone == second.BottomDeadzone &&
                first.MinOutput == second.MinOutput && first.MaxOutput == second.MaxOutput && first.Scale == second.Scale &&
                first.OutputDeadzone == second.OutputDeadzone && first.Hysteresis == second.Hysteresis &&
                CurvePointEditing.Same(first.CustomPoints, second.CustomPoints);
        }
    }
}
