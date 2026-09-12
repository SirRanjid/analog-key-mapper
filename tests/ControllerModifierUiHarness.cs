using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Tk75.App;
using Tk75.Mapping;

namespace Tk75.Tests
{
    public static partial class AppUiHarness
    {
        static RectangleF ModifierLayoutBounds(ControllerPreview controller, OutputTarget target)
        {
            object label = Field<IDictionary>(controller, "keyAssignments")[target];
            return (RectangleF)label.GetType().GetField("Bounds").GetValue(label);
        }
        static Rectangle ModifierClientBounds(ControllerPreview controller, OutputTarget target)
        {
            using (Graphics graphics = controller.CreateGraphics())
            using (Font font = (Font)typeof(ControllerPreview).GetMethod("StatusFont", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null))
            {
                object status = typeof(ControllerPreview).GetMethod("MeasureStatus", Private).Invoke(controller, new object[] { graphics, font });
                int figureHeight = (int)status.GetType().GetField("FigureHeight").GetValue(status);
                object viewport = typeof(ControllerPreview).GetMethod("GetViewport", Private).Invoke(controller, new object[] { figureHeight });
                return (Rectangle)viewport.GetType().GetMethod("ToClient").Invoke(viewport, new object[] { ModifierLayoutBounds(controller, target) });
            }
        }
        static Bitmap ModifierBitmap(ControllerPreview controller)
        {
            var image = new Bitmap(controller.Width, controller.Height);
            controller.DrawToBitmap(image, controller.ClientRectangle); return image;
        }
        static void RunControllerModifierUi(string artifacts)
        {
            int started = assertions;
            string[] names = { "Shift", "Ctrl", "Alt" }, leftCodes = { "ShiftLeft", "ControlLeft", "AltLeft" }, rightCodes = { "ShiftRight", "ControlRight", "AltRight" };
            foreach (ControllerStyle style in new[] { ControllerStyle.Xbox, ControllerStyle.PlayStation5 })
            using (var host = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000), ClientSize = new Size(440, 295) })
            using (var controller = new ControllerPreview { Dock = DockStyle.Fill, Style = style, CompactStatus = true })
            {
                string styleName = style == ControllerStyle.Xbox ? "xbox" : "ps";
                host.Controls.Add(controller); host.Show(); Application.DoEvents();
                for (int kind = 0; kind < names.Length; kind++)
                {
                    controller.SetKeyAssignments(new[] { new ControllerKeyAssignment(OutputTarget.LB, 4, names[kind], "Left " + names[kind], true, leftCodes[kind]) });
                    Rectangle bounds = ModifierClientBounds(controller, OutputTarget.LB);
                    using (Bitmap left = ModifierBitmap(controller))
                    {
                        controller.SetKeyAssignments(new[] { new ControllerKeyAssignment(OutputTarget.LB, 4, names[kind], "Right " + names[kind], true, rightCodes[kind]) });
                        Check(ModifierClientBounds(controller, OutputTarget.LB) == bounds, styleName + ": switching a modifier side leaves the keycap geometry stable.");
                        using (Bitmap right = ModifierBitmap(controller))
                        {
                            int upperDifferences = 0, lowerDifferences = 0;
                            for (int y = bounds.Top + 2; y < bounds.Bottom - 2; y++)
                                for (int x = bounds.Left + 2; x < bounds.Right - 2; x++)
                                    if (left.GetPixel(x, y) != right.GetPixel(x, y))
                                    { if (y < bounds.Top + bounds.Height / 2) upperDifferences++; else lowerDifferences++; }
                            Check(upperDifferences > 2, styleName + ": left/right " + names[kind] + " are visibly different in the keycap, without opening a tooltip.");
                            Check(lowerDifferences == 0, styleName + ": changing a modifier side preserves the full visible type label.");
                        }
                    }
                }
                var both = new[] {
                    new ControllerKeyAssignment(OutputTarget.LB, 4, "Shift", "Left Shift", true, "ShiftLeft"),
                    new ControllerKeyAssignment(OutputTarget.LB, 76, "Shift", "Right Shift", true, "ShiftRight") };
                controller.SetKeyAssignments(both);
                object combined = Field<IDictionary>(controller, "keyAssignments")[OutputTarget.LB];
                Equal("L/R", (string)combined.GetType().GetField("Side").GetValue(combined), "Both mapped Shift sides are displayed together rather than hidden behind a count.");
                Equal("Shift", (string)combined.GetType().GetField("Legend").GetValue(combined), "Shift stays spelled out, without the unreadable arrow substitute.");
                controller.SetKeyAssignments(new[] { new ControllerKeyAssignment(OutputTarget.LB, 4, "Custom label", "Learned label", true, "ControlLeft") });
                object custom = Field<IDictionary>(controller, "keyAssignments")[OutputTarget.LB];
                Equal("Ctrl", (string)custom.GetType().GetField("Legend").GetValue(custom), "A learned caption never hides the known physical modifier type.");
                Equal("Learned label", controller.GetAssignedKeyText(OutputTarget.LB), "The full learned description remains available.");

                string[] codes = { "ShiftLeft", "ShiftRight", "ControlLeft", "ControlRight", "AltLeft", "AltRight" };
                string[] legends = { "Shift", "Shift", "Strg", "Ctrl", "Alt", "AltGr" };
                var assignments = Enum.GetValues(typeof(OutputTarget)).Cast<OutputTarget>().Select((target, index) =>
                    new ControllerKeyAssignment(target, index, legends[index % legends.Length], codes[index % codes.Length], true, codes[index % codes.Length])).ToArray();
                controller.SetKeyAssignments(assignments);
                var layout = Field<ControllerLayout>(controller, "layout");
                int overlaps = 0, hits = 0, leadersInsideSource = 0; var placed = new List<RectangleF>();
                using (Graphics graphics = controller.CreateGraphics())
                using (Font text = new Font("Segoe UI Semibold", 11, FontStyle.Regular, GraphicsUnit.Pixel))
                foreach (ControllerKeyAssignment assignment in assignments)
                {
                    RectangleF bounds = ModifierLayoutBounds(controller, assignment.Target);
                    object displayed = Field<IDictionary>(controller, "keyAssignments")[assignment.Target];
                    PointF leaderStart = (PointF)displayed.GetType().GetField("LeaderStart").GetValue(displayed);
                    using (GraphicsPath originalSource = layout.Find(assignment.Target).CreatePath())
                        if (originalSource.IsVisible(leaderStart)) leadersInsideSource++;
                    Check(new RectangleF(0, 0, ControllerLayout.Width, ControllerLayout.Height).Contains(bounds), "Every broad modifier keycap stays inside the drawn controller viewport.");
                    Check(graphics.MeasureString(assignment.Legend, text, SizeF.Empty, StringFormat.GenericTypographic).Width < bounds.Width - 4,
                        "The full " + assignment.Legend + " type fits its keycap without ellipsis or side/type truncation.");
                    foreach (RectangleF previous in placed) if (bounds.IntersectsWith(previous)) overlaps++;
                    placed.Add(bounds);
                    using (GraphicsPath cap = ControllerLayout.Rounded(bounds, 3))
                    foreach (ControllerRegion original in layout.Regions)
                    using (GraphicsPath shape = original.CreatePath())
                    using (var intersection = new Region(cap))
                    { intersection.Intersect(shape); if (!intersection.IsEmpty(graphics)) overlaps++; }
                    Rectangle client = ModifierClientBounds(controller, assignment.Target);
                    if (controller.HitTestTarget(new Point(client.Left + client.Width / 2, client.Top + client.Height / 2)) == assignment.Target) hits++;
                }
                Check(overlaps == 0, styleName + ": all 24 wide modifier labels leave every original target and neighboring keycap uncovered.");
                Check(hits == assignments.Length, styleName + ": every visible modifier keycap dispatches its own controller target.");
                Check(leadersInsideSource == 0, styleName + ": modifier leaders start outside the original source path and do not strike through its native legend.");
                using (Bitmap picture = ModifierBitmap(controller)) picture.Save(Path.Combine(artifacts, "controller-modifiers-all-" + styleName + ".png"), System.Drawing.Imaging.ImageFormat.Png);

                Rectangle pickupBounds = ModifierClientBounds(controller, OutputTarget.DpadUp);
                Point pickup = new Point(pickupBounds.Left + pickupBounds.Width / 2, pickupBounds.Top + pickupBounds.Height / 2);
                Bitmap source;
                using (MappingDragVisual drag = CaptureControllerDragVisual(controller, OutputTarget.DpadUp, pickup, out source))
                using (source)
                {
                    Check(drag.Image.GetPixel(drag.Anchor.X, drag.Anchor.Y).A > 200, "Dragging the readable modifier keycap retains its own pickup pixels.");
                    Point origin = new Point(pickup.X - drag.Anchor.X, pickup.Y - drag.Anchor.Y);
                    int foreignPixels = 0;
                    foreach (ControllerKeyAssignment other in assignments.Where(item => item.Target != OutputTarget.DpadUp))
                    {
                        Rectangle otherBounds = ModifierClientBounds(controller, other.Target);
                        Point otherCenter = new Point(otherBounds.Left + otherBounds.Width / 2 - origin.X, otherBounds.Top + otherBounds.Height / 2 - origin.Y);
                        if (new Rectangle(Point.Empty, drag.Image.Size).Contains(otherCenter) && drag.Image.GetPixel(otherCenter.X, otherCenter.Y).A != 0) foreignPixels++;
                    }
                    Check(foreignPixels == 0, "A modifier drag contains no neighboring keycap's visible center.");
                    drag.Image.Save(Path.Combine(artifacts, "controller-modifier-drag-" + styleName + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                }
                // A practical mixed assignment keeps letter keycaps compact.
                controller.SetKeyAssignments(new[] {
                    new ControllerKeyAssignment(OutputTarget.LB, 4, "Shift", "Left Shift", true, "ShiftLeft"),
                    new ControllerKeyAssignment(OutputTarget.RB, 76, "Shift", "Right Shift", true, "ShiftRight"),
                    new ControllerKeyAssignment(OutputTarget.LeftTrigger, 5, "Strg", "Left Control", true, "ControlLeft"),
                    new ControllerKeyAssignment(OutputTarget.RightTrigger, 89, "Ctrl", "Right Control", true, "ControlRight"),
                    new ControllerKeyAssignment(OutputTarget.X, 6, "Alt", "Left Alt", true, "AltLeft"),
                    new ControllerKeyAssignment(OutputTarget.B, 66, "AltGr", "Right Alt", true, "AltRight"),
                    new ControllerKeyAssignment(OutputTarget.LeftYPositive, 14, "W", "W", true, "KeyW"),
                    new ControllerKeyAssignment(OutputTarget.LeftYNegative, 15, "S", "S", true, "KeyS"),
                    new ControllerKeyAssignment(OutputTarget.LeftXNegative, 9, "A", "A", true, "KeyA"),
                    new ControllerKeyAssignment(OutputTarget.LeftXPositive, 21, "D", "D", true, "KeyD") });
                using (Bitmap picture = ModifierBitmap(controller)) picture.Save(Path.Combine(artifacts, "controller-modifiers-" + styleName + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                host.Close();
            }
            Console.WriteLine("CONTROLLER MODIFIERS PASS: " + (assertions - started) + " assertions; visible L/R text, full type labels, all-target spacing and drag imagery; no hardware.");
        }
    }
}
