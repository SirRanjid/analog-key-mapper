using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;

namespace Tk75.App
{
    public enum KeyboardLegendStyle { Qwerty, Qwertz }

    // Geometry and protocol identity stay independent of the printed legend.
    public sealed class KeyboardKeyDefinition
    {
        public string Code { get; private set; }
        public int? KeyIndex { get; private set; }
        public RectangleF Bounds { get; private set; }
        public bool IsKnob { get; private set; }
        public bool VerifiedOnHardware { get; private set; }
        public KeyboardKeyDefinition(string code, int? index, RectangleF bounds, bool knob, bool verified)
        {
            if (String.IsNullOrWhiteSpace(code)) throw new ArgumentException("Tastencode fehlt.", "code");
            if (index.HasValue && (index.Value < 0 || index.Value > 255)) throw new ArgumentOutOfRangeException("index");
            if (!Finite(bounds.X) || !Finite(bounds.Y) || !Finite(bounds.Width) || !Finite(bounds.Height) ||
                bounds.X < 0 || bounds.Y < 0 || bounds.Width <= 0 || bounds.Height <= 0) throw new ArgumentException("Ungültige Tastengeometrie.", "bounds");
            Code = code; KeyIndex = index; Bounds = bounds; IsKnob = knob; VerifiedOnHardware = verified;
        }
        static bool Finite(float value) { return !Single.IsNaN(value) && !Single.IsInfinity(value); }
        public string GetLegend(KeyboardLegendStyle style)
        {
            if (style != KeyboardLegendStyle.Qwerty && style != KeyboardLegendStyle.Qwertz) throw new ArgumentOutOfRangeException("style");
            bool de = style == KeyboardLegendStyle.Qwertz;
            if (Code.StartsWith("Key", StringComparison.Ordinal) && Code.Length == 4)
            { string letter = Code.Substring(3); return de && letter == "Y" ? "Z" : de && letter == "Z" ? "Y" : letter; }
            if (Code.StartsWith("Digit", StringComparison.Ordinal) && Code.Length == 6) return Code.Substring(5);
            switch (Code)
            {
                case "Escape": return "Esc";
                case "Backspace": return "⌫";
                case "CapsLock": return "Caps";
                case "ControlLeft": case "ControlRight": return de ? "Strg" : "Ctrl";
                case "ShiftLeft": case "ShiftRight": return "Shift";
                case "AltLeft": return "Alt";
                case "AltRight": return de ? "AltGr" : "Alt";
                case "MetaLeft": return "Win";
                case "Space": return de ? "Leertaste" : "Space";
                case "Delete": return de ? "Entf" : "Del";
                case "Home": return de ? "Pos1" : "Home";
                case "PageUp": return de ? "Bild↑" : "PgUp";
                case "PageDown": return de ? "Bild↓" : "PgDn";
                case "ArrowLeft": return "←";
                case "ArrowRight": return "→";
                case "ArrowUp": return "↑";
                case "ArrowDown": return "↓";
                case "Backquote": return de ? "^" : "`";
                case "Minus": return de ? "ß" : "-";
                case "Equal": return de ? "´" : "=";
                case "BracketLeft": return de ? "Ü" : "[";
                case "BracketRight": return de ? "+" : "]";
                case "Semicolon": return de ? "Ö" : ";";
                case "Quote": return de ? "Ä" : "'";
                // German ANSI legend inferred from the manufacturer's ISO # key
                // at the same scan code (43); this never changes a hardware index.
                case "Backslash": return de ? "#" : "\\";
                case "IntlRo": return de ? "#" : "#";
                case "IntlBackslash": return de ? "<" : "\\";
                case "Comma": return ",";
                case "Period": return ".";
                case "Slash": return de ? "-" : "/";
                case "AudioVolumeDown": return "−";
                case "AudioVolumeUp": return "+";
                case "MediaPlayPause": return "•";
                default: return Code;
            }
        }
    }

    public sealed partial class KeyboardLayout
    {
        readonly ReadOnlyCollection<KeyboardKeyDefinition> keys;
        readonly Dictionary<int, KeyboardKeyDefinition> byIndex = new Dictionary<int, KeyboardKeyDefinition>();
        readonly Dictionary<string, KeyboardKeyDefinition> byCode = new Dictionary<string, KeyboardKeyDefinition>(StringComparer.Ordinal);
        public string Name { get; private set; }
        public SizeF Size { get; private set; }
        public IList<KeyboardKeyDefinition> Keys { get { return keys; } }
        public string SourceDescription { get; private set; }
        public KeyboardLayout(string name, SizeF size, IEnumerable<KeyboardKeyDefinition> definitions, string sourceDescription)
        {
            if (String.IsNullOrWhiteSpace(name)) throw new ArgumentException("Layoutname fehlt.", "name");
            if (Single.IsNaN(size.Width) || Single.IsNaN(size.Height) || Single.IsInfinity(size.Width) || Single.IsInfinity(size.Height) || size.Width <= 0 || size.Height <= 0)
                throw new ArgumentException("Ungültige Layoutgröße.", "size");
            if (definitions == null) throw new ArgumentNullException("definitions");
            List<KeyboardKeyDefinition> copy = new List<KeyboardKeyDefinition>();
            foreach (KeyboardKeyDefinition key in definitions)
            {
                if (key == null || key.Bounds.Right > size.Width || key.Bounds.Bottom > size.Height) throw new ArgumentException("Taste liegt außerhalb des Layouts.", "definitions");
                if (byCode.ContainsKey(key.Code) || (key.KeyIndex.HasValue && byIndex.ContainsKey(key.KeyIndex.Value))) throw new ArgumentException("Tastencode oder Index mehrfach vergeben.", "definitions");
                byCode.Add(key.Code, key); if (key.KeyIndex.HasValue) byIndex.Add(key.KeyIndex.Value, key); copy.Add(key);
            }
            if (copy.Count == 0) throw new ArgumentException("Layout enthält keine Tasten.", "definitions");
            keys = copy.AsReadOnly(); Name = name; Size = size; SourceDescription = sourceDescription ?? "";
        }
        public KeyboardKeyDefinition FindByIndex(int index) { KeyboardKeyDefinition result; return byIndex.TryGetValue(index, out result) ? result : null; }
        public KeyboardKeyDefinition FindByCode(string code) { KeyboardKeyDefinition result; return code != null && byCode.TryGetValue(code, out result) ? result : null; }
        public KeyboardLayout WithKeyIndex(string code, int? index, bool verified)
        {
            if (FindByCode(code) == null) throw new ArgumentException("Taste gehört nicht zu diesem Layout.", "code");
            List<KeyboardKeyDefinition> updated = new List<KeyboardKeyDefinition>();
            foreach (KeyboardKeyDefinition key in keys) updated.Add(key.Code == code ? new KeyboardKeyDefinition(code, index, key.Bounds, key.IsKnob, verified) : key);
            return new KeyboardLayout(Name, Size, updated, SourceDescription);
        }
        static KeyboardKeyDefinition K(string code, int index, int x, int y, int width, int height, bool knob)
        {
            return new KeyboardKeyDefinition(code, index, new RectangleF(x, y, width, height), knob,
                code == "KeyW" || code == "KeyA" || code == "KeyS" || code == "KeyD");
        }
    }
}
