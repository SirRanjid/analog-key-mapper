using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using Tk75.Mapping;

namespace Tk75.App
{
    public sealed partial class MainForm
    {
        readonly CheckBox rgbOverrideToggle = new CheckBox { Dock = DockStyle.Fill, AutoSize = false, Margin = new Padding(2, 0, 8, 0) };
        readonly SleekButton rgbColorButton = new SleekButton { Dock = DockStyle.Fill, Margin = new Padding(3, 2, 3, 2) };
        readonly SleekButton rgbDefaultColor = new SleekButton { Text = "↺", IconOnly = true, Dock = DockStyle.Fill, Margin = new Padding(3, 2, 0, 2) };
        readonly SleekButton rgbRestoreButton = new SleekButton { Dock = DockStyle.Fill, Margin = new Padding(8, 2, 0, 2) };
        readonly Label rgbStatus = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Tag = "muted", Margin = new Padding(2, 0, 8, 0) };
        bool syncingRgbUi;

        Control BuildRgbUi()
        {
            var card = new SleekCard { Dock = DockStyle.Top, Height = 84, Padding = new Padding(4), Margin = new Padding(0, 6, 0, 6) };
            var rows = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
            rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            rows.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); rows.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126)); top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38));
            top.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            top.Controls.Add(rgbOverrideToggle, 0, 0); top.Controls.Add(rgbColorButton, 1, 0); top.Controls.Add(rgbDefaultColor, 2, 0);
            var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
            bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            bottom.Controls.Add(rgbStatus, 0, 0); bottom.Controls.Add(rgbRestoreButton, 1, 0);
            rows.Controls.Add(top, 0, 0); rows.Controls.Add(bottom, 0, 1); card.Controls.Add(rows);
            UiText.PreserveText(rgbStatus);
            rgbOverrideToggle.CheckedChanged += delegate
            {
                if (syncingRgbUi) return;
                bool requested = rgbOverrideToggle.Checked;
                try
                {
                    Attempt(delegate
                    {
                        settings.EndEdit(); FlushInputDraft();
                        Profile profile = history.Current;
                        if (profile.RgbOverrideEnabled == requested) return;
                        profile.RgbOverrideEnabled = requested; Commit(profile);
                        if (!requested && RgbRestoreAvailable && !profile.ModeSwitchLightingEnabled) RestoreKeyboardLighting();
                        else RefreshRgbLighting();
                    });
                }
                finally { RefreshRgbUi(); }
            };
            rgbColorButton.Click += delegate { Attempt(ChooseControllerRgbColor); };
            rgbDefaultColor.Click += delegate
            {
                Attempt(delegate
                {
                    settings.EndEdit(); FlushInputDraft();
                    Commit(ControllerRouting.SetRgbColor(history.Current, SelectedControllerDefinition.Id, null));
                    RefreshRgbUi();
                });
            };
            rgbRestoreButton.Click += delegate
            {
                Attempt(delegate { if (RgbRestoreAvailable) RestoreKeyboardLighting(); RefreshRgbUi(); });
            };
            RefreshRgbUi(); return card;
        }

        void ChooseControllerRgbColor()
        {
            settings.EndEdit(); FlushInputDraft();
            string controllerId = SelectedControllerDefinition.Id, originalPath = profilePath;
            string original = ProfileJson.Serialize(history.Current);
            int value = RgbOverridePlan.GetControllerColor(history.Current, controllerId);
            using (var dialog = new ColorDialog { Color = RgbColor(value), FullOpen = true, AnyColor = true, SolidColorOnly = true })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                if (originalPath != profilePath || original != ProfileJson.Serialize(history.Current))
                    throw new InvalidOperationException(Tr("Profil oder Tastatur hat sich geändert. Bitte die Farbe erneut auswählen.", "The profile or keyboard changed. Please choose the color again."));
                int color = (dialog.Color.R << 16) | (dialog.Color.G << 8) | dialog.Color.B;
                Commit(ControllerRouting.SetRgbColor(history.Current, controllerId, color));
            }
            RefreshRgbUi();
        }

        static Color RgbColor(int value) { return Color.FromArgb((value >> 16) & 255, (value >> 8) & 255, value & 255); }

        void RefreshRgbUi()
        { RefreshRgbUi(null); }

        void RefreshRgbUi(string capturedStatus)
        {
            if (IsDisposed) return;
            bool previous = syncingRgbUi; syncingRgbUi = true;
            try
            {
                bool ready = RgbOverrideReady && !closing && !deviceDetachInProgress;
                Profile profile = UiReadProfile;
                ControllerDefinition selected = SelectedControllerDefinition;
                rgbOverrideToggle.Text = Tr("Tastenfarben", "Key colors");
                rgbOverrideToggle.Checked = profile.RgbOverrideEnabled; rgbOverrideToggle.Enabled = !closing && !deviceDetachInProgress;
                rgbColorButton.Text = Tr("Farbe wählen", "Choose color"); rgbColorButton.Enabled = !closing && !deviceDetachInProgress;
                rgbDefaultColor.Enabled = !closing && !deviceDetachInProgress && selected.RgbColor.HasValue;
                rgbDefaultColor.AccessibleName = Tr("Standardfarbe verwenden", "Use default color");
                rgbRestoreButton.Text = Tr("Wiederherstellen", "Restore lighting"); rgbRestoreButton.Enabled = RgbRestoreAvailable && !closing && !deviceDetachInProgress;
                rgbColorButton.AccessibleName = Tr("Farbe für ", "Color for ") + ControllerDisplayName;
                int color = RgbOverridePlan.GetControllerColor(profile, selected.Id);
                Color swatch = RgbColor(color); rgbColorButton.BackColor = swatch;
                rgbColorButton.ForeColor = swatch.R * .299 + swatch.G * .587 + swatch.B * .114 > 155 ? Color.FromArgb(24, 26, 32) : Color.White;
                string status = capturedStatus ?? RgbOverrideStatusText;
                rgbStatus.Text = String.IsNullOrWhiteSpace(status) ? ready ? Tr("Bereit für Controllerfarben", "Ready for controller colors") : Tr("Beleuchtungszugriff noch nicht bereit", "Lighting access is not ready") : UiText.Get(status);
                // Direct refreshes (toggle, restore, controller selection) and
                // timer refreshes must cache the state actually rendered. Never
                // cache a different worker read from the one shown in the label.
                lastRgbUiStatus = status;
                keyCardTips.SetToolTip(rgbStatus, rgbStatus.Text);
                keyCardTips.SetToolTip(rgbColorButton, ControllerDisplayName + " · #" + color.ToString("X6", CultureInfo.InvariantCulture));
                keyCardTips.SetToolTip(rgbDefaultColor, Tr("Zur Farbe aus der Profilpalette zurückkehren.", "Return to the profile palette color."));
                keyCardTips.SetToolTip(rgbOverrideToggle, Tr(
                    "Nur zugewiesene Tasten verbundener Controller werden im Controllermodus gefärbt. Bei mehreren Controllern auf derselben Taste gewinnt der erste im Profil. Farbänderungen werden bei bestehender Verbindung übernommen.",
                    "Mapped keys of connected controllers are colored in controller mode. When several controllers share a key, the first in the profile wins. Color changes keep controllers connected."));
                keyCardTips.SetToolTip(rgbRestoreButton, Tr("Die gesicherte normale Tastaturbeleuchtung wiederherstellen.", "Restore the saved normal keyboard lighting."));
            }
            finally { syncingRgbUi = previous; }
        }
    }
}
