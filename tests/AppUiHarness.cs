using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Tk75.App;
using Tk75.Mapping;
using Binding = Tk75.Mapping.Binding;

namespace Tk75.Tests
{
    // Actual application source; always preview:true; never Scan/Connect/Enable.
    public static partial class AppUiHarness
    {
        static int assertions;
        static readonly List<string> layoutFailures = new List<string>();
        static readonly Size DefaultClientSize = new Size(1440, 880);
        static readonly Size SupportedMinimumSize = new Size(1080, 740);
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        static T Field<T>(object owner, string name)
        {
            FieldInfo field = owner.GetType().GetField(name, Private);
            if (field == null) throw new InvalidOperationException("Test field missing: " + name);
            return (T)field.GetValue(owner);
        }
        static object Call(MainForm form, string name, params object[] args)
        {
            MethodInfo method = typeof(MainForm).GetMethod(name, Private);
            if (method == null) throw new InvalidOperationException("Test method missing: " + name);
            try { return method.Invoke(form, args); }
            catch (TargetInvocationException ex) { throw new InvalidOperationException("MainForm." + name + " failed.", ex.InnerException); }
        }
        static void Check(bool value, string message)
        { assertions++; if (!value) throw new InvalidOperationException("UI ASSERTION FAILED: " + message); }
        static void Equal(string expected, string actual, string message) { Check(expected == actual, message + " [actual=" + actual + "]"); }
        static Profile Current(MainForm form) { return Field<EditHistory>(form, "history").Current; }
        static string Json(Profile profile) { return ProfileJson.Serialize(profile); }
        static void Pump(MainForm form) { form.PerformLayout(); Application.DoEvents(); form.PerformLayout(); Application.DoEvents(); }
        static DataGridViewRow PropertyRow(MainForm form, string property)
        { return Field<DataGridView>(form, "settings").Rows.Cast<DataGridViewRow>().Single(row => (string)row.Tag == property); }
        static string PropertyText(MainForm form, string property) { return Convert.ToString(PropertyRow(form, property).Cells[1].Value); }
        static void SelectKeys(MainForm form, params int[] indices)
        {
            DataGridView keys = Field<DataGridView>(form, "keys"); keys.ClearSelection();
            foreach (int index in indices) { Check(keys.Rows[index].Visible, "Requested preview key visible: " + index); keys.Rows[index].Selected = true; }
            Pump(form);
            Check(((int[])Call(form, "SelectedKeys")).SequenceEqual(indices.OrderBy(value => value)), "Actual key-selection events retain physical IDs.");
        }
        static void SelectBindings(MainForm form, params string[] ids)
        {
            DataGridView grid = Field<DataGridView>(form, "bindings"); grid.ClearSelection();
            foreach (DataGridViewRow row in grid.Rows) if (ids.Contains((string)row.Tag)) row.Selected = true;
            Pump(form);
            Check(new HashSet<string>((string[])Call(form, "SelectedBindings")).SetEquals(ids), "Actual binding selection retains requested IDs.");
        }
        static void Edit(MainForm form, string property, string text)
        { DataGridViewRow row = PropertyRow(form, property); row.Cells[1].Value = text; Call(form, "EditProperty", row.Index); Pump(form); }
        static void Target(MainForm form, OutputTarget target)
        {
            ComboBox picker = Field<ComboBox>(form, "targets");
            picker.SelectedItem = picker.Items.Cast<object>().Single(item => (OutputTarget)item.GetType().GetField("Target").GetValue(item) == target);
        }
        static void AssertPassive(MainForm form)
        {
            Check(Field<object>(form, "reader") == null, "Preview creates no ReaderSession.");
            var runtime = Field<MultiControllerSession>(form, "runtime");
            Check(!runtime.Enabled && !runtime.AnyEnabled, "Every controller output remains disabled.");
            Check(runtime.ActiveControllerIds.Length == 0, "Preview has no connected controllers.");
            foreach (DictionaryEntry entry in Field<IDictionary>(runtime, "slots"))
            {
                object session = entry.Value.GetType().GetField("Session").GetValue(entry.Value);
                Check(Field<object>(Field<MappingSession>(session, "inner"), "output") == null, "No output backend is created for controller " + entry.Key + ".");
            }
            Check(!Field<System.Windows.Forms.Timer>(form, "uiTimer").Enabled, "Preview does not scan foreground programs.");
            Check(!Field<bool>(form, "hotkey"), "Preview registers no global hotkey.");
            Check(!Field<bool>(form, "modeHotkey"), "Preview registers no input-mode hotkey.");
        }
        static Rectangle Relative(MainForm form, Control control)
        { return new Rectangle(form.PointToClient(control.PointToScreen(Point.Empty)), control.Size); }
        static void LayoutCheck(bool ok, string message) { assertions++; if (!ok) layoutFailures.Add(message); }
        static void VisibleInside(MainForm form, Control control, string label)
        {
            LayoutCheck(control.Visible && control.Width > 0 && control.Height > 0, label + " is invisible or has no size.");
            Rectangle bounds = control.RectangleToScreen(control.ClientRectangle);
            for (Control parent = control.Parent; parent != null; parent = parent.Parent)
            {
                Rectangle clipped = Rectangle.Intersect(bounds, parent.RectangleToScreen(parent.ClientRectangle));
                LayoutCheck(clipped.Width >= bounds.Width - 1 && clipped.Height >= bounds.Height - 1,
                    label + " clipped by " + parent.GetType().Name + ": " + Relative(form, control) + " within " + Relative(form, parent));
                if (parent == form) break;
            }
        }
        static void CheckBar(MainForm form, Control bar, string name)
        {
            Control[] visible = bar.Controls.Cast<Control>().Where(c => c.Visible).ToArray();
            foreach (Control child in visible) VisibleInside(form, child, name + "/" + child.Text);
            for (int a = 0; a < visible.Length; a++) for (int b = a + 1; b < visible.Length; b++)
            {
                Rectangle overlap = Rectangle.Intersect(visible[a].Bounds, visible[b].Bounds);
                LayoutCheck(overlap.Width <= 1 || overlap.Height <= 1, name + " overlaps: " + visible[a].Text + " / " + visible[b].Text);
            }
        }
        static void DetailMode(MainForm form, string mode)
        { Call(form, "SetDetailMode", mode, true, false); Pump(form); }
        static void RevealKeySetting(MainForm form, Control control)
        { Call(form, "ScrollKeySettingsTo", control); Pump(form); }
        static void RevealCurveSetting(MainForm form, Control control)
        { Call(form, "ScrollCurveSettingsTo", control); Pump(form); }
        static Button[] DetailTabs(MainForm form)
        { return new[] { Field<Button>(form, "keySettingsToggle"), Field<Button>(form, "advancedToggle"), Field<Button>(form, "controllerToggle") }; }
        static void CheckDetailTabState(MainForm form, string mode, string label)
        {
            Button[] tabs = DetailTabs(form); int selected = mode == "advanced" ? 1 : mode == "controller" ? 2 : 0;
            Control strip = tabs[0].Parent;
            Check(strip.AccessibilityObject.Role == AccessibleRole.PageTabList, label + ": the detail navigation exposes a real tab list.");
            Check(tabs.All(tab => Object.ReferenceEquals(tab.Parent, strip)), label + ": all detail tabs belong to the same tab list.");
            for (int i = 0; i < tabs.Length; i++)
            {
                Check(tabs[i].AccessibilityObject.Role == AccessibleRole.PageTab, label + ": detail item " + i + " exposes a tab role.");
                Check(((tabs[i].AccessibilityObject.State & AccessibleStates.Selected) != 0) == (i == selected), label + ": only the active detail tab exposes selected state.");
                Check(tabs[i].TabStop == (i == selected), label + ": only the active detail tab participates in tab order.");
            }
            Check(Object.ReferenceEquals(strip.AccessibilityObject.GetSelected(), tabs[selected].AccessibilityObject), label + ": assistive navigation finds the active tab.");
            Check(Field<string>(form, "detailsMode") == mode, label + ": the tab selects its corresponding detail context.");
        }
        static void CheckDetailTabNavigation(MainForm form)
        {
            int started = assertions;
            Call(form, "ShowPage", "mapping"); DetailMode(form, null);
            var keyboard = Field<VisualKeyboard>(form, "keyboard");
            Rectangle baseline = Relative(form, keyboard); RectangleF drawing = keyboard.KeyboardBounds;
            Button[] tabs = DetailTabs(form); string[] modes = { null, "advanced", "controller" };
            string original = Json(Current(form));
            for (int i = 0; i < tabs.Length; i++)
            {
                tabs[i].PerformClick(); Pump(form);
                CheckDetailTabState(form, modes[i], "tab click " + i);
                CheckStableKeyboard(form, baseline, drawing, "tab click " + i);
                string panel = i == 0 ? "keyCard" : i == 1 ? "curve" : "controllerPreview";
                Check(Field<Control>(form, panel).Visible, "Clicking tab " + i + " makes its content visible.");
                if (i == 1) { Control curve = Field<Control>(form, "curve"); Check(curve.Width == curve.Height, "Clicking Curve retains the square curve editor."); }
            }
            tabs[0].PerformClick(); tabs[2].Focus(); Pump(form);
            CheckDetailTabState(form, null, "tab focus without activation");
            tabs[0].Focus();
            Keys[] navigation = { Keys.Right, Keys.Right, Keys.Right, Keys.Left, Keys.Home, Keys.End };
            int[] expected = { 1, 2, 0, 2, 0, 2 }; int current = 0;
            for (int i = 0; i < navigation.Length; i++)
            {
                var args = new KeyEventArgs(navigation[i]);
                tabs[current].GetType().GetMethod("OnKeyDown", Private).Invoke(tabs[current], new object[] { args });
                Pump(form); current = expected[i];
                Check(args.Handled && tabs[current].Focused, "Tab navigation via " + navigation[i] + " activates and focuses the expected tab.");
                CheckDetailTabState(form, modes[current], "tab navigation " + navigation[i]);
                CheckStableKeyboard(form, baseline, drawing, "tab navigation " + navigation[i]);
                if (current == 1) { Control curve = Field<Control>(form, "curve"); Check(curve.Width == curve.Height, "Keyboard tab navigation retains the square curve editor."); }
            }
            DetailMode(form, "input"); CheckDetailTabState(form, "input", "key behavior within Keys");
            Check(Field<Control>(form, "keySocdPanel").Visible && !Field<Control>(form, "keyBehaviorPanel").Visible, "SOCD remains within Keys while advanced pressure belongs to Curve.");
            CheckStableKeyboard(form, baseline, drawing, "key behavior tab state");
            DetailMode(form, null);
            Equal(original, Json(Current(form)), "Switching and focusing detail tabs preserves profile values.");
            Console.WriteLine("TABS PASS: " + (assertions - started) + " assertions; roles, selection, click, arrow/Home/End navigation and fixed detail geometry.");
        }
        static void CapturePreview(MainForm form, string artifacts, string label)
        {
            using (var bitmap = new Bitmap(form.Width, form.Height))
            { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(artifacts, "preview-" + label + ".png"), System.Drawing.Imaging.ImageFormat.Png); }
        }
        static void CheckStableKeyboard(MainForm form, Rectangle bounds, RectangleF drawing, string label)
        {
            var keyboard = Field<VisualKeyboard>(form, "keyboard");
            VisibleInside(form, keyboard, label + "/keyboard");
            LayoutCheck(Relative(form, keyboard) == bounds, label + ": changing the sidebar moved or resized the keyboard: " + Relative(form, keyboard) + "; expected " + bounds);
            LayoutCheck(keyboard.KeyboardBounds == drawing, label + ": changing the sidebar changed the physical keyboard drawing.");
            var mapping = (TableLayoutPanel)Field<Dictionary<string, Control>>(form, "pages")["mapping"];
            int[] widths = mapping.GetColumnWidths();
            LayoutCheck(widths.Length == 2 && Math.Abs(widths[0] - (widths[0] + widths[1]) * .618) <= 1,
                label + ": keyboard and details retain the requested 61.8% / 38.2% proportion: " + string.Join(", ", widths));
            Control host = Field<Control>(form, "detailsHost");
            VisibleInside(form, host, label + "/detailsHost");
            LayoutCheck(host.FindForm() == form, label + ": details remain in the main window.");
            LayoutCheck(Relative(form, host).Left >= bounds.Right, label + ": details are to the right of the keyboard.");
            LayoutCheck(form.OwnedForms.Length == 0, label + ": changing the sidebar creates no floating detail window.");
        }
        static void CheckDefaultWindowGeometry(MainForm form)
        {
            // WinForms can clamp both initial and minimum bounds to the current
            // desktop. Compare with an independent ordinary Form configured with
            // the intended dimensions, before changing this preview's test limits.
            using (var expected = new Form())
            {
                expected.ClientSize = DefaultClientSize; expected.MinimumSize = SupportedMinimumSize;
                expected.Font = form.Font; expected.ShowInTaskbar = false;
                expected.StartPosition = FormStartPosition.Manual; expected.Location = form.Location;
                expected.Show(); expected.PerformLayout(); Application.DoEvents();
                Console.WriteLine("WINDOW: client=" + form.ClientSize + "; bounds=" + form.Bounds + "; minimum=" + form.MinimumSize
                    + "; expected client=" + expected.ClientSize + "; expected minimum=" + expected.MinimumSize
                    + "; working area=" + Screen.FromControl(form).WorkingArea + "; max track=" + SystemInformation.MaxWindowTrackSize);
                Check(form.ClientSize == expected.ClientSize, "Main window uses the intended default client size within the current Windows desktop limits.");
                Check(form.MinimumSize == expected.MinimumSize, "Main window retains its supported minimum size within the current Windows desktop limits.");
            }
        }
        [System.Runtime.InteropServices.DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
        static void SetPreviewClientSize(MainForm form, Size size)
        {
            // Only our own hidden/offscreen preview. Native sizing avoids the
            // Framework's desktop-sized SetBoundsCore cap on small CI displays;
            // the actual client dimensions and every layout assertion stay exact.
            Size chrome = new Size(form.Width - form.ClientSize.Width, form.Height - form.ClientSize.Height);
            if (!SetWindowPos(form.Handle, IntPtr.Zero, 0, 0, size.Width + chrome.Width, size.Height + chrome.Height, 0x0216))
                throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            Pump(form);
            Console.WriteLine("RESIZE: requested client=" + size + "; actual client=" + form.ClientSize + "; bounds=" + form.Bounds);
        }
        static void CheckLayout(MainForm form, Size size, string artifacts)
        {
            Call(form, "ShowPage", "mapping"); DetailMode(form, null);
            SetPreviewClientSize(form, size); string label = size.Width + "x" + size.Height;
            Check(form.ClientSize == size, "Requested client size applied: " + label);
            foreach (string name in new[] { "devices", "profiles", "keyboard", "layoutMode", "mainControllerSlotPicker", "keyTitle", "controllerToggle", "advancedToggle", "deviceStatus", "outputStatus", "keyHint", "pressureText", "pressureRange", "pressureScaleMaximum", "calibrateRange", "targets", "addTargetButton" })
                VisibleInside(form, Field<Control>(form, name), label + "/" + name);
            foreach (string name in new[] { "preset", "pasteMode", "keys", "settings", "curve", "monitor", "controllerPreview", "keyBehaviorPanel" })
                LayoutCheck(!Field<Control>(form, name).Visible, label + "/" + name + " is hidden in the default Keys view.");
            CheckBar(form, Field<Control>(form, "devices").Parent, label + "/header");
            CheckBar(form, Field<Control>(form, "controllerToggle").Parent, label + "/detail-actions");
            CheckControllerFooter(form, label);
            var keyboard = Field<VisualKeyboard>(form, "keyboard");
            LayoutCheck(keyboard.KeyboardBounds.Width >= 400 && keyboard.KeyboardBounds.Height >= 110, label + ": keyboard remains a usable physical overview.");
            Rectangle baseline = Relative(form, keyboard); RectangleF drawing = keyboard.KeyboardBounds;
            CheckStableKeyboard(form, baseline, drawing, label + "/key"); CapturePreview(form, artifacts, label);
            CheckDetailTabState(form, null, label + "/key tabs");
            foreach (string name in new[] { "keyHint", "pressureText", "pressureRange", "pressureScaleMaximum", "calibrateRange", "targetHeader", "targets", "addTargetButton", "bindings", "removeTargetButton", "toggleTargetButton", "keyBehaviorToggle", "oppositeKey", "oppositeMode", "captureOpposite" })
            {
                Control control = Field<Control>(form, name); RevealKeySetting(form, control);
                VisibleInside(form, control, label + "/scrolled/" + name);
                CheckStableKeyboard(form, baseline, drawing, label + "/scroll/" + name);
            }
            RevealKeySetting(form, Field<Control>(form, "removeTargetButton").Parent);
            CheckBar(form, Field<Control>(form, "removeTargetButton").Parent, label + "/target-actions");
            DataGridView assignmentGrid = Field<DataGridView>(form, "bindings");
            RevealKeySetting(form, assignmentGrid);
            LayoutCheck(assignmentGrid.ClientSize.Height >= assignmentGrid.ColumnHeadersHeight + assignmentGrid.RowTemplate.Height,
                label + ": assignment list must show its column header and at least one usable data row: " + assignmentGrid.ClientSize);
            CapturePreview(form, artifacts, label + "-outputs");
            DetailMode(form, "input");
            CheckDetailTabState(form, "input", label + "/input alias tabs");
            RevealKeySetting(form, Field<Control>(form, "keySocdPanel"));
            foreach (string name in new[] { "oppositeKey", "oppositeMode", "captureOpposite" })
                VisibleInside(form, Field<Control>(form, name), label + "/input/" + name);
            LayoutCheck(Field<Control>(form, "keyTitle").Visible && !Field<Control>(form, "curve").Visible && !Field<Control>(form, "keyBehaviorPanel").Visible, label + ": SOCD is part of Keys and advanced pressure is reserved for Curve.");
            CheckStableKeyboard(form, baseline, drawing, label + "/input"); CapturePreview(form, artifacts, label + "-input");
            DetailMode(form, "advanced");
            CheckDetailTabState(form, "advanced", label + "/curve tabs");
            foreach (string name in new[] { "curveShape", "pasteMode", "curve" }) VisibleInside(form, Field<Control>(form, name), label + "/advanced/" + name);
            LayoutCheck(!Field<Control>(form, "keyTitle").Visible && Field<Control>(form, "keyBehaviorPanel").Visible && !Field<Control>(form, "controllerPreview").Visible, label + ": Curve contains the plot and advanced pressure controls.");
            LayoutCheck(!Field<Control>(form, "preset").Visible, label + ": presets do not create a second visible shape selector.");
            CheckBar(form, Field<Control>(form, "curveShape").Parent, label + "/advanced/signal");
            DataGridView settings = Field<DataGridView>(form, "settings"); Control curve = Field<Control>(form, "curve");
            LayoutCheck(settings.ClientSize.Width >= 180 && settings.ClientSize.Height >= 210, label + ": settings editor too small: " + settings.ClientSize);
            LayoutCheck(curve.ClientSize.Width >= 184 && curve.ClientSize.Height >= 184, label + ": curve editor too small: " + curve.ClientSize);
            LayoutCheck(curve.Width == curve.Height, label + ": curve control remains square: " + curve.Size);
            RectangleF plot = (RectangleF)typeof(CurveCanvas).GetProperty("Plot", Private).GetValue(curve, null);
            LayoutCheck(Math.Abs(plot.Width - plot.Height) < 0.01f, label + ": curve coordinate plot remains square: " + plot);
            LayoutCheck(plot.Width > 0 && plot.Height > 0 && plot.Left >= 0 && plot.Top >= 0 && plot.Right <= curve.ClientSize.Width && plot.Bottom + 7 + curve.Font.Height <= curve.ClientSize.Height,
                label + ": internal curve plot/axis labels clipped: " + plot + " within " + curve.ClientSize);
            Control thresholds = Field<Control>(form, "keyBehaviorPanel");
            if (size == DefaultClientSize)
            {
                LayoutCheck(thresholds.Right <= curve.Left && thresholds.Top <= curve.Top, label + ": vertical pressure controls sit to the left of the square curve.");
                LayoutCheck(!Field<Panel>(form, "curveEditorScroll").VerticalScroll.Visible, label + ": the normal Curve view scrolls only the lower settings grid.");
            }
            else if (curve.Parent.ClientSize.Width < 464)
                LayoutCheck(thresholds.Top >= curve.Bottom + 8, label + ": the minimum layout puts usable vertical sliders below the plot.");
            CheckStableKeyboard(form, baseline, drawing, label + "/advanced"); CapturePreview(form, artifacts, label + "-advanced");
            RevealCurveSetting(form, thresholds);
            foreach (string name in new[] { "keyBehaviorStatus", "rapidTrigger", "actuationPoint", "releaseMovement", "pressMovement", "actuationSlider", "releaseSlider", "repressSlider", "calibrateActuation", "calibrateRelease", "calibrateRepress", "applyKeyBehavior", "resetKeyBehavior" })
                VisibleInside(form, Field<Control>(form, name), label + "/advanced/" + name);
            foreach (string name in new[] { "actuationSlider", "releaseSlider", "repressSlider" })
                LayoutCheck(Field<Control>(form, name).Height >= 84, label + ": vertical slider retains a usable track: " + name);
            foreach (string name in new[] { "calibrateActuation", "calibrateRelease", "calibrateRepress" })
            {
                Control button = Field<Control>(form, name);
                int captionWidth = TextRenderer.MeasureText(button.Text, button.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
                LayoutCheck(captionWidth <= button.ClientSize.Width - button.Padding.Horizontal, label + ": per-option calibration caption fits: " + name);
            }
            CapturePreview(form, artifacts, label + "-advanced-thresholds");
            RevealCurveSetting(form, settings); VisibleInside(form, settings, label + "/advanced/reachable-settings");
            CheckStableKeyboard(form, baseline, drawing, label + "/advanced/scroll"); CapturePreview(form, artifacts, label + "-advanced-settings");
            DetailMode(form, "controller");
            CheckDetailTabState(form, "controller", label + "/controller tabs");
            foreach (string name in new[] { "controllerPreview", "controllerSlotPicker", "controllerKindPicker", "addControllerSlot", "removeControllerSlot", "controllerNameEdit", "controllerConnector", "rgbColorButton", "rgbDefaultColor", "rgbOverrideToggle", "rgbStatus", "rgbRestoreButton" })
                VisibleInside(form, Field<Control>(form, name), label + "/controller/" + name);
            LayoutCheck(!Field<Control>(form, "keyTitle").Visible && !Field<Control>(form, "keyBehaviorPanel").Visible && !Field<Control>(form, "curve").Visible, label + ": controller is the only visible detail context.");
            CheckBar(form, Field<Control>(form, "controllerSlotPicker").Parent, label + "/controller/selection");
            var controller = Field<ControllerPreview>(form, "controllerPreview");
            var controllerLayout = Field<ControllerLayout>(controller, "layout");
            using (Graphics graphics = controller.CreateGraphics())
            using (Font statusFont = (Font)typeof(ControllerPreview).GetMethod("StatusFont", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null))
            using (var body = controllerLayout.CreateBodyPath())
            {
                object status = typeof(ControllerPreview).GetMethod("MeasureStatus", Private).Invoke(controller, new object[] { graphics, statusFont });
                int figureHeight = (int)status.GetType().GetField("FigureHeight").GetValue(status);
                object viewport = typeof(ControllerPreview).GetMethod("GetViewport", Private).Invoke(controller, new object[] { figureHeight });
                float scale = (float)viewport.GetType().GetField("Scale").GetValue(viewport);
                float figureWidth = body.GetBounds().Width * scale;
                LayoutCheck(figureWidth >= controller.ClientSize.Width * .8f,
                    label + ": controller drawing uses at least 80% of its available preview width: " + figureWidth + "; preview=" + controller.ClientSize.Width);
            }
            Rectangle targetBounds = (Rectangle)typeof(ControllerPreview).GetMethod("TargetBounds", Private).Invoke(controller, new object[] { controllerLayout.Find(OutputTarget.A) });
            LayoutCheck(targetBounds.Width >= 12 && targetBounds.Height >= 12,
                label + ": controller face buttons retain a usable click/drop area: " + targetBounds.Size);
            Point targetCenter = controller.PointToClient(new Point(targetBounds.Left + targetBounds.Width / 2, targetBounds.Top + targetBounds.Height / 2));
            LayoutCheck(controller.HitTestTarget(targetCenter) == OutputTarget.A, label + ": the visible controller target remains clickable in its drawn position.");
            CheckStableKeyboard(form, baseline, drawing, label + "/controller"); CapturePreview(form, artifacts, label + "-controller");
            DetailMode(form, null); CheckStableKeyboard(form, baseline, drawing, label + "/return-to-key");
            CheckDetailTabState(form, null, label + "/returned tabs");
            VisibleInside(form, Field<Control>(form, "keyTitle"), label + "/return-to-key/title");
            Console.WriteLine("LAYOUT: " + label + "; stable keyboard=" + baseline + "; settings=" + settings.ClientSize + "; square curve=" + curve.ClientSize);
            Call(form, "ShowPage", "keys"); Pump(form); VisibleInside(form, Field<Control>(form, "keys"), label + "/diagnostic keys");
            Call(form, "ShowPage", "mapping"); Pump(form);
            CheckStableKeyboard(form, baseline, drawing, label + "/return-from-diagnostics");
        }
        static string SelectedController(ComboBox picker)
        { return (string)picker.SelectedItem.GetType().GetField("Id").GetValue(picker.SelectedItem); }
        static void CheckControllerFooter(MainForm form, string label)
        {
            Profile profile = Current(form); var definitions = ControllerRouting.EffectiveControllers(profile);
            var connectors = Field<Dictionary<string, ControllerConnector>>(form, "footerConnectors");
            var runtime = Field<MultiControllerSession>(form, "runtime"); string selected = runtime.SelectedControllerId;
            Call(form, "UpdateControllerConnectionUi"); Pump(form);
            Check(connectors.Count == definitions.Count, label + ": footer shows exactly the profile's controller slots.");
            VisibleInside(form, Field<Control>(form, "controllerFooterSlots"), label + "/controller footer");
            Button allOff = Field<Button>(form, "allOffButton");
            VisibleInside(form, allOff, label + "/all controllers off");
            LayoutCheck(allOff.Width >= 80 && allOff.Height >= 32, label + ": the all-off action remains a usable button.");
            for (int i = 0; i < definitions.Count; i++)
            {
                ControllerDefinition definition = definitions[i];
                Check(connectors.ContainsKey(definition.Id), label + ": footer connector follows controller identity " + definition.Id);
                ControllerConnector connector = connectors[definition.Id];
                Check(connector.Vertical && (string)connector.Tag == definition.Id, label + ": footer uses an individual vertical connector for its controller.");
                Check(connector.ControllerLabel == (i + 1).ToString() && connector.ControllerName == definition.Name, label + ": footer number and name match the profile order.");
                int color = RgbOverridePlan.GetControllerColor(profile, definition.Id);
                Check(connector.AccentColor.ToArgb() == Color.FromArgb((color >> 16) & 255, (color >> 8) & 255, color & 255).ToArgb(), label + ": footer accent matches its controller color.");
                Check(Field<bool>(connector, "connected") == runtime.IsControllerEnabled(definition.Id), label + ": footer displays the controller's confirmed connection state.");
                VisibleInside(form, connector, label + "/footer/" + definition.Name);
            }
            Check(runtime.SelectedControllerId == selected, label + ": displaying footer status never changes the editing route.");
        }
        static void CheckManyControllerFooter(MainForm form, string artifacts)
        {
            Profile original = Current(form); Profile many = original;
            Control originalFocus = form.ActiveControl;
            var originalConnectors = Field<Dictionary<string, ControllerConnector>>(form, "footerConnectors").ToDictionary(pair => pair.Key, pair => pair.Value);
            for (int i = 1; i < ControllerRouting.MaximumControllers; i++)
                many = ControllerRouting.Add(many, "ui-footer-" + i, "Synthetic controller " + (i + 1), ControllerKind.Xbox360);
            Call(form, "Commit", many); Pump(form);
            var runtime = Field<MultiControllerSession>(form, "runtime"); string selected = runtime.SelectedControllerId;
            var footer = Field<FlowLayoutPanel>(form, "controllerFooterSlots");
            Point originalScroll = footer.AutoScrollPosition;
            var connectors = Field<Dictionary<string, ControllerConnector>>(form, "footerConnectors");
            var definitions = ControllerRouting.EffectiveControllers(Current(form));
            var instances = connectors.ToDictionary(pair => pair.Key, pair => pair.Value);
            Check(definitions.Count == 32 && connectors.Count == 32, "The footer supports all 32 configured controller slots.");
            Check(footer.HorizontalScroll.Visible && !footer.VerticalScroll.Visible, "Many footer connectors use one horizontal scrollbar without a second vertical scrollbar.");
            foreach (var previous in originalConnectors) Check(Object.ReferenceEquals(previous.Value, connectors[previous.Key]), "Adding more footer slots retains existing connector controls.");
            // Test real scrollbar input with ordinary message dispatch only.
            // A forced layout must not be required to make native scrolling work.
            CheckNativeFooterScroll(form, footer, connectors[definitions[definitions.Count - 1].Id], 7, "right end");
            CheckNativeFooterScroll(form, footer, connectors[definitions[0].Id], 6, "left end");
            for (int i = 0; i < definitions.Count; i++)
            {
                ControllerDefinition definition = definitions[i]; ControllerConnector connector = connectors[definition.Id];
                Check(footer.Controls.GetChildIndex(connector) == i && connector.ControllerLabel == (i + 1).ToString() && connector.ControllerName == definition.Name, "Footer slot " + (i + 1) + " preserves profile identity, order and label.");
                RevealFooterConnector(form, footer, connector);
                VisibleInside(form, connector, "32-controller footer / slot " + (i + 1));
                Point pickup = ConnectorPoint(ConnectorProperty<RectangleF>(connector, "PlugBounds"));
                Point footerPickup = footer.PointToClient(connector.PointToScreen(pickup));
                Check(footer.ClientRectangle.Contains(footerPickup) && Object.ReferenceEquals(footer.GetChildAtPoint(footerPickup), connector),
                    "Footer slot " + (i + 1) + " exposes its real plug hit area inside the scrolled viewport.");
                // Exercise pickup without releasing a connection request to the
                // app: this suite must never create a hardware/output backend.
                ConnectorMouse(connector, "OnMouseDown", pickup, MouseButtons.Left);
                try { Check(Field<bool>(connector, "armed"), "Footer slot " + (i + 1) + " accepts a plug pickup after scrolling."); }
                finally { ConnectorEscape(connector); }
                Check(!Field<bool>(connector, "armed") && !connector.Capture, "Canceling the footer pickup releases its gesture without connecting.");
                Check(Object.ReferenceEquals(instances[definition.Id], connectors[definition.Id]), "Scrolling retains footer slot " + (i + 1) + " and any pending gesture.");
                if (i == 0 || i == definitions.Count - 1) CapturePreview(form, artifacts, "footer-32-" + (i == 0 ? "first" : "last"));
            }
            Check(footer.HorizontalScroll.Value > 0, "The last controller can be brought into view by horizontal scrolling.");
            Call(form, "UpdateControllerConnectionUi"); Pump(form);
            Check(runtime.SelectedControllerId == selected, "Refreshing 32 footer connection states retains the current editing controller.");
            foreach (var instance in instances) Check(Object.ReferenceEquals(instance.Value, connectors[instance.Key]), "Refreshing statuses retains the existing footer connector instance.");
            AssertPassive(form);
            if (originalFocus != null && !originalFocus.IsDisposed && originalFocus.CanFocus) originalFocus.Focus();
            footer.AutoScrollPosition = new Point(-originalScroll.X, -originalScroll.Y);
            footer.PerformLayout(); Application.DoEvents();
            Check(footer.AutoScrollPosition == originalScroll, "Footer checks restore their initial scroll position before later layout captures.");
            Call(form, "Commit", original); Pump(form);
            Check(connectors.Count == originalConnectors.Count && !footer.HorizontalScroll.Visible, "Returning to the original profile removes the extra footer slots and its scrollbar.");
            foreach (var instance in instances)
                if (!originalConnectors.ContainsKey(instance.Key)) Check(instance.Value.IsDisposed, "A removed footer controller releases its control and gestures.");
            foreach (var previous in originalConnectors) Check(Object.ReferenceEquals(previous.Value, connectors[previous.Key]), "Returning to the original profile retains its existing footer connector.");
            Equal(Json(original), Json(Current(form)), "The 32-controller footer regression restores the original synthetic profile.");
        }
        static void RevealFooterConnector(MainForm form, FlowLayoutPanel footer, ControllerConnector connector)
        {
            Application.DoEvents();
            footer.ScrollControlIntoView(connector);
            // Finish the scroll container's own child layout before inspecting
            // native bounds. Relaying only the outer form layout is insufficient
            // for offscreen ScrollWindowEx children in the preview harness.
            footer.PerformLayout(); Application.DoEvents();
            Rectangle visible = footer.ClientRectangle;
            Rectangle bounds = footer.RectangleToClient(connector.RectangleToScreen(connector.ClientRectangle));
            if (!visible.Contains(bounds))
            {
                int contentLeft = footer.Padding.Left;
                foreach (Control child in footer.Controls)
                {
                    if (Object.ReferenceEquals(child, connector)) break;
                    if (child.Visible) contentLeft += child.Margin.Horizontal + child.Width;
                }
                int maximum = Math.Max(0, footer.HorizontalScroll.Maximum - footer.HorizontalScroll.LargeChange + 1);
                footer.AutoScrollPosition = new Point(Math.Min(contentLeft, maximum), 0);
                footer.PerformLayout(); Application.DoEvents();
            }
            Check(footer.ClientRectangle.Contains(footer.RectangleToClient(connector.RectangleToScreen(connector.ClientRectangle))),
                "The actual scrolled footer fully exposes " + connector.ControllerName + "; position=" + footer.AutoScrollPosition + "; bounds=" + connector.Bounds + ".");
        }
        static void CheckNativeFooterScroll(MainForm form, FlowLayoutPanel footer, ControllerConnector connector, int command, string label)
        {
            ThemeSend(footer.Handle, 0x0114, (IntPtr)command, IntPtr.Zero); // WM_HSCROLL; SB_RIGHT / SB_LEFT
            Application.DoEvents(); Application.DoEvents();
            Rectangle native = footer.RectangleToClient(connector.RectangleToScreen(connector.ClientRectangle));
            Check(footer.ClientRectangle.Contains(native), "Native footer scroll to " + label + " exposes " + connector.ControllerName +
                " after ordinary message dispatch; position=" + footer.AutoScrollPosition + "; managed=" + connector.Bounds + "; native=" + native + ".");
            VisibleInside(form, connector, "native footer / " + label);
            Point pickup = footer.PointToClient(connector.PointToScreen(ConnectorPoint(ConnectorProperty<RectangleF>(connector, "PlugBounds"))));
            Check(Object.ReferenceEquals(footer.GetChildAtPoint(pickup), connector), "Native footer scroll to " + label + " exposes the actual plug hit target.");
        }
        static T ConnectorProperty<T>(ControllerConnector connector, string name)
        {
            PropertyInfo property = typeof(ControllerConnector).GetProperty(name, Private | BindingFlags.Public);
            if (property == null) throw new InvalidOperationException("Connector property missing: " + name);
            return (T)property.GetValue(connector, null);
        }
        static Point ConnectorPoint(RectangleF bounds)
        { return Point.Round(new PointF(bounds.Left + bounds.Width * .32f, bounds.Top + bounds.Height * .42f)); }
        static void CaptureConnector(ControllerConnector connector, string artifacts, string state)
        {
            using (var bitmap = new Bitmap(connector.Width, connector.Height))
            { connector.DrawToBitmap(bitmap, connector.ClientRectangle); bitmap.Save(Path.Combine(artifacts, "connector-" + (connector.Vertical ? "vertical" : "horizontal") + "-" + state + ".png"), System.Drawing.Imaging.ImageFormat.Png); }
            string previousLanguage = UiText.Language;
            try
            {
                UiText.SetLanguage("en"); string directory = Path.Combine(artifacts, "english"); Directory.CreateDirectory(directory);
                using (var bitmap = new Bitmap(connector.Width, connector.Height))
                { connector.DrawToBitmap(bitmap, connector.ClientRectangle); bitmap.Save(Path.Combine(directory, "connector-" + (connector.Vertical ? "vertical" : "horizontal") + "-" + state + ".png"), System.Drawing.Imaging.ImageFormat.Png); }
            }
            finally { UiText.SetLanguage(previousLanguage); }
        }
        static RectangleF ConnectorTipBounds(ControllerConnector connector, RectangleF plug)
        { return (RectangleF)typeof(ControllerConnector).GetMethod("TipBounds", Private).Invoke(connector, new object[] { plug }); }
        static RectangleF ConnectorVisibleTipBounds(ControllerConnector connector, RectangleF plug)
        { return (RectangleF)typeof(ControllerConnector).GetMethod("VisibleTipBounds", Private).Invoke(connector, new object[] { plug }); }
        static Point ConnectorExposedPoint(RectangleF bounds, params RectangleF[] covered)
        {
            Point middle = Point.Round(new PointF(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2));
            if (bounds.Contains(middle) && !covered.Any(part => part.Contains(middle))) return middle;
            for (int y = (int)Math.Ceiling(bounds.Top + 1); y < bounds.Bottom - 1; y++)
                for (int x = (int)Math.Ceiling(bounds.Left + 1); x < bounds.Right - 1; x++)
                {
                    Point point = new Point(x, y);
                    if (!covered.Any(part => part.Contains(point))) return point;
                }
            throw new InvalidOperationException("Connector has no exposed clickable point in " + bounds + ".");
        }
        static Point ConnectorOffset(Point point, bool vertical, int along, int across)
        { return new Point(point.X + (vertical ? across : along), point.Y + (vertical ? along : across)); }
        static float ConnectorAxis(RectangleF bounds, bool vertical)
        { return vertical ? bounds.Top : bounds.Left; }
        static void ConnectorMouse(ControllerConnector connector, string method, Point point, MouseButtons buttons)
        {
            try { typeof(ControllerConnector).GetMethod(method, Private).Invoke(connector, new object[] { new MouseEventArgs(buttons, 1, point.X, point.Y, 0) }); }
            catch (TargetInvocationException ex) { throw new InvalidOperationException("Connector." + method + " failed.", ex.InnerException); }
        }
        static void ConnectorEscape(ControllerConnector connector)
        { typeof(ControllerConnector).GetMethod("OnKeyDown", Private).Invoke(connector, new object[] { new KeyEventArgs(Keys.Escape) }); }
        static void ConnectorClick(ControllerConnector connector, Point point)
        { ConnectorMouse(connector, "OnMouseDown", point, MouseButtons.Left); ConnectorMouse(connector, "OnMouseUp", point, MouseButtons.Left); }
        static KeyEventArgs ConnectorKey(ControllerConnector connector, string method, Keys key)
        {
            var args = new KeyEventArgs(key);
            typeof(ControllerConnector).GetMethod(method, Private).Invoke(connector, new object[] { args }); return args;
        }
        static bool ConnectorNativeRepeat(ControllerConnector connector, Keys key, int messageId)
        {
            Message message = Message.Create(connector.Handle, messageId, new IntPtr((int)key), new IntPtr((1L << 30) | 1));
            return (bool)typeof(ControllerConnector).GetMethod("ProcessKeyEventArgs", Private).Invoke(connector, new object[] { message });
        }
        sealed class MarqueeKeyboard : VisualKeyboard
        {
            public Keys TestModifiers;
            protected override Keys SelectionModifiers { get { return TestModifiers; } }
            public void Mouse(string action, Point point, MouseButtons buttons)
            {
                var args = new MouseEventArgs(buttons, 1, point.X, point.Y, 0);
                if (action == "down") OnMouseDown(args); else if (action == "move") OnMouseMove(args); else OnMouseUp(args);
            }
            public void Escape() { OnKeyDown(new KeyEventArgs(Keys.Escape)); }
            public void LoseFocus() { OnLostFocus(EventArgs.Empty); }
            public void MouseLeaveEvent() { OnMouseLeave(EventArgs.Empty); }
        }
        static MappingDragVisual CaptureKeyDragVisual(MarqueeKeyboard keyboard, int[] indices, Point pickup, out Bitmap source)
        {
            MappingDragVisual visual = null; Bitmap rendered = null;
            keyboard.SetSelectedKeys(indices); keyboard.Focus();
            Action<int[]> capture = delegate(int[] dragged) {
                Check(dragged.SequenceEqual(indices.OrderBy(index => index)), "The drag image receives the actual detached key selection.");
                Check(Object.ReferenceEquals(keyboard.Cursor, DragCursors.Grabbing), "Capturing the key image retains the closed drag hand.");
                visual = keyboard.CreateDragVisual(dragged);
                rendered = new Bitmap(keyboard.Width, keyboard.Height);
                keyboard.DrawToBitmap(rendered, keyboard.ClientRectangle);
            };
            keyboard.KeyDragRequested += capture;
            try
            {
                keyboard.Mouse("down", pickup, MouseButtons.Left);
                keyboard.Mouse("move", new Point(pickup.X + SystemInformation.DragSize.Width + 8, pickup.Y + SystemInformation.DragSize.Height + 8), MouseButtons.Left);
            }
            finally { keyboard.KeyDragRequested -= capture; keyboard.Mouse("up", pickup, MouseButtons.Left); }
            source = rendered;
            Check(visual != null && rendered != null, "The actual key-drag callback creates a key image after crossing the drag threshold.");
            return visual;
        }
        static ulong BitmapSignature(Bitmap bitmap)
        {
            ulong hash = 14695981039346656037UL;
            unchecked {
                for (int y = 0; y < bitmap.Height; y++) for (int x = 0; x < bitmap.Width; x++)
                { hash ^= (uint)bitmap.GetPixel(x, y).ToArgb(); hash *= 1099511628211UL; }
            }
            return hash;
        }
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongW", ExactSpelling = true)]
        static extern int ReadPreviewWindowStyle(IntPtr window, int index);
        static void CheckMappingDragPreview(Form host, Control sourceControl, MappingDragVisual visual)
        {
            ulong original = BitmapSignature(visual.Image); Bitmap borrowed = visual.Image;
            using (var preview = new DragPreviewWindow(visual))
            {
                Check(preview.ClientSize == visual.Image.Size, "The drag window retains the source image's original pixel dimensions.");
                foreach (Point cursor in new[] { Point.Empty, new Point(-29, -17), new Point(1919, 1079) })
                {
                    preview.FollowCursor(cursor);
                    Check(preview.Location == new Point(cursor.X - visual.Anchor.X, cursor.Y - visual.Anchor.Y), "A mapping image stays at its pickup anchor without flipping or clamping at a screen edge.");
                }
                Point first = new Point(-30000, -30000); preview.FollowCursor(first);
                sourceControl.Focus(); Check(sourceControl.Focused, "The preview starts with its drag source focused.");
                preview.Show(host); Application.DoEvents();
                Check(preview.Visible && preview.Location == new Point(first.X - visual.Anchor.X, first.Y - visual.Anchor.Y), "Showing the layered image preserves its requested offscreen position.");
                Check(sourceControl.Focused, "The drag image does not activate itself or steal source focus.");
                const int passiveStyles = 0x08000000 | 0x00000020 | 0x00000080 | 0x00080000;
                Check((ReadPreviewWindowStyle(preview.Handle, -20) & passiveStyles) == passiveStyles, "The actual preview window is layered, input-transparent, nonactivating and excluded from the taskbar.");
                foreach (int messageId in new[] { 0x0084, 0x0021 })
                {
                    object[] message = { Message.Create(preview.Handle, messageId, IntPtr.Zero, IntPtr.Zero) };
                    typeof(DragPreviewWindow).GetMethod("WndProc", Private).Invoke(preview, message);
                    Check(((Message)message[0]).Result == new IntPtr(messageId == 0x0084 ? -1 : 3), "The mapping image declines native hit testing and mouse activation.");
                }
                Point next = new Point(-29977, -29989); preview.FollowCursor(next);
                Check(preview.Location == new Point(next.X - visual.Anchor.X, next.Y - visual.Anchor.Y) && preview.ClientSize == visual.Image.Size,
                    "Following the pointer moves the same mapping image without changing its size or pickup offset.");
                Check(Object.ReferenceEquals(visual.Image, borrowed) && BitmapSignature(visual.Image) == original, "Showing and moving the layered preview preserves every source pixel.");
                preview.Hide();
            }
            Check(Object.ReferenceEquals(visual.Image, borrowed) && BitmapSignature(visual.Image) == original, "Disposing a preview leaves its borrowed mapping image usable by its owner.");
        }
        static void RunKeyDragVisuals(string artifacts)
        {
            int started = assertions;
            using (var host = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000), ClientSize = new Size(940, 380) })
            using (var keyboard = new MarqueeKeyboard { Dock = DockStyle.Fill, LayoutModel = KeyboardLayout.Tk75Iso() })
            {
                host.Controls.Add(keyboard); host.Show(); Application.DoEvents(); keyboard.Focus();
                Check(keyboard.CreateDragVisual(new int[0]) == null, "An empty key selection creates no drag image.");
                string[][] fixtures = { new[] { "KeyW" }, new[] { "Space" }, new[] { "Enter" }, new[] { "KeyA", "KeyW", "KeyD" } };
                string[] labels = { "w", "space", "iso-enter", "multi" };
                for (int fixture = 0; fixture < fixtures.Length; fixture++)
                {
                    KeyboardKeyDefinition[] keys = fixtures[fixture].Select(code => keyboard.LayoutModel.FindByCode(code)).ToArray();
                    int[] indices = keys.Select(key => key.KeyIndex.Value).ToArray();
                    RectangleF first = keyboard.GetKeyBounds(keys[0]), union = first;
                    foreach (KeyboardKeyDefinition key in keys.Skip(1)) union = RectangleF.Union(union, keyboard.GetKeyBounds(key));
                    Point pickup = new Point((int)Math.Round(first.Left + first.Width * .36), (int)Math.Round(first.Top + first.Height * .34));
                    Bitmap source;
                    using (MappingDragVisual visual = CaptureKeyDragVisual(keyboard, indices, pickup, out source))
                    using (source)
                    {
                        Bitmap image = visual.Image; Point origin = new Point(pickup.X - visual.Anchor.X, pickup.Y - visual.Anchor.Y);
                        Check(image.Width >= union.Width && image.Height >= union.Height && image.Width <= union.Width + 12 && image.Height <= union.Height + 12,
                            labels[fixture] + ": the image crops the actual selected key geometry without resizing it into a text card.");
                        Check(new Rectangle(Point.Empty, image.Size).Contains(visual.Anchor), labels[fixture] + ": the pickup anchor lies inside the selected-key image.");
                        Check(image.GetPixel(0, 0).A == 0 && image.GetPixel(image.Width - 1, 0).A == 0 && image.GetPixel(0, image.Height - 1).A == 0 && image.GetPixel(image.Width - 1, image.Height - 1).A == 0,
                            labels[fixture] + ": corners remain genuinely transparent instead of carrying the keyboard or a colored panel background.");
                        int opaque = 0, matching = 0, transparent = 0;
                        for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++)
                        {
                            Color pixel = image.GetPixel(x, y);
                            if (pixel.A == 0) transparent++;
                            if (pixel.A != 255) continue;
                            opaque++;
                            if (source.GetPixel(origin.X + x, origin.Y + y).ToArgb() == pixel.ToArgb()) matching++;
                        }
                        Check(opaque > image.Width * image.Height / 8 && matching == opaque, labels[fixture] + ": opaque drag pixels exactly match the same painted keyboard keys at their original position.");
                        Check(transparent > 0, labels[fixture] + ": the key image includes transparent space outside its physical keys.");
                        foreach (KeyboardKeyDefinition key in keys)
                        {
                            RectangleF bounds = keyboard.GetKeyBounds(key);
                            Point center = new Point((int)(bounds.Left + bounds.Width * .6) - origin.X, (int)(bounds.Top + bounds.Height * .5) - origin.Y);
                            Check(image.GetPixel(center.X, center.Y).A > 200, labels[fixture] + ": selected key " + key.Code + " remains visibly present at its physical position.");
                        }
                        if (fixture == 2)
                        {
                            Point notch = new Point((int)(first.Left + first.Width * .1), (int)(first.Top + first.Height * .75));
                            Check(keyboard.HitTest(notch) == null && image.GetPixel(notch.X - origin.X, notch.Y - origin.Y).A == 0,
                                "The ISO Enter key keeps its transparent lower-left notch.");
                        }
                        if (fixture == 3)
                        {
                            RectangleF omitted = keyboard.GetKeyBounds(keyboard.LayoutModel.FindByCode("KeyS"));
                            Point center = Point.Round(new PointF(omitted.Left + omitted.Width / 2, omitted.Top + omitted.Height / 2));
                            Check(image.GetPixel(center.X - origin.X, center.Y - origin.Y).A == 0, "A multi-key image keeps an unselected key inside its bounding area transparent.");
                        }
                        image.Save(Path.Combine(artifacts, "key-drag-" + labels[fixture] + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                        if (fixture == 0) CheckMappingDragPreview(host, keyboard, visual);
                        Point shifted = new Point(pickup.X + 5, pickup.Y + 4); Bitmap shiftedSource;
                        using (MappingDragVisual shiftedVisual = CaptureKeyDragVisual(keyboard, indices, shifted, out shiftedSource))
                        using (shiftedSource)
                        {
                            Check(shiftedVisual.Image.Size == image.Size && shiftedVisual.Anchor == new Point(visual.Anchor.X + 5, visual.Anchor.Y + 4),
                                labels[fixture] + ": a different pickup position changes only the anchor, not the keys' size or arrangement.");
                        }
                    }
                }
                host.Close();
            }
            Console.WriteLine("KEY DRAG VISUAL PASS: " + (assertions - started) + " assertions; real key pixels, transparency, pickup anchors and a passive layered preview.");
        }
        static void CheckMappingDragFeedback(MainForm form)
        {
            Type type = typeof(MainForm).GetNestedType("MappingDrag", BindingFlags.NonPublic);
            object drag = Activator.CreateInstance(type, true); FieldInfo active = typeof(MainForm).GetField("activeMappingDrag", Private);
            Check(active.GetValue(form) == null, "Feedback checks start without an active drag.");
            type.GetField("Layout").SetValue(drag, Field<VisualKeyboard>(form, "keyboard").LayoutModel);
            type.GetField("ProfilePath").SetValue(drag, Field<string>(form, "profilePath"));
            type.GetField("ControllerId").SetValue(drag, Field<MultiControllerSession>(form, "runtime").SelectedControllerId);
            Cursor previous = Cursor.Current; string original = Json(Current(form));
            active.SetValue(form, drag);
            try
            {
                type.GetField("Keys").SetValue(drag, new[] { 14 });
                foreach (DragDropEffects effect in new[] { DragDropEffects.None, DragDropEffects.Copy })
                {
                    var args = new GiveFeedbackEventArgs(effect, true); Call(form, "UpdateMappingDragFeedback", args);
                    Check(!args.UseDefaultCursors && Cursor.Current.Handle == DragCursors.Grabbing.Handle, "Key drag feedback retains the closed hand for " + effect + " without changing drop eligibility.");
                    Check(args.Effect == effect, "Cursor feedback preserves the actual " + effect + " drop effect.");
                }
                type.GetField("Keys").SetValue(drag, null); type.GetField("Target").SetValue(drag, (OutputTarget?)OutputTarget.A);
                var invalidOutput = new GiveFeedbackEventArgs(DragDropEffects.None, false); Call(form, "UpdateMappingDragFeedback", invalidOutput);
                Check(!invalidOutput.UseDefaultCursors && Cursor.Current.Handle == DragCursors.Grabbing.Handle && invalidOutput.Effect == DragDropEffects.None,
                    "A reverse controller drag retains the same closed hand between targets while preserving its unavailable-drop effect.");
                var validOutput = new GiveFeedbackEventArgs(DragDropEffects.Copy, true); Call(form, "UpdateMappingDragFeedback", validOutput);
                Check(!validOutput.UseDefaultCursors && Cursor.Current.Handle == DragCursors.Grabbing.Handle, "A valid reverse controller drag still uses the closed hand.");
                type.GetField("Cancelled").SetValue(drag, true);
                var cancelled = new GiveFeedbackEventArgs(DragDropEffects.Copy, false); Call(form, "UpdateMappingDragFeedback", cancelled);
                Check(cancelled.UseDefaultCursors, "A cancelled mapping drag relinquishes its custom feedback cursor.");
            }
            finally { active.SetValue(form, null); Cursor.Current = previous ?? Cursors.Default; }
            Equal(original, Json(Current(form)), "Feedback-only checks leave the mapping profile unchanged."); AssertPassive(form);
        }
        static void ControllerMouse(ControllerPreview controller, string method, Point point, MouseButtons buttons)
        {
            try { typeof(ControllerPreview).GetMethod(method, Private).Invoke(controller, new object[] { new MouseEventArgs(buttons, 1, point.X, point.Y, 0) }); }
            catch (TargetInvocationException error) { throw new InvalidOperationException("ControllerPreview." + method + " failed.", error.InnerException); }
        }
        static Rectangle ControllerTargetBounds(ControllerPreview controller, OutputTarget target)
        {
            ControllerRegion region = Field<ControllerLayout>(controller, "layout").Find(target);
            Rectangle screen = (Rectangle)typeof(ControllerPreview).GetMethod("TargetBounds", Private).Invoke(controller, new object[] { region });
            return new Rectangle(controller.PointToClient(screen.Location), screen.Size);
        }
        static void ControllerEscape(ControllerPreview controller)
        {
            object[] args = { Message.Create(controller.Handle, 0x0100, new IntPtr((int)Keys.Escape), IntPtr.Zero), Keys.Escape };
            Check((bool)typeof(ControllerPreview).GetMethod("ProcessCmdKey", Private).Invoke(controller, args), "Escape is handled by the controller's own gesture path.");
        }
        static MappingDragVisual CaptureControllerDragVisual(ControllerPreview controller, OutputTarget target, Point pickup, out Bitmap source)
        {
            MappingDragVisual visual = null; Bitmap rendered = null;
            Action<OutputTarget> capture = delegate(OutputTarget actual) {
                Check(actual == target && Object.ReferenceEquals(controller.Cursor, DragCursors.Grabbing), "The controller callback retains its original target and the shared closed hand.");
                visual = controller.CreateDragVisual(actual);
                rendered = new Bitmap(controller.Width, controller.Height); controller.DrawToBitmap(rendered, controller.ClientRectangle);
            };
            controller.Focus(); controller.TargetDragRequested += capture;
            try
            {
                ControllerMouse(controller, "OnMouseDown", pickup, MouseButtons.Left);
                ControllerMouse(controller, "OnMouseMove", new Point(pickup.X + SystemInformation.DragSize.Width + 8, pickup.Y + SystemInformation.DragSize.Height + 8), MouseButtons.Left);
            }
            finally { controller.TargetDragRequested -= capture; ControllerMouse(controller, "OnMouseUp", pickup, MouseButtons.Left); }
            source = rendered; Check(visual != null && rendered != null, "The actual controller drag callback produces an image without starting an OLE drag.");
            return visual;
        }
        static void RunControllerDragVisuals(string artifacts)
        {
            int started = assertions;
            foreach (ControllerStyle style in new[] { ControllerStyle.Xbox, ControllerStyle.PlayStation5 })
            using (var host = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000), ClientSize = new Size(600, 400) })
            using (var controller = new ControllerPreview { Dock = DockStyle.Fill, CompactStatus = true, Style = style })
            {
                string label = style == ControllerStyle.Xbox ? "xbox" : "ps5";
                host.Controls.Add(controller); host.Show(); Application.DoEvents(); controller.Focus();
                int selected = 0, drags = 0; OutputTarget? lastSelected = null;
                controller.TargetSelected += delegate(OutputTarget target) { selected++; lastSelected = target; };
                controller.TargetDragRequested += delegate { drags++; };
                Rectangle face = ControllerTargetBounds(controller, OutputTarget.A); Point pickup = new Point(face.Left + face.Width / 2, face.Top + face.Height / 2);
                Point smallMove = new Point(pickup.X + Math.Max(0, SystemInformation.DragSize.Width / 2 - 1), pickup.Y);
                Check(controller.HitTestTarget(pickup) == OutputTarget.A, label + ": the painted face button can be picked up at its visible center.");
                ControllerMouse(controller, "OnMouseMove", pickup, MouseButtons.None);
                Check(Object.ReferenceEquals(controller.Cursor, DragCursors.Grab), label + ": hovering a controller target uses the same open hand as the keyboard.");
                ControllerMouse(controller, "OnMouseDown", pickup, MouseButtons.Left);
                Check(controller.Capture && Object.ReferenceEquals(controller.Cursor, DragCursors.Grabbing), label + ": pressing a controller target immediately closes the hand.");
                ControllerMouse(controller, "OnMouseMove", smallMove, MouseButtons.Left);
                Check(drags == 0 && selected == 0 && Object.ReferenceEquals(controller.Cursor, DragCursors.Grabbing), label + ": subthreshold movement retains the pressed hand without selecting or dragging.");
                ControllerMouse(controller, "OnMouseUp", pickup, MouseButtons.Left);
                Check(selected == 1 && lastSelected == OutputTarget.A && drags == 0 && !controller.Capture && Object.ReferenceEquals(controller.Cursor, DragCursors.Grab),
                    label + ": a simple click selects once and restores the open hand.");
                typeof(ControllerPreview).GetMethod("OnMouseLeave", Private).Invoke(controller, new object[] { EventArgs.Empty });
                Check(controller.Cursor == Cursors.Default, label + ": leaving the controller clears its hover cursor.");
                ControllerMouse(controller, "OnMouseMove", new Point(1, controller.Height - 1), MouseButtons.None);
                Check(controller.Cursor == Cursors.Default, label + ": controller status and background space do not offer a grab cursor.");
                foreach (string cancel in new[] { "escape", "capture", "button-release", "disable", "hide", "resize", "focus", "style" })
                {
                    controller.Focus(); int selectedBefore = selected, dragsBefore = drags;
                    ControllerMouse(controller, "OnMouseDown", pickup, MouseButtons.Left);
                    ControllerMouse(controller, "OnMouseMove", smallMove, MouseButtons.Left);
                    if (cancel == "escape") ControllerEscape(controller);
                    else if (cancel == "capture") controller.Capture = false;
                    else if (cancel == "button-release") ControllerMouse(controller, "OnMouseMove", pickup, MouseButtons.None);
                    else if (cancel == "disable") controller.Enabled = false;
                    else if (cancel == "hide") controller.Visible = false;
                    else if (cancel == "resize") host.ClientSize = new Size(601, 401);
                    else if (cancel == "style") controller.Style = style == ControllerStyle.Xbox ? ControllerStyle.PlayStation5 : ControllerStyle.Xbox;
                    else typeof(ControllerPreview).GetMethod("OnLostFocus", Private).Invoke(controller, new object[] { EventArgs.Empty });
                    ControllerMouse(controller, "OnMouseUp", pickup, MouseButtons.Left);
                    Check(selected == selectedBefore && drags == dragsBefore && !controller.Capture && !Object.ReferenceEquals(controller.Cursor, DragCursors.Grabbing),
                        label + ": " + cancel + " cancels a pending controller click without selecting or dragging a stale target.");
                    if (cancel == "disable" || cancel == "hide") Check(controller.Cursor == Cursors.Default, label + ": unavailable controller previews show the default cursor.");
                    controller.Enabled = controller.Visible = true;
                    if (cancel == "resize") host.ClientSize = new Size(600, 400);
                    if (cancel == "style") controller.Style = style;
                    ControllerMouse(controller, "OnMouseMove", pickup, MouseButtons.None);
                    Check(Object.ReferenceEquals(controller.Cursor, DragCursors.Grab), label + ": hover is usable again after " + cancel + ".");
                }
                OutputTarget[] targets = { OutputTarget.A, OutputTarget.LeftTrigger, OutputTarget.DpadUp, OutputTarget.LeftXPositive };
                string[] names = { "face", "trigger", "dpad", "stick-ring" };
                for (int fixture = 0; fixture < targets.Length; fixture++)
                {
                    OutputTarget target = targets[fixture]; Rectangle bounds = ControllerTargetBounds(controller, target);
                    Point firstPickup = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
                    Check(controller.HitTestTarget(firstPickup) == target, label + "/" + names[fixture] + ": the original region's visible center resolves to the requested output.");
                    int selectedBefore = selected, dragsBefore = drags; Bitmap source;
                    using (MappingDragVisual visual = CaptureControllerDragVisual(controller, target, firstPickup, out source))
                    using (source)
                    {
                        Bitmap image = visual.Image; Point origin = new Point(firstPickup.X - visual.Anchor.X, firstPickup.Y - visual.Anchor.Y);
                        Check(image.Width >= bounds.Width && image.Height >= bounds.Height && image.Width <= bounds.Width + 12 && image.Height <= bounds.Height + 12,
                            label + "/" + names[fixture] + ": the ghost retains the original controller region's size rather than a text panel.");
                        Check(new Rectangle(Point.Empty, image.Size).Contains(visual.Anchor) && image.GetPixel(visual.Anchor.X, visual.Anchor.Y).A > 200,
                            label + "/" + names[fixture] + ": the anchor remains on the actual picked-up controller shape.");
                        Check(image.GetPixel(0, 0).A == 0 && image.GetPixel(image.Width - 1, 0).A == 0 && image.GetPixel(0, image.Height - 1).A == 0 && image.GetPixel(image.Width - 1, image.Height - 1).A == 0,
                            label + "/" + names[fixture] + ": the controller image has transparent corners, without controller-body or text-card background.");
                        int opaque = 0, matching = 0, transparent = 0;
                        for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++)
                        {
                            Color pixel = image.GetPixel(x, y); if (pixel.A == 0) transparent++;
                            if (pixel.A != 255) continue; opaque++;
                            if (source.GetPixel(origin.X + x, origin.Y + y).ToArgb() == pixel.ToArgb()) matching++;
                        }
                        Check(opaque > image.Width * image.Height / 8 && matching == opaque && transparent > 0,
                            label + "/" + names[fixture] + ": opaque pixels exactly match the painted controller region and surrounding space stays transparent.");
                        if (target == OutputTarget.LeftXPositive)
                            Check(image.GetPixel(0, image.Height / 2).A == 0, label + ": the selected stick-ring wedge keeps the space toward the inner stick transparent.");
                        image.Save(Path.Combine(artifacts, "controller-drag-" + label + "-" + names[fixture] + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                        if (style == ControllerStyle.Xbox && fixture == 0) CheckMappingDragPreview(host, controller, visual);
                        Point secondPickup = new Point(firstPickup.X + 2, firstPickup.Y + 1); Bitmap shiftedSource;
                        Check(controller.HitTestTarget(secondPickup) == target, label + ": the alternate pickup stays inside the same controller region.");
                        using (MappingDragVisual shifted = CaptureControllerDragVisual(controller, target, secondPickup, out shiftedSource))
                        using (shiftedSource)
                            Check(shifted.Image.Size == image.Size && shifted.Anchor == new Point(visual.Anchor.X + 2, visual.Anchor.Y + 1),
                                label + "/" + names[fixture] + ": a different pickup point changes only the anchor, preserving region geometry.");
                    }
                    Check(selected == selectedBefore && drags == dragsBefore + 2 && !controller.Capture && Object.ReferenceEquals(controller.Cursor, DragCursors.Grab),
                        label + "/" + names[fixture] + ": completed controller drags do not also emit a click selection and restore the open hand.");
                }
                controller.SetDropTarget(OutputTarget.A);
                using (var highlighted = new Bitmap(controller.Width, controller.Height))
                {
                    controller.DrawToBitmap(highlighted, controller.ClientRectangle);
                    Rectangle area = Rectangle.Intersect(controller.ClientRectangle, Rectangle.Inflate(ControllerTargetBounds(controller, OutputTarget.A), 5, 5)); int amber = 0;
                    for (int y = area.Top; y < area.Bottom; y++) for (int x = area.Left; x < area.Right; x++)
                    { Color pixel = highlighted.GetPixel(x, y); if (Math.Abs(pixel.R - 255) <= 12 && Math.Abs(pixel.G - 198) <= 12 && Math.Abs(pixel.B - 92) <= 12) amber++; }
                    Check(amber > 4, label + ": a valid drop target uses the same amber marker as the keyboard.");
                    highlighted.Save(Path.Combine(artifacts, "controller-drop-" + label + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                }
                controller.SetDropTarget(null); host.Close();
            }
            Console.WriteLine("CONTROLLER DRAG PASS: " + (assertions - started) + " assertions; click/drag separation, aborts, original region pixels, pickup anchors and shared feedback.");
        }
        static void RunMarqueeSelection(string artifacts)
        {
            int started = assertions;
            using (var host = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000), ClientSize = new Size(940, 380) })
            using (var keyboard = new MarqueeKeyboard { Dock = DockStyle.Fill, LayoutModel = KeyboardLayout.Tk75Iso() })
            {
                host.Controls.Add(keyboard); host.Show(); Application.DoEvents(); keyboard.Focus();
                RectangleF w = keyboard.GetKeyBounds(keyboard.LayoutModel.FindByIndex(14));
                KeyboardKeyDefinition eKey = keyboard.LayoutModel.FindByCode("KeyE"); RectangleF e = keyboard.GetKeyBounds(eKey);
                Point start = new Point((int)Math.Ceiling(w.Left) - 1, (int)(w.Top + w.Height / 2));
                Point end = new Point((int)(e.Left + 5), start.Y + 4);
                Check(keyboard.HitTest(start) == null, "Selection rectangles start in free space between the physical keys.");
                int[] expected = new[] { 14, eKey.KeyIndex.Value }.OrderBy(index => index).ToArray();
                Cursor grab = DragCursors.Grab, grabbing = DragCursors.Grabbing;
                Point keyCenter = Point.Round(new PointF(w.Left + w.Width / 2, w.Top + w.Height / 2));
                keyboard.Mouse("move", keyCenter, MouseButtons.None);
                Check(Object.ReferenceEquals(keyboard.Cursor, grab), "A draggable keyboard key uses the shared open-hand cursor.");
                keyboard.Mouse("move", keyCenter, MouseButtons.None);
                Check(Object.ReferenceEquals(keyboard.Cursor, grab), "Repeated hover over the same key reuses its open-hand cursor.");
                keyboard.Mouse("move", start, MouseButtons.None);
                Check(keyboard.Cursor == Cursors.Default, "Empty keyboard space uses the default cursor.");
                keyboard.Mouse("move", keyCenter, MouseButtons.None); keyboard.MouseLeaveEvent();
                Check(keyboard.Cursor == Cursors.Default, "Leaving the keyboard restores the standard Windows pointer.");
                keyboard.Mouse("move", keyCenter, MouseButtons.None); keyboard.Enabled = false;
                Check(keyboard.Cursor == Cursors.Default, "A disabled keyboard does not offer a drag cursor.");
                keyboard.Enabled = true; keyboard.Focus(); keyboard.Mouse("move", keyCenter, MouseButtons.None);
                Check(Object.ReferenceEquals(keyboard.Cursor, grab), "Re-enabling and hovering the same key restores the open hand.");
                keyboard.Mouse("down", keyCenter, MouseButtons.Left);
                Check(Object.ReferenceEquals(keyboard.Cursor, grabbing), "Pressing a keyboard key immediately shows the same closed hand used while dragging.");
                keyboard.Mouse("up", keyCenter, MouseButtons.Left);
                Check(Object.ReferenceEquals(keyboard.Cursor, grab), "Releasing a simple key click restores the open hand.");
                int changes = 0, drags = 0; int[] dragged = null;
                keyboard.SelectionChanged += delegate { changes++; };
                keyboard.KeyDragRequested += delegate(int[] indices) {
                    drags++; dragged = indices;
                    Check(Object.ReferenceEquals(keyboard.Cursor, grabbing), "The keyboard shows the shared closed hand while its mapping drag is running.");
                };
                keyboard.SetSelectedKeys(new[] { 9 }); changes = 0;
                keyboard.Mouse("down", start, MouseButtons.Left); keyboard.Mouse("move", end, MouseButtons.Left);
                Check(keyboard.Capture && changes == 0 && keyboard.SelectedKeyIndices.SequenceEqual(new[] { 9 }), "Rectangle preview preserves the committed selection until release.");
                Check(keyboard.Cursor == Cursors.Cross, "Drawing a selection rectangle keeps the crosshair cursor.");
                using (var bitmap = new Bitmap(keyboard.Width, keyboard.Height))
                { keyboard.DrawToBitmap(bitmap, keyboard.ClientRectangle); bitmap.Save(Path.Combine(artifacts, "keyboard-marquee-preview.png"), System.Drawing.Imaging.ImageFormat.Png); }
                keyboard.Mouse("up", end, MouseButtons.Left);
                Check(keyboard.SelectedKeyIndices.SequenceEqual(expected) && changes == 1 && drags == 0, "A rectangle replaces selection once and includes a key whose edge is only partly covered.");
                foreach (Keys modifier in new[] { Keys.Control, Keys.Shift })
                {
                    keyboard.SetSelectedKeys(new[] { 9 }); changes = 0; keyboard.TestModifiers = modifier;
                    keyboard.Mouse("down", start, MouseButtons.Left); keyboard.TestModifiers = Keys.None;
                    keyboard.Mouse("move", end, MouseButtons.Left); keyboard.Mouse("up", end, MouseButtons.Left);
                    Check(keyboard.SelectedKeyIndices.SequenceEqual(expected.Concat(new[] { 9 }).OrderBy(index => index)) && changes == 1,
                        modifier + " at the start of a rectangle adds covered keys to the existing selection.");
                }
                Point reverseStart = new Point((int)Math.Ceiling(e.Right) + 1, end.Y), reverseEnd = new Point((int)(w.Left + 5), start.Y);
                Check(keyboard.HitTest(reverseStart) == null, "Reverse selection starts in the gap after E.");
                keyboard.SetSelectedKeys(new[] { 9 }); keyboard.Mouse("down", reverseStart, MouseButtons.Left); keyboard.Mouse("move", reverseEnd, MouseButtons.Left); keyboard.Mouse("up", reverseEnd, MouseButtons.Left);
                Check(keyboard.SelectedKeyIndices.SequenceEqual(expected), "Drawing the rectangle right-to-left selects the same touched keys.");
                foreach (string cancel in new[] { "escape", "capture", "release", "disable", "hide", "resize", "focus" })
                {
                    keyboard.SetSelectedKeys(new[] { 9 }); keyboard.Focus(); changes = 0;
                    keyboard.Mouse("down", start, MouseButtons.Left); keyboard.Mouse("move", end, MouseButtons.Left);
                    if (cancel == "escape") keyboard.Escape();
                    else if (cancel == "capture") keyboard.Capture = false;
                    else if (cancel == "release") keyboard.Mouse("move", end, MouseButtons.None);
                    else if (cancel == "disable") keyboard.Enabled = false;
                    else if (cancel == "hide") keyboard.Visible = false;
                    else if (cancel == "resize") host.ClientSize = new Size(942, 382);
                    else keyboard.LoseFocus();
                    keyboard.Mouse("up", end, MouseButtons.Left);
                    Check(keyboard.SelectedKeyIndices.SequenceEqual(new[] { 9 }) && changes == 0 && !keyboard.Capture, "Cancelling a selection rectangle via " + cancel + " preserves the original selection.");
                    Check(keyboard.Cursor == Cursors.Default || Object.ReferenceEquals(keyboard.Cursor, grab), "Cancelling via " + cancel + " restores the keyboard hover cursor.");
                    keyboard.Enabled = keyboard.Visible = true; if (cancel == "resize") host.ClientSize = new Size(940, 380);
                }
                keyboard.SetSelectedKeys(new[] { 9, 14 }); keyboard.Focus(); keyboard.TestModifiers = Keys.Control;
                keyboard.Mouse("down", keyCenter, MouseButtons.Left); keyboard.TestModifiers = Keys.None;
                Check(keyboard.SelectedKeyIndices.SequenceEqual(new[] { 9 }) && Object.ReferenceEquals(keyboard.Cursor, grabbing), "Ctrl-clicking a selected key removes it while showing the closed hand.");
                keyboard.Mouse("move", new Point(keyCenter.X + 30, keyCenter.Y + 20), MouseButtons.Left);
                Check(drags == 0 && Object.ReferenceEquals(keyboard.Cursor, grabbing), "Moving after Ctrl-deselecting a key keeps the closed hand without dragging the remaining selection.");
                keyboard.Mouse("up", keyCenter, MouseButtons.Left);
                Check(keyboard.SelectedKeyIndices.SequenceEqual(new[] { 9 }) && Object.ReferenceEquals(keyboard.Cursor, grab), "Releasing a Ctrl-deselected key restores the open hand and retains the selection.");
                keyboard.SetSelectedKeys(new[] { 9, 14 }); keyboard.Focus();
                keyboard.Mouse("down", keyCenter, MouseButtons.Left);
                Check(Object.ReferenceEquals(keyboard.Cursor, grabbing), "Arming a selected-key drag immediately shows the closed hand.");
                Point smallKeyMove = new Point(keyCenter.X + Math.Max(0, SystemInformation.DragSize.Width / 2 - 1), keyCenter.Y);
                keyboard.Mouse("move", smallKeyMove, MouseButtons.Left);
                Check(drags == 0 && Object.ReferenceEquals(keyboard.Cursor, grabbing), "A key movement below the drag threshold keeps the closed hand without starting a drag.");
                keyboard.Mouse("move", new Point(keyCenter.X + 30, keyCenter.Y + 20), MouseButtons.Left); keyboard.Mouse("up", keyCenter, MouseButtons.Left);
                Check(drags == 1 && dragged.SequenceEqual(new[] { 9, 14 }) && keyboard.SelectedKeyIndices.SequenceEqual(new[] { 9, 14 }), "Starting on a selected key preserves multi-key drag to the controller.");
                Check(Object.ReferenceEquals(keyboard.Cursor, grab), "Releasing the mapping drag restores the open hand over the key.");
                keyboard.Mouse("move", keyCenter, MouseButtons.None);
                Check(Object.ReferenceEquals(keyboard.Cursor, grab), "Hovering the key again after the drag reuses the shared open hand.");
                keyboard.Mouse("down", start, MouseButtons.Left); keyboard.Mouse("move", end, MouseButtons.Left);
                keyboard.LayoutModel = KeyboardLayout.Tk75Ansi(); keyboard.Mouse("up", end, MouseButtons.Left);
                Check(keyboard.SelectedKeyIndices.Length == 0 && !keyboard.Capture, "Changing physical layout cancels the rectangle without applying stale key identities.");
                host.Close();
            }
            Console.WriteLine("MARQUEE PASS: " + (assertions - started) + " assertions; physical key shapes, additive selection, aborts and original mapping drag.");
        }
        static void CheckMultiKeySignalScope(MainForm form)
        {
            Profile original = Current(form), fixture = ControllerRouting.Add(original, "ui-signal-other", "Other controller", ControllerKind.Xbox360);
            fixture = MappingAssignments.Add(fixture, new[] { 15 }, OutputTarget.X, ControllerRouting.DefaultControllerId);
            fixture = MappingAssignments.Add(fixture, new[] { 9, 14 }, OutputTarget.Y, "ui-signal-other");
            Call(form, "Commit", fixture); Pump(form);
            var keyboard = Field<VisualKeyboard>(form, "keyboard");
            keyboard.SelectKey(keyboard.LayoutModel.FindByIndex(14), false); Pump(form);
            string firstId = Current(form).Bindings.First(binding => binding.KeyIndex == 14 && binding.ControllerId == ControllerRouting.DefaultControllerId).BindingId;
            SelectBindings(form, firstId); keyboard.SelectKey(keyboard.LayoutModel.FindByIndex(9), true); Pump(form);
            string[] scope = Current(form).Bindings.Where(binding => (binding.KeyIndex == 9 || binding.KeyIndex == 14) && binding.ControllerId == ControllerRouting.DefaultControllerId).Select(binding => binding.BindingId).ToArray();
            Check(new HashSet<string>((string[])Call(form, "SelectedBindings")).SetEquals(scope), "Adding a physical key selects the bindings from both selected keys.");
            foreach (string action in new[] { "curve", "property", "preset" })
            {
                SelectBindings(form, firstId); Profile before = Current(form), expected = Current(form);
                if (action == "curve")
                {
                    var edit = new SignalSettings { Curve = CurveKind.Custom, CustomPoints = new List<CurvePoint> { new CurvePoint(0, 0), new CurvePoint(.4, .2), new CurvePoint(1, 1) } };
                    foreach (Binding binding in expected.Bindings.Where(binding => scope.Contains(binding.BindingId)))
                    { binding.Processing.Curve = edit.Curve; binding.Processing.CustomPoints = edit.CustomPoints.Select(point => new CurvePoint(point.X, point.Y)).ToList(); }
                    Call(form, "ApplyCurve", edit); Pump(form);
                }
                else if (action == "property")
                {
                    foreach (Binding binding in expected.Bindings.Where(binding => scope.Contains(binding.BindingId))) binding.Processing.Scale = .37;
                    Edit(form, "Scale", "0.37");
                }
                else
                {
                    foreach (Binding binding in expected.Bindings.Where(binding => scope.Contains(binding.BindingId))) binding.Processing = new SignalSettings { Curve = CurveKind.Logarithmic, Exponent = 4 };
                    Field<ComboBox>(form, "preset").SelectedIndex = 2; Call(form, "ApplyPreset"); Pump(form);
                }
                Equal(Json(expected), Json(Current(form)), "Multi-key " + action + " updates every selected key's binding despite a single selected row, preserving other keys and controllers.");
                Call(form, "Undo"); Pump(form); Equal(Json(before), Json(Current(form)), "Multi-key " + action + " is one complete undo step.");
            }
            Call(form, "Commit", original); SelectKeys(form, 14); DetailMode(form, null);
        }
        static void CheckMultiKeyInputScope(MainForm form)
        {
            Profile original = Current(form), fixture = Current(form);
            fixture.Inputs = new List<KeyInputSettings> {
                new KeyInputSettings { KeyIndex = 9, ActuationPoint = .2, ReleaseMovement = .03, PressMovement = .05, OppositeKeyIndex = 21, OppositePolicy = InputOpposedPolicy.FirstPressed },
                new KeyInputSettings { KeyIndex = 14, RapidTriggerEnabled = true, ActuationPoint = .4, ReleaseMovement = .06, PressMovement = .07, OppositeKeyIndex = 15, OppositePolicy = InputOpposedPolicy.LastPressed },
                new KeyInputSettings { KeyIndex = 15, ActuationPoint = .65, ReleaseMovement = .08, PressMovement = .09, OppositeKeyIndex = 14, OppositePolicy = InputOpposedPolicy.LastPressed },
                new KeyInputSettings { KeyIndex = 21, RapidTriggerEnabled = true, ActuationPoint = .8, ReleaseMovement = .11, PressMovement = .12, OppositeKeyIndex = 9, OppositePolicy = InputOpposedPolicy.FirstPressed }
            };
            Call(form, "Commit", fixture); SelectKeys(form, 9, 14); DetailMode(form, "input");
            Check(Field<CheckBox>(form, "rapidTrigger").CheckState == CheckState.Indeterminate, "Mixed rapid-trigger states remain explicit for multiple selected keys.");
            foreach (string name in new[] { "rapidTrigger", "actuationPoint", "releaseMovement", "pressMovement", "applyKeyBehavior" })
                Check(Field<Control>(form, name).Enabled, "Multiple selected keys keep the " + name + " editor available.");
            Profile expected = Current(form);
            foreach (KeyInputSettings input in expected.Inputs.Where(input => input.KeyIndex == 9 || input.KeyIndex == 14)) input.ActuationPoint = .31;
            Field<NumericUpDown>(form, "actuationPoint").Value = 31; Call(form, "SaveKeyBehavior"); Pump(form);
            Equal(Json(expected), Json(Current(form)), "A bulk input field changes only that field, preserving mixed settings and both existing SOCD pairs.");
            Call(form, "Undo"); Pump(form); Equal(Json(fixture), Json(Current(form)), "A bulk input edit is one undo step.");
            expected = Current(form);
            foreach (KeyInputSettings input in expected.Inputs.Where(input => input.KeyIndex == 9 || input.KeyIndex == 14)) input.RapidTriggerEnabled = true;
            Field<CheckBox>(form, "rapidTrigger").CheckState = CheckState.Checked; Call(form, "SaveKeyBehavior"); Pump(form);
            Equal(Json(expected), Json(Current(form)), "Rapid trigger can be enabled for every selected key without copying another key's numeric settings.");
            Call(form, "Undo"); Pump(form); Equal(Json(fixture), Json(Current(form)), "Bulk rapid-trigger editing is one undo step.");
            expected = Current(form);
            foreach (KeyInputSettings input in expected.Inputs)
            {
                input.OppositeKeyIndex = input.KeyIndex == 9 ? (int?)14 : input.KeyIndex == 14 ? (int?)9 : null;
                input.OppositePolicy = input.OppositeKeyIndex.HasValue ? InputOpposedPolicy.LastPressed : InputOpposedPolicy.Neutral;
            }
            ComboBox opposite = Field<ComboBox>(form, "oppositeKey"), policy = Field<ComboBox>(form, "oppositeMode");
            opposite.SelectedItem = opposite.Items.Cast<object>().Single(item => (int?)item.GetType().GetField("Index").GetValue(item) == 14); Pump(form);
            Profile pairedBeforePolicy = Current(form);
            Equal(Json(KeyInputEditing.SetPairing(fixture, new[] { 9, 14 }, 14, InputOpposedPolicy.FirstPressed)), Json(pairedBeforePolicy),
                "Choosing a two-key opposite commits that pairing immediately without Apply or a tab change.");
            policy.SelectedItem = policy.Items.Cast<object>().Single(item => (InputOpposedPolicy)item.GetType().GetField("Value").GetValue(item) == InputOpposedPolicy.LastPressed); Pump(form);
            Equal(Json(expected), Json(Current(form)), "Selecting the policy immediately updates both paired keys while preserving activation settings.");
            Call(form, "Undo"); Pump(form); Equal(Json(pairedBeforePolicy), Json(Current(form)), "The policy choice has its own undo step.");
            Call(form, "Undo"); Pump(form); Equal(Json(fixture), Json(Current(form)), "The preceding opposite-key choice has one undo step.");
            expected = Current(form);
            foreach (KeyInputSettings input in expected.Inputs.Where(input => input.KeyIndex == 9 || input.KeyIndex == 14)) input.ActuationPoint = .27;
            Field<NumericUpDown>(form, "actuationPoint").Value = 27;
            var keyboard = Field<VisualKeyboard>(form, "keyboard"); keyboard.SelectKey(keyboard.LayoutModel.FindByIndex(15), false); Pump(form);
            Equal(Json(expected), Json(Current(form)), "Changing physical selection flushes a pending input draft to its original selected keys only.");
            Call(form, "Undo"); Pump(form); Equal(Json(fixture), Json(Current(form)), "Auto-flushing a captured multi-key input draft is one undo step.");
            SelectKeys(form, 9, 14, 15); DetailMode(form, "input");
            Check(!Field<ComboBox>(form, "oppositeKey").Enabled && !Field<ComboBox>(form, "oppositeMode").Enabled, "Selecting more than two keys disables ambiguous SOCD pairing controls.");
            expected = Current(form);
            foreach (KeyInputSettings input in expected.Inputs.Where(input => input.KeyIndex != 21)) input.ReleaseMovement = .18;
            Field<NumericUpDown>(form, "releaseMovement").Value = 18; Call(form, "SaveKeyBehavior"); Pump(form);
            Equal(Json(expected), Json(Current(form)), "Three-key numeric editing retains established opposing pairs and the unselected key's settings.");
            Call(form, "Undo"); Pump(form); SelectKeys(form, 9, 14); DetailMode(form, "input");
            expected = Current(form); expected.Inputs.RemoveAll(input => input.KeyIndex == 9 || input.KeyIndex == 14);
            foreach (KeyInputSettings input in expected.Inputs) { input.OppositeKeyIndex = null; input.OppositePolicy = InputOpposedPolicy.Neutral; }
            Call(form, "ResetKeyBehavior"); Pump(form);
            Equal(Json(expected), Json(Current(form)), "Bulk reset removes only selected input settings and cleanly releases their existing pairs.");
            Call(form, "Undo"); Pump(form); Equal(Json(fixture), Json(Current(form)), "Bulk reset including pair cleanup is one undo step.");
            Call(form, "Commit", original); SelectKeys(form, 14); DetailMode(form, null); CheckSocdDropdownSelection(form);
        }
        static void CheckSocdDropdownSelection(MainForm form)
        {
            Profile original = Current(form), fixture = Current(form);
            fixture.Inputs = new List<KeyInputSettings> {
                new KeyInputSettings { KeyIndex = 9, ActuationPoint = .18 },
                new KeyInputSettings { KeyIndex = 14, RapidTriggerEnabled = true, ActuationPoint = .42, ReleaseMovement = .07, PressMovement = .09 },
                new KeyInputSettings { KeyIndex = 21, ActuationPoint = .73 }
            };
            try
            {
                Call(form, "Commit", fixture); SelectKeys(form, 9); DetailMode(form, null); RevealKeySetting(form, Field<Control>(form, "keySocdPanel"));
                var opposite = Field<ComboBox>(form, "oppositeKey"); var policy = Field<ComboBox>(form, "oppositeMode");
                object baseline = Field<EditHistory>(form, "history").SnapshotToken;
                for (int refresh = 0; refresh < 3; refresh++) Call(form, "RefreshInputEditor");
                Check(Object.ReferenceEquals(baseline, Field<EditHistory>(form, "history").SnapshotToken) && !Field<bool>(form, "inputDirty"),
                    "Rebuilding SOCD choices does not create phantom edits or history entries.");
                opposite.SelectedItem = opposite.Items.Cast<object>().Single(item => (int?)item.GetType().GetField("Index").GetValue(item) == 14); Pump(form);
                var paired = KeyInputEditing.SetPairing(fixture, new[] { 9 }, 14, InputOpposedPolicy.Neutral);
                Equal(Json(paired), Json(Current(form)), "The actual opposite-key dropdown immediately pairs the selected key and retains the unselected key's settings.");
                Check(Field<string>(form, "detailsMode") == null && !Field<bool>(form, "inputDirty"), "Direct pairing remains in Keys with no hidden pending Apply action.");
                policy.SelectedItem = policy.Items.Cast<object>().Single(item => (InputOpposedPolicy)item.GetType().GetField("Value").GetValue(item) == InputOpposedPolicy.LastPressed); Pump(form);
                var changed = KeyInputEditing.SetPairing(paired, new[] { 9 }, 14, InputOpposedPolicy.LastPressed);
                Equal(Json(changed), Json(Current(form)), "The actual policy dropdown immediately updates the pair and preserves every unrelated field.");
                Call(form, "Undo"); Pump(form); Equal(Json(paired), Json(Current(form)), "One undo restores the policy before the latest dropdown choice.");
                Call(form, "Undo"); Pump(form); Equal(Json(fixture), Json(Current(form)), "A second undo restores the original unpaired keys without phantom rebuild steps.");
                Call(form, "Redo"); Pump(form);
                opposite.SelectedItem = opposite.Items.Cast<object>().Single(item => (int?)item.GetType().GetField("Index").GetValue(item) == null); Pump(form);
                Equal(Json(KeyInputEditing.SetPairing(paired, new[] { 9 }, null, InputOpposedPolicy.Neutral)), Json(Current(form)),
                    "Choosing No opposite immediately unpairs both keys without touching another key.");
                Call(form, "Undo"); Pump(form); Equal(Json(paired), Json(Current(form)), "Unpairing is one undo step.");
                SelectKeys(form, 9, 14);
                object beforePreserve = Field<EditHistory>(form, "history").SnapshotToken;
                opposite.SelectedItem = opposite.Items.Cast<object>().Single(item => (bool)item.GetType().GetField("Preserve").GetValue(item)); Pump(form);
                Check(!policy.Enabled && Object.ReferenceEquals(beforePreserve, Field<EditHistory>(form, "history").SnapshotToken) && !Field<bool>(form, "inputDirty"),
                    "Keep existing pairs disables policy editing and creates no phantom history entry.");
            }
            finally { Call(form, "Commit", original); SelectKeys(form, 14); DetailMode(form, null); }
        }
        static void CheckConnectorKeys(ControllerConnector connector, List<bool> requests, string label)
        {
            foreach (Keys key in new[] { Keys.Space, Keys.Enter })
            {
                ConnectorKey(connector, "OnKeyUp", key); connector.SetConnection(false, null); requests.Clear();
                KeyEventArgs down = ConnectorKey(connector, "OnKeyDown", key);
                for (int repeat = 0; repeat < 4; repeat++) ConnectorKey(connector, "OnKeyDown", key);
                Check(requests.Count == 1 && requests[0], label + ": held " + key + " requests exactly one confirmed transition.");
                Check(down.Handled && !down.SuppressKeyPress, label + ": activation keeps the release event available to clear its latch.");
                KeyEventArgs up = ConnectorKey(connector, "OnKeyUp", key);
                Check(!up.Handled && !up.SuppressKeyPress, label + ": the activation key release is not suppressed.");
                ConnectorKey(connector, "OnKeyDown", key);
                Check(requests.Count == 2 && !requests[1], label + ": release and a fresh " + key + " press allow the next transition.");
                ConnectorEscape(connector); ConnectorKey(connector, "OnKeyDown", key);
                Check(requests.Count == 2, label + ": Escape cancels without turning a held activation key into another press.");
                ConnectorKey(connector, "OnKeyUp", key);
                var typed = new KeyPressEventArgs(key == Keys.Space ? ' ' : '\r');
                typeof(ControllerConnector).GetMethod("OnKeyPress", Private).Invoke(connector, new object[] { typed });
                Check(typed.Handled, label + ": activation characters are consumed without swallowing key release.");
                foreach (Keys modifier in new[] { Keys.Control, Keys.Alt, Keys.Shift })
                {
                    connector.SetConnection(false, null); requests.Clear();
                    ConnectorKey(connector, "OnKeyDown", key | modifier); ConnectorKey(connector, "OnKeyDown", key);
                    Check(requests.Count == 0, label + ": modified " + key + " and subsequent repeats after modifier release produce no action.");
                    ConnectorKey(connector, "OnKeyUp", key); ConnectorKey(connector, "OnKeyDown", key);
                    Check(requests.Count == 1 && requests[0], label + ": a fresh unmodified press works after releasing the modified key.");
                    ConnectorKey(connector, "OnKeyUp", key);
                }
                foreach (string reset in new[] { "focus", "disable", "hide" })
                {
                    connector.SetConnection(false, null); requests.Clear(); ConnectorKey(connector, "OnKeyDown", key);
                    if (reset == "focus") typeof(ControllerConnector).GetMethod("OnLostFocus", Private).Invoke(connector, new object[] { EventArgs.Empty });
                    else if (reset == "disable") { connector.Enabled = false; connector.Enabled = true; }
                    else { connector.Visible = false; connector.Visible = true; }
                    Check(ConnectorNativeRepeat(connector, key, 0x0100) && ConnectorNativeRepeat(connector, key, 0x0104), label + ": native key-repeat messages are handled after " + reset + ".");
                    Check(requests.Count == 1, label + ": native repeat bit prevents a held key from toggling after " + reset + ".");
                    ConnectorKey(connector, "OnKeyDown", key);
                    Check(requests.Count == 2 && !requests[1], label + ": " + reset + " clears the latch for a genuinely fresh press.");
                    ConnectorKey(connector, "OnKeyUp", key);
                }
            }
        }
        static void CheckInsertedMetal(ControllerConnector connector, List<bool> requests, string artifacts, string label)
        {
            bool vertical = connector.Vertical; connector.SetConnection(false, null); requests.Clear();
            RectangleF socket = ConnectorProperty<RectangleF>(connector, "SocketBounds"), opening = ConnectorProperty<RectangleF>(connector, "SocketOpeningBounds");
            RectangleF rest = ConnectorProperty<RectangleF>(connector, "PlugBounds"), tip = ConnectorTipBounds(connector, rest);
            float metalBreadth = vertical ? tip.Width : tip.Height, openingBreadth = vertical ? opening.Width : opening.Height;
            Check(metalBreadth <= openingBreadth + .1f && metalBreadth >= openingBreadth * .9f - .1f,
                label + ": the metal fits the socket's inner width with only a small clearance.");
            Point start = ConnectorPoint(rest);
            int partialDistance = (int)Math.Round(vertical ? opening.Top + opening.Height / 2 - tip.Top : opening.Left + opening.Width / 2 - tip.Right);
            using (var empty = new Bitmap(connector.Width, connector.Height))
            {
                connector.DrawToBitmap(empty, connector.ClientRectangle);
                ConnectorMouse(connector, "OnMouseDown", start, MouseButtons.Left);
                ConnectorMouse(connector, "OnMouseMove", ConnectorOffset(start, vertical, partialDistance, 0), MouseButtons.Left);
                RectangleF inserted = ConnectorTipBounds(connector, ConnectorProperty<RectangleF>(connector, "PlugBounds"));
                RectangleF sample = RectangleF.Intersect(RectangleF.Inflate(opening, -1, -1), inserted);
                int sampled = 0, visiblyChanged = 0;
                using (var partial = new Bitmap(connector.Width, connector.Height))
                {
                    connector.DrawToBitmap(partial, connector.ClientRectangle);
                    for (int y = (int)Math.Ceiling(sample.Top); y < sample.Bottom; y++)
                        for (int x = (int)Math.Ceiling(sample.Left); x < sample.Right; x++)
                        {
                            Color before = empty.GetPixel(x, y), after = partial.GetPixel(x, y); sampled++;
                            if (Math.Abs(before.R - after.R) + Math.Abs(before.G - after.G) + Math.Abs(before.B - after.B) > 60) visiblyChanged++;
                        }
                }
                Check(sampled >= 4 && visiblyChanged >= Math.Max(2, sampled / 3),
                    label + ": partial insertion paints visible metal inside the socket cavity instead of hiding it at the entrance.");
                CaptureConnector(connector, artifacts, "partial");
            }
            ConnectorEscape(connector);
            Check(requests.Count == 0 && ConnectorProperty<RectangleF>(connector, "PlugBounds") == rest, label + ": inspecting partial insertion preserves the confirmed state.");

            connector.SetConnection(true, null);
            RectangleF dock = ConnectorProperty<RectangleF>(connector, "PlugBounds"), dockTip = ConnectorTipBounds(connector, dock), visibleTip = ConnectorVisibleTipBounds(connector, dock);
            Check(Math.Abs(vertical ? visibleTip.Top - opening.Top : visibleTip.Right - opening.Right) < .1f,
                label + ": inserted metal stays visible up to the opposite inner edge.");
            Point hidden = ConnectorExposedPoint(RectangleF.Intersect(dockTip, socket), visibleTip, dock);
            int retract = vertical ? 9 : -9;
            ConnectorMouse(connector, "OnMouseDown", hidden, MouseButtons.Left);
            ConnectorMouse(connector, "OnMouseMove", ConnectorOffset(hidden, vertical, retract, 0), MouseButtons.Left);
            ConnectorMouse(connector, "OnMouseUp", ConnectorOffset(hidden, vertical, retract, 0), MouseButtons.Left);
            Check(requests.Count == 0 && ConnectorProperty<RectangleF>(connector, "PlugBounds") == dock,
                label + ": metal hidden behind the far rim cannot be grabbed or become a toggle while dragging.");
            Point visible = ConnectorExposedPoint(RectangleF.Intersect(visibleTip, opening), dock);
            ConnectorMouse(connector, "OnMouseDown", visible, MouseButtons.Left);
            ConnectorMouse(connector, "OnMouseMove", ConnectorOffset(visible, vertical, retract, 0), MouseButtons.Left);
            Check(Math.Abs(ConnectorAxis(ConnectorProperty<RectangleF>(connector, "PlugBounds"), vertical) - ConnectorAxis(dock, vertical) - retract) < .1f,
                label + ": visible metal within the opening remains a real grip area.");
            ConnectorEscape(connector); connector.SetConnection(false, null);
            float travel = ConnectorAxis(dock, vertical) - ConnectorAxis(rest, vertical);
            ConnectorMouse(connector, "OnMouseDown", start, MouseButtons.Left);
            ConnectorMouse(connector, "OnMouseMove", ConnectorOffset(start, vertical, (int)Math.Round(travel * 3), 0), MouseButtons.Left);
            RectangleF pushed = ConnectorProperty<RectangleF>(connector, "PlugBounds"), collision = RectangleF.Intersect(pushed, socket);
            Check(pushed == dock && (collision.Width <= .01f || collision.Height <= .01f) && requests.Count == 0,
                label + ": pushing well past the socket stops the plastic grip flush at its outer edge without entering it.");
            ConnectorEscape(connector);
        }
        static void RunConnectorGestures(string artifacts)
        {
            // Keep the small-connector visual reference independent of the
            // application's default display language.
            string previousLanguage = UiText.Language;
            try { UiText.SetLanguage("de"); RunConnectorGesturesCore(artifacts); }
            finally { UiText.SetLanguage(previousLanguage); }
        }
        static void RunConnectorGesturesCore(string artifacts)
        {
            int started = assertions;
            foreach (bool vertical in new[] { false, true })
            using (var host = new Form { Font = SystemFonts.DefaultFont, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000), ClientSize = new Size(520, 180) })
            using (var connector = new ControllerConnector { Vertical = vertical, Compact = true, Size = vertical ? new Size(56, 64) : new Size(420, 70), Location = new Point(20, 20), ControllerLabel = "2", ControllerName = "Synthetic controller", AccentColor = Color.FromArgb(55, 166, 207) })
            {
                host.Controls.Add(connector); host.Show(); Application.DoEvents(); connector.Focus();
                Size initialSize = connector.Size;
                string label = vertical ? "vertical mini" : "horizontal";
                var requests = new List<bool>(); bool confirm = false; Action<bool> observeRequest = null;
                connector.ConnectionRequested += delegate(bool next) { requests.Add(next); if (observeRequest != null) observeRequest(next); if (confirm) connector.SetConnection(next, null); };
                connector.SetConnection(false, null);
                RectangleF rest = ConnectorProperty<RectangleF>(connector, "PlugBounds");
                RectangleF socket = ConnectorProperty<RectangleF>(connector, "SocketBounds");
                PointF anchor = ConnectorProperty<PointF>(connector, "CableAnchor");
                connector.SetConnection(true, null); RectangleF dock = ConnectorProperty<RectangleF>(connector, "PlugBounds");
                Check(ConnectorProperty<PointF>(connector, "CableAnchor") == anchor, label + ": connecting retains the fixed cable anchor.");
                connector.SetConnection(false, null);
                float travel = ConnectorAxis(dock, vertical) - ConnectorAxis(rest, vertical);
                Check(Math.Abs(travel) >= 8, label + ": the connector has distinct connected and disconnected grip positions.");
                Check(connector.ClientRectangle.Contains(Rectangle.Ceiling(rest)) && connector.ClientRectangle.Contains(Rectangle.Ceiling(socket)), label + ": actual grip and socket fit inside the control.");
                Check(ConnectorProperty<PointF>(connector, "CableAnchor") == anchor, label + ": confirmed state changes keep the cable anchored.");
                Check(connector.ControllerLabel == "2" && connector.ControllerName == "Synthetic controller" && connector.AccentColor == Color.FromArgb(55, 166, 207), label + ": name, controller number and accent remain assigned.");
                CaptureConnector(connector, artifacts, "disconnected");
                CheckInsertedMetal(connector, requests, artifacts, label);

                // The gesture starts at an off-center point on the actual painted
                // grip. Geometry must retain that pickup offset, with one axis only.
                Point start = ConnectorPoint(rest);
                Cursor grab = DragCursors.Grab, grabbing = DragCursors.Grabbing;
                ConnectorMouse(connector, "OnMouseMove", start, MouseButtons.None);
                Check(Object.ReferenceEquals(connector.Cursor, grab), label + ": hovering the USB grip uses the shared open hand.");
                ConnectorMouse(connector, "OnMouseMove", start, MouseButtons.None);
                Check(Object.ReferenceEquals(connector.Cursor, grab), label + ": repeated USB hover reuses the same cursor.");
                typeof(ControllerConnector).GetMethod("OnMouseLeave", Private).Invoke(connector, new object[] { EventArgs.Empty });
                Check(connector.Cursor == Cursors.Default, label + ": leaving the connector clears its hover cursor.");
                ConnectorMouse(connector, "OnMouseDown", start, MouseButtons.Left);
                Check(connector.Capture, label + ": dragging the grip captures its own mouse gesture.");
                Check(Object.ReferenceEquals(connector.Cursor, grabbing), label + ": pressing the USB grip immediately shows the closed hand.");
                int smallStep = Math.Sign(travel) * Math.Max(0, (vertical ? SystemInformation.DragSize.Height : SystemInformation.DragSize.Width) / 2 - 1);
                ConnectorMouse(connector, "OnMouseMove", ConnectorOffset(start, vertical, smallStep, 0), MouseButtons.Left);
                Check(Object.ReferenceEquals(connector.Cursor, grabbing), label + ": USB movement below the drag threshold retains the closed hand.");
                Check(ConnectorProperty<RectangleF>(connector, "PlugBounds") == rest, label + ": picking up the grip does not jump to the pointer center.");
                int step = Math.Sign(travel) * Math.Max(6, Math.Min(18, (int)Math.Abs(travel) / 3));
                Point moved = ConnectorOffset(start, vertical, step, 17);
                ConnectorMouse(connector, "OnMouseMove", moved, MouseButtons.Left);
                Check(Object.ReferenceEquals(connector.Cursor, grabbing), label + ": the moving USB plug uses the shared closed hand.");
                RectangleF drag = ConnectorProperty<RectangleF>(connector, "PlugBounds");
                Check(Math.Abs(ConnectorAxis(drag, vertical) - ConnectorAxis(rest, vertical) - step) <= 1, label + ": the real grip follows axial motion without changing its pickup offset.");
                Check(vertical ? drag.Left == rest.Left : drag.Top == rest.Top, label + ": perpendicular pointer motion does not move the grip.");
                Check(drag.Size == rest.Size && ConnectorProperty<PointF>(connector, "CableAnchor") == anchor, label + ": dragging retains grip size and the fixed cable anchor.");
                Check(requests.Count == 0, label + ": moving the plug never requests a connection before mouse release.");
                Check(!ConnectorProperty<bool>(connector, "SocketHighlighted"), label + ": the socket stays unlit before the grip enters snap range.");
                ConnectorEscape(connector);
                Check(!connector.Capture && ConnectorProperty<RectangleF>(connector, "PlugBounds") == rest && requests.Count == 0, label + ": Escape cancels and returns the grip to its confirmed position.");
                Check(connector.Cursor == Cursors.Default || Object.ReferenceEquals(connector.Cursor, grab), label + ": Escape restores the USB hover cursor.");
                ConnectorMouse(connector, "OnMouseMove", start, MouseButtons.None);
                Check(Object.ReferenceEquals(connector.Cursor, grab), label + ": the cancelled grip can be hovered again with the same open hand.");

                // A movement across the permitted axis must also suppress the
                // click fallback, even though the grip itself never moved.
                ConnectorMouse(connector, "OnMouseDown", start, MouseButtons.Left);
                Point across = ConnectorOffset(start, vertical, 0, 30);
                ConnectorMouse(connector, "OnMouseMove", across, MouseButtons.Left);
                Check(ConnectorProperty<RectangleF>(connector, "PlugBounds") == rest, label + ": a purely perpendicular gesture leaves the grip still.");
                ConnectorMouse(connector, "OnMouseUp", across, MouseButtons.Left);
                Check(requests.Count == 0, label + ": perpendicular movement cannot become an accidental toggle click.");

                Point cable = Point.Round(anchor);
                Check(!rest.Contains(cable) && !socket.Contains(cable), label + ": cable anchor is outside the two clickable objects.");
                ConnectorMouse(connector, "OnMouseMove", cable, MouseButtons.None);
                Check(connector.Cursor == Cursors.Default, label + ": the fixed cable does not offer a grab cursor.");
                ConnectorClick(connector, cable); ConnectorClick(connector, new Point(connector.Width - 1, connector.Height - 1));
                Check(requests.Count == 0, label + ": cable and background clicks produce no requests.");
                Point socketPoint = ConnectorExposedPoint(socket, rest, ConnectorTipBounds(connector, rest));
                ConnectorMouse(connector, "OnMouseMove", socketPoint, MouseButtons.None);
                Check(Object.ReferenceEquals(connector.Cursor, grab), label + ": the clickable socket uses the shared open hand.");
                ConnectorMouse(connector, "OnMouseDown", socketPoint, MouseButtons.Left);
                Point socketMove = ConnectorOffset(socketPoint, vertical, 25, 0);
                ConnectorMouse(connector, "OnMouseMove", socketMove, MouseButtons.Left);
                Check(ConnectorProperty<RectangleF>(connector, "PlugBounds") == rest, label + ": the socket cannot be picked up as a plug.");
                ConnectorMouse(connector, "OnMouseUp", socketMove, MouseButtons.Left);
                Check(requests.Count == 0, label + ": dragging from the socket produces no toggle request.");

                ConnectorClick(connector, start);
                Check(requests.Count == 1 && requests[0], label + ": a grip click requests connection once.");
                Check(ConnectorProperty<RectangleF>(connector, "PlugBounds") == rest, label + ": an unconfirmed click leaves the connector visibly disconnected.");
                requests.Clear(); ConnectorClick(connector, socketPoint);
                Check(requests.Count == 1 && requests[0], label + ": a socket click also requests connection once.");
                Check(ConnectorProperty<RectangleF>(connector, "PlugBounds") == rest, label + ": an unconfirmed socket click cannot fabricate connection state.");
                requests.Clear();
                RectangleF tip = ConnectorTipBounds(connector, rest);
                Point tipPickup = ConnectorExposedPoint(tip, rest);
                Check(!rest.Contains(tipPickup), label + ": the visible metal tip is independently reachable outside the grip.");
                ConnectorClick(connector, tipPickup);
                Check(requests.Count == 1 && requests[0], label + ": clicking the actual metal tip requests connection.");
                requests.Clear(); ConnectorMouse(connector, "OnMouseDown", tipPickup, MouseButtons.Left);
                ConnectorMouse(connector, "OnMouseMove", ConnectorOffset(tipPickup, vertical, step, 0), MouseButtons.Left);
                Check(Math.Abs(ConnectorAxis(ConnectorProperty<RectangleF>(connector, "PlugBounds"), vertical) - ConnectorAxis(rest, vertical) - step) <= 1, label + ": picking up the metal tip moves the real grip with its original offset.");
                ConnectorEscape(connector);
                Check(requests.Count == 0 && ConnectorProperty<RectangleF>(connector, "PlugBounds") == rest, label + ": a cancelled tip drag returns to confirmed state.");

                Point destination = ConnectorOffset(start, vertical, (int)Math.Round(travel), 0);
                ConnectorMouse(connector, "OnMouseDown", start, MouseButtons.Left); ConnectorMouse(connector, "OnMouseMove", destination, MouseButtons.Left);
                Check(ConnectorProperty<bool>(connector, "SocketHighlighted"), label + ": the socket glows while the grip is in its valid snap range.");
                Check(requests.Count == 0, label + ": entering snap range still waits for release.");
                CaptureConnector(connector, artifacts, "drag");
                ConnectorMouse(connector, "OnMouseUp", destination, MouseButtons.Left);
                Check(requests.Count == 1 && requests[0], label + ": releasing in snap range sends exactly one connect request.");
                Check(ConnectorProperty<RectangleF>(connector, "PlugBounds") == rest, label + ": a rejected drop snaps back to the confirmed disconnected position.");
                Check(connector.Cursor == Cursors.Default || Object.ReferenceEquals(connector.Cursor, grab), label + ": releasing a rejected drop restores the USB hover cursor.");
                requests.Clear(); confirm = true;
                ConnectorMouse(connector, "OnMouseDown", start, MouseButtons.Left); ConnectorMouse(connector, "OnMouseMove", destination, MouseButtons.Left); ConnectorMouse(connector, "OnMouseUp", destination, MouseButtons.Left);
                Check(requests.Count == 1 && requests[0] && ConnectorProperty<RectangleF>(connector, "PlugBounds") == dock, label + ": SetConnection confirms and retains a successful drop.");
                Check(Object.ReferenceEquals(connector.Cursor, grab), label + ": a successful drop restores the open hand over the connected grip.");
                CaptureConnector(connector, artifacts, "connected");
                requests.Clear(); Point dockStart = ConnectorPoint(dock), retract = ConnectorOffset(dockStart, vertical, -(int)Math.Round(travel), 0);
                ConnectorMouse(connector, "OnMouseDown", dockStart, MouseButtons.Left); ConnectorMouse(connector, "OnMouseMove", retract, MouseButtons.Left);
                Check(requests.Count == 0, label + ": retracting a connected grip waits for mouse release.");
                ConnectorMouse(connector, "OnMouseUp", retract, MouseButtons.Left);
                Check(requests.Count == 1 && !requests[0] && ConnectorProperty<RectangleF>(connector, "PlugBounds") == rest, label + ": a full retraction requests and confirms disconnection once.");
                requests.Clear(); connector.SetConnection(true, null); ConnectorClick(connector, ConnectorPoint(dock));
                Check(requests.Count == 1 && !requests[0] && ConnectorProperty<RectangleF>(connector, "PlugBounds") == rest, label + ": the connected grip click requests disconnection.");
                requests.Clear(); connector.SetConnection(true, null); ConnectorClick(connector, socketPoint);
                Check(requests.Count == 1 && !requests[0] && ConnectorProperty<RectangleF>(connector, "PlugBounds") == rest, label + ": the connected socket click requests disconnection.");
                confirm = false; requests.Clear();

                foreach (bool initial in new[] { false, true }) foreach (string outcome in new[] { "confirm", "unconfirmed", "throw" })
                {
                    connector.SetConnection(initial, null); requests.Clear(); connector.Focus();
                    RectangleF initialBounds = initial ? dock : rest, desiredBounds = initial ? rest : dock;
                    Point pickup = ConnectorPoint(initialBounds);
                    Point release = ConnectorOffset(pickup, vertical, (int)Math.Round(ConnectorAxis(desiredBounds, vertical) - ConnectorAxis(initialBounds, vertical)), 0);
                    string requestLabel = label + "/" + (initial ? "disconnect" : "connect") + "/" + outcome;
                    var captureReleasePositions = new List<RectangleF>(); bool watchRelease = false, failed = false;
                    var failure = new InvalidOperationException("Synthetic connection callback failure."); int observed = 0;
                    EventHandler captureReleased = delegate { if (watchRelease && !connector.Capture) captureReleasePositions.Add(ConnectorProperty<RectangleF>(connector, "PlugBounds")); };
                    observeRequest = delegate(bool next) {
                        observed++; if (observed > 1) return; // Bound a broken reentrant implementation without recursing indefinitely.
                        Check(next != initial && Field<bool>(connector, "connected") == initial, requestLabel + ": the callback begins with the previous real connection state.");
                        Check(ConnectorProperty<RectangleF>(connector, "PlugBounds") == desiredBounds, requestLabel + ": the plug is already at its requested position before backend confirmation.");
                        connector.Invalidate(); connector.Update(); Application.DoEvents();
                        Check(ConnectorProperty<RectangleF>(connector, "PlugBounds") == desiredBounds && Field<bool>(connector, "connected") == initial,
                            requestLabel + ": repainting during the callback retains the requested position without fabricating confirmation.");
                        Point hover = ConnectorPoint(desiredBounds);
                        ConnectorMouse(connector, "OnMouseMove", hover, MouseButtons.None);
                        ConnectorMouse(connector, "OnMouseDown", hover, MouseButtons.Left);
                        Check(!connector.Capture, requestLabel + ": reentrant mouse-down cannot acquire another drag while the request is pending.");
                        ConnectorMouse(connector, "OnMouseMove", ConnectorOffset(hover, vertical, 30, 0), MouseButtons.Left);
                        ConnectorMouse(connector, "OnMouseUp", hover, MouseButtons.Left);
                        Check(requests.Count == 1 && ConnectorProperty<RectangleF>(connector, "PlugBounds") == desiredBounds,
                            requestLabel + ": reentrant hover and drag input dispatch no second request and do not move the pending plug.");
                        if (outcome == "confirm")
                        {
                            connector.SetConnection(next, null); connector.Invalidate(); connector.Update(); Application.DoEvents();
                            Check(Field<bool>(connector, "connected") == next && ConnectorProperty<RectangleF>(connector, "PlugBounds") == desiredBounds,
                                requestLabel + ": real confirmation preserves the already-painted target without an intermediate return.");
                        }
                        else if (outcome == "throw") throw failure;
                    };
                    connector.MouseCaptureChanged += captureReleased;
                    try
                    {
                        ConnectorMouse(connector, "OnMouseDown", pickup, MouseButtons.Left);
                        ConnectorMouse(connector, "OnMouseMove", release, MouseButtons.Left); watchRelease = true;
                        try { ConnectorMouse(connector, "OnMouseUp", release, MouseButtons.Left); }
                        catch (InvalidOperationException error) { if (!Object.ReferenceEquals(error.InnerException, failure)) throw; failed = true; }
                    }
                    finally { observeRequest = null; connector.MouseCaptureChanged -= captureReleased; }
                    Check(observed == 1 && requests.Count == 1 && failed == (outcome == "throw"), requestLabel + ": release dispatches exactly one callback and preserves its outcome.");
                    Check(captureReleasePositions.Count > 0 && captureReleasePositions.All(position => position == desiredBounds),
                        requestLabel + ": releasing mouse capture already exposes the requested geometry, never the old rest position.");
                    bool expectedConnection = outcome == "confirm" ? !initial : initial;
                    Check(!connector.Capture && Field<bool>(connector, "connected") == expectedConnection &&
                        ConnectorProperty<RectangleF>(connector, "PlugBounds") == (expectedConnection ? dock : rest),
                        requestLabel + ": callback completion shows the confirmed position, rolling back when confirmation is absent or fails.");
                }
                connector.SetConnection(false, null); requests.Clear();

                {
                    connector.Enabled = true; connector.SetConnection(false, null); requests.Clear(); connector.Focus();
                    Point pickup = ConnectorPoint(rest), release = ConnectorOffset(pickup, vertical, (int)Math.Round(travel), 0);
                    ConnectorMouse(connector, "OnMouseDown", pickup, MouseButtons.Left);
                    ConnectorMouse(connector, "OnMouseMove", release, MouseButtons.Left);
                    bool disabled = false, wasPending = false;
                    Action disable = delegate {
                        if (disabled) return;
                        disabled = true; wasPending = Field<bool>(connector, "requestingConnection"); connector.Enabled = false;
                    };
                    EventHandler releaseHandler = delegate { if (!connector.Capture) disable(); };
                    connector.MouseCaptureChanged += releaseHandler;
                    try { ConnectorMouse(connector, "OnMouseUp", release, MouseButtons.Left); }
                    finally { connector.MouseCaptureChanged -= releaseHandler; }
                    Check(disabled && wasPending, label + ": the control is disabled reentrantly when the final request releases mouse capture.");
                    Check(requests.Count == 0 && !connector.Enabled && !connector.Capture && !Field<bool>(connector, "requestingConnection") &&
                        !Field<bool>(connector, "connected") && ConnectorProperty<RectangleF>(connector, "PlugBounds") == rest,
                        label + ": disabling during capture release prevents backend dispatch and clears all pending gesture state.");
                    connector.Enabled = true; ConnectorClick(connector, pickup);
                    Check(requests.Count == 1 && requests[0], label + ": a fresh click works after recovering from capture-release cancellation.");
                }
                requests.Clear();

                // Observe the two transition boundaries through actual releases,
                // without duplicating the control's pixel tolerance constants.
                double firstConnect = 2, lastDisconnect = -1;
                for (int i = 1; i < 20; i++) foreach (bool initial in new[] { false, true })
                {
                    ConnectorEscape(connector); connector.SetConnection(initial, null); requests.Clear();
                    RectangleF initialBounds = ConnectorProperty<RectangleF>(connector, "PlugBounds"); Point pickup = ConnectorPoint(initialBounds);
                    float targetAxis = ConnectorAxis(rest, vertical) + travel * i / 20f;
                    int delta = (int)Math.Round(targetAxis - ConnectorAxis(initialBounds, vertical));
                    if (Math.Abs(delta) < Math.Max(SystemInformation.DragSize.Width, SystemInformation.DragSize.Height) + 2) continue;
                    Point target = ConnectorOffset(pickup, vertical, delta, 0);
                    ConnectorMouse(connector, "OnMouseDown", pickup, MouseButtons.Left); ConnectorMouse(connector, "OnMouseMove", target, MouseButtons.Left); ConnectorMouse(connector, "OnMouseUp", target, MouseButtons.Left);
                    Check(requests.Count <= 1, label + ": each tolerance probe can request at most one transition.");
                    if (requests.Count == 1) { Check(requests[0] != initial, label + ": transition probes only request the opposite confirmed state."); if (initial) lastDisconnect = Math.Max(lastDisconnect, i / 20.0); else firstConnect = Math.Min(firstConnect, i / 20.0); }
                }
                Check(firstConnect <= 1 && lastDisconnect >= 0 && lastDisconnect < firstConnect, label + ": insertion and retraction have separate tolerances, preventing boundary chatter.");

                foreach (string cancel in new[] { "capture", "button-release", "disable", "hide", "resize", "focus" })
                {
                    ConnectorEscape(connector); connector.SetConnection(false, null); requests.Clear();
                    rest = ConnectorProperty<RectangleF>(connector, "PlugBounds"); start = ConnectorPoint(rest); connector.Focus();
                    ConnectorMouse(connector, "OnMouseDown", start, MouseButtons.Left); ConnectorMouse(connector, "OnMouseMove", ConnectorOffset(start, vertical, (int)Math.Round(travel), 0), MouseButtons.Left);
                    Check(Object.ReferenceEquals(connector.Cursor, grabbing), label + ": the gesture before " + cancel + " visibly holds the plug.");
                    if (cancel == "capture") connector.Capture = false;
                    else if (cancel == "button-release") ConnectorMouse(connector, "OnMouseMove", start, MouseButtons.None);
                    else if (cancel == "disable") connector.Enabled = false;
                    else if (cancel == "hide") connector.Visible = false;
                    else if (cancel == "resize") connector.Size = new Size(connector.Width + 1, connector.Height + 1);
                    else typeof(ControllerConnector).GetMethod("OnLostFocus", Private).Invoke(connector, new object[] { EventArgs.Empty });
                    ConnectorMouse(connector, "OnMouseUp", destination, MouseButtons.Left);
                    Check(requests.Count == 0 && !connector.Capture, label + ": " + cancel + " cancels without a connection request.");
                    Check(connector.Cursor == Cursors.Default || Object.ReferenceEquals(connector.Cursor, grab), label + ": " + cancel + " restores the USB hover cursor.");
                    if (cancel == "disable" || cancel == "hide") Check(connector.Cursor == Cursors.Default, label + ": an unavailable connector uses the default cursor after " + cancel + ".");
                    connector.Enabled = connector.Visible = true;
                    if (cancel == "resize") connector.Size = initialSize;
                    connector.SetConnection(false, null);
                    Check(ConnectorProperty<RectangleF>(connector, "PlugBounds") == rest, label + ": " + cancel + " restores the confirmed plug position.");
                    ConnectorMouse(connector, "OnMouseMove", start, MouseButtons.None);
                    Check(Object.ReferenceEquals(connector.Cursor, grab), label + ": hover after " + cancel + " reuses the shared open hand.");
                }
                confirm = true; CheckConnectorKeys(connector, requests, label); confirm = false;
                host.Close();
            }
            Console.WriteLine("CONNECTOR PASS: " + (assertions - started) + " assertions; actual horizontal and vertical controls, synthetic confirmations only.");
        }
        static void CheckControllerSelection(MainForm form)
        {
            Profile original = Current(form); string originalJson = Json(original);
            var runtime = Field<MultiControllerSession>(form, "runtime");
            var mainPicker = Field<ComboBox>(form, "mainControllerSlotPicker");
            var detailPicker = Field<ComboBox>(form, "controllerSlotPicker");
            DetailMode(form, "controller");
            Field<Button>(form, "addControllerSlot").PerformClick(); Pump(form);
            Check(mainPicker.Items.Count == 2 && detailPicker.Items.Count == 2, "Adding a controller updates both visible controller selectors.");
            CheckControllerFooter(form, "two controllers");
            var footerBeforeSelection = Field<Dictionary<string, ControllerConnector>>(form, "footerConnectors").ToDictionary(pair => pair.Key, pair => pair.Value);
            string addedId = runtime.SelectedControllerId;
            Check(addedId != ControllerRouting.DefaultControllerId && SelectedController(mainPicker) == addedId && SelectedController(detailPicker) == addedId, "A new controller is selected consistently in both places.");
            Check(Field<DataGridView>(form, "bindings").Rows.Count == 0, "New controller selection shows its own empty assignments.");
            SelectKeys(form, 14); Target(form, OutputTarget.B); Call(form, "AddBinding"); Pump(form);
            Check(Current(form).Bindings.Count(b => b.ControllerId == addedId && b.KeyIndex == 14 && b.Target == OutputTarget.B) == 1, "New key assignments follow the selected controller.");
            Profile colors = ControllerRouting.SetRgbColor(Current(form), ControllerRouting.DefaultControllerId, 0xA02040);
            colors = ControllerRouting.SetRgbColor(colors, addedId, 0x2080A0); Call(form, "Commit", colors); Pump(form);
            CheckControllerFooter(form, "controller colors");
            DetailMode(form, "controller");
            string beforeSelection = Json(Current(form));
            mainPicker.SelectedIndex = 0; Pump(form);
            Check(runtime.SelectedControllerId == ControllerRouting.DefaultControllerId && SelectedController(detailPicker) == runtime.SelectedControllerId, "The main selector updates the sidebar selector and active editing route.");
            Check(Field<Button>(form, "rgbColorButton").BackColor.ToArgb() == Color.FromArgb(0xA0, 0x20, 0x40).ToArgb(), "Sidebar color follows the first controller.");
            Check(Field<DataGridView>(form, "bindings").Rows.Count == original.Bindings.Count(b => b.KeyIndex == 14), "Selecting the first controller restores its key assignments.");
            detailPicker.SelectedIndex = 1; Pump(form);
            Check(runtime.SelectedControllerId == addedId && SelectedController(mainPicker) == addedId, "The sidebar selector updates the main selector and active editing route.");
            Check(Field<Button>(form, "rgbColorButton").BackColor.ToArgb() == Color.FromArgb(0x20, 0x80, 0xA0).ToArgb(), "Sidebar color follows the second controller.");
            Check(Field<DataGridView>(form, "bindings").Rows.Count == 1, "The second controller retains its independent assignment.");
            Equal(beforeSelection, Json(Current(form)), "Changing controller selection does not edit the profile.");
            foreach (var entry in footerBeforeSelection)
                Check(Object.ReferenceEquals(entry.Value, Field<Dictionary<string, ControllerConnector>>(form, "footerConnectors")[entry.Key]), "Changing colors or controller selection retains each footer control and its gestures.");
            var kindPicker = Field<ComboBox>(form, "controllerKindPicker"); kindPicker.SelectedIndex = 1; Pump(form);
            Check(Field<ControllerPreview>(form, "controllerPreview").Style == ControllerStyle.PlayStation5, "The controller kind selector updates the inline preview.");
            Check(ControllerRouting.EffectiveControllers(Current(form)).Single(c => c.Id == addedId).Kind == ControllerKind.DualSense, "Controller kind editing affects the selected controller.");
            AssertPassive(form);
            var keyboard = Field<VisualKeyboard>(form, "keyboard"); Rectangle keyboardBounds = Relative(form, keyboard);
            ShortKeyboardClick(keyboard, 9); Pump(form);
            Check(Field<string>(form, "detailsMode") == null && Field<Control>(form, "keyTitle").Visible, "Selecting a physical key opens its settings in the sidebar.");
            Check(Relative(form, keyboard) == keyboardBounds, "Contextual key selection leaves keyboard size and position unchanged.");
            foreach (string name in new[] { "pressureRange", "pressureScaleMaximum", "calibrateRange", "targets" })
                VisibleInside(form, Field<Control>(form, name), "normal-key-click/" + name);
            Check(!Field<Control>(form, "keyBehaviorPanel").Visible, "A short key click keeps the simple Keys view and leaves advanced pressure under Curve.");
            Field<Button>(form, "keyBehaviorToggle").PerformClick(); Pump(form);
            VisibleInside(form, Field<Control>(form, "captureOpposite"), "opposite-key-shortcut/capture");
            Check(Field<string>(form, "detailsMode") == "input" && Field<Control>(form, "keySocdPanel").Visible, "The opposite-key shortcut retains the unified Keys content and reveals its capture section.");
            Field<Button>(form, "advancedToggle").PerformClick(); Pump(form);
            Check(Field<string>(form, "detailsMode") == "advanced" && Field<Control>(form, "curve").Visible, "Curve action opens the corresponding sidebar context.");
            Field<Button>(form, "controllerToggle").PerformClick(); Pump(form);
            Check(Field<string>(form, "detailsMode") == "controller" && Field<Control>(form, "controllerPreview").Visible, "Controller action opens the inline controller context.");
            Call(form, "Commit", original); DetailMode(form, null); SelectKeys(form, 14);
            Equal(originalJson, Json(Current(form)), "Controller regression restores the original synthetic profile.");
            Check(mainPicker.Items.Count == 1 && detailPicker.Items.Count == 1 && runtime.SelectedControllerId == ControllerRouting.DefaultControllerId, "Removing the temporary controller restores a valid synchronized selection.");
            CheckControllerFooter(form, "restored controller profile");
        }
        static void RunControllerAssignmentVisuals(string artifacts)
        {
            string[] legends = { "D", "A", "W", "S", "L", "J", "I", "K", "Q", "E", "Space", "C", "R", "F", "Shift", "Ctrl", "Tab", "Esc", "Z", "X", "↑", "↓", "←", "→" };
            OutputTarget[] targets = Enum.GetValues(typeof(OutputTarget)).Cast<OutputTarget>().ToArray();
            var assigned = targets.Select((target, index) => new ControllerKeyAssignment(target, index, legends[index], legends[index], true)).ToArray();
            foreach (ControllerStyle style in new[] { ControllerStyle.Xbox, ControllerStyle.PlayStation5 })
            using (var host = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000), ClientSize = new Size(440, 295) })
            using (var controller = new ControllerPreview { Dock = DockStyle.Fill, CompactStatus = true, Style = style })
            {
                string label = style == ControllerStyle.Xbox ? "xbox" : "ps";
                host.Controls.Add(controller); controller.SetKeyAssignments(assigned); host.Show(); Application.DoEvents();
                using (var bitmap = new Bitmap(controller.Width, controller.Height))
                {
                    controller.DrawToBitmap(bitmap, controller.ClientRectangle);
                    bitmap.Save(Path.Combine(artifacts, "controller-all-keys-" + label + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                }
                Rectangle face = ControllerTargetBounds(controller, OutputTarget.A); Point? badgePickup = null;
                for (int y = face.Bottom + 1; y < Math.Min(controller.Height, face.Bottom + 35) && !badgePickup.HasValue; y++)
                {
                    for (int x = Math.Max(0, face.Left - 15); x < Math.Min(controller.Width, face.Right + 25) && !badgePickup.HasValue; x++)
                    {
                        Point point = new Point(x, y);
                        if (controller.HitTestTarget(point) == OutputTarget.A &&
                            new[] { new Point(x - 2, y), new Point(x + 2, y), new Point(x, y - 2), new Point(x, y + 2) }.All(near => controller.HitTestTarget(near) == OutputTarget.A))
                            badgePickup = point;
                    }
                }
                Check(badgePickup.HasValue, label + ": the visible key badge below the face button is an interactive part of its assigned target.");
                Point pickup = badgePickup.Value;
                controller.SetKeyAssignments(new ControllerKeyAssignment[0]);
                Check(controller.HitTestTarget(pickup) != OutputTarget.A, label + ": that external hit area belongs to the annotation, not an unrelated original control.");
                controller.SetKeyAssignments(assigned);
                Bitmap source;
                using (MappingDragVisual visual = CaptureControllerDragVisual(controller, OutputTarget.A, pickup, out source))
                using (source)
                {
                    Rectangle crop = new Rectangle(pickup.X - visual.Anchor.X, pickup.Y - visual.Anchor.Y, visual.Image.Width, visual.Image.Height);
                    Check(crop.Contains(pickup) && visual.Image.GetPixel(visual.Anchor.X, visual.Anchor.Y).A != 0,
                        label + ": picking up the external key badge keeps its visible pixels and original cursor anchor in the dragged picture.");
                    int opaqueBelowFace = 0;
                    for (int y = Math.Max(0, face.Bottom + 1 - crop.Top); y < visual.Image.Height; y++)
                        for (int x = 0; x < visual.Image.Width; x++) if (visual.Image.GetPixel(x, y).A > 200) opaqueBelowFace++;
                    Check(opaqueBelowFace > 10, label + ": the controller drag picture includes the key annotation below the original face shape.");
                    visual.Image.Save(Path.Combine(artifacts, "controller-assigned-drag-" + label + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                }
                var broadLabels = assigned.Select(item => item.Target == OutputTarget.Start || item.Target == OutputTarget.X
                    ? new ControllerKeyAssignment(item.Target, item.KeyIndex, "AltGr", "Right Alt", true) : item).ToArray();
                controller.SetKeyAssignments(broadLabels);
                foreach (OutputTarget target in style == ControllerStyle.Xbox ? new[] { OutputTarget.Start } : new[] { OutputTarget.X, OutputTarget.Start })
                {
                    Rectangle originalShape = ControllerTargetBounds(controller, target);
                    Point targetPickup = new Point(originalShape.Left + originalShape.Width / 2, originalShape.Top + originalShape.Height / 2);
                    Bitmap actualSource;
                    using (MappingDragVisual visual = CaptureControllerDragVisual(controller, target, targetPickup, out actualSource))
                    using (actualSource)
                    {
                        Point cropOrigin = new Point(targetPickup.X - visual.Anchor.X, targetPickup.Y - visual.Anchor.Y);
                        int ownBadgePixels = 0, otherTargetPixels = 0; var annotationPixels = new List<Point>();
                        for (int y = 0; y < visual.Image.Height; y++)
                            for (int x = 0; x < visual.Image.Width; x++)
                            {
                                Point client = new Point(cropOrigin.X + x, cropOrigin.Y + y);
                                if (originalShape.Contains(client) || visual.Image.GetPixel(x, y).A < 240) continue;
                                annotationPixels.Add(client);
                                OutputTarget? hit = controller.HitTestTarget(client);
                                if (hit == target) ownBadgePixels++;
                                else if (hit.HasValue) otherTargetPixels++;
                            }
                        Check(ownBadgePixels > 5, label + "/" + target + ": the broad external label is present in the actual drag image and remains independently clickable.");
                        Check(otherTargetPixels == 0, label + "/" + target + ": the external label and its drag mask do not cover a neighboring target or that target's label.");
                        controller.SetKeyAssignments(new ControllerKeyAssignment[0]);
                        int coveredControls = annotationPixels.Count(point => { OutputTarget? hit = controller.HitTestTarget(point); return hit.HasValue && hit != target; });
                        controller.SetKeyAssignments(broadLabels);
                        Check(coveredControls == 0, label + "/" + target + ": the broad key badge also leaves the original neighboring controls uncovered, regardless of hit-test priority.");
                    }
                }
                host.Close();
            }
        }
        static void CheckControllerKeyAssignments(MainForm form, string artifacts)
        {
            int started = assertions;
            Profile original = Current(form); string originalJson = Json(original), language = UiText.Language;
            var originalKeymap = Field<Tk75.Diagnostics.KeyMapDocument>(form, "keymap");
            int? originalLegend = Field<int?>(form, "legendOverride");
            ComboBox layoutPicker = Field<ComboBox>(form, "layoutMode"); int originalLayout = layoutPicker.SelectedIndex;
            var controller = Field<ControllerPreview>(form, "controllerPreview");
            try
            {
                Call(form, "SwitchLanguage", "en"); layoutPicker.SelectedIndex = 2; Call(form, "SetLegendOverride", (int?)0);
                var learned = Tk75.Diagnostics.KeyMapStore.SetLabel(originalKeymap, 200, "Benutzername");
                typeof(MainForm).GetField("keymap", Private).SetValue(form, learned);
                var fixture = new Profile { Name = "Controller key labels" };
                fixture = ControllerRouting.Add(fixture, "labels-other", "Other player", ControllerKind.DualSense);
                foreach (int index in new[] { 9, 14 }) fixture.Bindings.Add(new Binding { KeyIndex = index, Target = OutputTarget.A });
                fixture.Bindings.Add(new Binding { KeyIndex = 14, Target = OutputTarget.RightTrigger, Enabled = false });
                fixture.Bindings.Add(new Binding { KeyIndex = 38, Target = OutputTarget.X });
                fixture.Bindings.Add(new Binding { KeyIndex = 4, Target = OutputTarget.LB });
                fixture.Bindings.Add(new Binding { KeyIndex = 76, Target = OutputTarget.LB });
                fixture.Bindings.Add(new Binding { KeyIndex = 200, Target = OutputTarget.B });
                fixture.Bindings.Add(new Binding { KeyIndex = 201, Target = OutputTarget.Y });
                fixture.Bindings.Add(new Binding { KeyIndex = 80, Target = OutputTarget.Start });
                fixture.Bindings.Add(new Binding { KeyIndex = 14, Target = OutputTarget.LeftYPositive });
                fixture.Bindings.Add(new Binding { KeyIndex = 9, Target = OutputTarget.LeftXNegative });
                fixture.Bindings.Add(new Binding { KeyIndex = 15, Target = OutputTarget.LeftYNegative });
                fixture.Bindings.Add(new Binding { KeyIndex = 21, Target = OutputTarget.LeftXPositive });
                fixture.Bindings.Add(new Binding { KeyIndex = 9, Target = OutputTarget.B, ControllerId = "labels-other" });
                Call(form, "Commit", fixture); SelectKeys(form, 15); DetailMode(form, "controller");
                Equal("A, W", controller.GetAssignedKeyText(OutputTarget.A), "Controller shows every assigned key, including keys outside the current selection.");
                Equal("W (disabled)", controller.GetAssignedKeyText(OutputTarget.RightTrigger), "Disabled mappings remain readable on the controller.");
                Equal("Left Shift, Right Shift", controller.GetAssignedKeyText(OutputTarget.LB), "The full assignment description distinguishes both physical Shift keys.");
                Equal("Benutzername", controller.GetAssignedKeyText(OutputTarget.B), "Unknown layout indices retain learned user names.");
                Equal("Index 201", controller.GetAssignedKeyText(OutputTarget.Y), "An unnamed unknown index has a readable fallback.");
                Equal("Y", controller.GetAssignedKeyText(OutputTarget.X), "QWERTY uses the actual Y legend.");
                Equal("Enter", controller.GetAssignedKeyText(OutputTarget.Start), "The ISO matrix identifies physical index 80 as Enter.");
                SelectKeys(form, 9); Equal("A, W", controller.GetAssignedKeyText(OutputTarget.A), "Changing the selected key does not filter the controller's assignments.");
                var slots = Field<ComboBox>(form, "mainControllerSlotPicker"); slots.SelectedIndex = 1; Pump(form);
                Equal("A", controller.GetAssignedKeyText(OutputTarget.B), "Selecting another controller displays only that controller's assigned key.");
                Equal("", controller.GetAssignedKeyText(OutputTarget.A), "Other players' assignments do not leak onto an unused target.");
                Equal("", controller.GetAssignedKeyText(OutputTarget.LeftYPositive), "Controller switching clears unassigned stick directions too.");
                slots.SelectedIndex = 0; Pump(form);
                Equal("A, W", controller.GetAssignedKeyText(OutputTarget.A), "Returning to the first controller restores its complete assignments.");
                SelectKeys(form, 14);
                string triggerId = Current(form).Bindings.Single(binding => binding.Target == OutputTarget.RightTrigger).BindingId;
                SelectBindings(form, triggerId); Call(form, "ToggleBindings");
                Equal("W", controller.GetAssignedKeyText(OutputTarget.RightTrigger), "Enabling a mapping immediately removes its disabled indication.");
                Call(form, "Undo"); Equal("W (disabled)", controller.GetAssignedKeyText(OutputTarget.RightTrigger), "Undo immediately restores a disabled mapping's visible state.");
                SelectBindings(form, triggerId); Call(form, "RemoveBinding");
                Equal("", controller.GetAssignedKeyText(OutputTarget.RightTrigger), "Removing the last mapping clears its controller annotation.");
                Call(form, "Undo"); Equal("W (disabled)", controller.GetAssignedKeyText(OutputTarget.RightTrigger), "Undo restores the removed controller annotation.");
                Call(form, "Redo"); Equal("", controller.GetAssignedKeyText(OutputTarget.RightTrigger), "Redo clears the annotation again.");
                Call(form, "Undo");
                string beforeLegends = Json(Current(form));
                Call(form, "SetLegendOverride", (int?)1);
                Equal("Z", controller.GetAssignedKeyText(OutputTarget.X), "QWERTZ immediately changes the same physical key's controller legend to Z.");
                Call(form, "SwitchLanguage", "de");
                Equal("Shift links, Shift rechts", controller.GetAssignedKeyText(OutputTarget.LB), "Language changes refresh the full modifier descriptions.");
                Equal("W (inaktiv)", controller.GetAssignedKeyText(OutputTarget.RightTrigger), "Language changes refresh disabled mapping descriptions.");
                Equal("Benutzername", controller.GetAssignedKeyText(OutputTarget.B), "Language changes preserve learned user text.");
                Call(form, "SetLegendOverride", (int?)0); layoutPicker.SelectedIndex = 1; Pump(form);
                Equal("\\", controller.GetAssignedKeyText(OutputTarget.Start), "Changing to ANSI reinterprets physical index 80 with the displayed matrix.");
                layoutPicker.SelectedIndex = 2; Pump(form);
                Equal("Enter", controller.GetAssignedKeyText(OutputTarget.Start), "Returning to ISO restores its physical Enter label.");
                Equal(beforeLegends, Json(Current(form)), "Layout and language presentation changes never edit mappings.");
                learned = Tk75.Diagnostics.KeyMapStore.SetLabel(learned, 200, "Renamed key");
                typeof(MainForm).GetField("keymap", Private).SetValue(form, learned); Call(form, "RefreshKeys");
                Equal("Renamed key", controller.GetAssignedKeyText(OutputTarget.B), "Refreshing a newly learned label updates its controller annotation.");
                Call(form, "SwitchLanguage", "en");
                Profile picture = Current(form);
                picture.Bindings.RemoveAll(binding => binding.KeyIndex == 200 || binding.KeyIndex == 201);
                picture.Bindings.Add(new Binding { KeyIndex = 41, Target = OutputTarget.B });
                picture.Bindings.Add(new Binding { KeyIndex = 10, Target = OutputTarget.Y });
                picture.Bindings.Add(new Binding { KeyIndex = 2, Target = OutputTarget.Back });
                Call(form, "Commit", picture); DetailMode(form, "controller");
                foreach (int kind in new[] { 0, 1 })
                {
                    Field<ComboBox>(form, "controllerKindPicker").SelectedIndex = kind; Pump(form);
                    using (var bitmap = new Bitmap(controller.Width, controller.Height))
                    {
                        controller.DrawToBitmap(bitmap, controller.ClientRectangle);
                        bitmap.Save(Path.Combine(artifacts, "controller-key-assignments-" + (kind == 0 ? "xbox" : "ps") + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                    }
                    Equal("W", controller.GetAssignedKeyText(OutputTarget.LeftYPositive), "Both controller styles retain the keyboard letter on the up direction.");
                }
                RunControllerAssignmentVisuals(artifacts);
                AssertPassive(form);
            }
            finally
            {
                typeof(MainForm).GetField("keymap", Private).SetValue(form, originalKeymap);
                layoutPicker.SelectedIndex = originalLayout; Call(form, "SetLegendOverride", originalLegend);
                Call(form, "Commit", original); Call(form, "SwitchLanguage", language); SelectKeys(form, 14); DetailMode(form, null);
            }
            Equal(originalJson, Json(Current(form)), "Controller-label checks restore the original synthetic profile.");
            Console.WriteLine("CONTROLLER KEY ASSIGNMENTS PASS: " + (assertions - started) + " assertions; actual selection, edits, undo/redo, layout and language events; no hardware.");
        }
        static void Run(MainForm form, string data, string artifacts)
        {
            Check(form.ClientSize == DefaultClientSize, "Offscreen preview retains the exact default-size layout test surface.");
            form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-30000, -30000);
            form.Show(); Pump(form); AssertPassive(form);
            CheckDetailTabNavigation(form); CheckMappingDragFeedback(form);
            DataGridView keys = Field<DataGridView>(form, "keys");
            Check(keys.Rows.Count == 256, "All physical index rows remain stable.");
            foreach (DataGridViewColumn column in keys.Columns) Check(column.SortMode == DataGridViewColumnSortMode.NotSortable, "Key sorting cannot corrupt row identity.");
            for (int i = 0; i < 256; i++) Check((int)keys.Rows[i].Cells[0].Value == i, "Physical row " + i);
            Equal("W", Convert.ToString(keys.Rows[14].Cells[1].Value), "W is index 14.");
            Equal("A", Convert.ToString(keys.Rows[9].Cells[1].Value), "A is index 9.");
            var keyboard = Field<VisualKeyboard>(form, "keyboard");
            Check(keyboard.LayoutModel.FindByCode("Enter").KeyIndex == 80, "User-confirmed ISO layout is the initial template.");
            Call(form, "ApplyDetectedModel", (uint?)3590);
            Check(keyboard.LayoutModel.FindByCode("Enter").KeyIndex == 81, "Confirmed manufacturer ANSI identity selects its matching matrix.");
            Call(form, "ApplyDetectedModel", (uint?)3591);
            Check(keyboard.LayoutModel.FindByCode("Enter").KeyIndex == 80, "Confirmed manufacturer ISO identity selects its matching matrix.");
            Field<ComboBox>(form, "layoutMode").SelectedIndex = 1;
            Call(form, "ApplyDetectedModel", (uint?)3591);
            Check(keyboard.LayoutModel.FindByCode("Enter").KeyIndex == 81, "A manual layout correction is not overwritten by identity updates.");
            Field<ComboBox>(form, "layoutMode").SelectedIndex = 0;
            Check(keyboard.LayoutModel.FindByCode("Enter").KeyIndex == 80, "Returning to automatic restores the detected layout.");
            Field<ComboBox>(form, "layoutMode").SelectedIndex = 1;
            Call(form, "ConfigureKeyboardForDevice", new Tk75.Diagnostics.CollectionInfo { vendorId = 1, productId = 2, product = "Unknown keyboard" });
            Check(keyboard.LayoutModel.FindByCode("Enter").KeyIndex == 81, "Explicit manual template survives a device change.");
            Field<ComboBox>(form, "layoutMode").SelectedIndex = 0;
            Check(keyboard.LayoutModel == null, "Returning to automatic does not guess a TK75 layout for an unknown device.");
            Call(form, "ConfigureKeyboardForDevice", new Tk75.Diagnostics.CollectionInfo { vendorId = 0x3151, productId = 0x5030, product = "TK75 TMR" });
            Equal("ISO · voreingestellt", (string)Field<ComboBox>(form, "layoutMode").Items[0], "Preset is distinguished from a real identity response.");
            Call(form, "ApplyDetectedModel", (uint?)3591);
            keyboard.SelectKey(keyboard.LayoutModel.FindByIndex(9), false); Pump(form);
            Check(((int[])Call(form, "SelectedKeys")).SequenceEqual(new[] { 9 }), "Clicking a physical key selects its manufacturer index.");
            Equal("Taste A", Field<Label>(form, "keyTitle").Text, "Key card follows direct physical selection.");
            keyboard.SelectKey(keyboard.LayoutModel.FindByIndex(14), true); Pump(form);
            Check(((int[])Call(form, "SelectedKeys")).SequenceEqual(new[] { 9, 14 }), "Ctrl-selection preserves multiple keyboard indices.");
            Check(!Field<Button>(form, "calibrateRange").Enabled, "Calibration needs active pressure input.");
            SelectKeys(form, 14);
            Binding[] w = Current(form).Bindings.Where(b => b.KeyIndex == 14).ToArray(); Check(w.Length == 2, "Preview includes two independent W bindings.");
            SelectBindings(form, w.Select(b => b.BindingId).ToArray());
            Equal("Gemischt", PropertyText(form, "TopDeadzone"), "Different deadzones show mixed state.");
            Check(Field<CurveCanvas>(form, "curve").Mixed, "Different curves show mixed state.");
            SelectBindings(form, w[0].BindingId);
            Check(PropertyText(form, "TopDeadzone") != "Gemischt", "Single binding shows its own value.");
            Profile expected = Current(form); expected.Bindings.Single(b => b.BindingId == w[0].BindingId).Processing.TopDeadzone = 0.12;
            Edit(form, "TopDeadzone", "0,12");
            Equal(Json(expected), Json(Current(form)), "Single-field edit changes no other field or mapping.");
            string afterSingle = Json(Current(form));
            SelectBindings(form, w.Select(b => b.BindingId).ToArray());
            Equal("Gemischt", PropertyText(form, "TopDeadzone"), "Mixed state survives isolated edit.");
            expected = Current(form); foreach (Binding b in expected.Bindings) b.Processing.Scale = 0.6;
            Edit(form, "Scale", "0.6"); Equal(Json(expected), Json(Current(form)), "Selected bindings receive only edited scale.");
            string afterBulk = Json(Current(form));
            Call(form, "Undo"); Pump(form); Equal(afterSingle, Json(Current(form)), "UI Undo restores whole bulk edit.");
            Call(form, "Redo"); Pump(form); Equal(afterBulk, Json(Current(form)), "UI Redo preserves original IDs.");
            SelectKeys(form, 14); Target(form, OutputTarget.A); Call(form, "AddBinding"); Pump(form);
            Profile added = Current(form); Binding[] addedW = added.Bindings.Where(b => b.KeyIndex == 14).ToArray();
            Check(addedW.Length == 3 && addedW.Count(b => b.Target == OutputTarget.A) == 1, "Add target appends instead of overwriting.");
            foreach (Binding prior in expected.Bindings)
            {
                Binding found = added.Bindings.Single(b => b.BindingId == prior.BindingId);
                Check(found.Target == prior.Target && found.Processing.Scale == prior.Processing.Scale && found.Processing.TopDeadzone == prior.Processing.TopDeadzone, "Adding preserves previous binding.");
            }
            string afterAdd = Json(added);
            Call(form, "Undo"); Pump(form); Equal(afterBulk, Json(Current(form)), "Add target is one undo step.");
            Call(form, "Redo"); Pump(form); Equal(afterAdd, Json(Current(form)), "Redo keeps added target ID.");
            SelectKeys(form, 14); Call(form, "CopySettings");
            List<Binding> clipboard = Field<List<Binding>>(form, "copied"); Check(clipboard.Count == 3, "UI Copy includes all source bindings.");
            SelectKeys(form, 9); Target(form, OutputTarget.RB); Call(form, "AddBinding"); Pump(form);
            string oldAId = Current(form).Bindings.Single(b => b.KeyIndex == 9).BindingId;
            string beforePaste = Json(Current(form));
            Field<ComboBox>(form, "pasteMode").SelectedIndex = 1; // Actual "Alles" UI mode.
            Call(form, "PasteSettings"); Pump(form);
            Profile pasted = Current(form); Binding[] a = pasted.Bindings.Where(b => b.KeyIndex == 9).ToArray();
            Check(a.Length == 3 && !pasted.Bindings.Any(b => b.BindingId == oldAId), "All-paste replaces old A mapping with full W list.");
            for (int i = 0; i < a.Length; i++)
            {
                Check(a[i].Target == clipboard[i].Target && a[i].Enabled == clipboard[i].Enabled && a[i].Processing.TopDeadzone == clipboard[i].Processing.TopDeadzone, "Paste retains target, enabled and processing.");
                Check(a[i].BindingId != clipboard[i].BindingId, "UI Paste uses fresh IDs.");
            }
            Check(pasted.Bindings.Select(b => b.BindingId).Distinct().Count() == pasted.Bindings.Count, "IDs unique across physical keys.");
            string afterPaste = Json(pasted);
            Call(form, "Undo"); Pump(form); Equal(beforePaste, Json(Current(form)), "UI Undo restores overwritten destination mapping and IDs.");
            Call(form, "Redo"); Pump(form); Equal(afterPaste, Json(Current(form)), "UI Redo preserves all newly pasted binding IDs.");
            SelectKeys(form, 9, 14); Check(Field<DataGridView>(form, "bindings").Rows.Count == 6, "Multi-key view includes six independent bindings.");
            SelectBindings(form, Current(form).Bindings.Select(b => b.BindingId).ToArray()); Edit(form, "OutputDeadzone", "0.1");
            Check(Current(form).Bindings.All(b => b.Processing.OutputDeadzone == 0.1), "Actual editor applies one property across physical keys.");
            Call(form, "SaveProfile"); string savedPath = Field<string>(form, "profilePath");
            string allowed = Path.GetFullPath(data).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Check(Path.GetFullPath(savedPath).StartsWith(allowed, StringComparison.OrdinalIgnoreCase), "Save stays inside synthetic workspace.");
            string saved = File.ReadAllText(savedPath);
            Equal(Json(Current(form)), Json(ProfileJson.Deserialize(saved)), "UI Save retains validated current profile.");
            Check(!saved.Contains("\"Calibration\"") && !saved.Contains("\"Rest\"") && !saved.Contains("\"Bottom\"") && !saved.Contains("\"MeasuredTravel\""), "Profile excludes hardware calibration.");
            Check(Directory.GetFiles(data, "calibration-*.json", SearchOption.AllDirectories).Length == 0, "UI edits create no calibration file.");
            Console.WriteLine("BEHAVIOR PASS: " + assertions + " assertions; real selection/edit/add/undo/redo/copy/paste/save paths.");
            SelectKeys(form, 14);
            CheckControllerSelection(form);
            CheckControllerKeyAssignments(form, artifacts);
            CheckMultiKeySignalScope(form);
            CheckCurveSettingsSliders(form, artifacts);
            CheckInputThresholdSliders(form);
            CheckMultiKeyInputScope(form);
            CheckManyControllerFooter(form, artifacts);
            CheckLayout(form, new Size(1440, 880), artifacts); CheckLayout(form, new Size(1080, 740), artifacts);
            Size chrome = new Size(form.Width - form.ClientSize.Width, form.Height - form.ClientSize.Height);
            CheckLayout(form, new Size(SupportedMinimumSize.Width - chrome.Width, SupportedMinimumSize.Height - chrome.Height), artifacts);
            AssertPassive(form);
            foreach (string failure in layoutFailures) Console.Error.WriteLine("LAYOUT FAILURE: " + failure);
            Check(layoutFailures.Count == 0, "Critical controls visible without overlap at the default, compact and true minimum window sizes (see layout diagnostics).");
        }
        static void RunEnglishContexts(MainForm form, string data, string artifacts)
        {
            string original = Json(Current(form));
            Call(form, "SwitchLanguage", "en"); Pump(form);
            Equal("en", UiText.Language, "The actual language action switches the application to English.");
            Equal("en", UiPreferences.LoadLanguage(data), "The selected English preference is retained.");
            CheckDetailTabNavigation(form);
            Field<ComboBox>(form, "layoutMode").SelectedIndex = 1; Pump(form);
            Equal("Your keyboard", Field<Label>(form, "keyboardTitle").Text, "Changing keyboard layout retains the English dynamic heading.");
            Field<ComboBox>(form, "layoutMode").SelectedIndex = 0; Pump(form);
            SelectKeys(form, 14); DetailMode(form, null);
            Equal("Key W", Field<Label>(form, "keyTitle").Text, "The selected key card uses English after switching language.");
            Equal("All off", Field<Button>(form, "allOffButton").Text, "The persistent footer action uses English.");
            SelectBindings(form, Current(form).Bindings.Where(binding => binding.KeyIndex == 14 && binding.ControllerId == ControllerRouting.DefaultControllerId).Select(binding => binding.BindingId).ToArray());
            DetailMode(form, "advanced");
            Equal("Mixed", Convert.ToString(PropertyRow(form, "TopDeadzone").Cells[1].FormattedValue), "Mixed numerical values are displayed in English.");
            string beforeMixed = Json(Current(form));
            Edit(form, "TopDeadzone", "Mixed");
            Equal(beforeMixed, Json(Current(form)), "Leaving an unchanged localized mixed value never edits the underlying profile.");
            SelectKeys(form, 9, 14);
            string invalidNumber = null;
            try { Edit(form, "Scale", "not-a-number"); }
            catch (InvalidOperationException error)
            { if (error.InnerException == null) throw; invalidNumber = UiText.Get(error.InnerException.Message); }
            Check(invalidNumber != null && invalidNumber.StartsWith("Enter a number.", StringComparison.Ordinal) && !invalidNumber.Contains("Parametername"), "The actual multi-key numerical edit reports a fully English validation error.");
            Equal(beforeMixed, Json(Current(form)), "An invalid multi-key numerical edit preserves all profile values.");
            string imported = Path.Combine(artifacts, "invalid-import.json"), invalidImport = null;
            Check(beforeMixed.Contains("\"Version\":1"), "The invalid-import fixture starts from the real supported profile format.");
            File.WriteAllText(imported, beforeMixed.Replace("\"Version\":1", "\"Version\":999"));
            try { Call(form, "ReadProfile", imported); }
            catch (InvalidOperationException error)
            { if (error.InnerException == null) throw; invalidImport = UiText.Get(error.InnerException.Message); }
            Check(invalidImport != null && invalidImport.StartsWith("Unsupported profile version.", StringComparison.Ordinal) && !invalidImport.Contains("Parametername"), "Reading an invalid imported profile reports a fully English validation error.");
            Equal(beforeMixed, Json(Current(form)), "An invalid import cannot replace the current profile.");
            SelectKeys(form, 14);
            string englishArtifacts = Path.Combine(artifacts, "english"); Directory.CreateDirectory(englishArtifacts);
            CheckLayout(form, new Size(1440, 880), englishArtifacts);
            Size chrome = new Size(form.Width - form.ClientSize.Width, form.Height - form.ClientSize.Height);
            CheckLayout(form, new Size(SupportedMinimumSize.Width - chrome.Width, SupportedMinimumSize.Height - chrome.Height), englishArtifacts);
            Equal(original, Json(Current(form)), "Language changes preserve user profile values and names.");
            AssertPassive(form);
            foreach (string failure in layoutFailures) Console.Error.WriteLine("LAYOUT FAILURE: " + failure);
            Check(layoutFailures.Count == 0, "English controls remain usable in every main context at the default and minimum sizes.");
        }
        [STAThread]
        public static int Main(string[] args)
        {
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
            Console.SetError(new StreamWriter(Console.OpenStandardError(), new System.Text.UTF8Encoding(false)) { AutoFlush = true });
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            MainForm form = null;
            try
            {
                if (args.Length != 1) throw new ArgumentException("One dedicated artifact directory required.");
                string artifacts = Path.GetFullPath(args[0]); Directory.CreateDirectory(artifacts); string data = Path.Combine(artifacts, "synthetic-data");
                Equal("en", UiText.Language, "English is the initial display language.");
                Equal("en", UiPreferences.LoadLanguage(data), "A fresh workspace defaults to English.");
                Equal("Key 42: Calibration is missing.", UiText.Get("Taste 42: Kalibrierung fehlt."), "English translates both the key prefix and its dynamic status detail.");
                form = new MainForm(data, true);
                Equal("en", UiText.Language, "A new application keeps the default English preference.");
                form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-30000, -30000);
                form.Show(); Pump(form); AssertPassive(form);
                CheckDefaultWindowGeometry(form);
                // This preview alone needs room for the fixed layout cases. Keep
                // real application sizing and the machine's display settings intact.
                form.MaximumSize = new Size(2048, 2048);
                SetPreviewClientSize(form, DefaultClientSize);
                Check(form.ClientSize == DefaultClientSize, "The offscreen preview reaches the exact default client size before UI interaction.");
                Equal("Key W", Field<Label>(form, "keyTitle").Text, "The first visible key card starts in English without a language toggle.");
                CapturePreview(form, artifacts, "initial-english");
                Call(form, "SwitchLanguage", "de");
                Equal("de", UiPreferences.LoadLanguage(data), "Explicit German selection remains supported and persists.");
                CheckPressureRangeSliderContract(); RunConnectorGestures(artifacts); RunMarqueeSelection(artifacts); RunKeyDragVisuals(artifacts); RunControllerDragVisuals(artifacts); Run(form, data, artifacts); RunDialogs(artifacts);
                RunEnglishContexts(form, data, artifacts);
                RunKeyboardTabClicks(form);
                RunControllerModifierUi(artifacts);
                RunNativeThemeControls(artifacts);
                CheckCurveShapePicker(form, artifacts);
                RunCurveDynamicsUi(form, artifacts);
                RunCurveRangeRailUi(form, artifacts);
                RunInputThresholdCapture(artifacts);
                RunKeyAnnotations(form, artifacts);
                CheckMappingSummaries(form, artifacts);
                RunSocdDragUi(form, artifacts);
                Console.WriteLine("PASS: " + assertions + " assertions; actual MainForm preview, synthetic data, no hardware/controller.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.ToString()); return 1; }
            finally { if (form != null) form.Dispose(); } // No modal FormClosing path.
        }
    }
}
