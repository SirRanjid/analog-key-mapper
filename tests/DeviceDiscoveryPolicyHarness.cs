using System;
using Tk75.App;

public static class DeviceDiscoveryPolicyHarness
{
    static int checks;
    static void Require(bool value, string reason) { checks++; if (!value) throw new Exception(reason); }
    static DeviceDiscoveryMetadata Input(string path)
    {
        return new DeviceDiscoveryMetadata { Path = path, Product = "TK75 TMR", VendorId = 0x3151, ProductId = 0x5030,
            UsagePage = 0xffff, Usage = 1, InputLength = 32, HasTravelDecoder = true };
    }
    static DeviceDiscoveryMetadata Config(string path)
    {
        var result = Input(path); result.Usage = 2; result.InputLength = 0; result.FeatureLength = 65; result.HasTravelDecoder = false; return result;
    }
    public static int Run()
    {
        checks = 0;
        var input = Input("input-A"); var config = Config("config-A");
        Require(DeviceDiscoveryPolicy.SelectPath(new[] { input, config }, null) == input.Path, "unique full pair not selected");
        Require(DeviceDiscoveryPolicy.SelectPath(new[] { input }, null) == null, "missing config was accepted");
        Require(DeviceDiscoveryPolicy.SelectPath(new[] { input, config, Config("config-B") }, null) == null, "ambiguous configs were paired by guess");
        Require(DeviceDiscoveryPolicy.SelectPath(new[] { input, Input("input-B"), config }, null) == null, "multiple inputs arbitrarily selected");
        Require(DeviceDiscoveryPolicy.SelectPath(new[] { input, Input("input-B"), config }, "INPUT-b") == "INPUT-b", "manual choice not preserved case-insensitively");
        Require(DeviceDiscoveryPolicy.SelectPath(new[] { input, config }, "removed") == input.Path, "removed selection prevented unique present choice");
        var unknown = Input("unknown"); unknown.Product = "Another keyboard";
        Require(DeviceDiscoveryPolicy.SelectPath(new[] { unknown, config }, null) == null, "decoder alone guessed another model");
        Require(DeviceDiscoveryPolicy.SelectPath(new[] { unknown }, "unknown") == "unknown", "existing manual diagnostic selection lost");
        unknown.Failed = true;
        Require(DeviceDiscoveryPolicy.ContainsPath(new[] { unknown }, "UNKNOWN"), "metadata error treated as device removal");
        Require(DeviceDiscoveryPolicy.SelectPath(new[] { unknown, input, config }, "unknown") == "unknown", "failed but present selection replaced");
        Require(!DeviceDiscoveryPolicy.ContainsPath(new[] { input }, "input"), "path-prefix adopted a different device");
        Require(!DeviceDiscoveryPolicy.ContainsPath(new[] { input }, null), "missing identity matched");
        Require(DeviceDiscoveryPolicy.SelectPath(new DeviceDiscoveryMetadata[0], null) == null, "empty inventory selected a device");
        Require(DeviceDiscoveryPolicy.SameInventory(new[] { input, config }, new[] { config, input }), "enumeration order caused rebuild");
        var same = Input("INPUT-a");
        Require(DeviceDiscoveryPolicy.SameInventory(new[] { input }, new[] { same }), "path case caused rebuild");
        same.Signature = "firmware changed";
        Require(!DeviceDiscoveryPolicy.SameInventory(new[] { input }, new[] { same }), "changed firmware inventory ignored");
        Require(DeviceDiscoveryPolicy.SelectPath(new[] { same, config }, null) == same.Path, "firmware version was mistaken for a support restriction");
        Require(!DeviceDiscoveryPolicy.SameInventory(new[] { input, config }, new[] { input }), "removal ignored");
        Require(!DeviceDiscoveryPolicy.SameInventory(new[] { input }, new[] { input, input }), "duplicate collection ignored");
        foreach (Action<DeviceDiscoveryMetadata> invalidate in new Action<DeviceDiscoveryMetadata>[] {
            d => d.VendorId = 1, d => d.ProductId = 1, d => d.Product = "TK75", d => d.UsagePage = 0xff01,
            d => d.Usage = 2, d => d.InputLength = 64, d => d.HasTravelDecoder = false, d => d.Failed = true })
        {
            var invalid = Input("invalid"); invalidate(invalid);
            Require(DeviceDiscoveryPolicy.SelectPath(new[] { invalid, config }, null) == null, "unsupported input auto-selected");
        }
        var invalidConfig = Config("invalid-config"); invalidConfig.FeatureLength = 64;
        Require(DeviceDiscoveryPolicy.SelectPath(new[] { input, invalidConfig }, null) == null, "unknown config format auto-selected");
        var auto = new KeyboardAutoConnect();
        Require(auto.TryBegin(new[] { input, config }, input.Path, false) == input.Path, "unique supported keyboard did not auto-connect");
        Require(auto.TryBegin(new[] { input, config }, input.Path, false) == null, "unchanged inventory retried a failed automatic connection");
        Require(auto.TryBegin(new DeviceDiscoveryMetadata[0], null, true) == null, "removal attempted connection while draining");
        Require(auto.TryBegin(new[] { input, config }, input.Path.ToUpperInvariant(), false) == input.Path, "replug did not permit one new attempt");
        var waiting = new KeyboardAutoConnect();
        Require(waiting.TryBegin(new[] { input, config }, input.Path, true) == null, "active connection replaced automatically");
        Require(waiting.TryBegin(new[] { input, config }, input.Path, false) == input.Path, "draining connection consumed the next attempt");
        var choose = new KeyboardAutoConnect();
        Require(choose.TryBegin(new[] { input, config, Input("input-B") }, input.Path, false) == null, "ambiguous devices auto-connected");
        Require(choose.TryBegin(new[] { input }, input.Path, false) == null, "missing config auto-connected");
        Require(choose.TryBegin(new[] { input, config }, "manual-other", false) == null, "manual selection replaced automatically");
        Require(choose.TryBegin(new[] { input, config }, input.Path, false) == input.Path, "resolved metadata failed to start");
        var failedInput = Input(input.Path); failedInput.Failed = true;
        Require(choose.TryBegin(new[] { failedInput, config }, input.Path, false) == null, "failed metadata caused attempt");
        Require(choose.TryBegin(new[] { input, config }, input.Path, false) == null, "temporary metadata error cleared attempt guard");
        choose.ObserveRemoval("unrelated-path");
        Require(choose.TryBegin(new[] { input, config }, input.Path, false) == null, "unrelated removal cleared attempt guard");
        choose.ObserveRemoval(input.Path.ToUpperInvariant());
        Require(choose.TryBegin(new[] { input, config }, input.Path, false) == input.Path, "quick replug with same inventory did not reconnect");
        Require(choose.TryBegin(new[] { input, config }, input.Path, false) == null, "quick replug allowed a retry loop");
        return checks;
    }
}
