using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Slot = Tk75.Output.ControllerStartupObservation.Slot;

namespace Tk75.Output
{
    // Explicit installation check only. It never submits a non-neutral frame and
    // does not open a keyboard. Other existing XInput slots remain untouched.
    public static class ControllerProbe
    {
        [StructLayout(LayoutKind.Sequential)]
        struct State { public uint PacketNumber; public XInputPacket Gamepad; }
        [DllImport("xinput1_4.dll", CallingConvention = CallingConvention.Winapi)]
        static extern uint XInputGetState(uint userIndex, out State state);
        static Slot[] Snapshot()
        {
            var result = new List<Slot>();
            for (uint i = 0; i < 4; i++)
            {
                State state; uint status = XInputGetState(i, out state);
                if (status != 0) state = new State(); // Failed XInput calls leave the output buffer unspecified.
                result.Add(new Slot { Index = (int)i, Status = status, Buttons = state.Gamepad.Buttons, LT = state.Gamepad.LeftTrigger, RT = state.Gamepad.RightTrigger,
                    LX = state.Gamepad.LeftX, LY = state.Gamepad.LeftY, RX = state.Gamepad.RightX, RY = state.Gamepad.RightY });
            }
            return result.ToArray();
        }
        public static int Run(string path)
        {
            return Run(path, "ViGEm (activation blocked)", delegate { return new IsolatedOutput(); });
        }

        // Explicit research check; this is never selected by the normal mapper.
        // The separately built helper is not installed or started by opening UI.
        public static int RunViiper(string path)
        {
            string helper = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ViiperOutputHost.exe");
            return Run(path, "VIIPER Xbox neutral-only probe (experimental)", delegate { return new IsolatedOutput(helper, "--xbox-probe-host", 15000, 250, 3500); });
        }

        public static int RunXboxRuntime(string path)
        {
            return Run(path, "VIIPER Xbox runtime neutral acceptance", delegate { return ControllerOutputs.Create(Tk75.Mapping.ControllerKind.Xbox360); });
        }

        static int Run(string path, string backend, Func<IControllerOutput> factory)
        {
            // Reserve a writable new evidence file before any controller action.
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            using (var writer = new StreamWriter(file, new UTF8Encoding(false)))
            {
                Slot[] before = null, after = null;
                ControllerStartupObservation observation = null;
                string error = null; bool connected = false, removed = false, neutral = false;
                object gate = new object();
                try
                {
                    before = Snapshot(); observation = new ControllerStartupObservation(before);
                    using (var output = factory())
                    using (var stop = new ManualResetEvent(false))
                    {
                        string observerError = null;
                        var observationClock = Stopwatch.StartNew();
                        // Observe while CONNECT is in progress too. A transient
                        // non-neutral sample stays a failure even if later zero.
                        var observer = new Thread(delegate() {
                            try {
                                do { var sample = Snapshot(); long sampledAt = observationClock.ElapsedMilliseconds; lock (gate) observation.Add(sample, sampledAt); } while (!stop.WaitOne(5));
                            } catch (Exception ex) { lock (gate) observerError = ex.Message; }
                        }) { IsBackground = true, Name = "Explicit neutral startup observer" };
                        observer.Start();
                        try
                        {
                            output.Connect(); output.Neutral(); connected = true;
                            var watch = Stopwatch.StartNew(); long connectedAt = observationClock.ElapsedMilliseconds;
                            while (watch.ElapsedMilliseconds < 3000)
                            {
                                lock (gate)
                                {
                                    if (observerError != null) throw new InvalidOperationException(observerError);
                                    if (observation.Failure != null) throw new InvalidOperationException(observation.Failure);
                                    if (observation.FirstObservedMs.HasValue && observation.LastObservedMs.HasValue &&
                                        observation.LastObservedMs.Value - Math.Max(connectedAt, observation.FirstObservedMs.Value) >= 500 &&
                                        observationClock.ElapsedMilliseconds - observation.LastObservedMs.Value <= 100)
                                    { neutral = true; break; }
                                }
                                // Explicit all-zero FRAME heartbeats also satisfy
                                // hosts whose NEUTRAL command does not renew input.
                                output.Submit(new Tk75.Mapping.ControllerFrame());
                                Thread.Sleep(50);
                            }
                            if (!neutral) throw new InvalidOperationException("Kein über 500 ms neutral beobachteter neuer XInput-Platz.");
                        }
                        finally
                        {
                            stop.Set();
                            if (!observer.Join(250)) throw new TimeoutException("Die XInput-Beobachtung konnte nicht rechtzeitig beendet werden.");
                            lock (gate)
                            {
                                if (observerError != null) throw new InvalidOperationException(observerError);
                                if (observation.Failure != null) throw new InvalidOperationException(observation.Failure);
                            }
                        }
                    }
                }
                catch (Exception ex) { error = ex.Message; neutral = false; }
                // Observe cleanup on success AND failure, without deleting any
                // existing controller or making another native connection.
                try
                {
                    var removal = Stopwatch.StartNew();
                    do {
                        after = Snapshot();
                        removed = observation != null && observation.AllNewSlotsRemoved(after);
                        if (removed || observation == null) break;
                        Thread.Sleep(50);
                    } while (removal.ElapsedMilliseconds < 2500);
                    if (!removed && error == null) error = "Nach dem Beenden ist noch ein neuer XInput-Platz sichtbar.";
                }
                catch (Exception ex) { if (error == null) error = ex.Message; }
                bool success = error == null && connected && neutral && removed;
                lock (gate)
                {
                    var document = new { schemaVersion = 2, utc = DateTime.UtcNow.ToString("o"), kind = "explicit-live-neutral-controller-check", backend,
                        connected, newSlot = observation == null ? -1 : observation.NewSlot, neutral, removed, success, error, before, after,
                        firstObserved = observation == null ? null : observation.First, lastObserved = observation == null ? null : observation.Last,
                        observedSamples = observation == null ? 0 : observation.Samples,
                        firstObservedMs = observation == null ? null : observation.FirstObservedMs, lastObservedMs = observation == null ? null : observation.LastObservedMs,
                        largestSampleGapMs = observation == null ? 0 : observation.LargestGapMs,
                        note = "No keyboard opened; no non-neutral input submitted. XInput sampled about every 5 ms, including CONNECT; neutral held for 500 ms. Sampling cannot exclude shorter unobserved transients. This does not prove real-key mapping or failure behavior." };
                    writer.WriteLine(new JavaScriptSerializer().Serialize(document));
                }
                return success ? 0 : 1;
            }
        }
    }
}
