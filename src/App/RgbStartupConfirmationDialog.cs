using System;
using System.Drawing;
using System.Windows.Forms;

namespace Tk75.App
{
    internal sealed class RgbStartupConfirmationDialog : Form
    {
        readonly CheckBox automatic;
        // Checking the box and then declining must not grant future consent.
        internal bool AutoRepairChecked { get { return DialogResult == DialogResult.OK && automatic.Checked; } }

        internal RgbStartupConfirmationDialog(string summary)
        {
            Icon applicationIcon = AppStatusIcon.CreateApplication();
            Icon = applicationIcon;
            Disposed += delegate { applicationIcon.Dispose(); };
            Text = UiText.Get("Alte Tastenfarben bereinigen?", "Clean up old key colors?");
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = true;
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(610, 398);
            Padding = new Padding(22);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Margin = Padding.Empty };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            var introduction = Paragraph(UiText.Get(
                "Diese Tasten tragen die eingestellten Umschalt- oder Controllerfarben:",
                "These keys have the configured mode-switch or controller colors:"));
            layout.Controls.Add(introduction, 0, 0);
            var details = new TextBox {
                Name = "startupMarkerSummary", Text = summary ?? String.Empty, ReadOnly = true, Multiline = true,
                WordWrap = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
                Margin = new Padding(0, 0, 0, 13), TabStop = false, AccessibleName = UiText.Get("Betroffene Tasten", "Affected keys")
            };
            layout.Controls.Add(details, 0, 1);
            layout.Controls.Add(Paragraph(UiText.Get(
                "Das Muster passt zu früheren App-Markierungen. Eigene Tastenfarben können jedoch genauso aussehen.",
                "The pattern matches earlier app markers. Your own key colors can look the same, though.")), 0, 2);
            layout.Controls.Add(Paragraph(UiText.Get(
                "Beim Bereinigen werden nur diese Tasten korrigiert. Die übrigen Farben bleiben erhalten; danach gelten die aktuellen App-Einstellungen.",
                "Cleanup corrects only these keys. Other colors are preserved, then the current app settings apply.")), 0, 3);
            automatic = new CheckBox {
                Name = "automaticStartupRepair", Text = UiText.Get("Solche Farbmuster künftig automatisch bereinigen", "Clean up matching color patterns automatically in future"),
                Checked = false, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 13), TabIndex = 0
            };
            layout.Controls.Add(automatic, 0, 4);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
            var clean = new SleekButton {
                Name = "cleanStartupMarkers", Text = UiText.Get("Bereinigen", "Clean up"), DialogResult = DialogResult.OK,
                Width = 142, Height = 40, Margin = new Padding(10, 4, 0, 0), TabIndex = 1
            };
            var keep = new SleekButton {
                Name = "keepStartupMarkers", Text = UiText.Get("Unverändert lassen", "Keep unchanged"), DialogResult = DialogResult.Cancel,
                Width = 190, Height = 40, Margin = new Padding(0, 4, 0, 0), TabIndex = 0
            };
            buttons.Controls.Add(clean); buttons.Controls.Add(keep); layout.Controls.Add(buttons, 0, 5);
            Controls.Add(layout);
            AcceptButton = clean; CancelButton = keep;
            // The startup prompt is also used modelessly, so shutdown and the
            // hidden main window stay responsive while a decision is pending.
            clean.Click += delegate { DialogResult = DialogResult.OK; Close(); };
            keep.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            ModernTheme.Apply(this); ModernTheme.Primary(clean); ModernTheme.Secondary(keep);
            // Merely opening the dialog does not preselect repair or consent.
            Shown += delegate { keep.Focus(); };
        }

        static Label Paragraph(string text)
        {
            return new Label {
                Text = text, AutoSize = true, Dock = DockStyle.Fill, MaximumSize = new Size(566, 0),
                Margin = new Padding(0, 0, 0, 13), UseMnemonic = false
            };
        }
    }
}
