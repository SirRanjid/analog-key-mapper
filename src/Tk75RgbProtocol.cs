using System;
using System.Collections.Generic;
using System.IO;

namespace Tk75.Diagnostics
{
    public interface IRgbSnapshotSource
    {
        bool RgbReadAvailable { get; }
        Tk75RgbSnapshot ReadRgbSnapshot(int layer, int timeoutMs);
    }
    public interface IRgbCompareExchangeSource : IRgbSnapshotSource
    {
        bool RgbWriteAvailable { get; }
        Tk75RgbSnapshot CompareExchangeRgb(Tk75RgbSnapshot expected, Tk75RgbSnapshot desired, int timeoutMs);
    }
    public interface IRgbAutomaticRestoreSource : IRgbCompareExchangeSource
    {
        bool RgbAutomaticRestoreAvailable { get; }
        Tk75RgbSnapshot CompareExchangeRgb(Tk75RgbSnapshot expected, Tk75RgbSnapshot desired, Tk75RgbSnapshot original, int timeoutMs);
    }
    // Pure manufacturer packet construction/readback only. Nothing in this file
    // opens a device or sends a write. Windows reports include report ID zero.
    // Pinned evidence: gearhub-v4-9d6437da.js SHA256 F8F20D3B0F44144788315A1A0FBDA7ED6A0D976C116A513666AFCF63D3105158.
    public static class Tk75RgbProtocol
    {
        public const int PictureLength = 384;
        public const int WritablePictureLength = 378;
        public const int PictureBlockCount = 6;
        public const int SnapshotLength = 456;
        // Literal physical positions in the pinned 3590/3591 defaultMatrix.
        // Sensor report key indices use these same positions (W14/A9/S15/D21
        // physically correlated). Unknown positions and rotary-encoder entries
        // 90..92 are deliberately not claimed to have individually mapped LEDs.
        static readonly int[] CommonKeys = new int[] {
            0,1,2,3,4,5,6,7,8,9,11,12,13,14,15,16,17,18,19,20,21,22,
            24,25,26,27,28,30,31,32,33,34,36,37,38,39,40,41,42,43,44,45,46,
            48,49,50,51,52,54,55,56,57,58,59,60,61,62,63,64,65,66,67,68,69,70,71,
            72,73,74,76,77,78,79,80,81,82,83,85,86,87,89 };

        static void Model(uint model) { if (model != 3590 && model != 3591) throw new InvalidDataException("RGB is only described for TK75 models 3590 and 3591."); }
        static void Layer(int layer) { if (layer < 0 || layer > 4) throw new ArgumentOutOfRangeException("layer"); }
        static byte[] Request(byte opcode, int checksum)
        { byte[] report = new byte[65]; report[1] = opcode; Finish(report, checksum); return report; }
        static void Finish(byte[] report, int checksum)
        { int sum = 0; for (int i = 1; i <= checksum; i++) sum += report[i]; report[checksum + 1] = (byte)(255 - (sum & 255)); }
        static byte[] Payload(byte[] reply)
        {
            if (reply == null || reply.Length != 65 || reply[0] != 0) throw new InvalidDataException("RGB reply must be exactly 65 bytes with report ID zero.");
            byte[] payload = new byte[64]; Buffer.BlockCopy(reply, 1, payload, 0, 64); return payload;
        }
        static void Settings(byte[] settings)
        { if (settings == null || settings.Length != 64 || settings[0] != 0x87) throw new InvalidDataException("Missing raw LED settings response."); }
        static void Picture(byte[] picture)
        { if (picture == null || picture.Length != PictureLength) throw new InvalidDataException("A complete 384-byte light picture is required."); }
        public static bool Equal(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
            return true;
        }
        public static byte[] ReadProfileRequest() { return Request(0x84, 7); }
        public static byte[] ReadSettingsRequest() { return Request(0x87, 7); }
        public static byte[] ReadPictureRequest(int layer, int block)
        {
            Layer(layer); if (block < 0 || block >= PictureBlockCount) throw new ArgumentOutOfRangeException("block");
            byte[] report = Request(0x8c, 7); report[2] = (byte)layer; report[3] = 255; report[4] = (byte)block; Finish(report, 7); return report;
        }
        public static byte ParseProfile(byte[] reply)
        { byte[] value = Payload(reply); if (value[0] != 0x84) throw new InvalidDataException("Wrong profile reply opcode."); return value[1]; }
        public static byte[] ParseSettings(byte[] reply)
        { byte[] value = Payload(reply); Settings(value); return value; }
        // The manufacturer's GET_USERPIC returns raw RGB data, not an echoed
        // opcode or block number. Exact request serialization is indispensable.
        public static byte[] ParsePictureBlock(byte[] reply) { return Payload(reply); }
        public static int[] GetSupportedKeyIndices(uint model)
        {
            Model(model); List<int> result = new List<int>(CommonKeys); if (model == 3591) { result.Add(10); result.Sort(); } return result.ToArray();
        }
        public static bool TryGetRgbMatrixIndex(uint model, int sensorKeyIndex, out int matrixIndex)
        {
            Model(model); matrixIndex = -1;
            if ((model == 3591 && sensorKeyIndex == 10) || Array.IndexOf(CommonKeys, sensorKeyIndex) >= 0) { matrixIndex = sensorKeyIndex; return true; }
            return false;
        }
        public static byte[] Overlay(uint model, byte[] original, IDictionary<int, int> sensorKeyColors)
        {
            Model(model); Picture(original); if (sensorKeyColors == null) throw new ArgumentNullException("sensorKeyColors");
            byte[] result = (byte[])original.Clone();
            foreach (KeyValuePair<int, int> entry in sensorKeyColors)
            {
                int index; if (!TryGetRgbMatrixIndex(model, entry.Key, out index)) throw new InvalidDataException("No verified LED position for sensor key " + entry.Key + ".");
                if (entry.Value < 0 || entry.Value > 0xffffff) throw new ArgumentOutOfRangeException("sensorKeyColors", "RGB must be 0x000000..0xFFFFFF.");
                result[index * 3] = (byte)(entry.Value >> 16); result[index * 3 + 1] = (byte)(entry.Value >> 8); result[index * 3 + 2] = (byte)entry.Value;
            }
            return result;
        }
        // USERPIC is stored artwork, not the currently visible LED framebuffer.
        // Preserve a supported visible background before switching to that mode.
        // Vendor getLightSetting resolves selectors 0..6 through COMMONCOLOR,
        // selector 7 through raw RGB, and 8 as DAZZLE (not a fixed color).
        public static byte[] OverlayVisibleLighting(Tk75RgbSnapshot original, IDictionary<int, int> sensorKeyColors)
        {
            if (original == null) throw new ArgumentNullException("original");
            if (sensorKeyColors == null) throw new ArgumentNullException("sensorKeyColors");
            byte[] settings = original.RawSettings, background = original.Picture;
            int mode = settings[1], selector = settings[4] & 15;
            int? solid = null;
            if (mode == 0) solid = 0;
            else if (mode == 1 && (settings[4] >> 4) == 0)
            {
                int[] palette = { 0xFF0000, 0xFF8000, 0xFFFF00, 0x00FF00, 0x00FFFF, 0x0000FF, 0xFF00FF };
                if (selector < palette.Length) solid = palette[selector];
                else if (selector == 7) solid = (settings[5] << 16) | (settings[6] << 8) | settings[7];
                else throw UnsupportedVisibleLighting();
            }
            else if (mode != 13 || selector != 0 || (settings[4] >> 4) != original.Layer)
                throw UnsupportedVisibleLighting();
            // The caller may turn brightness up to display mapped colors. Keep
            // originally unlit, unassigned keys dark in that case as well.
            if (settings[3] == 0) solid = 0;
            if (solid.HasValue)
            {
                int color = solid.Value;
                foreach (int index in GetSupportedKeyIndices(original.ModelId))
                {
                    background[index * 3] = (byte)(color >> 16);
                    background[index * 3 + 1] = (byte)(color >> 8);
                    background[index * 3 + 2] = (byte)color;
                }
            }
            // Overlay enforces the verified key allowlist. Unknown LED slots,
            // reserved bytes and the six trailing bytes remain byte-for-byte.
            return Overlay(original.ModelId, background, sensorKeyColors);
        }
        static InvalidDataException UnsupportedVisibleLighting()
        {
            return new InvalidDataException("Dieser Lichteffekt lässt sich auf nicht zugeordneten Tasten noch nicht erhalten. Bitte eine feste Farbe wählen oder den RGB-Override ausschalten.");
        }
        public static byte[][] BuildPictureWrites(int layer, byte[] picture)
        {
            Layer(layer); Picture(picture); byte[][] reports = new byte[7][];
            for (int block = 0; block < reports.Length; block++)
            {
                byte[] report = new byte[65]; int length = block == 6 ? 42 : 56;
                report[1] = 0x0c; report[2] = (byte)layer; report[3] = 255; report[4] = (byte)block;
                report[5] = (byte)length; report[6] = (byte)(block == 6 ? 1 : 0);
                // Match the manufacturer's last 48-byte slice including its six
                // preserved trailing bytes; the declared write length remains 42.
                int copied = Math.Min(56, picture.Length - block * 56);
                Buffer.BlockCopy(picture, block * 56, report, 9, copied); Finish(report, 7); reports[block] = report;
            }
            return reports;
        }
        public static byte[] BuildSettingsWrite(byte[] rawSettings)
        {
            Settings(rawSettings); byte[] report = new byte[65]; report[1] = 0x07;
            Buffer.BlockCopy(rawSettings, 1, report, 2, 7); Finish(report, 8); return report;
        }
        public static byte[] PictureModeSettings(byte[] rawSettings, int layer)
        {
            Settings(rawSettings); Layer(layer); byte[] result = (byte[])rawSettings.Clone();
            result[1] = 13; result[4] = (byte)(layer << 4); result[5] = 0; result[6] = 200; result[7] = 200; return result;
        }
        public static Tk75RgbSnapshot ReadSnapshot(uint model, int layer, Func<byte[], byte[]> exchange)
        {
            Model(model); Layer(layer); if (exchange == null) throw new ArgumentNullException("exchange");
            byte profile = ParseProfile(exchange(ReadProfileRequest())); byte[] settings = ParseSettings(exchange(ReadSettingsRequest()));
            byte[] picture = ReadPicture(layer, exchange);
            if (profile != ParseProfile(exchange(ReadProfileRequest())) || !Equal(settings, ParseSettings(exchange(ReadSettingsRequest()))))
                throw new InvalidDataException("Keyboard lighting/profile changed during backup.");
            if (!Equal(picture, ReadPicture(layer, exchange))) throw new InvalidDataException("Light picture was not stable across two complete reads.");
            if (profile != ParseProfile(exchange(ReadProfileRequest())) || !Equal(settings, ParseSettings(exchange(ReadSettingsRequest()))))
                throw new InvalidDataException("Keyboard lighting/profile changed during backup.");
            return new Tk75RgbSnapshot(model, profile, layer, settings, picture);
        }
        // Transaction verification has an already-journaled byte-exact target.
        // Read every picture byte once and require that exact target, bracketed
        // by profile/settings reads. Discovering an unknown original (above)
        // still requires two complete identical pictures before it is trusted.
        public static Tk75RgbSnapshot VerifyKnownSnapshot(Tk75RgbSnapshot expected, Func<byte[], byte[]> exchange)
        {
            if (expected == null) throw new ArgumentNullException("expected");
            if (exchange == null) throw new ArgumentNullException("exchange");
            byte profile = ParseProfile(exchange(ReadProfileRequest()));
            if (profile != expected.Profile) throw new InvalidDataException("Keyboard profile differs from the expected lighting state.");
            byte[] settings = ParseSettings(exchange(ReadSettingsRequest())), expectedSettings = expected.RawSettings;
            // The other response bytes are read-only metadata, not setter fields.
            // They may differ from a previous snapshot, but must stay stable for
            // this complete verification, just as for initial backup discovery.
            for (int i = 1; i <= 7; i++)
                if (settings[i] != expectedSettings[i]) throw new InvalidDataException("Keyboard settings differ from the expected lighting state.");
            byte[] picture = ReadPicture(expected.Layer, exchange);
            if (!Equal(picture, expected.Picture)) throw new InvalidDataException("Keyboard picture differs from the expected lighting state.");
            if (profile != ParseProfile(exchange(ReadProfileRequest())) || !Equal(settings, ParseSettings(exchange(ReadSettingsRequest()))))
                throw new InvalidDataException("Keyboard lighting/profile changed during verification.");
            return new Tk75RgbSnapshot(expected.ModelId, profile, expected.Layer, settings, picture);
        }
        static byte[] ReadPicture(int layer, Func<byte[], byte[]> exchange)
        {
            byte[] result = new byte[PictureLength];
            for (int block = 0; block < PictureBlockCount; block++) Buffer.BlockCopy(ParsePictureBlock(exchange(ReadPictureRequest(layer, block))), 0, result, block * 64, 64);
            return result;
        }
        public static byte[] EncodeSnapshot(Tk75RgbSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException("snapshot"); byte[] result = new byte[SnapshotLength];
            result[0] = 82; result[1] = 71; result[2] = 66; result[3] = 1; result[4] = (byte)snapshot.ModelId; result[5] = (byte)(snapshot.ModelId >> 8);
            result[6] = snapshot.Profile; result[7] = (byte)snapshot.Layer;
            Buffer.BlockCopy(snapshot.RawSettings, 0, result, 8, 64); Buffer.BlockCopy(snapshot.Picture, 0, result, 72, PictureLength); return result;
        }
        public static Tk75RgbSnapshot DecodeSnapshot(byte[] bytes)
        {
            if (bytes == null || bytes.Length != SnapshotLength || bytes[0] != 82 || bytes[1] != 71 || bytes[2] != 66 || bytes[3] != 1) throw new InvalidDataException("Unknown RGB snapshot format.");
            byte[] settings = new byte[64], picture = new byte[PictureLength]; Buffer.BlockCopy(bytes, 8, settings, 0, 64); Buffer.BlockCopy(bytes, 72, picture, 0, PictureLength);
            return new Tk75RgbSnapshot((uint)(bytes[4] | bytes[5] << 8), bytes[6], bytes[7], settings, picture);
        }
        internal static void ValidateSnapshot(uint model, int layer, byte[] settings, byte[] picture) { Model(model); Layer(layer); Settings(settings); Picture(picture); }
    }

    public sealed class Tk75RgbSnapshot
    {
        readonly byte[] settings, picture;
        public uint ModelId { get; private set; }
        public byte Profile { get; private set; }
        public int Layer { get; private set; }
        public byte[] RawSettings { get { return (byte[])settings.Clone(); } }
        public byte[] Picture { get { return (byte[])picture.Clone(); } }
        public Tk75RgbSnapshot(uint model, byte profile, int layer, byte[] rawSettings, byte[] rawPicture)
        {
            Tk75RgbProtocol.ValidateSnapshot(model, layer, rawSettings, rawPicture); ModelId = model; Profile = profile; Layer = layer;
            settings = (byte[])rawSettings.Clone(); picture = (byte[])rawPicture.Clone();
        }
    }
}
