using System;
using Tk75.Mapping;

namespace Tk75.App
{
    // Presentation never selects or promises a particular virtual output backend.
    public enum ControllerStyle { Xbox, PlayStation5 }

    public static class ControllerPresentation
    {
        public static string Name(ControllerStyle style)
        { Validate(style); return style == ControllerStyle.PlayStation5 ? "PlayStation 5" : "Xbox"; }
        public static string Label(OutputTarget target, ControllerStyle style)
        {
            Validate(style);
            switch (target)
            {
                case OutputTarget.LeftXPositive: return UiText.Get("Linker Stick · rechts (+X)", "Left stick · right (+X)");
                case OutputTarget.LeftXNegative: return UiText.Get("Linker Stick · links (−X)", "Left stick · left (−X)");
                case OutputTarget.LeftYPositive: return UiText.Get("Linker Stick · oben (+Y)", "Left stick · up (+Y)");
                case OutputTarget.LeftYNegative: return UiText.Get("Linker Stick · unten (−Y)", "Left stick · down (−Y)");
                case OutputTarget.RightXPositive: return UiText.Get("Rechter Stick · rechts (+X)", "Right stick · right (+X)");
                case OutputTarget.RightXNegative: return UiText.Get("Rechter Stick · links (−X)", "Right stick · left (−X)");
                case OutputTarget.RightYPositive: return UiText.Get("Rechter Stick · oben (+Y)", "Right stick · up (+Y)");
                case OutputTarget.RightYNegative: return UiText.Get("Rechter Stick · unten (−Y)", "Right stick · down (−Y)");
                case OutputTarget.LeftTrigger: return UiText.Get("Linker Trigger · ", "Left trigger · ") + ShortLabel(target, style);
                case OutputTarget.RightTrigger: return UiText.Get("Rechter Trigger · ", "Right trigger · ") + ShortLabel(target, style);
                case OutputTarget.LeftThumb: return UiText.Get("Linker Stick-Klick · L3", "Left stick click · L3");
                case OutputTarget.RightThumb: return UiText.Get("Rechter Stick-Klick · R3", "Right stick click · R3");
                case OutputTarget.DpadUp: return UiText.Get("Steuerkreuz · oben", "D-pad · up");
                case OutputTarget.DpadDown: return UiText.Get("Steuerkreuz · unten", "D-pad · down");
                case OutputTarget.DpadLeft: return UiText.Get("Steuerkreuz · links", "D-pad · left");
                case OutputTarget.DpadRight: return UiText.Get("Steuerkreuz · rechts", "D-pad · right");
                case OutputTarget.A: return style == ControllerStyle.PlayStation5 ? UiText.Get("Kreuz · ×", "Cross · ×") : "A";
                case OutputTarget.B: return style == ControllerStyle.PlayStation5 ? UiText.Get("Kreis · ○", "Circle · ○") : "B";
                case OutputTarget.X: return style == ControllerStyle.PlayStation5 ? UiText.Get("Quadrat · □", "Square · □") : "X";
                case OutputTarget.Y: return style == ControllerStyle.PlayStation5 ? UiText.Get("Dreieck · △", "Triangle · △") : "Y";
                case OutputTarget.LB: case OutputTarget.RB: case OutputTarget.Back: case OutputTarget.Start:
                    return ShortLabel(target, style);
                default: throw new ArgumentOutOfRangeException("target");
            }
        }
        internal static string ShortLabel(OutputTarget target, ControllerStyle style)
        {
            bool ps = style == ControllerStyle.PlayStation5;
            switch (target)
            {
                case OutputTarget.LeftTrigger: return ps ? "L2" : "LT";
                case OutputTarget.RightTrigger: return ps ? "R2" : "RT";
                case OutputTarget.LB: return ps ? "L1" : "LB";
                case OutputTarget.RB: return ps ? "R1" : "RB";
                case OutputTarget.Back: return ps ? "Create" : "Back";
                case OutputTarget.Start: return ps ? "Options" : "Start";
                case OutputTarget.LeftThumb: return "L3";
                case OutputTarget.RightThumb: return "R3";
                case OutputTarget.A: return ps ? "×" : "A";
                case OutputTarget.B: return ps ? "○" : "B";
                case OutputTarget.X: return ps ? "□" : "X";
                case OutputTarget.Y: return ps ? "△" : "Y";
                default: return "";
            }
        }
        internal static ushort ButtonMask(OutputTarget target)
        {
            switch (target)
            {
                case OutputTarget.DpadUp: return 0x0001;
                case OutputTarget.DpadDown: return 0x0002;
                case OutputTarget.DpadLeft: return 0x0004;
                case OutputTarget.DpadRight: return 0x0008;
                case OutputTarget.Start: return 0x0010;
                case OutputTarget.Back: return 0x0020;
                case OutputTarget.LeftThumb: return 0x0040;
                case OutputTarget.RightThumb: return 0x0080;
                case OutputTarget.LB: return 0x0100;
                case OutputTarget.RB: return 0x0200;
                case OutputTarget.A: return 0x1000;
                case OutputTarget.B: return 0x2000;
                case OutputTarget.X: return 0x4000;
                case OutputTarget.Y: return 0x8000;
                default: return 0;
            }
        }
        internal static void Validate(ControllerStyle style)
        { if (style != ControllerStyle.Xbox && style != ControllerStyle.PlayStation5) throw new ArgumentOutOfRangeException("style"); }
    }
}
