using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Tk75.Mapping;

namespace Tk75.App
{
    public sealed partial class MainForm
    {
        readonly Panel detailsHost = new Panel { Dock = DockStyle.Fill };
        Control keyCard;
        readonly Label detailsTitle = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Tag = "muted", Margin = new Padding(6, 0, 6, 0) };
        readonly Panel controllerPanel = new SleekCard { Dock = DockStyle.Fill, Padding = new Padding(8) };
        readonly Panel keyBehaviorPanel = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        readonly Panel keySocdPanel = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        readonly Panel keyCardScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
        readonly ControllerPreview controllerPreview = new ControllerPreview { Dock = DockStyle.Fill, CompactStatus = true };
        readonly Panel controllerHeading = new Panel { Dock = DockStyle.Fill };
        readonly List<ContextMenuStrip> languageMenus = new List<ContextMenuStrip>();
        readonly CheckBox rapidTrigger = new CheckBox { AutoSize = true };
        readonly NumericUpDown actuationPoint = PercentInput(), releaseMovement = PercentInput();
        readonly SleekComboBox oppositeKey = new SleekComboBox(), oppositeMode = new SleekComboBox();
        readonly Label keyBehaviorStatus = new LiveValueLabel { Dock = DockStyle.Fill, Tag = "muted", AutoEllipsis = true, Margin = new Padding(3, 0, 3, 0) };
        readonly Button applyKeyBehavior = new SleekButton { Size = new Size(140, 38), Margin = new Padding(3, 3, 9, 3), Tag = "primary" };
        readonly Button resetKeyBehavior = new SleekButton { Size = new Size(140, 38), Margin = new Padding(3) };
        Button controllerToggle, keyBehaviorToggle, keySettingsToggle;
        string detailsMode;
        bool updatingInput, inputDirty, applyingOppositeSelection;
        int? legendOverride;
        int[] editingInputKeys = new int[0];
        InputActivationFields inputFieldsDirty;
        bool inputPairingDirty;

        static NumericUpDown PercentInput()
        { return new NumericUpDown { DecimalPlaces = 2, Minimum = 0.01M, Maximum = 100M, Increment = 0.1M, Dock = DockStyle.Fill, TextAlign = HorizontalAlignment.Right }; }
        static string Tr(string german, string english) { return UiText.Get(german, english); }
        static bool ContainsTextEditorFocus(Control parent)
        {
            foreach (Control child in parent.Controls)
            { if (child is TextBoxBase && child.Focused) return true; if (child.ContainsFocus && ContainsTextEditorFocus(child)) return true; }
            return false;
        }
        void SwitchLanguage(string language)
        {
            settings.EndEdit(); FlushInputDraft();
            UiPreferences.SaveLanguage(store.Root, language); UiText.SetLanguage(language);
            RefreshSettings(); RefreshBindings(); RefreshKeyBehaviorAnnotations(); UpdateDetailsButtons();
            SetInputTooltips();
            UiText.Apply(this); foreach (var menu in languageMenus) UiText.Apply(menu);
            UpdateDetailsTitle();
            foreach (Control control in Descendants(this)) if (control is ComboBox) control.Invalidate();
            keyboard.RefreshLanguage(); curve.Invalidate(); controllerPreview.Invalidate();
            SyncOutputMode();
            RefreshInputModeUi();
        }
        void AddLanguageMenu(ContextMenuStrip menu)
        {
            var languages = new ToolStripMenuItem(Tr("Sprache / Language", "Language / Sprache"));
            languages.DropDownItems.Add("Deutsch", null, delegate { Attempt(delegate { SwitchLanguage("de"); }); });
            languages.DropDownItems.Add("English", null, delegate { Attempt(delegate { SwitchLanguage("en"); }); }); menu.Items.Add(languages);
        }
        void SetLegendOverride(int? choice)
        { legendOverride = choice; ApplyLegendForLayout(); RefreshKeys(); RefreshBindings(); }
        void ApplyLegendForLayout()
        {
            bool iso = layoutMode.SelectedIndex == 2 || layoutMode.SelectedIndex == 0 && automaticIso;
            keyboard.LegendStyle = legendOverride.HasValue ? legendOverride.Value == 0 ? KeyboardLegendStyle.Qwerty : KeyboardLegendStyle.Qwertz : iso ? KeyboardLegendStyle.Qwertz : KeyboardLegendStyle.Qwerty;
        }
        void BuildDetailAreas()
        {
            keyCard = BuildKeyCard(); detailsHost.Controls.Add(keyCard);
            controllerPanel.Controls.Add(controllerPreview); detailsHost.Controls.Add(controllerPanel);
            BuildControllerMapping();
            controllerNameEdit.AutoSize = false; controllerNameEdit.MaximumSize = Size.Empty; controllerNameEdit.Dock = DockStyle.Fill; controllerNameEdit.TextAlign = ContentAlignment.MiddleLeft;
            controllerSetup.AutoSize = false; controllerSetup.Dock = DockStyle.Right; controllerSetup.Width = 148; controllerSetup.TextAlign = ContentAlignment.MiddleRight;
            controllerHeading.Controls.Add(controllerNameEdit); controllerHeading.Controls.Add(controllerSetup); controllerHeading.Visible = false;
            BuildKeyBehavior(); detailsHost.Controls.Add(advancedPanel);
            controllerPanel.Visible = advancedPanel.Visible = false;
            UiText.PreserveText(detailsTitle);
        }
        void ChooseInitialDetails()
        { SetDetailMode(null, false); }
        void SetDetailMode(string mode, bool chosen, bool activate = true)
        {
            if (deviceDetachInProgress || closing) return;
            if (mode != "input" && socdCapture != null) CancelSocdCapture(false);
            if (mode != "advanced" && inputThresholdCaptureReader != null) CancelInputThresholdCapture();
            if (mode != null && mode != "input" && pressureCaptureReader != null)
            { CancelPressureCapture(); RefreshPressureRangeEditor(); }
            if (mode != detailsMode) { settings.EndEdit(); FlushInputDraft(); }
            detailsMode = mode; advancedVisible = mode == "advanced";
            controllerHeading.Visible = mode == "controller"; detailsTitle.Visible = mode != "controller";
            if (mode == "controller") controllerHeading.BringToFront(); else detailsTitle.BringToFront();
            // Swap only the contents of the fixed right-hand work area.
            // No new window or focus transfer can interrupt an input gesture.
            detailsHost.SuspendLayout();
            try
            {
                keyCard.Visible = mode == null || mode == "input";
                controllerPanel.Visible = mode == "controller"; advancedPanel.Visible = advancedVisible;
                if (mode == "controller") controllerPanel.BringToFront();
                else if (mode == "input") { keyCard.BringToFront(); RefreshInputEditor(); }
                else if (advancedVisible) { advancedPanel.BringToFront(); curveEditorScroll.AutoScrollPosition = Point.Empty; }
                else keyCard.BringToFront();
                if (mode == null || mode == "input") keyCardScroll.AutoScrollPosition = Point.Empty;
            }
            finally { detailsHost.ResumeLayout(true); }
            UpdateDetailsButtons(); UpdateDetailsTitle();
            NativeSurfaceTheme.RefreshLayout(detailsHost);
        }
        void ScrollKeySettingsTo(Control target)
        {
            if (target == null || target.IsDisposed || !keyCard.Visible) return;
            keyCardScroll.ScrollControlIntoView(target);
            NativeSurfaceTheme.RefreshLayout(keyCardScroll);
        }
        void UpdateDetailsTitle()
        {
            string title = detailsMode == "controller" ? ControllerDisplayName : detailsMode == "advanced" ? Tr("Feinabstimmung", "Fine tuning") : Tr("Tasteneinstellungen", "Key settings");
            int[] selected = SelectedKeys();
            if (detailsMode != "controller" && selected.Length == 1) title += " · " + Label(selected[0]);
            else if (detailsMode != "controller" && selected.Length > 1) title += string.Format(Tr(" · {0} Tasten", " · {0} keys"), selected.Length);
            if (detailsTitle.Text != title) detailsTitle.Text = title;
        }
        void UpdateDetailsButtons()
        {
            if (controllerToggle == null) return;
            controllerToggle.Text = Tr("Controller", "Controller"); keyBehaviorToggle.Text = Tr("Gegentasten", "Opposite keys"); advancedToggle.Text = Tr("Kurve", "Curve"); keySettingsToggle.Text = Tr("Tasten", "Keys");
            ((DetailTabButton)controllerToggle).IsSelected = detailsMode == "controller";
            if (detailsMode == "input") ModernTheme.Primary(keyBehaviorToggle); else ModernTheme.Secondary(keyBehaviorToggle);
            ((DetailTabButton)advancedToggle).IsSelected = detailsMode == "advanced";
            ((DetailTabButton)keySettingsToggle).IsSelected = detailsMode == null || detailsMode == "input";
        }
        Control DetailButtonBar()
        {
            var bar = new DetailTabStrip { Dock = DockStyle.Fill, AccessibleName = Tr("Einstellungen", "Settings") };
            keySettingsToggle = new DetailTabButton();
            advancedToggle = new DetailTabButton();
            controllerToggle = new DetailTabButton();
            keySettingsToggle.Click += delegate { Attempt(delegate { SetDetailMode(null, true); }); };
            advancedToggle.Click += delegate { Attempt(delegate { SetDetailMode("advanced", true); }); };
            controllerToggle.Click += delegate { Attempt(delegate { SetDetailMode("controller", true); }); };
            bar.Controls.Add(keySettingsToggle); bar.Controls.Add(advancedToggle); bar.Controls.Add(controllerToggle); BuildSocdDragTabs(); UpdateDetailsButtons(); return bar;
        }
        void BuildKeyBehavior()
        {
            UiText.PreserveText(keyBehaviorStatus);
            var thresholds = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, Margin = Padding.Empty };
            thresholds.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); thresholds.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            thresholds.RowStyles.Add(new RowStyle(SizeType.Absolute, 22)); thresholds.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            thresholds.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); thresholds.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            keyBehaviorPanel.Controls.Add(thresholds);
            thresholds.Controls.Add(keyBehaviorStatus, 0, 0); thresholds.SetColumnSpan(keyBehaviorStatus, 2);
            rapidTrigger.Text = Tr("Schnell erneut auslösen", "Rapid retrigger"); rapidTrigger.Margin = new Padding(3, 0, 3, 0); thresholds.Controls.Add(rapidTrigger, 0, 1); thresholds.SetColumnSpan(rapidTrigger, 2);
            thresholds.Controls.Add(BuildInputThresholdField(Tr("Auslösen %", "Actuation %"), actuationPoint, actuationSlider, calibrateActuation, InputActivationFields.Actuation), 0, 2);
            thresholds.Controls.Add(BuildInputThresholdField(Tr("Loslassen %", "Release %"), releaseMovement, releaseSlider, calibrateRelease, InputActivationFields.Release), 1, 2);
            applyKeyBehavior.Text = Tr("Übernehmen", "Apply"); resetKeyBehavior.Text = Tr("Zurücksetzen", "Reset");
            var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            foreach (Button action in new[] { applyKeyBehavior, resetKeyBehavior }) { action.Dock = DockStyle.Fill; action.MinimumSize = Size.Empty; action.Margin = new Padding(3, 2, 3, 0); action.Padding = Padding.Empty; }
            actions.Controls.Add(applyKeyBehavior, 0, 0); actions.Controls.Add(resetKeyBehavior, 1, 0); thresholds.Controls.Add(actions, 0, 3); thresholds.SetColumnSpan(actions, 2);

            var form = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 4, Margin = Padding.Empty };
            form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34)); form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33)); form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
            foreach (int height in new[] { 26, 36, 40, 36 }) form.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            keySocdPanel.Controls.Add(form);
            var oppositeCaption = Caption(Tr("Gegenrichtungen (SOCD)", "Opposite directions (SOCD)"), 26); form.Controls.Add(oppositeCaption, 0, 0); form.SetColumnSpan(oppositeCaption, 3);
            oppositeKey.Dock = oppositeMode.Dock = DockStyle.Fill; oppositeKey.DropDownWidth = 240;
            form.Controls.Add(oppositeKey, 0, 1); form.Controls.Add(oppositeMode, 1, 1); form.SetColumnSpan(oppositeMode, 2);
            oppositeMode.Items.Add(new OppositeModeItem(InputOpposedPolicy.Neutral)); oppositeMode.Items.Add(new OppositeModeItem(InputOpposedPolicy.LastPressed)); oppositeMode.Items.Add(new OppositeModeItem(InputOpposedPolicy.FirstPressed)); oppositeMode.SelectedIndex = 0;
            BuildSocdDragUi(form, 2);
            var explanation = Caption(Tr("Nur echte Tastendrücke. Das gewählte Tastenpaar wird gemeinsam geregelt.", "Physical key presses only. The selected pair shares one opposite-direction rule."), 36); explanation.Tag = "muted"; form.Controls.Add(explanation, 0, 3); form.SetColumnSpan(explanation, 3);
            applyKeyBehavior.Click += delegate { Attempt(SaveKeyBehavior); }; resetKeyBehavior.Click += delegate { Attempt(ResetKeyBehavior); };
            rapidTrigger.CheckStateChanged += delegate { if (!updatingInput) { MarkInputDirty(InputActivationFields.RapidTrigger); SetInputFieldAvailability(); RefreshCurveInputPreview(); } };
            oppositeKey.SelectedIndexChanged += delegate { ApplyOppositeSelection(); };
            oppositeMode.SelectedIndexChanged += delegate { if (oppositeMode.Enabled) ApplyOppositeSelection(); };
            actuationPoint.ValueChanged += delegate { MarkInputDirty(InputActivationFields.Actuation); if (!updatingInput) PreviewCurveInputOption(InputActivationFields.Actuation, (double)actuationPoint.Value); };
            releaseMovement.ValueChanged += delegate { MarkInputDirty(InputActivationFields.Release); if (!updatingInput) PreviewCurveInputOption(InputActivationFields.Release, (double)releaseMovement.Value); };
            // Retyping the displayed first value is still an explicit bulk edit
            // when other selected keys had different values.
            actuationPoint.TextChanged += delegate { if (actuationPoint.ContainsFocus) MarkInputDirty(InputActivationFields.Actuation); };
            releaseMovement.TextChanged += delegate { if (releaseMovement.ContainsFocus) MarkInputDirty(InputActivationFields.Release); };
            SetInputTooltips();
            BuildCurveInputPreviewEvents();
            Disposed += delegate { CancelInputThresholdCapture(); };
        }
        void SetInputTooltips()
        {
            RefreshSocdCaptureUi();
            keyCardTips.SetToolTip(rapidTrigger, Tr("Nach dem ersten Auslösen reicht ein kurzes Loslassen und erneutes Drücken. Es entstehen keine automatischen Wiederholungen.", "After the first actuation, a small release and renewed press can retrigger. This never generates automatic repeats."));
            keyCardTips.SetToolTip(actuationPoint, Tr("Bis zu diesem Punkt drücken, um auszulösen. Rapid Trigger nutzt denselben Weg zum erneuten Drücken nach dem Loslassen.", "Press to this point to activate. Rapid Trigger uses the same amount of travel to press again after release."));
            keyCardTips.SetToolTip(releaseMovement, Tr("Wie weit du nach einem Druck loslassen musst, damit die Taste wieder frei ist.", "How far you release from the deepest press before the key becomes inactive."));
            keyCardTips.SetToolTip(applyKeyBehavior, Tr("Jetzt übernehmen. Beim Tastenwechsel oder Speichern werden offene Änderungen ebenfalls übernommen; Strg+Z macht sie rückgängig.", "Apply now. Changing keys or saving also applies pending edits; Ctrl+Z undoes them."));
            keyCardTips.SetToolTip(oppositeKey, Tr("Eine Taste: Gegenpart wählen. Zwei Tasten: das ausgewählte Paar verbinden. Bei größerer Auswahl bleiben vorhandene Paare erhalten. Die Auswahl wird sofort übernommen; Strg+Z macht sie rückgängig.", "One key: choose its opposite. Two keys: pair the selected keys. Larger selections preserve existing pairs. The choice applies immediately; Ctrl+Z undoes it."));
            keyCardTips.SetToolTip(oppositeMode, Tr("Wenn beide Gegentasten gedrückt sind: beide neutral halten, die zuletzt gedrückte oder die zuerst gedrückte Taste bevorzugen. Die Regel wird sofort für das Paar übernommen; Strg+Z macht sie rückgängig.", "When both opposite keys are held: keep both neutral, prefer the last pressed key, or prefer the first pressed key. The rule applies to the pair immediately; Ctrl+Z undoes it."));
            keyCardTips.SetToolTip(resetKeyBehavior, Tr("Entfernt das eigene Verhalten aller ausgewählten Tasten und löst ihre Gegenrichtungs-Paare. Die Druckparameter anderer Tasten bleiben erhalten.", "Removes custom behavior from every selected key and unpairs their opposites. Other keys keep their pressure settings."));
            RefreshInputThresholdTooltips();
        }
        static Control PercentField(string label, NumericUpDown input)
        {
            var field = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(3, 0, 8, 0) };
            field.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); field.RowStyles.Add(new RowStyle(SizeType.Absolute, 26)); field.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            field.Controls.Add(Caption(label, 26), 0, 0); field.Controls.Add(input, 0, 1); return field;
        }
        void RefreshInputEditor()
        {
            if (oppositeMode.Items.Count == 0) return;
            CancelInputThresholdCapture(); CancelInputThresholdGestures();
            // Flush against the captured previous selection before replacing it.
            // SelectionChanged has already updated SelectedKeys at this point.
            FlushInputDraft();
            int[] selected = SelectedKeys();
            updatingInput = true;
            try
            {
                editingInputKeys = selected; inputFieldsDirty = InputActivationFields.None; inputPairingDirty = false;
                var profile = history.Current;
                var values = selected.Select(index => KeyInputEditing.GetOrDefault(profile, index)).ToArray();
                var value = values.Length == 0 ? new KeyInputSettings() : values[0];
                bool configured = profile.Inputs.Any(i => selected.Contains(i.KeyIndex));
                bool mixed = values.Skip(1).Any(other => other.RapidTriggerEnabled != value.RapidTriggerEnabled || other.ActuationPoint != value.ActuationPoint ||
                    other.ReleaseMovement != value.ReleaseMovement);
                SetInputStatus(selected.Length == 0 ? Tr("Wähle Tasten für ihr Verhalten.", "Select keys to configure their behavior.") : InputSelectionLabel() + " · " +
                    (mixed ? Tr("Gemischt · Änderungen gelten für alle", "Mixed · changes apply to all") : configured ? Tr("Eigene Einstellung", "Custom behavior") : Tr("Standardverhalten", "Default behavior")));
                oppositeKey.Items.Clear();
                if (selected.Length > 1) oppositeKey.Items.Add(new OppositeKeyItem(null, Tr("Bestehende Paare beibehalten", "Keep existing pairs"), true));
                if (selected.Length <= 2) oppositeKey.Items.Add(new OppositeKeyItem(null, Tr("Kein Gegenpart", "No opposite key")));
                if (selected.Length == 2)
                    oppositeKey.Items.Add(new OppositeKeyItem(selected[1], Label(selected[0]) + " ↔ " + Label(selected[1])));
                else if (selected.Length == 1)
                {
                    var candidates = LayoutIndices().Concat(profile.Bindings.Select(b => b.KeyIndex)).Concat(profile.Inputs.Select(i => i.KeyIndex)).Concat(reader == null ? Enumerable.Empty<int>() : reader.GetSnapshot().Select(s => s.KeyIndex)).Concat(keymap == null ? Enumerable.Empty<int>() : keymap.Entries.Select(e => e.KeyIndex)).Distinct().Where(i => i != selected[0]).OrderBy(i => Label(i), StringComparer.CurrentCultureIgnoreCase);
                    foreach (int candidate in candidates) oppositeKey.Items.Add(new OppositeKeyItem(candidate, Label(candidate)));
                }
                SetInputFields(value);
                if (values.Skip(1).Any(other => other.RapidTriggerEnabled != value.RapidTriggerEnabled)) rapidTrigger.CheckState = CheckState.Indeterminate;
                if (selected.Length == 2 && !(values[0].OppositeKeyIndex == selected[1] && values[1].OppositeKeyIndex == selected[0]) && values.Any(other => other.OppositeKeyIndex.HasValue))
                    oppositeKey.SelectedIndex = 0;
                rapidTrigger.Enabled = actuationPoint.Enabled = applyKeyBehavior.Enabled = selected.Length != 0;
                oppositeKey.Enabled = selected.Length == 1 || selected.Length == 2;
                resetKeyBehavior.Enabled = configured;
                SetInputTooltips();
                SetMixedInputTip(actuationPoint, values.Skip(1).Any(other => other.ActuationPoint != value.ActuationPoint));
                SetMixedInputTip(releaseMovement, values.Skip(1).Any(other => other.ReleaseMovement != value.ReleaseMovement));
                RefreshInputThresholdSliders(values);
            }
            finally { updatingInput = false; }
            SetInputFieldAvailability(); RefreshSocdDropTarget(); RefreshCurveInputPreview();
        }
        string InputSelectionLabel()
        { return editingInputKeys.Length == 1 ? string.Format(Tr("Taste {0}", "Key {0}"), Label(editingInputKeys[0])) : string.Format(Tr("{0} Tasten", "{0} keys"), editingInputKeys.Length); }
        void SetInputStatus(string text) { if (keyBehaviorStatus.Text == text) return; keyBehaviorStatus.Text = text; keyCardTips.SetToolTip(keyBehaviorStatus, text); }
        void SetMixedInputTip(Control control, bool mixed)
        { if (mixed) keyCardTips.SetToolTip(control, control.AccessibleDescription + Tr(" Gemischte Werte: angezeigt wird die erste Taste. Eine Änderung gilt für alle ausgewählten Tasten.", " Mixed values: the first key is shown. Editing applies to every selected key.")); }
        static decimal ToPercent(double value) { return Math.Max(0.01M, Math.Min(100M, (decimal)value * 100M)); }
        void SetInputFields(KeyInputSettings value)
        {
            rapidTrigger.Checked = value.RapidTriggerEnabled; actuationPoint.Value = ToPercent(value.ActuationPoint); releaseMovement.Value = ToPercent(value.ReleaseMovement);
            oppositeKey.SelectedIndex = 0;
            foreach (OppositeKeyItem candidate in oppositeKey.Items) if (!candidate.Preserve && candidate.Index == value.OppositeKeyIndex) oppositeKey.SelectedItem = candidate;
            foreach (OppositeModeItem item in oppositeMode.Items) if (item.Value == value.OppositePolicy) oppositeMode.SelectedItem = item;
        }
        void SetInputFieldAvailability()
        {
            releaseMovement.Enabled = rapidTrigger.Enabled && rapidTrigger.CheckState != CheckState.Unchecked;
            var pair = oppositeKey.SelectedItem as OppositeKeyItem; oppositeMode.Enabled = oppositeKey.Enabled && pair != null && !pair.Preserve && pair.Index.HasValue;
            RefreshInputThresholdAvailability();
            if (inputThresholdCaptureReader != null) oppositeKey.Enabled = oppositeMode.Enabled = false;
            else { oppositeKey.Enabled = editingInputKeys.Length == 1 || editingInputKeys.Length == 2; oppositeMode.Enabled = oppositeKey.Enabled && pair != null && !pair.Preserve && pair.Index.HasValue; }
        }
        void SaveKeyBehavior()
        {
            if (editingInputKeys.Length == 0) throw new InvalidOperationException(Tr("Mindestens eine Taste auswählen.", "Select at least one key."));
            if (!inputDirty) return;
            Commit(history.Current); RefreshInputEditor();
        }
        KeyInputSettings ReadInputDraft()
        {
            CancelInputThresholdGestures();
            var value = new KeyInputSettings { KeyIndex = editingInputKeys[0] };
            value.RapidTriggerEnabled = rapidTrigger.Checked; value.ActuationPoint = (double)actuationPoint.Value / 100; value.ReleaseMovement = (double)releaseMovement.Value / 100; value.PressMovement = value.ActuationPoint;
            var pair = oppositeKey.SelectedItem as OppositeKeyItem; value.OppositeKeyIndex = pair == null ? (int?)null : pair.Index;
            var mode = oppositeMode.SelectedItem as OppositeModeItem; value.OppositePolicy = mode == null ? InputOpposedPolicy.Neutral : mode.Value;
            return value;
        }
        void ResetKeyBehavior()
        {
            int[] selected = SelectedKeys(); if (selected.Length == 0) return;
            inputDirty = false; inputFieldsDirty = InputActivationFields.None; inputPairingDirty = false;
            Commit(KeyInputEditing.Remove(history.Current, selected)); RefreshInputEditor();
        }
        void MarkInputDirty(InputActivationFields fields)
        {
            if (updatingInput || editingInputKeys.Length == 0) return;
            if (!inputDirty) { inputFieldsDirty = InputActivationFields.None; inputPairingDirty = false; }
            inputFieldsDirty |= fields;
            inputDirty = true;
            SetInputStatus(InputSelectionLabel() + Tr(" · noch nicht übernommen", " · pending changes"));
        }
        void MarkInputPairingDirty()
        {
            if (updatingInput || editingInputKeys.Length == 0 || editingInputKeys.Length > 2) return;
            MarkInputDirty(InputActivationFields.None); inputPairingDirty = true;
        }
        void ApplyOppositeSelection()
        {
            if (updatingInput || applyingOppositeSelection || inputThresholdCaptureReader != null || socdCapture != null) return;
            SetInputFieldAvailability();
            var pair = oppositeKey.SelectedItem as OppositeKeyItem;
            if (pair == null || pair.Preserve) return;
            MarkInputPairingDirty();
            if (!inputPairingDirty) return;
            applyingOppositeSelection = true;
            try { Attempt(SaveKeyBehavior); }
            finally { applyingOppositeSelection = false; }
        }
        Profile MergePendingInput(Profile profile)
        {
            if (!inputDirty || editingInputKeys.Length == 0) return profile;
            KeyInputSettings value = ReadInputDraft();
            Profile next = inputFieldsDirty == InputActivationFields.None ? profile : KeyInputEditing.ApplyActivation(profile, editingInputKeys, value, inputFieldsDirty);
            var pair = oppositeKey.SelectedItem as OppositeKeyItem;
            if (inputPairingDirty && pair != null && !pair.Preserve && editingInputKeys.Length <= 2)
                next = KeyInputEditing.SetPairing(next, editingInputKeys, pair.Index, value.OppositePolicy);
            return next;
        }
        void FlushInputDraft()
        {
            CancelInputThresholdGestures();
            if (!inputDirty || editingInputKeys.Length == 0 || updatingInput) return;
            EditHistory previousHistory = history; object previousSnapshot = history.SnapshotToken;
            var next = MergePendingInput(history.Current); history.Commit(next); inputDirty = false; inputFieldsDirty = InputActivationFields.None; inputPairingDirty = false;
            AdvanceSocdDraftSnapshot(previousHistory, previousSnapshot); Configure();
            SetInputStatus(InputSelectionLabel() + " · " + Tr("Eigene Einstellung", "Custom behavior"));
            resetKeyBehavior.Enabled = true;
            RefreshMappingSummaries(false);
        }
        sealed class OppositeKeyItem
        {
            public readonly int? Index; public readonly bool Preserve; readonly string label;
            public OppositeKeyItem(int? index, string text, bool preserve = false) { Index = index; label = text; Preserve = preserve; }
            public override string ToString() { return label; }
        }
        sealed class OppositeModeItem
        {
            public readonly InputOpposedPolicy Value;
            public OppositeModeItem(InputOpposedPolicy value) { Value = value; }
            public override string ToString()
            { return Value == InputOpposedPolicy.LastPressed ? Tr("Zuletzt gedrückte Taste gewinnt", "Last pressed key wins") : Value == InputOpposedPolicy.FirstPressed ? Tr("Zuerst gedrückte Taste gewinnt", "First pressed key wins") : Tr("Beide neutral", "Both neutral"); }
        }
    }
}
