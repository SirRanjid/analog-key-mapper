using System;
using System.Collections.Generic;
using System.Threading;
using Tk75.Mapping;

namespace Tk75.App
{
    // A small independent seam for orchestration tests. Production endpoints
    // delegate to MappingSession; no fake endpoint is created by the application.
    public interface IControllerSession : IDisposable
    {
        bool Enabled { get; }
        ControllerFrame Frame { get; }
        PreviewSnapshot Preview { get; }
        string Status { get; }
        void Configure(Profile profile, IDictionary<int, Calibration> calibration);
        void SetInputSource(object source);
        void SetKeyboardMode(bool value);
        void Enable();
        void Disable(string reason);
        ControllerRelease PrepareDisable(string reason);
    }

    // Optional for detached test/custom endpoints. Every production session
    // implements display demand separately from its actual output lifecycle.
    public interface IPreviewDemandSession
    {
        void SetPreviewActive(bool value);
    }

    public sealed partial class MultiControllerSession : IDisposable
    {
        public const int MaximumConnectedXboxControllers = 4;
        readonly object gate = new object();
        readonly Func<IControllerSession> createSession;
        readonly int maximumConnectedXboxControllers;
        readonly Dictionary<string, Slot> slots = new Dictionary<string, Slot>(StringComparer.Ordinal);
        string selected = ControllerRouting.DefaultControllerId;
        string configurationError;
        object inputSource;
        bool disposed, keyboardMode;
        bool previewActive = true;

        sealed class Slot
        {
            public ControllerDefinition Definition;
            public IControllerSession Session;
            public string Failure;
            public int[] ActiveKeys;
        }
        sealed class PendingRelease
        {
            public Slot Slot;
            public ControllerRelease Release;
            public Exception Error;
        }
        public MultiControllerSession(Func<IControllerSession> factory) : this(factory, MaximumConnectedXboxControllers) { }
        public MultiControllerSession(Func<IControllerSession> factory, int maximumXboxControllers)
        {
            if (factory == null) throw new ArgumentNullException("factory");
            if (maximumXboxControllers < 1 || maximumXboxControllers > MaximumConnectedXboxControllers) throw new ArgumentOutOfRangeException("maximumXboxControllers");
            maximumConnectedXboxControllers = maximumXboxControllers;
            createSession = factory;
            Configure(new Profile(), new Dictionary<int, Calibration>());
        }
        public string SelectedControllerId
        {
            get { lock (gate) return selected; }
            set
            {
                lock (gate)
                {
                    CheckDisposed();
                    if (value == null || !slots.ContainsKey(value)) throw new ArgumentException("Unknown controller.", "value");
                    // Only display demand follows selection. Actual mapping and
                    // output state stay independent for every connected route.
                    if (selected == value) return;
                    selected = value; UpdatePreviewDemandLocked();
                }
            }
        }
        public void SetPreviewActive(bool value)
        {
            lock (gate)
            {
                CheckDisposed(); if (previewActive == value) return;
                previewActive = value; UpdatePreviewDemandLocked();
            }
        }
        void UpdatePreviewDemandLocked()
        {
            foreach (KeyValuePair<string, Slot> entry in slots)
            {
                if (entry.Value.Failure != null) continue;
                var demand = entry.Value.Session as IPreviewDemandSession;
                if (demand != null) demand.SetPreviewActive(previewActive && entry.Key == selected);
            }
        }
        public bool Enabled { get { lock (gate) { Slot slot; return !disposed && slots.TryGetValue(selected, out slot) && slot.Failure == null && slot.Session.Enabled; } } }
        public bool IsControllerEnabled(string controllerId)
        {
            lock (gate)
            {
                Slot slot;
                return !disposed && controllerId != null && slots.TryGetValue(controllerId, out slot) && slot.Failure == null && slot.Session.Enabled;
            }
        }
        public bool AnyEnabled
        {
            get { lock (gate) { if (disposed) return false; foreach (Slot slot in slots.Values) if (slot.Failure == null && slot.Session.Enabled) return true; return false; } }
        }
        public bool KeyboardMode
        {
            get { lock (gate) return keyboardMode; }
            set
            {
                lock (gate)
                {
                    CheckDisposed(); if (keyboardMode == value) return;
                    keyboardMode = value;
                    Exception first = null;
                    foreach (Slot slot in slots.Values)
                    {
                        if (slot.Failure != null) continue;
                        try { slot.Session.SetKeyboardMode(value); }
                        catch (Exception ex) { if (first == null) first = ex; FailSlot(slot, ex); }
                    }
                    if (first != null) throw new InvalidOperationException("Moduswechsel konnte nicht für alle Controller bestätigt werden.", first);
                }
            }
        }
        // Routed keys are detached metadata. KeyboardMode is deliberately a
        // separate condition so suppression can be installed before resuming.
        public int[] ActiveKeyIndices
        {
            get
            {
                lock (gate)
                {
                    if (disposed) return new int[0];
                    var keys = new HashSet<int>();
                    foreach (Slot slot in slots.Values)
                    {
                        if (slot.Failure != null) continue;
                        bool connected; try { connected = slot.Session.Enabled; } catch { connected = false; }
                        if (connected) keys.UnionWith(slot.ActiveKeys);
                    }
                    int[] result = new int[keys.Count]; keys.CopyTo(result); Array.Sort(result); return result;
                }
            }
        }
        public string[] ActiveControllerIds
        {
            get
            {
                lock (gate)
                {
                    if (disposed) return new string[0];
                    var result = new List<string>();
                    foreach (KeyValuePair<string, Slot> entry in slots)
                    {
                        if (entry.Value.Failure != null) continue;
                        bool active; try { active = entry.Value.Session.Enabled; } catch { active = false; }
                        if (active) result.Add(entry.Key);
                    }
                    return result.ToArray();
                }
            }
        }
        public ControllerFrame Frame
        {
            get
            {
                lock (gate)
                {
                    Slot slot;
                    string error = configurationError;
                    if (!disposed && slots.TryGetValue(selected, out slot))
                    {
                        if (slot.Failure == null) return slot.Session.Frame;
                        error = slot.Failure;
                    }
                    var frame = new ControllerFrame();
                    if (error != null) frame.Errors.Add(error);
                    return frame;
                }
            }
        }
        public string Status
        {
            get { lock (gate) { Slot slot; return !disposed && slots.TryGetValue(selected, out slot) ? slot.Failure ?? slot.Session.Status : configurationError ?? "Controller aus"; } }
        }

        public PreviewSnapshot Preview
        {
            get
            {
                lock (gate)
                {
                    Slot slot;
                    string error = configurationError;
                    if (!disposed && slots.TryGetValue(selected, out slot))
                    {
                        if (slot.Failure == null) return slot.Session.Preview;
                        error = slot.Failure;
                    }
                    var snapshot = new PreviewSnapshot();
                    if (error != null) snapshot.Errors.Add(error);
                    return snapshot;
                }
            }
        }

        public void Configure(Profile profile, IDictionary<int, Calibration> calibration)
        {
            lock (gate)
            {
                CheckDisposed();
                var created = new List<IControllerSession>();
                try
                {
                    DisableLocked("Einstellungen geändert – alle Controller aus");
                    if (calibration == null) throw new ArgumentNullException("calibration");
                    // Resolve every route before changing an existing worker.
                    List<ControllerDefinition> definitions = ControllerRouting.EffectiveControllers(profile);
                    var routes = new Dictionary<string, Profile>(StringComparer.Ordinal);
                    foreach (ControllerDefinition definition in definitions) routes.Add(definition.Id, ControllerRouting.ForController(profile, definition.Id));
                    var next = new Dictionary<string, Slot>(StringComparer.Ordinal);
                    foreach (ControllerDefinition definition in definitions)
                    {
                        Slot slot;
                        if (!slots.TryGetValue(definition.Id, out slot) || slot.Failure != null)
                        {
                            IControllerSession endpoint = createSession();
                            if (endpoint == null) throw new InvalidOperationException("Controller session factory returned no session.");
                            foreach (Slot existing in slots.Values)
                                if (Object.ReferenceEquals(existing.Session, endpoint)) throw new InvalidOperationException("A controller session cannot belong to multiple slots.");
                            foreach (IControllerSession existing in created)
                                if (Object.ReferenceEquals(existing, endpoint)) throw new InvalidOperationException("A controller session cannot belong to multiple slots.");
                            created.Add(endpoint);
                            slot = new Slot { Session = endpoint };
                            var demand = endpoint as IPreviewDemandSession;
                            if (demand != null) demand.SetPreviewActive(false);
                            endpoint.SetInputSource(inputSource);
                        }
                        slot.Session.Configure(routes[definition.Id], calibration);
                        slot.Session.SetKeyboardMode(keyboardMode);
                        var activeKeys = new HashSet<int>();
                        foreach (Binding binding in routes[definition.Id].Bindings) if (binding.Enabled) activeKeys.Add(binding.KeyIndex);
                        int[] keyIndices = new int[activeKeys.Count]; activeKeys.CopyTo(keyIndices); Array.Sort(keyIndices);
                        next.Add(definition.Id, new Slot { Definition = definition, Session = slot.Session, ActiveKeys = keyIndices });
                    }
                    foreach (KeyValuePair<string, Slot> old in slots)
                        if (!next.ContainsKey(old.Key)) old.Value.Session.Dispose();
                    slots.Clear(); foreach (KeyValuePair<string, Slot> item in next) slots.Add(item.Key, item.Value);
                    if (!slots.ContainsKey(selected)) selected = definitions[0].Id;
                    UpdatePreviewDemandLocked();
                    configurationError = null;
                }
                catch (Exception ex)
                {
                    // A partially configured set must not keep stale outputs alive.
                    foreach (IControllerSession endpoint in created) QuietClose(endpoint);
                    foreach (Slot old in slots.Values) QuietClose(old.Session);
                    slots.Clear(); configurationError = "Controller-Einrichtung fehlgeschlagen: " + ex.Message;
                    throw;
                }
            }
        }
        // Pure coordination: this opaque value is forwarded, never interpreted or
        // opened. The production-only adapter exposes typed SetReader(ReaderSession).
        public void SetInputSource(object value)
        {
            lock (gate)
            {
                CheckDisposed();
                try
                {
                    DisableLocked("Tastaturverbindung geändert – alle Controller aus");
                    foreach (Slot slot in slots.Values) if (slot.Failure == null) slot.Session.SetInputSource(value);
                    inputSource = value;
                }
                catch (Exception ex)
                {
                    inputSource = null;
                    foreach (Slot slot in slots.Values) QuietClose(slot.Session);
                    slots.Clear(); configurationError = "Tastaturverbindung fehlgeschlagen: " + ex.Message;
                    throw;
                }
            }
        }
        public void Enable()
        {
            lock (gate)
            {
                CheckDisposed(); EnableSlot(Selected());
            }
        }
        // Footer controls address their own stable ID. Never temporarily select
        // another route: output and editing selection are independent state.
        public void EnableController(string controllerId)
        {
            lock (gate)
            {
                CheckDisposed(); Slot slot;
                if (controllerId == null || !slots.TryGetValue(controllerId, out slot)) throw new ArgumentException("Unknown controller.", "controllerId");
                EnableSlot(slot);
            }
        }
        void EnableSlot(Slot slot)
        {
            if (slot.Failure != null) throw new InvalidOperationException(slot.Failure);
            if (slot.Session.Enabled) return;
            if (slot.Definition.Kind == ControllerKind.Xbox360)
            {
                int count = 0;
                foreach (Slot other in slots.Values)
                    if (other.Definition.Kind == ControllerKind.Xbox360 && other.Failure == null && other.Session.Enabled) count++;
                if (count >= maximumConnectedXboxControllers)
                    throw new InvalidOperationException(maximumConnectedXboxControllers == 1 ? "Der aktuelle Xbox-Ausgabeweg unterstützt zunächst einen verbundenen Controller. Trenne den anderen Controller, um diesen zu verwenden." : "Es können höchstens vier Xbox-Controller gleichzeitig verbunden sein. Trenne zuerst einen anderen Controller.");
            }
            try { slot.Session.Enable(); }
            catch
            {
                try { slot.Session.Disable("Controller-Verbindung fehlgeschlagen"); }
                catch (Exception cleanupError) { FailSlot(slot, cleanupError); }
                throw;
            }
        }
        public void DisableSelected(string reason)
        { lock (gate) { DisableControllerLocked(selected, reason); } }
        public void DisableController(string controllerId, string reason)
        { lock (gate) { DisableControllerLocked(controllerId, reason); } }
        void DisableControllerLocked(string controllerId, string reason)
        {
            if (disposed || controllerId == null) return;
            Slot slot;
            if (!slots.TryGetValue(controllerId, out slot)) return;
            if (slot.Failure != null) return;
            try { slot.Session.Disable(reason); }
            catch (Exception ex) { FailSlot(slot, ex); throw; }
        }
        public void Disable(string reason)
        { lock (gate) { if (!disposed) DisableLocked(reason); } }
        void DisableLocked(string reason)
        {
            var pending = new List<PendingRelease>();
            foreach (Slot slot in slots.Values)
                if (slot.Failure == null)
                {
                    var item = new PendingRelease { Slot = slot }; pending.Add(item);
                    try { item.Release = slot.Session.PrepareDisable(reason); } catch (Exception ex) { item.Error = ex; }
                }
            // Every worker has relinquished its output before any removal starts.
            // Neutral itself can enter a backend's slow failure cleanup, so each
            // owned output gets an independent stop attempt without a thread-pool
            // queue. At most the profile's 32 slots participate in this rare path.
            ReleasePhase(pending, true);
            ReleasePhase(pending, false);
            Exception first = null;
            foreach (PendingRelease item in pending)
                if (item.Error != null) { if (first == null) first = item.Error; FailSlot(item.Slot, item.Error); }
            if (first != null) throw new InvalidOperationException("Controller konnten nicht vollständig getrennt werden.", first);
        }
        static void ReleasePhase(List<PendingRelease> pending, bool neutral)
        {
            var workers = new List<Thread>();
            var fallback = new List<ThreadStart>();
            foreach (PendingRelease item in pending)
            {
                if (item.Release == null) continue;
                PendingRelease current = item;
                ThreadStart action = delegate {
                    try { if (neutral) current.Release.Neutral(); else current.Release.Dispose(); }
                    catch (Exception error) { if (current.Error == null) current.Error = error; }
                };
                var worker = new Thread(action) { IsBackground = true, Name = neutral ? "Controller neutral" : "Controller removal" };
                try { worker.Start(); workers.Add(worker); }
                catch (Exception) { fallback.Add(action); }
            }
            foreach (ThreadStart action in fallback) action();
            foreach (Thread worker in workers) worker.Join();
        }
        Slot Selected()
        {
            Slot slot;
            if (!slots.TryGetValue(selected, out slot)) throw new InvalidOperationException(configurationError ?? "No controller is selected.");
            return slot;
        }
        static void QuietClose(IControllerSession endpoint)
        { try { endpoint.Disable("Controller aus"); } catch (Exception) { } try { endpoint.Dispose(); } catch (Exception) { } }
        static void FailSlot(Slot slot, Exception error)
        {
            slot.Failure = "Controller nach Verbindungsfehler beendet. Einstellungen erneut anwenden: " + error.Message;
            QuietClose(slot.Session);
        }
        void CheckDisposed() { if (disposed) throw new ObjectDisposedException("MultiControllerSession"); }
        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true; inputSource = null;
                try { DisableLocked("Anwendung beendet"); } catch (Exception) { }
                foreach (Slot slot in slots.Values) QuietClose(slot.Session);
                slots.Clear();
            }
        }
    }
}
