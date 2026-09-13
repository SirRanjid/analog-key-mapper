using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;
using Tk75.Mapping;

namespace Tk75.App
{
    public sealed partial class CurveCanvas
    {
        readonly SleekButton viewButton = new SleekButton { Height = 25, TabStop = true };
        bool shapeEditing;
        string activeSetting;
        Control inputRangeRail, outputRangeRail;
        public void SetRangeRails(Control input, Control output)
        {
            inputRangeRail = input; outputRangeRail = output;
            if (input != null && input.Parent != this) Controls.Add(input);
            if (output != null && output.Parent != this) Controls.Add(output);
            PositionRangeRails(); Invalidate();
        }
        void PositionRangeRails()
        {
            RectangleF plot = Plot;
            int top = (int)Math.Round(plot.Top) - 6, height = (int)Math.Round(plot.Height) + 12;
            if (inputRangeRail != null) inputRangeRail.SetBounds(5, top, 22, height);
            if (outputRangeRail != null) outputRangeRail.SetBounds(Math.Max(0, Width - 20), top, 18, height);
        }
        void DrawRangeRailLabels(Graphics graphics)
        {
            RectangleF plot = Plot;
            if (inputRangeRail != null) TextRenderer.DrawText(graphics, "IN", Font, new Rectangle(3, (int)plot.Bottom + 7, 28, 18), ModernTheme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            if (outputRangeRail != null) TextRenderer.DrawText(graphics, "OUT", Font, new Rectangle(Math.Max(0, Width - 28), (int)plot.Bottom + 7, 28, 18), ModernTheme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }
        public bool ShapeEditing
        {
            get { return shapeEditing; }
            set
            {
                if (shapeEditing == value) return;
                CancelEdit(); shapeEditing = value; inspectedInput = null; hovered = hoveredPart = -1;
                if (value) { activeInputField = InputActivationFields.None; activeSetting = null; }
                UpdateViewButton(); UpdateTip(); Invalidate();
            }
        }
        public string ActiveSetting
        {
            get { return activeSetting; }
            set
            {
                if (activeSetting == value && (value == null || activeInputField == InputActivationFields.None)) return;
                activeSetting = value;
                if (value != null) { CancelEdit(); shapeEditing = false; activeInputField = InputActivationFields.None; inspectedInput = null; }
                UpdateViewButton(); UpdateTip(); Invalidate();
            }
        }
        void BuildViewButton()
        {
            Controls.Add(viewButton);
            viewButton.Click += delegate {
                if (DynamicPreviewVisible) { activeSetting = null; activeInputField = InputActivationFields.None; UpdateViewButton(); UpdateTip(); Invalidate(); }
                else ShapeEditing = !ShapeEditing;
            };
            UpdateViewButton();
        }
        void UpdateViewButton()
        {
            if (viewButton == null) return;
            viewButton.SetBounds(Math.Max(58, Width - 104), 4, Math.Min(98, Math.Max(70, Width - 64)), 25);
            viewButton.Text = shapeEditing || DynamicPreviewVisible ? UiText.Get("Antwort", "Response") : UiText.Get("Form ändern", "Edit shape");
            viewButton.Enabled = settings != null || DynamicPreviewVisible;
            viewButton.AccessibleName = viewButton.Text;
            tips.SetToolTip(viewButton, shapeEditing || DynamicPreviewVisible ? UiText.Get("Die tatsächliche Ausgabe mit Totbereichen, Stärke und Ausgabegrenzen anzeigen.",
                "Show the effective output including deadzones, strength and output limits.") : UiText.Get("Kurvenform bearbeiten: eigene Punkte setzen und ziehen, Bézier-Griffe verändern.",
                "Edit the curve shape: add and drag custom points or adjust Bézier handles."));
        }
        static string Percent(double value) { return (value * 100).ToString("0.#", CultureInfo.CurrentCulture) + "%"; }
        string ResponseCaption(SignalSettings shown)
        {
            if (activeInputField == InputActivationFields.Actuation && inputPreview != null)
                return (inputPreviewConfigured ? UiText.Get("Tasten-Aktuation · ", "Key actuation · ") : UiText.Get("Aktuation Vorschau · ", "Actuation preview · ")) + Percent(inputPreview.ActuationPoint);
            if (activeSetting != null)
            {
                switch (activeSetting)
                {
                    case "TopDeadzone": return UiText.Get("Ruhebereich · ", "Top deadzone · ") + Percent(shown.TopDeadzone);
                    case "BottomDeadzone": return UiText.Get("Anschlagbereich · ", "Bottom deadzone · ") + Percent(shown.BottomDeadzone);
                    case "Hysteresis": return UiText.Get("Ein/Aus-Abstand · ", "Press/release gap · ") + Percent(shown.Hysteresis);
                    case "MinOutput": return UiText.Get("Ausgabe min. · ", "Min. output · ") + Percent(shown.MinOutput);
                    case "MaxOutput": return UiText.Get("Ausgabe max. · ", "Max. output · ") + Percent(shown.MaxOutput);
                    case "OutputDeadzone": return UiText.Get("Ausgabesperre · ", "Output deadzone · ") + Percent(shown.OutputDeadzone);
                    case "ButtonThreshold": return UiText.Get("Button-Schwelle · ", "Button threshold · ") + Percent(shown.ButtonThreshold);
                    case "Scale": return UiText.Get("Ausgabestärke · ", "Output strength · ") + shown.Scale.ToString("0.##", CultureInfo.CurrentCulture) + "×";
                    case "Exponent": return UiText.Get("Kurvenstärke · ", "Curve strength · ") + shown.Exponent.ToString("0.##", CultureInfo.CurrentCulture) + "×";
                }
            }
            if (shown.SmoothingTimeConstant > 0 || activeSetting == "SmoothingTimeConstant")
                return UiText.Get("Eingeschwungen · ", "Settled · ") + (shown.SmoothingTimeConstant * 1000).ToString("0.#", CultureInfo.CurrentCulture) + UiText.Get(" ms Glättung", " ms smoothing");
            if (mixed) return UiText.Get("Gemischt · erste Zuordnung", "Mixed · first mapping");
            return shown.Hysteresis > 0 ? UiText.Get("Drücken ━  Loslassen ┄", "Press ━  Release ┄") : UiText.Get("Druck → Ausgabe", "Pressure → output");
        }
        void DrawReleaseResponse(Graphics graphics, double[] samples)
        {
            RectangleF plot = Plot; var points = new PointF[samples.Length];
            for (int i = 0; i < samples.Length; i++)
                points[i] = new PointF(plot.Left + i / (float)(samples.Length - 1) * plot.Width, plot.Bottom - (float)samples[i] * plot.Height);
            using (var pen = new Pen(ModernTheme.Foreground, 1.3f))
            { pen.DashStyle = DashStyle.Dash; graphics.DrawLines(pen, points); }
        }
        void DrawResponseGuides(Graphics graphics, SignalSettings shown)
        {
            RectangleF plot = Plot;
            using (var shade = new SolidBrush(Color.FromArgb(32, ModernTheme.Muted)))
            {
                if (shown.TopDeadzone > 0) graphics.FillRectangle(shade, plot.Left, plot.Top, (float)shown.TopDeadzone * plot.Width, plot.Height);
                if (shown.BottomDeadzone > 0) graphics.FillRectangle(shade, plot.Right - (float)shown.BottomDeadzone * plot.Width, plot.Top, (float)shown.BottomDeadzone * plot.Width, plot.Height);
            }
            if (shown.Hysteresis > 0)
                using (var shade = new SolidBrush(Color.FromArgb(28, ModernTheme.Accent)))
                    graphics.FillRectangle(shade, plot.Left + (float)shown.TopDeadzone * plot.Width, plot.Top, (float)shown.Hysteresis * plot.Width, plot.Height);
            if (shown.TopDeadzone > 0 || activeSetting == "TopDeadzone") DrawResponseGuide(graphics, shown.TopDeadzone, true, "TopDeadzone");
            if (shown.BottomDeadzone > 0 || activeSetting == "BottomDeadzone") DrawResponseGuide(graphics, 1 - shown.BottomDeadzone, true, "BottomDeadzone");
            if (shown.Hysteresis > 0 || activeSetting == "Hysteresis") DrawResponseGuide(graphics, shown.TopDeadzone + shown.Hysteresis, true, "Hysteresis");
            if (shown.MinOutput > 0 || activeSetting == "MinOutput") DrawResponseGuide(graphics, shown.MinOutput, false, "MinOutput");
            if (shown.MaxOutput < 1 || activeSetting == "MaxOutput") DrawResponseGuide(graphics, shown.MaxOutput, false, "MaxOutput");
            // Digital threshold is a guide, not a change in continuous output.
            DrawResponseGuide(graphics, shown.ButtonThreshold, false, "ButtonThreshold");
            DrawInputActuationGuide(graphics);
            if (activeSetting == "OutputDeadzone")
            {
                int first = 0; while (first < sampledResponse.Press.Length && sampledResponse.Press[first] <= 0) first++;
                if (first < sampledResponse.Press.Length) DrawResponseGuide(graphics, first / (double)(sampledResponse.Press.Length - 1), true, "OutputDeadzone");
            }
        }
        void DrawResponseGuide(Graphics graphics, double value, bool vertical, string property)
        {
            RectangleF plot = Plot; bool active = activeSetting == property;
            using (var pen = new Pen(active ? ModernTheme.AccentHover : Color.FromArgb(105, ModernTheme.Muted), active ? 1.5f : 1))
            {
                pen.DashStyle = DashStyle.Dot;
                if (vertical)
                { float x = plot.Left + (float)value * plot.Width; graphics.DrawLine(pen, x, plot.Top, x, plot.Bottom); graphics.DrawLine(pen, x, plot.Bottom, x, plot.Bottom + 4); }
                else
                { float y = plot.Bottom - (float)value * plot.Height; graphics.DrawLine(pen, plot.Left, y, plot.Right, y); graphics.DrawLine(pen, plot.Left - 4, y, plot.Left, y); }
            }
        }
        string ResponseHelp(SignalSettings shown)
        {
            string text = UiText.Get("Horizontal: kalibrierter Druck vor den Totbereichen. Vertikal: berechnete Ausgabe nach Kurve, Ausgabestärke, Ausgabesperre und Ausgabegrenzen. Durchgezogen = Drücken; gestrichelt = Loslassen. Schattierte Bereiche und gepunktete Linien sind Orientierungshilfen, keine Ziehpunkte. Werte mit den Reglern darunter ändern; eigene Kurvenpunkte über „Form ändern“ bearbeiten.",
                "Horizontal: calibrated pressure before deadzones. Vertical: output after the curve, output strength, output deadzone and output limits. Solid = pressing; dashed = releasing. Shaded bands and dotted lines are guides, not drag handles. Change values with the sliders below; use Edit shape to work with custom curve points.");
            if (shown == null) return text;
            text += "\n\n" + UiText.Get("Drücken oberhalb ", "Press above ") + Percent(shown.TopDeadzone + shown.Hysteresis) + UiText.Get("; Loslassen bei ", "; release at ") + Percent(shown.TopDeadzone) + ".";
            text += "\n" + UiText.Get("Ausgabegrenzen: ", "Output limits: ") + Percent(shown.MinOutput) + "–" + Percent(shown.MaxOutput) + UiText.Get(". Button-Schwelle (gepunktet): ", ". Button threshold (dotted): ") + Percent(shown.ButtonThreshold) + UiText.Get("; gilt nur für digitale Controller-Buttons.", "; applies only to digital controller buttons.");
            text += "\n" + UiText.Get("Die Kurve zeigt die eingeschwungene Ausgabe. Glättung wirkt zeitlich und verschiebt diese Kurve nicht: ",
                "The graph shows settled output. Smoothing acts over time and does not move this curve: ") + (shown.SmoothingTimeConstant * 1000).ToString("0.#", CultureInfo.CurrentCulture) + " ms.";
            if (mixed) text += "\n\n" + UiText.Get("Die ausgewählten Zuordnungen haben unterschiedliche Einstellungen. Vorschau der ersten Zuordnung; Änderungen gelten für alle ausgewählten Zuordnungen.",
                "Selected mappings have different settings. Preview shows the first mapping; changes apply to every selected mapping.");
            text += InputPreviewHelp();
            return text;
        }
    }
}
