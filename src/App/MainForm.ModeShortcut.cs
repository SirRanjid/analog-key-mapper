using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Tk75.Mapping;

namespace Tk75.App
{
    public sealed partial class MainForm
    {
        bool modeShortcutRegistrationActive, modeShortcutDialogOpen;
        HotkeySettings attemptedModeShortcut, attemptedStopShortcut;
        IntPtr modeShortcutHandle, stopShortcutHandle;
        string modeShortcutError, stopShortcutError;
        string inputModePreferenceProfile;
        string registeredShortcutProfile;
        bool shortcutQueueReady = true;
        bool? configuredInputModePreference;
        SleekButton allOffButton;
        string ShortcutLabel { get { return FormatShortcut(UiReadProfile.ModeSwitchHotkey); } }
        string EmergencyLabel { get { return FormatShortcut(UiReadProfile.EmergencyStopHotkey); } }

        static HotkeySettings CopyShortcut(HotkeySettings value)
        { return new HotkeySettings { Enabled = value.Enabled, KeyCode = value.KeyCode, Modifiers = value.Modifiers }; }
        static string FormatShortcut(HotkeySettings value)
        {
            var parts = new List<string>();
            if ((value.Modifiers & 2) != 0) parts.Add(UiText.Get("Strg", "Ctrl"));
            if ((value.Modifiers & 1) != 0) parts.Add("Alt");
            if ((value.Modifiers & 4) != 0) parts.Add("Shift");
            if ((value.Modifiers & 8) != 0) parts.Add("Win");
            string key = ((Keys)value.KeyCode).ToString();
            if (value.KeyCode >= 0x30 && value.KeyCode <= 0x39) key = ((char)value.KeyCode).ToString();
            else if (value.KeyCode == (int)Keys.Space) key = UiText.Get("Leertaste", "Space");
            else if (value.KeyCode == (int)Keys.Return) key = "Enter";
            else if (value.KeyCode == (int)Keys.Escape) key = "Esc";
            else if (!Enum.IsDefined(typeof(Keys), value.KeyCode)) key = "VK " + value.KeyCode.ToString("X2", CultureInfo.InvariantCulture);
            parts.Add(key); return String.Join(" + ", parts.ToArray());
        }
        void ResetProfileInputMode() { configuredInputModePreference = null; registeredShortcutProfile = null; }
        void ApplyProfileInputModePreference()
        {
            bool enabled = history.Current.ControllerInputEnabled;
            if (inputModePreferenceProfile == profilePath && configuredInputModePreference == enabled) return;
            runtime.KeyboardMode = !enabled;
            inputModePreferenceProfile = profilePath; configuredInputModePreference = enabled;
        }
        void ReleaseShortcutRegistrations()
        {
            IntPtr oldHandle = modeShortcutHandle != IntPtr.Zero ? modeShortcutHandle : stopShortcutHandle;
            if (modeShortcutHandle != IntPtr.Zero) UnregisterHotKey(modeShortcutHandle, ModeHotkey);
            if (stopShortcutHandle != IntPtr.Zero) UnregisterHotKey(stopShortcutHandle, DisableHotkey);
            modeShortcutHandle = stopShortcutHandle = IntPtr.Zero;
            modeHotkey = hotkey = false; attemptedModeShortcut = attemptedStopShortcut = null;
            PublishProtectedProfileShortcuts();
            // These are the only two hotkeys registered to this window. Drain
            // their pending messages after unregistering, before the next profile
            // can reuse the same key combination. Never pump other UI messages.
            if (oldHandle != IntPtr.Zero)
            {
                ShortcutNativeMessage pending;
                int drained = 0;
                while (drained < 4096 && PeekMessage(out pending, oldHandle, 0x0312, 0x0312, 1)) drained++;
                shortcutQueueReady = !PeekMessage(out pending, oldHandle, 0x0312, 0x0312, 0);
            }
        }
        [StructLayout(LayoutKind.Sequential)]
        struct ShortcutNativeMessage
        { internal IntPtr Hwnd; internal uint Message; internal IntPtr WParam, LParam; internal uint Time; internal Point Point; internal uint Private; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool PeekMessage(out ShortcutNativeMessage message, IntPtr window, uint minimum, uint maximum, uint remove);
        void PublishProtectedProfileShortcuts()
        {
            if (closing || IsDisposed || Disposing) return;
            Profile profile = history.Current;
            HotkeySettings mode = profile.ModeSwitchHotkey, stop = profile.EmergencyStopHotkey;
            bool active = modeShortcutRegistrationActive && !modeShortcutDialogOpen;
            keyboardSuppression.SetProtectedHotkeys(mode.KeyCode, mode.Modifiers, active && mode.Enabled && modeHotkey,
                stop.KeyCode, stop.Modifiers, active && stop.Enabled && hotkey);
        }
        void RegisterProfileShortcut(HotkeySettings value, int id, ref HotkeySettings attempted, ref IntPtr registeredHandle, ref bool registered, ref string error)
        {
            if (attempted != null && attempted.Enabled == value.Enabled && attempted.KeyCode == value.KeyCode && attempted.Modifiers == value.Modifiers) return;
            if (registeredHandle != IntPtr.Zero) UnregisterHotKey(registeredHandle, id);
            registeredHandle = IntPtr.Zero; registered = false; error = null; attempted = CopyShortcut(value);
            if (!value.Enabled) return;
            registered = RegisterHotKey(Handle, id, (uint)value.Modifiers | 0x4000U, (uint)value.KeyCode);
            if (registered) registeredHandle = Handle;
            else error = Tr("In Windows nicht verfügbar: ", "Unavailable in Windows: ") + FormatShortcut(value);
        }
        void RefreshModeShortcutRegistration()
        {
            Profile profile = history.Current;
            HotkeySettings mode = profile.ModeSwitchHotkey, stop = profile.EmergencyStopHotkey;
            if (!modeShortcutRegistrationActive || modeShortcutDialogOpen || closing || IsDisposed || !IsHandleCreated)
            { PublishProtectedProfileShortcuts(); return; }
            // Releasing both permits swapping their assignments without a false conflict.
            bool changed = registeredShortcutProfile != profilePath || attemptedModeShortcut == null || attemptedStopShortcut == null ||
                attemptedModeShortcut.Enabled != mode.Enabled || attemptedModeShortcut.KeyCode != mode.KeyCode || attemptedModeShortcut.Modifiers != mode.Modifiers ||
                attemptedStopShortcut.Enabled != stop.Enabled || attemptedStopShortcut.KeyCode != stop.KeyCode || attemptedStopShortcut.Modifiers != stop.Modifiers;
            if (changed) ReleaseShortcutRegistrations();
            if (!shortcutQueueReady)
            {
                modeShortcutError = stopShortcutError = Tr("Tastenkombinationen warten auf einen App-Neustart.", "Shortcuts require an app restart.");
                RefreshInputModeUi(); RefreshStopShortcutUi(); return;
            }
            RegisterProfileShortcut(mode, ModeHotkey, ref attemptedModeShortcut, ref modeShortcutHandle, ref modeHotkey, ref modeShortcutError);
            RegisterProfileShortcut(stop, DisableHotkey, ref attemptedStopShortcut, ref stopShortcutHandle, ref hotkey, ref stopShortcutError);
            registeredShortcutProfile = profilePath; PublishProtectedProfileShortcuts();
            RefreshInputModeUi(); RefreshStopShortcutUi();
        }
        void RefreshStopShortcutUi()
        {
            if (allOffButton == null) return;
            // Its footer column is AutoSize: account for the complete shortcut,
            // translated caption and current font instead of a fixed 100 px cell.
            if (!allOffButton.AutoSize) allOffButton.AutoSize = true;
            if (allOffButton.AutoSizeMode != AutoSizeMode.GrowAndShrink) allOffButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding margin = new Padding(8, 0, 0, 0), padding = new Padding(10, 2, 10, 2);
            if (allOffButton.Margin != margin) allOffButton.Margin = margin;
            if (allOffButton.Padding != padding) allOffButton.Padding = padding;
            string text = Tr("Alle aus", "All off") + (hotkey ? " · " + EmergencyLabel : "");
            if (allOffButton.Text != text) allOffButton.Text = text;
            string hint = Tr("Trennt alle virtuellen Controller.", "Disconnects all virtual controllers.");
            if (hotkey) hint += " " + Tr("Tastenkombination: ", "Shortcut: ") + EmergencyLabel;
            if (!String.IsNullOrEmpty(stopShortcutError)) hint += " " + stopShortcutError;
            if (keyCardTips.GetToolTip(allOffButton) != hint) keyCardTips.SetToolTip(allOffButton, hint);
        }
        bool IsCurrentShortcutMessage(Message message, bool stop)
        {
            if (!(stop ? hotkey : modeHotkey) || modeShortcutDialogOpen) return false;
            HotkeySettings value = stop ? UiReadProfile.EmergencyStopHotkey : UiReadProfile.ModeSwitchHotkey;
            long data = message.LParam.ToInt64();
            return value.Enabled && ((data >> 16) & 0xFFFF) == value.KeyCode && (data & 15) == value.Modifiers;
        }
        void ShowModeShortcut()
        {
            settings.EndEdit(); FlushInputDraft();
            string originalPath = profilePath, original = ProfileJson.Serialize(history.Current);
            Profile current = history.Current;
            using (var dialog = new Form { Text = Tr("Controller-Eingaben & Tastenkombinationen", "Controller input & shortcuts"), ClientSize = new Size(640, 579),
                MinimumSize = new Size(615, 604), StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false })
            {
                var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 6 };
                panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
                panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 172)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 128));
                panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
                var inputEnabled = new CheckBox { Dock = DockStyle.Fill, Checked = current.ControllerInputEnabled, Text = Tr("Controller-Eingaben in diesem Profil verwenden", "Use controller input in this profile") };
                var help = new Label { Dock = DockStyle.Fill, Text = Tr("Aus: Der verbundene Controller bleibt neutral. An: Zugewiesene Tasten steuern ihn. Dieser Standard gilt beim Laden des Profils; ein Moduswechsel gilt bis zum nächsten Laden. Die USB-Verbindung schaltest du weiterhin separat ein.",
                    "Off: The connected controller stays neutral. On: Mapped keys control it. This default applies when loading the profile; a mode switch lasts until the next load. The USB connection is still enabled separately.") };
                var mode = new ShortcutEditor(current.ModeSwitchHotkey, Tr("Modus wechseln", "Switch input mode"), 0x78);
                var modeLighting = new ModeLightingEditor(current);
                var modeGroup = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
                modeGroup.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                modeGroup.RowStyles.Add(new RowStyle(SizeType.Absolute, 128)); modeGroup.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
                modeGroup.Controls.Add(mode, 0, 0); modeGroup.Controls.Add(modeLighting, 0, 1);
                mode.ValueChanged += delegate { modeLighting.SetShortcutEnabled(mode.Value.Enabled); };
                var stop = new ShortcutEditor(current.EmergencyStopHotkey, Tr("Alle Controller ausschalten", "Disconnect all controllers"), 0x77);
                var status = new Label { Dock = DockStyle.Fill, ForeColor = ModernTheme.Muted, Text = Tr("Tastenkombinationen sind optional und hier pausiert. Zum Aufnehmen ins Feld klicken.\nDie Markierung färbt die echte Umschalttaste in beiden Modi; bei Kombinationen nur die Haupttaste nach aktivem Windows-Layout.",
                    "Shortcuts are optional and paused here. Click a field to record one.\nThe color marks the physical mode-switch key in both modes; for combinations, only the main key using the active Windows layout.") };
                mode.HelpChanged += delegate(string value) { status.Text = value; }; stop.HelpChanged += delegate(string value) { status.Text = value; };
                var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
                var apply = new SleekButton { Text = Tr("Übernehmen", "Apply"), Size = new Size(135, 36), Tag = "primary" };
                var cancel = new SleekButton { Text = Tr("Abbrechen", "Cancel"), Size = new Size(120, 36), DialogResult = DialogResult.Cancel };
                actions.Controls.Add(apply); actions.Controls.Add(cancel);
                apply.Click += delegate
                {
                    try
                    {
                        if (closing || deviceDetachInProgress || originalPath != profilePath || original != ProfileJson.Serialize(history.Current))
                            throw new InvalidOperationException(Tr("Das Profil hat sich geändert. Öffne die Einstellung erneut.", "The profile changed. Reopen this setting."));
                        Profile edited = history.Current; edited.ModeSwitchHotkey = mode.Value; edited.EmergencyStopHotkey = stop.Value; edited.ControllerInputEnabled = inputEnabled.Checked;
                        edited.ModeSwitchLightingEnabled = modeLighting.MarkEnabled; edited.ModeSwitchRgbColor = modeLighting.RgbValue;
                        MappingValidation.RequireValid(edited); Commit(edited); SaveProfile(); dialog.DialogResult = DialogResult.OK; dialog.Close();
                    }
                    catch (Exception ex) { status.Text = UiText.Get(ex.Message); }
                };
                panel.Controls.Add(inputEnabled, 0, 0); panel.Controls.Add(help, 0, 1); panel.Controls.Add(modeGroup, 0, 2); panel.Controls.Add(stop, 0, 3); panel.Controls.Add(status, 0, 4); panel.Controls.Add(actions, 0, 5);
                dialog.Controls.Add(panel); dialog.CancelButton = cancel; ModernTheme.Apply(dialog); UiText.Apply(dialog); modeLighting.RefreshState();
                modeShortcutDialogOpen = true; ReleaseShortcutRegistrations(); RefreshInputModeUi(); RefreshStopShortcutUi();
                try { dialog.ShowDialog(this); }
                finally { modeShortcutDialogOpen = false; lastForeground = ""; RefreshModeShortcutRegistration(); }
            }
        }
        sealed class ShortcutEditor : TableLayoutPanel
        {
            HotkeySettings value;
            internal event Action<string> HelpChanged;
            internal event Action ValueChanged;
            internal HotkeySettings Value { get { return CopyShortcut(value); } }
            internal ShortcutEditor(HotkeySettings initial, string title, int defaultKey)
            {
                value = CopyShortcut(initial); Dock = DockStyle.Fill; ColumnCount = 2; RowCount = 3; Margin = new Padding(0, 4, 0, 8);
                ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
                RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                var enabled = new CheckBox { Text = title, Checked = value.Enabled, Dock = DockStyle.Fill };
                var capture = new ModeShortcutCapture { Dock = DockStyle.Fill, ReadOnly = true, Text = FormatShortcut(value), Enabled = value.Enabled };
                var reset = new SleekButton { Dock = DockStyle.Fill, Text = UiText.Get("Standard: ", "Default: ") + FormatShortcut(new HotkeySettings { KeyCode = defaultKey }), Margin = new Padding(6, 0, 0, 0) };
                var hint = new Label { Dock = DockStyle.Fill, Text = UiText.Get("Taste drücken · optional Strg, Alt, Shift und Win dazu", "Press a key · optionally add Ctrl, Alt, Shift and Win"), Tag = "muted" };
                Controls.Add(enabled, 0, 0); SetColumnSpan(enabled, 2); Controls.Add(capture, 0, 1); Controls.Add(reset, 1, 1); Controls.Add(hint, 0, 2); SetColumnSpan(hint, 2);
                enabled.CheckedChanged += delegate { value.Enabled = enabled.Checked; capture.Enabled = enabled.Checked; if (ValueChanged != null) ValueChanged(); };
                reset.Click += delegate { value = new HotkeySettings { KeyCode = defaultKey }; enabled.Checked = true; capture.Text = FormatShortcut(value); capture.Focus(); if (ValueChanged != null) ValueChanged(); };
                capture.Captured += delegate(Keys keyData)
                {
                    int modifiers = ((keyData & Keys.Alt) != 0 ? 1 : 0) | ((keyData & Keys.Control) != 0 ? 2 : 0) |
                        ((keyData & Keys.Shift) != 0 ? 4 : 0) | (KeyboardSuppression.IsVirtualKeyDown(0x5B) || KeyboardSuppression.IsVirtualKeyDown(0x5C) ? 8 : 0);
                    var candidate = new HotkeySettings { Enabled = enabled.Checked, KeyCode = (int)(keyData & Keys.KeyCode), Modifiers = modifiers };
                    if (MappingValidation.ValidateHotkey(candidate).Count != 0)
                    { if (HelpChanged != null) HelpChanged(UiText.Get("Drücke eine normale Taste dazu.", "Add a regular key.")); return; }
                    value = candidate; capture.Text = FormatShortcut(value);
                    if (ValueChanged != null) ValueChanged();
                    if (HelpChanged != null) HelpChanged(UiText.Get("Neue Kombination: ", "New shortcut: ") + capture.Text);
                };
            }
        }
        sealed class ModeShortcutCapture : TextBox, IShortcutCaptureControl
        {
            internal event Action<Keys> Captured;
            void ReportCapture(Keys keys) { if (Captured != null) Captured(keys); }
            protected override bool ProcessCmdKey(ref Message message, Keys keyData) { ReportCapture(keyData); return true; }
            protected override void OnKeyDown(KeyEventArgs e) { ReportCapture(e.KeyData); e.Handled = true; e.SuppressKeyPress = true; }
        }
    }
}
