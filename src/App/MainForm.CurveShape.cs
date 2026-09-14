using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Tk75.Mapping;
using Binding = Tk75.Mapping.Binding;

namespace Tk75.App
{
    public sealed partial class MainForm
    {
        readonly SleekComboBox curveShape = new SleekComboBox();

        Control BuildCurveTools()
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, Margin = Padding.Empty };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); row.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            curveShape.Dock = DockStyle.Fill; curveShape.Margin = new Padding(3, 3, 3, 3);
            curveShape.DisplayMember = "Text"; curveShape.DropDownWidth = 250;
            curveShape.SelectedIndexChanged += delegate {
                if (updating) return;
                var option = curveShape.SelectedItem as SettingOption;
                if (option == null || option.Value == "—" || option.Value == "Gemischt") return;
                Attempt(delegate {
                    settings.EndEdit();
                    string[] ids = SelectedSettingsBindings();
                    if (ids.Length == 0) return;
                    try { Commit(PrepareCurveShape(ids, option.Value)); }
                    finally { RefreshSettings(); }
                });
            };
            row.Controls.Add(curveShape, 0, 0); row.SetColumnSpan(curveShape, 2);

            // A single visible shape choice. Whole-response presets live in the
            // adjacent menu; choosing a shape never resets the other settings.
            Combo(preset, 142, new object[] { "Linear", "Soft", "Aggressiv", "Racing", "Präzise Bewegung" }); preset.SelectedIndex = 0; preset.Visible = false;
            Disposed += delegate { preset.Dispose(); };
            var presetMenu = new ContextMenuStrip();
            var applyPresets = new ToolStripMenuItem(Tr("Preset anwenden", "Apply preset")); presetMenu.Items.Add(applyPresets);
            presetMenu.Opening += delegate {
                while (applyPresets.DropDownItems.Count != 0) { var old = applyPresets.DropDownItems[0]; applyPresets.DropDownItems.RemoveAt(0); old.Dispose(); }
                foreach (object candidate in preset.Items)
                {
                    object choice = candidate;
                    var item = new ToolStripMenuItem(choice is string ? UiText.Get((string)choice) : choice.ToString());
                    item.Enabled = SelectedSettingsBindings().Length != 0;
                    item.Click += delegate { Attempt(delegate { preset.SelectedItem = choice; settings.EndEdit(); ApplyPreset(); }); };
                    applyPresets.DropDownItems.Add(item);
                }
            };
            presetMenu.Items.Add(Tr("Aus gewählter Zuordnung speichern", "Save from selected mapping"), null, delegate { Attempt(SaveSignalPreset); });
            presetMenu.Items.Add(Tr("Importieren", "Import"), null, delegate { Attempt(ImportSignalPreset); });
            var export = new ToolStripMenuItem(Tr("Aktuelle Antwort exportieren…", "Export current response…"));
            export.Click += delegate { Attempt(ExportCurrentCurveResponse); }; presetMenu.Items.Add(export);
            presetMenu.Opening += delegate { export.Enabled = SelectedSettingsBindings().Length == 1; }; StyleMenu(presetMenu);
            var managePresets = new SleekButton { Text = "Presets ▾", Dock = DockStyle.Fill };
            managePresets.Click += delegate { presetMenu.Show(managePresets, new Point(0, managePresets.Height)); }; row.Controls.Add(managePresets, 2, 0);
            keyCardTips.SetToolTip(managePresets, Tr("Presets ersetzen die gesamten Ausgabeeinstellungen der Auswahl. Die Kurvenform links ändert nur die Form.",
                "Presets replace the selection's complete response settings. The shape choice on the left changes only the shape."));
            Combo(pasteMode, 176, new object[] { "Alles außer Mapping", "Alles", "Nur Deadzones", "Nur Kurve", "Nur Filter", "Nur Output Range", "Nur Mapping" });
            pasteMode.SelectedIndex = 0; pasteMode.Dock = DockStyle.Fill; pasteMode.DropDownWidth = 240; row.Controls.Add(pasteMode, 0, 1);
            var copy = new SleekButton { Text = "Kopieren", Dock = DockStyle.Fill }; copy.Click += delegate { Attempt(CopySettings); }; row.Controls.Add(copy, 1, 1);
            var paste = new SleekButton { Text = "Einfügen", Dock = DockStyle.Fill }; paste.Click += delegate { Attempt(PasteSettings); }; row.Controls.Add(paste, 2, 1);
            foreach (Control control in row.Controls) if (control is Button) { control.MinimumSize = Size.Empty; control.Margin = new Padding(3); }
            return row;
        }

        Profile PrepareCurveShape(string[] ids, string value)
        {
            Profile before = history.Current;
            Profile changed = KeyEditing.ApplyProperty(before, ids, "Curve", value);
            if (value == "Bezier")
                foreach (Binding binding in changed.Bindings.Where(binding => ids.Contains(binding.BindingId)))
                {
                    System.Collections.Generic.List<CurvePoint> points;
                    if (!CurveBezierEditing.TryCreate(before.Bindings.Single(original => original.BindingId == binding.BindingId).Processing, out points))
                        throw new InvalidOperationException(Tr("Diese Kurvenform ist für eine genaue Bézier-Umwandlung zu steil. Bitte zuerst die Krümmung reduzieren.",
                            "This curve is too steep for an accurate Bézier conversion. Reduce curvature first."));
                    binding.Processing.CustomPoints = points;
                }
            return changed;
        }

        void ExportCurrentCurveResponse()
        {
            settings.EndEdit();
            string[] ids = SelectedSettingsBindings();
            if (ids.Length != 1) throw new InvalidOperationException(Tr("Genau eine Zuordnung zum Exportieren auswählen.", "Select exactly one mapping to export."));
            var response = new SignalPreset { Name = Tr("Eigene Charakteristik", "Custom response"),
                Settings = CurveResponsePreview.Copy(history.Current.Bindings.Single(binding => binding.BindingId == ids[0]).Processing) };
            using (var dialog = new SaveFileDialog { Filter = Tr("Signal-Preset|*.json", "Signal preset|*.json"), FileName = "signal-preset.json" })
                if (dialog.ShowDialog(this) == DialogResult.OK) WorkspaceStore.WriteAtomic(dialog.FileName, SignalPresetJson.Serialize(response));
        }

        void RefreshCurveShapePicker(Binding[] selected)
        {
            bool previous = updating; updating = true;
            try
            {
                string[] values = selected.Select(binding => binding.Processing.Curve.ToString()).Distinct().ToArray();
                string displayed = values.Length == 0 ? "—" : values.Length == 1 ? values[0] : "Gemischt";
                var choices = Enum.GetValues(typeof(CurveKind)).Cast<CurveKind>().Select(kind => new SettingOption(kind.ToString()));
                if (values.Length != 1) choices = new[] { new SettingOption(displayed) }.Concat(choices);
                RefreshChoices(curveShape, choices, (before, after) => before.Value == after.Value);
                curveShape.SelectedItem = curveShape.Items.Cast<SettingOption>().Single(option => option.Value == displayed);
                curveShape.Enabled = selected.Length != 0;
                string help = CurveSettingHelp("Curve") + "\n" + Tr("Eine Änderung gilt für alle ausgewählten Zuordnungen. Gemischt lässt ihre bisherigen Formen unverändert.",
                    "Changing the shape applies to every selected mapping. Mixed keeps their existing shapes until you choose one.");
                curveShape.AccessibleName = Tr("Kurvenform", "Curve shape");
                curveShape.AccessibleDescription = help; keyCardTips.SetToolTip(curveShape, help);
            }
            finally { updating = previous; }
        }
    }
}
