using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Tk75.Diagnostics;

namespace Tk75.App
{
    public sealed class MonitorCleanupException : IOException
    {
        public MonitorCleanupException(string message) : base(message) { }
        public MonitorCleanupException(string message, Exception inner) : base(message, inner) { }
    }
    // Parent-side transport only. All HID feature calls remain in the separately
    // bundled helper. An explicit local build can accept an unsigned helper;
    // rejected signatures and Windows process-start refusals have no fallback.
    public sealed class ManagedMonitorSource : IReportSource, IIdentifiedReportSource, IRgbCompareExchangeSource, IEventStateReportSource
    {
#if ALLOW_UNSIGNED_MONITOR
        const bool AllowUnsignedMonitor = true;
#else
        const bool AllowUnsignedMonitor = false;
#endif
        readonly object gate = new object();
        readonly Queue<byte[]> pending = new Queue<byte[]>();
        readonly string devicePath;
        Process process;
        Thread outputReader, heartbeat;
        bool ready, stopping, stopped, disposed;
        string failure;
        uint? modelId;
        sealed class RgbReadOperation
        {
            public int Id, Layer;
            public bool Sent, Complete;
            public Tk75RgbSnapshot Snapshot;
            public Tk75RgbSnapshot Expected, Desired;
            public string Error;
        }
        RgbReadOperation rgbRead;
        int rgbSequence;
        bool rgbReadSupported;
        bool rgbWriteSupported;
        bool eventStateSupported;
        readonly Stopwatch evidenceClock = Stopwatch.StartNew();
        readonly MonitorDeviceLease deviceLease = new MonitorDeviceLease();
        public EventDeviceState DeviceState
        {
            get
            {
                lock (gate)
                {
                    if (!eventStateSupported) return EventDeviceState.Unsupported;
                    if (failure != null || stopping || stopped || disposed) return EventDeviceState.Expired;
                    int value = deviceLease.Status(evidenceClock.ElapsedMilliseconds);
                    return value == 1 ? EventDeviceState.Live : value == 2 ? EventDeviceState.Expired : EventDeviceState.Waiting;
                }
            }
        }
        public uint? DeviceModelId { get { lock (gate) { return modelId; } } }
        public bool RgbReadAvailable { get { lock (gate) { return ready && rgbReadSupported && !stopping && !disposed && failure == null; } } }
        public bool RgbWriteAvailable { get { lock (gate) { return ready && rgbReadSupported && rgbWriteSupported && !stopping && !disposed && failure == null; } } }

        public ManagedMonitorSource(CollectionInfo device) : this(device, delegate { return false; }) { }
        public ManagedMonitorSource(CollectionInfo device, Func<bool> cancelled)
        {
            if (device == null || String.IsNullOrWhiteSpace(device.devicePath)) throw new ArgumentException("Die ausgewählte Tastatur fehlt.");
            if (cancelled == null) throw new ArgumentNullException("cancelled");
            devicePath = device.devicePath;
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tk75Monitor.exe");
            try
            {
                using (SignedMonitorAccess.AcquireFile(path, AllowUnsignedMonitor))
                {
                    if (cancelled()) throw new OperationCanceledException("Verbindung abgebrochen.");
                    process = new Process { StartInfo = new ProcessStartInfo(path, "--stream-host") {
                        UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                        WorkingDirectory = Path.GetDirectoryName(path)
                    } };
                    // Windows application control still makes the final decision.
                    if (!process.Start()) throw new IOException("Windows hat den Tastatur-Zugriff nicht gestartet.");
                }
                process.ErrorDataReceived += delegate { /* Drain diagnostics; no raw input is logged. */ };
                process.BeginErrorReadLine();
                outputReader = new Thread(ReadOutput) { IsBackground = true, Name = "Keyboard monitor reports" };
                heartbeat = new Thread(SendCommands) { IsBackground = true, Name = "Keyboard monitor heartbeat" };
                outputReader.Start(); heartbeat.Start();
                Stopwatch deadline = Stopwatch.StartNew();
                lock (gate)
                {
                    while (!ready && failure == null && deadline.ElapsedMilliseconds < 6000 && !cancelled()) Monitor.Wait(gate, 100);
                    if (cancelled()) throw new OperationCanceledException("Verbindung abgebrochen.");
                    if (failure != null) throw new IOException(failure);
                    if (!ready) throw new IOException("Die Tastatur hat den Zugriff nicht rechtzeitig bestätigt.");
                }
            }
            catch (Exception openingError)
            {
                try { Dispose(); }
                catch (Exception cleanupError) { throw new MonitorCleanupException(openingError.Message + " " + cleanupError.Message, openingError); }
                throw;
            }
        }

        void Fail(string message)
        {
            lock (gate) { if (failure == null) failure = message; pending.Clear(); Monitor.PulseAll(gate); }
        }

        void SendCommands()
        {
            try
            {
                process.StandardInput.WriteLine("START " + Convert.ToBase64String(Encoding.UTF8.GetBytes(devicePath)));
                process.StandardInput.Flush();
                while (true)
                {
                    Stopwatch interval = Stopwatch.StartNew();
                    string rgbCommand = null;
                    lock (gate)
                    {
                        while (!stopping && failure == null && !(rgbRead != null && !rgbRead.Sent) && interval.ElapsedMilliseconds < 250)
                            Monitor.Wait(gate, Math.Max(1, 250 - (int)interval.ElapsedMilliseconds));
                        if (stopping || failure != null) break;
                        if (rgbRead != null && !rgbRead.Sent)
                        {
                            rgbRead.Sent = true;
                            rgbCommand = rgbRead.Expected == null
                                ? "RGBREAD " + rgbRead.Id.ToString(CultureInfo.InvariantCulture) + " " + rgbRead.Layer.ToString(CultureInfo.InvariantCulture)
                                : "RGBWRITE " + rgbRead.Id.ToString(CultureInfo.InvariantCulture) + " " + Convert.ToBase64String(Tk75RgbProtocol.EncodeSnapshot(rgbRead.Expected)) + " " + Convert.ToBase64String(Tk75RgbProtocol.EncodeSnapshot(rgbRead.Desired));
                        }
                    }
                    // Keep one writer for heartbeat, RGB requests and STOP. RGB
                    // traffic does not replace or extend the heartbeat contract.
                    process.StandardInput.WriteLine("PING");
                    if (rgbCommand != null) process.StandardInput.WriteLine(rgbCommand);
                    process.StandardInput.Flush();
                }
                process.StandardInput.WriteLine("STOP"); process.StandardInput.Flush();
            }
            catch (Exception ex) { lock (gate) { if (!stopping) Fail("Tastatur-Verbindung unterbrochen: " + ex.Message); } }
            finally { try { process.StandardInput.Close(); } catch (Exception) { } }
        }

        void ReadOutput()
        {
            try
            {
                string line;
                while ((line = MonitorWire.ReadLine(process.StandardOutput, 4096)) != null)
                {
                    lock (gate)
                    {
                        if (line == "STOPPED")
                        {
                            // Cancellation may follow ON before the queued READY is
                            // delivered. A successful OFF is still a valid cleanup.
                            if (!ready && !stopping) throw new InvalidDataException("Tastatur-Zugriff wurde vorzeitig beendet.");
                            stopped = true;
                            if (!stopping) Fail("Die Tastatur-Verbindung wurde beendet.");
                            Monitor.PulseAll(gate); continue;
                        }
                        if (line.StartsWith("ERROR ", StringComparison.Ordinal)) { Fail(MonitorWire.DecodeError(line)); continue; }
                        if (failure != null || stopped) throw new InvalidDataException("Unerwartete Daten nach dem Verbindungsende.");
                        if (line.StartsWith("READY ", StringComparison.Ordinal))
                        {
                            if (ready) throw new InvalidDataException("Doppelte Tastatur-Identifikation.");
                            modelId = MonitorWire.DecodeReady(line); ready = true; Monitor.PulseAll(gate); continue;
                        }
                        if (!ready) throw new InvalidDataException("Druckwerte ohne bestätigte Tastatur-Identifikation.");
                        if (line == "CAPS EVENTSTATE1")
                        {
                            if (eventStateSupported) throw new InvalidDataException("Doppelte Geräte-Frischebestätigung.");
                            eventStateSupported = true; deviceLease.Start(evidenceClock.ElapsedMilliseconds); Monitor.PulseAll(gate); continue;
                        }
                        if (line.StartsWith("LIVE ", StringComparison.Ordinal))
                        {
                            if (!eventStateSupported) throw new InvalidDataException("Geräte-Frische ohne bestätigtes Protokoll.");
                            deviceLease.AcceptProof(line, evidenceClock.ElapsedMilliseconds); Monitor.PulseAll(gate); continue;
                        }
                        if (line == "CAPS RGBREAD1")
                        {
                            if (rgbReadSupported) throw new InvalidDataException("Doppelte RGB-Funktionsbestätigung.");
                            rgbReadSupported = true; Monitor.PulseAll(gate); continue;
                        }
                        if (line == "CAPS RGBWRITE1")
                        {
                            if (!rgbReadSupported || rgbWriteSupported) throw new InvalidDataException("Unbekannte RGB-Funktionsreihenfolge.");
                            rgbWriteSupported = true; Monitor.PulseAll(gate); continue;
                        }
                        if (line.StartsWith("RGBSNAPSHOT ", StringComparison.Ordinal) || line.StartsWith("RGBERROR ", StringComparison.Ordinal))
                        {
                            string[] parts = line.Split(' ');
                            if (parts.Length != 3 || rgbRead == null || !rgbRead.Sent || rgbRead.Complete ||
                                parts[1] != rgbRead.Id.ToString(CultureInfo.InvariantCulture)) throw new InvalidDataException("RGB-Antwort ohne passende Leseanfrage.");
                            if (parts[0] == "RGBSNAPSHOT")
                            {
                                byte[] bytes;
                                try { bytes = Convert.FromBase64String(parts[2]); }
                                catch (FormatException) { throw new InvalidDataException("Ungültige RGB-Sicherung."); }
                                if (Convert.ToBase64String(bytes) != parts[2]) throw new InvalidDataException("Uneindeutige RGB-Sicherung.");
                                Tk75RgbSnapshot snapshot = Tk75RgbProtocol.DecodeSnapshot(bytes);
                                if (snapshot.ModelId != modelId || snapshot.Layer != rgbRead.Layer) throw new InvalidDataException("RGB-Sicherung gehört zu einer anderen Tastatur oder Lichtebene.");
                                if (rgbRead.Desired != null && !Tk75RgbExchange.Equivalent(snapshot, rgbRead.Desired)) throw new InvalidDataException("RGB-Änderung wurde nicht im erwarteten Zustand bestätigt; Sicherung behalten.");
                                rgbRead.Snapshot = snapshot;
                            }
                            else rgbRead.Error = MonitorWire.DecodeError("ERROR " + parts[2]);
                            rgbRead.Complete = true; Monitor.PulseAll(gate); continue;
                        }
                        byte[] report = MonitorWire.DecodeData(line);
                        if (eventStateSupported)
                        {
                            TravelSample sample; string error;
                            if (!Tk75TravelReport.TryParse(report, out sample, out error)) throw new InvalidDataException(error);
                            deviceLease.AcceptValidData(evidenceClock.ElapsedMilliseconds);
                        }
                        if (!stopping)
                        {
                            if (pending.Count >= 2048) throw new IOException("Druckwerte können nicht schnell genug verarbeitet werden. Bitte neu verbinden.");
                            pending.Enqueue(report); Monitor.PulseAll(gate);
                        }
                    }
                }
                lock (gate) { if (!stopping || !stopped) Fail("Tastatur-Zugriff beendet; Ausschalten der Messung wurde nicht bestätigt."); }
            }
            catch (Exception ex) { Fail("Tastatur-Zugriff: " + ex.Message); }
        }

        public byte[] Read(int timeoutMs)
        {
            if (timeoutMs < 0) throw new ArgumentOutOfRangeException("timeoutMs");
            Stopwatch limit = Stopwatch.StartNew();
            lock (gate)
            {
                while (pending.Count == 0 && failure == null && !stopping && limit.ElapsedMilliseconds < timeoutMs)
                {
                    if (eventStateSupported && deviceLease.Status(evidenceClock.ElapsedMilliseconds) == 2) { Fail("Die Tastatur antwortet nicht mehr rechtzeitig. Bitte neu verbinden."); break; }
                    Monitor.Wait(gate, Math.Max(1, timeoutMs - (int)limit.ElapsedMilliseconds));
                }
                if (eventStateSupported && deviceLease.Status(evidenceClock.ElapsedMilliseconds) == 2) Fail("Die Tastatur antwortet nicht mehr rechtzeitig. Bitte neu verbinden.");
                if (failure != null) throw new IOException(failure);
                if (stopping) return null;
                return pending.Count == 0 ? null : pending.Dequeue();
            }
        }

        // Call outside the UI thread and without holding ReaderSession/runtime
        // locks. A timeout abandons only this wait; its reply stays correlated and
        // another request cannot overtake an operation still owned by the helper.
        public Tk75RgbSnapshot ReadRgbSnapshot(int layer, int timeoutMs)
        { return RequestRgb(layer, timeoutMs, null, null); }
        public Tk75RgbSnapshot CompareExchangeRgb(Tk75RgbSnapshot expected, Tk75RgbSnapshot desired, int timeoutMs)
        {
            Tk75RgbExchange.Validate(expected, desired);
            return RequestRgb(expected.Layer, timeoutMs, expected, desired);
        }
        Tk75RgbSnapshot RequestRgb(int layer, int timeoutMs, Tk75RgbSnapshot expected, Tk75RgbSnapshot desired)
        {
            if (layer < 0 || layer > 4) throw new ArgumentOutOfRangeException("layer");
            if (timeoutMs < 1 || timeoutMs > 15000) throw new ArgumentOutOfRangeException("timeoutMs");
            lock (gate)
            {
                if (!ready || stopping || disposed) throw new InvalidOperationException("Die Tastatur ist nicht verbunden.");
                if (!rgbReadSupported) throw new InvalidOperationException("Der laufende Tastatur-Helfer unterstützt den RGB-Lesezugriff noch nicht.");
                if (expected != null && (!rgbWriteSupported || expected.ModelId != modelId)) throw new InvalidOperationException("Der laufende Tastatur-Helfer unterstützt diese RGB-Änderung noch nicht.");
                if (failure != null) throw new IOException(failure);
                if (rgbRead != null && !rgbRead.Complete) throw new InvalidOperationException("Die Beleuchtung wird bereits gelesen.");
                if (rgbSequence == Int32.MaxValue) throw new InvalidOperationException("Bitte die Tastatur neu verbinden.");
                RgbReadOperation operation = new RgbReadOperation { Id = ++rgbSequence, Layer = layer, Expected = expected, Desired = desired };
                rgbRead = operation; Monitor.PulseAll(gate); Stopwatch deadline = Stopwatch.StartNew();
                while (!operation.Complete && failure == null && !stopping && deadline.ElapsedMilliseconds < timeoutMs)
                    Monitor.Wait(gate, Math.Max(1, timeoutMs - (int)deadline.ElapsedMilliseconds));
                if (failure != null) throw new IOException(failure);
                if (stopping || disposed) throw new OperationCanceledException("Lesen der Beleuchtung abgebrochen.");
                if (!operation.Complete) throw new TimeoutException("Die Beleuchtung konnte nicht rechtzeitig vollständig gelesen werden.");
                if (operation.Error != null) throw new IOException(operation.Error);
                return operation.Snapshot;
            }
        }

        public void Dispose()
        {
            lock (gate) { if (disposed) return; disposed = true; stopping = true; pending.Clear(); Monitor.PulseAll(gate); }
            bool launched = false, confirmed, terminated = false;
            string finalFailure; int? exitCode = null;
            if (process != null)
            {
                try { int ownedProcessId = process.Id; launched = ownedProcessId > 0; } catch (InvalidOperationException) { }
                if (launched)
                {
                    // Give the helper time to send OFF and exit. Only our own child
                    // may be terminated if a synchronous Windows HID call hangs.
                    if (!process.WaitForExit(1500)) { terminated = true; try { process.Kill(); } catch (InvalidOperationException) { } process.WaitForExit(500); }
                    if (outputReader != null) outputReader.Join(300);
                    if (heartbeat != null) heartbeat.Join(300);
                    if (process.HasExited) exitCode = process.ExitCode;
                }
                process.Dispose();
            }
            lock (gate) { confirmed = stopped; finalFailure = failure; }
            if (launched && !confirmed) throw new MonitorCleanupException("Zugriff getrennt; die Übertragung des Ausschaltbefehls konnte nicht bestätigt werden.");
            if (terminated) throw new MonitorCleanupException("Ausschaltbefehl übertragen; Windows hat den Tastatur-Lesezugriff anschließend nicht rechtzeitig freigegeben.");
            if (launched && (finalFailure != null || exitCode != 0)) throw new MonitorCleanupException(finalFailure ?? "Der Tastatur-Zugriff konnte nicht fehlerfrei beendet werden.");
        }
    }

}
