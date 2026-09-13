using System;

namespace Tk75.App
{
    // Shared pointer arithmetic only. Each gesture retains its unrounded value;
    // formatting and a control's permitted value steps never feed back into it.
    internal struct SliderDragPrecision
    {
        double value, originValue, previousAxis, perpendicularAxis, uiScale;
        public double Value { get { return value; } }

        public void Begin(double startValue, double axis, double trackPerpendicular, double scale)
        {
            value = originValue = startValue; previousAxis = axis; perpendicularAxis = trackPerpendicular;
            uiScale = Math.Max(1, scale);
        }

        public static double Sensitivity(double perpendicularDistance, double scale)
        {
            // The first 24 logical pixels retain direct manipulation. Outside
            // that band the gain decreases smoothly: +64px = 1/2, +128px = 1/5.
            double distance = Math.Max(0, Math.Abs(perpendicularDistance) / Math.Max(1, scale) - 24) / 64;
            return 1 / (1 + distance * distance);
        }

        public bool Move(double axis, double perpendicular, double unitsPerPixel, double minimum, double maximum)
        {
            double delta = axis - previousAxis; previousAxis = axis;
            if (delta == 0) return false;
            value = Math.Max(minimum, Math.Min(maximum,
                value + delta * unitsPerPixel * Sensitivity(perpendicular - perpendicularAxis, uiScale)));
            return true;
        }

        public double QuantizedValue(double step, double minimum, double maximum)
        {
            // A manually entered value need not lie on a slider's usual step
            // grid. Quantize the *movement* from its exact pickup value, so a
            // tiny forward drag can never round an existing value backwards.
            if (value <= minimum) return minimum;
            if (value >= maximum) return maximum;
            return Math.Max(minimum, Math.Min(maximum,
                originValue + Math.Round((value - originValue) / step) * step));
        }
    }
}
