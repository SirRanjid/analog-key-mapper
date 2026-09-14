using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Tk75.Diagnostics;

namespace Tk75.App
{
    // Read must return a report or null within timeoutMs. Dispose drains owned reads.
    public interface IReportSource : IDisposable
    {
        byte[] Read(int timeoutMs);
    }

    public interface IIdentifiedReportSource
    {
        uint? DeviceModelId { get; }
    }

    public enum EventDeviceState { Unsupported, Waiting, Live, Expired }
    public interface IEventStateReportSource
    {
        EventDeviceState DeviceState { get; }
    }

    sealed class PassiveHidReportSource : IReportSource
    {
        readonly InputReader reader;
        public PassiveHidReportSource(CollectionInfo device) { reader = new InputReader(device); }
        public byte[] Read(int timeoutMs) { return reader.Read(timeoutMs); }
        public void Dispose() { reader.Dispose(); }
    }

    // One explicitly selected device per session. The separate trusted helper owns
    // stream activation. No feature commands or automatic reconnect in this class.
    public sealed class ReaderSession : IDisposable
    {
        readonly object gate = new object();
        readonly ITravelDecoder decoder;
        readonly Func<IReportSource> sourceFactory;
        readonly RawLiveView view = new RawLiveView();
        readonly Stopwatch clock = new Stopwatch();
        Thread worker;
        Exception cleanupFailure;
        volatile bool stopRequested;
        bool disposed, isReading;
        long sampleCount;
        uint? deviceModelId;
        IRgbSnapshotSource rgbSource;
        IEventStateReportSource eventSource;
        bool eventLeaseFailed;
        int faultRaised;
        string status = "Bereit; Lesen nicht gestartet.";
        Action<TravelSample, double>[] sampleHandlers = new Action<TravelSample, double>[0];
        Action<string>[] faultHandlers = new Action<string>[0];

        public CollectionInfo Device { get; private set; }
        public string Fingerprint { get; private set; }
        public string Status { get { lock (gate) { return status; } } }
        public bool IsReading { get { lock (gate) { return isReading; } } }
        internal bool IsStarting { get { lock (gate) { return worker != null && !stopRequested && !isReading; } } }
        internal bool HasFault { get { lock (gate) { return faultRaised != 0; } } }
        public bool HasReceivedSamples { get { lock (gate) { return sampleCount != 0; } } }
        public long SampleCount { get { lock (gate) { return sampleCount; } } }
        public uint? DeviceModelId { get { lock (gate) { return deviceModelId; } } }
        public bool RgbReadAvailable
        {
            get
            {
                IRgbSnapshotSource source;
                lock (gate) { source = !disposed && !stopRequested && isReading ? rgbSource : null; }
                return source != null && source.RgbReadAvailable;
            }
        }
        public Tk75RgbSnapshot ReadRgbSnapshot(int layer, int timeoutMs)
        {
            IRgbSnapshotSource source;
            lock (gate)
            {
                if (disposed || stopRequested || !isReading || rgbSource == null) throw new InvalidOperationException("Die verbundene Tastatur bietet noch keinen RGB-Lesezugriff.");
                source = rgbSource;
            }
            // No ReaderSession lock across pipe/HID waiting. Disposal remains
            // owned by the reading worker and wakes the parent's pending request.
            Tk75RgbSnapshot snapshot = source.ReadRgbSnapshot(layer, timeoutMs);
            lock (gate)
                if (disposed || stopRequested || !isReading || !Object.ReferenceEquals(source, rgbSource)) throw new OperationCanceledException("Die Tastaturverbindung hat sich während der RGB-Sicherung geändert.");
            return snapshot;
        }
        public bool RgbWriteAvailable
        {
            get
            {
                IRgbCompareExchangeSource source;
                lock (gate) { source = !disposed && !stopRequested && isReading ? rgbSource as IRgbCompareExchangeSource : null; }
                return source != null && source.RgbWriteAvailable;
            }
        }
        public Tk75RgbSnapshot CompareExchangeRgb(Tk75RgbSnapshot expected, Tk75RgbSnapshot desired, int timeoutMs)
        { return CompareExchangeRgb(expected, desired, null, timeoutMs); }
        public bool RgbAutomaticRestoreAvailable
        {
            get
            {
                IRgbAutomaticRestoreSource source;
                lock (gate) source = !disposed && !stopRequested && isReading ? rgbSource as IRgbAutomaticRestoreSource : null;
                return source != null && source.RgbAutomaticRestoreAvailable;
            }
        }
        public Tk75RgbSnapshot CompareExchangeRgb(Tk75RgbSnapshot expected, Tk75RgbSnapshot desired, Tk75RgbSnapshot original, int timeoutMs)
        {
            IRgbCompareExchangeSource source;
            lock (gate)
            {
                source = rgbSource as IRgbCompareExchangeSource;
                if (disposed || stopRequested || !isReading || source == null) throw new InvalidOperationException("Die verbundene Tastatur bietet noch keinen RGB-Schreibzugriff.");
            }
            IRgbAutomaticRestoreSource automatic = source as IRgbAutomaticRestoreSource;
            Tk75RgbSnapshot snapshot = original != null && automatic != null && automatic.RgbAutomaticRestoreAvailable
                ? automatic.CompareExchangeRgb(expected, desired, original, timeoutMs)
                : source.CompareExchangeRgb(expected, desired, timeoutMs);
            lock (gate)
                if (disposed || stopRequested || !isReading || !Object.ReferenceEquals(source, rgbSource)) throw new OperationCanceledException("Die Tastaturverbindung hat sich während der RGB-Änderung geändert; Sicherung behalten.");
            return snapshot;
        }

        // Immutable handler arrays are copied only on subscription, never per sample.
        static T[] Append<T>(T[] source, T value) where T : class
        { T[] copy = new T[source.Length + 1]; Array.Copy(source, copy, source.Length); copy[source.Length] = value; return copy; }
        static T[] Remove<T>(T[] source, T value) where T : class
        {
            int index = Array.LastIndexOf(source, value);
            if (index < 0) return source;
            T[] copy = new T[source.Length - 1];
            Array.Copy(source, 0, copy, 0, index); Array.Copy(source, index + 1, copy, index, source.Length - index - 1);
            return copy;
        }
        public event Action<TravelSample, double> Sample
        {
            add { if (value != null) lock (gate) { sampleHandlers = Append(sampleHandlers, value); } }
            remove { lock (gate) { sampleHandlers = Remove(sampleHandlers, value); } }
        }
        public event Action<string> Fault
        {
            add { if (value != null) lock (gate) { faultHandlers = Append(faultHandlers, value); } }
            remove { lock (gate) { faultHandlers = Remove(faultHandlers, value); } }
        }

        public ReaderSession(CollectionInfo device) : this(device, TravelProtocols.Find(device), null) { }

        // Dependency injection is for offline tests; production uses a signed helper.
        public ReaderSession(CollectionInfo device, ITravelDecoder decoder, Func<IReportSource> sourceFactory)
        {
            if (device == null) throw new ArgumentNullException("device");
            if (decoder == null || !decoder.Matches(device)) throw new NotSupportedException("Kein eindeutiger Decoder fuer diese Input-Collection.");
            Device = device;
            this.decoder = decoder;
            this.sourceFactory = sourceFactory ?? delegate { return new ManagedMonitorSource(device, delegate { return stopRequested; }); };
            Fingerprint = ProtocolFingerprint.Calculate(device, decoder.Id);
        }

        public void Start()
        {
            try
            {
                lock (gate)
                {
                    if (disposed) throw new ObjectDisposedException("ReaderSession");
                    if (worker != null) return;
                    stopRequested = false; faultRaised = 0; cleanupFailure = null; isReading = false; sampleCount = 0; deviceModelId = null; rgbSource = null; eventSource = null; eventLeaseFailed = false;
                    view.Invalidate("Neue Lesesitzung; alte Werte sind unbekannt.");
                    clock.Restart(); status = "Tastatur-Zugriff wird geprüft und gestartet …";
                    worker = new Thread(ReadLoop) { IsBackground = true, Name = "TK75 managed input" };
                    try { worker.Start(); }
                    catch { worker = null; clock.Stop(); throw; }
                }
            }
            catch (ObjectDisposedException) { throw; }
            catch (Exception ex) { Fail("Lesen konnte nicht gestartet werden: " + ex.Message); }
        }

        void Fail(string message)
        {
            Action<string>[] handlers;
            lock (gate)
            {
                stopRequested = true; isReading = false;
                if (faultRaised != 0) return;
                faultRaised = 1; status = message; view.Invalidate(message);
                handlers = faultHandlers;
            }
            foreach (Action<string> handler in handlers)
                try { handler(message); } catch (Exception) { }
        }

        void ReadLoop()
        {
            IReportSource source = null;
            try
            {
                source = sourceFactory();
                if (source == null) throw new InvalidOperationException("Die Inputquelle fehlt.");
                lock (gate)
                {
                    if (stopRequested) return;
                    var identified = source as IIdentifiedReportSource;
                    deviceModelId = identified == null ? null : identified.DeviceModelId;
                    rgbSource = source as IRgbSnapshotSource;
                    eventSource = source as IEventStateReportSource;
                    isReading = true; status = "Tastatur bereit. Drücke eine Taste – noch keine Druckwerte empfangen.";
                }
                while (!stopRequested)
                {
                    byte[] report = source.Read(100);
                    if (stopRequested) break;
                    if (report == null) continue;
                    TravelSample sample; string error;
                    if (!decoder.TryParse(report, out sample, out error)) throw new InvalidDataException(error ?? "Unbekanntes Inputformat.");
                    Action<TravelSample, double>[] handlers;
                    double elapsed;
                    lock (gate)
                    {
                        if (stopRequested) break;
                        elapsed = clock.Elapsed.TotalMilliseconds;
                        view.Publish(sample, elapsed);
                        sampleCount++;
                        if (sampleCount == 1) status = "Druckwerte werden empfangen.";
                        handlers = sampleHandlers;
                    }
                    foreach (Action<TravelSample, double> handler in handlers)
                        try { handler(sample, elapsed); } catch (Exception) { }
                }
            }
            catch (Exception ex)
            {
                // Source construction can itself have failed while draining an
                // already launched helper. Preserve that cleanup failure too.
                if (ex is MonitorCleanupException) lock (gate) cleanupFailure = ex;
                if (!stopRequested || ex is MonitorCleanupException) Fail("Inputfehler: " + ex.Message);
            }
            finally
            {
                lock (gate) { rgbSource = null; eventSource = null; }
                if (source != null)
                    try { source.Dispose(); }
                    catch (Exception ex)
                    {
                        lock (gate) cleanupFailure = ex;
                        Fail("Inputquelle konnte nicht sauber geschlossen werden: " + ex.Message);
                    }
                lock (gate)
                {
                    isReading = false;
                    if (faultRaised == 0) status = "Lesen angehalten.";
                    view.Invalidate(status); clock.Stop(); worker = null;
                }
            }
        }

        public KeyStateSnapshot[] GetSnapshot()
        { lock (gate) { return view.Snapshot(clock.Elapsed.TotalMilliseconds); } }

        // Main-window display uses the same freshness policy as mapping. An
        // event lease can keep an unchanged held key valid without a new report;
        // AgeMs and the separate diagnostic snapshot still retain its real age.
        internal KeyStateSnapshot[] GetUiSnapshot(double maxAgeMs)
        {
            if (double.IsNaN(maxAgeMs) || double.IsInfinity(maxAgeMs) || maxAgeMs < 0) throw new ArgumentOutOfRangeException("maxAgeMs");
            lock (gate)
            {
                var snapshot = view.Snapshot(clock.Elapsed.TotalMilliseconds);
                bool usable = isReading && !stopRequested;
                EventDeviceState deviceState = usable && eventSource != null ? eventSource.DeviceState : EventDeviceState.Unsupported;
                if (deviceState == EventDeviceState.Expired) eventLeaseFailed = true;
                usable = usable && !eventLeaseFailed && deviceState != EventDeviceState.Waiting;
                bool retainKnown = deviceState == EventDeviceState.Live;
                for (int i = 0; i < snapshot.Length; i++)
                    snapshot[i].Stale = !usable || !snapshot[i].Known ||
                        !(retainKnown || snapshot[i].RawValue == 0 || snapshot[i].AgeMs <= maxAgeMs);
                return snapshot;
            }
        }

        // Legacy/passive sources retain the per-key TTL. An explicitly verified
        // event source retains only known states while its device lease is live;
        // an unchanged held key is not a missing input report.
        public Dictionary<int, double> GetRawSnapshot(double maxAgeMs)
        {
            var result = new Dictionary<int, double>();
            CopyRawSnapshot(maxAgeMs, result);
            return result;
        }

        // The mapping worker owns this buffer and its lock. Public snapshots
        // remain detached; no reader-owned collection escapes to a consumer.
        internal void CopyRawSnapshot(double maxAgeMs, Dictionary<int, double> destination)
        {
            if (double.IsNaN(maxAgeMs) || double.IsInfinity(maxAgeMs) || maxAgeMs < 0) throw new ArgumentOutOfRangeException("maxAgeMs");
            if (destination == null) throw new ArgumentNullException("destination");
            lock (gate)
            {
                destination.Clear();
                if (!isReading || stopRequested) return;
                EventDeviceState deviceState = eventSource == null ? EventDeviceState.Unsupported : eventSource.DeviceState;
                if (deviceState == EventDeviceState.Expired) eventLeaseFailed = true;
                if (eventLeaseFailed || deviceState == EventDeviceState.Waiting) return;
                bool retainKnown = deviceState == EventDeviceState.Live;
                view.CopyRawValues(destination, clock.Elapsed.TotalMilliseconds, maxAgeMs, retainKnown);
            }
        }

        public void Stop()
        {
            Thread active;
            lock (gate)
            {
                stopRequested = true; isReading = false;
                view.Invalidate("Lesen angehalten; alte Werte sind unbekannt.");
                if (faultRaised == 0) status = "Lesen wird angehalten.";
                active = worker;
                if (active == null) { clock.Stop(); if (faultRaised == 0) status = "Lesen angehalten."; }
            }
            // Ordinary reads wake in <=100ms. A misbehaving source/subscriber must not
            // freeze the UI; the worker still owns and eventually drains its resources.
            if (active != null && active != Thread.CurrentThread && !active.Join(250))
                lock (gate) { if (worker == active && faultRaised == 0) status = "Anhalten ausstehend; Eingabewerte sind unbekannt."; }
        }

        public void Dispose()
        {
            lock (gate) { disposed = true; }
            Stop();
        }

        // Stop/Dispose intentionally return promptly for interactive callers.
        // Session-end and reconnect owners must also wait for source.Dispose:
        // the HID helper can still be restoring lighting after Stop's 250ms join.
        internal void DisposeAndWait(int timeoutMs)
        {
            if (timeoutMs < 0) throw new ArgumentOutOfRangeException("timeoutMs");
            var elapsed = Stopwatch.StartNew();
            Dispose();
            Thread active;
            lock (gate) active = worker;
            if (active != null && (active == Thread.CurrentThread ||
                !active.Join(Math.Max(0, timeoutMs - (int)elapsed.ElapsedMilliseconds))))
                throw new TimeoutException("The keyboard reader/helper cleanup has not finished; lighting recovery remains unconfirmed.");
            Exception failure;
            lock (gate) failure = cleanupFailure;
            if (failure != null) throw new IOException("The keyboard reader/helper did not close cleanly.", failure);
        }
    }
}
