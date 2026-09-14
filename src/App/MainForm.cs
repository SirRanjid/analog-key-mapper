using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Tk75.Diagnostics;
using Tk75.Mapping;
using Binding = Tk75.Mapping.Binding;

namespace Tk75.App
{
    public sealed partial class MainForm : Form
    {
        readonly WorkspaceStore store;
        readonly MultiControllerSession runtime;
        volatile ReaderSession reader;
        KeyMapDocument keymap;
        CalibrationDocument calibration;
        EditHistory history = new EditHistory(new Profile());
        string profilePath;
        readonly SleekComboBox devices = new SleekComboBox();
        readonly SleekComboBox profiles = new SleekComboBox();
        readonly SleekComboBox targets = new SleekComboBox();
        readonly SleekComboBox pasteMode = new SleekComboBox();
        readonly SleekComboBox preset = new SleekComboBox();
        readonly Label deviceStatus = new Label(), outputStatus = new Label(), selectionStatus = new Label(), liveStatus = new Label();
        readonly DataGridView keys = Grid(), bindings = Grid(true), settings = CreateCurveSettingsGrid(), monitor = Grid();
        readonly VisualKeyboard keyboard = new VisualKeyboard();
        readonly CurveCanvas curve = new CurveCanvas();
        readonly Timer uiTimer = new Timer { Interval = 33 };
        readonly CheckBox focusEnabled = new CheckBox { Text = "Automatisch nach Vordergrundprogramm", AutoSize = true };
        readonly CheckBox showAll = new CheckBox { Text = "Auch unbekannte Tasten-Indizes zeigen", AutoSize = true };
        readonly Dictionary<string, string> propertyLabels = new Dictionary<string, string> {
            {"TopDeadzone", "Ruhebereich (0–1)"}, {"BottomDeadzone", "Anschlagbereich (0–1)"},
            {"Curve", "Kurve"}, {"Exponent", "Kurvenstärke"}, {"MinOutput", "Ausgabe Minimum (0–1)"},
            {"MaxOutput", "Ausgabe Maximum (0–1)"}, {"Scale", "Ausgabestärke"}, {"OutputDeadzone", "Kleine Ausgaben ignorieren (0–1)"},
            {"Hysteresis", "Schaltabstand (0–1)"}, {"SmoothingTimeConstant", "Glättung (Sekunden; 0 = aus)"}, {"ButtonThreshold", "Auslösepunkt für Buttons (0–1)"}
        };
        List<Binding> copied;
        bool updating, hotkey, closing, applicationInputActive;
        FocusSettings focus;
        string lastForeground = "";
        int ticks;
        bool? lastCardReading, lastCardSamples;
        const int DisableHotkey = 7275;
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        public MainForm(string dataPath, bool preview)
        {
            previewMode = preview;
            Icon applicationIcon = AppStatusIcon.CreateApplication();
            Icon = applicationIcon;
            Disposed += delegate { applicationIcon.Dispose(); };
            Text = "Analog · Keyboard Mapper"; ClientSize = new Size(1440, 880); MinimumSize = new Size(1080, 740);
            Font = new Font("Segoe UI", 9.5f); BackColor = ModernTheme.Background; StartPosition = FormStartPosition.CenterScreen;
            store = new WorkspaceStore(dataPath); runtime = new MultiControllerSession(store);
            try { UiText.SetLanguage(UiPreferences.LoadLanguage(store.Root)); } catch (Exception ex) { store.Event("Language preference unavailable: " + ex.Message); }
            try { focus = store.LoadFocus(); }
            catch (Exception ex) { focus = new FocusSettings(); store.Event("Invalid foreground rules left unchanged: " + ex.Message); }
            BuildUi(); ModernTheme.Apply(this); UiText.Apply(this);
            keys.Columns[0].MinimumWidth = 48; keys.Columns[0].FillWeight = 16;
            keys.Columns[1].MinimumWidth = 70; keys.Columns[1].FillWeight = 36;
            keys.Columns[2].MinimumWidth = 65; keys.Columns[2].FillWeight = 30;
            keys.Columns[3].MinimumWidth = 42; keys.Columns[3].FillWeight = 18;
            foreach (string saved in store.Profiles())
            {
                try { history = new EditHistory(ReadProfile(saved)); profilePath = saved; break; }
                catch (Exception ex) { store.Event("Invalid startup profile left unchanged: " + ex.Message); }
            }
            if (profilePath == null) { history = new EditHistory(new Profile { Name = Tr("Neues Profil", "New profile") }); profilePath = store.NewProfilePath(); SaveProfile(); }
            Configure(); ReloadProfiles(); ReloadPresets(); SyncOutputMode(); RefreshKeys(); RefreshBindings(); focusEnabled.Checked = focus.Enabled;
            Shown += delegate { StartUiSession(); OpenSavedLearnedSources(); };
            HandleDestroyed += delegate { ReleaseShortcutRegistrations(); };
            HandleCreated += delegate { if (modeShortcutRegistrationActive) RefreshModeShortcutRegistration(); };
            if (preview)
            {
                keymap = KeyMapStore.Create(new string('a', 64)); keymap = KeyMapStore.SetLabel(keymap, 14, "W"); keymap = KeyMapStore.SetLabel(keymap, 9, "A");
                var example = new Profile { Name = Tr("Beispiel · freie Mehrfachzuordnung", "Example · multiple mappings") }; example.Bindings.Add(new Binding { KeyIndex = 14, Target = OutputTarget.LeftYPositive });
                example.Bindings.Add(new Binding { KeyIndex = 14, Target = OutputTarget.RightTrigger, Processing = new SignalSettings { Curve = CurveKind.Exponential, Exponent = 2, TopDeadzone = 0.04 } });
                history = new EditHistory(example); RefreshKeys();
                Shown += delegate { keys.ClearSelection(); keys.Rows[14].Selected = true; RefreshBindings(); updating = true; profiles.Items.Clear(); profiles.Items.Add(new ProfileItem { Name = example.Name, Path = profilePath }); profiles.SelectedIndex = 0; updating = false; };
                deviceStatus.Text = "Oberflächenvorschau · Beispieldaten · keine Geräte geöffnet";
                outputStatus.Text = OutputAvailability == null ? "Controller aus" : "Vorschau · Controller noch in Prüfung";
            }
            uiTimer.Tick += delegate { UpdateLive(); };
            FormClosing += OnApplicationClosing;
            Microsoft.Win32.SystemEvents.PowerModeChanged += OnPower;
            Disposed += delegate { DisposeDeviceDiscovery(); Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPower; uiTimer.Dispose(); DisposeApplicationResources(); };
        }
        void OnPower(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
        { if (e.Mode == Microsoft.Win32.PowerModes.Suspend) { CancelStartupReconnect(); runtime.Disable("Ruhezustand – Controller aus"); } }
        static DataGridView Grid(bool liveValues = false)
        {
            DataGridView grid = liveValues ? new BufferedValueGrid() : new DataGridView();
            grid.Dock = DockStyle.Fill; grid.BackgroundColor = ModernTheme.Surface; grid.BorderStyle = BorderStyle.None; grid.RowHeadersVisible = false;
            grid.AllowUserToAddRows = grid.AllowUserToDeleteRows = false; grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; grid.ReadOnly = grid.MultiSelect = true;
            return grid;
        }
        static FlowLayoutPanel Bar() { return new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8), WrapContents = true }; }
        Button Add(FlowLayoutPanel bar, string text, Action action)
        { var button = new SleekButton { Text = text, AutoSize = true, Margin = new Padding(3), Padding = new Padding(3) }; button.Click += delegate { Attempt(action); }; bar.Controls.Add(button); return button; }
        void Attempt(Action action) { if (deviceDetachInProgress || rgbClosePending) return; try { action(); } catch (Exception ex) { MessageBox.Show(this, UiText.Get(ex.Message), "Analog Key Mapper", MessageBoxButtons.OK, MessageBoxIcon.Information); } }
        static void Combo(ComboBox box, int width, object[] values)
        { box.DropDownStyle = ComboBoxStyle.DropDownList; box.Width = width; if (values != null) box.Items.AddRange(values); }
        void BuildBehavior(Control page)
        {
            var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(20), WrapContents = false, AutoScroll = true }; page.Controls.Add(panel);
            foreach (string field in new[] { "StickShape", "OpposedPolicy", "Aggregation" })
            {
                string property = field; var combo = new SleekComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 250, Tag = property };
                Type type = typeof(Profile).GetField(property).FieldType; combo.Items.AddRange(Enum.GetNames(type)); combo.SelectedItem = typeof(Profile).GetField(property).GetValue(history.Current).ToString();
                panel.Controls.Add(new Label { Text = property == "StickShape" ? "Stickform: Circle = diagonale Länge begrenzen" : property == "OpposedPolicy" ? "Gegenrichtungen: Neutral oder Differenz" : "Gleiche Ziele: Maximum oder begrenzte Summe", Width = 550, Height = 25 }); panel.Controls.Add(combo);
                combo.SelectedIndexChanged += delegate { if (!updating) Attempt(delegate { var profile = history.Current; typeof(Profile).GetField(property).SetValue(profile, Enum.Parse(type, (string)combo.SelectedItem)); Commit(profile); }); };
            }
            panel.Controls.Add(new Label { Text = "Achse invertieren: am Mapping das entsprechende + / − Ziel wählen.\nKeine Ausgabe an einer losgelassenen Taste, auch bei Minimum > 0.", Width = 650, Height = 55 });
            panel.Controls.Add(focusEnabled); focusEnabled.CheckedChanged += delegate { if (!updating) Attempt(delegate { bool enabled = focusEnabled.Checked; ChangeFocus(next => next.Enabled = enabled); }); };
            var bar = Bar(); bar.Dock = DockStyle.None; Add(bar, Tr("Programmregel hinzufügen", "Add application rule"), AddFocusRule); Add(bar, Tr("Dieses Profil als Standard", "Use this profile by default"), delegate { ChangeFocus(next => next.DefaultProfileFile = Path.GetFileName(profilePath)); }); Add(bar, Tr("Programmregeln anzeigen / entfernen", "View / remove application rules"), EditFocusRules); panel.Controls.Add(bar);
            panel.Controls.Add(new Label { Text = "Es werden nur Programmname oder Programmpfad im Vordergrund gelesen.\nEin automatischer Profilwechsel neutralisiert und deaktiviert den Controller.\nDas gewählte Profil wird danach bewusst wieder aktiviert.", Width = 700, Height = 80 });
        }
        void Connect()
        {
            if (deviceDetachInProgress) throw new InvalidOperationException(Tr("Die vorige Tastaturverbindung wird noch beendet.", "The previous keyboard connection is still closing."));
            var selected = devices.SelectedItem as DeviceItem; if (selected == null) Scan();
            if (selected == null) throw new InvalidOperationException(UiText.Get("Mehrere oder keine passenden Tastaturen gefunden. Bitte oben die gewünschte Tastatur auswählen.", "Multiple or no matching keyboards found. Choose your keyboard above."));
            if (TravelProtocols.Find(selected.Device) == null) throw new InvalidOperationException("Für dieses Reportformat fehlt ein bestätigter Adapter. Das Diagnoseprogramm kann Rohreports aufzeichnen.");
            if (DeferRgbReconnect()) return;
            runtime.SetReader(null); if (reader != null) { reader.Dispose(); reader = null; }
            var candidate = new ReaderSession(selected.Device);
            try
            {
                string identity = WorkspaceStore.Identity(selected.Device, candidate.Fingerprint); calibration = store.LoadCalibration(identity, candidate.Fingerprint);
                string path = store.KeyMapPath(candidate.Fingerprint); keymap = File.Exists(path) ? KeyMapStore.Load(path) : KeyMapStore.Create(candidate.Fingerprint);
                if (keymap.ProtocolFingerprint != candidate.Fingerprint) throw new InvalidDataException(Tr("Tastenplan stimmt nicht überein.", "The key map does not match this keyboard."));
                candidate.Fault += delegate(string error) { if (object.ReferenceEquals(reader, candidate)) runtime.Disable("Eingabefehler – Controller aus"); store.Event("Reader fault: " + error); };
                candidate.Start(); reader = candidate; runtime.SetReader(reader); Configure(); ConfigureKeyboardForDevice(selected.Device); store.Event("Input session requested; protocol " + reader.Fingerprint); RefreshKeys(); RefreshBindings();
            }
            catch { runtime.SetReader(null); reader = null; candidate.Dispose(); throw; }
        }
        string Label(int index) { var named = keymap == null ? null : keymap.Entries.FirstOrDefault(e => e.KeyIndex == index); return named == null ? LayoutLabel(index) : named.Label; }
        int[] SelectedKeys() { return keys.SelectedRows.Cast<DataGridViewRow>().Select(r => (int)r.Cells[0].Value).OrderBy(x => x).ToArray(); }
        string[] SelectedBindings() { return bindings.SelectedRows.Cast<DataGridViewRow>().Select(r => (string)r.Tag).ToArray(); }
        string[] SelectedSettingsBindings()
        {
            int[] selectedKeys = SelectedKeys();
            // A keyboard group is the editing scope, including when opening
            // its curve by double-clicking a single mapping row.
            if (selectedKeys.Length <= 1) return SelectedBindings();
            var indices = new HashSet<int>(selectedKeys);
            return CurrentControllerBindings(UiReadProfile).Where(b => indices.Contains(b.KeyIndex)).Select(b => b.BindingId).ToArray();
        }
        readonly HashSet<int> displayedBindingKeys = new HashSet<int>();
        IEnumerable<Binding> CurrentControllerBindings(Profile profile)
        { return profile.Bindings.Where(b => (string.IsNullOrEmpty(b.ControllerId) ? ControllerRouting.DefaultControllerId : b.ControllerId) == runtime.SelectedControllerId); }
        void RefreshKeys()
        {
            int[] selection = SelectedKeys(); bool previous = updating; updating = true;
            try
            {
                var profile = history.Current;
                if (keys.Rows.Count == 0) keys.Rows.Add(256);
                var known = new HashSet<int>(profile.Bindings.Select(b => b.KeyIndex).Concat(profile.Inputs.Select(i => i.KeyIndex))); foreach (int index in LayoutIndices()) known.Add(index); if (keymap != null) foreach (var entry in keymap.Entries) known.Add(entry.KeyIndex); if (reader != null) foreach (var entry in reader.GetSnapshot()) known.Add(entry.KeyIndex);
                if (profile.LearnedInputs != null) foreach (var input in profile.LearnedInputs) known.Add(input.KeyIndex);
                var counts = CurrentControllerBindings(profile).GroupBy(b => b.KeyIndex).ToDictionary(group => group.Key, group => group.Count());
                var snapshot = InputUiSnapshot().ToDictionary(entry => entry.KeyIndex);
                for (int i = 0; i < 256; i++)
                {
                    int count; counts.TryGetValue(i, out count);
                    var row = keys.Rows[i];
                    SetCurveCellValue(row.Cells[0], i); SetCurveCellValue(row.Cells[1], Label(i));
                    KeyStateSnapshot sample;
                    SetCurveCellValue(row.Cells[2], snapshot.TryGetValue(i, out sample) && sample.Known
                        ? sample.RawValue.ToString() + (sample.Stale ? Tr(" · alt", " · stale") : "") : "—");
                    SetCurveCellValue(row.Cells[3], count);
                    var named = keymap == null ? null : keymap.Entries.FirstOrDefault(e => e.KeyIndex == i); keyboard.SetKeyLabel(i, named == null ? null : named.Label);
                }
                if (keys.CurrentCell != null && !showAll.Checked && !known.Contains(keys.CurrentCell.RowIndex)) keys.CurrentCell = null;
                for (int i = 0; i < 256; i++) keys.Rows[i].Visible = showAll.Checked || known.Contains(i);
                var selected = new HashSet<int>(selection);
                for (int i = 0; i < 256; i++) keys.Rows[i].Selected = keys.Rows[i].Visible && selected.Contains(i);
            }
            finally { updating = previous; } HighlightKeys(); RefreshKeyControllerAssignments(); RefreshKeyBehaviorAnnotations();
        }
        void HighlightKeys()
        {
            bool previous = updating; updating = true;
            try { keyboard.SetSelectedKeys(SelectedKeys()); keyboard.SetMappedKeys(CurrentControllerBindings(history.Current).Select(b => b.KeyIndex)); }
            finally { updating = previous; }
        }
        void RefreshBindings()
        {
            var selectedKeys = new HashSet<int>(SelectedKeys());
            string[] chosen = selectedKeys.SetEquals(displayedBindingKeys) ? SelectedBindings() : new string[0]; bool previous = updating; updating = true;
            try
            {
                Binding[] visible = CurrentControllerBindings(history.Current).Where(b => selectedKeys.Contains(b.KeyIndex)).ToArray();
                // The grid owns persistent rows/cells. Only a changed mapping
                // count needs new or removed rows; their identity is rebound
                // before any selection or live-value event can observe them.
                while (bindings.Rows.Count > visible.Length) bindings.Rows.RemoveAt(bindings.Rows.Count - 1);
                if (bindings.Rows.Count < visible.Length) bindings.Rows.Add(visible.Length - bindings.Rows.Count);
                for (int i = 0; i < visible.Length; i++)
                {
                    Binding binding = visible[i]; var row = bindings.Rows[i];
                    bool rebound = !Object.Equals(row.Tag, binding.BindingId); row.Tag = binding.BindingId;
                    SetCurveCellValue(row.Cells[0], Label(binding.KeyIndex)); SetCurveCellValue(row.Cells[1], TargetLabel(binding.Target));
                    SetCurveCellValue(row.Cells[2], binding.Enabled ? Tr("Ja", "Yes") : Tr("Nein", "No"));
                    if (rebound || row.Cells[3].Value == null) SetCurveCellValue(row.Cells[3], "—");
                }
                bindings.ClearSelection(); foreach (DataGridViewRow row in bindings.Rows) if (chosen.Contains((string)row.Tag)) row.Selected = true;
                if (bindings.SelectedRows.Count == 0) bindings.SelectAll();
                displayedBindingKeys.Clear(); displayedBindingKeys.UnionWith(selectedKeys);
                selectionStatus.Text = selectedKeys.Count + (selectedKeys.Count == 1 ? Tr(" Taste · ", " key · ") : Tr(" Tasten · ", " keys · ")) + bindings.Rows.Count + (bindings.Rows.Count == 1 ? Tr(" Zuordnung", " mapping") : Tr(" Zuordnungen", " mappings"));
            }
            finally { updating = previous; } RefreshSettings(); UpdateKeyCard(); RefreshInputEditor(); RefreshMappingSummaries(true);
        }
        void RefreshSettings()
        {
            CancelCurveRangeGestures();
            ((CurveSettingsGrid)settings).CancelSlider();
            string[] ids = SelectedSettingsBindings(); var chosen = history.Current.Bindings.Where(b => ids.Contains(b.BindingId)).ToArray(); bool previousUpdating = updating; updating = true;
            try
            {
                string context = CurveSelectionContext(ids);
                // Reusing rows must not carry an unfinished numeric draft to
                // a different key, controller or profile. Cancel under the
                // refresh guard so CellEndEdit cannot apply it to the new scope.
                // CancelEdit restores the old text but intentionally keeps the
                // native editor open. End that restored edit before updating
                // its reused cell with the next selection's value.
                if (curveSettingsContext != null && curveSettingsContext != context && settings.IsCurrentCellInEditMode && settings.CancelEdit()) settings.EndEdit();
                curveSettingsContext = context;
                // CellEndEdit may run inside SetCurrentCellAddressCore while a
                // click is moving to another cell. Replacing rows or assigning
                // CurrentCell here reenters that transition. Keep the row/cell
                // instances, selection and scroll position owned by the grid.
                foreach (DataGridViewRow row in settings.Rows)
                {
                    string property = (string)row.Tag;
                    var field = typeof(SignalSettings).GetField(property); string[] values = chosen.Select(b => Convert.ToString(field.GetValue(b.Processing), CultureInfo.InvariantCulture)).Distinct().ToArray();
                    SetCurveCellValue(row.Cells[0], CurveSettingCaption(property));
                    SetCurveCellValue(row.Cells[1], values.Length == 0 ? "—" : values.Length == 1 ? values[0] : "Gemischt");
                    RefreshCurveSettingRow(row, property, chosen, values);
                }
                RefreshCurveShapePicker(chosen);
                settings.Columns[2].HeaderText = Tr("Regler", "Adjust");
                curve.UpdateCurve(chosen.Length == 0 ? null : chosen[0].Processing, chosen.Length > 1 && chosen.Skip(1).Any(b => !SameCurve(chosen[0].Processing, b.Processing)),
                    CurveSelectionContext(ids));
                RefreshCurveRangeRails(chosen);
            }
            finally { updating = previousUpdating; }
        }
        static bool SameCurve(SignalSettings a, SignalSettings b) { return CurveResponsePreview.Same(a, b); }
        void EditProperty(int row)
        {
            if (row < 0 || row >= settings.Rows.Count) return; string property = (string)settings.Rows[row].Tag; string value = Convert.ToString(settings.Rows[row].Cells[1].Value, CultureInfo.InvariantCulture);
            // A formatted placeholder may come back from the text editor when
            // the user leaves a mixed cell without supplying a new value.
            if (value == "—" || value == "Gemischt" || value == UiText.Get("Gemischt")) { RefreshSettings(); return; }
            var ids = SelectedSettingsBindings(); if (ids.Length == 0) { RefreshSettings(); return; }
            try { Commit(KeyEditing.ApplyProperty(history.Current, ids, property, value)); } finally { RefreshSettings(); }
        }
        void ApplyCurve(SignalSettings editedCurve)
        { Attempt(delegate { var ids = SelectedSettingsBindings(); var profile = history.Current; foreach (var b in profile.Bindings.Where(b => ids.Contains(b.BindingId))) { b.Processing.Curve = editedCurve.Curve; b.Processing.CustomPoints = editedCurve.CustomPoints.Select(p => new CurvePoint(p.X, p.Y) { Tangent = p.Tangent }).ToList(); } Commit(profile); }); }
        void AddBinding()
        { int[] selected = SelectedKeys(); if (selected.Length == 0) throw new InvalidOperationException("Zuerst eine oder mehrere Tasten auswählen."); Commit(MappingAssignments.Add(history.Current, selected, ((TargetItem)targets.SelectedItem).Target, runtime.SelectedControllerId)); }
        void RemoveBinding() { string[] ids = SelectedBindings(); var profile = history.Current; profile.Bindings.RemoveAll(b => ids.Contains(b.BindingId)); Commit(profile); }
        void ToggleBindings() { string[] ids = SelectedSettingsBindings(); var profile = history.Current; bool activate = profile.Bindings.Where(b => ids.Contains(b.BindingId)).Any(b => !b.Enabled); foreach (var b in profile.Bindings.Where(b => ids.Contains(b.BindingId))) b.Enabled = activate; Commit(profile); }
        void ApplyPreset()
        {
            Commit(ProfileEditing.ApplySettings(history.Current, SelectedSettingsBindings(), SelectedPreset().Settings));
        }
        SignalPreset SelectedPreset()
        {
            var custom = preset.SelectedItem as PresetItem; if (custom != null) return SignalPresetJson.Clone(custom.Value);
            var signal = new SignalSettings(); string name = (string)preset.SelectedItem;
            if (name == "Soft") { signal.Curve = CurveKind.Exponential; signal.Exponent = 2; }
            if (name == "Aggressiv") { signal.Curve = CurveKind.Logarithmic; signal.Exponent = 4; }
            if (name == "Racing") { signal.Curve = CurveKind.Smoothstep; signal.TopDeadzone = 0.03; signal.BottomDeadzone = 0.03; }
            if (name == "Präzise Bewegung") { signal.Curve = CurveKind.Exponential; signal.Exponent = 2.5; signal.MaxOutput = 0.8; }
            return new SignalPreset { Name = name, Settings = signal };
        }
        void ReloadPresets()
        {
            while (preset.Items.Count > 5) preset.Items.RemoveAt(5);
            string directory = Path.Combine(store.Root, "presets"); Directory.CreateDirectory(directory);
            foreach (string path in Directory.GetFiles(directory, "*.json"))
            { try { if (new FileInfo(path).Length > 1048576) throw new InvalidDataException("Preset ist zu groß."); preset.Items.Add(new PresetItem { Value = SignalPresetJson.Deserialize(File.ReadAllText(path)) }); } catch (Exception ex) { store.Event("Preset skipped: " + ex.Message); } }
            if (preset.SelectedIndex < 0) preset.SelectedIndex = 0;
        }
        void StorePreset(SignalPreset value)
        { WorkspaceStore.WriteAtomic(Path.Combine(store.Root, "presets", Guid.NewGuid().ToString("N") + ".json"), SignalPresetJson.Serialize(value)); ReloadPresets(); preset.SelectedIndex = preset.Items.Count - 1; }
        void SaveSignalPreset()
        { string[] ids = SelectedBindings(); if (ids.Length != 1) throw new InvalidOperationException("Genau eine Zuordnung als Quelle für das neue Preset auswählen."); string name = Ask("Presetname", Tr("Eigene Charakteristik", "Custom response")); if (name == null) return; StorePreset(new SignalPreset { Name = name, Settings = history.Current.Bindings.First(b => b.BindingId == ids[0]).Processing }); }
        void ImportSignalPreset()
        { using (var dialog = new OpenFileDialog { Filter = Tr("Signal-Preset|*.json", "Signal preset|*.json") }) if (dialog.ShowDialog(this) == DialogResult.OK) { if (new FileInfo(dialog.FileName).Length > 1048576) throw new InvalidDataException(Tr("Preset ist zu groß.", "The preset file is too large.")); StorePreset(SignalPresetJson.Deserialize(File.ReadAllText(dialog.FileName))); } }
        void ExportSignalPreset()
        { using (var dialog = new SaveFileDialog { Filter = Tr("Signal-Preset|*.json", "Signal preset|*.json"), FileName = "signal-preset.json" }) if (dialog.ShowDialog(this) == DialogResult.OK) WorkspaceStore.WriteAtomic(dialog.FileName, SignalPresetJson.Serialize(SelectedPreset())); }
        void CopySettings()
        {
            int[] selected = SelectedKeys(); if (selected.Length != 1) throw new InvalidOperationException("Zum Kopieren genau eine Quelltaste auswählen.");
            copied = KeyEditing.CopyKey(history.Current, selected[0], runtime.SelectedControllerId);
        }
        void PasteSettings()
        {
            if (copied == null) throw new InvalidOperationException("Zuerst eine Taste mit Zuordnungen kopieren.");
            CopyPart[] parts = { CopyPart.AllExceptMapping, CopyPart.All, CopyPart.Deadzones, CopyPart.Curve, CopyPart.Filters, CopyPart.OutputRange, CopyPart.Mapping };
            Commit(KeyEditing.Paste(history.Current, SelectedKeys(), copied, parts[pasteMode.SelectedIndex], runtime.SelectedControllerId));
        }
        void Configure()
        {
            CancelMappingDrag(); EnsureLearnedPressureRange(); sharedPressureRange = new KeyboardPressureRange(calibration);
            if (sharedPressureRange.HasIgnoredLegacyValues && !Object.ReferenceEquals(reportedLegacyPressureRange, calibration))
            {
                reportedLegacyPressureRange = calibration;
                store.Event("Legacy pressure ranges outside the positive 16-bit sensor domain were left unchanged on disk and excluded from the shared range.");
            }
            keyboardSuppression.UpdateEligibility(new SuppressionKey[0], false, true); runtime.Configure(history.Current, sharedPressureRange.Resolve());
            ConfigureLearnedInputs(); RefreshKeySources();
            ApplyProfileInputModePreference(); RefreshModeShortcutRegistration(); ApplyKeyboardSuppressionPreference(); SyncOutputMode();
            RefreshKeyBehaviorAnnotations();
        }
        void Commit(Profile profile)
        {
            profile = MergePendingInput(profile); MappingValidation.RequireValid(profile);
            if (ProfileJson.Serialize(profile) == ProfileJson.Serialize(history.Current)) { inputDirty = false; return; }
            Profile previous = history.Current;
            history.Commit(profile); inputDirty = false; ApplyProfileEdit(previous);
        }
        void ApplyProfileEdit(Profile previous)
        {
            if (!ProfileChangeImpact.RequiresControllerReset(previous, history.Current))
            { ApplyKeyboardSuppressionPreference(); return; }
            Configure(); RefreshKeys(); RefreshBindings(); SyncBehavior();
        }
        void Undo() { FlushInputDraft(); Profile previous = history.Current; if (history.Undo()) ApplyProfileEdit(previous); }
        void Redo() { FlushInputDraft(); Profile previous = history.Current; if (history.Redo()) ApplyProfileEdit(previous); }
        void SyncBehavior()
        {
            updating = true;
            try { foreach (Control child in Descendants(this)) { var combo = child as ComboBox; if (combo != null && combo.Tag is string && typeof(Profile).GetField((string)combo.Tag) != null) combo.SelectedItem = typeof(Profile).GetField((string)combo.Tag).GetValue(history.Current).ToString(); } }
            finally { updating = false; }
        }
        static IEnumerable<Control> Descendants(Control parent) { foreach (Control child in parent.Controls) { yield return child; foreach (Control item in Descendants(child)) yield return item; } }
        Profile ReadProfile(string path) { if (new FileInfo(path).Length > 4194304) throw new InvalidDataException(Tr("Profil ist zu groß.", "The profile file is too large.")); return ProfileJson.Deserialize(File.ReadAllText(path)); }
        void SaveProfile() { FlushInputDraft(); if (profilePath != null) WorkspaceStore.WriteAtomic(profilePath, ProfileJson.Serialize(history.Current)); }
        void LoadProfile(string path) { var profile = ReadProfile(path); CancelStartupReconnect(); inputDirty = false; history = new EditHistory(profile); profilePath = path; ResetProfileInputMode(); Configure(); ReloadProfiles(); RefreshKeys(); RefreshBindings(); SyncBehavior(); store.Event("Profile changed"); }
        void ReloadProfiles()
        {
            updating = true;
            try { profiles.Items.Clear(); foreach (string path in store.Profiles()) { try { var item = new ProfileItem { Path = path, Name = ReadProfile(path).Name }; profiles.Items.Add(item); if (path == profilePath) profiles.SelectedItem = item; } catch (Exception ex) { store.Event("Invalid profile skipped: " + ex.Message); } } }
            finally { updating = false; }
        }
        void NewProfile(bool duplicate)
        { string name = Ask("Profilname", duplicate ? history.Current.Name + Tr(" Kopie", " copy") : Tr("Neues Profil", "New profile")); if (name == null) return; SaveProfile(); var profile = duplicate ? history.Current : new Profile(); profile.Name = name; MappingValidation.RequireValid(profile); profilePath = store.NewProfilePath(); history = new EditHistory(profile); SaveProfile(); Configure(); ReloadProfiles(); RefreshKeys(); RefreshBindings(); SyncBehavior(); }
        void RenameProfile() { string name = Ask("Profilname", history.Current.Name); if (name == null) return; var profile = history.Current; profile.Name = name; Commit(profile); SaveProfile(); ReloadProfiles(); }
        void DeleteProfile()
        {
            if (MessageBox.Show(this, string.Format(Tr("Profil „{0}“ löschen?", "Delete profile ‘{0}’?"), history.Current.Name), Tr("Profil löschen", "Delete profile"), MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            runtime.Disable("Profil gelöscht"); string file = Path.GetFileName(profilePath);
            ChangeFocus(next => { next.Rules.RemoveAll(r => r.ProfileFile == file); if (next.DefaultProfileFile == file) next.DefaultProfileFile = null; });
            File.Delete(profilePath); inputDirty = false; profilePath = null; history = new EditHistory(new Profile { Name = Tr("Neues Profil", "New profile") });
            foreach (string remaining in store.Profiles()) { try { ReadProfile(remaining); LoadProfile(remaining); return; } catch (Exception ex) { store.Event("Invalid remaining profile skipped: " + ex.Message); } }
            profilePath = store.NewProfilePath(); SaveProfile(); Configure(); ReloadProfiles(); RefreshKeys(); RefreshBindings(); SyncBehavior();
        }
        void ImportProfile() { using (var dialog = new OpenFileDialog { Filter = Tr("JSON-Profil|*.json", "JSON profile|*.json") }) if (dialog.ShowDialog(this) == DialogResult.OK) { var profile = ReadProfile(dialog.FileName); SaveProfile(); profilePath = store.NewProfilePath(); history = new EditHistory(profile); SaveProfile(); Configure(); ReloadProfiles(); RefreshKeys(); RefreshBindings(); SyncBehavior(); } }
        void ExportProfile() { FlushInputDraft(); using (var dialog = new SaveFileDialog { Filter = Tr("JSON-Profil|*.json", "JSON profile|*.json"), FileName = "profile.json" }) if (dialog.ShowDialog(this) == DialogResult.OK) WorkspaceStore.WriteAtomic(dialog.FileName, ProfileJson.Serialize(history.Current)); }
        string Ask(string title, string initial)
        {
            using (var dialog = new Form { Text = title, ClientSize = new Size(450, 120), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false })
            { var input = new TextBox { Left = 15, Top = 16, Width = 420, Text = initial }; var ok = new SleekButton { Text = "Übernehmen", Left = 310, Top = 65, Width = 125, DialogResult = DialogResult.OK, Tag = "primary" }; dialog.Controls.Add(input); dialog.Controls.Add(ok); dialog.AcceptButton = ok; ModernTheme.Apply(dialog); UiText.Apply(dialog); return dialog.ShowDialog(this) == DialogResult.OK ? input.Text.Trim() : null; }
        }
        void LearnKey()
        {
            if (reader == null || !reader.IsReading) throw new InvalidOperationException(Tr("Zuerst die analoge Tastatur verbinden.", "Connect the analog keyboard first."));
            string label = Ask("Welche Taste anlernen?", "W"); if (label == null) return; runtime.Disable("Tastenlernen – Controller aus");
            using (var dialog = new LearnDialog(reader, label)) if (dialog.ShowDialog(this) == DialogResult.OK)
            { var next = KeyMapStore.SetLabel(keymap, dialog.KeyIndex, label); KeyMapStore.Save(store.KeyMapPath(reader.Fingerprint), next); keymap = next; RefreshKeys(); RefreshBindings(); store.Event("Key label learned"); }
        }
        void RequireReader() { if (!LiveInputReading) throw new InvalidOperationException("Zuerst die Tastatur verbinden."); }
        void UpdateLive()
        {
            UpdatePressureCapture();
            UpdateInputThresholdCapture();
            if (closing || deviceDetachInProgress) { RefreshTrayStatus(); return; }
            bool showLive = Visible && WindowState != FormWindowState.Minimized;
            runtime.SetPreviewActive(showLive);
            ++ticks;
            // Input safety, lighting recovery and foreground rules must keep
            // running even when the window does not need a visual refresh.
            RefreshKeyboardSuppression(false);
            RefreshRgbLighting();
            UpdateDetectedLayout();
            if (ticks % 30 == 0 && activeMappingDrag == null) CheckForeground();
            TryReconnectStartupControllers();
            RefreshTrayStatus();
            if (!showLive) return;

            RefreshInputModeUi();
            UpdateControllerConnectionUi();
            var snapshot = InputUiSnapshot();
            bool refreshMonitor = ticks % 10 == 0 && monitor.Visible;
            var frame = bindings.Visible || controllerPreview.Visible || liveStatus.Visible || refreshMonitor ? runtime.Preview : null;
            var byIndex = refreshMonitor || controllerPreview.Visible && frame.UnavailableKeys.Count > 0 ? snapshot.ToDictionary(s => s.KeyIndex) : null;
            if (keys.Visible)
                foreach (var entry in snapshot) { if (!keys.Rows[entry.KeyIndex].Visible) keys.Rows[entry.KeyIndex].Visible = true; SetCellValue(keys.Rows[entry.KeyIndex].Cells[2], entry.Known ? entry.RawValue.ToString() + (entry.Stale ? Tr(" · alt", " · stale") : "") : "—"); }
            if (keyboard.Visible) UpdateKeyboardValues(snapshot);
            bool reading = LiveInputReading, sampled = LiveInputSamples;
            if (lastCardReading != reading || lastCardSamples != sampled) { UpdateKeyCard(); lastCardReading = reading; lastCardSamples = sampled; }
            if (pressure.Visible || pressureText.Visible) UpdatePressure(snapshot);
            if (bindings.Visible)
                foreach (DataGridViewRow row in bindings.Rows) { SignalResult result; SetCellValue(row.Cells[3], frame.BindingResults.TryGetValue((string)row.Tag, out result) && result.IsValid ? result.Final.ToString("P1") : "—"); }
            if (refreshMonitor)
            {
                var profile = UiReadProfile;
                monitor.Rows.Clear();
                foreach (var b in CurrentControllerBindings(profile))
                {
                    SignalResult result; frame.BindingResults.TryGetValue(b.BindingId, out result); KeyStateSnapshot raw; bool has = byIndex.TryGetValue(b.KeyIndex, out raw) && raw.Known;
                    CalibrationEntry cal = calibration == null ? null : calibration.Entries.FirstOrDefault(e => e.KeyIndex == b.KeyIndex);
                    monitor.Rows.Add(Label(b.KeyIndex) + " → " + TargetLabel(b.Target), has ? raw.RawValue.ToString() : "—", result != null && result.IsValid && cal != null && cal.MeasuredTravel.HasValue ? (result.Normalized * cal.MeasuredTravel.Value).ToString("F2") + " ≈" : "—",
                        Percent(result, 0), Percent(result, 1), Percent(result, 2), Percent(result, 3), !b.Enabled ? Tr("Inaktiv", "Inactive") : result == null ? Tr("Wartet", "Waiting") : result.Error == null ? "OK" : UiText.Get(result.Error));
                }
            }
            deviceStatus.Text = reader == null ? HasLearnedInputs ? Tr("Gelernte Eingaben · ", "Learned inputs · ") + (reading ? Tr("verbunden", "connected") : Tr("Gerät nicht verfügbar", "device unavailable")) : DiscoveryStatus() : UiText.Get(reader.Status) + " · " + snapshot.Length + Tr(" Tasten gesehen", " keys seen");
            string nextOutputStatus = OutputAvailability == null ? UiText.Get(runtime.Status) : selectedControllerKind == ControllerKind.DualSense ? Tr("PS5-Ausgabe · Einrichtung noch offen", "PS5 output · setup pending") : UiText.Get("Vorschau · Controller noch in Prüfung");
            bool anyEnabled = runtime.AnyEnabled;
            if (anyEnabled && !runtime.Enabled) nextOutputStatus = Tr("Andere Controller verbunden", "Other controllers connected");
            if (controllerPreview.Visible)
            {
                controllerPreview.KeyboardMode = runtime.KeyboardMode;
                controllerPreview.UnseenInputsOnly = reading && frame.UnavailableKeys.Count > 0 && frame.UnavailableKeys.All(index => !byIndex.ContainsKey(index));
                controllerPreview.MissingInputLabels = String.Join(", ", frame.UnavailableKeys.Take(3).Select(Label).ToArray()) + (frame.UnavailableKeys.Count > 3 ? " +" + (frame.UnavailableKeys.Count - 3) : "");
                controllerPreview.SetPreview(frame, reading, runtime.Enabled);
            }
            if (runtime.KeyboardMode && anyEnabled) nextOutputStatus = Tr("Tastaturmodus · Controller verbunden und neutral", "Keyboard mode · controller connected and neutral");
            if (keyboardSuppression.IsEnabled && !keyboardSuppression.IsInstalled) nextOutputStatus += " · " + UiText.Get(keyboardSuppression.Status);
            // Publish only the final text. Intermediate general/mode statuses
            // caused repeated label repaints even while nothing had changed.
            if (outputStatus.Text != nextOutputStatus) outputStatus.Text = nextOutputStatus;
            Color nextOutputColor = anyEnabled ? Color.FromArgb(22, 125, 82) : ModernTheme.Muted;
            if (outputStatus.ForeColor != nextOutputColor) outputStatus.ForeColor = nextOutputColor;
            if (liveStatus.Visible)
            {
                string nextLiveStatus = string.Format(UiText.Get("LX {0:F3}   LY {1:F3}   RX {2:F3}   RY {3:F3}   LT {4:P1}   RT {5:P1}   Buttons 0x{6:X4}\n¹ mm: lineare Schätzung aus selbst gemessenem Gesamthub. Alte/fehlende Werte erzeugen keine Controller-Ausgabe."), frame.LeftX, frame.LeftY, frame.RightX, frame.RightY, frame.LeftTrigger, frame.RightTrigger, frame.Buttons);
                if (liveStatus.Text != nextLiveStatus) liveStatus.Text = nextLiveStatus;
            }
        }
        static void SetCellValue(DataGridViewCell cell, string value) { if (!object.Equals(cell.Value, value)) cell.Value = value; }
        static string Percent(SignalResult result, int stage) { if (result == null || !result.IsValid) return "—"; return (stage == 0 ? result.Normalized : stage == 1 ? result.AfterDeadzone : stage == 2 ? result.AfterCurve : result.Final).ToString("P1"); }
        void AddFocusRule()
        {
            string executable = Ask("Programmname (z. B. Spiel.exe) oder vollständiger Pfad", ""); if (executable == null) return;
            ChangeFocus(next => { next.Rules.RemoveAll(r => string.Equals(r.Executable, executable, StringComparison.OrdinalIgnoreCase)); next.Rules.Add(new FocusRule { Executable = executable, ProfileFile = Path.GetFileName(profilePath) }); });
        }
        void ChangeFocus(Action<FocusSettings> edit)
        {
            var next = new FocusSettings { Enabled = focus.Enabled, DefaultProfileFile = focus.DefaultProfileFile, Rules = focus.Rules.Select(r => new FocusRule { Executable = r.Executable, ProfileFile = r.ProfileFile }).ToList() };
            try { edit(next); store.SaveFocus(next); focus = next; lastForeground = ""; }
            finally { bool previous = updating; updating = true; focusEnabled.Checked = focus.Enabled; updating = previous; }
        }
        void EditFocusRules()
        {
            using (var form = new Form { Text = "Programmregeln", ClientSize = new Size(650, 350), StartPosition = FormStartPosition.CenterParent })
            { var list = new ListBox { Dock = DockStyle.Fill }; foreach (var rule in focus.Rules) list.Items.Add(rule.Executable + " → " + rule.ProfileFile); form.Controls.Add(list); var remove = new SleekButton { Dock = DockStyle.Bottom, Text = "Ausgewählte Regel entfernen" }; form.Controls.Add(remove); remove.Click += delegate { Attempt(delegate { int index = list.SelectedIndex; if (index >= 0) { ChangeFocus(next => next.Rules.RemoveAt(index)); list.Items.RemoveAt(index); } }); }; ModernTheme.Apply(form); UiText.Apply(form); form.ShowDialog(this); }
        }
        void CheckForeground()
        {
            if (!focus.Enabled || suppressionDialogOpen || modeShortcutDialogOpen) return;
            try
            {
                uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid); if (pid == 0 || pid == Process.GetCurrentProcess().Id) return;
                using (var process = Process.GetProcessById((int)pid))
                {
                    string name = process.ProcessName + ".exe", path = null;
                    if (focus.Rules.Any(r => Path.IsPathRooted(r.Executable))) { try { path = process.MainModule.FileName; } catch (System.ComponentModel.Win32Exception) { } }
                    string marker = pid + ":" + name; if (marker == lastForeground) return; lastForeground = marker;
                    var rule = focus.Rules.FirstOrDefault(r => string.Equals(r.Executable, path, StringComparison.OrdinalIgnoreCase)) ?? focus.Rules.FirstOrDefault(r => string.Equals(r.Executable, name, StringComparison.OrdinalIgnoreCase));
                    string file = rule == null ? focus.DefaultProfileFile : rule.ProfileFile;
                    if (file != null) { string target = store.RequireLocalProfile(file); if (target != profilePath) { SaveProfile(); LoadProfile(target); } }
                }
            }
            catch (Exception ex) { runtime.Disable("Profilwechsel fehlgeschlagen"); store.Event("Foreground profile failed: " + ex.Message); }
        }
        protected override void WndProc(ref Message message)
        {
            if (HandleSystemSessionMessage(ref message)) return;
            if (message.Msg == 0x0312 && message.WParam.ToInt32() == DisableHotkey && !deviceDetachInProgress && IsCurrentShortcutMessage(message, true)) { CancelStartupReconnect(); runtime.Disable("Abschalter – Controller aus"); RefreshKeyboardSuppression(false); }
            if (message.Msg == 0x0312 && message.WParam.ToInt32() == ModeHotkey && !deviceDetachInProgress && IsCurrentShortcutMessage(message, false)) Attempt(ToggleInputMode);
            if (message.Msg == 0x0219) OnDeviceChange(message);
            base.WndProc(ref message);
        }
        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if (keyData == Keys.Escape && TryCancelSocdCapture()) return true;
            if (deviceDetachInProgress && (keyData == (Keys.Control | Keys.S) || keyData == (Keys.Control | Keys.C) ||
                keyData == (Keys.Control | Keys.V) || keyData == (Keys.Control | Keys.Z) || keyData == (Keys.Control | Keys.Y) ||
                keyData == (Keys.Control | Keys.Shift | Keys.Z))) return true;
            if (settings.ContainsFocus && settings.IsCurrentCellInEditMode || ContainsTextEditorFocus(this)) return base.ProcessCmdKey(ref message, keyData);
            if (keyData == (Keys.Control | Keys.S)) { Attempt(SaveProfile); return true; }
            if (keyData == (Keys.Control | Keys.C)) { Attempt(CopySettings); return true; } if (keyData == (Keys.Control | Keys.V)) { Attempt(PasteSettings); return true; }
            if (keyData == (Keys.Control | Keys.Z)) { Attempt(Undo); return true; } if (keyData == (Keys.Control | Keys.Y) || keyData == (Keys.Control | Keys.Shift | Keys.Z)) { Attempt(Redo); return true; }
            return base.ProcessCmdKey(ref message, keyData);
        }
        sealed class DeviceItem
        {
            public readonly CollectionInfo Device;
            readonly int similarDeviceNumber;
            public DeviceItem(CollectionInfo value, int number = 0) { Device = value; similarDeviceNumber = number; }
            public override string ToString()
            {
                return (string.IsNullOrWhiteSpace(Device.product) ? Tr("USB-Gerät", "USB device") : Device.product) +
                    (TravelProtocols.Find(Device) == null ? string.Format(Tr(" · Diagnose ({0:X4}:{1:X4})", " · Diagnostics ({0:X4}:{1:X4})"), Device.vendorId, Device.productId) : " · USB") +
                    (similarDeviceNumber == 0 ? "" : string.Format(Tr(" · Gerät {0}", " · Device {0}"), similarDeviceNumber));
            }
        }
        sealed class ProfileItem { public string Name, Path; public override string ToString() { return Name; } }
        sealed class PresetItem { public SignalPreset Value; public override string ToString() { return "★ " + Value.Name; } }
        sealed class SettingOption
        {
            public string Value { get; private set; }
            public string Text { get { return UiText.Get(Value); } }
            public SettingOption(string value) { Value = value; }
        }
        sealed class TargetItem
        {
            public readonly OutputTarget Target;
            readonly Func<ControllerStyle> style;
            public TargetItem(OutputTarget target, Func<ControllerStyle> selectedStyle) { Target = target; style = selectedStyle; }
            public override string ToString() { return ControllerPresentation.Label(Target, style()); }
        }
    }
}
