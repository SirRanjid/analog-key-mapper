using System;
using System.Globalization;

namespace Tk75.App
{
    // Set-1 physical scan identity. E0 distinguishes left/right modifiers and
    // navigation keys from keypad positions; translated letters never identify a key.
    public static class KeyboardLearningControls
    {
        public static string ControlId(int scanCode, bool extended)
        {
            if (scanCode < 1 || scanCode > 127) throw new ArgumentOutOfRangeException("scanCode");
            return "scan:" + scanCode.ToString("x2", CultureInfo.InvariantCulture) + (extended ? ":e0" : "");
        }

        public static bool TryParseControlId(string controlId, out int scanCode, out bool extended)
        {
            scanCode = 0; extended = false;
            if (controlId == null || (controlId.Length != 7 && controlId.Length != 10) || !controlId.StartsWith("scan:", StringComparison.Ordinal)) return false;
            if (controlId.Length == 10 && !controlId.EndsWith(":e0", StringComparison.Ordinal)) return false;
            int value;
            if (!int.TryParse(controlId.Substring(5, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value) || value < 1 || value > 127) return false;
            scanCode = value; extended = controlId.Length == 10;
            return string.Equals(ControlId(value, extended), controlId, StringComparison.Ordinal);
        }

        public static bool TryDecode(ushort makeCode, ushort flags, ushort virtualKey, out string controlId, out bool down)
        {
            controlId = null; down = false;
            // E1/Pause can lack a matching break and must not become a held
            // controller button. Zero scan codes have no proven physical identity.
            if (makeCode < 1 || makeCode > 127 || virtualKey == 0 || virtualKey >= 255 || (flags & ~3) != 0) return false;
            bool extended = (flags & 2) != 0;
            if (extended && (makeCode == 0x2a || makeCode == 0x36)) return false; // Print-screen prefix pseudo-shifts.
            controlId = ControlId(makeCode, extended); down = (flags & 1) == 0; return true;
        }
    }
}
