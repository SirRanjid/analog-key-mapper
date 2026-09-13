using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Tk75.App;
using Tk75.Diagnostics;
using Tk75.Mapping;

// Only the hardware reader is replaced. The production router, source contract,
// immutable interpretation and physical/raw snapshot structures are compiled.
namespace Tk75.Diagnostics { public sealed class CollectionInfo { public string devicePath; } }
namespace Tk75.App
{
    public sealed class ReaderSession
    {
        public readonly CollectionInfo Device = new CollectionInfo { devicePath = "test-keyboard-a" };
        public string Fingerprint = new string('f', 64);
        public bool IsReading = true, HasReceivedSamples;
        public event Action<TravelSample, double> Sample;
        readonly RawLiveView view = new RawLiveView();
        public void Send(int key, int value)
        {
            HasReceivedSamples = true;
            var sample = new TravelSample { KeyIndex = key, RawValue = value, KeyLabel = "Physical " + key };
            view.Publish(sample, 0); var handler = Sample; if (handler != null) handler(sample, 0);
        }
        public KeyStateSnapshot[] GetUiSnapshot(double age)
        {
            var states = view.Snapshot(0);
            for (int i = 0; i < states.Length; i++) states[i].Stale = !IsReading;
            return states;
        }
        internal void CopyRawSnapshot(double age, Dictionary<int, double> result)
        { result.Clear(); if (IsReading) view.CopyRawValues(result, 0, age, true); }
    }
}

public static class LearnedInputRoutingHarness
{
    static int checks;
    const string DeviceA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    static void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
    static void Throws(Action action, string message)
    { bool thrown = false; try { action(); } catch (ArgumentException) { thrown = true; } Check(thrown, message); }
    static long Now { get { return (long)(Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency)); } }
    sealed class Source : ILearnedInputDeviceSource
    {
        readonly object gate = new object();
        readonly Dictionary<string, double> values = new Dictionary<string, double>();
        public string DeviceId { get { return DeviceA; } }
        public string DisplayName { get { return "Synthetic input"; } }
        public bool IsReading { get; set; }
        public InputControlDescriptor[] Controls { get; private set; }
        public string Status { get { return IsReading ? "Reading" : "Removed"; } }
        public bool Disposed;
        public event Action<InputControlSample> Sample;
        public Source(params InputControlDescriptor[] controls) { Controls = controls; IsReading = true; }
        public bool TryGetValue(string controlId, out double value)
        { lock (gate) { value = 0; return IsReading && values.TryGetValue(controlId, out value); } }
        public void Send(string control, double value) { Send(control, value, Now); }
        public void Send(string control, double value, long timestamp)
        {
            lock (gate)
            {
                var descriptor = Controls.First(c => c.ControlId == control);
                values[control] = descriptor.Kind == InputControlKind.Relative ? 0 : value;
            }
            var handler = Sample; if (handler != null) handler(new InputControlSample(control, value, timestamp));
        }
        public void Dispose() { Disposed = true; IsReading = false; }
    }
    static InputControlDescriptor Descriptor(string control, InputControlKind kind, double minimum, double maximum, double? neutral)
    { return new InputControlDescriptor(control, control, kind, minimum, maximum, neutral, null); }
    static LearnedKeyBinding Travel(ReaderSession reader, int target, int physical)
    { return new LearnedKeyBinding { KeyIndex = target, Backend = "travel", SourceDeviceId = LearnedInputRouting.TravelDeviceId(reader), SourceName = "Keyboard", ControlId = "key:" + physical, SourceKeyIndex = physical }; }
    static LearnedKeyBinding External(int target, InputControlDescriptor descriptor, double rest, double active, int direction, double? hat)
    {
        return new LearnedKeyBinding { KeyIndex = target, Backend = "hid", SourceDeviceId = DeviceA, SourceName = "Synthetic input", ControlId = descriptor.ControlId,
            Kind = (int)descriptor.Kind, Minimum = descriptor.LogicalMinimum, Maximum = descriptor.LogicalMaximum, Rest = rest, Active = active, Direction = direction, HatValue = hat };
    }
    static Dictionary<int, double> Snapshot(LearnedInputRouting router)
    { var values = new Dictionary<int, double>(); router.CopyRawSnapshot(500, values); return values; }
    static KeyStateSnapshot State(LearnedInputRouting router, int key) { return router.GetUiSnapshot(500).Single(s => s.KeyIndex == key); }

    static void TravelRoutes()
    {
        var reader = new ReaderSession(); reader.Send(9, 123); reader.Send(14, 17); reader.Send(21, 321);
        using (var direct = new LearnedInputRouting(reader, null, 385, null))
        {
            Check(direct.IsReading && direct.HasReceivedSamples, "Direct reader state is retained.");
            Check(Snapshot(direct)[9] == 123 && direct.ResolvePhysicalKey(9) == 9, "Unlearned keys pass through unchanged.");
            Check(State(direct, 9).KeyLabel == "Physical 9", "Physical diagnostic metadata is retained.");
        }
        var first = Travel(reader, 14, 9); var second = Travel(reader, 9, 21);
        using (var router = new LearnedInputRouting(reader, new[] { first, second }, 385, null))
        {
            Check(Snapshot(router)[14] == 123 && Snapshot(router)[9] == 321, "Travel routes read original inputs without chaining.");
            Check(State(router, 14).RawValue == 123 && State(router, 9).RawValue == 321, "UI and runtime use the same travel routes.");
            Check(router.ResolvePhysicalKey(14) == 9 && router.ResolvePhysicalKey(9) == 21, "Physical source resolution never follows logical chains.");
            first.SourceKeyIndex = 42;
            Check(Snapshot(router)[14] == 123, "Routing is detached from later profile mutations.");
            var events = new List<TravelSample>(); router.Sample += delegate(TravelSample value, double elapsed) { events.Add(value); };
            reader.Send(9, 170);
            Check(events.Count == 1 && events[0].KeyIndex == 14 && events[0].RawValue == 170, "Sample events suppress the replaced target and publish its learned target.");
            reader.Send(21, 0);
            Check(events.Count == 3 && events[1].KeyIndex == 21 && events[2].KeyIndex == 9, "One physical release reaches its ordinary key and remapped target.");
        }
        var missing = Travel(reader, 14, 9); missing.SourceDeviceId = new string('b', 64);
        using (var router = new LearnedInputRouting(reader, new[] { missing }, 385, null))
        {
            Check(!Snapshot(router).ContainsKey(14), "A missing device never exposes the target's old physical value.");
            Check(router.ResolvePhysicalKey(14) == null && !State(router, 14).Known, "A mismatched physical identity is unavailable to lighting and UI.");
        }
        var id = LearnedInputRouting.TravelDeviceId(reader);
        reader.Device.devicePath = reader.Device.devicePath.ToUpperInvariant();
        Check(LearnedInputRouting.TravelDeviceId(reader) == id, "Device identity ignores Windows path casing.");
        reader.Device.devicePath += "-different";
        Check(LearnedInputRouting.TravelDeviceId(reader) != id, "Different physical device paths remain distinct.");
    }

    static void ExternalRoutes()
    {
        var button = Descriptor("button", InputControlKind.Button, 0, 1, 0);
        var analog = Descriptor("axis", InputControlKind.Absolute, 0, 4095, 0);
        var reverse = Descriptor("reverse", InputControlKind.Absolute, 0, 1023, 1023);
        var centered = Descriptor("center", InputControlKind.Absolute, -32768, 32767, 0);
        var hat = Descriptor("hat", InputControlKind.Hat, 0, 7, 8);
        var source = new Source(button, analog, reverse, centered, hat);
        var reader = new ReaderSession(); reader.Send(99, 380);
        var bindings = new[] { External(99, button, 0, 1, 1, null), External(100, analog, 0, 100, 1, null),
            External(101, reverse, 1023, 511, -1, null), External(102, centered, 0, -15000, -1, null), External(103, hat, 8, 3, 1, 3) };
        using (var router = new LearnedInputRouting(reader, bindings, 385, new[] { source }))
        {
            Check(!Snapshot(router).ContainsKey(99) && !State(router, 99).Known, "Unseen external button replaces rather than leaks existing physical pressure.");
            foreach (var binding in bindings) Check(router.ResolvePhysicalKey(binding.KeyIndex) == null, "External input has no physical RGB/suppression index.");
            var events = new List<TravelSample>(); router.Sample += delegate(TravelSample value, double elapsed) { events.Add(value); };
            source.Send("button", 0); source.Send("button", 1);
            Check(Snapshot(router)[99] == 385 && State(router, 99).RawValue == 385, "Binary input projects into the global pressure scale.");
            Check(events.Count == 2 && events[0].RawValue == 0 && events[1].RawValue == 385 && events[1].KeyIndex == 99, "Capture receives the same binary interpretation.");
            source.Send("button", 0);
            Check(Snapshot(router)[99] == 0 && events.Last().RawValue == 0, "Release is forwarded without waiting for the next UI tick.");
            source.Send("axis", 100);
            Check(Snapshot(router)[100] == Math.Round(100.0 / 4095 * 385) && Snapshot(router)[100] < 20, "A shallow learning gesture does not inflate full scale.");
            source.Send("axis", 4095); source.Send("reverse", 0); source.Send("center", -32768); source.Send("hat", 3);
            foreach (int key in new[] { 100, 101, 102, 103 }) Check(Snapshot(router)[key] == 385 && State(router, key).RawValue == 385, "Kinds normalize correctly at their active endpoint.");
            source.Send("reverse", 1023); source.Send("center", 20000); source.Send("hat", 8);
            foreach (int key in new[] { 101, 102, 103 }) Check(Snapshot(router)[key] == 0, "Rest, opposite half axis, and hat neutral are zero.");
            reader.IsReading = false;
            Check(router.IsReading && Snapshot(router)[100] == 385, "Connected external input remains usable without the keyboard reader.");
            source.IsReading = false;
            Check(!router.IsReading && !Snapshot(router).ContainsKey(100) && State(router, 100).Stale, "Disconnected external input never retains a held value.");
        }
        Check(!source.Disposed, "Routing disposal does not own hardware lifetime.");
        source.IsReading = true;
        using (var scaled = new LearnedInputRouting(null, bindings, 1024, new[] { source }))
            Check(Snapshot(scaled)[100] == 1024, "Changing global scale keeps source normalization consistent.");
        var incompatible = new Source(Descriptor("button", InputControlKind.Button, 0, 255, 0));
        using (var router = new LearnedInputRouting(reader, new[] { bindings[0] }, 385, new[] { incompatible }))
            Check(!Snapshot(router).ContainsKey(99), "Changed descriptor ranges invalidate the saved interpretation.");
    }

    static void RelativeRoutes()
    {
        var control = Descriptor("wheel", InputControlKind.Relative, -127, 127, 0);
        var source = new Source(control); source.Send("wheel", 0);
        using (var router = new LearnedInputRouting(null, new[] { External(200, control, 0, 1, 1, null) }, 385, new[] { source }))
        {
            Check(Snapshot(router)[200] == 0, "A connected relative control starts neutral.");
            source.Send("wheel", 1);
            Check(Snapshot(router)[200] == 385 && State(router, 200).RawValue == 385, "Relative direction emits an immediate bounded pulse.");
            source.Send("wheel", 1, Now - 100);
            Check(Snapshot(router)[200] == 0 && State(router, 200).RawValue == 0, "An expired event cannot latch a pressure value.");
            source.Send("wheel", -1);
            Check(Snapshot(router)[200] == 0, "Opposite relative direction does not activate the route.");
            source.IsReading = false;
            Check(!Snapshot(router).ContainsKey(200), "Removing a relative source invalidates even its neutral state.");
        }
    }

    static void LifetimeAndConcurrency()
    {
        var control = Descriptor("button", InputControlKind.Button, 0, 1, 0);
        var source = new Source(control); var reader = new ReaderSession();
        var binding = External(255, control, 0, 1, 1, null);
        Throws(delegate { new LearnedInputRouting(reader, new[] { binding, binding }, 385, new[] { source }); }, "Duplicate targets are rejected.");
        Throws(delegate { new LearnedInputRouting(reader, null, 385, new[] { source, source }); }, "Duplicate device identities are rejected.");
        Throws(delegate { new LearnedInputRouting(reader, null, double.NaN, null); }, "An invalid global scale is rejected.");
        var router = new LearnedInputRouting(reader, new[] { binding }, 385, new[] { source });
        int events = 0;
        router.Sample += delegate { throw new InvalidOperationException("Synthetic subscriber failure"); };
        router.Sample += delegate { Interlocked.Increment(ref events); };
        source.Send("button", 0);
        Check(events == 1, "One failing capture subscriber cannot interrupt other consumers.");
        Exception failure = null;
        var producer = new Thread(delegate() { try { for (int n = 0; n < 1000; n++) source.Send("button", n % 2); } catch (Exception ex) { failure = ex; } });
        producer.Start();
        var reusable = new Dictionary<int, double>();
        for (int n = 0; n < 1000; n++)
        { router.CopyRawSnapshot(500, reusable); Check(!reusable.ContainsKey(255) || reusable[255] == 0 || reusable[255] == 385, "Concurrent snapshots remain complete normalized values."); }
        Check(producer.Join(5000) && failure == null, "Concurrent input events and snapshots complete without deadlock.");
        var detached = router.GetUiSnapshot(500); detached[0].RawValue = -123;
        Check(State(router, 255).RawValue >= 0, "UI snapshots are detached from cached state.");
        router.Dispose(); int completed = events; source.Send("button", 1); reader.Send(1, 4);
        Check(events == completed && !router.IsReading && Snapshot(router).Count == 0 && router.GetUiSnapshot(500).Length == 0, "Disposal removes subscriptions and invalidates snapshots.");
        Check(!source.Disposed && reader.IsReading, "Reader and source remain owned by their connection lifecycle.");
        router.Dispose();
    }
    public static string Run()
    { TravelRoutes(); ExternalRoutes(); RelativeRoutes(); LifetimeAndConcurrency(); return "PASS: " + checks + " learned input routing checks."; }
}
