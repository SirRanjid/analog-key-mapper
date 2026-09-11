using System;
using System.Collections.Generic;
using System.Linq;
using Tk75.Mapping;

public static class MappingPairingPolicyHarness
{
    static int checks;
    static void Require(bool value, string reason) { checks++; if (!value) throw new Exception(reason); }
    static void Reject(Action action)
    { bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } Require(rejected, "invalid metadata/request accepted"); }
    static Profile TwoControllers()
    {
        return new Profile { Controllers = new List<ControllerDefinition> {
            new ControllerDefinition { Id = "main", Name = "One", Kind = ControllerKind.Xbox360 },
            new ControllerDefinition { Id = "Main", Name = "Two", Kind = ControllerKind.DualSense }
        } };
    }
    public static int Run()
    {
        checks = 0; var allTargets = Enum.GetValues(typeof(OutputTarget)).Cast<OutputTarget>().ToArray();
        var legacy = new Profile(); var policy = new MappingPairingPolicy(legacy, "main");
        Require(policy.AvailableTargets(new[] { 14 }).SequenceEqual(allTargets), "empty legacy profile hid a valid output");
        Require(policy.AvailableTargets(new int[0]).Length == 0, "empty key selection offered a target");
        Require(policy.MissingKeys(new[] { 14, 14, 9 }, OutputTarget.A).SequenceEqual(new[] { 14, 9 }), "duplicate selection produced duplicate pair");
        Reject(delegate { new MappingPairingPolicy(legacy, "unknown"); });
        Reject(delegate { new MappingPairingPolicy(legacy, "MAIN"); });
        Reject(delegate { new MappingPairingPolicy(null, "main"); });
        Reject(delegate { policy.MissingKeys(null, OutputTarget.A); });
        Reject(delegate { policy.MissingKeys(new[] { -1 }, OutputTarget.A); });
        Reject(delegate { policy.AvailableTargets(new[] { 256 }); });
        Reject(delegate { policy.MissingKeys(new[] { 14 }, (OutputTarget)999); });

        var profile = TwoControllers();
        profile.Bindings.Add(new Binding { KeyIndex = 14, Target = OutputTarget.A, ControllerId = "main", Enabled = false });
        profile.Bindings.Add(new Binding { KeyIndex = 9, Target = OutputTarget.B, ControllerId = "main" });
        profile.Bindings.Add(new Binding { KeyIndex = 14, Target = OutputTarget.B, ControllerId = "Main" });
        policy = new MappingPairingPolicy(profile, "main");
        var second = new MappingPairingPolicy(profile, "Main");
        Require(policy.MissingKeys(new[] { 14 }, OutputTarget.A).Length == 0, "disabled existing binding was offered again");
        Require(policy.MissingKeys(new[] { 14, 9 }, OutputTarget.A).SequenceEqual(new[] { 9 }), "partially assigned selection lost missing member");
        Require(policy.MissingKeys(new[] { 14 }, OutputTarget.B).SequenceEqual(new[] { 14 }), "different target/controller occupied this pair");
        Require(second.MissingKeys(new[] { 14 }, OutputTarget.A).Length == 1, "route IDs were compared case-insensitively");
        Require(second.MissingKeys(new[] { 14 }, OutputTarget.B).Length == 0, "second controller ignored its own pair");
        Require(!policy.AvailableTargets(new[] { 14 }).Contains(OutputTarget.A), "forward drag offered an occupied target");
        Require(policy.AvailableTargets(new[] { 14, 9 }).Contains(OutputTarget.A), "forward multi-drag rejected a partially free target");
        Require(policy.MissingKeys(Enumerable.Range(0, 256), OutputTarget.A).Length == 255, "reverse drag hid unrelated physical keys");
        var result = policy.MissingKeys(new[] { 14, 9 }, OutputTarget.A); result[0] = 200;
        Require(policy.MissingKeys(new[] { 14, 9 }, OutputTarget.A)[0] == 9, "result array aliased internal state");
        profile.Bindings[0].KeyIndex = 12; profile.Bindings.Clear();
        Require(policy.MissingKeys(new[] { 14 }, OutputTarget.A).Length == 0, "mutable profile changed the gesture snapshot");

        var full = new Profile();
        foreach (OutputTarget target in allTargets) full.Bindings.Add(new Binding { KeyIndex = 14, Target = target, Enabled = false });
        policy = new MappingPairingPolicy(full, "main");
        Require(policy.AvailableTargets(new[] { 14 }).Length == 0, "fully mapped key retained a free partner");
        Require(policy.AvailableTargets(new[] { 14, 9 }).Length == allTargets.Length, "another free key was blocked by mapped selection member");
        Require(policy.MissingKeys(new[] { 14, 14 }, OutputTarget.A).Length == 0, "repeated mapped key was treated as new");
        full.Bindings.Add(new Binding { KeyIndex = 14, Target = OutputTarget.A });
        Reject(delegate { new MappingPairingPolicy(full, "main"); });

        // All source/target/slot combinations in a mixed sparse inventory. The
        // independent expected set also verifies forward/reverse symmetry.
        profile = TwoControllers();
        foreach (string slot in new[] { "main", "Main" })
            for (int key = 0; key < 12; key++)
                for (int target = 0; target < allTargets.Length; target++)
                    if ((key * 7 + target * 3 + (slot == "main" ? 1 : 2)) % 5 == 0)
                        profile.Bindings.Add(new Binding { ControllerId = slot, KeyIndex = key, Target = allTargets[target], Enabled = key % 2 == 0 });
        foreach (string slot in new[] { "main", "Main" })
        {
            policy = new MappingPairingPolicy(profile, slot);
            for (int key = 0; key < 12; key++)
            {
                OutputTarget[] available = policy.AvailableTargets(new[] { key });
                foreach (OutputTarget target in allTargets)
                {
                    bool missing = !profile.Bindings.Any(b => b.ControllerId == slot && b.KeyIndex == key && b.Target == target);
                    Require(available.Contains(target) == missing && (policy.MissingKeys(new[] { key }, target).Length == 1) == missing,
                        "partner directions disagree or leaked another route");
                }
            }
        }
        return checks;
    }
}
