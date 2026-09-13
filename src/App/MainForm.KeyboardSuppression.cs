using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Tk75.Mapping;

namespace Tk75.App
{
    public sealed partial class MainForm
    {
        readonly KeyboardSuppression keyboardSuppression = new KeyboardSuppression();
        readonly SleekButton inputModeButton = new SleekButton { Dock = DockStyle.Fill, Height = 32, Margin = Padding.Empty };
        readonly CheckBox controllerOnlyInput = new CheckBox { Dock = DockStyle.Fill, AutoEllipsis = true, Margin = Padding.Empty };
        readonly Label suppressionState = new Label { Dock = DockStyle.Fill, AutoSize = true, TextAlign = ContentAlignment.MiddleRight, Margin = Padding.Empty, Padding = new Padding(8, 0, 0, 0) };
        readonly SleekButton suppressionDetails = new SleekButton { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(8, 0, 0, 0) };
        bool modeHotkey, updatingSuppressionUi;
        bool suppressionDialogOpen;
        const int ModeHotkey = 7276;

        Control BuildInputModeControl()
        {
            UiText.PreserveText(inputModeButton); UiText.PreserveText(controllerOnlyInput);
            UiText.PreserveText(suppressionState); UiText.PreserveText(suppressionDetails);
            inputModeButton.Click += delegate { Attempt(ToggleInputMode); };
            controllerOnlyInput.CheckedChanged += delegate
            {
                if (updatingSuppressionUi || closing || IsDisposed || Disposing) return;
                bool enabled = controllerOnlyInput.Checked;
                Attempt(delegate { SetKeyboardSuppressionPreference(enabled ? KeyboardSuppressionMode.AllMapped : KeyboardSuppressionMode.Off, null); });
                RefreshSuppressionUi();
            };
            suppressionDetails.Click += delegate { Attempt(ShowKeyboardSuppression); };
            var control = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 64, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty };
            control.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            control.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); control.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 1, ColumnCount = 3, Margin = Padding.Empty };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            row.Controls.Add(controllerOnlyInput, 0, 0); row.Controls.Add(suppressionState, 1, 0); row.Controls.Add(suppressionDetails, 2, 0);
            control.Controls.Add(inputModeButton, 0, 0); control.Controls.Add(row, 0, 1);
            RefreshInputModeUi();
            return control;
        }

        void ToggleInputMode()
        {
            if (closing || deviceDetachInProgress) return;
            bool nextKeyboardMode = !runtime.KeyboardMode;
            // Publish the suppression lease before mapped output resumes. The
            // hook still waits for a fresh key-down for already held keys.
            if (!nextKeyboardMode) RefreshKeyboardSuppression(true);
            try { runtime.KeyboardMode = nextKeyboardMode; }
            finally { RefreshKeyboardSuppression(false); RefreshInputModeUi(); }
        }

        void RefreshInputModeUi()
        {
            // The theme's general button margins do not fit these compact rows.
            if (inputModeButton.Margin != Padding.Empty) inputModeButton.Margin = Padding.Empty;
            Padding compactPadding = new Padding(8, 2, 8, 2);
            if (inputModeButton.Padding != compactPadding) inputModeButton.Padding = compactPadding;
            string text = runtime.KeyboardMode ? Tr("Modus: Tastatur", "Mode: Keyboard") : Tr("Modus: Controller", "Mode: Controller");
            if (modeHotkey) text += " · " + ShortcutLabel;
            if (inputModeButton.Text != text) inputModeButton.Text = text;
            string hint = Tr(
                "Klicken zum Umschalten. Im Tastaturmodus kannst du normal tippen; der verbundene Controller bleibt neutral. Im Controller-Modus steuern deine Zuordnungen den Controller. „Nur Controller-Eingaben“ verhindert dabei zusätzliche Tastatureingaben. Profilstandard und Tastenkombinationen findest du im Menü.",
                "Click to switch. Keyboard mode allows normal typing and keeps the connected controller neutral. Controller mode uses your mappings. ‘Controller input only’ prevents additional keyboard input. Find the profile default and shortcuts in the menu.") +
                (String.IsNullOrEmpty(modeShortcutError) ? "" : " " + modeShortcutError);
            RefreshStopShortcutUi();
            if (keyCardTips.GetToolTip(inputModeButton) != hint) keyCardTips.SetToolTip(inputModeButton, hint);
            RefreshSuppressionUi();
        }

        void RefreshSuppressionUi()
        {
            if (IsDisposed || Disposing) return;
            Profile profile = UiReadProfile;
            bool configured = profile.KeyboardSuppressionMode != KeyboardSuppressionMode.Off;
            updatingSuppressionUi = true;
            try
            {
                Padding detailsMargin = new Padding(8, 0, 0, 0), detailsPadding = new Padding(8, 2, 8, 2);
                if (suppressionDetails.Margin != detailsMargin) suppressionDetails.Margin = detailsMargin;
                if (suppressionDetails.Padding != detailsPadding) suppressionDetails.Padding = detailsPadding;
                controllerOnlyInput.Text = Tr("Nur Controller-Eingaben", "Controller input only");
                controllerOnlyInput.Checked = configured;
                controllerOnlyInput.Enabled = !closing && !deviceDetachInProgress;
                suppressionDetails.Text = Tr("Details…", "Details…");
                suppressionDetails.Enabled = !closing && !deviceDetachInProgress;
            }
            finally { updatingSuppressionUi = false; }
            string state = !configured ? Tr("Aus", "Off") : runtime.KeyboardMode ? Tr("Pausiert", "Paused") :
                !runtime.AnyEnabled ? Tr("Wartet", "Waiting") : !keyboardSuppression.IsInstalled ? Tr("Nicht bereit", "Not ready") : Tr("Aktiv", "Active");
            if (suppressionState.Text != state) suppressionState.Text = state;
            suppressionState.ForeColor = configured && runtime.AnyEnabled && !runtime.KeyboardMode ? ModernTheme.Accent : ModernTheme.Muted;
            string scope = profile.KeyboardSuppressionMode == KeyboardSuppressionMode.SelectedMapped ?
                Tr("Nur die unter Details ausgewählten Tasten.", "Only the keys selected under Details.") :
                Tr("Alle einem verbundenen Controller zugewiesenen Tasten.", "All keys mapped to a connected controller.");
            string hint = configured ? Tr("Für dieses Profil eingeschaltet. ", "Enabled for this profile. ") + scope :
                Tr("Einschalten, um zusätzliche Tastatureingaben der zugewiesenen Tasten zu verhindern.", "Enable to prevent additional keyboard input from mapped keys.");
            hint += Tr(" Im Tastaturmodus und ohne verbundenen Controller kannst du normal tippen. Diese App bleibt bedienbar.",
                " Keyboard mode and disconnected controllers allow normal typing. This app stays usable.");
            if (configured && !keyboardSuppression.IsInstalled && applicationInputActive) hint += " " + UiText.Get(keyboardSuppression.Status);
            foreach (Control target in new Control[] { controllerOnlyInput, suppressionState, suppressionDetails })
                if (keyCardTips.GetToolTip(target) != hint) keyCardTips.SetToolTip(target, hint);
        }

        void ApplyKeyboardSuppressionPreference()
        {
            RefreshKeyboardSuppression(false);
            RefreshSuppressionUi();
        }

        void RefreshKeyboardSuppression(bool preparingControllerMode)
        {
            if (IsDisposed || Disposing) return;
            Profile profile = UiReadProfile;
            bool enabled = applicationInputActive && !closing && !rgbClosePending && profile.KeyboardSuppressionMode != KeyboardSuppressionMode.Off;
            keyboardSuppression.SetEnabled(enabled);
            if (!enabled) return;
            var eligible = new List<SuppressionKey>();
            bool active = !deviceDetachInProgress && runtime.AnyEnabled;
            if (active)
            {
                var activeKeys = new HashSet<int>(runtime.ActiveKeyIndices);
                IEnumerable<int> candidates = profile.KeyboardSuppressionMode == KeyboardSuppressionMode.AllMapped ?
                    (IEnumerable<int>)activeKeys : profile.SuppressedKeyboardKeys;
                foreach (int index in candidates)
                {
                    if (!activeKeys.Contains(index)) continue;
                    SuppressionKey code;
                    if (TryInputSuppressionKey(profile, index, out code) && KeyboardSuppressionPolicy.CanSuppress(code)) eligible.Add(code);
                }
            }
            keyboardSuppression.UpdateEligibility(eligible, active, !preparingControllerMode && runtime.KeyboardMode);
        }

        void SetKeyboardSuppressionPreference(KeyboardSuppressionMode mode, IEnumerable<int> selected)
        {
            if (closing || IsDisposed || Disposing || deviceDetachInProgress) return;
            Profile edited = history.Current;
            edited.KeyboardSuppressionMode = mode;
            if (selected != null) edited.SuppressedKeyboardKeys = selected.Distinct().OrderBy(index => index).ToList();
            MappingValidation.RequireValid(edited);
            string serialized = ProfileJson.Serialize(edited);
            // Only metadata changes here. Do not flush an unrelated input draft
            // or reconfigure mapping sessions: either would disconnect output.
            // Persist first, so a failed save leaves the active preference intact.
            if (profilePath != null) WorkspaceStore.WriteAtomic(profilePath, serialized);
            history.Commit(edited);
            ApplyKeyboardSuppressionPreference();
        }

        sealed class SuppressionChoice
        {
            public int Index;
            public string Text;
            public override string ToString() { return Text; }
        }

        void ShowKeyboardSuppression()
        {
            Profile original = history.Current;
            string originalPath = profilePath, originalSnapshot = ProfileJson.Serialize(original);
            using (var dialog = new Form { Text = Tr("Nur Controller-Eingaben", "Controller input only"), ClientSize = new Size(600, 370), MinimumSize = new Size(550, 370), StartPosition = FormStartPosition.CenterParent })
            {
                var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 6 };
                panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
                panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 64)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
                panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
                var enabled = new CheckBox { Text = Tr("Nur Controller-Eingaben", "Controller input only"), Checked = original.KeyboardSuppressionMode != KeyboardSuppressionMode.Off, Dock = DockStyle.Fill };
                var help = new Label { Dock = DockStyle.Fill, Text = Tr("Verhindert, dass eine zugewiesene Taste gleichzeitig eine Tastatur- und Controller-Aktion auslöst.", "Prevents mapped keys from triggering both a keyboard and a controller action.") };
                var scope = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty };
                scope.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); scope.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); scope.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
                var all = new RadioButton { Text = Tr("Alle zugewiesenen Tasten", "All mapped keys"), Checked = original.KeyboardSuppressionMode != KeyboardSuppressionMode.SelectedMapped, Dock = DockStyle.Fill };
                var selected = new RadioButton { Text = Tr("Nur ausgewählte Tasten", "Selected keys only"), Checked = original.KeyboardSuppressionMode == KeyboardSuppressionMode.SelectedMapped, Dock = DockStyle.Fill };
                scope.Controls.Add(all, 0, 0); scope.Controls.Add(selected, 0, 1);
                var choices = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle };
                foreach (int index in original.Bindings.Select(binding => binding.KeyIndex).Distinct().OrderBy(index => index))
                {
                    SuppressionKey code;
                    if (!TryInputSuppressionKey(original, index, out code) || !KeyboardSuppressionPolicy.CanSuppress(code)) continue;
                    choices.Items.Add(new SuppressionChoice { Index = index, Text = Label(index) }, original.SuppressedKeyboardKeys.Contains(index));
                }
                var state = new Label { Dock = DockStyle.Fill, ForeColor = ModernTheme.Muted, Padding = new Padding(0, 8, 0, 0), Text = Tr(
                    "Im Profil gespeichert. Wirkt nur im Controller-Modus bei verbundenem Controller. In dieser App bleibt normales Tippen möglich.\n\nGilt für alle Tastaturen; manche Spiele umgehen die Sperre durch direkte Eingabe. Firmware-Umbelegungen werden nicht erkannt. Strg, Alt, Win und aktivierte Tastenkombinationen bleiben frei.",
                    "Saved in this profile. Applies only in controller mode with a connected controller. This app allows normal typing.\n\nAffects all keyboards; some games bypass suppression through direct input. Firmware remaps are not detected. Ctrl, Alt, Win and enabled shortcuts remain available.") };
                var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
                var apply = new SleekButton { Text = Tr("Übernehmen", "Apply"), Size = new Size(132, 36), Tag = "primary" };
                var cancel = new SleekButton { Text = Tr("Abbrechen", "Cancel"), Size = new Size(116, 36), DialogResult = DialogResult.Cancel };
                actions.Controls.Add(apply); actions.Controls.Add(cancel);
                panel.Controls.Add(enabled, 0, 0); panel.Controls.Add(help, 0, 1); panel.Controls.Add(scope, 0, 2);
                panel.Controls.Add(choices, 0, 3); panel.Controls.Add(state, 0, 4); panel.Controls.Add(actions, 0, 5);
                dialog.Controls.Add(panel); dialog.CancelButton = cancel;
                Action refreshScope = delegate
                {
                    bool showKeys = selected.Checked;
                    choices.Visible = showKeys; choices.Enabled = enabled.Checked;
                    scope.Enabled = enabled.Checked;
                    panel.RowStyles[3].Height = showKeys ? 210 : 0;
                    dialog.ClientSize = new Size(dialog.ClientSize.Width, showKeys ? 580 : 370);
                };
                selected.CheckedChanged += delegate { refreshScope(); };
                enabled.CheckedChanged += delegate { refreshScope(); };
                apply.Click += delegate
                {
                    try
                    {
                        if (closing || deviceDetachInProgress || originalPath != profilePath || originalSnapshot != ProfileJson.Serialize(history.Current))
                            throw new InvalidOperationException(Tr("Das Profil hat sich geändert. Öffne die Einstellungen bitte erneut.", "The profile changed. Please reopen these settings."));
                        var chosen = new HashSet<int>(original.SuppressedKeyboardKeys);
                        for (int index = 0; index < choices.Items.Count; index++)
                        {
                            int keyIndex = ((SuppressionChoice)choices.Items[index]).Index;
                            if (choices.GetItemChecked(index)) chosen.Add(keyIndex); else chosen.Remove(keyIndex);
                        }
                        KeyboardSuppressionMode mode = !enabled.Checked ? KeyboardSuppressionMode.Off : selected.Checked ? KeyboardSuppressionMode.SelectedMapped : KeyboardSuppressionMode.AllMapped;
                        if (mode == KeyboardSuppressionMode.SelectedMapped && choices.CheckedItems.Count == 0)
                            throw new InvalidOperationException(Tr("Wähle mindestens eine Taste oder „Alle zugewiesenen Tasten“.", "Select at least one key, or choose ‘All mapped keys’."));
                        SetKeyboardSuppressionPreference(mode, chosen);
                        dialog.DialogResult = DialogResult.OK; dialog.Close();
                    }
                    catch (Exception ex) { state.Text = UiText.Get(ex.Message); }
                };
                ModernTheme.Apply(dialog); UiText.Apply(dialog); refreshScope();
                suppressionDialogOpen = true;
                try { dialog.ShowDialog(this); }
                finally { suppressionDialogOpen = false; lastForeground = ""; RefreshSuppressionUi(); }
            }
        }
    }
}

