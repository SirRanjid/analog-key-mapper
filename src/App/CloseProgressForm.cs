using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Tk75.App
{
    // A noninteractive status window. Progress advances only when an actual
    // shutdown phase finishes; elapsed time never invents a percentage.
    internal sealed class CloseProgressForm : Form
    {
        readonly Label[] phases = new Label[4];
        readonly ShutdownPhaseState[] states = new ShutdownPhaseState[4];
        readonly SleekProgressBar progress = new SleekProgressBar { Dock = DockStyle.Fill, Maximum = 4, Height = 7, Margin = new Padding(0, 10, 0, 4) };
        readonly Label detail = new Label { Dock = DockStyle.Fill, AutoSize = false, AutoEllipsis = true };
        readonly string[] names;
        bool needsPaint = true;

        internal CloseProgressForm()
        {
            Text = "Analog Key Mapper"; ShowInTaskbar = false; ControlBox = false;
            FormBorderStyle = FormBorderStyle.FixedDialog; StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Font; ClientSize = new Size(460, 258); MinimumSize = Size;
            names = new[] { UiText.Get("Profil speichern", "Save profile"), UiText.Get("Controller beenden", "Stop controllers"),
                UiText.Get("Tastaturbeleuchtung wiederherstellen", "Restore keyboard lighting"), UiText.Get("Verbindungen und Hilfsprozesse schließen", "Close connections and helper processes") };
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22, 16, 22, 14), ColumnCount = 1, RowCount = 7 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
            for (int phase = 0; phase < phases.Length; phase++) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 31));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var title = new Label { Text = UiText.Get("Analog Key Mapper wird beendet", "Closing Analog Key Mapper"), Dock = DockStyle.Fill, AutoEllipsis = true };
            layout.Controls.Add(title, 0, 0);
            for (int phase = 0; phase < phases.Length; phase++)
            {
                phases[phase] = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
                layout.Controls.Add(phases[phase], 0, phase + 1);
            }
            layout.Controls.Add(progress, 0, 5); layout.Controls.Add(detail, 0, 6); Controls.Add(layout);
            ModernTheme.Apply(this);
            for (int phase = 0; phase < phases.Length; phase++) RenderPhase(phase);
            detail.Text = UiText.Get("Bitte kurz warten …", "Please wait …");
        }
        internal void SetPhase(int phase, ShutdownPhaseState state)
        {
            if (states[phase] == state) return;
            states[phase] = state; RenderPhase(phase); needsPaint = true;
            progress.Value = states.Count(value => value == ShutdownPhaseState.Completed);
        }
        void RenderPhase(int phase)
        {
            ShutdownPhaseState state = states[phase];
            phases[phase].Text = (state == ShutdownPhaseState.Completed ? "✓  " : state == ShutdownPhaseState.Failed ? "!  " : state == ShutdownPhaseState.Running ? "›  " : "·  ") + names[phase];
            phases[phase].ForeColor = state == ShutdownPhaseState.Failed ? ModernTheme.DangerColor : state == ShutdownPhaseState.Running ? ModernTheme.Accent : state == ShutdownPhaseState.Completed ? ModernTheme.Foreground : ModernTheme.Muted;
            phases[phase].AccessibleDescription = state.ToString();
        }
        internal void SetDetail(string text) { if (detail.Text != text) { detail.Text = text; needsPaint = true; } }
        // Synchronous paint is intentional during WM_ENDSESSION: dispatching
        // arbitrary application messages could reenter close or device callbacks.
        internal void PaintProgress() { if (needsPaint && Visible && !IsDisposed) { Invalidate(true); Update(); needsPaint = false; } }
        protected override bool ProcessDialogKey(Keys keyData) { return true; }
    }
}
