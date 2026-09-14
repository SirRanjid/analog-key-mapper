using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Tk75.App;

namespace Tk75.Tests
{
    public static partial class AppUiHarness
    {
        static Point KeyGesturePoint(VisualKeyboard keyboard, int index)
        {
            RectangleF bounds = keyboard.GetKeyBounds(keyboard.LayoutModel.FindByIndex(index));
            return Point.Round(new PointF(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2));
        }
        static void SendKeyboardMouse(VisualKeyboard keyboard, string action, Point point)
        {
            try { typeof(VisualKeyboard).GetMethod(action, Private).Invoke(keyboard, new object[] { new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0) }); }
            catch (TargetInvocationException error) { throw new InvalidOperationException("Keyboard gesture failed.", error.InnerException); }
        }
        static void ShortKeyboardClick(VisualKeyboard keyboard, int index)
        {
            keyboard.Focus(); Point point = KeyGesturePoint(keyboard, index);
            SendKeyboardMouse(keyboard, "OnMouseDown", point); SendKeyboardMouse(keyboard, "OnMouseUp", point);
        }
        static void RunKeyboardTabClicks(MainForm form)
        {
            var keyboard = Field<VisualKeyboard>(form, "keyboard");
            string before = Json(Current(form));
            foreach (string mode in new string[] { null, "controller", "advanced", "input" })
            {
                SelectKeys(form, 14); DetailMode(form, mode); keyboard.Focus();
                Rectangle original = Relative(form, keyboard); Point point = KeyGesturePoint(keyboard, 14);
                SendKeyboardMouse(keyboard, "OnMouseDown", point);
                Equal(mode, Field<string>(form, "detailsMode"), "Holding an already-selected key does not flash the Keys tab before drag detection.");
                SendKeyboardMouse(keyboard, "OnMouseUp", point); Pump(form);
                CheckDetailTabState(form, mode, "A short click on the already-selected key retains " + (mode ?? "Keys"));
                Check(Relative(form, keyboard) == original, "Short-click navigation keeps keyboard geometry fixed.");
            }
            DetailMode(form, "advanced"); ShortKeyboardClick(keyboard, 9); Pump(form);
            Check(((int[])Call(form, "SelectedKeys")).SequenceEqual(new[] { 9 }) && Field<string>(form, "detailsMode") == "advanced",
                "A short click on another key selects it while retaining Curve.");
            DetailMode(form, "input");
            object drag = Call(form, "PrepareKeyMappingDrag", new[] { 9 });
            Check(drag != null && Field<string>(form, "detailsMode") == "controller",
                "The actual key-drag preparation activates Controller without an OLE test or device.");
            Equal(before, Json(Current(form)), "Click/drag navigation alone never changes mappings.");
            DetailMode(form, null); SelectKeys(form, 14);

            using (var host = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000), ClientSize = new Size(830, 350) })
            using (var source = new MarqueeKeyboard { Dock = DockStyle.Fill, LayoutModel = KeyboardLayout.Tk75Iso() })
            {
                host.Controls.Add(source); host.Show(); Application.DoEvents(); source.Focus();
                int clicks = 0, drags = 0; int[] beforePress = null;
                source.KeyClicked += delegate { clicks++; };
                source.KeyPressStarted += delegate { beforePress = source.SelectedKeyIndices; };
                source.KeyDragRequested += delegate { drags++; Check(Object.ReferenceEquals(source.Cursor, DragCursors.Grabbing), "A real keyboard drag event retains the grabbing cursor."); };
                source.SetSelectedKeys(new[] { 9 }); Point p = KeyGesturePoint(source, 14);
                source.Mouse("down", p, MouseButtons.Left);
                Check(beforePress.SequenceEqual(new[] { 9 }), "Press context captures the edited key before selecting the drag source.");
                source.Mouse("up", p, MouseButtons.Left);
                Check(clicks == 1 && drags == 0, "A short click emits one click and no drag.");
                source.Mouse("down", p, MouseButtons.Left);
                source.Mouse("move", new Point(p.X + SystemInformation.DragSize.Width + 5, p.Y), MouseButtons.Left);
                source.Mouse("up", p, MouseButtons.Left);
                Check(clicks == 1 && drags == 1, "Dragging and releasing over the source never emits a trailing click.");
                source.Mouse("down", p, MouseButtons.Left); source.Escape(); source.Mouse("up", p, MouseButtons.Left);
                Check(clicks == 1, "Escape cancels a pending short click.");
                source.Focus(); source.Mouse("down", p, MouseButtons.Left); source.Capture = false; source.Mouse("up", p, MouseButtons.Left);
                Check(clicks == 1, "Losing capture cancels a pending short click.");
                source.Focus(); source.TestModifiers = Keys.Control; source.SetSelectedKeys(new[] { 14 });
                source.Mouse("down", p, MouseButtons.Left); source.Mouse("up", p, MouseButtons.Left);
                Check(clicks == 2 && source.SelectedKeyIndices.Length == 0, "Ctrl-click deselection is still a short click, never a drag.");
                source.TestModifiers = Keys.None;
            }
            AssertPassive(form);
        }
    }
}
