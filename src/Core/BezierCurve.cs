using System;
using System.Collections.Generic;

namespace Tk75.Mapping
{
    /// <summary>
    /// Piecewise cubic Bezier curve through the supplied points. The X handles
    /// lie at segment thirds, so X(t) is linear and evaluation needs no root
    /// search. Each knot has one slope shared by both adjacent segments (C1).
    /// No method mutates the points or replaces a stored automatic tangent.
    /// </summary>
    public static class BezierCurve
    {
        private static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
        private static void RequirePoints(IList<CurvePoint> points)
        {
            if (points == null || points.Count < 2 || points.Count > 64)
                throw new ArgumentException("A Bezier curve requires 2..64 points.", "points");
            for (int i = 0; i < points.Count; i++)
            {
                CurvePoint p = points[i];
                if (p == null || !Finite(p.X) || !Finite(p.Y) || p.X < 0 || p.X > 1 || p.Y < 0 || p.Y > 1 ||
                    (p.Tangent.HasValue && (!Finite(p.Tangent.Value) || p.Tangent.Value < 0)))
                    throw new ArgumentException("Curve points and tangents must be finite and valid.", "points");
                if (i == 0 && (p.X != 0 || p.Y != 0) || i == points.Count - 1 && (p.X != 1 || p.Y != 1))
                    throw new ArgumentException("Curve endpoints must be (0,0) and (1,1).", "points");
                if (i > 0 && (p.X <= points[i - 1].X || p.Y < points[i - 1].Y))
                    throw new ArgumentException("Curve X must increase strictly and Y must not decrease.", "points");
            }
        }
        private static void RequireIndex(IList<CurvePoint> points, int index)
        { if (index < 0 || index >= points.Count) throw new ArgumentOutOfRangeException("index"); }

        // Saturation is required for valid, representable X gaps whose secant
        // slope exceeds Double.MaxValue. It never produces infinity or NaN.
        private static double Secant(CurvePoint left, CurvePoint right)
        {
            double slope = (right.Y - left.Y) / (right.X - left.X);
            return Double.IsPositiveInfinity(slope) ? Double.MaxValue : slope;
        }
        private static double Triple(double value)
        { return value > Double.MaxValue / 3 ? Double.MaxValue : value * 3; }
        private static double Limit(IList<CurvePoint> points, int index)
        {
            double adjacent = index == 0 ? Secant(points[0], points[1]) : Secant(points[index - 1], points[index]);
            if (index + 1 < points.Count) adjacent = Math.Min(adjacent, Secant(points[index], points[index + 1]));
            return Triple(adjacent);
        }
        private static double Automatic(IList<CurvePoint> points, int index)
        {
            if (index == 0) return Secant(points[0], points[1]);
            if (index == points.Count - 1) return Secant(points[index - 1], points[index]);
            double a = Secant(points[index - 1], points[index]), b = Secant(points[index], points[index + 1]);
            double low = Math.Min(a, b), high = Math.Max(a, b);
            if (low == 0) return 0;
            // Stable harmonic mean: avoids both reciprocal underflow and a*b
            // overflow, including subnormal X gaps and almost-flat segments.
            double factor = 2 / (1 + low / high);
            return low > Double.MaxValue / factor ? Double.MaxValue : low * factor;
        }
        private static double TangentUnchecked(IList<CurvePoint> points, int index)
        { return Math.Min(points[index].Tangent ?? Automatic(points, index), Limit(points, index)); }

        /// <summary>Detached effective slopes; automatic/manual requests remain unchanged.</summary>
        public static double[] EffectiveTangents(IList<CurvePoint> points)
        {
            RequirePoints(points);
            var result = new double[points.Count];
            for (int i = 0; i < result.Length; i++) result[i] = TangentUnchecked(points, i);
            return result;
        }
        public static double EffectiveTangent(IList<CurvePoint> points, int index)
        { RequirePoints(points); RequireIndex(points, index); return TangentUnchecked(points, index); }

        /// <summary>
        /// Clamp an edit without changing any point. The independent bound is
        /// stable when the returned slope is stored and then evaluated again.
        /// </summary>
        public static double ClampTangent(IList<CurvePoint> points, int index, double requestedSlope)
        {
            RequirePoints(points); RequireIndex(points, index);
            if (!Finite(requestedSlope) || requestedSlope < 0) throw new ArgumentOutOfRangeException("requestedSlope");
            return Math.Min(requestedSlope, Limit(points, index));
        }

        private static double HandleY(CurvePoint left, CurvePoint right, double slope, bool outgoing)
        {
            // Multiply before dividing so a subnormal gap is not rounded to
            // zero before multiplication by its (possibly large) slope.
            double distance = (slope * (right.X - left.X)) / 3;
            double value = outgoing ? left.Y + distance : right.Y - distance;
            return Math.Max(left.Y, Math.Min(right.Y, value));
        }
        /// <summary>Detached incoming/outgoing control point; null on the missing outer side.</summary>
        public static CurvePoint GetHandle(IList<CurvePoint> points, int index, bool outgoing)
        {
            RequirePoints(points); RequireIndex(points, index);
            if (outgoing && index == points.Count - 1 || !outgoing && index == 0) return null;
            CurvePoint left = points[outgoing ? index : index - 1], right = points[outgoing ? index + 1 : index];
            double width = right.X - left.X;
            double x = outgoing ? left.X + width / 3 : right.X - width / 3;
            return new CurvePoint(x, HandleY(left, right, TangentUnchecked(points, index), outgoing));
        }

        private static double Mix(double a, double b, double t) { return a + (b - a) * t; }
        public static double Evaluate(IList<CurvePoint> points, double x)
        {
            RequirePoints(points);
            if (!Finite(x)) throw new ArgumentOutOfRangeException("x");
            if (x <= 0) return 0;
            if (x >= 1) return 1;
            for (int i = 1; i < points.Count; i++)
            {
                CurvePoint left = points[i - 1], right = points[i];
                if (x > right.X) continue;
                if (x == left.X) return left.Y;
                if (x == right.X) return right.Y;
                double t = (x - left.X) / (right.X - left.X);
                double c1 = HandleY(left, right, TangentUnchecked(points, i - 1), true);
                double c2 = HandleY(left, right, TangentUnchecked(points, i), false);
                double a = Mix(left.Y, c1, t), b = Mix(c1, c2, t), c = Mix(c2, right.Y, t);
                double y = Mix(Mix(a, b, t), Mix(b, c, t), t);
                // The tangent box 0 <= m_left,m_right <= 3*secant guarantees
                // monotonicity: its four derivative corner polynomials are
                // 6t(1-t), 3(1-t)^2, 3t^2 and 3(2t-1)^2, all nonnegative.
                // Shared slopes satisfy both adjacent boxes, including flats.
                return Math.Max(left.Y, Math.Min(right.Y, y));
            }
            return 1;
        }
    }
}
