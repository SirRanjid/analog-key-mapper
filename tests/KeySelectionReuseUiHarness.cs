using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Tk75.App;
using Tk75.Mapping;
using Binding = Tk75.Mapping.Binding;

namespace Tk75.Tests
{
    public static partial class AppUiHarness
    {
        static IEnumerable<Control> ReuseControls(Control root)
        {
            yield return root;
            foreach (Control child in root.Controls)
                foreach (Control descendant in ReuseControls(child)) yield return descendant;
        }
        static object ReuseOppositeChoice(ComboBox picker, int index)
        { return picker.Items.Cast<object>().Single(item => (int?)item.GetType().GetField("Index").GetValue(item) == index); }
        static void CheckReuseRows(DataGridView grid, DataGridViewRow[] rows, DataGridViewCell[] cells, string label)
        {
            Check(rows.SequenceEqual(grid.Rows.Cast<DataGridViewRow>()), label + ": row instances survive refresh.");
            Check(cells.SequenceEqual(grid.Rows.Cast<DataGridViewRow>().SelectMany(row => row.Cells.Cast<DataGridViewCell>())), label + ": cell instances survive refresh.");
        }
        static DataGridViewCell[] ReuseCells(DataGridView grid)
        { return grid.Rows.Cast<DataGridViewRow>().SelectMany(row => row.Cells.Cast<DataGridViewCell>()).ToArray(); }
        static void CheckReuseKeyData(MainForm form, int key, string controller)
        {
            Binding[] expected = Current(form).Bindings.Where(binding => binding.ControllerId == controller && binding.KeyIndex == key).ToArray();
            var grid = Field<DataGridView>(form, "bindings");
            Check(((int[])Call(form, "SelectedKeys")).SequenceEqual(new[] { key }), "The reused view selects only physical key " + key + ".");
            Equal("Key " + (string)Call(form, "Label", key), Field<Label>(form, "keyTitle").Text, "The reused title names the newly selected key.");
            Check(grid.Rows.Count == expected.Length, "The reused grid has precisely the new key's mapping count.");
            for (int i = 0; i < expected.Length; i++)
            {
                DataGridViewRow row = grid.Rows[i]; Binding binding = expected[i];
                Equal(binding.BindingId, (string)row.Tag, "The reused row owns the new binding ID before later edits.");
                Equal((string)Call(form, "TargetLabel", binding.Target), Convert.ToString(row.Cells[1].Value), "A reused target cell displays the new output.");
                Equal(binding.Enabled ? "Yes" : "No", Convert.ToString(row.Cells[2].Value), "A reused state cell displays the new enabled value.");
                Equal("—", Convert.ToString(row.Cells[3].Value), "A rebound row cannot display the old key's live value.");
            }
            Check(new HashSet<string>((string[])Call(form, "SelectedBindings")).SetEquals(expected.Select(binding => binding.BindingId)),
                "Selection follows the newly bound IDs without retaining the old key's mapping IDs.");
            KeyInputSettings input = KeyInputEditing.GetOrDefault(Current(form), key);
            Check(Field<NumericUpDown>(form, "actuationPoint").Value == (decimal)input.ActuationPoint * 100M,
                "The persistent input editor displays the new key's activation value.");
            object opposite = Field<ComboBox>(form, "oppositeKey").SelectedItem;
            Check(opposite != null && (int?)opposite.GetType().GetField("Index").GetValue(opposite) == input.OppositeKeyIndex,
                "The persistent opposite-key selector displays the new key's pairing.");
            var canvas = Field<CurveCanvas>(form, "curve");
            if (expected.Length == 0)
            {
                Check(canvas.Settings == null && !Field<ComboBox>(form, "curveShape").Enabled,
                    "An unmapped key clears the prior response and disables its shape editor.");
                Check(Field<DataGridView>(form, "settings").Rows.Cast<DataGridViewRow>().All(row => Convert.ToString(row.Cells[1].Value) == "—"),
                    "An unmapped key clears every reused response value.");
            }
            else
            {
                Check(canvas.Settings != null && Math.Abs(canvas.Settings.Scale - expected[0].Processing.Scale) < 1e-10,
                    "The persistent curve renders the newly selected response.");
                Equal(expected[0].Processing.Curve.ToString(), ShapeChoiceValue(Field<ComboBox>(form, "curveShape").SelectedItem),
                    "The persistent curve selector chooses the new shape.");
            }
            Check(!Field<bool>(form, "inputDirty"), "Rebinding input controls does not create a draft.");
        }
        static void RunKeySelectionReuse(MainForm form, string artifacts)
        {
            int started = assertions;
            string controller = Field<MultiControllerSession>(form, "runtime").SelectedControllerId;
            Profile fixture = ControllerRouting.Add(Current(form), "reuse-other", "Unchanged controller", ControllerKind.Xbox360);
            fixture.Bindings = new List<Binding> {
                new Binding { KeyIndex = 14, ControllerId = controller, Target = OutputTarget.LeftYPositive, Processing = new SignalSettings { Curve = CurveKind.Exponential, Scale = .42 } },
                new Binding { KeyIndex = 14, ControllerId = controller, Target = OutputTarget.RightTrigger, Enabled = false, Processing = new SignalSettings { Curve = CurveKind.Exponential, Scale = .42 } },
                new Binding { KeyIndex = 15, ControllerId = controller, Target = OutputTarget.LeftYNegative, Enabled = false, Processing = new SignalSettings { Curve = CurveKind.Smoothstep, Scale = .83 } },
                new Binding { KeyIndex = 15, ControllerId = controller, Target = OutputTarget.LeftTrigger, Processing = new SignalSettings { Curve = CurveKind.Smoothstep, Scale = .83 } },
                new Binding { KeyIndex = 21, ControllerId = controller, Target = OutputTarget.A },
                new Binding { KeyIndex = 15, ControllerId = "reuse-other", Target = OutputTarget.B, Processing = new SignalSettings { Scale = .27 } }
            };
            fixture.Inputs = new List<KeyInputSettings> {
                new KeyInputSettings { KeyIndex = 14, ActuationPoint = .22, OppositeKeyIndex = 21 },
                new KeyInputSettings { KeyIndex = 21, ActuationPoint = .38, OppositeKeyIndex = 14 },
                new KeyInputSettings { KeyIndex = 15, ActuationPoint = .67, RapidTriggerEnabled = true, ReleaseMovement = .18, OppositeKeyIndex = 9 },
                new KeyInputSettings { KeyIndex = 9, ActuationPoint = .49, OppositeKeyIndex = 15 }
            };
            Call(form, "Commit", fixture); Call(form, "ShowPage", "mapping"); SelectKeys(form, 14);
            SetPreviewClientSize(form, new Size(1320, 750));
            // Warm each persistent panel before observing control/handle identity.
            // Mouse gestures below use the keyboard's real selection events;
            // ClearSelection followed by a programmatic select would introduce
            // an artificial empty selection and unrelated row removals.
            foreach (string mode in new string[] { null, "advanced", "controller", "input" }) DetailMode(form, mode);
            var keyboard = Field<VisualKeyboard>(form, "keyboard");
            var keyPanel = Field<Panel>(form, "keyCardScroll");
            var curvePanel = Field<Panel>(form, "curveEditorScroll");
            var keysGrid = Field<DataGridView>(form, "keys");
            var bindingsGrid = Field<DataGridView>(form, "bindings");
            var settingsGrid = Field<DataGridView>(form, "settings");
            Control host = Field<Control>(form, "detailsHost");
            Control[] controls = ReuseControls(host).Concat(new Control[] { keysGrid, keyboard }).Distinct().ToArray();
            var handles = controls.Where(control => control.IsHandleCreated).ToDictionary(control => control, control => control.Handle);
            int addedControls = 0, removedControls = 0, disposedControls = 0, destroyedHandles = 0;
            foreach (Control control in controls)
            {
                control.ControlAdded += delegate { addedControls++; }; control.ControlRemoved += delegate { removedControls++; };
                control.Disposed += delegate { disposedControls++; }; control.HandleDestroyed += delegate { destroyedHandles++; };
            }
            DataGridViewRow[] keyRows = keysGrid.Rows.Cast<DataGridViewRow>().ToArray(), settingsRows = settingsGrid.Rows.Cast<DataGridViewRow>().ToArray(), bindingRows = bindingsGrid.Rows.Cast<DataGridViewRow>().ToArray();
            DataGridViewCell[] keyCells = ReuseCells(keysGrid), settingsCells = ReuseCells(settingsGrid), bindingCells = ReuseCells(bindingsGrid);
            int keyChanges = 0, settingChanges = 0, bindingsAdded = 0, bindingsRemoved = 0;
            keysGrid.RowsAdded += delegate { keyChanges++; }; keysGrid.RowsRemoved += delegate { keyChanges++; };
            settingsGrid.RowsAdded += delegate { settingChanges++; }; settingsGrid.RowsRemoved += delegate { settingChanges++; };
            bindingsGrid.RowsAdded += delegate(object sender, DataGridViewRowsAddedEventArgs e) { bindingsAdded += e.RowCount; };
            bindingsGrid.RowsRemoved += delegate(object sender, DataGridViewRowsRemovedEventArgs e) { bindingsRemoved += e.RowCount; };
            var shape = Field<ComboBox>(form, "curveShape");
            object[] shapeChoices = shape.Items.Cast<object>().ToArray();
            var opposite = Field<ComboBox>(form, "oppositeKey"); object unchangedChoice = ReuseOppositeChoice(opposite, 21);
            object token = Field<EditHistory>(form, "history").SnapshotToken; string before = Json(Current(form));
            keysGrid.Rows[14].Cells[2].Value = "987 stale";
            Call(form, "RefreshKeys");
            Equal("—", Convert.ToString(keysGrid.Rows[14].Cells[2].Value), "Refreshing a reused raw-value cell without an input snapshot clears the old sample.");
            CheckReuseRows(keysGrid, keyRows, keyCells, "Raw-value refresh");
            bindingsGrid.Rows[0].Cells[3].Value = "987 stale";

            foreach (string mode in new string[] { null, "advanced", "controller", "input" })
            {
                DetailMode(form, mode);
                Panel visibleScroll = mode == "advanced" ? curvePanel : mode == "controller" ? null : keyPanel;
                if (visibleScroll != null) { visibleScroll.AutoScrollPosition = new Point(0, 35); NativeSurfaceTheme.RefreshLayout(visibleScroll); }
                settingsGrid.CurrentCell = PropertyRow(form, "Scale").Cells[1];
                if (mode == "advanced") settingsGrid.FirstDisplayedScrollingRowIndex = 1;
                keyboard.Focus(); Pump(form);
                if (visibleScroll != null) Check(visibleScroll.AutoScrollPosition.Y < 0, "The sticky " + (mode ?? "Keys") + " tab fixture really is scrolled.");
                Point keyPosition = keyPanel.AutoScrollPosition, curvePosition = curvePanel.AutoScrollPosition;
                DataGridViewCell currentCell = settingsGrid.CurrentCell; int firstRow = settingsGrid.FirstDisplayedScrollingRowIndex;
                foreach (int key in new[] { 15, 15, 14 })
                {
                    ShortKeyboardClick(keyboard, key); Pump(form);
                    Equal(mode, Field<string>(form, "detailsMode"), "Key " + key + " retains " + (mode ?? "Keys") + ".");
                    Check(keyPanel.AutoScrollPosition == keyPosition && curvePanel.AutoScrollPosition == curvePosition,
                        "A key click, including an already-selected key, retains both detail scroll positions.");
                    Check(Object.ReferenceEquals(settingsGrid.CurrentCell, currentCell) && settingsGrid.FirstDisplayedScrollingRowIndex == firstRow,
                        "Key selection retains the current response cell and its list viewport.");
                    CheckReuseRows(bindingsGrid, bindingRows, bindingCells, "Equal-count key selection");
                    if (key == 14) CheckReuseKeyData(form, key, controller);
                    Check(shapeChoices.SequenceEqual(shape.Items.Cast<object>()) && Object.ReferenceEquals(unchangedChoice, ReuseOppositeChoice(opposite, 21)),
                        "Shared curve and opposite-key choices retain their instances while selection changes.");
                    Check(controls.All(control => !control.IsDisposed) && handles.All(pair => pair.Key.IsHandleCreated && pair.Key.Handle == pair.Value),
                        "Key selection reuses every warmed detail control and native handle.");
                }
                Call(form, "SetDetailMode", mode, true, false); Pump(form);
                Check(keyPanel.AutoScrollPosition == keyPosition && curvePanel.AutoScrollPosition == curvePosition,
                    "Activating the current tab is a no-op for both scroll positions.");
            }
            Check(addedControls == 0 && removedControls == 0 && disposedControls == 0 && destroyedHandles == 0,
                "Ordinary key selection never removes, replaces or recreates the detail control tree.");
            Check(bindingsAdded == 0 && bindingsRemoved == 0, "Equal-count mapping changes perform no row additions or removals.");
            CheckReuseRows(keysGrid, keyRows, keyCells, "Key selection/raw grid");
            CheckReuseRows(settingsGrid, settingsRows, settingsCells, "Key selection/response grid");
            Check(Object.ReferenceEquals(token, Field<EditHistory>(form, "history").SnapshotToken), "Selection and combo rebinding create no history snapshot.");
            Equal(before, Json(Current(form)), "Selection and view state changes do not mutate the profile.");

            ShortKeyboardClick(keyboard, 21); Pump(form); CheckReuseKeyData(form, 21, controller);
            Check(bindingsAdded == 0 && bindingsRemoved == 1 && Object.ReferenceEquals(bindingsGrid.Rows[0], bindingRows[0]) && Object.ReferenceEquals(bindingsGrid.Rows[0].Cells[1], bindingRows[0].Cells[1]),
                "Changing from two mappings to one removes only the trailing row and retains the remaining cells.");
            ShortKeyboardClick(keyboard, 9); Pump(form); CheckReuseKeyData(form, 9, controller);
            Check(bindingsAdded == 0 && bindingsRemoved == 2, "Changing to an unmapped key removes only the last mapping row.");
            ShortKeyboardClick(keyboard, 15); Pump(form); CheckReuseKeyData(form, 15, controller);
            Check(bindingsAdded == 2 && bindingsRemoved == 2, "Returning from an unmapped key creates precisely the two required rows.");
            string[] scope = Current(form).Bindings.Where(binding => binding.ControllerId == controller && binding.KeyIndex == 15).Select(binding => binding.BindingId).ToArray();
            Profile editBefore = Current(form); DetailMode(form, "advanced"); Edit(form, "Scale", "0.91");
            Profile expectedEdit = KeyEditing.ApplyProperty(editBefore, scope, "Scale", "0.91");
            Equal(Json(expectedEdit), Json(Current(form)), "An edit through the reused response grid affects only the newly selected key and controller.");
            Call(form, "Undo"); Pump(form); Equal(Json(editBefore), Json(Current(form)), "One undo restores the reused editor's entire new-key edit.");
            Call(form, "Redo"); Pump(form); Equal(Json(expectedEdit), Json(Current(form)), "Redo reapplies only the captured new-key scope.");
            CheckReuseRows(keysGrid, keyRows, keyCells, "Edit/undo/redo raw grid");
            CheckReuseRows(settingsGrid, settingsRows, settingsCells, "Edit/undo/redo response grid");
            Check(keyChanges == 0 && settingChanges == 0, "Key and response schemas remain persistent throughout selection, edit, undo and redo.");
            AssertPassive(form);
            Console.WriteLine("KEY REUSE PASS: " + (assertions - started) + " assertions; actual key gestures, controls/handles/rows/cells, sticky tabs and viewports, precise rebinding and new-key edits.");
        }
    }
}
