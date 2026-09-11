using System;
using System.Collections.Generic;
using System.IO;
using Tk75.Diagnostics;

namespace Tk75.App
{
    public sealed class RgbRecoveryStep
    {
        public int Sequence;
        public Tk75RgbSnapshot Expected, Desired, Confirmed;
    }
    // Pure journal reconciliation. No device reads, writes, files or callbacks.
    public sealed class RgbRecoveryDecision
    {
        public bool NeedsRecovery, CanRestore, AtOriginal;
        public string Error;
        public static RgbRecoveryDecision Assess(Tk75RgbSnapshot original, Tk75RgbSnapshot current, IEnumerable<RgbRecoveryStep> steps)
        {
            try
            {
                if (steps == null) throw new InvalidDataException("The lighting journal step list is missing.");
                Tk75RgbExchange.Validate(original, current);
                Tk75RgbExchange.Validate(current, original);
                RgbRecoveryStep previous = null;
                int count = 0;
                foreach (RgbRecoveryStep step in steps)
                {
                    if (++count > 4096) throw new InvalidDataException("The lighting journal exceeds 4096 steps.");
                    if (step == null || step.Sequence <= 0 || previous != null && step.Sequence <= previous.Sequence)
                        throw new InvalidDataException("Lighting journal sequence numbers must be positive and strictly increasing.");
                    Tk75RgbExchange.Validate(original, step.Expected);
                    Tk75RgbExchange.Validate(step.Expected, step.Desired);
                    if (step.Confirmed != null)
                    {
                        Tk75RgbExchange.Validate(step.Desired, step.Confirmed);
                        if (!Tk75RgbExchange.Equivalent(step.Confirmed, step.Desired))
                            throw new InvalidDataException("A confirmed lighting state does not match its requested state.");
                    }
                    bool chained = previous == null ? Tk75RgbExchange.Equivalent(step.Expected, original) :
                        previous.Confirmed != null ? Tk75RgbExchange.Equivalent(step.Expected, previous.Confirmed) :
                        MatchesWritePrefix(previous.Expected, previous.Desired, step.Expected);
                    if (!chained) throw new InvalidDataException("The lighting journal does not form a continuous expected-state chain.");
                    // Snapshot types are immutable; copy the mutable journal
                    // fields so enumeration cannot later alter the previous step.
                    previous = new RgbRecoveryStep { Sequence = step.Sequence, Expected = step.Expected, Desired = step.Desired, Confirmed = step.Confirmed };
                }
                if (Tk75RgbExchange.Equivalent(current, original))
                    return new RgbRecoveryDecision { AtOriginal = true };
                if (previous != null && (previous.Confirmed != null ? Tk75RgbExchange.Equivalent(current, previous.Confirmed) :
                    MatchesWritePrefix(previous.Expected, previous.Desired, current)))
                    return new RgbRecoveryDecision { NeedsRecovery = true, CanRestore = true };
                return Failed(previous == null ? "Lighting differs from the original backup, but no recorded change explains it." :
                    "Current lighting does not match a complete recorded state or an ordered write boundary of the last unconfirmed change. Automatic restoration is unavailable.");
            }
            catch (Exception error)
            {
                if (!(error is ArgumentException) && !(error is InvalidDataException) && !(error is InvalidOperationException)) throw;
                return Failed(error.Message);
            }
        }
        // Execute writes whole picture reports in order, followed by at most one
        // settings report. A lost acknowledgment may leave any complete-report
        // prefix visible. Reconstruct those exact states rather than accepting
        // arbitrary mixtures of old/new LED colors or partial report bytes.
        // This also covers firmware that stages the image until its last block:
        // it exposes the starting state or the completed picture state.
        static bool MatchesWritePrefix(Tk75RgbSnapshot expected, Tk75RgbSnapshot desired, Tk75RgbSnapshot candidate)
        {
            if (Tk75RgbExchange.Equivalent(candidate, expected)) return true;
            byte[] picture = expected.Picture, settings = expected.RawSettings;
            if (!Tk75RgbProtocol.Equal(picture, desired.Picture))
            {
                foreach (byte[] report in Tk75RgbProtocol.BuildPictureWrites(desired.Layer, desired.Picture))
                {
                    // Apply only the declared payload. The last report also
                    // carries six preserved trailing bytes outside that length.
                    int offset = report[4] * 56, length = report[5];
                    bool changed = false;
                    for (int i = 0; i < length; i++) if (picture[offset + i] != report[9 + i]) { changed = true; break; }
                    if (!changed) continue;
                    Buffer.BlockCopy(report, 9, picture, offset, length);
                    if (Tk75RgbExchange.Equivalent(candidate, new Tk75RgbSnapshot(expected.ModelId, expected.Profile, expected.Layer, settings, picture))) return true;
                }
            }
            if (!Tk75RgbExchange.SameSettings(settings, desired.RawSettings))
            {
                byte[] report = Tk75RgbProtocol.BuildSettingsWrite(desired.RawSettings);
                // Keep the response opcode and reserved response bytes intact.
                Buffer.BlockCopy(report, 2, settings, 1, 7);
                if (Tk75RgbExchange.Equivalent(candidate, new Tk75RgbSnapshot(expected.ModelId, expected.Profile, expected.Layer, settings, picture))) return true;
            }
            return false;
        }
        static RgbRecoveryDecision Failed(string error)
        { return new RgbRecoveryDecision { NeedsRecovery = true, Error = error }; }
    }
}
