using System;
using System.Collections.Generic;
using Tk75.App;
using Tk75.Mapping;

namespace Tk75.Tests
{
    // Only synthetic profiles, connection observations and explicit clock values.
    public static class ControllerConnectionPolicyHarness
    {
        static int checks;
        static readonly DateTime Start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        static bool No(string id) { return false; }
        static void Check(bool condition, string message)
        { checks++; if (!condition) throw new InvalidOperationException(message); }
        static string Next(ControllerConnectionPolicy policy, DateTime time)
        { return policy.Next(time, No, No); }

        static Profile Players()
        {
            return new Profile {
                Controllers = new List<ControllerDefinition> {
                    new ControllerDefinition { Id = "main", Name = "Player 1", Kind = ControllerKind.Xbox360 },
                    new ControllerDefinition { Id = "ps", Name = "Player 2", Kind = ControllerKind.DualSense },
                    new ControllerDefinition { Id = "empty", Name = "Unconfigured", Kind = ControllerKind.Xbox360 } },
                Bindings = new List<Binding> {
                    new Binding { ControllerId = "main", KeyIndex = 14, Target = OutputTarget.A },
                    new Binding { ControllerId = "ps", KeyIndex = 30, Target = OutputTarget.B },
                    new Binding { ControllerId = "empty", KeyIndex = 31, Target = OutputTarget.X, Enabled = false } } };
        }

        static void StartupAndPending()
        {
            var profile = Players(); var policy = new ControllerConnectionPolicy();
            policy.Configure(profile, true);
            Check(Next(policy, Start) == "main", "Startup chooses the first configured route without a saved connection snapshot.");
            Check(policy.Next(Start, id => id == "main", No) == "ps", "Already connected routes are skipped.");
            Check(policy.Next(Start, No, id => id == "main") == "ps", "Pending routes do not block another due controller.");
            Check(policy.Next(Start, id => id != "empty", No) == null, "A slot with only disabled mappings never connects automatically.");
            policy.SetDesired("empty", true);
            Check(!policy.IsDesired("empty"), "Even manual retry intent waits until a route has enabled mappings.");
            profile.Bindings[2].Enabled = true; policy.Configure(profile, true);
            Check(policy.Next(Start, id => id != "empty", No) == "empty", "Adding a mapping activates an eligible configured slot.");
            profile.Bindings.Clear(); policy.Configure(profile, true);
            Check(Next(policy, Start) == null, "Removing all mappings leaves no automatic work.");
            var legacy = new Profile { Bindings = new List<Binding> { new Binding { KeyIndex = 14, Target = OutputTarget.A } } };
            policy = new ControllerConnectionPolicy(); policy.Configure(legacy, true);
            Check(Next(policy, Start) == "main", "Legacy single-controller profiles are eligible at startup.");
        }

        static void RetryAndRecovery()
        {
            var policy = new ControllerConnectionPolicy(); policy.Configure(Players(), true);
            policy.Failed("main", Start);
            Check(Next(policy, Start) == "ps", "A failed first controller does not block other players.");
            policy.Failed("ps", Start);
            Check(Next(policy, Start.AddMilliseconds(1999)) == null, "Failure waits two seconds before the first retry.");
            Check(Next(policy, Start.AddSeconds(2)) == "main", "The first retry becomes due exactly at its deadline.");
            DateTime time = Start.AddSeconds(2);
            foreach (int seconds in new[] { 4, 8, 16, 30, 30, 30 })
            {
                policy.Failed("main", time);
                Check(policy.Next(time.AddSeconds(seconds).AddTicks(-1), id => id == "ps", No) == null, "Retry delay is enforced: " + seconds);
                time = time.AddSeconds(seconds);
                Check(policy.Next(time, id => id == "ps", No) == "main", "Retry is due after bounded exponential backoff: " + seconds);
            }
            policy.Succeeded("main"); policy.Failed("main", time);
            Check(policy.Next(time.AddSeconds(2), id => id == "ps", No) == "main", "Success resets the delay for a later loss.");
            policy.Failed("main", time.AddSeconds(2));
            policy.Next(time.AddSeconds(2), id => true, No);
            Check(policy.Next(time.AddSeconds(2), id => id == "ps", No) == null, "Observed connection loss starts a fresh delay.");
            Check(policy.Next(time.AddSeconds(4), id => id == "ps", No) == "main", "Observed connection clears stale backoff and retries two seconds after loss.");
        }

        static void ConnectionLoss()
        {
            var profile = Players(); var policy = new ControllerConnectionPolicy(); policy.Configure(profile, true);
            policy.Succeeded("main");
            Check(Next(policy, Start) == "ps", "Immediate loss after successful connection delays that route without starving a peer.");
            Check(policy.Next(Start.AddMilliseconds(1999), id => id == "ps", No) == null, "A short-lived successful connection cannot retry on each UI tick.");
            Check(policy.Next(Start.AddSeconds(2), id => id == "ps", No) == "main", "A short-lived successful connection retries after two seconds.");
            policy.Succeeded("main");
            Check(policy.Next(Start.AddSeconds(2), id => id == "ps", No) == null, "Another immediate output loss starts another delay.");
            Check(policy.Next(Start.AddSeconds(4), id => id == "ps", No) == "main", "Repeated output loss retains a minimum two-second retry interval.");

            policy.Next(Start.AddSeconds(5), id => true, No);
            profile.Bindings[0].Processing.Scale = .7; policy.Configure(profile, true);
            Check(policy.Next(Start.AddSeconds(5), id => id == "ps", No) == null, "Settings edits preserve the observation of a connection before loss.");
            Check(policy.Next(Start.AddSeconds(6.999), id => id == "ps", No) == null, "Observed live connection loss waits until its deadline.");
            Check(policy.Next(Start.AddSeconds(7), id => id == "ps", No) == "main", "Observed live connection loss becomes due after two seconds.");

            policy.Succeeded("main");
            Check(policy.Next(Start.AddSeconds(8), id => id == "ps", id => id == "main") == null, "A pending connection is not treated as a loss.");
            Check(policy.Next(Start.AddSeconds(10), id => id == "ps", No) == null, "Loss delay begins when the pending connection has ended.");
            policy.SetDesired("main", true);
            Check(policy.Next(Start.AddSeconds(10), id => id == "ps", No) == "main", "An explicit connection request bypasses a loss retry delay.");
            policy.Succeeded("main"); policy.SetDesired("main", true);
            Check(policy.Next(Start.AddSeconds(10), id => id == "ps", No) == "main", "An explicit connection request also clears an unobserved loss.");

            policy.Succeeded("main");
            profile = ControllerRouting.SetKind(profile, "main", ControllerKind.DualSense); policy.Configure(profile, true);
            Check(policy.Next(Start.AddSeconds(10), id => id == "ps", No) == "main", "Changing controller type clears the old connection observation.");

            policy = new ControllerConnectionPolicy(); policy.Configure(Players(), true);
            policy.Succeeded("ps");
            Check(Next(policy, Start) == "main", "A due first controller does not hide a later controller's loss.");
            policy.Failed("main", Start);
            Check(Next(policy, Start.AddSeconds(2)) == "main", "Both routes may become due together.");
            Check(policy.Next(Start.AddSeconds(2), No, id => id == "main") == "ps", "The later route's loss deadline was recorded despite the earlier due controller.");
        }

        static void ManualIntentAndStop()
        {
            var profile = Players(); var policy = new ControllerConnectionPolicy(); policy.Configure(profile, true);
            policy.SetDesired("main", false);
            profile.Bindings[0].Processing.Scale = .8; policy.Configure(profile, true);
            Check(Next(policy, Start) == "ps" && !policy.IsDesired("main"), "Settings edits never revive a manually disconnected route.");
            profile = ControllerRouting.SetKind(profile, "main", ControllerKind.DualSense); policy.Configure(profile, true);
            Check(!policy.IsDesired("main"), "Changing controller type preserves an explicit manual disconnect.");
            policy.SetDesired("main", true); policy.Configure(profile, false);
            Check(policy.IsDesired("main") && !policy.IsDesired("ps"), "Disabling automation retains manual intent but clears implicit automatic intent.");
            policy.StopAll(); policy.Configure(profile, true);
            Check(Next(policy, Start) == null, "Stop all remains stopped after a settings edit.");
            profile = ControllerRouting.Add(profile, "new", "New player", ControllerKind.Xbox360);
            profile.Bindings.Add(new Binding { ControllerId = "new", KeyIndex = 32, Target = OutputTarget.Y }); policy.Configure(profile, true);
            Check(Next(policy, Start) == null, "New controllers do not circumvent stop all.");
            policy.SetDesired("ps", true);
            Check(Next(policy, Start) == "ps" && !policy.IsDesired("main") && !policy.IsDesired("new"), "Manual connection after stop all revives only the selected controller.");
            policy.StopAll(); policy.Failed("ps", Start); policy.Succeeded("ps"); policy.Configure(profile, true);
            Check(Next(policy, Start) == null, "Stale completions never revive stopped intent.");
        }

        static void ProfileChanges()
        {
            var profile = Players(); var policy = new ControllerConnectionPolicy(); policy.Configure(profile, true);
            policy.Failed("main", Start); policy.Failed("main", Start);
            profile.Controllers[0].Name = "Renamed player"; profile.Bindings[0].Processing.Scale = .5;
            policy.Configure(profile, true);
            Check(policy.Next(Start.AddSeconds(2), id => id == "ps", No) == null, "Ordinary edits retain retry backoff.");
            profile = ControllerRouting.SetKind(profile, "main", ControllerKind.DualSense); policy.Configure(profile, true);
            Check(Next(policy, Start) == "main", "A changed controller type can attempt immediately with fresh retry state.");
            policy.SetDesired("main", true); policy.Failed("main", Start);
            profile = ControllerRouting.Remove(profile, "main"); policy.Configure(profile, false);
            Check(!policy.IsDesired("main") && Next(policy, Start) == null, "Removed routes lose manual intent and retry state.");
            policy.Failed("main", Start); policy.Succeeded("main"); policy.SetDesired("main", true);
            profile = ControllerRouting.Add(profile, "main", "Replacement player", ControllerKind.DualSense);
            profile.Bindings.Add(new Binding { ControllerId = "main", KeyIndex = 14, Target = OutputTarget.A }); policy.Configure(profile, false);
            Check(Next(policy, Start) == null, "Reusing a removed ID does not inherit an old manual request.");
            policy.Configure(profile, true);
            Check(policy.Next(Start, id => id == "ps", No) == "main", "Readded automatic routes receive fresh retry state.");
            policy = new ControllerConnectionPolicy(); policy.Configure(profile, true);
            Check(Next(policy, Start) == "ps", "Readded routes follow existing players in profile order.");
            profile.Controllers.Reverse(); policy.Configure(profile, true);
            Check(Next(policy, Start) == "main", "Candidate order follows the current profile after reordering.");
        }

        public static int Main()
        {
            try
            {
                StartupAndPending(); RetryAndRecovery(); ConnectionLoss(); ManualIntentAndStop(); ProfileChanges();
                Console.WriteLine("PASS: " + checks + " controller connection policy checks; synthetic profiles and clock only.");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
