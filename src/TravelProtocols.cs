using System;
using System.Collections.Generic;
using System.Globalization;

namespace Tk75.Diagnostics
{
    // Decoder selection is based on advertised report capabilities, not bcdDevice.
    // Matching metadata is a candidate, not proof that every firmware is compatible.
    public interface ITravelDecoder
    {
        string Id { get; }
        bool Matches(CollectionInfo device);
        bool TryParse(byte[] report, out TravelSample sample, out string error);
    }

    public sealed class RongYuanTravel32 : ITravelDecoder
    {
        static readonly string[] UnknownLabels = MakeUnknownLabels();
        static string[] MakeUnknownLabels()
        {
            string[] labels = new string[256];
            for (int index = 0; index < labels.Length; index++) labels[index] = "Index " + index.ToString(CultureInfo.InvariantCulture);
            return labels;
        }
        public string Id { get { return "rongyuan-travel-05-1b-u16le-32-v1"; } }
        public bool Matches(CollectionInfo device)
        {
            if (device == null || device.error != null || device.usagePage != 0xffff || device.usage != 1 || device.inputReportLength != 32) return false;
            if (device.reportCapabilities == null) return false;
            int inputCaps = 0;
            foreach (Dictionary<string, object> cap in device.reportCapabilities)
            {
                if (cap == null) return false;
                string type = Text(cap, "reportType");
                if (type == "input")
                {
                    if (Text(cap, "kind") != "value" || Number(cap, "reportId") != 5 || Number(cap, "usagePage") != 0xffff || Number(cap, "bitSize") != 8 || Number(cap, "reportCount") != 31)
                        return false;
                    inputCaps++;
                }
                else if (type != "output" && type != "feature") return false;
            }
            return inputCaps == 1;
        }
        static string Text(Dictionary<string, object> item, string name)
        { object value; return item.TryGetValue(name, out value) ? value as string : null; }
        static int Number(Dictionary<string, object> item, string name)
        {
            object value;
            if (!item.TryGetValue(name, out value) || value == null) return -1;
            // Metadata may have passed through JSON (Int64/Double), but fractions,
            // booleans and numeric strings must not become a matching capability.
            TypeCode type = Type.GetTypeCode(value.GetType());
            if (value.GetType().IsEnum || type < TypeCode.SByte || type > TypeCode.Decimal) return -1;
            if (type == TypeCode.Double || type == TypeCode.Single)
            {
                double floating = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (double.IsNaN(floating) || double.IsInfinity(floating) || floating != Math.Truncate(floating)) return -1;
            }
            try
            {
                decimal number = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                if (number != decimal.Truncate(number) || number < int.MinValue || number > int.MaxValue) return -1;
                return (int)number;
            }
            catch (FormatException) { return -1; }
            catch (OverflowException) { return -1; }
            catch (InvalidCastException) { return -1; }
        }
        public bool TryParse(byte[] report, out TravelSample sample, out string error)
        {
            if (!Tk75TravelReport.TryParse(report, out sample, out error)) return false;
            // Layout labels are separate from this common protocol: VID/PID are shared.
            sample.KeyLabel = UnknownLabels[sample.KeyIndex];
            return true;
        }
    }

    public static class TravelProtocols
    {
        static readonly ITravelDecoder[] decoders = new ITravelDecoder[] { new RongYuanTravel32() };
        public static ITravelDecoder Find(CollectionInfo device)
        {
            ITravelDecoder match = null;
            foreach (ITravelDecoder decoder in decoders)
            {
                if (!decoder.Matches(device)) continue;
                if (match != null) return null; // An ambiguous signature requires investigation.
                match = decoder;
            }
            return match;
        }
        public static bool IsVendorInput(CollectionInfo device)
        { return device != null && device.error == null && device.usagePage >= 0xff00 && device.usagePage <= 0xffff && device.inputReportLength > 0 && device.inputReportLength <= 65536; }
    }
}
