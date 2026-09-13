using System;
using System.Collections.Generic;
using System.IO;
using Tk75.Diagnostics;

public static class Tk75RgbExchangeHarness
{
    static int checks;
    static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    static void Reject(Action action, string message)
    { bool rejected = false; try { action(); } catch (InvalidDataException) { rejected = true; } Check(rejected, message); }
    sealed class Device
    {
        public byte[] Settings = new byte[64], Picture = new byte[384];
        public byte Profile = 2;
        public int Reads, Writes, FailWriteAt;
        public bool IgnoreWrites;
        public readonly List<byte> Opcodes = new List<byte>();
        public Device()
        {
            Settings[0] = 0x87; Settings[1] = 4; Settings[2] = 2; Settings[3] = 3;
            Settings[4] = 9; Settings[5] = 255; Settings[7] = 25;
            for (int i = 0; i < Picture.Length; i++) Picture[i] = (byte)((i * 23 + 17) & 255);
        }
        public Tk75RgbSnapshot Snapshot() { return new Tk75RgbSnapshot(3591, Profile, 4, Settings, Picture); }
        public byte[] Read(byte[] request)
        {
            Reads++; byte[] reply = new byte[65];
            if (request[1] == 0x84) { reply[1] = 0x84; reply[2] = Profile; }
            else if (request[1] == 0x87) Buffer.BlockCopy(Settings, 0, reply, 1, 64);
            else if (request[1] == 0x8c && request[2] == 4 && request[4] < 6) Buffer.BlockCopy(Picture, request[4] * 64, reply, 1, 64);
            else throw new Exception("Unknown fake GET.");
            return reply;
        }
        public void Write(byte[] request)
        {
            Writes++; Opcodes.Add(request[1]);
            if (Writes == FailWriteAt) throw new IOException("Synthetic transport interrupted.");
            if (IgnoreWrites) return;
            if (request[1] == 12)
            {
                Check(request[2] == 4 && request[4] < 7, "Only reserved backed-up image written.");
                Buffer.BlockCopy(request, 9, Picture, request[4] * 56, request[5]);
            }
            else if (request[1] == 7) Buffer.BlockCopy(request, 2, Settings, 1, 7);
            else throw new Exception("Unknown fake setter.");
        }
    }
    static Tk75RgbSnapshot Paint(Tk75RgbSnapshot baseline, bool changeMode)
    {
        byte[] picture = Tk75RgbProtocol.Overlay(baseline.ModelId, baseline.Picture, new Dictionary<int, int> { { 14, 0x123456 } });
        byte[] settings = changeMode ? Tk75RgbProtocol.PictureModeSettings(baseline.RawSettings, baseline.Layer) : baseline.RawSettings;
        return new Tk75RgbSnapshot(baseline.ModelId, baseline.Profile, baseline.Layer, settings, picture);
    }
    static void AutomaticRestoreChecks()
    {
        Device device = new Device(); var guard = new Tk75RgbRestoreGuard();
        Check(!guard.Armed && guard.Restore(delegate { throw new Exception("Unarmed cleanup must not identify."); }, device.Read, device.Write) == null && device.Reads == 0 && device.Writes == 0,
            "A reader without authorized temporary lighting performs no cleanup RGB traffic.");
        for (int failure = 0; failure <= 8; failure++)
        {
            device = new Device(); Tk75RgbSnapshot original = device.Snapshot();
            var colors = new Dictionary<int, int>();
            foreach (int key in Tk75RgbProtocol.GetSupportedKeyIndices(original.ModelId)) colors[key] = 0x123456 + key;
            Tk75RgbSnapshot desired = new Tk75RgbSnapshot(original.ModelId, original.Profile, original.Layer,
                Tk75RgbProtocol.PictureModeSettings(original.RawSettings, original.Layer), Tk75RgbProtocol.Overlay(original.ModelId, original.Picture, colors));
            guard = new Tk75RgbRestoreGuard(); guard.Track(original, original, desired);
            device.FailWriteAt = failure;
            try { Tk75RgbExchange.Execute(original, desired, device.Read, device.Write); }
            catch (IOException) { Check(failure > 0, "Only the requested synthetic write interruption is swallowed."); }
            device.FailWriteAt = 0;
            Tk75RgbSnapshot restored = guard.Restore(delegate { return 3591; }, device.Read, device.Write);
            Check(Tk75RgbProtocol.Equal(Tk75RgbProtocol.EncodeSnapshot(restored), Tk75RgbProtocol.EncodeSnapshot(original)),
                "Helper cleanup restores every original LED, effect, brightness and reserved byte after write boundary " + failure + ".");
            int writes = device.Writes; guard.Restore(delegate { return 3591; }, device.Read, device.Write);
            Check(device.Writes == writes, "Repeated cleanup never repaints an already restored keyboard.");
        }
        device = new Device(); Tk75RgbSnapshot baseline = device.Snapshot(), painted = Paint(baseline, true);
        guard = new Tk75RgbRestoreGuard(); guard.Track(baseline, baseline, painted);
        Tk75RgbExchange.Execute(baseline, painted, device.Read, device.Write);
        device.Picture[42] ^= 0x80; int before = device.Writes;
        Reject(delegate { guard.Restore(delegate { return 3591; }, device.Read, device.Write); }, "Automatic cleanup rejects an unrelated LED color change.");
        Check(device.Writes == before, "Unrelated changes receive no cleanup writes.");
        device = new Device(); baseline = device.Snapshot(); painted = Paint(baseline, true);
        guard = new Tk75RgbRestoreGuard(); guard.Track(baseline, baseline, painted);
        Tk75RgbExchange.Execute(baseline, painted, device.Read, device.Write); device.Profile++;
        before = device.Writes;
        Reject(delegate { guard.Restore(delegate { return 3591; }, device.Read, device.Write); }, "Automatic cleanup cannot write an original into another onboard profile.");
        Check(device.Writes == before, "Profile mismatch receives no cleanup writes.");
        Reject(delegate { guard.Track(painted, painted, baseline); }, "The startup-original snapshot cannot be replaced by temporary colors.");
        device = new Device(); baseline = device.Snapshot(); painted = Paint(baseline, true);
        guard = new Tk75RgbRestoreGuard(); guard.Track(baseline, baseline, painted);
        Tk75RgbExchange.Execute(baseline, painted, device.Read, device.Write);
        // Initial state on reconnect can still be the previous temporary colors:
        // the explicit original must win, rather than that first observed state.
        guard = new Tk75RgbRestoreGuard(); guard.Track(baseline, painted, baseline);
        Check(Tk75RgbExchange.Equivalent(guard.Restore(delegate { return 3591; }, device.Read, device.Write), baseline), "Recovery session cleanup uses the explicit persisted original.");
        device = new Device(); baseline = device.Snapshot(); painted = Paint(baseline, true);
        guard = new Tk75RgbRestoreGuard(); guard.Track(baseline, baseline, painted);
        Reject(delegate { guard.Restore(delegate { return 3590; }, device.Read, device.Write); }, "A different freshly identified model blocks cleanup even when its RGB bytes could match.");
        Check(device.Reads == 0 && device.Writes == 0, "Changed model is rejected before RGB reads or writes.");
        bool identityFailed = false;
        try { guard.Restore(delegate { throw new IOException("Synthetic GET_ID failure."); }, device.Read, device.Write); }
        catch (IOException) { identityFailed = true; }
        Check(identityFailed && device.Reads == 0 && device.Writes == 0, "Unavailable identity preserves the backup without RGB traffic.");
    }
    public static int Run()
    {
        checks = 0;
        Device device = new Device(); Tk75RgbSnapshot original = device.Snapshot(); Tk75RgbSnapshot painted = Paint(original, true);
        Tk75RgbSnapshot actual = Tk75RgbExchange.Execute(original, painted, device.Read, device.Write);
        Check(Tk75RgbExchange.Equivalent(actual, painted), "Full update returns exactly confirmed target.");
        Check(device.Writes == 8 && device.Reads == 36 && device.Opcodes[7] == 7, "Full update verifies before/after and selects mode after all seven image chunks.");
        for (int i = 0; i < 7; i++) Check(device.Opcodes[i] == 12, "Picture precedes mode.");
        for (int i = 378; i < 384; i++) Check(actual.Picture[i] == original.Picture[i], "Unverified final six bytes never changed.");
        int before = device.Writes; actual = Tk75RgbExchange.Execute(painted, original, device.Read, device.Write);
        Check(device.Writes == before + 8 && Tk75RgbExchange.Equivalent(actual, original), "Restore reconstructs original non-custom effect and picture.");

        device = new Device(); original = device.Snapshot(); device.Settings[63] = 99;
        actual = Tk75RgbExchange.Execute(original, original, device.Read, device.Write);
        Check(device.Writes == 0 && device.Reads == 18 && actual.RawSettings[63] == 99, "No-op does not write, yet validates current state and returns current reserved bytes.");
        device = new Device(); original = device.Snapshot(); painted = Paint(original, false);
        actual = Tk75RgbExchange.Execute(original, painted, device.Read, device.Write);
        Check(device.Writes == 7 && device.Opcodes.TrueForAll(delegate(byte opcode) { return opcode == 12; }), "Single-key color change does not rewrite mode.");
        device = new Device(); original = device.Snapshot();
        Tk75RgbSnapshot modeOnly = new Tk75RgbSnapshot(3591, 2, 4, Tk75RgbProtocol.PictureModeSettings(original.RawSettings, 4), original.Picture);
        actual = Tk75RgbExchange.Execute(original, modeOnly, device.Read, device.Write);
        Check(device.Writes == 1 && device.Opcodes[0] == 7 && Tk75RgbExchange.Equivalent(actual, modeOnly), "Mode-only update never rewrites image.");

        device = new Device(); original = device.Snapshot(); painted = Paint(original, true); device.Picture[42] ^= 1;
        Reject(delegate { Tk75RgbExchange.Execute(original, painted, device.Read, device.Write); }, "External color change prevents overwrite.");
        Check(device.Writes == 0, "No writes after stale baseline.");
        device = new Device(); original = device.Snapshot(); painted = Paint(original, true); device.Profile = 3;
        Reject(delegate { Tk75RgbExchange.Execute(original, painted, device.Read, device.Write); }, "External profile switch prevents overwrite.");
        Check(device.Writes == 0, "No writes to wrong onboard profile.");
        foreach (int invalidByte in new int[] { 69, 270, 378, 383 })
        {
            device = new Device(); original = device.Snapshot(); byte[] invalidPicture = original.Picture; invalidPicture[invalidByte] ^= 1;
            Tk75RgbSnapshot invalid = new Tk75RgbSnapshot(3591, 2, 4, original.RawSettings, invalidPicture);
            Reject(delegate { Tk75RgbExchange.Execute(original, invalid, device.Read, device.Write); }, "Empty/encoder/tail RGB changes rejected.");
            Check(device.Reads == 0 && device.Writes == 0, "Invalid plan invokes neither transport callback.");
        }
        device = new Device(); original = device.Snapshot();
        foreach (Tk75RgbSnapshot invalid in new Tk75RgbSnapshot[] {
            new Tk75RgbSnapshot(3590,2,4,original.RawSettings,original.Picture),
            new Tk75RgbSnapshot(3591,3,4,original.RawSettings,original.Picture),
            new Tk75RgbSnapshot(3591,2,3,original.RawSettings,original.Picture) })
            Reject(delegate { Tk75RgbExchange.Execute(original, invalid, device.Read, device.Write); }, "Identity/profile/layer cannot be changed by transaction.");
        Check(device.Reads == 0 && device.Writes == 0, "Wrong identity plan never reaches device.");
        Reject(delegate { Tk75RgbExchange.Validate(null, original); }, "Expected baseline is compulsory.");
        Reject(delegate { Tk75RgbExchange.Validate(original, null); }, "Desired state is compulsory.");

        device = new Device(); device.Settings[1] = 13; device.Settings[4] = 0; original = device.Snapshot(); painted = Paint(original, true);
        Tk75RgbExchange.Execute(original, painted, device.Read, device.Write);
        actual = Tk75RgbExchange.Execute(painted, original, device.Read, device.Write);
        Check(actual.RawSettings[1] == 13 && actual.RawSettings[4] == 0, "Restore may reselect original custom picture0 while image4 is the backed-up override.");

        device = new Device(); original = device.Snapshot(); painted = Paint(original, true); device.IgnoreWrites = true;
        Reject(delegate { Tk75RgbExchange.Execute(original, painted, device.Read, device.Write); }, "Successful transport without actual device change is not success.");
        Check(device.Writes == 8, "Unconfirmed update has no hidden rollback writes.");
        device = new Device(); original = device.Snapshot(); painted = Paint(original, true); device.FailWriteAt = 2;
        bool interrupted = false; try { Tk75RgbExchange.Execute(original, painted, device.Read, device.Write); } catch (IOException) { interrupted = true; }
        Check(interrupted && device.Writes == 2 && device.Reads == 18, "Interrupted transfer stops immediately without claiming confirmation or automatic rollback.");
        Check(device.Settings[1] == 4, "Failure during picture transfer does not proceed to mode switch.");
        AutomaticRestoreChecks();
        return checks;
    }
}
