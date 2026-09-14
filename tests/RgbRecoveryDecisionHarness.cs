using System;
using System.Collections.Generic;
using Tk75.App;
using Tk75.Diagnostics;

public static class RgbRecoveryDecisionHarness
{
    static int checks;
    static void Check(bool value, string reason) { checks++; if (!value) throw new Exception(reason); }
    static Tk75RgbSnapshot Original()
    { var settings = new byte[64]; settings[0] = 0x87; settings[1] = 4; return new Tk75RgbSnapshot(3591, 2, 4, settings, new byte[384]); }
    static Tk75RgbSnapshot Paint(Tk75RgbSnapshot original, int color)
    { return new Tk75RgbSnapshot(original.ModelId, original.Profile, original.Layer, Tk75RgbProtocol.PictureModeSettings(original.RawSettings, 4), Tk75RgbProtocol.Overlay(original.ModelId, original.Picture, new Dictionary<int, int> { { 14, color } })); }
    static void Known(RgbRecoveryDecision value, string reason)
    { Check(value.NeedsRecovery && value.CanRestore && !value.AtOriginal && value.Error == null, reason); }
    static void Unknown(RgbRecoveryDecision value, string reason)
    { Check(value.NeedsRecovery && !value.CanRestore && !value.AtOriginal && !String.IsNullOrEmpty(value.Error), reason); }
    static IEnumerable<RgbRecoveryStep> TooMany(Tk75RgbSnapshot original)
    { for (int i = 1; i <= 4097; i++) yield return new RgbRecoveryStep { Sequence = i, Expected = original, Desired = original, Confirmed = original }; }
    static Tk75RgbSnapshot Prefix(Tk75RgbSnapshot expected, Tk75RgbSnapshot desired, int blocks, bool settingsWritten)
    {
        byte[] picture = expected.Picture;
        Buffer.BlockCopy(desired.Picture, 0, picture, 0, Math.Min(378, blocks * 56));
        return new Tk75RgbSnapshot(expected.ModelId, expected.Profile, expected.Layer, settingsWritten ? desired.RawSettings : expected.RawSettings, picture);
    }
    static void Recoverable(Tk75RgbSnapshot original, Tk75RgbSnapshot current, RgbRecoveryStep[] steps, string reason)
    {
        RgbRecoveryDecision value = RgbRecoveryDecision.Assess(original, current, steps);
        if (Tk75RgbExchange.Equivalent(original, current)) Check(value.AtOriginal && !value.NeedsRecovery && value.Error == null, reason);
        else Known(value, reason);
    }
    static void OrderedPrefixes()
    {
        foreach (uint model in new uint[] { 3590, 3591 })
        {
            byte[] originalSettings = Original().RawSettings, originalPicture = new byte[384];
            for (int i = 8; i < 64; i++) originalSettings[i] = (byte)(i + 30);
            for (int i = 0; i < originalPicture.Length; i++) originalPicture[i] = (byte)(i * 13 + 7);
            Tk75RgbSnapshot original = new Tk75RgbSnapshot(model, 2, 4, originalSettings, originalPicture);
            var colors = new Dictionary<int, int>();
            foreach (int key in Tk75RgbProtocol.GetSupportedKeyIndices(model)) colors.Add(key, 0xabcdef ^ (key * 313));
            Tk75RgbSnapshot desired = new Tk75RgbSnapshot(model, 2, 4, Tk75RgbProtocol.PictureModeSettings(originalSettings, 4), Tk75RgbProtocol.Overlay(model, originalPicture, colors));
            var write = new RgbRecoveryStep { Sequence = 1, Expected = original, Desired = desired };
            var journal = new[] { write };
            for (int blocks = 0; blocks <= 7; blocks++)
            {
                Tk75RgbSnapshot partial = Prefix(original, desired, blocks, false);
                Recoverable(original, partial, journal, "Every exact complete-report prefix is recoverable, including no-op final reports");
                var restore = new RgbRecoveryStep { Sequence = 2, Expected = partial, Desired = original };
                Recoverable(original, partial, new[] { write, restore }, "A restoration can start at each unconfirmed write boundary");
                for (int restoreBlocks = 0; restoreBlocks <= 7; restoreBlocks++)
                    Recoverable(original, Prefix(partial, original, restoreBlocks, false), new[] { write, restore }, "Interrupted restoration follows its own exact ordered boundaries");
                restore.Confirmed = original;
                Check(RgbRecoveryDecision.Assess(original, original, new[] { write, restore }).AtOriginal, "A confirmed restore chains from an incomplete earlier write");
                write.Confirmed = desired;
                Unknown(RgbRecoveryDecision.Assess(original, original, new[] { write, restore }), "At-original shortcut still validates a broken confirmed chain");
                write.Confirmed = null;
            }
            Known(RgbRecoveryDecision.Assess(original, desired, journal), "Final settings report follows complete picture");
            // All currently verified LEDs lie in blocks 0..4. Enumerate every
            // old/new whole-block mixture and both settings states independently.
            // Only cumulative prefixes, then final settings, may be explained.
            for (int mask = 0; mask < 32; mask++)
            {
                byte[] mixedPicture = original.Picture, target = desired.Picture;
                for (int block = 0; block < 5; block++) if ((mask & (1 << block)) != 0) Buffer.BlockCopy(target, block * 56, mixedPicture, block * 56, 56);
                bool prefix = mask == 0 || (mask & (mask + 1)) == 0;
                for (int useSettings = 0; useSettings < 2; useSettings++)
                {
                    Tk75RgbSnapshot mixed = new Tk75RgbSnapshot(model, 2, 4, useSettings == 0 ? original.RawSettings : desired.RawSettings, mixedPicture);
                    bool allowed = useSettings == 0 ? prefix : mask == 31;
                    if (allowed) Recoverable(original, mixed, journal, "Only ordered report boundaries are accepted");
                    else
                    {
                        Unknown(RgbRecoveryDecision.Assess(original, mixed, journal), "Out-of-order blocks or premature settings are rejected");
                        Unknown(RgbRecoveryDecision.Assess(original, original, new[] { write, new RgbRecoveryStep { Sequence = 2, Expected = mixed, Desired = original } }), "A follow-up restore cannot legitimize an impossible prior state");
                    }
                }
            }
            byte[] tornPicture = Prefix(original, desired, 1, false).Picture;
            tornPicture[0] = original.Picture[0];
            Tk75RgbSnapshot torn = new Tk75RgbSnapshot(model, 2, 4, original.RawSettings, tornPicture);
            Unknown(RgbRecoveryDecision.Assess(original, torn, journal), "A torn report byte is not a complete-report prefix");
            byte[] changedTail = Prefix(original, desired, 1, false).Picture; changedTail[378] ^= 1;
            Unknown(RgbRecoveryDecision.Assess(original, new Tk75RgbSnapshot(model, 2, 4, original.RawSettings, changedTail), journal), "An otherwise exact prefix cannot change trailing bytes");
            byte[] changedReservedLed = Prefix(original, desired, 1, false).Picture; changedReservedLed[90 * 3] ^= 1;
            Unknown(RgbRecoveryDecision.Assess(original, new Tk75RgbSnapshot(model, 2, 4, original.RawSettings, changedReservedLed), journal), "An otherwise exact prefix cannot change unverified LED bytes");
            byte[] tornSettings = original.RawSettings; tornSettings[1] = desired.RawSettings[1];
            Unknown(RgbRecoveryDecision.Assess(original, new Tk75RgbSnapshot(model, 2, 4, tornSettings, desired.Picture), journal), "A partial settings report is rejected");
            var pictureOnly = new Tk75RgbSnapshot(model, 2, 4, original.RawSettings, desired.Picture);
            write.Desired = pictureOnly;
            for (int blocks = 0; blocks <= 7; blocks++) Recoverable(original, Prefix(original, pictureOnly, blocks, false), journal, "Picture-only exchanges have the same exact prefixes");
            var settingsOnly = new Tk75RgbSnapshot(model, 2, 4, desired.RawSettings, original.Picture);
            write.Desired = settingsOnly;
            Recoverable(original, original, journal, "Settings-only exchange starts unchanged");
            Known(RgbRecoveryDecision.Assess(original, settingsOnly, journal), "Settings-only exchange accepts the complete settings report");
            Unknown(RgbRecoveryDecision.Assess(original, pictureOnly, journal), "Skipped picture writes cannot authorize a picture mutation");
            write.Desired = original;
            Recoverable(original, original, journal, "No-op exchange permits only the original");
            Unknown(RgbRecoveryDecision.Assess(original, settingsOnly, journal), "No-op exchange cannot authorize a settings mutation");
            Check(Tk75RgbProtocol.Equal(original.RawSettings, originalSettings) && Tk75RgbProtocol.Equal(original.Picture, originalPicture), "Prefix assessment preserves reserved bytes and immutable originals");
        }
    }
    static void OnboardProfileRetry()
    {
        Tk75RgbSnapshot original = Original(), marker = Paint(original, 0xFFAA22);
        var applied = new RgbRecoveryStep { Sequence = 1, Expected = original, Desired = marker, Confirmed = marker };
        var changedProfile = new Tk75RgbSnapshot(original.ModelId, 1, original.Layer, marker.RawSettings, marker.Picture);
        Unknown(RgbRecoveryDecision.Assess(original, changedProfile, new[] { applied }), "Identical lighting in another onboard profile does not authorize cross-profile restoration");
        // Synthetic new readback after the user returns to the original profile.
        // No profile or hardware operation is performed by this test.
        var returnedProfile = new Tk75RgbSnapshot(original.ModelId, original.Profile, original.Layer, changedProfile.RawSettings, changedProfile.Picture);
        Known(RgbRecoveryDecision.Assess(original, returnedProfile, new[] { applied }), "A fresh readback of the original profile can restore the exact recorded marker");
        var altered = Paint(original, 0x00FFFF);
        Unknown(RgbRecoveryDecision.Assess(original, altered, new[] { applied }), "Returning to the right profile does not authorize unrelated lighting changes");
        var restored = new RgbRecoveryStep { Sequence = 2, Expected = returnedProfile, Desired = original, Confirmed = original };
        RgbRecoveryDecision decision = RgbRecoveryDecision.Assess(original, original, new[] { applied, restored });
        Check(decision.AtOriginal && !decision.NeedsRecovery && decision.Error == null, "Successful retry produces the existing resolvable original-state chain");
        Check(changedProfile.Profile == 1 && original.Profile == 2 && applied.Confirmed == marker, "Retry assessment preserves both source contexts and the recovery authority");
    }
    static void AutomaticCleanupPrefixes()
    {
        Tk75RgbSnapshot original = Original(); var colors = new Dictionary<int, int>();
        foreach (int key in Tk75RgbProtocol.GetSupportedKeyIndices(original.ModelId)) colors[key] = 0x123456 + key;
        Tk75RgbSnapshot desired = new Tk75RgbSnapshot(original.ModelId, original.Profile, original.Layer,
            Tk75RgbProtocol.PictureModeSettings(original.RawSettings, original.Layer), Tk75RgbProtocol.Overlay(original.ModelId, original.Picture, colors));
        var write = new RgbRecoveryStep { Sequence = 1, Expected = original, Desired = desired, AutomaticRestore = true };
        for (int appliedBlocks = 0; appliedBlocks <= 7; appliedBlocks++)
            for (int restoredBlocks = 0; restoredBlocks <= 7; restoredBlocks++)
            {
                Tk75RgbSnapshot partial = Prefix(original, desired, appliedBlocks, false);
                Tk75RgbSnapshot cleanup = Prefix(partial, original, restoredBlocks, false);
                Recoverable(original, cleanup, new[] { write }, "An authorized helper cleanup is recoverable at every complete write/restore report boundary.");
                var repair = new RgbRecoveryStep { Sequence = 2, Expected = cleanup, Desired = original, Confirmed = original };
                Check(RgbRecoveryDecision.Assess(original, original, new[] { write, repair }).AtOriginal,
                    "A recovery transaction chains from an interrupted authorized helper cleanup.");
            }
        write.Confirmed = desired;
        for (int restoredBlocks = 0; restoredBlocks <= 7; restoredBlocks++)
            Recoverable(original, Prefix(desired, original, restoredBlocks, false), new[] { write },
                "Confirmed colors also authorize only an ordered cleanup back to the original.");
        Tk75RgbSnapshot partiallyRestored = Prefix(desired, original, 1, false);
        write.AutomaticRestore = false;
        Unknown(RgbRecoveryDecision.Assess(original, partiallyRestored, new[] { write }),
            "Legacy journals without helper cleanup authorization do not accept cleanup-only states.");
        write.AutomaticRestore = true;
        byte[] unrelated = partiallyRestored.Picture; unrelated[42] = 0xEF;
        Unknown(RgbRecoveryDecision.Assess(original, new Tk75RgbSnapshot(original.ModelId, original.Profile, original.Layer, partiallyRestored.RawSettings, unrelated), new[] { write }),
            "Cleanup authorization cannot explain an unrelated LED byte.");
    }
    static void SeededStartupRepair()
    {
        Tk75RgbSnapshot source = Original();
        var background = new Dictionary<int, int>();
        var activeColors = new Dictionary<int, int>();
        foreach (int key in Tk75RgbProtocol.GetSupportedKeyIndices(source.ModelId))
        { background[key] = 0x192837; activeColors[key] = 0xAB1000 + key; }
        var original = new Tk75RgbSnapshot(source.ModelId, source.Profile, source.Layer,
            Tk75RgbProtocol.PictureModeSettings(source.RawSettings, source.Layer), Tk75RgbProtocol.Overlay(source.ModelId, source.Picture, background));
        var observed = new Tk75RgbSnapshot(original.ModelId, original.Profile, original.Layer, original.RawSettings,
            Tk75RgbProtocol.Overlay(original.ModelId, original.Picture, new Dictionary<int, int> { { 14, 0xFFC65C }, { 65, 0x79C8AF } }));
        var cleanup = new RgbRecoveryStep { Sequence = 1, Expected = observed, Desired = original, AutomaticRestore = true };
        var journal = new[] { cleanup };
        Unknown(RgbRecoveryDecision.Assess(original, observed, new RgbRecoveryStep[0], observed),
            "A validated startup seed without a recorded transaction cannot authorize restoration.");
        Check(RgbRecoveryDecision.Assess(original, original, new RgbRecoveryStep[0], observed).AtOriginal,
            "An empty seeded journal can recognize an already clean original.");
        Unknown(RgbRecoveryDecision.Assess(original, observed, journal),
            "Legacy assessment never treats a differing startup seed as the original.");
        Known(RgbRecoveryDecision.Assess(original, observed, journal, observed),
            "A durably recorded startup cleanup starts from its separately validated observation.");
        for (int blocks = 0; blocks <= 7; blocks++)
        {
            Tk75RgbSnapshot partial = Prefix(observed, original, blocks, false);
            RgbRecoveryDecision value = RgbRecoveryDecision.Assess(original, partial, journal, observed);
            if (Tk75RgbExchange.Equivalent(partial, original)) Check(value.AtOriginal && !value.NeedsRecovery, "Startup cleanup recognizes its clean target.");
            else Known(value, "Every complete startup cleanup report boundary is recoverable.");
            var retry = new RgbRecoveryStep { Sequence = 2, Expected = partial, Desired = original, Confirmed = original };
            Check(RgbRecoveryDecision.Assess(original, original, new[] { cleanup, retry }, observed).AtOriginal,
                "Startup cleanup retry chains from the recorded partial write.");
        }
        var wrongSeed = Paint(observed, 0x112233);
        Unknown(RgbRecoveryDecision.Assess(original, original, journal, wrongSeed),
            "A mismatched startup seed fails chain validation even when current lighting is clean.");
        Unknown(RgbRecoveryDecision.Assess(original, observed, journal, null), "Missing startup provenance cannot seed a recovery chain.");
        foreach (var wrong in new[] {
            new Tk75RgbSnapshot(3590, observed.Profile, observed.Layer, observed.RawSettings, observed.Picture),
            new Tk75RgbSnapshot(observed.ModelId, 1, observed.Layer, observed.RawSettings, observed.Picture),
            new Tk75RgbSnapshot(observed.ModelId, observed.Profile, 3, observed.RawSettings, observed.Picture) })
            Unknown(RgbRecoveryDecision.Assess(original, original, new RgbRecoveryStep[0], wrong),
                "A startup seed must share the original model, onboard profile and layer even before writes.");
        foreach (int offset in new[] { 90 * 3, 383 })
        {
            byte[] invalid = observed.Picture; invalid[offset] ^= 1;
            var unsafeSeed = new Tk75RgbSnapshot(observed.ModelId, observed.Profile, observed.Layer, observed.RawSettings, invalid);
            Unknown(RgbRecoveryDecision.Assess(original, original, new RgbRecoveryStep[0], unsafeSeed),
                "A startup seed cannot legitimize unknown LED positions or trailing-byte differences.");
        }
        cleanup.Confirmed = original;
        Check(RgbRecoveryDecision.Assess(original, original, journal, observed).AtOriginal,
            "Confirmed startup cleanup restores the clean baseline, not the stale observed marker.");
        Unknown(RgbRecoveryDecision.Assess(original, observed, journal, observed),
            "Confirmed startup cleanup does not authorize a return to the stale observation.");
        var active = new Tk75RgbSnapshot(original.ModelId, original.Profile, original.Layer, original.RawSettings,
            Tk75RgbProtocol.Overlay(original.ModelId, original.Picture, activeColors));
        var apply = new RgbRecoveryStep { Sequence = 2, Expected = original, Desired = active, AutomaticRestore = true };
        var later = new[] { cleanup, apply };
        Known(RgbRecoveryDecision.Assess(original, active, later, observed),
            "Normal color updates can follow a seeded cleanup from the clean original.");
        for (int appliedBlocks = 0; appliedBlocks <= 7; appliedBlocks++)
            for (int restoredBlocks = 0; restoredBlocks <= 7; restoredBlocks++)
            {
                Tk75RgbSnapshot partial = Prefix(original, active, appliedBlocks, false);
                Tk75RgbSnapshot restored = Prefix(partial, original, restoredBlocks, false);
                RgbRecoveryDecision value = RgbRecoveryDecision.Assess(original, restored, later, observed);
                if (Tk75RgbExchange.Equivalent(restored, original)) Check(value.AtOriginal && !value.NeedsRecovery, "Seeded automatic cleanup recognizes its clean baseline.");
                else Known(value, "Seeded journals retain automatic cleanup recovery at complete report boundaries.");
                var repair = new RgbRecoveryStep { Sequence = 3, Expected = restored, Desired = original, Confirmed = original };
                Check(RgbRecoveryDecision.Assess(original, original, new[] { cleanup, apply, repair }, observed).AtOriginal,
                    "Normal restoration chains from an interrupted helper cleanup after a seeded startup.");
            }
        apply.Confirmed = active;
        for (int restoredBlocks = 0; restoredBlocks <= 7; restoredBlocks++)
        {
            Tk75RgbSnapshot restored = Prefix(active, original, restoredBlocks, false);
            RgbRecoveryDecision value = RgbRecoveryDecision.Assess(original, restored, later, observed);
            if (Tk75RgbExchange.Equivalent(restored, original)) Check(value.AtOriginal && !value.NeedsRecovery, "Confirmed colors still restore to the clean baseline.");
            else Known(value, "Confirmed ordinary colors permit ordered helper restoration after a seeded startup.");
        }
        apply.AutomaticRestore = false;
        Unknown(RgbRecoveryDecision.Assess(original, Prefix(active, original, 1, false), later, observed),
            "A startup seed does not grant helper-cleanup authority to later transactions.");
    }
    public static string Run()
    {
        checks = 0;
        OnboardProfileRetry();
        AutomaticCleanupPrefixes();
        SeededStartupRepair();
        var original = Original(); var first = Paint(original, 0x123456); var second = Paint(original, 0xabcdef); var third = Paint(original, 0xffffff);
        var step = new RgbRecoveryStep { Sequence = 1, Expected = original, Desired = first };
        Known(RgbRecoveryDecision.Assess(original, first, new[] { step }), "Crash after write before confirmation can restore the recorded target");
        var atOriginal = RgbRecoveryDecision.Assess(original, original, new[] { step });
        Check(atOriginal.AtOriginal && !atOriginal.NeedsRecovery && !atOriginal.CanRestore && atOriginal.Error == null, "Crash before first write is already at original");
        step.Confirmed = first;
        Known(RgbRecoveryDecision.Assess(original, first, new[] { step }), "Completed application left active still needs recovery");
        var next = new RgbRecoveryStep { Sequence = 3, Expected = first, Desired = second };
        Known(RgbRecoveryDecision.Assess(original, first, new[] { step, next }), "Crash before a later write permits the previous recorded state");
        Known(RgbRecoveryDecision.Assess(original, second, new[] { step, next }), "Crash after a later write permits its target");
        next.Confirmed = second;
        Unknown(RgbRecoveryDecision.Assess(original, first, new[] { step, next }), "Confirmed last step cannot claim its stale starting state");
        var restore = new RgbRecoveryStep { Sequence = 4, Expected = second, Desired = original, Confirmed = original };
        atOriginal = RgbRecoveryDecision.Assess(original, original, new[] { step, next, restore });
        Check(atOriginal.AtOriginal && !atOriginal.NeedsRecovery, "Confirmed restoration can be durably resolved by caller");
        restore.Confirmed = null;
        Known(RgbRecoveryDecision.Assess(original, second, new[] { step, next, restore }), "Crash before restoration is still recoverable");
        Unknown(RgbRecoveryDecision.Assess(original, third, new[] { step, next }), "Unrecorded complete state is not overwritten");
        var mixedPicture = second.Picture; mixedPicture[42] = first.Picture[42];
        var mixed = new Tk75RgbSnapshot(3591, 2, 4, second.RawSettings, mixedPicture);
        Unknown(RgbRecoveryDecision.Assess(original, mixed, new[] { step, next }), "Partial application cannot be mistaken for a known complete target");
        Unknown(RgbRecoveryDecision.Assess(original, first, new RgbRecoveryStep[0]), "No journal cannot authorize replacement of changed lighting");
        Check(RgbRecoveryDecision.Assess(original, original, new RgbRecoveryStep[0]).AtOriginal, "No writes and unchanged original need no recovery");
        Unknown(RgbRecoveryDecision.Assess(original, original, null), "Missing journal list is distinct from an empty valid journal");
        Unknown(RgbRecoveryDecision.Assess(null, first, new[] { step }), "Missing backup is not recoverable");
        Unknown(RgbRecoveryDecision.Assess(original, null, new[] { step }), "Missing current readback is not recoverable");
        Unknown(RgbRecoveryDecision.Assess(original, first, new RgbRecoveryStep[] { null }), "Missing step is rejected");
        foreach (int sequence in new[] { -1, 0 })
            Unknown(RgbRecoveryDecision.Assess(original, first, new[] { new RgbRecoveryStep { Sequence = sequence, Expected = original, Desired = first } }), "Nonpositive sequence is rejected");
        foreach (int sequence in new[] { 1, 0 })
            Unknown(RgbRecoveryDecision.Assess(original, second, new[] { step, new RgbRecoveryStep { Sequence = sequence, Expected = first, Desired = second } }), "Duplicate or decreasing sequence is rejected");
        Unknown(RgbRecoveryDecision.Assess(original, second, new[] { new RgbRecoveryStep { Sequence = 1, Expected = first, Desired = second } }), "First expected state must match the original");
        Unknown(RgbRecoveryDecision.Assess(original, third, new[] { step, new RgbRecoveryStep { Sequence = 2, Expected = second, Desired = third } }), "Broken later expected chain is rejected");
        Unknown(RgbRecoveryDecision.Assess(original, first, new[] { new RgbRecoveryStep { Sequence = 1, Expected = original, Desired = first, Confirmed = second } }), "Confirmation must match desired snapshot");
        var failed = new RgbRecoveryStep { Sequence = 1, Expected = original, Desired = first };
        Known(RgbRecoveryDecision.Assess(original, second, new[] { failed, new RgbRecoveryStep { Sequence = 2, Expected = original, Desired = second } }), "A failed write may be followed from its unchanged expected state");
        Known(RgbRecoveryDecision.Assess(original, second, new[] { failed, new RgbRecoveryStep { Sequence = 2, Expected = first, Desired = second } }), "An unconfirmed successful write may be followed from its desired state");
        foreach (var wrong in new[] { new Tk75RgbSnapshot(3590, 2, 4, first.RawSettings, first.Picture), new Tk75RgbSnapshot(3591, 3, 4, first.RawSettings, first.Picture), new Tk75RgbSnapshot(3591, 2, 3, first.RawSettings, first.Picture) })
            Unknown(RgbRecoveryDecision.Assess(original, wrong, new[] { step }), "Wrong model, onboard profile or image layer is rejected");
        var unknownLed = first.Picture; unknownLed[90 * 3] = 1;
        var unknown = new Tk75RgbSnapshot(3591, 2, 4, first.RawSettings, unknownLed);
        Unknown(RgbRecoveryDecision.Assess(original, unknown, new[] { step }), "Unverified LED change blocks recovery");
        Unknown(RgbRecoveryDecision.Assess(original, unknown, new[] { new RgbRecoveryStep { Sequence = 1, Expected = original, Desired = unknown } }), "Journal cannot legitimize an unverified LED mutation");
        var tail = first.Picture; tail[383] = 1;
        Unknown(RgbRecoveryDecision.Assess(original, new Tk75RgbSnapshot(3591, 2, 4, first.RawSettings, tail), new[] { step }), "Unverified trailing bytes stay protected");
        Unknown(RgbRecoveryDecision.Assess(original, original, TooMany(original)), "Journal enumeration has a finite step budget");
        Check(Tk75RgbExchange.Equivalent(original, Original()) && step.Confirmed == first && next.Confirmed == second, "Assessment does not mutate snapshots or journal steps");
        OrderedPrefixes();
        return "RGB recovery: " + checks + " pure checks PASS";
    }
}
