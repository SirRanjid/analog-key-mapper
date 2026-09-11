using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using Tk75.Mapping;

namespace Tk75.App
{
    internal enum ControllerRegionKind { Trigger, Shoulder, System, Face, Dpad, StickDirection, StickClick }

    // One path definition is used by painting, highlighting and hit testing.
    internal sealed class ControllerRegion
    {
        public readonly OutputTarget Target;
        public readonly ControllerRegionKind Kind;
        public readonly RectangleF Bounds;
        public readonly PointF Center;
        public readonly int Direction;
        readonly PointF[] polygon;
        public ControllerRegion(OutputTarget target, ControllerRegionKind kind, RectangleF bounds, int direction, PointF[] polygon)
        { Target = target; Kind = kind; Bounds = bounds; Center = new PointF(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2); Direction = direction; this.polygon = polygon; }
        public GraphicsPath CreatePath()
        {
            GraphicsPath path = new GraphicsPath();
            if (polygon != null) { path.AddPolygon(polygon); return path; }
            if (Kind == ControllerRegionKind.Face || Kind == ControllerRegionKind.StickClick) { path.AddEllipse(Bounds); return path; }
            if (Kind == ControllerRegionKind.StickDirection)
            {
                float start = Direction * 90 - 42;
                RectangleF inner = new RectangleF(Center.X - 15, Center.Y - 15, 30, 30);
                path.AddArc(Bounds, start, 84); path.AddArc(inner, start + 84, -84); path.CloseFigure(); return path;
            }
            path.Dispose(); return ControllerLayout.Rounded(Bounds, Kind == ControllerRegionKind.Trigger ? 7 : Bounds.Height / 2);
        }
        public PointF LabelCenter
        {
            get
            {
                if (Kind != ControllerRegionKind.StickDirection) return Center;
                double angle = Direction * Math.PI / 2;
                return new PointF(Center.X + (float)Math.Cos(angle) * 25, Center.Y + (float)Math.Sin(angle) * 25);
            }
        }
    }

    internal sealed class ControllerLayout
    {
        public const float Width = 440, Height = 235;
        public readonly ControllerStyle Style;
        public readonly PointF LeftStick, RightStick;
        public readonly ReadOnlyCollection<ControllerRegion> Regions;
        readonly Dictionary<OutputTarget, ControllerRegion> byTarget = new Dictionary<OutputTarget, ControllerRegion>();
        public ControllerLayout(ControllerStyle style)
        {
            ControllerPresentation.Validate(style); Style = style;
            bool ps = style == ControllerStyle.PlayStation5;
            LeftStick = ps ? new PointF(145, 157) : new PointF(108, 106);
            RightStick = ps ? new PointF(295, 157) : new PointF(282, 163);
            List<ControllerRegion> regions = new List<ControllerRegion>();
            Add(regions, OutputTarget.LeftTrigger, ControllerRegionKind.Trigger, 79, 7, 90, 27);
            Add(regions, OutputTarget.RightTrigger, ControllerRegionKind.Trigger, 271, 7, 90, 27);
            Add(regions, OutputTarget.LB, ControllerRegionKind.Shoulder, 79, 39, 90, ps ? 16 : 18);
            Add(regions, OutputTarget.RB, ControllerRegionKind.Shoulder, 271, 39, 90, ps ? 16 : 18);
            Add(regions, OutputTarget.Back, ControllerRegionKind.System, ps ? 133 : 192, ps ? 65 : 96, 37, ps ? 17 : 20);
            Add(regions, OutputTarget.Start, ControllerRegionKind.System, ps ? 270 : 237, ps ? 65 : 96, 39, ps ? 17 : 20);
            PointF face = ps ? new PointF(344, 95) : new PointF(350, 106);
            Add(regions, OutputTarget.Y, ControllerRegionKind.Face, face.X - 12, face.Y - 39, 24, 24);
            Add(regions, OutputTarget.A, ControllerRegionKind.Face, face.X - 12, face.Y + 15, 24, 24);
            Add(regions, OutputTarget.X, ControllerRegionKind.Face, face.X - 39, face.Y - 12, 24, 24);
            Add(regions, OutputTarget.B, ControllerRegionKind.Face, face.X + 15, face.Y - 12, 24, 24);
            AddStick(regions, LeftStick, true); AddStick(regions, RightStick, false);
            AddDpad(regions, ps ? new PointF(96, 95) : new PointF(163, 165));
            foreach (ControllerRegion region in regions) byTarget.Add(region.Target, region);
            // New outputs must be given an explicit visible region, never a fallback.
            if (regions.Count != Enum.GetValues(typeof(OutputTarget)).Length) throw new InvalidOperationException("Controller layout does not cover every output target.");
            Regions = regions.AsReadOnly();
        }
        public ControllerRegion Find(OutputTarget target) { ControllerRegion region; return byTarget.TryGetValue(target, out region) ? region : null; }
        public OutputTarget? HitTest(PointF point)
        {
            foreach (ControllerRegion region in Regions)
                using (GraphicsPath path = region.CreatePath()) if (path.IsVisible(point)) return region.Target;
            return null;
        }
        static void Add(List<ControllerRegion> regions, OutputTarget target, ControllerRegionKind kind, float x, float y, float width, float height)
        { regions.Add(new ControllerRegion(target, kind, new RectangleF(x, y, width, height), 0, null)); }
        static void AddStick(List<ControllerRegion> regions, PointF center, bool left)
        {
            OutputTarget[] targets = left
                ? new[] { OutputTarget.LeftXPositive, OutputTarget.LeftYNegative, OutputTarget.LeftXNegative, OutputTarget.LeftYPositive }
                : new[] { OutputTarget.RightXPositive, OutputTarget.RightYNegative, OutputTarget.RightXNegative, OutputTarget.RightYPositive };
            for (int direction = 0; direction < targets.Length; direction++)
                regions.Add(new ControllerRegion(targets[direction], ControllerRegionKind.StickDirection, new RectangleF(center.X - 35, center.Y - 35, 70, 70), direction, null));
            Add(regions, left ? OutputTarget.LeftThumb : OutputTarget.RightThumb, ControllerRegionKind.StickClick, center.X - 12, center.Y - 12, 24, 24);
        }
        static void AddDpad(List<ControllerRegion> regions, PointF center)
        {
            OutputTarget[] targets = { OutputTarget.DpadRight, OutputTarget.DpadDown, OutputTarget.DpadLeft, OutputTarget.DpadUp };
            PointF[] shape = { new PointF(5, -7), new PointF(24, -7), new PointF(24, 7), new PointF(5, 7), new PointF(1, 0) };
            for (int direction = 0; direction < targets.Length; direction++)
            {
                double angle = direction * Math.PI / 2; PointF[] points = new PointF[shape.Length];
                for (int i = 0; i < shape.Length; i++) points[i] = new PointF(center.X + (float)(shape[i].X * Math.Cos(angle) - shape[i].Y * Math.Sin(angle)), center.Y + (float)(shape[i].X * Math.Sin(angle) + shape[i].Y * Math.Cos(angle)));
                float minX = Single.MaxValue, minY = Single.MaxValue, maxX = Single.MinValue, maxY = Single.MinValue;
                foreach (PointF point in points) { minX = Math.Min(minX, point.X); minY = Math.Min(minY, point.Y); maxX = Math.Max(maxX, point.X); maxY = Math.Max(maxY, point.Y); }
                regions.Add(new ControllerRegion(targets[direction], ControllerRegionKind.Dpad, RectangleF.FromLTRB(minX, minY, maxX, maxY), direction, points));
            }
        }
        public GraphicsPath CreateBodyPath()
        {
            GraphicsPath path = new GraphicsPath();
            if (Style == ControllerStyle.PlayStation5)
            {
                path.AddBezier(77, 53, 111, 45, 148, 61, 174, 61);
                path.AddBezier(174, 61, 207, 58, 233, 58, 266, 61);
                path.AddBezier(266, 61, 292, 61, 329, 45, 363, 53);
                path.AddBezier(363, 53, 392, 77, 412, 128, 422, 197);
                path.AddBezier(422, 197, 424, 232, 401, 237, 377, 214);
                path.AddBezier(377, 214, 356, 194, 340, 176, 317, 174);
                path.AddBezier(317, 174, 265, 195, 175, 195, 123, 174);
                path.AddBezier(123, 174, 100, 176, 84, 194, 63, 214);
                path.AddBezier(63, 214, 39, 237, 16, 232, 18, 197);
                path.AddBezier(18, 197, 28, 128, 48, 77, 77, 53);
            }
            else
            {
                path.AddBezier(82, 53, 113, 45, 154, 59, 176, 62);
                path.AddBezier(176, 62, 205, 69, 235, 69, 264, 62);
                path.AddBezier(264, 62, 286, 59, 327, 45, 358, 53);
                path.AddBezier(358, 53, 391, 65, 404, 111, 419, 185);
                path.AddBezier(419, 185, 426, 226, 404, 238, 378, 215);
                path.AddBezier(378, 215, 350, 187, 331, 179, 314, 188);
                path.AddBezier(314, 188, 266, 204, 174, 204, 126, 188);
                path.AddBezier(126, 188, 109, 179, 90, 187, 62, 215);
                path.AddBezier(62, 215, 36, 238, 14, 226, 21, 185);
                path.AddBezier(21, 185, 36, 111, 49, 65, 82, 53);
            }
            path.CloseFigure(); return path;
        }
        internal static GraphicsPath Rounded(RectangleF bounds, float radius)
        {
            float diameter = Math.Max(.5f, Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height)));
            GraphicsPath path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90); path.CloseFigure(); return path;
        }
    }
}
