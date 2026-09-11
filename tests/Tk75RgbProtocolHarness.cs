using System;
using System.Collections.Generic;
using System.IO;
using Tk75.Diagnostics;

public static class Tk75RgbProtocolHarness
{
    static int checks;
    static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    static void Reject(Action action, string message)
    { bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } catch (InvalidDataException) { rejected = true; } Check(rejected, message); }
    static byte[] Report(byte[] payload) { byte[] value = new byte[65]; Buffer.BlockCopy(payload, 0, value, 1, payload.Length); return value; }
    static byte[] Picture() { byte[] result = new byte[384]; for (int i = 0; i < result.Length; i++) result[i] = (byte)((i * 19 + 7) & 255); return result; }
    static byte[] Settings() { byte[] result = new byte[64]; result[0] = 0x87; result[1] = 4; result[2] = 2; result[3] = 3; result[4] = 0xa9; result[5] = 255; result[6] = 0; result[7] = 17; result[63] = 83; return result; }
    sealed class ReadFixture
    {
        public int Calls;
        public Func<int, byte[], byte[]> Alter;
        public byte[] Exchange(byte[] request)
        {
            Calls++; Check(request.Length == 65 && request[0] == 0, "Read report shape.");
            byte[] reply;
            if (request[1] == 0x84) { reply = new byte[65]; reply[1] = 0x84; reply[2] = 2; }
            else if (request[1] == 0x87) reply = Report(Settings());
            else
            {
                Check(request[1] == 0x8c && request[2] == 4 && request[3] == 255 && request[4] < 6, "Read snapshot must never issue a write or an unknown GET.");
                reply = new byte[65]; Buffer.BlockCopy(Picture(), request[4] * 64, reply, 1, 64);
            }
            return Alter == null ? reply : Alter(Calls, reply);
        }
    }
    static int ColorAt(byte[] picture, int key) { return (picture[key * 3] << 16) | (picture[key * 3 + 1] << 8) | picture[key * 3 + 2]; }
    static void VisibleLighting()
    {
        int[] palette = { 0xFF0000, 0xFF8000, 0xFFFF00, 0x00FF00, 0x00FFFF, 0x0000FF, 0xFF00FF, 0x123456 };
        var colors = new Dictionary<int, int> { { 14, 0xABCDEF } };
        foreach (uint model in new uint[] { 3590, 3591 })
        {
            var known = new HashSet<int>(Tk75RgbProtocol.GetSupportedKeyIndices(model));
            for (int selector = 0; selector <= 7; selector++)
            {
                byte[] settings = Settings(); settings[1] = 1; settings[2] = 4; settings[3] = 1; settings[4] = (byte)selector;
                settings[5] = 0x12; settings[6] = 0x34; settings[7] = 0x56;
                var original = new Tk75RgbSnapshot(model, 2, 4, settings, Picture());
                byte[] saved = Tk75RgbProtocol.EncodeSnapshot(original);
                byte[] result = Tk75RgbProtocol.OverlayVisibleLighting(original, colors);
                foreach (int key in known) Check(ColorAt(result, key) == (key == 14 ? 0xABCDEF : palette[selector]), "Static palette or raw color remains on every unassigned known key");
                for (int i = 0; i < result.Length; i++) if (!known.Contains(i / 3)) Check(result[i] == Picture()[i], "Visible baseline preserves every unknown LED byte and trailing byte");
                Check(Tk75RgbProtocol.Equal(saved, Tk75RgbProtocol.EncodeSnapshot(original)), "Visible overlay never alters its original backup or settings");
            }
            byte[] actualSettings = new byte[64]; actualSettings[0] = 0x87; actualSettings[1] = 1; actualSettings[2] = 4; actualSettings[3] = 1; actualSettings[4] = 4; actualSettings[5] = 255;
            var actual = new Tk75RgbSnapshot(model, 2, 4, actualSettings, new byte[384]);
            byte[] actualResult = Tk75RgbProtocol.OverlayVisibleLighting(actual, colors);
            Check(ColorAt(actualResult, 9) == 0x00FFFF && ColorAt(actualResult, 14) == 0xABCDEF, "Actual cyan selector overrides stale red raw RGB and empty stored artwork");
            Check(actual.RawSettings[3] == 1 && actual.Picture[27] == 0, "Actual brightness and empty original artwork remain recoverable unchanged");

            byte[] offSettings = Settings(); offSettings[1] = 0;
            var off = new Tk75RgbSnapshot(model, 2, 4, offSettings, Picture());
            byte[] offResult = Tk75RgbProtocol.OverlayVisibleLighting(off, colors);
            foreach (int key in known) Check(ColorAt(offResult, key) == (key == 14 ? 0xABCDEF : 0), "Light-off mode keeps all unassigned known keys dark despite nonblack stored artwork");
            for (int i = 0; i < offResult.Length; i++) if (!known.Contains(i / 3)) Check(offResult[i] == Picture()[i], "Light-off baseline preserves unverified positions and tail");

            byte[] customSettings = Settings(); customSettings[1] = 13; customSettings[4] = 0x40;
            var custom = new Tk75RgbSnapshot(model, 2, 4, customSettings, Picture());
            byte[] customResult = Tk75RgbProtocol.OverlayVisibleLighting(custom, colors);
            for (int i = 0; i < customResult.Length; i++) if (i < 42 || i > 44) Check(customResult[i] == Picture()[i], "Active backed-up custom picture retains each unassigned RGB byte exactly");
            Check(ColorAt(customResult, 14) == 0xABCDEF, "Only assigned key is changed over an active custom picture");
            Check(Tk75RgbProtocol.Equal(Tk75RgbProtocol.OverlayVisibleLighting(custom, new Dictionary<int, int>()), custom.Picture), "Empty overlay on active custom picture is a byte-exact no-op");
            foreach (int mode in new[] { 1, 13 })
            {
                byte[] darkSettings = mode == 1 ? actual.RawSettings : custom.RawSettings; darkSettings[3] = 0;
                byte[] darkResult = Tk75RgbProtocol.OverlayVisibleLighting(new Tk75RgbSnapshot(model, 2, 4, darkSettings, Picture()), colors);
                foreach (int key in known) Check(ColorAt(darkResult, key) == (key == 14 ? 0xABCDEF : 0), "Originally zero brightness cannot turn on unassigned keys when mapped lighting is enabled");
            }
            foreach (int mode in new[] { 2,3,4,5,6,7,8,9,10,11,12,14,15,16,17,18,19,20,21,22,23,24,25,255 })
            {
                byte[] settings = actual.RawSettings; settings[1] = (byte)mode;
                var unsupported = new Tk75RgbSnapshot(model, 2, 4, settings, Picture());
                Reject(delegate { Tk75RgbProtocol.OverlayVisibleLighting(unsupported, colors); }, "Animated or unknown effect cannot be replaced with guessed visible colors");
            }
            foreach (byte selector in new byte[] { 8, 9, 15, 0x14, 0x17 })
            {
                byte[] settings = actual.RawSettings; settings[4] = selector;
                Reject(delegate { Tk75RgbProtocol.OverlayVisibleLighting(new Tk75RgbSnapshot(model, 2, 4, settings, Picture()), colors); }, "Dazzle or an unsupported static variant is rejected before an overlay is returned");
            }
            foreach (byte selection in new byte[] { 0x30, 0x41, 0x50 })
            {
                byte[] settings = custom.RawSettings; settings[4] = selection;
                Reject(delegate { Tk75RgbProtocol.OverlayVisibleLighting(new Tk75RgbSnapshot(model, 2, 4, settings, Picture()), colors); }, "Another active custom layer or unsupported custom selection cannot use the wrong artwork");
            }
            Reject(delegate { Tk75RgbProtocol.OverlayVisibleLighting(custom, new Dictionary<int, int> { { 90, 1 } }); }, "Visible-overlay API still rejects unknown mapped LED positions");
            Reject(delegate { Tk75RgbProtocol.OverlayVisibleLighting(custom, new Dictionary<int, int> { { 14, -1 } }); }, "Visible-overlay API still rejects invalid mapped color values");
        }
        Reject(delegate { Tk75RgbProtocol.OverlayVisibleLighting(null, colors); }, "Visible lighting requires an original snapshot");
    }
    public static int Run()
    {
        checks = 0;
        VisibleLighting();
        byte[] request = Tk75RgbProtocol.ReadProfileRequest(); Check(request[1] == 0x84 && request[8] == 0x7b, "Profile read golden checksum.");
        request = Tk75RgbProtocol.ReadSettingsRequest(); Check(request[1] == 0x87 && request[8] == 0x78, "Settings read golden checksum.");
        request = Tk75RgbProtocol.ReadPictureRequest(4, 5);
        Check(request[1] == 0x8c && request[2] == 4 && request[3] == 255 && request[4] == 5 && request[8] == 0x6b, "Picture read golden header.");
        for (int i = 9; i < request.Length; i++) Check(request[i] == 0, "Read never includes color payload.");
        Reject(delegate { Tk75RgbProtocol.ReadPictureRequest(-1, 0); }, "Negative layer.");
        Reject(delegate { Tk75RgbProtocol.ReadPictureRequest(5, 0); }, "Layer outside five known pictures.");
        Reject(delegate { Tk75RgbProtocol.ReadPictureRequest(0, 6); }, "Extra read block.");

        byte[] original = Picture(); byte[] originalCopy = (byte[])original.Clone();
        byte[] overlaid = Tk75RgbProtocol.Overlay(3591, original, new Dictionary<int, int> { { 14, 0x123456 }, { 10, 0xabcdef } });
        Check(overlaid[42] == 0x12 && overlaid[43] == 0x34 && overlaid[44] == 0x56, "W sensor index14 maps to RGB bytes42..44.");
        Check(overlaid[30] == 0xab && overlaid[31] == 0xcd && overlaid[32] == 0xef, "ISO extra key maps to slot10.");
        for (int i = 0; i < overlaid.Length; i++) if ((i < 42 || i > 44) && (i < 30 || i > 32)) Check(overlaid[i] == original[i], "Overlay preserves every other RGB byte including tail.");
        Check(Tk75RgbProtocol.Equal(original, originalCopy), "Overlay does not mutate backup.");
        Reject(delegate { Tk75RgbProtocol.Overlay(3590, original, new Dictionary<int, int> { { 10, 1 } }); }, "ISO-only key rejected on ANSI.");
        Reject(delegate { Tk75RgbProtocol.Overlay(3591, original, new Dictionary<int, int> { { 90, 1 } }); }, "No invented knob LED.");
        Reject(delegate { Tk75RgbProtocol.Overlay(3591, original, new Dictionary<int, int> { { 23, 1 } }); }, "Empty matrix slot rejected.");
        Reject(delegate { Tk75RgbProtocol.Overlay(3591, original, new Dictionary<int, int> { { 14, 0x1000000 } }); }, "No RGB integer truncation.");
        Reject(delegate { Tk75RgbProtocol.GetSupportedKeyIndices(1); }, "Unknown model rejected.");

        byte[][] writes = Tk75RgbProtocol.BuildPictureWrites(4, overlaid);
        Check(writes.Length == 7, "Exactly seven manufacturer chunks.");
        Check(writes[0][8] == 0xb8 && writes[6][8] == 0xbf, "Golden first/final write checksums.");
        for (int i = 0; i < writes.Length; i++)
        {
            Check(writes[i].Length == 65 && writes[i][0] == 0 && writes[i][1] == 12 && writes[i][2] == 4 && writes[i][3] == 255 && writes[i][4] == i, "Write header.");
            Check(writes[i][5] == (i == 6 ? 42 : 56) && writes[i][6] == (i == 6 ? 1 : 0), "Only final chunk finalizes.");
            for (int j = 0; j < Math.Min(56, 384 - i * 56); j++) Check(writes[i][9 + j] == overlaid[i * 56 + j], "RGB channel order and page boundaries.");
        }
        for (int i = 57; i < 65; i++) Check(writes[6][i] == 0, "Final chunk transport padding matches manufacturer.");
        Reject(delegate { Tk75RgbProtocol.BuildPictureWrites(0, new byte[378]); }, "Partial backup never accepted as a full picture.");
        byte[] rawSettings = Settings(); byte[] set = Tk75RgbProtocol.BuildSettingsWrite(rawSettings);
        Check(set[1] == 7 && set[2] == 4 && set[5] == 0xa9 && set[6] == 255 && set[8] == 17 && set[9] == 54, "Raw settings restore preserves white and nibble without lossy UI transforms.");
        for (int i = 10; i < set.Length; i++) Check(set[i] == 0, "Read-only/reserved settings bytes are not emitted as setters.");
        byte[] pictureMode = Tk75RgbProtocol.PictureModeSettings(rawSettings, 4);
        Check(pictureMode[1] == 13 && pictureMode[2] == 2 && pictureMode[3] == 3 && pictureMode[4] == 0x40 && pictureMode[5] == 0 && pictureMode[6] == 200 && pictureMode[7] == 200, "Custom picture mode follows manufacturer and retains speed/brightness.");
        Check(rawSettings[1] == 4 && rawSettings[4] == 0xa9, "Building picture mode does not mutate original.");

        ReadFixture good = new ReadFixture(); Tk75RgbSnapshot snapshot = Tk75RgbProtocol.ReadSnapshot(3591, 4, good.Exchange);
        Check(good.Calls == 18 && snapshot.Profile == 2 && snapshot.ModelId == 3591 && snapshot.Layer == 4, "Two full pictures plus final settings/profile reread.");
        Check(Tk75RgbProtocol.Equal(snapshot.Picture, Picture()) && Tk75RgbProtocol.Equal(snapshot.RawSettings, Settings()), "Byte-exact complete snapshot.");
        byte[] escaped = snapshot.Picture; escaped[0] ^= 255; Check(snapshot.Picture[0] == Picture()[0], "Snapshot getter does not expose mutable backup.");
        byte[] encoded = Tk75RgbProtocol.EncodeSnapshot(snapshot); Check(encoded.Length == 456 && encoded[0] == 82 && encoded[3] == 1, "Versioned fixed snapshot size.");
        Tk75RgbSnapshot decoded = Tk75RgbProtocol.DecodeSnapshot(encoded); encoded[72] ^= 255;
        Check(Tk75RgbProtocol.Equal(decoded.Picture, snapshot.Picture) && Tk75RgbProtocol.Equal(decoded.RawSettings, snapshot.RawSettings), "Wire snapshot roundtrips and owns its arrays.");
        Reject(delegate { Tk75RgbProtocol.DecodeSnapshot(new byte[456]); }, "Wrong snapshot signature rejected.");
        Reject(delegate { Tk75RgbProtocol.ParseSettings(new byte[65]); }, "Stale or wrong opcode cannot become settings backup.");
        Reject(delegate { Tk75RgbProtocol.ParsePictureBlock(new byte[64]); }, "Missing report ID is not silently shifted.");
        ReadFixture missing = new ReadFixture { Alter = delegate(int call, byte[] reply) { return call == 3 ? null : reply; } };
        Reject(delegate { Tk75RgbProtocol.ReadSnapshot(3591, 4, missing.Exchange); }, "Missing picture block fails without synthesizing black."); Check(missing.Calls == 3, "Failed read stops immediately.");
        ReadFixture changed = new ReadFixture { Alter = delegate(int call, byte[] reply) { if (call == 11) reply[1] ^= 1; return reply; } };
        Reject(delegate { Tk75RgbProtocol.ReadSnapshot(3591, 4, changed.Exchange); }, "Changed second picture fails stability check.");
        ReadFixture profile = new ReadFixture { Alter = delegate(int call, byte[] reply) { if (call == 9) reply[2] = 3; return reply; } };
        Reject(delegate { Tk75RgbProtocol.ReadSnapshot(3591, 4, profile.Exchange); }, "Profile switch during backup fails.");
        ReadFixture finalSettings = new ReadFixture { Alter = delegate(int call, byte[] reply) { if (call == 18) reply[4] ^= 1; return reply; } };
        Reject(delegate { Tk75RgbProtocol.ReadSnapshot(3591, 4, finalSettings.Exchange); }, "Settings changed after final picture fail.");
        return checks;
    }
}
