using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Tk75.Diagnostics;
using Tk75.Mapping;

namespace Tk75.App
{
    // Logical profile inputs are a view over explicitly connected devices. This
    // class never changes the physical reader, owns hardware, scans, or polls.
    // The normal mapping worker supplies its reusable snapshot destination.
    public sealed class LearnedInputRouting : IDisposable
    {
        const double RelativePulseMilliseconds = 40;
        readonly object gate = new object();
        readonly ReaderSession reader;
        readonly string travelDeviceId;
        readonly double scaleMaximum;
        readonly RuntimeRoute[] routes;
        readonly RuntimeRoute[] byKey = new RuntimeRoute[256];
        readonly SourceSubscription[] sources;
        readonly double[] travelValues;
        readonly bool[] travelKnown;
        Action<TravelSample, double>[] sampleHandlers = new Action<TravelSample, double>[0];
        bool disposed, receivedSamples;

        sealed class RuntimeRoute
        {
            internal int KeyIndex, PhysicalKey;
            internal string Label, ControlId;
            internal bool Travel, TravelMatches;
            internal LearnedInputRoute Interpretation;
            internal ILearnedInputDeviceSource Source;
            internal bool RelativeKnown;
            internal double RelativeValue, LastSampleAt;
        }
        sealed class SourceSubscription
        {
            internal ILearnedInputDeviceSource Source;
            internal Action<InputControlSample> Handler;
            internal Dictionary<string, RuntimeRoute[]> Routes;
        }

        public LearnedInputRouting(ReaderSession reader, IEnumerable<LearnedKeyBinding> bindings,
            double scaleMaximum, IEnumerable<ILearnedInputDeviceSource> sources)
        {
            if (!Finite(scaleMaximum) || scaleMaximum <= 0 || scaleMaximum > 65535) throw new ArgumentOutOfRangeException("scaleMaximum");
            this.reader = reader; this.scaleMaximum = scaleMaximum;
            travelDeviceId = reader == null ? null : TravelDeviceId(reader);
            var deviceSources = new Dictionary<string, ILearnedInputDeviceSource>(StringComparer.Ordinal);
            if (sources != null) foreach (var source in sources)
            {
                if (source == null || string.IsNullOrEmpty(source.DeviceId)) throw new ArgumentException("An input source requires an identity.", "sources");
                if (deviceSources.ContainsKey(source.DeviceId)) throw new ArgumentException("Duplicate input device identity.", "sources");
                deviceSources.Add(source.DeviceId, source);
            }
            var compiled = new List<RuntimeRoute>();
            if (bindings != null) foreach (var binding in bindings)
            {
                if (binding == null || (uint)binding.KeyIndex >= 256 || byKey[binding.KeyIndex] != null)
                    throw new ArgumentException("Learned inputs require distinct logical key indices in 0..255.", "bindings");
                var route = new RuntimeRoute { KeyIndex = binding.KeyIndex, Label = binding.SourceName,
                    ControlId = binding.ControlId, Travel = binding.Backend == "travel", PhysicalKey = -1 };
                if (route.Travel)
                {
                    if (!binding.SourceKeyIndex.HasValue || (uint)binding.SourceKeyIndex.Value >= 256 ||
                        binding.ControlId != "key:" + binding.SourceKeyIndex.Value.ToString(CultureInfo.InvariantCulture))
                        throw new ArgumentException("A travel input requires its physical key index.", "bindings");
                    route.PhysicalKey = binding.SourceKeyIndex.Value;
                    route.TravelMatches = string.Equals(binding.SourceDeviceId, travelDeviceId, StringComparison.Ordinal);
                }
                else
                {
                    if (binding.Backend != "hid" && binding.Backend != "keyboard") throw new ArgumentException("Unsupported input backend.", "bindings");
                    route.Interpretation = new LearnedInputRoute(binding.SourceDeviceId, binding.ControlId, (InputControlKind)binding.Kind,
                        binding.Minimum, binding.Maximum, binding.Rest, binding.Active, binding.Direction, binding.HatValue);
                    ILearnedInputDeviceSource source;
                    if (deviceSources.TryGetValue(binding.SourceDeviceId, out source) && MatchesDescriptor(source, binding)) route.Source = source;
                }
                byKey[route.KeyIndex] = route; compiled.Add(route);
            }
            routes = compiled.ToArray(); travelValues = new double[routes.Length]; travelKnown = new bool[routes.Length];
            var subscriptions = new List<SourceSubscription>();
            foreach (var source in deviceSources.Values)
            {
                var controls = new Dictionary<string, List<RuntimeRoute>>(StringComparer.Ordinal);
                foreach (var route in routes) if (object.ReferenceEquals(route.Source, source))
                {
                    List<RuntimeRoute> matches;
                    if (!controls.TryGetValue(route.ControlId, out matches)) { matches = new List<RuntimeRoute>(); controls.Add(route.ControlId, matches); }
                    matches.Add(route);
                }
                if (controls.Count == 0) continue;
                var subscription = new SourceSubscription { Source = source, Routes = new Dictionary<string, RuntimeRoute[]>(StringComparer.Ordinal) };
                foreach (var control in controls) subscription.Routes.Add(control.Key, control.Value.ToArray());
                SourceSubscription captured = subscription;
                subscription.Handler = delegate(InputControlSample sample) { FeedExternal(captured, sample); };
                subscriptions.Add(subscription);
            }
            this.sources = subscriptions.ToArray();
            try
            {
                if (reader != null) reader.Sample += FeedTravel;
                foreach (var source in this.sources) source.Source.Sample += source.Handler;
            }
            catch { Dispose(); throw; }
        }

        static bool MatchesDescriptor(ILearnedInputDeviceSource source, LearnedKeyBinding binding)
        {
            var controls = source.Controls;
            if (controls == null) return false;
            foreach (var control in controls)
                if (control != null && control.ControlId == binding.ControlId && (int)control.Kind == binding.Kind &&
                    control.LogicalMinimum == binding.Minimum && control.LogicalMaximum == binding.Maximum) return true;
            return false;
        }
        static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        static double Now { get { return Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency); } }
        static bool Reading(ILearnedInputDeviceSource source) { return source != null && source.IsReading; }

        public static string TravelDeviceId(ReaderSession reader)
        {
            if (reader == null) throw new ArgumentNullException("reader");
            string identity = "travel\n" + reader.Fingerprint + "\n" + (reader.Device.devicePath ?? "").ToUpperInvariant();
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(identity))).Replace("-", "").ToLowerInvariant();
        }
        public bool IsReading
        {
            get
            {
                lock (gate)
                {
                    if (disposed) return false;
                    if (reader != null && reader.IsReading) return true;
                    foreach (var source in sources) if (Reading(source.Source)) return true;
                    return false;
                }
            }
        }
        public bool HasReceivedSamples { get { lock (gate) { return !disposed && (receivedSamples || reader != null && reader.HasReceivedSamples); } } }
        public event Action<TravelSample, double> Sample
        {
            add
            {
                if (value == null) return;
                lock (gate)
                {
                    if (disposed) throw new ObjectDisposedException("LearnedInputRouting");
                    var next = new Action<TravelSample, double>[sampleHandlers.Length + 1];
                    Array.Copy(sampleHandlers, next, sampleHandlers.Length); next[next.Length - 1] = value; sampleHandlers = next;
                }
            }
            remove
            {
                if (value == null) return;
                lock (gate)
                {
                    int index = Array.LastIndexOf(sampleHandlers, value); if (index < 0) return;
                    var next = new Action<TravelSample, double>[sampleHandlers.Length - 1];
                    Array.Copy(sampleHandlers, 0, next, 0, index); Array.Copy(sampleHandlers, index + 1, next, index, next.Length - index); sampleHandlers = next;
                }
            }
        }
        public int? ResolvePhysicalKey(int logicalKey)
        {
            if ((uint)logicalKey >= 256) throw new ArgumentOutOfRangeException("logicalKey");
            RuntimeRoute route = byKey[logicalKey];
            return route == null ? (int?)logicalKey : route.Travel && route.TravelMatches ? (int?)route.PhysicalKey : null;
        }

        // Capture each original travel value before applying any route. A chain
        // A->B and B->C always reads physical A/B, never the already routed B.
        public void CopyRawSnapshot(double maxAgeMs, Dictionary<int, double> destination)
        {
            if (!Finite(maxAgeMs) || maxAgeMs < 0) throw new ArgumentOutOfRangeException("maxAgeMs");
            if (destination == null) throw new ArgumentNullException("destination");
            lock (gate)
            {
                destination.Clear(); if (disposed) return;
                if (reader != null) reader.CopyRawSnapshot(maxAgeMs, destination);
                for (int i = 0; i < routes.Length; i++)
                    travelKnown[i] = routes[i].Travel && routes[i].TravelMatches && destination.TryGetValue(routes[i].PhysicalKey, out travelValues[i]);
                double now = Now;
                for (int i = 0; i < routes.Length; i++)
                {
                    RuntimeRoute route = routes[i]; double value = 0;
                    if (route.Travel ? travelKnown[i] : TryExternalValue(route, now, out value))
                    {
                        if (route.Travel) value = travelValues[i];
                        destination[route.KeyIndex] = value; receivedSamples = true;
                    }
                    else destination.Remove(route.KeyIndex);
                }
            }
        }
        bool TryExternalValue(RuntimeRoute route, double now, out double value)
        {
            value = 0; if (!Reading(route.Source)) return false;
            if (route.Interpretation.Kind == InputControlKind.Relative)
            {
                if (!route.RelativeKnown)
                {
                    double ignored;
                    return route.Source.TryGetValue(route.ControlId, out ignored) && Reading(route.Source);
                }
                value = now >= route.LastSampleAt && now - route.LastSampleAt < RelativePulseMilliseconds ? route.RelativeValue : 0;
                return true;
            }
            double raw;
            if (!route.Source.TryGetValue(route.ControlId, out raw) || !Finite(raw) || !Reading(route.Source)) return false;
            value = Math.Round(route.Interpretation.Normalize(raw) * scaleMaximum);
            return true;
        }
        public KeyStateSnapshot[] GetUiSnapshot(double maxAgeMs)
        {
            if (!Finite(maxAgeMs) || maxAgeMs < 0) throw new ArgumentOutOfRangeException("maxAgeMs");
            lock (gate)
            {
                if (disposed) return new KeyStateSnapshot[0];
                var physical = reader == null ? new KeyStateSnapshot[0] : reader.GetUiSnapshot(maxAgeMs);
                if (routes.Length == 0) return physical;
                var result = new List<KeyStateSnapshot>(physical.Length + routes.Length);
                foreach (var entry in physical) if (byKey[entry.KeyIndex] == null) result.Add(entry);
                double now = Now;
                foreach (var route in routes)
                {
                    var state = new KeyStateSnapshot { KeyIndex = route.KeyIndex, KeyLabel = route.Label, Stale = true };
                    if (route.Travel && route.TravelMatches)
                    {
                        foreach (var entry in physical) if (entry.KeyIndex == route.PhysicalKey)
                        { state = entry; state.KeyIndex = route.KeyIndex; break; }
                    }
                    else if (!route.Travel)
                    {
                        double value; state.Known = TryExternalValue(route, now, out value); state.Stale = !state.Known;
                        state.RawValue = state.Known ? (int)value : 0;
                        state.AgeMs = route.LastSampleAt <= 0 ? 0 : Math.Max(0, now - route.LastSampleAt);
                    }
                    if (state.Known && !state.Stale) receivedSamples = true;
                    result.Add(state);
                }
                result.Sort(delegate(KeyStateSnapshot a, KeyStateSnapshot b) { return a.KeyIndex.CompareTo(b.KeyIndex); });
                return result.ToArray();
            }
        }
        void FeedTravel(TravelSample sample, double elapsed)
        {
            Action<TravelSample, double>[] handlers;
            lock (gate) { if (disposed) return; receivedSamples = true; handlers = sampleHandlers; }
            if ((uint)sample.KeyIndex >= 256) return;
            if (byKey[sample.KeyIndex] == null) Publish(handlers, sample, elapsed);
            foreach (var route in routes) if (route.Travel && route.TravelMatches && route.PhysicalKey == sample.KeyIndex)
            { var routed = sample; routed.KeyIndex = route.KeyIndex; Publish(handlers, routed, elapsed); }
        }
        void FeedExternal(SourceSubscription subscription, InputControlSample sample)
        {
            RuntimeRoute[] targets;
            if (!subscription.Routes.TryGetValue(sample.ControlId, out targets)) return;
            foreach (var route in targets)
            {
                Action<TravelSample, double>[] handlers; double value;
                lock (gate)
                {
                    if (disposed || !Reading(subscription.Source) || !Finite(sample.Value)) return;
                    route.LastSampleAt = sample.TimestampMilliseconds;
                    value = Math.Round(route.Interpretation.Normalize(sample.Value) * scaleMaximum);
                    if (route.Interpretation.Kind == InputControlKind.Relative)
                    { route.RelativeKnown = true; route.RelativeValue = value; }
                    receivedSamples = true; handlers = sampleHandlers;
                }
                Publish(handlers, new TravelSample { KeyIndex = route.KeyIndex, RawValue = (int)value, KeyLabel = route.Label }, sample.TimestampMilliseconds);
            }
        }
        static void Publish(Action<TravelSample, double>[] handlers, TravelSample sample, double elapsed)
        { foreach (var handler in handlers) try { handler(sample, elapsed); } catch (Exception) { } }
        public void Dispose()
        {
            lock (gate) { if (disposed) return; disposed = true; sampleHandlers = new Action<TravelSample, double>[0]; }
            if (reader != null) reader.Sample -= FeedTravel;
            foreach (var source in sources) source.Source.Sample -= source.Handler;
        }
    }
}
