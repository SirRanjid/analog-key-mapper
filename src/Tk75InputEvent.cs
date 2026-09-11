using System;
using System.Globalization;
using System.IO;

namespace Tk75.Diagnostics
{
    public enum Tk75InputEventKind
    {
        Travel,
        LightingChanged,
        BusyBegin,
        BusyEnd,
        StateInvalidated
    }

    // The vendor input endpoint carries both pressure and configuration events.
    // Classification is not an acknowledgement of any feature-write request.
    // Only Travel may change pressure values or their freshness timestamps.
    public sealed class Tk75InputEvent
    {
        public readonly Tk75InputEventKind Kind;
        public readonly TravelSample Sample;
        public readonly string Description;

        Tk75InputEvent(Tk75InputEventKind kind, TravelSample sample, string description)
        { Kind = kind; Sample = sample; Description = description; }

        public static Tk75InputEvent Parse(byte[] report)
        {
            if (report == null) throw Invalid("Missing input report.", report);
            if (report.Length != 32) throw Invalid("Unsupported input report length: expected 32 bytes.", report);
            if (report[0] != 0x05) throw Invalid("Unexpected input report ID: expected 05.", report);
            if (report[1] == 0x1b)
            {
                TravelSample sample; string error;
                if (!Tk75TravelReport.TryParse(report, out sample, out error)) throw Invalid(error, report);
                return new Tk75InputEvent(Tk75InputEventKind.Travel, sample, "Pressure sample");
            }

            // gearhub-v4-index.b1e406e4.js g4/___vendorRecv classify the first
            // three payload bytes after the report ID. Remaining bytes have no
            // described meaning here: accept zero padding only, retaining raw
            // evidence on rejection instead of assuming a future report layout.
            Tk75InputEventKind kind;
            string description;
            if (report[1] >= 0x04 && report[1] <= 0x07)
            {
                kind = Tk75InputEventKind.LightingChanged;
                description = "Lighting changed";
            }
            else if (report[1] == 0x0f && report[2] == 1 && report[3] == 0)
            {
                kind = Tk75InputEventKind.BusyBegin;
                description = "Vendor operation started";
            }
            else if (report[1] == 0x0f && report[2] == 0 && report[3] == 0)
            {
                // This is the end of a vendor busy interval, not a statement
                // that pressure monitoring stopped or that a write succeeded.
                kind = Tk75InputEventKind.BusyEnd;
                description = "Vendor operation ended";
            }
            else if (report[1] == 0x01)
            {
                kind = Tk75InputEventKind.StateInvalidated;
                description = "Onboard profile changed";
            }
            else if (report[1] == 0x0d && report[2] == 0 && (report[3] == 0 || report[3] == 1))
            {
                kind = Tk75InputEventKind.StateInvalidated;
                description = report[3] == 0 ? "Keyboard reset" : "Lighting reset";
            }
            else
            {
                // In particular, input event 0C is not the acknowledgement of
                // feature command SET_USERPIC (also 0C). The vendor decoder uses
                // that event number for mouse DPI changes, outside this adapter.
                throw Invalid("Unknown input event opcode " + report[1].ToString("X2", CultureInfo.InvariantCulture) + ".", report);
            }
            for (int offset = 4; offset < report.Length; offset++)
                if (report[offset] != 0)
                    throw Invalid("Unsupported notification byte at offset " + offset.ToString(CultureInfo.InvariantCulture) + ".", report);
            return new Tk75InputEvent(kind, new TravelSample(), description);
        }

        static InvalidDataException Invalid(string reason, byte[] report)
        {
            string raw = report == null ? "<null>" :
                BitConverter.ToString(report, 0, Math.Min(32, report.Length)).Replace("-", "") +
                (report.Length > 32 ? "..." : "");
            return new InvalidDataException(reason + " Raw report (" +
                (report == null ? "missing" : report.Length.ToString(CultureInfo.InvariantCulture) + " bytes") + "): " + raw);
        }
    }
}
