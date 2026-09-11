using System;
using System.Collections.Generic;

namespace Tk75.App
{
    // Windows set-1 scan codes for the drawn physical positions, independent of
    // QWERTY/QWERTZ legends. This is not a readback of a remapped firmware layer.
    public static class KeyboardScanCodes
    {
        static readonly Dictionary<string, int> codes = Create();
        static Dictionary<string, int> Create()
        {
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            Add(result, "Escape Digit1 Digit2 Digit3 Digit4 Digit5 Digit6 Digit7 Digit8 Digit9 Digit0 Minus Equal Backspace Tab KeyQ KeyW KeyE KeyR KeyT KeyY KeyU KeyI KeyO KeyP BracketLeft BracketRight Enter ControlLeft KeyA KeyS KeyD KeyF KeyG KeyH KeyJ KeyK KeyL Semicolon Quote Backquote ShiftLeft Backslash KeyZ KeyX KeyC KeyV KeyB KeyN KeyM Comma Period Slash ShiftRight NumpadMultiply AltLeft Space CapsLock F1 F2 F3 F4 F5 F6 F7 F8 F9 F10", 1);
            result.Add("F11", 0x57); result.Add("F12", 0x58);
            result.Add("IntlBackslash", 0x56); result.Add("IntlRo", 0x2b);
            result.Add("ControlRight", 0x11d); result.Add("AltRight", 0x138);
            result.Add("MetaLeft", 0x15b); result.Add("MetaRight", 0x15c);
            result.Add("Home", 0x147); result.Add("ArrowUp", 0x148); result.Add("PageUp", 0x149);
            result.Add("ArrowLeft", 0x14b); result.Add("ArrowRight", 0x14d);
            result.Add("End", 0x14f); result.Add("ArrowDown", 0x150); result.Add("PageDown", 0x151);
            result.Add("Insert", 0x152); result.Add("Delete", 0x153);
            return result;
        }
        static void Add(Dictionary<string, int> result, string names, int first)
        { foreach (string name in names.Split(' ')) result.Add(name, first++); }
        public static bool TryGet(string physicalCode, out SuppressionKey key)
        {
            int code;
            if (physicalCode != null && codes.TryGetValue(physicalCode, out code))
            { key = new SuppressionKey(code & 255, (code & 256) != 0); return true; }
            key = new SuppressionKey(); return false;
        }
    }
}
