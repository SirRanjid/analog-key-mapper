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
        // Only complete HID-report boundaries are recoverable. Arbitrary mixtures
        // can be another application's changes and must never be overwritten.
        public static bool MatchesWritePrefix(Tk75RgbSnapshot expected, Tk75RgbSnapshot desired, Tk75RgbSnapshot candidate)
        {
            foreach (Tk75RgbSnapshot state in WriteStates(expected, desired)) if (Equivalent(candidate, state)) return true;
            return false;
        }
        public static bool MatchesAutomaticRestorePrefix(Tk75RgbSnapshot expected, Tk75RgbSnapshot desired, Tk75RgbSnapshot original, Tk75RgbSnapshot candidate)
        {
            // Cleanup may have started after any complete report of an interrupted
            // color change. Its own interruption must also remain recoverable.
            foreach (Tk75RgbSnapshot state in WriteStates(expected, desired))
                if (MatchesWritePrefix(state, original, candidate)) return true;
            return false;
        }
        static IEnumerable<Tk75RgbSnapshot> WriteStates(Tk75RgbSnapshot expected, Tk75RgbSnapshot desired)
        {
            Validate(expected, desired);
            yield return expected;
            byte[] picture = expected.Picture, settings = expected.RawSettings;
            if (!Tk75RgbProtocol.Equal(picture, desired.Picture))
                foreach (byte[] report in Tk75RgbProtocol.BuildPictureWrites(desired.Layer, desired.Picture))
                {
                    Buffer.BlockCopy(report, 9, picture, report[4] * 56, report[5]);
                    yield return new Tk75RgbSnapshot(expected.ModelId, expected.Profile, expected.Layer, settings, picture);
                }
            if (!SameSettings(settings, desired.RawSettings))
            {
                byte[] report = Tk75RgbProtocol.BuildSettingsWrite(desired.RawSettings);
                Buffer.BlockCopy(report, 2, settings, 1, 7);
                yield return new Tk75RgbSnapshot(expected.ModelId, expected.Profile, expected.Layer, settings, picture);
            }
        }
        public static Tk75RgbSnapshot Execute(Tk75RgbSnapshot expected, Tk75RgbSnapshot desired,
            Func<byte[], byte[]> read, Action<byte[]> write)
        {
            Validate(expected, desired);
            if (read == null) throw new ArgumentNullException("read");
            if (write == null) throw new ArgumentNullException("write");
            Tk75RgbSnapshot current = Tk75RgbProtocol.VerifyKnownSnapshot(expected, read);
            if (!Equivalent(current, expected)) throw new InvalidDataException("Keyboard lighting differs from the expected saved state; nothing was written.");
            bool pictureChanged = !Tk75RgbProtocol.Equal(current.Picture, desired.Picture);
            bool settingsChanged = !SameSettings(current.RawSettings, desired.RawSettings);
            if (!pictureChanged && !settingsChanged) return current;
            // Failures propagate without claiming success or issuing an unjournaled
            // automatic rollback. The caller must retain its recovery journal.
            if (pictureChanged)
                foreach (byte[] report in Tk75RgbProtocol.BuildPictureWrites(desired.Layer, desired.Picture)) write(report);
            if (settingsChanged) write(Tk75RgbProtocol.BuildSettingsWrite(desired.RawSettings));
            Tk75RgbSnapshot confirmed = Tk75RgbProtocol.VerifyKnownSnapshot(desired, read);
            if (!Equivalent(confirmed, desired)) throw new InvalidDataException("RGB update was not confirmed by complete readback; keep the recovery backup.");
            return confirmed;
        }
    }

    // The HID-owning helper retains the parent's durably backed-up original.
    // Its final cleanup does not depend on the UI or a healthy parent pipe.
    public sealed class Tk75RgbRestoreGuard
    {
        Tk75RgbSnapshot original, expected, desired;
        bool confirmed;
        public bool Armed { get { return original != null; } }
        public void Track(Tk75RgbSnapshot baseline, Tk75RgbSnapshot before, Tk75RgbSnapshot after)
        {
            Tk75RgbExchange.Validate(baseline, before); Tk75RgbExchange.Validate(before, after);
            Tk75RgbExchange.Validate(after, baseline);
            if (original != null && !Tk75RgbExchange.Equivalent(original, baseline))
                throw new InvalidDataException("The original keyboard lighting cannot change during an active session.");
            original = baseline; expected = before; desired = after; confirmed = false;
        }
        public void Confirm(Tk75RgbSnapshot snapshot)
        {
            if (original == null) return;
            if (!Tk75RgbExchange.Equivalent(snapshot, desired)) throw new InvalidDataException("The cleanup guard received an unexpected write confirmation.");
            confirmed = true;
        }
        public Tk75RgbSnapshot Restore(Func<uint> identify, Func<byte[], byte[]> read, Action<byte[]> write)
        {
            if (original == null) return null;
            if (identify == null) throw new ArgumentNullException("identify");
            // RGB snapshots carry a caller-supplied model ID. A fresh independent
            // GET_ID must validate the live device before trusting those snapshots.
            if (identify() != original.ModelId) throw new InvalidDataException("The keyboard model changed; automatic cleanup retained the original backup.");
            Tk75RgbSnapshot current = Tk75RgbProtocol.ReadSnapshot(original.ModelId, original.Layer, read);
            if (Tk75RgbExchange.Equivalent(current, original)) return current;
            Tk75RgbExchange.Validate(current, original);
            Tk75RgbSnapshot before = confirmed ? desired : expected;
            if (!Tk75RgbExchange.MatchesWritePrefix(before, desired, current) &&
                !Tk75RgbExchange.MatchesAutomaticRestorePrefix(before, desired, original, current))
                throw new InvalidDataException("Keyboard lighting changed outside the recorded operation; automatic cleanup retained the original backup.");
            return Tk75RgbExchange.Execute(current, original, read, write);
        }
    }
}
