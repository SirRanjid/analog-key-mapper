using System.Globalization;

namespace Tk75.Diagnostics
{
    public struct TravelSample
    {
        public int KeyIndex;
        public int RawValue;
        public string KeyLabel;
    }

    // Pure decoder for the 32-byte layout observed in the saved W/WASD captures.
    // The caller must establish the supported TK75 device/transport separately.
    // RawValue is an unsigned protocol value, not millimeters or a calibrated range.
    public static class Tk75TravelReport
    {
        private static readonly string[] Labels = BuildLabels();

        private static string[] BuildLabels()
        {
            string[] labels = new string[256];
            for (int index = 0; index < labels.Length; index++)
                labels[index] = "Index " + index.ToString(CultureInfo.InvariantCulture);
            // Manufacturer V4 defaultMatrix in gearhub-v4-0d356e11.js and
            // gearhub-v4-06858deb.js; see research/tk75-key-index-findings.md.
            // WASD also correlate with the local self-paced physical-key captures.
            // These observations are not controlled calibration measurements.
            labels[14] = "W"; labels[9] = "A"; labels[15] = "S"; labels[21] = "D";
            return labels;
        }

        public static bool TryParse(byte[] report, out TravelSample sample, out string error)
        {
            sample = new TravelSample();
            error = null;
            if (report == null) { error = "Missing input report."; return false; }
            if (report.Length != 32) { error = "Unsupported report length: expected 32 bytes."; return false; }
            if (report[0] != 0x05) { error = "Unexpected report ID: expected 05."; return false; }
            if (report[1] != 0x1b) { error = "Unexpected event opcode: expected 1B."; return false; }
            // All measured frames have zero padding here. Future/nonmatching layouts
            // must be investigated instead of silently assuming the same semantics.
            for (int offset = 5; offset < report.Length; offset++)
            {
                if (report[offset] != 0)
                {
                    error = "Unsupported nonzero trailing byte at offset " + offset.ToString(CultureInfo.InvariantCulture) + ".";
                    return false;
                }
            }
            sample.KeyIndex = report[4];
            sample.RawValue = report[2] | (report[3] << 8);
            sample.KeyLabel = Labels[sample.KeyIndex];
            return true;
        }
    }
}
