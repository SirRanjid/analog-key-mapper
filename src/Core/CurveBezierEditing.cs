using System;
using System.Collections.Generic;

namespace Tk75.Mapping
{
    /// <summary>
    /// Builds a detached, editable shape only when the editor needs it. This is
    /// not part of input processing. Linear, smoothstep and powers 1, 2 and 3 are
    /// exact cubics; other analytic shapes are numerical approximations. The
    /// adaptive fit targets 0.05 percentage points of shape output, probing both
    /// input and output coordinates so steep endpoint regions are not skipped.
    /// The existing 64-knot storage limit remains a hard upper bound.
    /// </summary>
    public static class CurveBezierEditing
    {
        public const double FitTolerance = .0005;
        const int Probes = 32;

        public static List<CurvePoint> Create(SignalSettings source)
        {
            List<CurvePoint> result;
            if (!TryCreate(source, out result)) throw new InvalidOperationException("This shape cannot be accurately represented within the editable Bezier point limit.");
            return result;
        }

        /// <summary>
        /// Refuses a fit if its input/output and endpoint probes still exceed
        /// tolerance at the point budget. No inaccurate draft is returned.
        /// Unsupported/invalid input is a normal false result for UI refreshes.
        /// </summary>
        public static bool TryCreate(SignalSettings source, out List<CurvePoint> result)
        {
            result = null;
            if (source == null || !Finite(source.Exponent) || source.Exponent <= 0) return false;
            if (source.Curve == CurveKind.Custom || source.Curve == CurveKind.Bezier)
            {
                // Existing user curves retain their original knots and handles.
                try { BezierCurve.EffectiveTangents(source.CustomPoints); }
                catch (ArgumentException) { return false; }
                var copy = new List<CurvePoint>();
                foreach (CurvePoint point in source.CustomPoints) copy.Add(new CurvePoint(point.X, point.Y, point.Tangent));
                result = copy; return true;
            }
            if (!Enum.IsDefined(typeof(CurveKind), source.Curve)) return false;
            var points = new List<CurvePoint> { Knot(source, 0), Knot(source, 1) };
            while (true)
            {
                double worst = FitTolerance, split = -1; int after = -1;
                for (int segment = 1; segment < points.Count; segment++)
                {
                    CurvePoint left = points[segment - 1], right = points[segment];
                    // Explicit representable endpoint limits protect very small
                    // imported powers and steep changes next to one. Inverse
                    // output probes can underflow/round away these locations.
                    if (left.X == 0) Inspect(source, points, Double.Epsilon, segment, ref worst, ref split, ref after);
                    if (right.X == 1) Inspect(source, points, 1 - 1.1102230246251565e-16, segment, ref worst, ref split, ref after);
                    for (int probe = 1; probe < Probes; probe++)
                    {
                        double t = probe / (double)Probes;
                        Inspect(source, points, left.X + (right.X - left.X) * t, segment, ref worst, ref split, ref after);
                        // Inverse probes also see fractional powers near zero
                        // and large powers near one, where uniform X misses detail.
                        if (source.Curve == CurveKind.Exponential || source.Curve == CurveKind.Logarithmic)
                            Inspect(source, points, Inverse(source, left.Y + (right.Y - left.Y) * t), segment, ref worst, ref split, ref after);
                    }
                }
                if (after < 0) { result = points; return true; }
                if (points.Count >= 64) return false;
                points.Insert(after, Knot(source, split));
            }
        }

        static void Inspect(SignalSettings source, List<CurvePoint> points, double x, int segment,
            ref double worst, ref double split, ref int after)
        {
            if (!Finite(x) || x <= points[segment - 1].X || x >= points[segment].X) return;
            double error = Math.Abs(Value(source, x) - BezierCurve.Evaluate(points, x));
            if (error > worst) { worst = error; split = x; after = segment; }
        }
        static bool Finite(double value) { return !Double.IsInfinity(value) && !Double.IsNaN(value); }
        static double Unit(double value) { return Math.Max(0, Math.Min(1, value)); }
        static CurvePoint Knot(SignalSettings source, double x)
        {
            double tangent;
            switch (source.Curve)
            {
                case CurveKind.Exponential:
                    tangent = source.Exponent == 1 ? 1 : x == 0 ? source.Exponent < 1 ? Double.MaxValue : 0 : source.Exponent * Math.Pow(x, source.Exponent - 1);
                    break;
                case CurveKind.Logarithmic:
                    tangent = source.Exponent < .00000001 ? 1 : (source.Exponent / (1 + source.Exponent * x)) / Math.Log(1 + source.Exponent);
                    break;
                case CurveKind.Smoothstep: tangent = 6 * x * (1 - x); break;
                default: tangent = 1; break;
            }
            if (!Finite(tangent)) tangent = Double.MaxValue;
            return new CurvePoint(x, Value(source, x), Math.Max(0, tangent));
        }
        static double Value(SignalSettings source, double x)
        {
            if (x <= 0) return 0; if (x >= 1) return 1;
            switch (source.Curve)
            {
                case CurveKind.Exponential: return Unit(Math.Pow(x, source.Exponent));
                case CurveKind.Logarithmic: return source.Exponent < .00000001 ? x : Unit(Math.Log(1 + source.Exponent * x) / Math.Log(1 + source.Exponent));
                case CurveKind.Smoothstep: return x * x * (3 - 2 * x);
                default: return x;
            }
        }
        static double Inverse(SignalSettings source, double y)
        {
            if (source.Curve == CurveKind.Exponential) return Math.Pow(y, 1 / source.Exponent);
            return source.Exponent < .00000001 ? y : (Math.Exp(y * Math.Log(1 + source.Exponent)) - 1) / source.Exponent;
        }
    }
}
