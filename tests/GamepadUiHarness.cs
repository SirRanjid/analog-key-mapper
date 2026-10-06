using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Tk75.App;
using Tk75.Mapping;

namespace Tk75.Tests
{
    // Real editor, synthetic profiles, passive preview: never creates input or output devices.
    public static class GamepadUiHarness
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        static int checks;
        static T Field<T>(object owner, string name)
        { return (T)owner.GetType().GetField(name, Private).GetValue(owner); }
        static void Set(object owner, string name, object value)
        { owner.GetType().GetField(name, Private).SetValue(owner, value); }
        static object Call(object owner, string name, params object[] args)
        {
            try { return owner.GetType().GetMethod(name, Private).Invoke(owner, args); }
            catch (TargetInvocationException error) { throw new InvalidOperationException(name + " failed.", error.InnerException); }
        }
        static void Check(bool condition, string message)
        { checks++; if (!condition) throw new InvalidOperationException("Gamepad UI: " + message); }
        static void Pump(MainForm form)
        { form.PerformLayout(); Application.DoEvents(); form.PerformLayout(); Application.DoEvents(); }
        static Profile Current(MainForm form) { return Field<EditHistory>(form, "history").Current; }
        static ControllerPreview Source(MainForm form)
        {
            var output = Field<ControllerPreview>(form, "controllerPreview");
            return typeof(MainForm).GetFields(Private).Where(field => field.FieldType == typeof(ControllerPreview))
                .Select(field => (ControllerPreview)field.GetValue(form)).Single(value => !Object.ReferenceEquals(value, output));
        }
        static void Passive(MainForm form)
        {
            Check(Field<object>(form, "reader") == null, "preview must not open a keyboard reader");
            var runtime = Field<MultiControllerSession>(form, "runtime");
            Check(!runtime.AnyEnabled && runtime.ActiveControllerIds.Length == 0, "preview must not create controller output");
            Check(!Field<Timer>(form, "uiTimer").Enabled, "preview must not poll");
            Check(!Field<bool>(form, "hotkey") && !Field<bool>(form, "modeHotkey"), "preview must not register global hotkeys");
        }
        static Point TargetPoint(ControllerPreview control, OutputTarget target)
        {
            ControllerRegion region = Field<ControllerLayout>(control, "layout").Find(target);
            Rectangle screen = (Rectangle)Call(control, "TargetBounds", region);
            Rectangle bounds = new Rectangle(control.PointToClient(screen.Location), screen.Size);
            var center = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
            if (control.HitTestTarget(center) == target) return center;
            for (int y = bounds.Top + 1; y < bounds.Bottom; y += 2)
                for (int x = bounds.Left + 1; x < bounds.Right; x += 2)
                    if (control.HitTestTarget(new Point(x, y)) == target) return new Point(x, y);
            throw new InvalidOperationException("No reachable visual region: " + target + ", " + bounds);
        }
        static void Select(MainForm form, OutputTarget source)
        {
            ControllerPreview control = Source(form);
            Point point = TargetPoint(control, source);
            Check(control.HitTestTarget(point) == source, "visible source region resolves to " + source);
            Call(control, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0));
            Call(control, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0));
            Pump(form);
            int[] selected = (int[])Call(form, "SelectedKeys");
            Check(selected.SequenceEqual(new[] { GamepadInputProfile.KeyIndex(source) }), "controller click selects only its logical input: " + source);
        }
        static void VisibleInside(MainForm form, Control control, string label)
        {
            Check(control.Visible && control.Width > 0 && control.Height > 0, label + " visible with usable dimensions");
            Rectangle bounds = control.RectangleToScreen(control.ClientRectangle);
            for (Control parent = control.Parent; parent != null; parent = parent.Parent)
            {
                Rectangle clipped = Rectangle.Intersect(bounds, parent.RectangleToScreen(parent.ClientRectangle));
                Check(clipped.Width >= bounds.Width - 1 && clipped.Height >= bounds.Height - 1, label + " fits in " + parent.GetType().Name);
                if (parent == form) break;
            }
        }
        static void Capture(MainForm form, string output, string name)
        {
            Pump(form); Passive(form);
            Point clientOrigin = form.PointToScreen(Point.Empty);
            var offset = new Point(clientOrigin.X - form.Left, clientOrigin.Y - form.Top);
            using (var window = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(window, new Rectangle(Point.Empty, window.Size));
                using (var client = window.Clone(new Rectangle(offset, form.ClientSize), PixelFormat.Format32bppArgb))
                    client.Save(Path.Combine(output, name + ".png"), ImageFormat.Png);
            }
            Console.WriteLine(name + ".png: " + form.ClientSize.Width + " x " + form.ClientSize.Height);
        }
        static void LoadFixture(MainForm form, Profile profile)
        { Call(form, "Commit", profile); Call(form, "SaveProfile"); Call(form, "ReloadProfiles"); Pump(form); }
        static void CheckRemapping(MainForm form)
        {
            Select(form, OutputTarget.A);
            string before = ProfileJson.Serialize(Current(form));
            Call(form, "SelectTarget", OutputTarget.RightTrigger);
            Call(form, "AddBinding"); Pump(form);
            Profile added = Current(form);
            Check(added.Bindings.Any(binding => binding.KeyIndex == GamepadInputProfile.KeyIndex(OutputTarget.A) && binding.Target == OutputTarget.RightTrigger), "button may map to analog trigger");
            Check(added.Bindings.Count == GamepadInputProfile.ControlCount + 1, "adding an output retains all previous mappings");
            Call(form, "AddBinding");
            Check(Current(form).Bindings.Count == added.Bindings.Count, "duplicate output addition is idempotent");
            Call(form, "Undo"); Pump(form);
            Check(ProfileJson.Serialize(Current(form)) == before, "one undo restores the previous mapping");
            Call(form, "Redo"); Pump(form);
            Check(ProfileJson.Serialize(Current(form)) == ProfileJson.Serialize(added), "redo restores the edited mapping");
            Field<DataGridView>(form, "bindings").SelectAll();
            Call(form, "RemoveBinding"); Pump(form);
            Check(!Current(form).Bindings.Any(binding => binding.KeyIndex == GamepadInputProfile.KeyIndex(OutputTarget.A)), "all selected outputs can be removed to unmap an input");
            Call(form, "Undo"); Pump(form);
            Check(ProfileJson.Serialize(Current(form)) == ProfileJson.Serialize(added), "removal is undoable");
        }
        static void CheckDragMapping(MainForm form)
        {
            Select(form, OutputTarget.LeftTrigger);
            Call(form, "SelectGamepadControl", OutputTarget.RightTrigger, true);
            Check(((int[])Call(form, "SelectedKeys")).Length == 2, "selection can contain multiple controller inputs");
            Call(form, "SelectGamepadControl", OutputTarget.LeftTrigger, true);
            Check(((int[])Call(form, "SelectedKeys")).SequenceEqual(new[] { GamepadInputProfile.KeyIndex(OutputTarget.RightTrigger) }), "toggling one input retains the other");

            string before = ProfileJson.Serialize(Current(form));
            object drag = Call(form, "PrepareKeyMappingDrag", new[] { GamepadInputProfile.KeyIndex(OutputTarget.RightTrigger) });
            Call(form, "InitializeMappingDrag", drag);
            try
            {
                var target = Field<ControllerPreview>(form, "controllerPreview");
                Point point = target.PointToScreen(TargetPoint(target, OutputTarget.B));
                var data = new DataObject(); data.SetData(DataFormats.UnicodeText, false, (string)drag.GetType().GetField("Token").GetValue(drag));
                var drop = new DragEventArgs(data, 0, point.X, point.Y, DragDropEffects.Copy, DragDropEffects.None);
                Call(form, "DropOnController", target, drop);
                Check(drop.Effect == DragDropEffects.Copy, "input-to-output drag accepts an authenticated mapping");
                Check(Current(form).Bindings.Any(binding => binding.KeyIndex == GamepadInputProfile.KeyIndex(OutputTarget.RightTrigger) && binding.Target == OutputTarget.B), "trigger can be dragged to a virtual face button");
            }
            finally { Call(form, "CancelMappingDrag"); Set(form, "activeMappingDrag", null); }
            Call(form, "Undo"); Pump(form);
            Check(ProfileJson.Serialize(Current(form)) == before, "input-to-output drag has one undo step");

            Type dragType = typeof(MainForm).GetNestedType("MappingDrag", BindingFlags.NonPublic);
            drag = Activator.CreateInstance(dragType, true); dragType.GetField("Target").SetValue(drag, OutputTarget.LB);
            Call(form, "InitializeMappingDrag", drag);
            try
            {
                ControllerPreview input = Source(form); Point point = input.PointToScreen(TargetPoint(input, OutputTarget.LeftTrigger));
                var data = new DataObject(); data.SetData(DataFormats.UnicodeText, false, (string)dragType.GetField("Token").GetValue(drag));
                var drop = new DragEventArgs(data, 0, point.X, point.Y, DragDropEffects.Copy, DragDropEffects.None);
                Call(form, "DropOnGamepad", input, drop);
                Check(drop.Effect == DragDropEffects.Copy, "output-to-input drag accepts an authenticated mapping");
                Check(Current(form).Bindings.Any(binding => binding.KeyIndex == GamepadInputProfile.KeyIndex(OutputTarget.LeftTrigger) && binding.Target == OutputTarget.LB), "virtual shoulder output can be dragged onto a trigger input");
            }
            finally { Call(form, "CancelMappingDrag"); Set(form, "activeMappingDrag", null); }
            Call(form, "Undo"); Pump(form);
            Check(ProfileJson.Serialize(Current(form)) == before, "output-to-input drag has one undo step");
        }
        static void CheckStyle(MainForm form, InputMode mode, string images)
        {
            Call(form, "SwitchInputSourceMode", mode); Pump(form);
            Check(Current(form).InputMode == mode, "input-source choice loads the correct profile family");
            var profile = GamepadInputProfile.Create(mode, mode == InputMode.XboxController ? "Xbox - custom controls" : "PlayStation - custom controls");
            LoadFixture(form, profile);
            ControllerPreview input = Source(form);
            Check(input.Visible && !Field<VisualKeyboard>(form, "keyboard").Visible, "controller source replaces the keyboard picture");
            Check(input.Style == (mode == InputMode.XboxController ? ControllerStyle.Xbox : ControllerStyle.PlayStation5), "source keeps the selected controller family");
            var grid = Field<DataGridView>(form, "keys");
            Check(grid.Rows.Cast<DataGridViewRow>().Count(row => row.Visible) == GamepadInputProfile.ControlCount, "controller mode lists exactly the supported controls");
            foreach (OutputTarget target in Enum.GetValues(typeof(OutputTarget))) Select(form, target);
            Check(!Field<PressureRangeSlider>(form, "pressureRange").Visible && !Field<PressureRangeSlider>(form, "pressureRange").Enabled, "keyboard pressure calibration is hidden and disabled for controller input");
            Check(!Field<Control>(form, "calibrateRange").Visible && !Field<Control>(form, "calibrateRange").Enabled, "keyboard calibration capture is unavailable for controller input");
            Check(Field<CalibrationDocument>(form, "calibration") == null, "keyboard calibration does not affect normalized controller inputs");
            Check(!Field<CheckBox>(form, "controllerOnlyInput").Visible, "keyboard suppression is hidden in controller mode");
            Check(!Field<Control>(form, "captureOpposite").Visible && !Field<Control>(form, "captureOpposite").Enabled, "keyboard-only opposite-key capture is unavailable in controller mode");
            CheckRemapping(form);
            CheckDragMapping(form);

            ControllerStyle sourceStyle = input.Style;
            Call(form, "Commit", ControllerRouting.SetKind(Current(form), ControllerRouting.DefaultControllerId,
                mode == InputMode.XboxController ? ControllerKind.DualSense : ControllerKind.Xbox360));
            Check(input.Style == sourceStyle, "output device type cannot change the physical source style");
            Check(Field<ControllerPreview>(form, "controllerPreview").Style != sourceStyle, "output style follows its independent output selection");
            Call(form, "SetDetailMode", "controller", true, false);
            Check(!Field<Control>(form, "rgbSettingsCard").Visible, "keyboard lighting controls are hidden for gamepad input");
            foreach (Size size in new[] { new Size(1440, 880), new Size(1080, 740) })
            {
                form.ClientSize = size; Pump(form);
                VisibleInside(form, input, "source figure");
                VisibleInside(form, Field<ControllerPreview>(form, "controllerPreview"), "output figure");
                foreach (OutputTarget target in Enum.GetValues(typeof(OutputTarget)))
                    Check(input.HitTestTarget(TargetPoint(input, target)) == target, "all source regions remain reachable at " + size + ": " + target);
            }
            form.ClientSize = new Size(1440, 880); Pump(form);
            Select(form, OutputTarget.LeftTrigger);
            Call(form, "SetDetailMode", "controller", true, false);
            if (images != null) Capture(form, images, mode == InputMode.XboxController ? "controller-input-xbox" : "controller-input-playstation");
            if (images != null && mode == InputMode.XboxController)
            {
                Profile remapped = Current(form);
                int key = GamepadInputProfile.KeyIndex(OutputTarget.A);
                remapped.Bindings.RemoveAll(binding => binding.KeyIndex == key);
                remapped.Bindings.Add(new Tk75.Mapping.Binding { KeyIndex = key, Target = OutputTarget.LeftXPositive,
                    Processing = new SignalSettings { Curve = CurveKind.Smoothstep, TopDeadzone = .04 } });
                Call(form, "Commit", remapped); Select(form, OutputTarget.A); Call(form, "SelectTarget", OutputTarget.LeftXPositive);
                Call(form, "SetDetailMode", null, true, false); Capture(form, images, "controller-input-remap");
            }
            Call(form, "SwitchLanguage", "de"); Pump(form);
            Check(input.Visible, "language switch preserves source view");
            Call(form, "SwitchLanguage", "en"); Pump(form);
            Passive(form);
        }
        [STAThread]
        public static int Main(string[] args)
        {
            MainForm form = null;
            try
            {
                if (args.Length < 1 || args.Length > 2) throw new ArgumentException("Pass a fresh artifact directory and an optional screenshot directory.");
                string data = Path.Combine(Path.GetFullPath(args[0]), "gamepad-synthetic-data");
                Check(!Directory.Exists(data), "never use existing user data");
                string images = args.Length == 2 ? Path.GetFullPath(args[1]) : null;
                if (images != null) Directory.CreateDirectory(images);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                form = new MainForm(data, true) { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000) };
                form.Show(); Pump(form); Passive(form);
                Profile keyboard = Current(form); string unchangedKeyboard = ProfileJson.Serialize(keyboard);
                var savedCalibration = new CalibrationDocument { DeviceIdentity = new string('c', 64), ProtocolFingerprint = new string('d', 64), ScaleMaximum = 700 };
                savedCalibration.KeyRanges.Add(new KeyPressureRangeEntry { KeyIndex = 14, Minimum = 20, Maximum = 620 });
                Set(form, "calibration", savedCalibration); Call(form, "Configure");
                CheckStyle(form, InputMode.XboxController, images);
                CheckStyle(form, InputMode.PlayStationController, images);
                Call(form, "SwitchInputSourceMode", InputMode.Keyboard); Pump(form);
                Check(Field<VisualKeyboard>(form, "keyboard").Visible && !Source(form).Visible, "loading a keyboard profile restores its picture");
                Check(ProfileJson.Serialize(Current(form)) == unchangedKeyboard, "keyboard profile restores without remapped indices or changed settings");
                Check(Object.ReferenceEquals(Field<CalibrationDocument>(form, "calibration"), savedCalibration), "switching back restores the original keyboard calibration");
                Calibration restoredRange = Field<KeyboardPressureRange>(form, "sharedPressureRange").ForKey(14);
                Check(restoredRange.Rest == 20 && restoredRange.Bottom == 620 && savedCalibration.ScaleMaximum == 700, "controller editing preserves measured keyboard range and scale");
                Passive(form);
                Console.WriteLine("PASS: " + checks + " controller input UI checks; synthetic profiles, no hardware or output.");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            finally { if (form != null) form.Dispose(); }
        }
    }
}
