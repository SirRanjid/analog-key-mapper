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
        public bool AutomaticRestore;
    }
    // Pure journal reconciliation. No device reads, writes, files or callbacks.
    public sealed class RgbRecoveryDecision
    {
        public bool NeedsRecovery, CanRestore, AtOriginal;
        public string Error;
        public static RgbRecoveryDecision Assess(Tk75RgbSnapshot original, Tk75RgbSnapshot current, IEnumerable<RgbRecoveryStep> steps)
        { return Assess(original, current, steps, original); }

        // A separately validated startup repair can have an observed starting
        // state that differs from its clean restoration target. The caller owns
        // that provenance check; the seed alone never authorizes a device write.
        public static RgbRecoveryDecision Assess(Tk75RgbSnapshot original, Tk75RgbSnapshot current,
            IEnumerable<RgbRecoveryStep> steps, Tk75RgbSnapshot initialExpected)
        {
            try
            {
                if (steps == null) throw new InvalidDataException("The lighting journal step list is missing.");
                Tk75RgbExchange.Validate(original, current);
                Tk75RgbExchange.Validate(current, original);
                Tk75RgbExchange.Validate(original, initialExpected);
                Tk75RgbExchange.Validate(initialExpected, original);
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
                    bool chained = previous == null ? Tk75RgbExchange.Equivalent(step.Expected, initialExpected) :
                        previous.Confirmed != null ? Tk75RgbExchange.Equivalent(step.Expected, previous.Confirmed) :
                        Tk75RgbExchange.MatchesWritePrefix(previous.Expected, previous.Desired, step.Expected);
                    if (!chained && previous != null && previous.AutomaticRestore)
                        chained = Tk75RgbExchange.MatchesAutomaticRestorePrefix(previous.Confirmed ?? previous.Expected, previous.Desired, original, step.Expected);
                    if (!chained) throw new InvalidDataException("The lighting journal does not form a continuous expected-state chain.");
                    // Snapshot types are immutable; copy the mutable journal
                    // fields so enumeration cannot later alter the previous step.
                    previous = new RgbRecoveryStep { Sequence = step.Sequence, Expected = step.Expected, Desired = step.Desired, Confirmed = step.Confirmed, AutomaticRestore = step.AutomaticRestore };
                }
                if (Tk75RgbExchange.Equivalent(current, original))
                    return new RgbRecoveryDecision { AtOriginal = true };
                if (previous != null && (previous.Confirmed != null ? Tk75RgbExchange.Equivalent(current, previous.Confirmed) :
                    Tk75RgbExchange.MatchesWritePrefix(previous.Expected, previous.Desired, current)))
                    return new RgbRecoveryDecision { NeedsRecovery = true, CanRestore = true };
                if (previous != null && previous.AutomaticRestore &&
                    Tk75RgbExchange.MatchesAutomaticRestorePrefix(previous.Confirmed ?? previous.Expected, previous.Desired, original, current))
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
        static RgbRecoveryDecision Failed(string error)
        { return new RgbRecoveryDecision { NeedsRecovery = true, Error = error }; }
    }
}
