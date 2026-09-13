using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Tk75.Diagnostics
{
    // Runs only as the separate, normally launched monitor executable. The main
    // application must authenticate this helper before normal Process.Start.
    // Never load this type through an alternate host to evade application control.
    internal static class MonitorStreamHost
    {
        // A blocked parent stdout reader must not stop the HID-owning thread from
        // reaching OFF. Only this background worker can block in a pipe write.
        sealed class OutputQueue
        {
            readonly object gate = new object();
            readonly Queue<string> pending = new Queue<string>();
            readonly AutoResetEvent ready = new AutoResetEvent(false);
            readonly Thread worker;
            bool cleaningUp, finishing;
            string error;
            public OutputQueue() { worker = new Thread(WriteLoop); worker.IsBackground = true; worker.Start(); }
            public string Error { get { lock (gate) return error; } }
            public void Send(string line)
            {
                lock (gate)
                {
                    if (error != null) throw new IOException(error);
                    if (cleaningUp || finishing || pending.Count >= 256) throw new IOException("Das Hauptprogramm liest die Druckwerte nicht rechtzeitig.");
                    pending.Enqueue(line);
                    ready.Set();
                }
            }
            public void BeginCleanup(string failure, bool offSucceeded)
            {
                lock (gate)
                {
                    // Send the actual OFF result before the potentially blocking
                    // interrupt-IN drain. STOPPED confirms OFF, not process exit.
                    cleaningUp = true;
                    pending.Clear();
                    if (failure != null) pending.Enqueue(MonitorHostProtocol.ErrorLine(failure));
                    if (offSucceeded) pending.Enqueue("STOPPED");
                    ready.Set();
                }
            }
            public void Finish(string cleanupFailure)
            {
                lock (gate)
                {
                    // Preserve queued OFF/error status. A later reader cleanup
                    // failure must still be reported, even after STOPPED was sent.
                    if (cleanupFailure != null) pending.Enqueue(MonitorHostProtocol.ErrorLine(cleanupFailure));
                    finishing = true;
                    ready.Set();
                }
                // A permanently blocked pipe writer is a background thread; the
                // helper may still exit after it has attempted its hardware cleanup.
                if (worker.Join(500)) ready.Dispose();
            }
            void WriteLoop()
            {
                try
                {
                    while (true)
                    {
                        string next = null;
                        lock (gate)
                        {
                            if (pending.Count > 0) next = pending.Dequeue();
                            else if (finishing) return;
                        }
                        if (next == null) { ready.WaitOne(); continue; }
                        Console.Out.WriteLine(next);
                        Console.Out.Flush();
                    }
                }
                catch (Exception ex) { lock (gate) error = "Die Druckwert-Verbindung wurde unterbrochen: " + ex.Message; }
            }
        }

        static void RequireAlive(MonitorHostProtocol state, Stopwatch clock)
        {
            state.CheckHeartbeat(clock.ElapsedMilliseconds);
            if (state.Stopped) throw new OperationCanceledException();
        }

        static void SendFeature(SafeFileHandle handle, byte[] bytes, string purpose)
        {
            if (!FeatureNative.HidD_SetFeature(handle, bytes, bytes.Length)) throw Native.Error(purpose);
        }

        sealed class DeviceEvidence
        {
            readonly Stopwatch clock;
            readonly OutputQueue output;
            long sequence, lastEvidence, lastProof;
            public DeviceEvidence(Stopwatch clock, OutputQueue output) { this.clock = clock; this.output = output; }
            public bool NeedsQuery { get { return clock.ElapsedMilliseconds - lastEvidence >= 150; } }
            public void ConfirmResponse()
            {
                lastEvidence = clock.ElapsedMilliseconds; SendProof();
            }
            public void ConfirmTravel()
            {
                lastEvidence = clock.ElapsedMilliseconds;
                // A periodic timestamp also detects stale queued DATA in the
                // parent, without feature polling during continuous movement.
                if (lastEvidence - lastProof >= 150) SendProof();
            }
            void SendProof()
            {
                if (sequence == Int64.MaxValue) throw new InvalidDataException("Bitte die Tastatur neu verbinden.");
                lastProof = lastEvidence;
                output.Send("LIVE " + (++sequence).ToString(CultureInfo.InvariantCulture) + " " + lastEvidence.ToString(CultureInfo.InvariantCulture));
            }
        }

        static void SendTravelReport(byte[] report, OutputQueue output, DeviceEvidence evidence)
        {
            Tk75InputEvent inputEvent;
            try { inputEvent = Tk75InputEvent.Parse(report); }
            catch (InvalidDataException error) { throw new IOException("Ungültige Tastaturmeldung: " + error.Message, error); }
            switch (inputEvent.Kind)
            {
                case Tk75InputEventKind.Travel: break;
                case Tk75InputEventKind.LightingChanged:
                case Tk75InputEventKind.BusyBegin:
                case Tk75InputEventKind.BusyEnd:
                    // Known manufacturer control notifications are not pressure
                    // samples, and do not prove the input stream is still live.
                    return;
                default: throw new IOException("Tastaturzustand geändert: " + inputEvent.Description + ". Bitte neu verbinden.");
            }
            output.Send(MonitorHostProtocol.DataLine(report));
            evidence.ConfirmTravel();
        }

        static uint Identify(SafeFileHandle handle, MonitorHostProtocol state, Stopwatch clock, Action pump = null)
        {
            RequireAlive(state, clock);
            SendFeature(handle, MonitorProtocol.IdentifyRequest(), "Geräteidentifikation");
            for (int attempt = 0; attempt < 10; attempt++)
            {
                Thread.Sleep(20);
                RequireAlive(state, clock);
                byte[] reply = new byte[65];
                if (!FeatureNative.HidD_GetFeature(handle, reply, reply.Length)) throw Native.Error("Identifikation lesen");
                if (pump != null) pump();
                if (reply[0] != 0 || reply[1] != 0x8f) continue;
                uint id = MonitorProtocol.DeviceId(reply);
                if (!MonitorProtocol.IsSupportedTk75(id)) throw new InvalidDataException("Dieses Tastaturmodell ist noch nicht für die automatische Druckwert-Erfassung geprüft.");
                return id;
            }
            throw new InvalidDataException("Die Tastatur hat keine passende Identifikation geliefert.");
        }

        static void PumpRgbInput(InputReader input, MonitorHostProtocol state, Stopwatch clock, OutputQueue output, DeviceEvidence evidence)
        {
            for (int count = 0; count < 32; count++)
            {
                byte[] report = input.Read(0); if (report == null) break;
                SendTravelReport(report, output, evidence); RequireAlive(state, clock);
            }
        }
        static void SettleRgbWithInput(int milliseconds, InputReader input, MonitorHostProtocol state,
            Stopwatch clock, OutputQueue output, DeviceEvidence evidence)
        {
            RgbInputSettling.Wait(milliseconds, Stopwatch.Frequency, Stopwatch.GetTimestamp, input.Read,
                delegate(byte[] report) { SendTravelReport(report, output, evidence); },
                delegate { RequireAlive(state, clock); });
        }
        static void RefreshDeviceEvidence(SafeFileHandle feature, InputReader input, uint model,
            MonitorHostProtocol state, Stopwatch clock, OutputQueue output, DeviceEvidence evidence)
        {
            if (!evidence.NeedsQuery) return;
            uint currentId;
            try { currentId = Identify(feature, state, clock, delegate { PumpRgbInput(input, state, clock, output, evidence); }); }
            catch (InvalidDataException error)
            {
                // Identity failure is a reader fault, including inside an RGB
                // transaction whose ordinary format errors only produce RGBERROR.
                throw new IOException("Geräte-Frische konnte nicht bestätigt werden: " + error.Message, error);
            }
            if (currentId != model) throw new IOException("Die Tastatur-Modellkennung hat sich geändert. Bitte neu verbinden.");
            evidence.ConfirmResponse();
        }
        static byte[] ReadRgbFeature(SafeFileHandle feature, InputReader input, byte[] command,
            MonitorHostProtocol state, Stopwatch clock, OutputQueue output, DeviceEvidence evidence)
        {
            RequireAlive(state, clock); SendFeature(feature, command, "Beleuchtung lesen");
            SettleRgbWithInput(20, input, state, clock, output, evidence); byte[] reply = new byte[65];
            if (!FeatureNative.HidD_GetFeature(feature, reply, reply.Length)) throw Native.Error("Beleuchtung lesen");
            // Count a real successful GET with its existing opcode-specific
            // validation. A SetFeature/write acknowledgment is never evidence.
            switch (command[1])
            {
                case 0x84: Tk75RgbProtocol.ParseProfile(reply); break;
                case 0x87: Tk75RgbProtocol.ParseSettings(reply); break;
                case 0x8c: Tk75RgbProtocol.ParsePictureBlock(reply); break;
                default: throw new InvalidDataException("Unbekannte RGB-Leseanfrage.");
            }
            RequireAlive(state, clock); evidence.ConfirmResponse(); PumpRgbInput(input, state, clock, output, evidence); return reply;
        }
        static void ReadRgbSnapshot(SafeFileHandle feature, InputReader input, uint model,
            MonitorRgbReadRequest request, MonitorHostProtocol state, Stopwatch clock, OutputQueue output, DeviceEvidence evidence)
        {
            // Same owning thread and feature handle as identify/monitor ON/OFF.
            // All requests constructed by ReadSnapshot are
            // GET_PROFILE/GET_LEDPARAM/GET_USERPIC only.
            string result;
            try
            {
                Tk75RgbSnapshot snapshot = Tk75RgbProtocol.ReadSnapshot(model, request.Layer, delegate(byte[] command)
                {
                    return ReadRgbFeature(feature, input, command, state, clock, output, evidence);
                });
                result = "RGBSNAPSHOT " + request.RequestId.ToString(CultureInfo.InvariantCulture) + " " + Convert.ToBase64String(Tk75RgbProtocol.EncodeSnapshot(snapshot));
            }
            catch (InvalidDataException ex)
            {
                // An unstable/incomplete read is not a valid backup. It does not
                // fabricate zero colors or interrupt an otherwise healthy reader.
                result = "RGBERROR " + request.RequestId.ToString(CultureInfo.InvariantCulture) + " " +
                    Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(ex.Message));
            }
            finally { state.CompleteRgbRead(request.RequestId); }
            // Release the request slot before the response can cause the parent
            // to submit another request on the independently running pipe thread.
            output.Send(result);
        }
        static void CompareExchangeRgb(SafeFileHandle feature, InputReader input, uint model,
            MonitorRgbWriteRequest request, MonitorHostProtocol state, Stopwatch clock, OutputQueue output, DeviceEvidence evidence, Tk75RgbRestoreGuard restoreGuard)
        {
            string result;
            try
            {
                if (request.Expected.ModelId != model) throw new InvalidDataException("RGB-Sicherung gehört zu einem anderen Tastaturmodell.");
                // The parent has already durably published this original and the
                // authorized cleanup fallback before sending the write request.
                if (request.Original != null) restoreGuard.Track(request.Original, request.Expected, request.Desired);
                Tk75RgbSnapshot snapshot = Tk75RgbExchange.Execute(request.Expected, request.Desired,
                    delegate(byte[] command) { return ReadRgbFeature(feature, input, command, state, clock, output, evidence); },
                    delegate(byte[] command)
                    {
                        // Only the pure validated plan can construct these reports.
                        // No pipe command accepts arbitrary opcodes or raw packets.
                        RefreshDeviceEvidence(feature, input, model, state, clock, output, evidence);
                        RequireAlive(state, clock); SendFeature(feature, command, "Beleuchtung ändern");
                        int settlingMs = command[1] == 0x07 || (command[1] == 0x0c && command[6] == 1) ? 100 : 20;
                        // Preserve the full settling deadline while forwarding
                        // pressure reports as soon as the owned read completes.
                        // Only real evidence, never waiting/writes, renews a lease.
                        SettleRgbWithInput(settlingMs, input, state, clock, output, evidence);
                        // The nominal 320ms of a full write burst is not a wall-
                        // clock bound: scheduler rounding and synchronous USB
                        // calls add time. Query only between completed commands,
                        // never renew from a write or interrupt a pending GET.
                        RefreshDeviceEvidence(feature, input, model, state, clock, output, evidence);
                    });
                if (request.Original != null) restoreGuard.Confirm(snapshot);
                result = "RGBSNAPSHOT " + request.RequestId.ToString(CultureInfo.InvariantCulture) + " " + Convert.ToBase64String(Tk75RgbProtocol.EncodeSnapshot(snapshot));
            }
            catch (InvalidDataException ex)
            {
                result = "RGBERROR " + request.RequestId.ToString(CultureInfo.InvariantCulture) + " " +
                    Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(ex.Message));
            }
            finally { state.CompleteRgbRead(request.RequestId); }
            output.Send(result);
        }

        static uint IdentifyRgbCleanup(SafeFileHandle feature, Action requireTime)
        {
            requireTime(); SendFeature(feature, MonitorProtocol.IdentifyRequest(), "Tastatur vor der Wiederherstellung prüfen");
            for (int attempt = 0; attempt < 10; attempt++)
            {
                Thread.Sleep(20); requireTime(); byte[] reply = new byte[65];
                if (!FeatureNative.HidD_GetFeature(feature, reply, reply.Length)) throw Native.Error("Tastatur vor der Wiederherstellung prüfen");
                if (reply[0] == 0 && reply[1] == 0x8f) return MonitorProtocol.DeviceId(reply);
            }
            throw new InvalidDataException("Die Tastaturidentität konnte vor der Wiederherstellung nicht bestätigt werden.");
        }

        static void RestoreRgbOnExit(SafeFileHandle feature, Tk75RgbRestoreGuard restoreGuard)
        {
            if (!restoreGuard.Armed) return;
            var deadline = Stopwatch.StartNew();
            Action requireTime = delegate { if (deadline.ElapsedMilliseconds >= 5000) throw new TimeoutException("Die normale Beleuchtung konnte vor dem Verbindungsende nicht vollständig bestätigt werden."); };
            // Cleanup uses the same owned HID handle, after input monitoring is
            // off. It deliberately needs neither a parent heartbeat nor stdout.
            restoreGuard.Restore(delegate { return IdentifyRgbCleanup(feature, requireTime); }, delegate(byte[] command)
            {
                requireTime(); SendFeature(feature, command, "Normale Beleuchtung lesen"); Thread.Sleep(20); requireTime();
                byte[] reply = new byte[65];
                if (!FeatureNative.HidD_GetFeature(feature, reply, reply.Length)) throw Native.Error("Normale Beleuchtung lesen");
                return reply;
            }, delegate(byte[] command)
            {
                requireTime(); SendFeature(feature, command, "Normale Beleuchtung wiederherstellen");
                Thread.Sleep(command[1] == 0x07 || command[1] == 0x0c && command[6] == 1 ? 100 : 20); requireTime();
            });
        }

        public static int Run()
        {
            MonitorHostProtocol state = new MonitorHostProtocol();
            Stopwatch clock = Stopwatch.StartNew();
            OutputQueue output = new OutputQueue();
            Thread commands = new Thread(delegate()
            {
                try
                {
                    while (!state.Stopped)
                    {
                        string line = MonitorHostProtocol.ReadBoundedLine(Console.In);
                        state.Receive(line, clock.ElapsedMilliseconds);
                        if (line == null) return;
                    }
                }
                catch (Exception ex) { state.Fail(ex.Message); }
            });
            commands.IsBackground = true;
            ConsoleCancelEventHandler cancel = delegate(object sender, ConsoleCancelEventArgs e) { e.Cancel = true; state.Stop(); };
            Console.CancelKeyPress += cancel;
            commands.Start();
            SafeFileHandle feature = null;
            InputReader input = null;
            Mutex monitorOwnership = null;
            bool ownsMonitor = false;
            bool cleanupRequired = false, offSucceeded = false;
            var restoreGuard = new Tk75RgbRestoreGuard();
            string failure = null;
            try
            {
                // Register before taking HID ownership or sending any feature
                // command; otherwise Windows could end this child before the
                // editor's lighting restore has finished during session end.
                MonitorShutdownOrder.ConfigureNative();
                while (state.DevicePath == null) { RequireAlive(state, clock); Thread.Sleep(10); }
                RequireAlive(state, clock);
                // Own the shared feature/input session before identification or ON.
                // Keep ownership through OFF, reader drain and process-side cleanup.
                monitorOwnership = new Mutex(false, @"Local\AnalogKeyMapper.Tk75Monitor");
                try { ownsMonitor = monitorOwnership.WaitOne(0); }
                catch (AbandonedMutexException) { ownsMonitor = true; }
                if (!ownsMonitor)
                    throw new InvalidOperationException("Die Tastatur wird noch von einer anderen Verbindung verwendet. Bitte kurz warten und erneut verbinden.");
                List<CollectionInfo> devices = HidInventory.Enumerate().Where(d => d.vendorId == 0x3151 && d.productId == 0x5030 && d.product == "TK75 TMR" && d.error == null).ToList();
                List<CollectionInfo> inputs = devices.Where(d => d.usagePage == 0xffff && d.usage == 1 && d.inputReportLength == 32).ToList();
                List<CollectionInfo> configs = devices.Where(d => d.usagePage == 0xffff && d.usage == 2 && d.featureReportLength == 65).ToList();
                if (inputs.Count != 1 || configs.Count != 1) throw new InvalidOperationException("Die TK75-Verbindung ist nicht eindeutig. Bitte genau eine TK75 TMR per Kabel anschließen.");
                if (!String.Equals(inputs[0].devicePath, state.DevicePath, StringComparison.Ordinal)) throw new InvalidOperationException("Die ausgewählte Tastaturverbindung hat sich geändert. Bitte neu verbinden.");
                feature = Native.CreateFile(configs[0].devicePath, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (feature.IsInvalid) throw Native.Error("Tastatur-Steuerverbindung öffnen");
                uint id = Identify(feature, state, clock);
                RequireAlive(state, clock);
                input = new InputReader(inputs[0]);
                RequireAlive(state, clock);
                cleanupRequired = true; // Even a failed/partially transferred ON requires OFF.
                SendFeature(feature, MonitorProtocol.MonitorRequest(true), "Druckwert-Erfassung einschalten");
                RequireAlive(state, clock);
                output.Send("READY " + id.ToString(CultureInfo.InvariantCulture));
                output.Send("CAPS EVENTSTATE1");
                DeviceEvidence evidence = new DeviceEvidence(clock, output);
                evidence.ConfirmResponse(); // Initial Identify succeeded; ON was transferred.
                output.Send("CAPS RGBREAD1");
                output.Send("CAPS RGBWRITE1");
                output.Send("CAPS RGBRESTORE1");
                while (true)
                {
                    RequireAlive(state, clock);
                    if (output.Error != null) throw new IOException(output.Error);
                    MonitorRgbReadRequest rgbRead = state.TakeRgbRead();
                    if (rgbRead != null) ReadRgbSnapshot(feature, input, id, rgbRead, state, clock, output, evidence);
                    MonitorRgbWriteRequest rgbWrite = state.TakeRgbWrite();
                    if (rgbWrite != null) CompareExchangeRgb(feature, input, id, rgbWrite, state, clock, output, evidence, restoreGuard);
                    RefreshDeviceEvidence(feature, input, id, state, clock, output, evidence);
                    byte[] report = input.Read(50);
                    RequireAlive(state, clock);
                    if (report != null) SendTravelReport(report, output, evidence);
                }
            }
            catch (OperationCanceledException) { failure = state.Error; }
            catch (Exception ex) { failure = ex.Message; }
            finally
            {
                try
                {
                    state.Stop();
                    try
                    {
                        if (cleanupRequired)
                        {
                            SendFeature(feature, MonitorProtocol.MonitorRequest(false), "Druckwert-Erfassung ausschalten");
                            offSucceeded = true;
                        }
                    }
                    catch (Exception ex) { failure = "Die Druckwert-Erfassung konnte nicht ausgeschaltet werden. Bitte das Tastaturkabel kurz trennen. " + ex.Message; }
                    finally
                    {
                        try { if (feature != null && !feature.IsInvalid) RestoreRgbOnExit(feature, restoreGuard); }
                        catch (Exception ex) { failure = "Normale Beleuchtung konnte nicht vollständig wiederhergestellt werden; Original-Sicherung bleibt erhalten. " + ex.Message; }
                        output.BeginCleanup(failure, offSucceeded);
                        string cleanupFailure = null;
                        // OFF must precede this cancel/drain: InputReader retains its
                        // native buffer until GetOverlappedResult(wait:true) finishes.
                        // Do not free that memory early to force a bounded shutdown.
                        try { if (input != null) input.Dispose(); }
                        catch (Exception ex) { cleanupFailure = "Druckwert-Leser konnte nicht geschlossen werden: " + ex.Message; }
                        try { if (feature != null) feature.Dispose(); }
                        catch (Exception ex) { cleanupFailure = "Tastatur-Steuerverbindung konnte nicht geschlossen werden: " + ex.Message; }
                        finally
                        {
                            if (cleanupFailure != null) failure = cleanupFailure;
                            Console.CancelKeyPress -= cancel;
                            output.Finish(cleanupFailure);
                        }
                    }
                }
                finally
                {
                    // AbandonedMutexException also grants ownership. Release on
                    // this owning thread, and close even if earlier cleanup threw.
                    try { if (ownsMonitor) monitorOwnership.ReleaseMutex(); }
                    finally { if (monitorOwnership != null) monitorOwnership.Dispose(); }
                }
            }
            return failure == null ? 0 : 1;
        }
    }
}
