using System;
using System.Collections.Generic;
using System.Linq;

namespace Tk75.Mapping
{
    // Pure local gesture state. Preview points never mutate the profile; Finish
    // returns at most one changed snapshot, while Cancel returns to the source.
    public sealed class CurvePointEditing
    {
        readonly List<CurvePoint> original;
        List<CurvePoint> points;
        double minimumX, maximumX;
        double handleSpan, originalSlope;
        bool outgoing;
        public bool Active { get; private set; }
        public bool EditingHandle { get; private set; }
        public int SelectedIndex { get; private set; }
        public List<CurvePoint> Preview { get { return Copy(points); } }

        public CurvePointEditing(IEnumerable<CurvePoint> source)
        {
            if (source == null) throw new ArgumentNullException("source");
            original = Copy(source);
            if (original.Count < 2 || original.Count > 64) throw new ArgumentException("A curve needs 2..64 points.", "source");
            for (int i = 0; i < original.Count; i++)
            {
                CurvePoint point = original[i];
                if (!Unit(point.X) || !Unit(point.Y) || (point.Tangent.HasValue && (!Finite(point.Tangent.Value) || point.Tangent.Value < 0)) ||
                    (i > 0 && (point.X <= original[i - 1].X || point.Y < original[i - 1].Y)))
                    throw new ArgumentException("Curve coordinates must be finite, bounded and monotone.", "source");
            }
            if (original[0].X != 0 || original[0].Y != 0 || original[original.Count - 1].X != 1 || original[original.Count - 1].Y != 1)
                throw new ArgumentException("Curve endpoints must stay at (0,0) and (1,1).", "source");
            points = Copy(original); SelectedIndex = -1;
        }
        static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        static bool Unit(double value) { return Finite(value) && value >= 0 && value <= 1; }
        static double Clamp(double value, double low, double high) { return Math.Max(low, Math.Min(high, value)); }
        static List<CurvePoint> Copy(IEnumerable<CurvePoint> source)
        { return source.Select(p => p == null ? throwNullPoint() : new CurvePoint(p.X, p.Y) { Tangent = p.Tangent }).ToList(); }
        static CurvePoint throwNullPoint() { throw new ArgumentException("Curve points cannot be null."); }

        public int HitTest(double pixelX, double pixelY, double left, double top, double width, double height, double radius)
        {
            if (!Finite(pixelX) || !Finite(pixelY) || !Finite(left) || !Finite(top) || !Finite(width) || !Finite(height) ||
                !Finite(radius) || width <= 0 || height <= 0 || radius < 0) return -1;
            int nearest = -1; double best = radius * radius;
            for (int i = 0; i < points.Count; i++)
            {
                double dx = left + points[i].X * width - pixelX, dy = top + (1 - points[i].Y) * height - pixelY;
                double distance = dx * dx + dy * dy;
                if (distance <= best) { best = distance; nearest = i; }
            }
            return nearest;
        }

        // Result: -1 none, 0 node, 1 incoming handle, 2 outgoing handle. The
        // nearest visible shape wins in screen pixels; nodes win exact ties.
        public int HitPart(int selectedIndex, double pixelX, double pixelY, double left, double top, double width, double height, double radius, out int pointIndex)
        {
            pointIndex = HitTest(pixelX, pixelY, left, top, width, height, radius);
            if (!Finite(pixelX) || !Finite(pixelY) || !Finite(left) || !Finite(top) || !Finite(width) || !Finite(height) || !Finite(radius) || width <= 0 || height <= 0 || radius < 0) return -1;
            int part = pointIndex < 0 ? -1 : 0;
            double best = pointIndex < 0 ? radius * radius : PixelDistance(points[pointIndex], pixelX, pixelY, left, top, width, height);
            if (selectedIndex < 0 || selectedIndex >= points.Count) return part;
            for (int side = 0; side < 2; side++)
            {
                CurvePoint handle = GetHandle(selectedIndex, side == 1); if (handle == null) continue;
                double distance = PixelDistance(handle, pixelX, pixelY, left, top, width, height);
                if (distance < best || distance == best && part < 0)
                { best = distance; part = side + 1; pointIndex = selectedIndex; }
            }
            return part;
        }
        static double PixelDistance(CurvePoint point, double x, double y, double left, double top, double width, double height)
        { double dx = left + point.X * width - x, dy = top + (1 - point.Y) * height - y; return dx * dx + dy * dy; }
        public CurvePoint GetHandle(int index, bool isOutgoing) { return BezierCurve.GetHandle(points, index, isOutgoing); }
        public bool BeginHandle(int index, bool isOutgoing)
        {
            if (Active || index < 0 || index >= points.Count) return false;
            CurvePoint handle = GetHandle(index, isOutgoing); if (handle == null) return false;
            handleSpan = Math.Abs(handle.X - points[index].X); if (handleSpan == 0) return false;
            SelectedIndex = index; outgoing = isOutgoing; originalSlope = BezierCurve.EffectiveTangent(points, index);
            EditingHandle = Active = true; return true;
        }

        public bool Begin(int hitIndex, double x, double y, double minimumSpacing)
        {
            if (Active || !Finite(x) || !Finite(y) || !Finite(minimumSpacing) || minimumSpacing <= 0 || minimumSpacing >= .5) return false;
            if (hitIndex < -1 || hitIndex >= points.Count || hitIndex == 0 || hitIndex == points.Count - 1) return false;
            if (hitIndex < 0)
            {
                if (points.Count >= 64) return false;
                x = Clamp(x, minimumSpacing, 1 - minimumSpacing);
                int after = 1; while (after < points.Count - 1 && points[after].X < x) after++;
                double low = points[after - 1].X + minimumSpacing, high = points[after].X - minimumSpacing;
                if (low > high || low <= points[after - 1].X || high >= points[after].X) return false;
                points.Insert(after, new CurvePoint(Clamp(x, low, high), Clamp(y, points[after - 1].Y, points[after].Y)));
                hitIndex = after;
            }
            SelectedIndex = hitIndex;
            double current = points[hitIndex].X;
            double spacing = Math.Min(minimumSpacing, Math.Min(current - points[hitIndex - 1].X, points[hitIndex + 1].X - current) / 2);
            minimumX = points[hitIndex - 1].X + spacing; maximumX = points[hitIndex + 1].X - spacing;
            // Dense imported curves can be only one representable double apart.
            if (minimumX <= points[hitIndex - 1].X) minimumX = current;
            if (maximumX >= points[hitIndex + 1].X) maximumX = current;
            EditingHandle = false; Active = true; return true;
        }
        public bool Move(double x, double y)
        {
            if (!Active || !Finite(x) || !Finite(y)) return false;
            if (EditingHandle) return MoveHandle(x, y);
            var current = points[SelectedIndex];
            x = Clamp(x, minimumX, maximumX);
            y = Clamp(y, points[SelectedIndex - 1].Y, points[SelectedIndex + 1].Y);
            if (current.X == x && current.Y == y) return false;
            current.X = x; current.Y = y; return true;
        }
        bool MoveHandle(double x, double y)
        {
            CurvePoint point = points[SelectedIndex]; double sign = outgoing ? 1 : -1;
            // The cursor changes direction/slope, never the handle's segment/3
            // X-length. A tiny positive denominator avoids a vertical infinity.
            double dy = (y - point.Y) * sign, dx = Math.Max(handleSpan * .01, (x - point.X) * sign);
            double requested = dy <= 0 ? 0 : dy / dx; if (!Finite(requested)) requested = Double.MaxValue;
            double slope = BezierCurve.ClampTangent(points, SelectedIndex, requested);
            double? tangent = Math.Abs(slope - originalSlope) <= 1e-12 * Math.Max(1, originalSlope) ? original[SelectedIndex].Tangent : (double?)slope;
            if (point.Tangent == tangent) return false;
            point.Tangent = tangent; return true;
        }
        public List<CurvePoint> Finish()
        {
            if (!Active) return null;
            Active = EditingHandle = false; SelectedIndex = -1;
            return Same(points, original) ? null : Copy(points);
        }
        public void Cancel() { Active = EditingHandle = false; SelectedIndex = -1; points = Copy(original); }
        public List<CurvePoint> ResetTangent(int index)
        {
            if (Active || index < 0 || index >= points.Count || !points[index].Tangent.HasValue) return null;
            var result = Copy(points); result[index].Tangent = null; return result;
        }
        public List<CurvePoint> Remove(int index)
        {
            if (Active || index <= 0 || index >= points.Count - 1) return null;
            var result = Copy(points); result.RemoveAt(index); return result;
        }
        public static bool Same(IList<CurvePoint> first, IList<CurvePoint> second)
        {
            if (first == null || second == null) return first == null && second == null;
            if (first.Count != second.Count) return false;
            for (int i = 0; i < first.Count; i++)
                if (first[i] == null || second[i] == null || first[i].X != second[i].X || first[i].Y != second[i].Y || first[i].Tangent != second[i].Tangent) return false;
            return true;
        }
    }
}
