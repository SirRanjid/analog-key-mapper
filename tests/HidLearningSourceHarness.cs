using System;
using System.Collections.Generic;
using System.IO;
using Tk75.App;
using Tk75.Diagnostics;
using Tk75.Mapping;

public static class HidLearningSourceHarness
{
    static int checks;
    static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    static void U16(byte[] bytes, int offset, int value) { Array.Copy(BitConverter.GetBytes((ushort)value), 0, bytes, offset, 2); }
    static byte[] Cap(bool scalar, int page, int usage, int index, byte report, int minimum, int maximum, int bits)
    {
        byte[] bytes = new byte[72]; U16(bytes, 0, page); bytes[2] = report; bytes[15] = 1;
        U16(bytes, 56, usage); U16(bytes, 68, index);
        if (scalar) { U16(bytes, 18, bits); U16(bytes, 20, 1); Array.Copy(BitConverter.GetBytes(minimum), 0, bytes, 40, 4); Array.Copy(BitConverter.GetBytes(maximum), 0, bytes, 44, 4); }
        return bytes;
    }
    static HidLearningReportDecoder Decoder(byte[][] buttons, byte[][] values) { return new HidLearningReportDecoder(HidLearningReportDecoder.ParseCapabilities(buttons, values)); }
    public static string Run()
    {
        byte[] button = Cap(false, 9, 1, 0, 1, 0, 1, 1), otherButton = Cap(false, 9, 2, 1, 2, 0, 1, 1);
        byte[] axis = Cap(true, 1, 0x30, 2, 1, -127, 127, 8), relative = Cap(true, 1, 0x38, 3, 3, -127, 127, 8);
        relative[15] = 0;
        byte[] hat = Cap(true, 1, 0x39, 4, 4, 0, 7, 4); hat[16] = 1;
        var decoder = Decoder(new[] { button, otherButton }, new[] { axis, relative, hat });
        double value; Check(!decoder.TryGetValue("hid:1:9:1:0:0", out value), "Unseen buttons stay unknown.");
        Check(decoder.TryGetValue("hid:3:1:38:0:3", out value) && value == 0, "Relative snapshot is neutral, never a retained delta.");
        var samples = new List<InputControlSample>();
        decoder.Decode(1, new[] { new HidLearningData(0, 1), new HidLearningData(2, 0xff) }, 2, 100, samples);
        Check(decoder.TryGetValue("hid:1:9:1:0:0", out value) && value == 1, "Button press decoded.");
        Check(decoder.TryGetValue("hid:1:1:30:0:2", out value) && value == -1, "8-bit scalar sign extended.");
        decoder.Decode(2, new[] { new HidLearningData(1, 1) }, 1, 101, samples);
        samples.Clear(); decoder.Decode(1, new[] { new HidLearningData(2, 0) }, 1, 102, samples);
        Check(samples.Count == 2 && samples[0].Value == 0 && samples[0].TimestampMilliseconds == 102, "Absent buttons emit OFF in their own report.");
        Check(decoder.TryGetValue("hid:2:9:2:0:1", out value) && value == 1, "Other report button state retained.");
        samples.Clear(); decoder.Decode(1, new[] { new HidLearningData(2, 0) }, 1, 103, samples);
        Check(samples.Count == 0, "Unchanged absolute report does not allocate events.");
        decoder.Decode(3, new[] { new HidLearningData(3, 2) }, 1, 104, samples);
        decoder.Decode(3, new[] { new HidLearningData(3, 2) }, 1, 105, samples);
        Check(samples.Count == 2 && samples[1].Value == 2, "Repeated relative deltas preserved.");
        decoder.Decode(4, new[] { new HidLearningData(4, 15) }, 1, 106, samples);
        Check(decoder.TryGetValue("hid:4:1:39:0:4", out value) && value == 8, "Null hat is explicit neutral outside direction range.");
        decoder.Decode(4, new[] { new HidLearningData(4, 0) }, 1, 107, samples);
        Check(decoder.TryGetValue("hid:4:1:39:0:4", out value) && value == 0, "Hat direction zero is active, not neutral.");
        bool rejected = false;
        try { decoder.Decode(1, new HidLearningData[0], 0, 108, samples); } catch (InvalidDataException) { rejected = true; }
        Check(rejected && decoder.TryGetValue("hid:1:1:30:0:2", out value) && value == 0, "Missing scalar rejected without partial publication.");
        rejected = false;
        try { decoder.Decode(1, new[] { new HidLearningData(2, 1), new HidLearningData(2, 2) }, 2, 108, samples); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Duplicate indices rejected.");
        decoder.Clear(); Check(!decoder.TryGetValue("hid:2:9:2:0:1", out value), "Disconnect invalidates retained button.");
        for (int bits = 1; bits <= 32; bits++)
        {
            uint mask = bits == 32 ? uint.MaxValue : (1u << bits) - 1;
            Check(HidLearningReportDecoder.DecodeScalar(mask, bits, true) == -1, "Signed all-ones " + bits);
            Check(HidLearningReportDecoder.DecodeScalar(mask, bits, false) == (double)mask, "Unsigned all-ones " + bits);
            Check(HidLearningReportDecoder.DecodeScalar(1u << (bits - 1), bits, true) == -(double)(1L << (bits - 1)), "Signed minimum " + bits);
        }
        byte[] usageArray = Cap(true, 1, 0x30, 10, 1, 0, 255, 8); U16(usageArray, 20, 8);
        Check(HidLearningReportDecoder.ParseCapabilities(new byte[0][], new[] { usageArray }).Count == 0, "Usage arrays are not guessed.");
        byte[] vendor = Cap(true, 0xff00, 1, 10, 1, 0, 255, 8);
        Check(HidLearningReportDecoder.ParseCapabilities(new byte[0][], new[] { vendor }).Count == 0, "Vendor bytes not guessed.");
        byte[] range = Cap(false, 9, 1, 10, 5, 0, 1, 1); range[12] = 1; U16(range, 58, 4); U16(range, 70, 13);
        var ranged = Decoder(new[] { range }, new byte[0][]);
        Check(ranged.Controls.Length == 4 && ranged.Controls[3].ControlId == "hid:5:9:4:0:13", "Button usage range maps one-to-one to indices.");
        U16(range, 70, 12); rejected = false;
        try { Decoder(new[] { range }, new byte[0][]); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Inconsistent ranges rejected.");
        var device = new CollectionInfo { devicePath = "hid://one", product = "Controller", inputReportLength = 8, usagePage = 1, usage = 5 };
        Check(HidLearningSource.IsEligible(device), "Standard gamepad eligible.");
        string id = HidLearningSource.IdentifyDevice(device); device.devicePath = "HID://ONE";
        Check(id == HidLearningSource.IdentifyDevice(device) && id.Length == 64, "Stable case-insensitive hashed identity.");
        device.devicePath = "hid://two"; Check(id != HidLearningSource.IdentifyDevice(device), "Identical devices at different paths remain distinct.");
        device.usage = 6; Check(!HidLearningSource.IsEligible(device), "System keyboard excluded from HID decoder.");
        Check(HidLearningNativeSession.IsVirtualOutputAncestor("ROOT\\USBIP_WIN2\\UDE", null), "USB/IP output bus rejected.");
        Check(HidLearningNativeSession.IsVirtualOutputAncestor("ROOT\\VIGEMBUS\\0000", null), "ViGEm output bus rejected.");
        Check(HidLearningNativeSession.IsVirtualOutputAncestor("ROOT\\UNKNOWN\\0000", "usbip2_ude"), "USB/IP service rejected.");
        Check(!HidLearningNativeSession.IsVirtualOutputAncestor("USB\\VID_054C&PID_0CE6\\real", "HidUsb"), "Physical Sony device not blocked by VID/PID.");
        return checks + " pure HID descriptor/report checks passed.";
    }
}
