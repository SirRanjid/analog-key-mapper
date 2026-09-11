using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using Tk75.Mapping;

namespace Tk75.App
{
    public sealed partial class MainForm
    {
        sealed class ModeLightingEditor : TableLayoutPanel
        {
            readonly CheckBox mark;
            readonly SleekButton color;
            readonly ToolTip tips = new ToolTip();
            int rgb;
            bool shortcutEnabled;
            internal bool MarkEnabled { get { return mark.Checked; } }
            internal int RgbValue { get { return rgb; } }

            internal ModeLightingEditor(Profile profile)
            {
                Dock = DockStyle.Fill; ColumnCount = 2; RowCount = 1; Margin = new Padding(0, 0, 0, 4);
                ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                rgb = profile.ModeSwitchRgbColor; shortcutEnabled = profile.ModeSwitchHotkey.Enabled;
                mark = new CheckBox { Text = Tr("Umschalttaste farbig markieren", "Highlight the mode-switch key"), Checked = profile.ModeSwitchLightingEnabled, Dock = DockStyle.Fill, Margin = Padding.Empty };
                color = new SleekButton { Text = Tr("Farbe…", "Color…"), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, Margin = new Padding(8, 2, 0, 2) };
                Controls.Add(mark, 0, 0); Controls.Add(color, 1, 0);
                UiText.PreserveText(mark); UiText.PreserveText(color);
                mark.CheckedChanged += delegate { RefreshState(); };
                color.Click += delegate
                {
                    using (var picker = new ColorDialog { Color = RgbColor(rgb), FullOpen = true, AnyColor = true, SolidColorOnly = true })
                    {
                        if (picker.ShowDialog(FindForm()) != DialogResult.OK) return;
                        rgb = picker.Color.R << 16 | picker.Color.G << 8 | picker.Color.B;
                        RefreshState();
                    }
                };
                RefreshState();
            }

            internal void SetShortcutEnabled(bool enabled)
            { shortcutEnabled = enabled; RefreshState(); }

            internal void RefreshState()
            {
                if (IsDisposed || Disposing) return;
                mark.Enabled = shortcutEnabled;
                color.Enabled = shortcutEnabled && mark.Checked;
                // Restore compact dimensions and the chosen swatch after theming.
                color.Margin = new Padding(8, 2, 0, 2); color.Padding = new Padding(10, 2, 10, 2);
                Color swatch = RgbColor(rgb); color.BackColor = swatch;
                color.ForeColor = swatch.R * .299 + swatch.G * .587 + swatch.B * .114 > 155 ? ModernTheme.Background : Color.White;
                string explanation = Tr(
                    "Markiert die echte Taste auf der Tastatur in beiden Modi. Bei einer Kombination wird nur die Haupttaste gefärbt. Die Zuordnung folgt dem aktiven Windows-Tastaturlayout.",
                    "Highlights the physical keyboard key in both modes. For a combination, only the main key is colored. Its position follows the active Windows keyboard layout.");
                if (!shortcutEnabled) explanation += Tr(" Schalte zuerst „Modus wechseln“ ein.", " Enable ‘Switch input mode’ first.");
                tips.SetToolTip(mark, explanation);
                tips.SetToolTip(color, explanation + " #" + rgb.ToString("X6", CultureInfo.InvariantCulture));
                color.AccessibleName = Tr("Farbe der Umschalttaste wählen", "Choose the mode-switch key color");
            }

            protected override void Dispose(bool disposing)
            { if (disposing) tips.Dispose(); base.Dispose(disposing); }
        }
    }
}
