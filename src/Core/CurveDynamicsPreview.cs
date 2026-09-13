using System;
using System.Collections.Generic;

namespace Tk75.Mapping
{
    // Independent, finite presentation samples. No clock, animation, runtime
    // state or hardware is shared with these illustrative diagrams.
    public sealed class SmoothingStepPreview
    {
        public double DurationSeconds { get; private set; }
        public double SettledOutput { get; private set; }
        public double[] Output { get; private set; }
        public static SmoothingStepPreview Create(SignalSettings settings, double durationSeconds, int intervals)
        {
            if (!MappingValidation.IsFinite(durationSeconds) || durationSeconds <= 0) throw new ArgumentOutOfRangeException("durationSeconds");
            if (intervals < 2 || intervals > 4096) throw new ArgumentOutOfRangeException("intervals");
            var errors = MappingValidation.ValidateSettings(settings);
            if (errors.Count != 0) throw new ArgumentException(string.Join(" ", errors.ToArray()), "settings");
            var steady = CurveResponsePreview.Copy(settings); steady.SmoothingTimeConstant = 0;
            var result = new SmoothingStepPreview { DurationSeconds = durationSeconds,
                SettledOutput = SignalProcessor.ProcessNormalized(1, steady, new SignalState(), 0).Final, Output = new double[intervals + 1] };
            for (int i = 0; i <= intervals; i++)
                result.Output[i] = SignalProcessor.ProcessNormalized(1, settings, new SignalState(), durationSeconds * i / intervals).Final;
            return result;
        }
    }

    public sealed class RapidTriggerMovementPreview
    {
        public double FromPressure { get; private set; }
        public double ToPressure { get; private set; }
        public bool RequiresFreshActuation { get; private set; }
        public List<CurvePoint> Path { get; private set; }
        public static RapidTriggerMovementPreview Create(KeyInputSettings settings, InputActivationFields field)
        {
            var errors = MappingValidation.ValidateInputSettings(settings);
            if (errors.Count != 0) throw new ArgumentException(string.Join(" ", errors.ToArray()), "settings");
            if (field != InputActivationFields.Release && field != InputActivationFields.Press) throw new ArgumentOutOfRangeException("field");
            var result = new RapidTriggerMovementPreview();
            if (field == InputActivationFields.Release)
            {
                result.FromPressure = 1; result.ToPressure = 1 - settings.ReleaseMovement;
                result.Path = new List<CurvePoint> { new CurvePoint(0, 0), new CurvePoint(.32, 1), new CurvePoint(.88, result.ToPressure), new CurvePoint(1, result.ToPressure) };
            }
            else
            {
                // Begin above rest after a release long enough to deactivate.
                // If either distance requires all travel, rest necessarily
                // resets Rapid Trigger: do not invent a relative retrigger.
                result.FromPressure = Math.Max(0, Math.Min(.25, Math.Min((1 - settings.ActuationPoint) / 2, 1 - settings.ReleaseMovement)));
                result.ToPressure = result.FromPressure + settings.ActuationPoint;
                result.RequiresFreshActuation = result.FromPressure <= 0;
                result.Path = new List<CurvePoint> { new CurvePoint(0, 0), new CurvePoint(.2, 1), new CurvePoint(.52, result.FromPressure), new CurvePoint(.88, result.ToPressure), new CurvePoint(1, result.ToPressure) };
            }
            return result;
        }
    }
}
