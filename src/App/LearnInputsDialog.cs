using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Tk75.Mapping;

namespace Tk75.App
{
    public sealed class LearnInputDeviceChoice
    {
        public string Backend, Name, DeviceId;
        public Func<ILearnedInputDeviceSource> Open;
        public override string ToString() { return Name; }
    }

    // One detached transaction for a whole selection. No output or profile writes.
    public sealed class LearnInputsDialog : Form
    {
        readonly object gate = new object();
        readonly int[] targets;
        readonly string[] labels;
        readonly Func<LearnInputDeviceChoice[]> discover;
        readonly Func<bool> contextValid;
        readonly SleekComboBox devices = new SleekComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        readonly Label title = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        readonly Label instruction = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        readonly Label reading = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        readonly SleekProgressBar meter = new SleekProgressBar { Dock = DockStyle.Fill, Maximum = 1000 };
        readonly DataGridView queue = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
            AllowUserToDeleteRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, BorderStyle = BorderStyle.None };
        readonly Button back = Button("Zurück", "Back"), skip = Button("Überspringen", "Skip"), retry = Button("Erneut", "Try again"),
            finish = Button("Eingabe fertig", "Finish input"), apply = Button("Übernehmen", "Apply"), cancel = Button("Abbrechen", "Cancel");
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 50 };
        readonly Dictionary<int, LearnedKeyBinding> results = new Dictionary<int, LearnedKeyBinding>();
        readonly HashSet<int> skipped = new HashSet<int>();
        readonly HashSet<int> automatic = new HashSet<int>();
        readonly Queue<InputControlSample> pending = new Queue<InputControlSample>();
        ILearnedInputDeviceSource source;
        ILearnedInputDeviceSource openingSource;
        Action<InputControlSample> sourceHandler;
        InputLearningCapture capture;
        LearnInputDeviceChoice choice;
        int current;
        volatile int generation;
        volatile bool stopped;
        bool changingDevice, duplicate, overflow, contextLost;
        long captureTimestamp;
        string failure;
        public LearnedKeyBinding[] Bindings { get { return targets.Where(results.ContainsKey).Select(key => results[key]).ToArray(); } }
        public LearnInputsDialog(int[] keys, string[] keyLabels, int knownSkipped,
            Func<LearnInputDeviceChoice[]> discoverDevices, Func<bool> isContextValid)
        {
            if (keys == null || keyLabels == null || keys.Length == 0 || keys.Length != keyLabels.Length || keys.Distinct().Count() != keys.Length)
                throw new ArgumentException("A nonempty unique key selection and its labels are required.");
            if (keys.Any(key => (uint)key >= 256) || discoverDevices == null) throw new ArgumentException("Valid logical keys and a device provider are required.");
            targets = (int[])keys.Clone(); labels = (string[])keyLabels.Clone(); discover = discoverDevices; contextValid = isContextValid;
            Text = T("Unbekannte Eingaben lernen", "Learn unknown inputs"); ClientSize = new Size(680, 590); MinimumSize = new Size(620, 570);
            StartPosition = FormStartPosition.CenterParent; MinimizeBox = false; MaximizeBox = false; KeyPreview = true;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 9 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int height in new[] { 36, 40, 62, 28, 12, 12 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            layout.Controls.Add(devices, 0, 0); layout.Controls.Add(title, 0, 1); layout.Controls.Add(instruction, 0, 2);
            layout.Controls.Add(reading, 0, 3); layout.Controls.Add(meter, 0, 4); layout.Controls.Add(queue, 0, 6);
            queue.Columns.Add("Key", T("Taste", "Key")); queue.Columns.Add("Input", T("Erkannte Eingabe", "Detected input"));
            queue.Columns[0].FillWeight = 25; queue.Columns[1].FillWeight = 75;
            foreach (DataGridViewColumn column in queue.Columns) column.SortMode = DataGridViewColumnSortMode.NotSortable;
            foreach (string label in labels) queue.Rows.Add(label, T("Wartet", "Waiting"));
            var steps = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            foreach (Button button in new[] { back, skip, retry, finish }) steps.Controls.Add(button);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
            actions.Controls.Add(apply); actions.Controls.Add(cancel); apply.Tag = "primary";
            layout.Controls.Add(steps, 0, 7); layout.Controls.Add(actions, 0, 8); Controls.Add(layout);
            cancel.DialogResult = DialogResult.Cancel; // Escape is captured input, not an accidental Cancel shortcut.
            title.Font = new Font(Font, FontStyle.Bold);
            instruction.Text = knownSkipped == 0 ? T("Eingabegerät auswählen. Bestehende Einstellungen bleiben erhalten.", "Choose an input device. Existing settings are preserved.") :
                String.Format(T("{0} bekannte Eingaben übersprungen. Eingabegerät auswählen.", "Skipped {0} known inputs. Choose an input device."), knownSkipped);
            foreach (Control control in new Control[] { this, devices, title, instruction, reading, back, skip, retry, finish, apply, cancel }) UiText.PreserveText(control);
            ModernTheme.Apply(this); UiText.Apply(this);
            devices.SelectedIndexChanged += delegate { if (!changingDevice) OpenDevice(); };
            back.Click += delegate { MoveBack(); };
            skip.Click += delegate { if (current < targets.Length) { results.Remove(targets[current]); skipped.Add(targets[current]); Next(); } };
            retry.Click += delegate { if (source == null || !source.IsReading) OpenDevice(); else if (current < targets.Length) { results.Remove(targets[current]); skipped.Remove(targets[current]); UpdateRow(targets[current]); BeginCapture(); } };
            finish.Click += delegate { RefreshCapture(); if (capture != null && capture.CanFinish) capture.Finish(HidLearningSource.TimestampMilliseconds); RefreshCapture(); };
            apply.Click += delegate { if (ReadyToApply()) { DialogResult = DialogResult.OK; Close(); } };
            timer.Tick += delegate { RefreshCapture(); };
            Shown += delegate { timer.Start(); Discover(); };
            FormClosed += delegate { Stop(); };
            UpdateButtons();
        }
        static string T(string de, string en) { return UiText.Get(de, en); }
        static Button Button(string de, string en)
        { return new SleekButton { Text = T(de, en), Size = new Size(128, 34), Margin = new Padding(0, 6, 7, 0), AutoSize = false }; }
        bool Post(Action action)
        {
            if (stopped || !IsHandleCreated || IsDisposed) return false;
            try { BeginInvoke(action); return true; } catch (InvalidOperationException) { return false; }
        }
        void Discover()
        {
            SetText(reading, T("Geräte werden gesucht …", "Finding devices …"));
            ThreadPool.QueueUserWorkItem(delegate {
                LearnInputDeviceChoice[] found = null; string error = null;
                try { found = discover(); } catch (Exception ex) { error = ex.Message; }
                Post(delegate {
                    if (stopped) return;
                    if (error != null) { failure = error; RefreshCapture(); return; }
                    changingDevice = true; devices.Items.Clear(); devices.Items.AddRange(found ?? new LearnInputDeviceChoice[0]); changingDevice = false;
                    if (devices.Items.Count != 0) devices.SelectedIndex = 0;
                    else { failure = T("Keine unterstützte Eingabe gefunden. Gerät verbinden und diesen Dialog erneut öffnen.", "No supported input found. Connect a device and reopen this dialog."); RefreshCapture(); }
                });
            });
        }
        void OpenDevice()
        {
            if (stopped || contextLost) return;
            choice = devices.SelectedItem as LearnInputDeviceChoice; if (choice == null) return;
            ++generation; int request = generation; var selected = choice;
            DropSource(); results.Clear(); skipped.Clear(); automatic.Clear(); current = 0; failure = null; duplicate = false; capture = null;
            for (int i = 0; i < queue.Rows.Count; i++) queue.Rows[i].Cells[1].Value = T("Wartet", "Waiting");
            SetText(reading, T("Eingabegerät wird verbunden …", "Connecting input device …")); UpdateButtons();
            ThreadPool.QueueUserWorkItem(delegate {
                ILearnedInputDeviceSource opened = null; string error = null;
                try { opened = selected.Open(); } catch (Exception ex) { error = ex.Message; }
                var value = opened;
                lock (gate)
                {
                    if (!stopped && request == generation) openingSource = value;
                    else { DisposeSource(value); return; }
                }
                if (!Post(delegate {
                    lock (gate)
                    {
                        if (stopped || request != generation || !object.ReferenceEquals(openingSource, value)) return;
                        openingSource = null;
                    }
                    if (error != null) { failure = error; RefreshCapture(); return; }
                    source = value;
                    if (source == null) { failure = T("Eingabe nicht verfügbar.", "Input unavailable."); RefreshCapture(); return; }
                    if (!string.Equals(source.DeviceId, selected.DeviceId, StringComparison.Ordinal))
                    { failure = T("Das ausgewählte Gerät hat sich geändert. Bitte erneut auswählen.", "The selected device changed. Please select it again."); DropSource(); RefreshCapture(); return; }
                    sourceHandler = delegate(InputControlSample sample) { Receive(value, request, sample); };
                    source.Sample += sourceHandler;
                    // Standard keys already identify their logical destination.
                    foreach (var control in source.Controls)
                    {
                        if (!control.KnownKeyIndex.HasValue || !targets.Contains(control.KnownKeyIndex.Value) || control.Kind != InputControlKind.Button) continue;
                        int key = control.KnownKeyIndex.Value;
                        double rest = control.Neutral ?? control.LogicalMinimum;
                        double active = rest == control.LogicalMinimum ? control.LogicalMaximum : control.LogicalMinimum;
                        results[key] = MakeBinding(key, new LearnedInputRoute(source.DeviceId, control.ControlId, control.Kind,
                            control.LogicalMinimum, control.LogicalMaximum, rest, active, active > rest ? 1 : -1, null)); automatic.Add(key); UpdateRow(key);
                    }
                    NextUnfinished();
                }))
                {
                    lock (gate) { if (object.ReferenceEquals(openingSource, value)) { openingSource = null; DisposeSource(value); } }
                }
            });
        }
        void Receive(ILearnedInputDeviceSource sender, int request, InputControlSample sample)
        {
            lock (gate)
            {
                if (stopped || sample == null || sample.TimestampMilliseconds < 0 || request != generation || !object.ReferenceEquals(source, sender)) return;
                if (pending.Count >= 8192) { overflow = true; return; }
                // The source stamped this event before entering our queue. A UI
                // timer may have advanced the capture clock in the meantime.
                // Retain event order without discarding a press at that boundary.
                long time = Math.Max(sample.TimestampMilliseconds, captureTimestamp);
                captureTimestamp = time;
                pending.Enqueue(new InputControlSample(sample.ControlId, sample.Value, time));
            }
        }
        InputControlSample[] InitialSamples()
        {
            var samples = new List<InputControlSample>(); long now = HidLearningSource.TimestampMilliseconds;
            foreach (var control in source.Controls)
            { double value; if (source.TryGetValue(control.ControlId, out value)) samples.Add(new InputControlSample(control.ControlId, value, now)); }
            return samples.ToArray();
        }
        void BeginCapture()
        {
            if (contextLost) return;
            failure = null; duplicate = false;
            if (source == null || current >= targets.Length) { capture = null; UpdateButtons(); return; }
            lock (gate)
            {
                pending.Clear(); overflow = false;
                var initial = InitialSamples(); captureTimestamp = HidLearningSource.TimestampMilliseconds;
                capture = new InputLearningCapture(source.DeviceId, source.Controls, initial, captureTimestamp);
            }
            queue.ClearSelection(); queue.Rows[current].Selected = true; queue.FirstDisplayedScrollingRowIndex = current;
            RefreshCapture();
        }
        void Next()
        {
            if (current < targets.Length) UpdateRow(targets[current]);
            ++current; NextUnfinished();
        }
        void NextUnfinished()
        {
            while (current < targets.Length && (automatic.Contains(targets[current]) || results.ContainsKey(targets[current]) || skipped.Contains(targets[current]))) ++current;
            BeginCapture();
        }
        void MoveBack()
        {
            int previous = current - 1;
            while (previous >= 0 && automatic.Contains(targets[previous])) --previous;
            if (previous < 0) return;
            current = previous; results.Remove(targets[current]); skipped.Remove(targets[current]); UpdateRow(targets[current]); BeginCapture();
        }
        void UpdateRow(int key)
        {
            int index = Array.IndexOf(targets, key); LearnedKeyBinding binding;
            queue.Rows[index].Cells[1].Value = results.TryGetValue(key, out binding) ?
                BindingSummary(binding) + (automatic.Contains(key) ? T(" · automatisch", " · automatic") : "") :
                skipped.Contains(key) ? T("Übersprungen", "Skipped") : T("Wartet", "Waiting");
        }
        string BindingSummary(LearnedKeyBinding binding)
        {
            var control = source == null ? null : source.Controls.FirstOrDefault(item => item.ControlId == binding.ControlId);
            string name = control == null ? binding.ControlId : control.Label;
            var kind = (InputControlKind)binding.Kind;
            string direction = kind == InputControlKind.Absolute ? binding.Direction > 0 ? T(" · steigend", " · increasing") : T(" · fallend", " · decreasing") :
                kind == InputControlKind.Relative ? binding.Direction > 0 ? " +" : " −" : kind == InputControlKind.Hat ? " " + binding.HatValue : "";
            return name + " · " + KindName(kind) + direction;
        }
        void RefreshCapture()
        {
            if (stopped) return;
            if (contextValid != null && !contextValid()) { contextLost = true; failure = T("Auswahl oder Profil geändert. Lernen erneut starten.", "Selection or profile changed. Start learning again."); capture = null; }
            if (source != null && !source.IsReading) { failure = T("Verbindung verloren. Gerät erneut auswählen; bisherige Zuordnungen wurden nicht gespeichert.", "Connection lost. Select the device again; pending assignments have not been saved."); capture = null; }
            if (failure != null) { SetText(instruction, failure); UpdateButtons(); return; }
            if (source == null) { UpdateButtons(); return; }
            if (current >= targets.Length)
            {
                SetText(title, String.Format(T("{0} Eingaben bereit", "{0} inputs ready"), results.Count));
                SetText(instruction, T("Zuordnungen prüfen und gemeinsam übernehmen. Übersprungene und bereits bekannte Tasten bleiben unverändert.", "Review and apply these assignments together. Skipped and previously known keys keep their settings."));
                SetText(reading, T("Noch nicht gespeichert", "Not saved yet")); meter.Value = 0; UpdateButtons(); return;
            }
            SetText(title, String.Format(T("{0} · {1} von {2}", "{0} · {1} of {2}"), labels[current], current + 1, targets.Length));
            if (capture == null || duplicate) { UpdateButtons(); return; }
            lock (gate)
            {
                if (overflow) { failure = T("Zu viele gleichzeitige Signale. Bitte erneut versuchen.", "Too many simultaneous signals. Please try again."); pending.Clear(); }
                while (failure == null && pending.Count != 0) capture.Feed(pending.Dequeue());
                captureTimestamp = Math.Max(captureTimestamp, HidLearningSource.TimestampMilliseconds);
                if (failure == null) capture.Advance(captureTimestamp);
            }
            if (failure != null) { SetText(instruction, failure); UpdateButtons(); return; }
            if (capture.State == InputLearningCaptureState.Completed)
            {
                var route = capture.Result;
                var used = results.Values.FirstOrDefault(item => item.KeyIndex != targets[current] && item.SourceDeviceId == route.SourceDeviceId && item.ControlId == route.ControlId && item.Direction == route.Direction && item.HatValue == route.HatValue);
                if (used != null)
                {
                    duplicate = true; SetText(instruction, String.Format(T("Diese Eingabe gehört bereits zu {0}. Erneut versuchen oder überspringen.", "This input is already assigned to {0}. Try again or skip."), labels[Array.IndexOf(targets, used.KeyIndex)]));
                }
                else { skipped.Remove(targets[current]); results[targets[current]] = MakeBinding(targets[current], route); Next(); return; }
            }
            else SetText(instruction, capture.State == InputLearningCaptureState.Ambiguous ? T("Mehrere Eingaben erkannt. Erneut versuchen und nur die gewünschte Eingabe bewegen.", "Several inputs detected. Try again and move only the desired input.") :
                capture.State == InputLearningCaptureState.WaitingForNeutral ? T("Eingaben loslassen bzw. in Ruhe bringen. Dann die gewünschte Eingabe bewegen.", "Release or rest the controls. Then move the input you want to assign.") :
                capture.State == InputLearningCaptureState.Capturing ? T("Erkannt. Loslassen bzw. zur Ruheposition zurückkehren. Bei Drehrädern oder Schaltern: „Eingabe fertig“.", "Detected. Release or return to rest. For wheels or switches, choose “Finish input”.") :
                T("Gewünschte Taste drücken oder Regler bewegen, dann loslassen. Das Tempo bestimmst du.", "Press the desired button or move a control, then release. Go at your own pace."));
            var active = capture.Control;
            SetText(reading, active == null ? source.DisplayName : active.Label + " · " + KindName(active.Kind) + " · " + capture.LiveValue.ToString("P0"));
            meter.Value = Math.Max(0, Math.Min(1000, (int)Math.Round(capture.LiveValue * 1000))); UpdateButtons();
        }
        LearnedKeyBinding MakeBinding(int key, LearnedInputRoute route)
        {
            int physical;
            return new LearnedKeyBinding { KeyIndex = key, Backend = choice.Backend, SourceDeviceId = route.SourceDeviceId,
                SourceName = string.IsNullOrWhiteSpace(source.DisplayName) ? choice.Name : source.DisplayName.Length > 128 ? source.DisplayName.Substring(0, 128) : source.DisplayName,
                ControlId = route.ControlId, SourceKeyIndex = choice.Backend == "travel" && route.ControlId.StartsWith("key:", StringComparison.Ordinal) && Int32.TryParse(route.ControlId.Substring(4), out physical) ? (int?)physical : null,
                Kind = (int)route.Kind, Minimum = route.LogicalMinimum, Maximum = route.LogicalMaximum, Rest = route.Rest,
                Active = route.Active, Direction = route.Direction, HatValue = route.HatValue };
        }
        static string KindName(InputControlKind kind)
        { return kind == InputControlKind.Button ? T("Ein/Aus", "On/off") : kind == InputControlKind.Relative ? T("Schritte", "Steps") : kind == InputControlKind.Hat ? T("Richtung", "Direction") : T("Analog", "Analog"); }
        static void SetText(Control control, string text) { if (control.Text != text) control.Text = text; }
        bool ReadyToApply() { return failure == null && source != null && source.IsReading && current >= targets.Length && results.Count != 0 && (contextValid == null || contextValid()); }
        void UpdateButtons()
        {
            bool active = source != null && source.IsReading && failure == null;
            back.Enabled = active && Enumerable.Range(0, Math.Min(current, targets.Length)).Any(i => !automatic.Contains(targets[i]));
            skip.Enabled = active && current < targets.Length; retry.Enabled = !contextLost && choice != null && !changingDevice && (failure != null || current < targets.Length);
            finish.Visible = capture != null && capture.CanFinish; finish.Enabled = active && capture != null && capture.CanFinish;
            apply.Enabled = ReadyToApply();
        }
        public ILearnedInputDeviceSource TakeSource()
        { return DialogResult == DialogResult.OK ? DetachSource() : null; }
        ILearnedInputDeviceSource DetachSource()
        {
            var value = source;
            if (value != null && sourceHandler != null) value.Sample -= sourceHandler;
            sourceHandler = null;
            source = null; return value;
        }
        static void DisposeSource(ILearnedInputDeviceSource value)
        { if (value != null) ThreadPool.QueueUserWorkItem(delegate { try { value.Dispose(); } catch (Exception) { } }); }
        void DropSource()
        {
            DisposeSource(DetachSource());
            lock (gate) { DisposeSource(openingSource); openingSource = null; pending.Clear(); overflow = false; }
        }
        void Stop()
        {
            if (stopped) return; stopped = true; ++generation; timer.Stop(); timer.Dispose();
            // Keep the selected source until the caller takes it after OK.
            if (DialogResult != DialogResult.OK) DropSource();
        }
        protected override void Dispose(bool disposing)
        { if (disposing) { Stop(); DropSource(); } base.Dispose(disposing); }
        protected override bool ProcessDialogKey(Keys keyData)
        {
            // A keyboard being learned must not activate navigation/default buttons.
            if (CapturingKeyboardActions) return true;
            return base.ProcessDialogKey(keyData);
        }
        bool CapturingKeyboardActions { get { return source != null && current < targets.Length && !contextLost; } }
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        { return CapturingKeyboardActions || base.ProcessCmdKey(ref msg, keyData); }
        protected override void OnKeyDown(KeyEventArgs e)
        { if (CapturingKeyboardActions) { e.Handled = e.SuppressKeyPress = true; } else base.OnKeyDown(e); }
    }
}
