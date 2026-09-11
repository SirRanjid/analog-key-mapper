using System;
using System.Collections.Generic;
using System.IO;

namespace Tk75.Diagnostics
{
    // Pure compare/exchange plan. The caller supplies transport, and must persist
    // the original backup and expected/desired journal before invoking Execute.
    public static class Tk75RgbExchange
    {
        public static bool SameSettings(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != 64 || right.Length != 64) return false;
            for (int i = 1; i <= 7; i++) if (left[i] != right[i]) return false;
            return true;
        }
        public static bool Equivalent(Tk75RgbSnapshot left, Tk75RgbSnapshot right)
        {
            return left != null && right != null && left.ModelId == right.ModelId && left.Profile == right.Profile && left.Layer == right.Layer &&
                SameSettings(left.RawSettings, right.RawSettings) && Tk75RgbProtocol.Equal(left.Picture, right.Picture);
        }
        public static void Validate(Tk75RgbSnapshot expected, Tk75RgbSnapshot desired)
        {
            if (expected == null || desired == null) throw new InvalidDataException("Expected and desired RGB snapshots are both required.");
            if (expected.ModelId != desired.ModelId || expected.Profile != desired.Profile || expected.Layer != desired.Layer)
                throw new InvalidDataException("RGB transaction cannot change keyboard model, onboard profile or light-picture layer.");
            byte[] settings = desired.RawSettings;
            if (settings[1] > 25) throw new InvalidDataException("Unknown target lighting mode.");
            // Restoring an original custom effect may select another existing
            // picture. Layer identifies the one image we backed up/modified;
            // it is not necessarily the effect's currently selected picture.
            if (settings[1] == 13 && ((settings[4] & 15) != 0 || (settings[4] >> 4) > 4)) throw new InvalidDataException("Unknown custom light-picture selection.");
            byte[] before = expected.Picture, after = desired.Picture;
            HashSet<int> allowed = new HashSet<int>(Tk75RgbProtocol.GetSupportedKeyIndices(expected.ModelId));
            for (int i = 0; i < before.Length; i++)
                if (before[i] != after[i] && (i >= Tk75RgbProtocol.WritablePictureLength || !allowed.Contains(i / 3)))
                    throw new InvalidDataException("RGB transaction would change an unknown LED position or the six unverified trailing bytes.");
        }
        public static Tk75RgbSnapshot Execute(Tk75RgbSnapshot expected, Tk75RgbSnapshot desired,
            Func<byte[], byte[]> read, Action<byte[]> write)
        {
            Validate(expected, desired);
            if (read == null) throw new ArgumentNullException("read");
            if (write == null) throw new ArgumentNullException("write");
            Tk75RgbSnapshot current = Tk75RgbProtocol.ReadSnapshot(expected.ModelId, expected.Layer, read);
            if (!Equivalent(current, expected)) throw new InvalidDataException("Keyboard lighting differs from the expected saved state; nothing was written.");
            bool pictureChanged = !Tk75RgbProtocol.Equal(current.Picture, desired.Picture);
            bool settingsChanged = !SameSettings(current.RawSettings, desired.RawSettings);
            if (!pictureChanged && !settingsChanged) return current;
            // Failures propagate without claiming success or issuing an unjournaled
            // automatic rollback. The caller must retain its recovery journal.
            if (pictureChanged)
                foreach (byte[] report in Tk75RgbProtocol.BuildPictureWrites(desired.Layer, desired.Picture)) write(report);
            if (settingsChanged) write(Tk75RgbProtocol.BuildSettingsWrite(desired.RawSettings));
            Tk75RgbSnapshot confirmed = Tk75RgbProtocol.ReadSnapshot(desired.ModelId, desired.Layer, read);
            if (!Equivalent(confirmed, desired)) throw new InvalidDataException("RGB update was not confirmed by complete readback; keep the recovery backup.");
            return confirmed;
        }
    }
}
