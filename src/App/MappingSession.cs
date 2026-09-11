using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Tk75.Mapping;
using Tk75.Output;

namespace Tk75.App
{
    // A single gate serializes configuration, worker and non-thread-safe output.
    // Losing input/output disarms the session until an explicit Enable call.
    public sealed class MappingSession : IDisposable
    {
        public const double MaximumInputAgeMilliseconds = 500;
        const double PreviewIntervalSeconds = 0.033;
        readonly object gate = new object();
        readonly ManualResetEvent stopping = new ManualResetEvent(false);
        readonly AutoResetEvent changed = new AutoResetEvent(false);
        readonly Thread worker;
        readonly WorkspaceStore store;
        readonly Func<ControllerKind, IControllerOutput> outputFactory;
        Func<bool> isReading;
        Func<double, IDictionary<int, double>> getRawSnapshot;
        Action<double, Dictionary<int, double>> copyRawSnapshot;
        readonly Dictionary<int, double> rawSnapshot = new Dictionary<int, double>();
        Profile profile = new Profile();
        Profile availableProfile = new Profile();
        readonly HashSet<int> observedKeys = new HashSet<int>();
        HashSet<int> neededKeys = new HashSet<int>();
        readonly HashSet<int> unavailableKeys = new HashSet<int>();
        readonly HashSet<int> startupHeldKeys = new HashSet<int>();
        readonly List<int> startupReleasedKeys = new List<int>();
        Dictionary<int, Calibration> calibrations = new Dictionary<int, Calibration>();
        readonly Dictionary<string, SignalState> states = new Dictionary<string, SignalState>();
        readonly Dictionary<int, KeyInputState> inputStates = new Dictionary<int, KeyInputState>();
        readonly Dictionary<string, SignalState> previewStates = new Dictionary<string, SignalState>();
        readonly Dictionary<int, KeyInputState> previewInputStates = new Dictionary<int, KeyInputState>();
        IControllerOutput output;
        ControllerFrame frame = new ControllerFrame();
        PreviewSnapshot preview = new PreviewSnapshot();
        readonly ControllerFrame unavailableFrame = ErrorFrame("Die Tastatur liest keine aktuellen Eingabedaten.");
        readonly PreviewSnapshot unavailablePreview = ErrorPreview("Die Tastatur liest keine aktuellen Eingabedaten.");
        string status = "Nur lesen - Controller aus";
        bool disposed, keyboardMode;
        volatile bool previewActive = true;
        bool previewClockValid;
        double previewPrevious;
        long previewComputations;

        public MappingSession(WorkspaceStore workspace)
            : this(RequireWorkspace(workspace), ControllerOutputs.Create, null, null, true) { }

        // Legacy Xbox-only offline seam: fake output plus detached snapshots.
        // No ReaderSession or ViGEm instance; null store disables logging.
        public MappingSession(WorkspaceStore workspace, Func<IControllerOutput> createOutput,
            Func<bool> inputIsReading, Func<double, IDictionary<int, double>> snapshot)
            : this(workspace, AdaptFactory(createOutput), inputIsReading, snapshot, true) { }

        // Named seam avoids ambiguous legacy anonymous delegates with no parameter list.
        public static MappingSession CreateWithControllerFactory(WorkspaceStore workspace,
            Func<ControllerKind, IControllerOutput> createOutput, Func<bool> inputIsReading,
            Func<double, IDictionary<int, double>> snapshot)
        { return new MappingSession(workspace, createOutput, inputIsReading, snapshot, true); }

        private MappingSession(WorkspaceStore workspace, Func<ControllerKind, IControllerOutput> createOutput,
            Func<bool> inputIsReading, Func<double, IDictionary<int, double>> snapshot, bool typedFactory)
        {
            if (createOutput == null) throw new ArgumentNullException("createOutput");
            if ((inputIsReading == null) != (snapshot == null)) throw new ArgumentException("Beide Eingabefunktionen gemeinsam angeben.");
            store = workspace;
            outputFactory = createOutput;
            isReading = inputIsReading ?? delegate { return false; };
            getRawSnapshot = snapshot ?? delegate { return new Dictionary<int, double>(); };
            worker = new Thread(Run) { IsBackground = true, Name = "Analog mapping" };
            try { worker.Start(); } catch { stopping.Dispose(); changed.Dispose(); throw; }
        }
        static Func<ControllerKind, IControllerOutput> AdaptFactory(Func<IControllerOutput> createOutput)
        {
            if (createOutput == null) throw new ArgumentNullException("createOutput");
            return delegate(ControllerKind kind) {
                if (kind != ControllerKind.Xbox360)
                    throw new InvalidOperationException("Diese Ausgabefunktion unterstützt nur Xbox 360. Für andere Controllertypen ist eine typisierte Ausgabefunktion erforderlich.");
                return createOutput();
            };
        }
        static WorkspaceStore RequireWorkspace(WorkspaceStore workspace)
        { if (workspace == null) throw new ArgumentNullException("workspace"); return workspace; }
        public string Status { get { lock (gate) return status; } }
        public bool Enabled
        { get { lock (gate) { try { return !disposed && output != null && output.IsConnected; } catch { return false; } } } }
        public ControllerFrame Frame { get { lock (gate) return CopyFrame(frame); } }
        public PreviewSnapshot Preview { get { lock (gate) return preview.Copy(); } }
        internal long PreviewComputations { get { lock (gate) return previewComputations; } }
        public bool KeyboardMode { get { lock (gate) return keyboardMode; } }

        // Display demand never arms, pauses or resets actual output processing.
        // A newly visible route starts a fresh preview history and clock; time
        // spent hidden must not advance its smoothing or rapid-trigger history.
        public void SetPreviewActive(bool value)
        {
            lock (gate)
            {
                CheckDisposed(); if (previewActive == value) return;
                previewActive = value; ResetPreview(null); changed.Set();
            }
        }

        // The same gate as Submit makes return from this method a neutral-output
        // boundary. The caller may release Windows-key suppression afterwards.
        public void SetKeyboardMode(bool value)
        {
            lock (gate)
            {
                CheckDisposed(); if (keyboardMode == value) return;
                keyboardMode = value; ResetProcessing(); frame = new ControllerFrame();
                if (!value) ResetPreview(null);
                if (output == null) return;
                try
                {
                    if (value)
                    {
                        output.Neutral();
                        if (!output.IsConnected) throw new InvalidOperationException("Controllerverbindung beim Neutralisieren verloren.");
                        ValidatePausedInputLocked(CurrentInputLocked());
                        status = "Tastaturmodus – Controller verbunden und neutral";
                    }
                    else
                    {
                        // Validate the current input, never the preview's retained
                        // RT/SOCD/filter history. The worker starts fresh next tick.
                        var raw = CurrentInputLocked();
                        var check = MappingEngine.Compose(raw, calibrations, AvailableProfileLocked(raw),
                            new Dictionary<string, SignalState>(), new Dictionary<int, KeyInputState>(), 0);
                        if (check.Errors.Count != 0) throw new InvalidOperationException("Aktuelle Eingabedaten fehlen: " + check.Errors[0]);
                        if (!output.IsConnected) throw new InvalidOperationException("Controllerverbindung wurde unterbrochen.");
                        RefreshConnectedStatusLocked();
                    }
                }
                catch (Exception ex)
                {
                    DisableLocked("Moduswechsel fehlgeschlagen – Controller aus: " + ex.Message);
                    frame = ErrorFrame(ex.Message); throw;
                }
            }
        }

        public void SetReader(ReaderSession value)
        {
            lock (gate)
            {
                CheckDisposed();
                DisableLocked("Geraetewechsel - Controller aus");
                ResetPreview(null);
                observedKeys.Clear();
                rawSnapshot.Clear();
                isReading = value == null ? (Func<bool>)(delegate { return false; }) : delegate { return value.IsReading; };
                getRawSnapshot = null;
                copyRawSnapshot = value == null ? null : (Action<double, Dictionary<int, double>>)value.CopyRawSnapshot;
                changed.Set();
            }
        }
        public void Configure(Profile value, IDictionary<int, Calibration> calibration)
        {
            lock (gate)
            {
                CheckDisposed();
                // Rejected edits must also disarm the previous configuration.
                DisableLocked("Einstellungen geaendert - Controller aus");
                ResetPreview(null);
                try
                {
                    Profile copy = ProfileJson.Deserialize(ProfileJson.Serialize(value));
                    if (calibration == null) throw new ArgumentNullException("calibration");
                    var newCalibration = new Dictionary<int, Calibration>();
                    foreach (var item in calibration)
                    {
                        if (item.Key < 0 || item.Key > 255 || item.Value == null)
                            throw new ArgumentException("Ungueltige Tastenkalibrierung.", "calibration");
                        newCalibration.Add(item.Key, new Calibration(item.Value.Rest, item.Value.Bottom) {
                            UsableMin = item.Value.UsableMin, UsableMax = item.Value.UsableMax, MeasuredTravel = item.Value.MeasuredTravel });
                    }
                    Profile newAvailableProfile = ProfileJson.Clone(copy);
                    HashSet<int> newNeededKeys = NeededKeys(copy);
                    profile = copy;
                    availableProfile = newAvailableProfile;
                    neededKeys = newNeededKeys;
                    calibrations = newCalibration;
                    changed.Set();
                }
                catch (Exception ex)
                { status = "Einstellungen ungueltig - Controller aus: " + ex.Message; throw; }
            }
        }

        IDictionary<int, double> CurrentInputLocked()
        {
            if (!isReading()) { observedKeys.Clear(); throw new InvalidOperationException("Die Tastatur liest keine aktuellen Eingabedaten."); }
            if (copyRawSnapshot != null) copyRawSnapshot(MaximumInputAgeMilliseconds, rawSnapshot);
            else
            {
                rawSnapshot.Clear();
                var raw = getRawSnapshot(MaximumInputAgeMilliseconds);
                if (raw == null) { observedKeys.Clear(); throw new InvalidOperationException("Eingabeverbindung wurde unterbrochen."); }
                // Injected sources keep the same detached-snapshot contract.
                foreach (var item in raw) rawSnapshot.Add(item.Key, item.Value);
            }
            // Recheck a disconnect while the independently locked snapshot was read.
            if (!isReading()) { observedKeys.Clear(); rawSnapshot.Clear(); throw new InvalidOperationException("Eingabeverbindung wurde unterbrochen."); }
            foreach (int key in rawSnapshot.Keys) observedKeys.Add(key);
            return rawSnapshot;
        }
        static HashSet<int> NeededKeys(Profile value)
        {
            var needed = new HashSet<int>();
            foreach (Binding binding in value.Bindings) if (binding.Enabled) needed.Add(binding.KeyIndex);
            foreach (KeyInputSettings input in value.Inputs)
                if (needed.Contains(input.KeyIndex) && input.OppositeKeyIndex.HasValue) needed.Add(input.OppositeKeyIndex.Value);
            return needed;
        }
        void ValidatePausedInputLocked(IDictionary<int, double> raw)
        {
            // Missing/stale keys cannot affect neutral output in keyboard mode.
            // Present malformed values still indicate a broken input source.
            foreach (int index in neededKeys)
            {
                double value; if (!raw.TryGetValue(index, out value)) continue;
                Calibration calibration; calibrations.TryGetValue(index, out calibration);
                double normalized; string error;
                if (!SignalProcessor.Normalize(value, calibration, out normalized, out error)) throw new InvalidOperationException(error);
            }
        }
        Profile AvailableProfileLocked(IDictionary<int, double> raw)
        {
            var unavailable = unavailableKeys;
            unavailable.Clear();
            foreach (int index in neededKeys)
            {
                // Missing input may defer a binding, never hide an invalid or
                // missing calibration belonging to that configured binding.
                Calibration calibration; calibrations.TryGetValue(index, out calibration);
                var errors = MappingValidation.ValidateCalibration(calibration);
                if (errors.Count != 0) throw new InvalidOperationException("Taste " + index + ": " + errors[0]);
                double value;
                if (!raw.TryGetValue(index, out value))
                {
                    if (observedKeys.Contains(index)) throw new InvalidOperationException("Ein zuvor gelesener Tastenwert ist nicht mehr aktuell (Taste " + index + ").");
                    unavailable.Add(index);
                    continue;
                }
                double normalized; string error;
                if (!SignalProcessor.Normalize(value, calibration, out normalized, out error)) throw new InvalidOperationException("Taste " + index + ": " + error);
            }
            // Initial held keys are an output gate, not missing measurements.
            // Validate their calibration/freshness above before withholding any
            // binding. A paired key must not affect SOCD through a hidden member.
            unavailable.UnionWith(startupHeldKeys);
            // Do not evaluate a physical opposite-key policy with only one
            // member. Unrelated known inputs remain active immediately.
            foreach (KeyInputSettings input in profile.Inputs)
                if (input.OppositeKeyIndex.HasValue && (unavailable.Contains(input.KeyIndex) || unavailable.Contains(input.OppositeKeyIndex.Value)))
                { unavailable.Add(input.KeyIndex); unavailable.Add(input.OppositeKeyIndex.Value); }
            for (int i = 0; i < profile.Bindings.Count; i++)
                availableProfile.Bindings[i].Enabled = profile.Bindings[i].Enabled && !unavailable.Contains(profile.Bindings[i].KeyIndex);
            // Only binding participation changes. Neither the retained profile
            // nor raw values are changed, and no unknown key is reported as zero.
            return availableProfile;
        }
        void CheckConnectionInputLocked(bool captureHeldKeys)
        {
            if (!profile.Bindings.Exists(b => b.Enabled)) throw new InvalidOperationException("Das Profil hat keine aktive Zuordnung.");
            var raw = CurrentInputLocked();
            var check = MappingEngine.Compose(raw, calibrations, AvailableProfileLocked(raw), new Dictionary<string, SignalState>(), new Dictionary<int, KeyInputState>(), 0);
            if (check.Errors.Count != 0)
                throw new InvalidOperationException("Die Eingabeeinstellungen sind noch nicht bereit. " + check.Errors[0]);
            if (!captureHeldKeys) return;
            startupHeldKeys.Clear();
            // Inspect physical keys even if an unknown SOCD counterpart or
            // opposing output currently hides them in the composed frame.
            foreach (int index in neededKeys)
            {
                double value;
                if (raw.TryGetValue(index, out value) && StartupKeyPressedLocked(index, value, false)) startupHeldKeys.Add(index);
            }
        }
        bool StartupKeyPressedLocked(int index, double raw, bool waitingForRelease)
        {
            Calibration calibration = calibrations[index];
            double normalized; string error;
            if (!SignalProcessor.Normalize(raw, calibration, out normalized, out error)) throw new InvalidOperationException(error);
            // At most one raw sensor count, capped at 0.5% for small/custom
            // ranges. This affects only initial readiness, never live values,
            // calibration, profile deadzones or the ordinary signal processor.
            double restTolerance = Math.Min(1.0 / Math.Abs(calibration.Bottom - calibration.Rest), 0.005);
            if (normalized <= restTolerance && StartupRestSignalIsSmallLocked(index, raw, restTolerance)) return false;
            foreach (KeyInputSettings input in profile.Inputs)
                if (input.KeyIndex == index) return normalized >= input.ActuationPoint;
            foreach (Binding binding in profile.Bindings)
                if (binding.Enabled && binding.KeyIndex == index &&
                    normalized > binding.Processing.TopDeadzone + (waitingForRelease ? 0 : binding.Processing.Hysteresis)) return true;
            return false;
        }
        bool StartupRestSignalIsSmallLocked(int index, double raw, double tolerance)
        {
            foreach (Binding binding in profile.Bindings)
            {
                if (!binding.Enabled || binding.KeyIndex != index) continue;
                SignalSettings settings = binding.Processing;
                SignalResult signal = SignalProcessor.Process(raw, calibrations[index], settings, new SignalState(), 0);
                if (!signal.IsValid) throw new InvalidOperationException(signal.Error);
                // A tiny sensor offset must not bypass the gate when the user's
                // curve/minimum/scale amplifies it or a sensitive button fires.
                // Inspect before smoothing, which is zero on the first frame.
                if (signal.AfterDeadzone > 0 && (settings.MinOutput > 0 || settings.Scale > 1 || signal.AfterCurve > tolerance ||
                    ((int)binding.Target >= (int)OutputTarget.A && signal.AfterCurve > 0 && signal.AfterCurve >= settings.ButtonThreshold))) return false;
            }
            return true;
        }
        void UpdateStartupReleaseLocked(IDictionary<int, double> raw)
        {
            if (startupHeldKeys.Count == 0) return;
            startupReleasedKeys.Clear();
            foreach (int index in startupHeldKeys)
            {
                double value;
                // Absence is never a release. The strict path still checks the
                // original needed-key set and disconnects on stale known input.
                if (raw.TryGetValue(index, out value) && !StartupKeyPressedLocked(index, value, true)) startupReleasedKeys.Add(index);
            }
            foreach (int index in startupReleasedKeys) startupHeldKeys.Remove(index);
            if (startupReleasedKeys.Count != 0) RefreshConnectedStatusLocked();
        }
        void RefreshConnectedStatusLocked()
        {
            if (output == null) return;
            status = keyboardMode ? "Tastaturmodus – Controller verbunden und neutral" : startupHeldKeys.Count == 0 ?
                "Virtueller Controller aktiv" : "Controller verbunden – gehaltene Tasten nach Loslassen bereit";
        }
        public void Enable()
        {
            lock (gate)
            {
                CheckDisposed();
                if (output != null)
                {
                    try { if (output.IsConnected) return; }
                    catch (Exception ex) { DisableLocked("Controllerstatus fehlerhaft - Controller aus: " + ex.Message); throw; }
                    DisableLocked("Controllerverbindung verloren - Controller aus");
                }
                IControllerOutput candidate = null;
                try
                {
                    CheckConnectionInputLocked(false);
                    candidate = outputFactory(profile.Controller);
                    if (candidate == null) throw new InvalidOperationException("Controller-Backend fehlt.");
                    candidate.Connect();
                    if (!candidate.IsConnected) throw new InvalidOperationException("Controller-Backend hat keine Verbindung bestaetigt.");
                    candidate.Neutral();
                    // A slow Connect must not use an old snapshot after input
                    // loss. Newly held keys also wait for release independently.
                    CheckConnectionInputLocked(true);
                    if (!candidate.IsConnected) throw new InvalidOperationException("Controllerverbindung wurde beim Aktivieren verloren.");
                    output = candidate; candidate = null;
                    changed.Set();
                    ResetProcessing(); frame = new ControllerFrame();
                    RefreshConnectedStatusLocked();
                    Log("Controller enabled");
                }
                catch (Exception ex)
                {
                    ReleaseOutput(candidate);
                    startupHeldKeys.Clear(); ResetProcessing(); frame = ErrorFrame(ex.Message);
                    status = "Controller aus: " + ex.Message;
                    throw;
                }
            }
        }
        public void Disable(string reason)
        { lock (gate) { if (!disposed) DisableLocked(reason ?? "Controller aus"); } }
        public ControllerRelease PrepareDisable(string reason)
        {
            // Do not wait behind this slot's in-flight Submit before the group
            // can stop its other outputs. The neutral phase drains that Submit
            // under the worker gate, independently for every detached output.
            IControllerOutput previous = Interlocked.Exchange(ref output, null);
            if (previous == null) return null;
            return new ControllerRelease(delegate {
                lock (gate) { ResetDetachedOutputLocked(reason ?? "Controller aus"); }
                previous.Neutral();
            }, delegate { try { previous.Dispose(); } finally { Log("Controller disabled: " + reason); } });
        }
        IControllerOutput DetachOutputLocked(string reason)
        {
            IControllerOutput previous = Interlocked.Exchange(ref output, null);
            ResetDetachedOutputLocked(reason);
            return previous;
        }
        void ResetDetachedOutputLocked(string reason)
        {
            startupHeldKeys.Clear(); startupReleasedKeys.Clear();
            ResetProcessing(); frame = new ControllerFrame(); status = reason;
        }
        void DisableLocked(string reason)
        {
            IControllerOutput previous = DetachOutputLocked(reason);
            if (previous != null) { ReleaseOutput(previous); Log("Controller disabled: " + reason); }
        }
        void ReleaseOutput(IControllerOutput previous)
        {
            if (previous == null) return;
            try { previous.Neutral(); } catch (Exception ex) { Log("Neutral output failed: " + ex.Message); }
            try { previous.Dispose(); } catch (Exception ex) { Log("Output disconnect failed: " + ex.Message); }
        }
        void Log(string message)
        { if (store != null) try { store.Event(message); } catch (Exception) { } }
        void ResetProcessing() { states.Clear(); inputStates.Clear(); }
        void ResetPreview(string error)
        {
            previewClockValid = false;
            previewStates.Clear(); previewInputStates.Clear(); preview = new PreviewSnapshot();
            if (error != null) preview.Errors.Add(error);
        }

        void Run()
        {
            var watch = Stopwatch.StartNew(); double previous = watch.Elapsed.TotalSeconds;
            WaitHandle[] signals = { stopping, changed };
            for (;;)
            {
                int interval = Interlocked.CompareExchange(ref output, null, null) != null ? 4 : previewActive ? 16 : Timeout.Infinite;
                if (WaitHandle.WaitAny(signals, interval) == 0) return;
                // Hidden/disconnected time is not an input-processing interval.
                double now = watch.Elapsed.TotalSeconds, dt = interval == Timeout.Infinite ? 0 : now - previous; previous = now;
                lock (gate)
                {
                    if (disposed) return;
                    // No device output and no display consumer: configuration,
                    // reader changes, Enable and preview activation wake this
                    // worker explicitly. Do not capture input or compose frames
                    // merely because one of those settings changed while idle.
                    if (output == null && !previewActive) continue;
                    bool previewComputed = false;
                    try
                    {
                        if (!isReading())
                        {
                            observedKeys.Clear();
                            if (output != null) DisableLocked("Eingabeverbindung unterbrochen - Controller aus");
                            ResetProcessing(); frame = unavailableFrame;
                            previewStates.Clear(); previewInputStates.Clear(); preview = unavailablePreview;
                            previewClockValid = false;
                            continue;
                        }
                        // Both paths observe the same detached input. Preview states
                        // remain independent of output arming and unrelated missing keys.
                        var raw = CurrentInputLocked();
                        if (previewActive && (!previewClockValid || now - previewPrevious >= PreviewIntervalSeconds))
                        {
                            double previewDt = previewClockValid ? Math.Min(now - previewPrevious, 0.1) : 0;
                            preview = MappingEngine.ComposePreview(raw, calibrations, profile, previewStates, previewInputStates, previewDt);
                            previewPrevious = now; previewClockValid = true; previewComputations++;
                        }
                        // A deliberately skipped display update keeps its own
                        // history. An output-only error must not restart it at
                        // the faster output cadence on the following tick.
                        previewComputed = true;
                        UpdateStartupReleaseLocked(raw);
                        if (keyboardMode)
                        {
                            ValidatePausedInputLocked(raw);
                            var neutral = new ControllerFrame();
                            if (output != null)
                            {
                                if (!isReading() || !output.IsConnected) throw new InvalidOperationException("Eingabe- oder Controllerverbindung wurde unterbrochen.");
                                // Isolated backends renew their watchdog on FRAME,
                                // not on NEUTRAL. This is an explicit zero frame,
                                // never a mapped result or a fabricated input.
                                output.Submit(neutral);
                                if (!isReading() || !output.IsConnected) throw new InvalidOperationException("Eingabe- oder Controllerverbindung beim Neutralisieren verloren.");
                            }
                            frame = neutral;
                            continue;
                        }
                        var next = MappingEngine.Compose(raw, calibrations, AvailableProfileLocked(raw), states, inputStates, Math.Min(dt, 0.1));
                        if (next.Errors.Count != 0)
                        {
                            if (output != null) DisableLocked("Eingabedaten fehlen oder sind veraltet - Controller aus");
                            ResetProcessing(); ClearAxes(next);
                        }
                        else if (output != null)
                        {
                            if (!isReading() || !output.IsConnected)
                                throw new InvalidOperationException("Eingabe- oder Controllerverbindung wurde unterbrochen.");
                            output.Submit(next);
                            if (!output.IsConnected) throw new InvalidOperationException("Controllerverbindung beim Senden verloren.");
                        }
                        frame = next;
                    }
                    catch (Exception ex)
                    {
                        if (output != null) DisableLocked("Daten- oder Ausgabefehler - " + ex.Message);
                        ResetProcessing(); frame = ErrorFrame(ex.Message);
                        bool reading;
                        try { reading = isReading(); } catch { reading = false; }
                        if (!reading) observedKeys.Clear();
                        if (!previewComputed || !reading) ResetPreview(ex.Message);
                    }
                }
            }
        }
        static void ClearAxes(ControllerFrame value)
        { value.LeftX = value.LeftY = value.RightX = value.RightY = value.LeftTrigger = value.RightTrigger = 0; value.Buttons = 0; }
        static ControllerFrame ErrorFrame(string error)
        { var value = new ControllerFrame(); value.Errors.Add(error); return value; }
        static PreviewSnapshot ErrorPreview(string error)
        { var value = new PreviewSnapshot(); value.Errors.Add(error); return value; }
        static ControllerFrame CopyFrame(ControllerFrame source)
        {
            var copy = new ControllerFrame { LeftX = source.LeftX, LeftY = source.LeftY, RightX = source.RightX, RightY = source.RightY,
                LeftTrigger = source.LeftTrigger, RightTrigger = source.RightTrigger, Buttons = source.Buttons };
            copy.Errors.AddRange(source.Errors);
            foreach (var item in source.BindingResults)
                copy.BindingResults.Add(item.Key, new SignalResult { Normalized = item.Value.Normalized, AfterDeadzone = item.Value.AfterDeadzone,
                    AfterCurve = item.Value.AfterCurve, Final = item.Value.Final, Error = item.Value.Error });
            foreach (var item in source.InputResults)
                copy.InputResults.Add(item.Key, new KeyInputResult { Normalized = item.Value.Normalized, Active = item.Value.Active,
                    Allowed = item.Value.Allowed, Error = item.Value.Error });
            return copy;
        }
        void CheckDisposed() { if (disposed) throw new ObjectDisposedException("MappingSession"); }
        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true; DisableLocked("Anwendung beendet"); ResetPreview(null); stopping.Set();
            }
            worker.Join(); stopping.Dispose(); changed.Dispose();
        }
    }
}
