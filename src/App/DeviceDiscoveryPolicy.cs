using System;
using System.Collections.Generic;
using System.Linq;

namespace Tk75.App
{
    // Pure inventory decisions. No device, helper, UI or native calls.
    public sealed class DeviceDiscoveryMetadata
    {
        public string Path, Product, Signature;
        public int VendorId, ProductId, UsagePage, Usage, InputLength, FeatureLength;
        public bool Failed, HasTravelDecoder;
        public bool Visible { get { return !Failed && UsagePage >= 0xff00 && UsagePage <= 0xffff && InputLength > 0 && InputLength <= 65536; } }
        bool KnownModel { get { return !Failed && VendorId == 0x3151 && ProductId == 0x5030 && string.Equals(Product, "TK75 TMR", StringComparison.Ordinal); } }
        public bool KnownInput { get { return KnownModel && UsagePage == 0xffff && Usage == 1 && InputLength == 32 && HasTravelDecoder; } }
        public bool KnownConfiguration { get { return KnownModel && UsagePage == 0xffff && Usage == 2 && FeatureLength == 65; } }
    }

    public static class DeviceDiscoveryPolicy
    {
        public static bool ContainsPath(IEnumerable<DeviceDiscoveryMetadata> inventory, string path)
        { return path != null && inventory.Any(d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase)); }

        public static string SelectPath(IEnumerable<DeviceDiscoveryMetadata> inventory, string remembered)
        {
            var all = inventory.ToArray();
            // Even failed metadata still proves a collection path is present.
            if (ContainsPath(all, remembered)) return remembered;
            var inputs = all.Where(d => d.KnownInput).ToArray();
            // The current monitor requires one input and one configuration
            // globally; metadata lacks a proven per-physical-device pairing ID.
            return inputs.Length == 1 && all.Count(d => d.KnownConfiguration) == 1 ? inputs[0].Path : null;
        }

        public static bool SameInventory(IEnumerable<DeviceDiscoveryMetadata> first, IEnumerable<DeviceDiscoveryMetadata> second)
        {
            return InventoryKeys(first).SequenceEqual(InventoryKeys(second), StringComparer.Ordinal);
        }
        static string[] InventoryKeys(IEnumerable<DeviceDiscoveryMetadata> inventory)
        {
            return inventory.Select(d => (d.Path ?? "").ToUpperInvariant() + "\n" + (d.Signature ?? "") + "\n" +
                d.VendorId + ":" + d.ProductId + ":" + d.UsagePage + ":" + d.Usage + ":" + d.InputLength + ":" + d.FeatureLength + ":" +
                d.Failed + ":" + d.HasTravelDecoder + ":" + (d.Product ?? "")).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        }
    }

    // One automatic attempt per physical presence. Repeated inventory events
    // cannot create a reconnect loop after a helper or device error.
    public sealed class KeyboardAutoConnect
    {
        readonly HashSet<string> attempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public void ObserveRemoval(string path)
        { if (path != null) attempted.Remove(path); }
        public string TryBegin(IEnumerable<DeviceDiscoveryMetadata> inventory, string selected, bool connectionPresent)
        {
            var all = inventory.ToArray();
            attempted.RemoveWhere(path => !DeviceDiscoveryPolicy.ContainsPath(all, path));
            if (connectionPresent) return null;
            string candidate = DeviceDiscoveryPolicy.SelectPath(all, null);
            if (candidate == null || !string.Equals(candidate, selected, StringComparison.OrdinalIgnoreCase)) return null;
            return attempted.Add(candidate) ? candidate : null;
        }
    }
}
