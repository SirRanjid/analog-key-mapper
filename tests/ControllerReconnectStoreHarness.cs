using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tk75.App;
using Tk75.Mapping;

namespace Tk75.Tests
{
    // Entirely in-memory persistence; no UI, registry, input, or outputs.
    public static class ControllerReconnectStoreHarness
    {
        static int checks;
        static readonly Profile Profile = new Profile { Controllers = new List<ControllerDefinition> {
            new ControllerDefinition { Id = "xbox", Name = "Player 1", Kind = ControllerKind.Xbox360 },
            new ControllerDefinition { Id = "ps", Name = "Player 2", Kind = ControllerKind.DualSense } } };
        static void Check(bool value, string message)
        { checks++; if (!value) throw new InvalidOperationException(message); }
        static void Reject(Action action, string message)
        { bool failed = false; try { action(); } catch (IOException) { failed = true; } catch (InvalidOperationException) { failed = true; } Check(failed, message); }

        sealed class Disk
        {
            internal readonly Dictionary<string, string> Files = new Dictionary<string, string>();
            internal string FailBefore, Ignore, FailAfter, FailRead;
            internal int Writes;
            internal string Read(string name)
            {
                if (name == FailRead) throw new IOException("Synthetic read failure.");
                string value; return Files.TryGetValue(name, out value) ? value : null;
            }
            internal void Write(string name, string content)
            {
                Writes++;
                if (name == FailBefore) throw new IOException("Synthetic write failure.");
                if (name != Ignore) Files[name] = content;
                if (name == FailAfter) throw new IOException("Synthetic failure after atomic replacement.");
            }
            internal ControllerReconnectStore Create() { return new ControllerReconnectStore(Read, Write); }
            internal ControllerReconnectStore Open() { var result = Create(); result.BeginSession(); return result; }
        }

        static ControllerReconnectSettings Capture(params string[] ids)
        { return ControllerReconnectSettings.Capture(true, "players.json", Profile, ids); }
        static void SaveClean(ControllerReconnectStore session, params string[] ids)
        { session.PrepareExit(Capture(ids)); session.ConfirmExit(); }
        static Disk PreviouslyClean(params string[] ids)
        { var disk = new Disk(); var first = disk.Open(); first.SetEnabled(true); SaveClean(first, ids); return disk; }
        static string[] Targets(ControllerReconnectStore session)
        { return session.StartupSnapshot == null ? new string[0] : session.StartupSnapshot.Targets("players.json", Profile); }

        static void DefaultsAndLegacy()
        {
            var disk = new Disk(); var fresh = disk.Open();
            Check(fresh.SessionReady && !fresh.Preferences.Enabled && Targets(fresh).Length == 0, "Fresh installations require manual connection.");
            Check(disk.Writes == 1 && disk.Files.ContainsKey(ControllerReconnectStore.SessionFile), "Startup writes the consumption guard before exposing targets.");
            fresh.SetEnabled(true);
            Check(fresh.Preferences.Enabled && Targets(fresh).Length == 0, "Opt-in stores a preference without scheduling existing controllers.");
            var enabledOnly = disk.Open();
            Check(enabledOnly.Preferences.Enabled && Targets(enabledOnly).Length == 0, "Opt-in alone survives restart but cannot create a remembered list.");
            disk = new Disk(); disk.Files[ControllerReconnectStore.SettingsFile] = Capture("xbox", "ps").Serialize();
            var legacy = disk.Open();
            Check(legacy.Preferences.Enabled && legacy.RecoveryRequired && Targets(legacy).Length == 0, "Legacy lists keep the explicit preference but are never auto-trusted.");
            SaveClean(legacy, "ps");
            var migrated = disk.Open();
            Check(Targets(migrated).SequenceEqual(new[] { "ps" }), "A new confirmed exit replaces the legacy list with current slots only.");
        }

        static void ConsumptionAndCrash()
        {
            var disk = PreviouslyClean("xbox", "ps");
            string oldSettings = disk.Read(ControllerReconnectStore.SettingsFile);
            var next = disk.Open();
            Check(Targets(next).SequenceEqual(new[] { "xbox", "ps" }), "A matching clean exit exposes its mixed controller list once.");
            Check(disk.Read(ControllerReconnectStore.SettingsFile) == oldSettings, "Consumption does not need to overwrite the old preference document.");
            Check(!disk.Read(ControllerReconnectStore.SessionFile).Contains("\"Completed\":true"), "The next session is already durably marked uncompleted.");
            var afterCrash = disk.Open();
            Check(afterCrash.Preferences.Enabled && afterCrash.RecoveryRequired && Targets(afterCrash).Length == 0, "A crash before clean exit cannot replay the consumed list.");
            SaveClean(afterCrash, "ps");
            Check(Targets(disk.Open()).SequenceEqual(new[] { "ps" }), "Manual recovery followed by clean exit creates a new trusted list.");

            disk = PreviouslyClean("xbox"); next = disk.Open(); next.PrepareExit(Capture("ps"));
            Check(Targets(disk.Open()).Length == 0, "A crash after saving settings but before exit confirmation remains untrusted.");
            disk = PreviouslyClean("xbox"); next = disk.Open(); next.PrepareExit(Capture("ps")); next.CancelExit(); next.ConfirmExit();
            Check(Targets(disk.Open()).Length == 0, "Cancelled close or failed profile save cannot confirm a prepared list later.");
            disk = PreviouslyClean("xbox"); next = disk.Open(); SaveClean(next);
            Check(Targets(disk.Open()).Length == 0, "Normal exit with all controllers off saves an empty list, not old active slots.");
        }

        static void FailedPersistence()
        {
            foreach (string mode in new[] { "fail", "ignore", "after" })
            {
                var disk = PreviouslyClean("xbox"); var next = disk.Open();
                if (mode == "fail") disk.FailBefore = ControllerReconnectStore.SettingsFile;
                if (mode == "ignore") disk.Ignore = ControllerReconnectStore.SettingsFile;
                if (mode == "after") disk.FailAfter = ControllerReconnectStore.SettingsFile;
                Reject(delegate { next.PrepareExit(Capture("ps")); }, "A settings failure or readback mismatch is surfaced: " + mode);
                next.ConfirmExit();
                disk.FailBefore = disk.Ignore = disk.FailAfter = null;
                var restarted = disk.Open();
                Check(restarted.Preferences.Enabled && Targets(restarted).Length == 0, "Failed exit persistence cannot reactivate either old or unconfirmed slots: " + mode);
            }
            foreach (string mode in new[] { "fail", "ignore" })
            {
                var disk = PreviouslyClean("xbox"); var next = disk.Open(); next.PrepareExit(Capture("ps"));
                if (mode == "fail") disk.FailBefore = ControllerReconnectStore.SessionFile; else disk.Ignore = ControllerReconnectStore.SessionFile;
                Reject(next.ConfirmExit, "Exit confirmation write/readback failure is reported: " + mode);
                disk.FailBefore = disk.Ignore = null;
                Check(Targets(disk.Open()).Length == 0, "An unconfirmed exit cannot schedule connection: " + mode);
            }
            // An exception after the final atomic commit cannot reveal obsolete
            // settings: both durable records already refer to the current exit.
            var appliedDisk = PreviouslyClean("xbox"); var applied = appliedDisk.Open(); applied.PrepareExit(Capture("ps"));
            appliedDisk.FailAfter = ControllerReconnectStore.SessionFile;
            Reject(applied.ConfirmExit, "A reported post-commit I/O error is visible.");
            appliedDisk.FailAfter = null;
            Check(Targets(appliedDisk.Open()).SequenceEqual(new[] { "ps" }), "If final commit landed, only that exact current list is eligible.");

            foreach (string mode in new[] { "fail", "ignore", "after", "read" })
            {
                var disk = PreviouslyClean("xbox"); var next = disk.Create();
                if (mode == "fail") disk.FailBefore = ControllerReconnectStore.SessionFile;
                if (mode == "ignore") disk.Ignore = ControllerReconnectStore.SessionFile;
                if (mode == "after") disk.FailAfter = ControllerReconnectStore.SessionFile;
                if (mode == "read") disk.FailRead = ControllerReconnectStore.SessionFile;
                Reject(next.BeginSession, "Startup guard failure is surfaced before any target can escape: " + mode);
                Check(!next.SessionReady && Targets(next).Length == 0, "Unverified persistence disables automatic startup for this session: " + mode);
                Reject(delegate { next.PrepareExit(Capture("ps")); }, "An uninitialized session cannot forge a clean record: " + mode);
            }
        }

        static void CorruptionAndPreferences()
        {
            var disk = PreviouslyClean("xbox");
            disk.Files[ControllerReconnectStore.SessionFile] = "{invalid";
            var invalid = disk.Open();
            Check(Targets(invalid).Length == 0 && invalid.Preferences.Enabled, "A malformed journal cannot authenticate a saved list.");
            disk = PreviouslyClean("xbox");
            var settings = ControllerReconnectSettings.Parse(disk.Read(ControllerReconnectStore.SettingsFile));
            settings.SessionId = Guid.NewGuid().ToString("N"); disk.Files[ControllerReconnectStore.SettingsFile] = settings.Serialize();
            Check(Targets(disk.Open()).Length == 0, "A valid but mismatched session token is rejected.");
            disk = PreviouslyClean("xbox"); disk.Files.Remove(ControllerReconnectStore.SessionFile);
            Check(Targets(disk.Open()).Length == 0, "A missing journal cannot authenticate the list.");
            disk = PreviouslyClean("xbox"); var next = disk.Open(); next.SetEnabled(false); SaveClean(next, "ps");
            var disabled = disk.Open();
            Check(!disabled.Preferences.Enabled && Targets(disabled).Length == 0, "Disabling reconnect persists independently of connected controllers.");
            disk = PreviouslyClean("xbox"); next = disk.Open(); next.PrepareExit(Capture("ps")); next.SetEnabled(false); next.ConfirmExit();
            Check(Targets(disk.Open()).Length == 0, "Changing the preference cancels any previously prepared exit record.");
            disk = PreviouslyClean("xbox"); next = disk.Open(); disk.FailBefore = ControllerReconnectStore.SettingsFile;
            Reject(delegate { next.SetEnabled(false); }, "Preference write failure is surfaced.");
            Check(next.Preferences.Enabled && Targets(next).Length == 0, "A failed toggle preserves actual persisted preference but cancels automatic work.");
            disk.FailBefore = null;
            Check(Targets(disk.Open()).Length == 0, "The failed toggle cannot revive the last consumed session.");

            disk = PreviouslyClean("xbox"); next = disk.Open(); var capture = Capture("ps"); next.PrepareExit(capture); capture.Controllers.Clear(); next.ConfirmExit();
            Check(Targets(disk.Open()).SequenceEqual(new[] { "ps" }), "Post-save mutation of the caller's snapshot cannot alter the committed record.");

            disk = PreviouslyClean("xbox"); next = disk.Open(); next.PrepareExit(Capture("ps"));
            var newerSession = disk.Open();
            Reject(next.ConfirmExit, "A different session cannot be overwritten by an obsolete exit confirmation.");
            Reject(delegate { next.PrepareExit(Capture("xbox")); }, "A superseded session cannot replace current preference data.");
            Check(newerSession.SessionReady && Targets(disk.Open()).Length == 0, "Losing session ownership leaves the new session unconfirmed.");
            disk = PreviouslyClean("xbox"); next = disk.Open(); next.PrepareExit(Capture("ps"));
            disk.Files[ControllerReconnectStore.SettingsFile] = Capture("xbox").Serialize();
            Reject(next.ConfirmExit, "Preferences changed after prepare cannot be confirmed as the captured clean exit.");
            Check(Targets(disk.Open()).Length == 0, "Modified on-disk preferences remain untrusted after failed confirmation.");
        }

        public static int Main()
        {
            try
            {
                DefaultsAndLegacy(); ConsumptionAndCrash(); FailedPersistence(); CorruptionAndPreferences();
                Console.WriteLine("PASS: " + checks + " in-memory controller reconnect persistence checks; no registry, UI, helpers or devices.");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
