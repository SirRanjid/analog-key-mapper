using System;
using System.IO;

namespace Tk75.Diagnostics
{
    // Pure packet encoding/parsing only: no handles, native calls or device access.
    // Layout is derived from the manufacturer excerpts in research/vendor-evidence.json.
    public static class MonitorProtocol
    {
        public static byte[] IdentifyRequest() { return Encode(0x8f, 0); }
        public static byte[] MonitorRequest(bool enabled) { return Encode(0x1b, enabled ? (byte)1 : (byte)0); }
        private static byte[] Encode(byte opcode, byte parameter)
        {
            byte[] report = new byte[65]; report[1] = opcode; report[2] = parameter;
            report[8] = (byte)(255 - ((opcode + parameter) & 255)); return report;
        }
        public static uint DeviceId(byte[] reply)
        {
            if (reply == null || reply.Length != 65 || reply[0] != 0 || reply[1] != 0x8f)
                throw new InvalidDataException("Identify reply has wrong size, report ID or opcode.");
            uint id = (uint)reply[2] | ((uint)reply[3] << 8) | ((uint)reply[4] << 16) | ((uint)reply[5] << 24);
            // Manufacturer ___getDeviceId maps this sentinel to -1; it is not an identity.
            if (id == uint.MaxValue) throw new InvalidDataException("Manufacturer returned an unavailable device ID (FFFFFFFF).");
            return id;
        }
        // Unverified V5 host interpretation only (qmk-index.Bs-RnLKs.js, getUSBVersion).
        // The V4 host uses different bytes/endianness. No device family is inferred here.
        public static int UsbFirmwareV5Candidate(byte[] reply) { DeviceId(reply); return (reply[9] << 8) | reply[10]; }
        public static bool IsSupportedTk75(uint deviceId) { return deviceId == 3590 || deviceId == 3591; }
    }
}
