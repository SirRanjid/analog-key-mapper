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
        KeyInputSettings inputPreview;
        bool inputPreviewConfigured, inputPreviewMixed;
        InputActivationFields activeInputField;
        SignalSettings sampledStepSource;
        SmoothingStepPreview stepPreview;
        public InputActivationFields ActiveInputField { get { return activeInputField; } }
        public bool InputPreviewConfigured { get { return inputPreviewConfigured; } }
        public KeyInputSettings InputPreview { get { return CopyInputPreview(inputPreview); } }
        bool DynamicPreviewVisible
        {
            get { return !shapeEditing && (settings != null && activeSetting == "SmoothingTimeConstant" || inputPreview != null &&
                (activeInputField == InputActivationFields.Release || activeInputField == InputActivationFields.Press || settings == null && activeInputField == InputActivationFields.Actuation)); }
        }
        static KeyInputSettings CopyInputPreview(KeyInputSettings value)
        { return value == null ? null : new KeyInputSettings { KeyIndex = value.KeyIndex, RapidTriggerEnabled = value.RapidTriggerEnabled,
            ActuationPoint = value.ActuationPoint, ReleaseMovement = value.ReleaseMovement, PressMovement = value.ActuationPoint }; }
        public static bool SameInputPreview(KeyInputSettings first, KeyInputSettings second)
        { return first == null || second == null ? first == null && second == null : first.RapidTriggerEnabled == second.RapidTriggerEnabled &&
            first.ActuationPoint == second.ActuationPoint && first.ReleaseMovement == second.ReleaseMovement; }
        public void UpdateInputPreview(KeyInputSettings value, bool configured, bool isMixed, InputActivationFields field)
        {
            if (SameInputPreview(inputPreview, value) && inputPreviewConfigured == configured && inputPreviewMixed == isMixed && activeInputField == field) return;
            inputPreview = CopyInputPreview(value); inputPreviewConfigured = configured; inputPreviewMixed = isMixed; activeInputField = field;
            if (field != InputActivationFields.None) { CancelEdit(); shapeEditing = false; activeSetting = null; inspectedInput = null; }
            UpdateViewButton(); UpdateTip(); Invalidate();
        }
        void DrawInputActuationGuide(Graphics graphics)
        {
            if (inputPreview == null || !inputPreviewConfigured && activeInputField != InputActivationFields.Actuation) return;
            RectangleF plot = Plot; float x = plot.Left + (float)inputPreview.ActuationPoint * plot.Width;
            using (var pen = new Pen(activeInputField == InputActivationFields.Actuation ? ModernTheme.AccentHover : Color.FromArgb(150, ModernTheme.Muted), 1.5f))
            { pen.DashStyle = DashStyle.DashDot; graphics.DrawLine(pen, x, plot.Top, x, plot.Bottom); graphics.DrawLine(pen, x - 3, plot.Top, x + 3, plot.Top); }
        }
        string InputPreviewHelp()
        {
            if (inputPreview == null) return "";
            string help = "\n\n" + UiText.Get("Die strichpunktierte Aktuationsmarke gehört zur separaten physischen Tastenstufe. Die dargestellte Signal-Kennlinie enthält diese Freigabe nicht. Rapid Trigger nutzt Bewegungsabstände ab dem letzten Hoch-/Tiefpunkt, keine festen Druckpositionen; fokussiere Loslassen für ein Bewegungsbeispiel. Erneutes Drücken verwendet denselben Weg wie Auslösen.",
                "The dash-dot actuation guide belongs to the separate physical-key stage. The signal response does not include that input gate. Rapid Trigger uses movement distances from the last peak/valley, not fixed pressure positions; focus Release for a movement example. Repress uses the actuation amount.");
            if (!inputPreviewConfigured) help += "\n" + UiText.Get("Diese Taste arbeitet bisher kontinuierlich. Ein fokussierter Aktuationswert ist eine Vorschau; eine Einstellung ist erst nach einer Änderung gespeichert.",
                "This key currently uses continuous pressure. A focused actuation value is a preview; it becomes a configured setting after a change is applied.");
            if (inputPreviewMixed) help += "\n" + UiText.Get("Tasteneinstellungen gemischt: gezeigt wird die erste ausgewählte Taste.", "Key settings are mixed: the first selected key is shown.");
            return help;
        }
        string DynamicPreviewHelp()
        {
            if (activeSetting == "SmoothingTimeConstant")
                return UiText.Get("Zeitansicht für Glättung: Die Eingabe springt bei 0 ms von Ruhe auf vollen Druck. Die Linie zeigt die berechnete Ausgabe innerhalb einer Sekunde einschließlich Ausgabegrenzen. Die gestrichelte Linie ist der eingeschwungene Zielwert. Nach einer Zeitkonstante sind etwa 63 % dieses Ziels erreicht. 0 ms bedeutet sofortige Reaktion. Loslassen bleibt sofort; dies ist keine animierte Druckkurve.",
                    "Smoothing time view: input steps from rest to full pressure at 0 ms. The line shows calculated output over one second, including output limits. The dashed line is the settled target. After one time constant, about 63% of that target is reached. Zero means immediate response. Release stays immediate; this is not an animated pressure curve.");
            if (activeInputField == InputActivationFields.Actuation)
                return UiText.Get("Physische Tastenstufe: oben losgelassen, unten voll gedrückt. Die Marke zeigt den Auslösepunkt innerhalb des individuellen Min/Max-Bereichs. Sie ist keine Änderung der Controller-Kurvenform.",
                    "Physical-key stage: released at the top, fully pressed at the bottom. The marker shows actuation within this key's min/max range. It does not change the controller curve shape.") + InputPreviewHelp();
            string text = UiText.Get("Beispielbewegung, keine Messung: Horizontal folgen Bewegungsabschnitte ohne feste Zeitskala. Vertikal ist der Druck: 0 % oben, 100 % unten, passend zu den Reglern. Der Pfeil zeigt den konfigurierten relativen Weg. Loslassen zählt ab einem Hochpunkt, erneutes Drücken ab einem Tiefpunkt. Andere Ausgangspositionen verschieben den Pfeil; der Weg bleibt gleich.",
                "Example movement, not a measurement: horizontal stages have no fixed time scale. Vertical pressure is 0% at the top and 100% at the bottom, matching the sliders. The arrow shows the configured relative travel. Release is measured from a peak; repress from a valley. Different starting positions move the arrow while preserving its length.");
            return text + "\n" + UiText.Get("Vollständiges Loslassen setzt Rapid Trigger zurück. Danach gilt wieder der erste Aktuationspunkt; ein erneuter relativer Druckweg von 100 % ist daher kein gültiges Beispiel ohne Rücksetzen.",
                "Fully releasing resets Rapid Trigger. The next press uses initial actuation again; a 100% relative repress cannot be illustrated without this reset.") + InputPreviewHelp();
        }
        bool DrawDynamicPreview(Graphics graphics)
        {
            if (!DynamicPreviewVisible) return false;
            bool time = activeSetting == "SmoothingTimeConstant";
            string title = time ? UiText.Get("Glättung", "Smoothing") : activeInputField == InputActivationFields.Actuation ? UiText.Get("Auslösen", "Actuation") :
                activeInputField == InputActivationFields.Release ? UiText.Get("Loslassen", "Release") : UiText.Get("Erneut", "Repress");
            TextRenderer.DrawText(graphics, title, Font, new Rectangle(8, 5, Math.Max(1, viewButton.Left - 12), 24), ModernTheme.Foreground,
                TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            RectangleF plot = Plot;
            using (var pen = new Pen(ModernTheme.Border)) for (int i = 0; i <= 4; i++)
            { float t = i / 4f; graphics.DrawLine(pen, plot.Left, plot.Top + plot.Height * t, plot.Right, plot.Top + plot.Height * t); graphics.DrawLine(pen, plot.Left + plot.Width * t, plot.Top, plot.Left + plot.Width * t, plot.Bottom); }
            using (var brush = new SolidBrush(ModernTheme.Muted))
            {
                graphics.DrawString(time ? "100%" : "0%", Font, brush, plot.Left + 3, plot.Top + 1);
                graphics.DrawString(time ? "0" : "100%", Font, brush, plot.Left + 3, plot.Bottom - Font.Height - 1);
                if (time) graphics.DrawString("0", Font, brush, plot.Left - 3, plot.Bottom + 5);
                string right = time ? "1000 ms" : UiText.Get("Bewegung →", "Movement →");
                Size rightSize = TextRenderer.MeasureText(right, Font, Size.Empty, TextFormatFlags.NoPadding);
                graphics.DrawString(right, Font, brush, Math.Max(plot.Left + 10, plot.Right - rightSize.Width), plot.Bottom + 5);
            }
            DrawRangeRailLabels(graphics);
            if (time) DrawSmoothingStep(graphics); else DrawInputMotion(graphics);
            return true;
        }
        void DrawSmoothingStep(Graphics graphics)
        {
            if (stepPreview == null || !CurveResponsePreview.SameResponse(sampledStepSource, settings) || sampledStepSource.SmoothingTimeConstant != settings.SmoothingTimeConstant)
            { sampledStepSource = CopySettings(settings); stepPreview = SmoothingStepPreview.Create(settings, 1, 256); }
            RectangleF plot = Plot; var points = new PointF[stepPreview.Output.Length];
            for (int i = 0; i < points.Length; i++) points[i] = new PointF(plot.Left + i / (float)(points.Length - 1) * plot.Width, plot.Bottom - (float)stepPreview.Output[i] * plot.Height);
            using (var guide = new Pen(ModernTheme.Muted, 1))
            {
                guide.DashStyle = DashStyle.Dash; float targetY = plot.Bottom - (float)stepPreview.SettledOutput * plot.Height;
                graphics.DrawLine(guide, plot.Left, targetY, plot.Right, targetY);
                if (settings.SmoothingTimeConstant > 0 && settings.SmoothingTimeConstant <= 1)
                { float x = plot.Left + (float)settings.SmoothingTimeConstant * plot.Width, y = plot.Bottom - (float)(stepPreview.SettledOutput * (1 - Math.Exp(-1))) * plot.Height;
                    graphics.DrawLine(guide, x, plot.Bottom, x, y); graphics.DrawLine(guide, plot.Left, y, x, y); }
            }
            using (var line = new Pen(ModernTheme.Accent, 2.5f))
            { if (settings.SmoothingTimeConstant == 0) graphics.DrawLine(line, plot.Left, plot.Bottom, plot.Left, points[0].Y); graphics.DrawLines(line, points); }
            string caption = settings.SmoothingTimeConstant == 0 ? UiText.Get("Zeitsprung · sofort", "Time step · immediate") :
                "63% · " + (settings.SmoothingTimeConstant * 1000).ToString("0.#", CultureInfo.CurrentCulture) + " ms";
            DrawDynamicCaption(graphics, caption);
        }
        void DrawInputMotion(Graphics graphics)
        {
            RectangleF plot = Plot;
            if (activeInputField == InputActivationFields.Actuation)
            {
                float y = plot.Top + (float)inputPreview.ActuationPoint * plot.Height;
                using (var shade = new SolidBrush(Color.FromArgb(35, ModernTheme.Accent))) graphics.FillRectangle(shade, plot.Left, y, plot.Width, plot.Bottom - y);
                using (var line = new Pen(ModernTheme.AccentHover, 2)) graphics.DrawLine(line, plot.Left, y, plot.Right, y);
                DrawDynamicCaption(graphics, (inputPreviewConfigured ? UiText.Get("Tasten-Aktuation · ", "Key actuation · ") : UiText.Get("Vorschau · ", "Preview · ")) + Percent(inputPreview.ActuationPoint));
                return;
            }
            var motion = RapidTriggerMovementPreview.Create(inputPreview, activeInputField);
            if (!inputPreview.RapidTriggerEnabled || motion.RequiresFreshActuation)
            {
                string message = !inputPreview.RapidTriggerEnabled ? UiText.Get("Rapid Trigger ist aus", "Rapid Trigger is off") : UiText.Get("Voll losgelassen:\nAktuation gilt erneut", "Fully released:\nactuation applies again");
                TextRenderer.DrawText(graphics, message, Font, Rectangle.Round(plot), ModernTheme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                DrawDynamicCaption(graphics, UiText.Get("Beispiel · kein RT-Auslösen", "Example · no RT retrigger")); return;
            }
            var points = new PointF[motion.Path.Count];
            for (int i = 0; i < points.Length; i++) points[i] = new PointF(plot.Left + (float)motion.Path[i].X * plot.Width, plot.Top + (float)motion.Path[i].Y * plot.Height);
            using (var line = new Pen(ModernTheme.Muted, 1.5f)) graphics.DrawLines(line, points);
            float arrowX = plot.Left + plot.Width * .74f, fromY = plot.Top + (float)motion.FromPressure * plot.Height, toY = plot.Top + (float)motion.ToPressure * plot.Height;
            using (var guide = new Pen(ModernTheme.Muted, 1))
            { guide.DashStyle = DashStyle.Dot; graphics.DrawLine(guide, plot.Left, fromY, plot.Right, fromY); graphics.DrawLine(guide, plot.Left, toY, plot.Right, toY); }
            using (var arrow = new Pen(ModernTheme.AccentHover, 2))
            using (var cap = new AdjustableArrowCap(3, 4))
            { arrow.CustomEndCap = cap; graphics.DrawLine(arrow, arrowX, fromY, arrowX, toY); }
            double amount = activeInputField == InputActivationFields.Release ? inputPreview.ReleaseMovement : inputPreview.ActuationPoint;
            DrawDynamicCaption(graphics, UiText.Get("Beispiel · Weg ", "Example · travel ") + Percent(amount));
        }
        void DrawDynamicCaption(Graphics graphics, string text)
        { TextRenderer.DrawText(graphics, text, Font, new Rectangle(6, Height - 24, Math.Max(1, Width - 12), 20), ModernTheme.Foreground,
            TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix); }
    }
}
