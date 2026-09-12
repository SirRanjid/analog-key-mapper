using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using Tk75.Mapping;

namespace Tk75.App
{
    public sealed class ControllerKeyAssignment
    {
        public readonly OutputTarget Target;
        public readonly int KeyIndex;
        public readonly string Legend, Description, KeyCode;
        public readonly bool Enabled;
        public ControllerKeyAssignment(OutputTarget target, int keyIndex, string legend, string description, bool enabled)
            : this(target, keyIndex, legend, description, enabled, null) { }
        public ControllerKeyAssignment(OutputTarget target, int keyIndex, string legend, string description, bool enabled, string keyCode)
        {
            if (!Enum.IsDefined(typeof(OutputTarget), target)) throw new ArgumentOutOfRangeException("target");
            if (keyIndex < 0 || keyIndex > 255) throw new ArgumentOutOfRangeException("keyIndex");
            if (String.IsNullOrWhiteSpace(legend)) throw new ArgumentException("A key legend is required.", "legend");
            Target = target; KeyIndex = keyIndex; Legend = legend; KeyCode = keyCode;
            Description = String.IsNullOrWhiteSpace(description) ? legend : description; Enabled = enabled;
        }
    }

    public sealed partial class ControllerPreview
    {
        sealed class KeyAssignmentLabel
        {
            public string Legend, Description, Side;
            public bool Enabled;
            public int Count;
            public int VisibleCount = 1;
            public bool Modifier;
            public RectangleF Bounds;
            public bool HasLeader;
            public PointF LeaderStart, LeaderEnd;
        }
        sealed class AssignmentObstacle
        {
            public RectangleF Bounds;
            public RectangleF[] Spans;
        }
        readonly Dictionary<OutputTarget, KeyAssignmentLabel> keyAssignments = new Dictionary<OutputTarget, KeyAssignmentLabel>();

        // Only edits, layout changes and controller selection update this cache.
        // Live controller frames never enumerate a profile or build key labels.
        public void SetKeyAssignments(IEnumerable<ControllerKeyAssignment> assignments)
        {
            if (assignments == null) throw new ArgumentNullException("assignments");
            var grouped = new Dictionary<OutputTarget, SortedDictionary<int, ControllerKeyAssignment>>();
            foreach (ControllerKeyAssignment item in assignments)
            {
                if (item == null) throw new ArgumentException("A key assignment cannot be null.", "assignments");
                SortedDictionary<int, ControllerKeyAssignment> keys;
                if (!grouped.TryGetValue(item.Target, out keys)) grouped.Add(item.Target, keys = new SortedDictionary<int, ControllerKeyAssignment>());
                ControllerKeyAssignment previous;
                if (!keys.TryGetValue(item.KeyIndex, out previous) || item.Enabled && !previous.Enabled) keys[item.KeyIndex] = item;
            }
            var next = new Dictionary<OutputTarget, KeyAssignmentLabel>();
            foreach (var target in grouped)
            {
                var label = new KeyAssignmentLabel { Count = target.Value.Count };
                var descriptions = new List<string>();
                foreach (ControllerKeyAssignment key in target.Value.Values)
                {
                    if (label.Legend == null || key.Enabled && !label.Enabled)
                    {
                        label.Legend = CompactKeyLegend(key.Legend); label.Side = ModifierSide(key.KeyCode);
                        label.Modifier = label.Side != null || IsModifierLegend(key.Legend);
                        if (label.Modifier) label.Legend = ModifierLegend(key);
                    }
                    label.Enabled |= key.Enabled;
                    descriptions.Add(key.Description + (key.Enabled ? "" : UiText.Get(" (inaktiv)", " (disabled)")));
                }
                if (label.Modifier && label.Side != null)
                {
                    bool left = false, right = false;
                    foreach (ControllerKeyAssignment key in target.Value.Values)
                    {
                        if (key.Enabled != label.Enabled || ModifierLegend(key) != label.Legend) continue;
                        string side = ModifierSide(key.KeyCode); left |= side == "L"; right |= side == "R";
                    }
                    if (left && right) { label.Side = "L/R"; label.VisibleCount = 2; }
                }
                label.Description = String.Join(", ", descriptions.ToArray()); next.Add(target.Key, label);
            }
            bool changed = next.Count != keyAssignments.Count;
            foreach (var pair in next)
            {
                KeyAssignmentLabel previous;
                if (!keyAssignments.TryGetValue(pair.Key, out previous) || previous.Legend != pair.Value.Legend ||
                    previous.Description != pair.Value.Description || previous.Enabled != pair.Value.Enabled || previous.Count != pair.Value.Count ||
                    previous.Side != pair.Value.Side || previous.Modifier != pair.Value.Modifier || previous.VisibleCount != pair.Value.VisibleCount) changed = true;
            }
            if (!changed) return;
            keyAssignments.Clear(); foreach (var pair in next) keyAssignments.Add(pair.Key, pair.Value);
            LayoutKeyAssignments();
            UpdateTooltip(); Invalidate();
            if (IsHandleCreated) AccessibilityNotifyClients(System.Windows.Forms.AccessibleEvents.DescriptionChange, -1);
        }
        public string GetAssignedKeyText(OutputTarget target)
        {
            KeyAssignmentLabel label;
            return keyAssignments.TryGetValue(target, out label) ? label.Description : "";
        }
        string AssignmentStatus(OutputTarget target)
        {
            string keys = GetAssignedKeyText(target);
            return keys.Length == 0 ? "" : " · " + keys;
        }
        static string CompactKeyLegend(string legend)
        {
            switch (legend)
            {
                case "Space": case "Leertaste": return "␣";
                case "Enter": return "↵";
                case "Backspace": return "⌫";
                case "Control": case "Strg": return "Ctrl";
                default: return legend;
            }
        }
        static bool IsModifierLegend(string legend)
        { return legend == "Shift" || legend == "Ctrl" || legend == "Control" || legend == "Strg" || legend == "Alt" || legend == "AltGr"; }
        static string ModifierSide(string code)
        {
            switch (code)
            {
                case "ShiftLeft": case "ControlLeft": case "AltLeft": return "L";
                case "ShiftRight": case "ControlRight": case "AltRight": return "R";
                default: return null;
            }
        }
        static string ModifierLegend(ControllerKeyAssignment key)
        {
            switch (key.KeyCode)
            {
                case "ShiftLeft": case "ShiftRight": return "Shift";
                case "ControlLeft": case "ControlRight": return key.Legend == "Strg" ? "Strg" : "Ctrl";
                case "AltLeft": return "Alt";
                case "AltRight": return key.Legend == "AltGr" ? "AltGr" : "Alt";
                default: return CompactKeyLegend(key.Legend);
            }
        }
        RectangleF KeyAssignmentBounds(ControllerRegion region, KeyAssignmentLabel label)
        { return label.Bounds; }
        RectangleF StandardKeyAssignmentBounds(ControllerRegion region, KeyAssignmentLabel label)
        {
            PointF center = region.LabelCenter;
            float width = Math.Min(32, Math.Max(15, 8 + label.Legend.Length * 5.5f)), height = 14;
            if (label.Count > 1) width = Math.Min(38, width + 11);
            switch (region.Kind)
            {
                case ControllerRegionKind.Trigger: center = new PointF(region.Bounds.X + 45, region.Bounds.Y + 10); width = Math.Min(28, width); height = 13; break;
                case ControllerRegionKind.Shoulder: center.X = region.Bounds.X + 58; width = Math.Min(49, width); break;
                case ControllerRegionKind.System:
                    center.Y = region.Bounds.Bottom + (Style == ControllerStyle.PlayStation5 ? 9 : 3);
                    width = Math.Min(Style == ControllerStyle.PlayStation5 ? 30 : 36, width); break;
                case ControllerRegionKind.Face:
                    if (region.Target == OutputTarget.A)
                    { center.Y += 27; if (Style == ControllerStyle.PlayStation5) center.X += 10; }
                    else if (region.Target == OutputTarget.X)
                    {
                        if (Style == ControllerStyle.PlayStation5) center.Y += 20;
                        else center.X -= 32;
                        width = Math.Min(27, width);
                    }
                    else { center.X += 33; width = Math.Min(31, width); }
                    break;
                case ControllerRegionKind.StickDirection:
                    width = Math.Min(region.Direction % 2 == 0 ? 20 : 30, width); break;
                case ControllerRegionKind.StickClick: center.Y += 5; width = Math.Min(18, width); height = 11; break;
                case ControllerRegionKind.Dpad:
                    center.X += (float)Math.Cos(region.Direction * Math.PI / 2) * 3;
                    center.Y += (float)Math.Sin(region.Direction * Math.PI / 2) * 3;
                    width = Math.Min(region.Direction % 2 == 0 ? 16 : 15, width); height = 12; break;
            }
            return new RectangleF(center.X - width / 2, center.Y - height / 2, width, height);
        }
        // Broad modifiers cannot fit inside a D-pad arm or stick wedge. Position
        // their full keycaps once per edit, in nearby free space. The original
        // shapes and all other keycaps remain available for drawing and input.
        void LayoutKeyAssignments()
        {
            var occupied = new List<RectangleF>();
            bool anyModifier = false;
            foreach (ControllerRegion region in layout.Regions)
            {
                KeyAssignmentLabel label; if (!keyAssignments.TryGetValue(region.Target, out label)) continue;
                if (label.Modifier) { anyModifier = true; continue; }
                label.Bounds = StandardKeyAssignmentBounds(region, label); occupied.Add(RectangleF.Inflate(label.Bounds, 1.5f, 1.5f));
            }
            if (!anyModifier) { LayoutAssignmentLeaders(); return; }
            var native = new List<AssignmentObstacle>();
            using (var identity = new Matrix())
                foreach (ControllerRegion region in layout.Regions)
                    using (GraphicsPath path = region.CreatePath())
                    using (var area = new Region(path)) native.Add(new AssignmentObstacle { Bounds = path.GetBounds(), Spans = area.GetRegionScans(identity) });
            foreach (ControllerRegion region in layout.Regions)
            {
                KeyAssignmentLabel label; if (!keyAssignments.TryGetValue(region.Target, out label) || !label.Modifier) continue;
                PointF preferred = region.LabelCenter;
                if (region.Kind == ControllerRegionKind.Trigger || region.Kind == ControllerRegionKind.Shoulder)
                    preferred = new PointF(region.Center.X < 220 ? region.Bounds.Left - 24 : region.Bounds.Right + 24, region.Center.Y);
                else if (region.Kind == ControllerRegionKind.StickDirection)
                    preferred = new PointF(region.Center.X + (float)Math.Cos(region.Direction * Math.PI / 2) * 60,
                        region.Center.Y + (float)Math.Sin(region.Direction * Math.PI / 2) * 60);
                else if (region.Kind == ControllerRegionKind.Dpad)
                    preferred = new PointF(region.Center.X + (float)Math.Cos(region.Direction * Math.PI / 2) * 32,
                        region.Center.Y + (float)Math.Sin(region.Direction * Math.PI / 2) * 32);
                else
                {
                    RectangleF ordinary = StandardKeyAssignmentBounds(region, label);
                    preferred = new PointF(ordinary.Left + ordinary.Width / 2, ordinary.Top + ordinary.Height / 2);
                }
                RectangleF best = RectangleF.Empty; double distance = Double.MaxValue;
                for (int y = 2; y <= ControllerLayout.Height - 28; y += 4)
                    for (int x = 2; x <= ControllerLayout.Width - 42; x += 4)
                    {
                        var candidate = new RectangleF(x, y, 40, 26);
                        double dx = candidate.X + 20 - preferred.X, dy = candidate.Y + 13 - preferred.Y;
                        double score = dx * dx + dy * dy;
                        if (score >= distance || !AssignmentSpaceFree(candidate, occupied, native)) continue;
                        best = candidate; distance = score;
                    }
                // There are only 24 controller targets. Even with every target
                // carrying a modifier, this fixed figure has free keycap space.
                if (best.IsEmpty) throw new InvalidOperationException("No free space for the controller key labels.");
                label.Bounds = best; occupied.Add(RectangleF.Inflate(best, 1.5f, 1.5f));
            }
            LayoutAssignmentLeaders();
        }
        void LayoutAssignmentLeaders()
        {
            foreach (ControllerRegion region in layout.Regions)
            {
                KeyAssignmentLabel label; if (!keyAssignments.TryGetValue(region.Target, out label)) continue;
                label.HasLeader = label.Modifier || region.Kind == ControllerRegionKind.Face || region.Kind == ControllerRegionKind.System;
                if (!label.HasLeader) continue;
                label.LeaderEnd = new PointF(label.Bounds.X + label.Bounds.Width / 2, label.Bounds.Y + label.Bounds.Height / 2);
                PointF origin = region.LabelCenter;
                float dx = label.LeaderEnd.X - origin.X, dy = label.LeaderEnd.Y - origin.Y;
                float distance = (float)Math.Sqrt(dx * dx + dy * dy);
                label.LeaderStart = origin;
                if (distance <= 0) { label.HasLeader = false; continue; }
                // Walk backwards from the badge to the last source-path edge.
                // This also handles a non-convex stick wedge without drawing a
                // line through its glyph or through the source's centre hole.
                using (GraphicsPath source = region.CreatePath())
                {
                    int steps = Math.Max(1, (int)Math.Ceiling(distance * 2));
                    for (int step = steps; step >= 0; step--)
                    {
                        float inside = (float)step / steps;
                        if (!source.IsVisible(origin.X + dx * inside, origin.Y + dy * inside)) continue;
                        float outside = Math.Min(1, (float)(step + 1) / steps);
                        for (int refine = 0; refine < 10; refine++)
                        {
                            float middle = (inside + outside) / 2;
                            if (source.IsVisible(origin.X + dx * middle, origin.Y + dy * middle)) inside = middle; else outside = middle;
                        }
                        float start = Math.Min(1, outside + .75f / distance);
                        label.LeaderStart = new PointF(origin.X + dx * start, origin.Y + dy * start);
                        break;
                    }
                }
            }
        }
        static bool AssignmentSpaceFree(RectangleF candidate, List<RectangleF> occupied, List<AssignmentObstacle> native)
        {
            RectangleF ink = RectangleF.Inflate(candidate, 1.5f, 1.5f);
            foreach (RectangleF other in occupied) if (ink.IntersectsWith(other)) return false;
            foreach (AssignmentObstacle shape in native)
                if (ink.IntersectsWith(shape.Bounds)) foreach (RectangleF span in shape.Spans) if (ink.IntersectsWith(span)) return false;
            return true;
        }
        bool InlineKeyAssignment(ControllerRegion region)
        { KeyAssignmentLabel label; return keyAssignments.TryGetValue(region.Target, out label) && !label.Modifier; }
        GraphicsPath CreateDragTargetPath(ControllerRegion region)
        {
            GraphicsPath path = region.CreatePath();
            KeyAssignmentLabel label;
            if (keyAssignments.TryGetValue(region.Target, out label))
                using (GraphicsPath badge = ControllerLayout.Rounded(KeyAssignmentBounds(region, label), 3))
                { path.FillMode = FillMode.Winding; path.AddPath(badge, false); }
            return path;
        }
        void DrawKeyAssignments(Graphics graphics)
        {
            if (keyAssignments.Count == 0) return;
            // Callout lines stay behind every keycap, including those painted
            // earlier in the target order.
            foreach (ControllerRegion region in layout.Regions)
            {
                KeyAssignmentLabel label; if (!keyAssignments.TryGetValue(region.Target, out label)) continue;
                if (!label.HasLeader) continue;
                using (Pen line = new Pen(label.Enabled ? Color.FromArgb(133, 127, 178) : Color.FromArgb(79, 82, 96), .8f)) graphics.DrawLine(line, label.LeaderStart, label.LeaderEnd);
            }
            using (Font font = new Font("Segoe UI Semibold", 9, FontStyle.Regular, GraphicsUnit.Pixel))
            using (Font small = new Font("Segoe UI", 7, FontStyle.Regular, GraphicsUnit.Pixel))
            using (Font modifierType = new Font("Segoe UI Semibold", 11, FontStyle.Regular, GraphicsUnit.Pixel))
            using (Font modifierSide = new Font("Segoe UI Semibold", 10, FontStyle.Regular, GraphicsUnit.Pixel))
            foreach (ControllerRegion region in layout.Regions)
            {
                KeyAssignmentLabel label; if (!keyAssignments.TryGetValue(region.Target, out label)) continue;
                RectangleF bounds = KeyAssignmentBounds(region, label);
                Color border = label.Enabled ? Color.FromArgb(133, 127, 178) : Color.FromArgb(79, 82, 96);
                using (GraphicsPath path = ControllerLayout.Rounded(bounds, 3))
                using (Brush fill = new LinearGradientBrush(bounds, Color.FromArgb(47, 49, 62), Color.FromArgb(28, 30, 40), LinearGradientMode.Vertical))
                using (Pen edge = new Pen(border, .8f))
                {
                    graphics.FillPath(fill, path); graphics.DrawPath(edge, path);
                }
                if (label.Modifier)
                {
                    string side = label.Side ?? "";
                    if (label.Count > label.VisibleCount) side += (side.Length == 0 ? "" : " ") + "+" + (label.Count - label.VisibleCount);
                    Color textColor = label.Enabled ? Color.FromArgb(248, 246, 255) : ModernTheme.Muted;
                    using (var brush = new SolidBrush(textColor))
                    using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.None })
                    {
                        if (side.Length != 0) graphics.DrawString(side, modifierSide, brush, new RectangleF(bounds.X + 2, bounds.Y + 1, bounds.Width - 4, 11), format);
                        graphics.DrawString(label.Legend, modifierType, brush, side.Length == 0 ? bounds : new RectangleF(bounds.X + 2, bounds.Y + 11, bounds.Width - 4, 14), format);
                    }
                    continue;
                }
                string text = label.Legend;
                if (label.Count > 1) text += "+" + (label.Count - 1);
                Font chosen = bounds.Height < 13 || text.Length * 5.2f > bounds.Width - 3 ? small : font;
                DrawLabel(graphics, text, chosen, bounds, label.Enabled ? Color.FromArgb(239, 235, 255) : ModernTheme.Muted);
            }
        }
    }
}
