using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Tk75.Diagnostics;
using Tk75.Mapping;

namespace Tk75.App
{
    public sealed partial class MainForm
    {
        const int RgbBackupLayer = 4;
        const int RgbOperationTimeoutMilliseconds = 5000;
        // One in-flight update, a fresh restore read and the restore exchange,
        // plus bounded journal/scheduling overhead. The UI waits asynchronously.
        const int RgbCloseTimeoutMilliseconds = 3 * RgbOperationTimeoutMilliseconds + 2000;
        // A cancelled native connection may first finish its 15s Connect wait,
        // then neutralize/remove its own helper. It runs alongside RGB restore.
        const int NormalCloseTimeoutMilliseconds = 25000;
        const int RgbInitializationMaximumAttempts = 3;
        const int RgbInitializationRetryBaseMilliseconds = 500;
        RgbBackupWork rgbBackupWork;
        ReaderSession rgbAttemptedReader;
        uint? rgbAttemptedModel;
        string lastRgbUiStatus;
        bool rgbClosePending, rgbCloseFinished;
        bool rgbCloseSucceeded = true;
        RgbLightingPlan rgbPlanCache;

        bool RgbOverrideReady
        {
            get
            {
                RgbBackupWork work = rgbBackupWork; ReaderSession current = reader;
                if (work == null || current == null || !Object.ReferenceEquals(work.Reader, current) || !current.RgbWriteAvailable) return false;
                lock (work.Gate) return work.State == 1 && work.Original != null && !work.RecoveryRequired && !work.Abort && !work.Stopped;
            }
        }
        bool RgbRestoreAvailable
        {
            get
            {
                RgbBackupWork work = rgbBackupWork; ReaderSession current = reader;
                if (work == null || current == null || !Object.ReferenceEquals(work.Reader, current) || !current.RgbWriteAvailable) return false;
                lock (work.Gate) return work.Original != null && !work.Abort && !work.Stopped;
            }
        }
        string RgbOverrideStatusText
        {
            get
            {
                ReaderSession current = reader;
                if (current == null || !current.IsReading) return Tr("Für Beleuchtung die Tastatur verbinden", "Connect the keyboard for lighting");
                if (!current.RgbReadAvailable) return Tr("Beleuchtungszugriff ist noch nicht verfügbar", "Lighting access is not available yet");
                RgbBackupWork work = rgbBackupWork;
                if (work == null || !Object.ReferenceEquals(work.Reader, current) || current.DeviceModelId != work.Model)
                    return Tr("Beleuchtungszugriff wird vorbereitet …", "Preparing lighting access …");
                lock (work.Gate)
                {
                    if (work.State == 0) return Tr("Normale Beleuchtung wird gesichert …", "Backing up normal lighting …");
                    if (work.Error != null)
                    {
                        if (work.Original != null && work.Current != null && work.Original.ModelId == work.Current.ModelId &&
                            work.Original.Layer == work.Current.Layer && work.Original.Profile != work.Current.Profile)
                            return Tr("Tastaturprofil gewechselt · Zum vorherigen Tastaturprofil zurückwechseln, dann „Wiederherstellen“ wählen",
                                "Keyboard profile changed · Switch back to the previous keyboard profile, then choose Restore lighting");
                        return Tr("Beleuchtung angehalten: ", "Lighting paused: ") + UiText.Get(work.Error);
                    }
                    if (work.RecoveryRequired) return work.Busy ? Tr("Gesicherte Beleuchtung wird wiederhergestellt …", "Restoring saved keyboard lighting …") : Tr("Frühere Beleuchtung übernehmen: „Wiederherstellen“ wählen", "Previous lighting backup found: choose Restore");
                    if (!current.RgbWriteAvailable) return Tr("Beleuchtung gesichert; Schreibzugriff noch nicht verfügbar", "Lighting backed up; writing is not available yet");
                    if (work.Busy) return Tr("Beleuchtung wird abgeglichen …", "Updating keyboard lighting …");
                    if (work.UnsupportedKeys != 0) return Tr("Einige Tasten haben noch keine bekannte Lichtposition", "Some keys have no known lighting position yet");
                    if (work.SwitchKeyUnavailable) return Tr("Für die Umschalttaste ist keine Lichtposition bekannt", "No lighting position is known for the mode-switch key");
                    if (UiReadProfile.ModeSwitchLightingEnabled && !modeHotkey) return Tr("Umschalttaste: Modus-Tastenkombination ist nicht aktiv", "Mode-switch lighting: the shortcut is not active");
                    if (work.Applied) return Tr("Tastenfarben aktiv · Original gesichert", "Key colors active · original backed up");
                    if (!UiReadProfile.RgbOverrideEnabled) return Tr("Tastenfarben ausgeschaltet · Sicherung vorhanden", "Key colors off · backup available");
                    if (runtime.KeyboardMode) return Tr("Normale Beleuchtung · Tastaturmodus", "Normal lighting · keyboard mode");
                    if (!runtime.AnyEnabled) return Tr("Tastenfarben bereit · Controller verbinden", "Key colors ready · connect a controller");
                    return Tr("Normale Beleuchtung · Sicherung vorhanden", "Normal lighting · backup available");
                }
            }
        }

        void RefreshRgbLighting()
        {
            if (closing || IsDisposed || deviceDetachInProgress || rgbClosePending) return;
            ReaderSession current = reader;
            uint? model = current == null ? (uint?)null : current.DeviceModelId;
            bool workerIdle = true;
            if (rgbBackupWork != null) lock (rgbBackupWork.Gate) workerIdle = !rgbBackupWork.Busy;
            if (workerIdle && current != null && current.IsReading && current.RgbReadAvailable &&
                (model == 3590 || model == 3591) &&
                (!Object.ReferenceEquals(rgbAttemptedReader, current) || rgbAttemptedModel != model))
            {
                // Capture the session/model on the UI thread; metadata copying,
                // pipe waiting and disk I/O run without UI/ReaderSession locks.
                rgbAttemptedReader = current; rgbAttemptedModel = model;
                if (rgbBackupWork != null) lock (rgbBackupWork.Gate) { rgbBackupWork.Abort = true; Monitor.PulseAll(rgbBackupWork.Gate); }
                var work = new RgbBackupWork(current, model.Value, store.Root);
                rgbBackupWork = work;
                try
                {
                    work.Worker = new Thread(new ThreadStart(delegate { RunRgbWorker(work); })) { IsBackground = true, Name = "Keyboard lighting" };
                    work.Worker.Start();
                }
                catch (Exception ex) { lock (work.Gate) { work.Error = BoundedRgbError(ex); work.State = 2; work.Busy = false; work.Stopped = true; } }
            }
            // The shortcut editor temporarily unregisters its hotkeys. Keep the
            // applied color plan while editing instead of restoring the original
            // picture and writing the same marker again when the dialog closes.
            // Identity/readiness checks above and worker restore requests remain
            // active; only new ordinary color plans pause in the modal editor.
            bool rgbReady = RgbOverrideReady;
            if (rgbReady && !modeShortcutDialogOpen)
            {
                RgbBackupWork work = rgbBackupWork;
                Profile profile = UiReadProfile;
                bool shortcutActive = modeHotkey && modeShortcutRegistrationActive && !modeShortcutDialogOpen;
                KeyboardLayout layout = keyboard.LayoutModel;
                int? shortcutIndex = profile.ModeSwitchLightingEnabled && shortcutActive
                    ? HotkeyPhysicalKeyResolver.ResolveIndex(layout, profile.ModeSwitchHotkey.KeyCode) : null;
                RgbLightingPlan plan = GetRgbLightingPlan(work, profile, runtime.ActiveControllerIds, runtime.KeyboardMode,
                    shortcutActive, shortcutIndex, layout);
                lock (work.Gate)
                {
                    // Recheck readiness after the short UI calculation: a worker
                    // failure must not receive a new ordinary color request.
                    if (work.State != 1 || work.RecoveryRequired || work.Abort || work.Stopped) rgbPlanCache = null;
                    else
                    {
                        work.UnsupportedKeys = plan.UnsupportedKeys;
                        work.SwitchKeyUnavailable = plan.SwitchKeyUnavailable;
                        // Queue identity remains separate from calculation identity.
                        // Manual restore can clear LastPlanKey even on a cache hit.
                        if (work.LastPlanKey != plan.Key && !work.QuitAfterRestore)
                        {
                            try
                            {
                                Tk75RgbSnapshot desired = plan.Colors.Count == 0 ? work.Original : BuildRgbDesired(work.Original, plan.Colors);
                                work.LastPlanKey = plan.Key; work.Desired = desired;
                                Monitor.PulseAll(work.Gate);
                            }
                            catch (InvalidDataException error)
                            { work.Error = BoundedRgbError(error); work.State = 2; work.Desired = null; rgbPlanCache = null; }
                        }
                    }
                }
            }
            else if (!rgbReady) rgbPlanCache = null;
            string status = RgbOverrideStatusText;
            // Render the same captured worker state that is compared here. A
            // no-op transaction can finish between this read and UI refresh.
            if (status != lastRgbUiStatus) RefreshRgbUi(status);
        }

        RgbLightingPlan GetRgbLightingPlan(RgbBackupWork work, Profile profile, string[] activeIds, bool keyboardMode,
            bool shortcutActive, int? shortcutIndex, KeyboardLayout layout)
        {
            RgbLightingPlan cached = rgbPlanCache;
            // UiReadProfile changes identity on every history/snapshot change,
            // including Undo/Redo and loading a different history with equal JSON.
            if (cached != null && Object.ReferenceEquals(cached.Work, work) && Object.ReferenceEquals(cached.Profile, profile) &&
                Object.ReferenceEquals(cached.Layout, layout) && cached.KeyboardMode == keyboardMode &&
                cached.ShortcutActive == shortcutActive && cached.ShortcutIndex == shortcutIndex && cached.ActiveIds.SetEquals(activeIds)) return cached;
            return rgbPlanCache = new RgbLightingPlan(work, profile, activeIds, keyboardMode, shortcutActive, shortcutIndex, layout, PhysicalInputKey);
        }
        sealed class RgbLightingPlan
        {
            internal readonly RgbBackupWork Work;
            internal readonly Profile Profile;
            internal readonly KeyboardLayout Layout;
            internal readonly HashSet<string> ActiveIds;
            internal readonly bool KeyboardMode, ShortcutActive, SwitchKeyUnavailable;
            internal readonly int? ShortcutIndex;
            internal readonly Dictionary<int, int> Colors;
            internal readonly string Key;
            internal readonly int UnsupportedKeys;
            internal RgbLightingPlan(RgbBackupWork work, Profile profile, string[] activeIds, bool keyboardMode,
                bool shortcutActive, int? shortcutIndex, KeyboardLayout layout, Func<int, int?> physicalKey)
            {
                Work = work; Profile = profile; Layout = layout; KeyboardMode = keyboardMode; ShortcutActive = shortcutActive;
                ShortcutIndex = shortcutIndex; ActiveIds = new HashSet<string>(activeIds, StringComparer.Ordinal);
                Dictionary<int, int> logical = RgbOverridePlan.Build(profile, activeIds, keyboardMode, null, false);
                var planned = new Dictionary<int, int>();
                foreach (var entry in logical)
                { int? physical = physicalKey(entry.Key); if (physical.HasValue && !planned.ContainsKey(physical.Value)) planned.Add(physical.Value, entry.Value); }
                // The mode hotkey is already a physical keyboard key, not a routed input.
                if (profile.ModeSwitchLightingEnabled && profile.ModeSwitchHotkey.Enabled && shortcutActive && shortcutIndex.HasValue)
                    planned[shortcutIndex.Value] = profile.ModeSwitchRgbColor;
                Colors = new Dictionary<int, int>(); int unsupported = 0;
                foreach (var entry in planned)
                { int index; if (Tk75RgbProtocol.TryGetRgbMatrixIndex(work.Model, entry.Key, out index)) Colors.Add(entry.Key, entry.Value); else unsupported++; }
                UnsupportedKeys = unsupported;
                SwitchKeyUnavailable = profile.ModeSwitchLightingEnabled && shortcutActive && !shortcutIndex.HasValue;
                Key = string.Join(";", Colors.OrderBy(p => p.Key).Select(p => p.Key.ToString(CultureInfo.InvariantCulture) + ":" + p.Value.ToString("X6", CultureInfo.InvariantCulture)).ToArray());
            }
        }

        void RestoreKeyboardLighting()
        {
            if (!RgbRestoreAvailable) throw new InvalidOperationException(Tr("Die ursprüngliche Beleuchtung ist noch nicht verfügbar.", "The original lighting is not available yet."));
            Profile profile = history.Current;
            if (profile.RgbOverrideEnabled || profile.ModeSwitchLightingEnabled)
            { profile.RgbOverrideEnabled = false; profile.ModeSwitchLightingEnabled = false; Commit(profile); }
            RgbBackupWork work = rgbBackupWork;
            lock (work.Gate) { work.LastPlanKey = null; work.Desired = work.Original; work.RestoreRequested = true; Monitor.PulseAll(work.Gate); }
        }

        static Tk75RgbSnapshot BuildRgbDesired(Tk75RgbSnapshot original, IDictionary<int, int> colors)
        {
            byte[] settings = Tk75RgbProtocol.PictureModeSettings(original.RawSettings, original.Layer);
            if (settings[3] == 0) settings[3] = 4;
            return new Tk75RgbSnapshot(original.ModelId, original.Profile, original.Layer, settings, Tk75RgbProtocol.OverlayVisibleLighting(original, colors));
        }

        sealed class RgbBackupWork
        {
            internal readonly ReaderSession Reader;
            internal readonly uint Model;
            internal readonly string Root;
            internal readonly string RequestId = Guid.NewGuid().ToString("N");
            internal readonly DateTime StartedUtc = DateTime.UtcNow;
            internal readonly object Gate = new object();
            internal readonly List<RgbRecoveryStep> Steps = new List<RgbRecoveryStep>();
            internal int State;
            internal string Error;
            internal string Prefix, IdentityHash, BackupFile, LastPlanKey;
            internal Dictionary<string, object> Identity;
            internal Tk75RgbSnapshot Original, Current, Desired;
            internal Thread Worker;
            internal bool Busy = true, Stopped, Abort, QuitAfterRestore, RestoreRequested, RecoveryRequired, Applied, SwitchKeyUnavailable;
            internal int Sequence, UnsupportedKeys, InitAttempts, CloseRestoreAttempts;
            internal bool InitRetryEligible;
            internal RgbBackupWork(ReaderSession reader, uint model, string root)
            { Reader = reader; Model = model; Root = root; }
        }

        static string BoundedRgbError(Exception error)
        {
            string message = error == null ? "Unknown error." : error.Message;
            return message.Length > 500 ? message.Substring(0, 500) : message;
        }
        static string RgbHash(byte[] bytes)
        { using (var algorithm = SHA256.Create()) return BitConverter.ToString(algorithm.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }

        static Dictionary<string, object> RgbDeviceIdentity(ReaderSession source, uint model, out string fingerprint)
        {
            CollectionInfo device = source.Device;
            if (device == null || String.IsNullOrWhiteSpace(device.devicePath) || String.IsNullOrWhiteSpace(source.Fingerprint))
                throw new InvalidDataException("A complete device path and protocol identity are required for a lighting backup.");
            var identity = new Dictionary<string, object> {
                { "IdentitySchema", 1 }, { "ModelId", model }, { "DevicePath", device.devicePath },
                { "Serial", device.serial }, { "Manufacturer", device.manufacturer }, { "Product", device.product },
                { "VendorId", device.vendorId }, { "ProductId", device.productId }, { "Version", device.version },
                { "UsagePage", device.usagePage }, { "Usage", device.usage }, { "InputReportLength", device.inputReportLength },
                { "OutputReportLength", device.outputReportLength }, { "FeatureReportLength", device.featureReportLength },
                { "ProtocolFingerprint", source.Fingerprint }
            };
            // Canonical identity uses an explicit field order, fixed-width
            // little-endian numbers and BinaryWriter length-prefixed UTF-8.
            using (var bytes = new MemoryStream())
            {
                using (var writer = new BinaryWriter(bytes, new UTF8Encoding(false), true))
                {
                    writer.Write("AnalogKeyMapper.RgbIdentity.1"); writer.Write(model);
                    foreach (string field in new[] { "DevicePath", "Serial", "Manufacturer", "Product", "ProtocolFingerprint" })
                    { string value = (string)identity[field]; writer.Write(value != null); if (value != null) writer.Write(value); }
                    foreach (string field in new[] { "VendorId", "ProductId", "Version", "UsagePage", "Usage", "InputReportLength", "OutputReportLength", "FeatureReportLength" })
                        writer.Write((int)identity[field]);
                    writer.Flush();
                }
                byte[] canonical = bytes.ToArray(); fingerprint = RgbHash(canonical);
                identity.Add("CanonicalBase64", Convert.ToBase64String(canonical));
            }
            return identity;
        }

        static void RequireRgbOrdinaryPath(string path)
        {
            FileSystemInfo item = Directory.Exists(path) ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
            if (!item.Exists) throw new DirectoryNotFoundException(path);
            while (item != null)
            {
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Lighting backup paths must not contain links or junctions.");
                var file = item as FileInfo; item = file == null ? ((DirectoryInfo)item).Parent : file.Directory;
            }
        }
        static void WriteRgbJournal(string path, Dictionary<string, object> content)
        {
            RequireRgbJournalPath(path);
            string directory = Path.GetDirectoryName(path); RequireRgbOrdinaryPath(directory);
            string temporary = path + ".pending";
            byte[] bytes = new UTF8Encoding(false).GetBytes(new JavaScriptSerializer().Serialize(content));
            // Preserve partial files on any error. A final filename is published
            // only after durable flush; neither rename nor CreateNew overwrite.
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { file.Write(bytes, 0, bytes.Length); file.Flush(true); }
            File.Move(temporary, path);
        }
        static void RequireRgbJournalPath(string path)
        {
            if (Path.GetFullPath(path).Length + ".pending".Length >= 260)
                throw new PathTooLongException("Der Speicherort für die Beleuchtung ist zu lang. Bitte die App in einem kürzeren Ordnerpfad ablegen.");
        }

        static Dictionary<string, object> ReadRgbJournal(string path)
        {
            RequireRgbOrdinaryPath(path);
            if (new FileInfo(path).Length > 65536) throw new InvalidDataException("Lighting journal is too large.");
            var value = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
            if (value == null) throw new InvalidDataException("Invalid lighting journal."); return value;
        }
        static string RgbText(Dictionary<string, object> value, string field)
        { object found; if (!value.TryGetValue(field, out found) || !(found is string)) throw new InvalidDataException("Missing lighting journal field: " + field); return (string)found; }
        static int RgbNumber(Dictionary<string, object> value, string field)
        { object found; if (!value.TryGetValue(field, out found) || !(found is int)) throw new InvalidDataException("Invalid lighting journal number: " + field); return (int)found; }
        static Tk75RgbSnapshot RgbJournalSnapshot(Dictionary<string, object> journal, string prefix)
        {
            string encoded = RgbText(journal, prefix + "Base64"); byte[] bytes;
            try { bytes = Convert.FromBase64String(encoded); } catch (FormatException) { throw new InvalidDataException("Invalid lighting snapshot encoding."); }
            if (Convert.ToBase64String(bytes) != encoded || RgbHash(bytes) != RgbText(journal, prefix + "Sha256")) throw new InvalidDataException("Lighting snapshot integrity check failed.");
            return Tk75RgbProtocol.DecodeSnapshot(bytes);
        }
        static void PutRgbSnapshot(Dictionary<string, object> journal, string prefix, Tk75RgbSnapshot snapshot)
        { byte[] bytes = Tk75RgbProtocol.EncodeSnapshot(snapshot); journal[prefix + "Base64"] = Convert.ToBase64String(bytes); journal[prefix + "Sha256"] = RgbHash(bytes); }
        static string RgbReferencedFile(string directory, string name)
        {
            if (String.IsNullOrEmpty(name) || name != Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException("Lighting journal reference is not a local filename.");
            return Path.Combine(directory, name);
        }
        static void RequireRgbIdentity(RgbBackupWork work)
        {
            string current; RgbDeviceIdentity(work.Reader, work.Model, out current);
            if (current != work.IdentityHash || work.Reader.DeviceModelId != work.Model || !work.Reader.IsReading)
                throw new InvalidDataException("Keyboard identity changed; the original lighting backup has been retained.");
            lock (work.Gate) if (work.Abort) throw new OperationCanceledException("Lighting operation was cancelled; backup retained.");
        }
        sealed class RgbRecoveryGroup
        {
            internal string BackupFile, Prefix;
            internal Tk75RgbSnapshot Original;
            internal int Sequence;
            internal readonly List<RgbRecoveryStep> Steps = new List<RgbRecoveryStep>();
        }
        static RgbRecoveryGroup FindRgbRecovery(RgbBackupWork work, string directory)
        {
            string[] files = Directory.GetFiles(directory, "rgb-" + work.IdentityHash + "-*.transaction.json");
            if (files.Length > 4096) throw new InvalidDataException("Too many lighting recovery records; keep the backups and inspect the lighting folder.");
            var groups = new Dictionary<string, RgbRecoveryGroup>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in files)
            {
                Dictionary<string, object> request = ReadRgbJournal(path);
                if (RgbNumber(request, "SchemaVersion") != 2 || RgbText(request, "IdentitySha256") != work.IdentityHash) throw new InvalidDataException("Lighting transaction identity mismatch.");
                string backupName = RgbText(request, "OriginalBackup"); RgbRecoveryGroup group;
                if (!groups.TryGetValue(backupName, out group))
                {
                    string backupFile = RgbReferencedFile(directory, backupName); Dictionary<string, object> backup = ReadRgbJournal(backupFile);
                    if (RgbNumber(backup, "SchemaVersion") != 1 || RgbText(backup, "IdentitySha256") != work.IdentityHash || RgbText(backup, "Phase") != "Complete")
                        throw new InvalidDataException("Original lighting backup identity mismatch.");
                    group = new RgbRecoveryGroup { BackupFile = backupFile, Prefix = backupFile.Substring(0, backupFile.Length - ".backup.json".Length), Original = RgbJournalSnapshot(backup, "Snapshot") };
                    groups.Add(backupName, group);
                }
                if (RgbText(request, "OriginalSnapshotSha256") != RgbHash(Tk75RgbProtocol.EncodeSnapshot(group.Original))) throw new InvalidDataException("Lighting transaction refers to a different original snapshot.");
                var step = new RgbRecoveryStep { Sequence = RgbNumber(request, "Sequence"), Expected = RgbJournalSnapshot(request, "Expected"), Desired = RgbJournalSnapshot(request, "Desired") };
                object automaticRestore;
                if (request.TryGetValue("AutomaticRestore", out automaticRestore))
                {
                    if (!(automaticRestore is bool)) throw new InvalidDataException("Invalid automatic lighting restore marker.");
                    step.AutomaticRestore = (bool)automaticRestore;
                }
                string confirmedPath = path + ".confirmed.json";
                if (File.Exists(confirmedPath))
                {
                    Dictionary<string, object> confirmed = ReadRgbJournal(confirmedPath);
                    if (RgbText(confirmed, "IdentitySha256") != work.IdentityHash || RgbText(confirmed, "TransactionFile") != Path.GetFileName(path) || RgbText(confirmed, "OriginalBackup") != backupName)
                        throw new InvalidDataException("Lighting confirmation identity mismatch.");
                    step.Confirmed = RgbJournalSnapshot(confirmed, "Confirmed");
                }
                group.Steps.Add(step); group.Sequence = Math.Max(group.Sequence, step.Sequence);
            }
            RgbRecoveryGroup pending = null;
            foreach (RgbRecoveryGroup group in groups.Values)
            {
                group.Steps.Sort(delegate(RgbRecoveryStep a, RgbRecoveryStep b) { return a.Sequence.CompareTo(b.Sequence); });
                int resolved = 0;
                foreach (string resolutionPath in Directory.GetFiles(directory, Path.GetFileName(group.Prefix) + ".resolved-*.json"))
                {
                    Dictionary<string, object> resolution = ReadRgbJournal(resolutionPath); int sequence = RgbNumber(resolution, "ResolvedThroughSequence");
                    if (RgbText(resolution, "IdentitySha256") != work.IdentityHash || RgbText(resolution, "OriginalBackup") != Path.GetFileName(group.BackupFile) ||
                        sequence < 1 || sequence > group.Sequence || !Tk75RgbExchange.Equivalent(RgbJournalSnapshot(resolution, "Confirmed"), group.Original))
                        throw new InvalidDataException("Invalid lighting recovery resolution.");
                    resolved = Math.Max(resolved, sequence);
                }
                group.Steps.RemoveAll(delegate(RgbRecoveryStep step) { return step.Sequence <= resolved; });
                if (group.Steps.Count == 0) continue;
                // Validate every retained chain even when its last confirmed
                // operation restored the original; corruption never opens writes.
                RgbRecoveryDecision valid = RgbRecoveryDecision.Assess(group.Original, group.Original, group.Steps);
                if (!valid.AtOriginal) throw new InvalidDataException(valid.Error ?? "Invalid lighting recovery history.");
                RgbRecoveryStep last = group.Steps[group.Steps.Count - 1];
                if (last.Confirmed != null && Tk75RgbExchange.Equivalent(last.Confirmed, group.Original)) continue;
                if (pending != null) throw new InvalidDataException("Several unfinished original lighting backups exist; automatic changes are paused.");
                pending = group;
            }
            return pending;
        }
        static void ResolveRgbAtOriginal(RgbBackupWork work, Tk75RgbSnapshot current)
        {
            if (work.Steps.Count == 0) return;
            var resolution = new Dictionary<string, object> {
                { "SchemaVersion", 2 }, { "IdentitySha256", work.IdentityHash }, { "OriginalBackup", Path.GetFileName(work.BackupFile) },
                { "ResolvedThroughSequence", work.Sequence }, { "CompletedUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) }
            };
            PutRgbSnapshot(resolution, "Confirmed", current);
            WriteRgbJournal(work.Prefix + ".resolved-" + work.Sequence.ToString("D8", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".json", resolution);
            work.Steps.Clear();
        }
        static void InitializeRgbWork(RgbBackupWork work)
        {
            string prefix = null;
            Dictionary<string, object> journal = null;
            try
            {
                RequireRgbOrdinaryPath(work.Root);
                string directory = Path.Combine(Path.GetFullPath(work.Root), "lighting");
                Directory.CreateDirectory(directory); RequireRgbOrdinaryPath(directory);
                string identityHash;
                Dictionary<string, object> identity = RgbDeviceIdentity(work.Reader, work.Model, out identityHash);
                work.Identity = identity; work.IdentityHash = identityHash;
                // Journals are immutable. A read-only retry needs its own
                // request identity rather than overwriting the failed request.
                string requestId = work.InitAttempts > 1 ? Guid.NewGuid().ToString("N") : work.RequestId;
                // The full identity stays in the filename; the request GUID is
                // encoded losslessly without padding. Time and the ordinary GUID
                // remain in the JSON. Avoid exhausting MAX_PATH in a normal ZIP
                // extraction directory before later transaction/resolution files.
                string fileToken = Convert.ToBase64String(new Guid(requestId).ToByteArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                prefix = Path.Combine(directory, "rgb-" + identityHash + "-" + fileToken);
                // Validate the longest future journal (including atomic-write
                // staging) before any initial read or readiness publication.
                RequireRgbJournalPath(prefix + ".resolved-" + Int32.MaxValue.ToString(CultureInfo.InvariantCulture) + "-" + new string('0', 32) + ".json");
                journal = new Dictionary<string, object> {
                    { "SchemaVersion", 1 }, { "RequestId", requestId }, { "StartedUtc", work.StartedUtc.ToString("o", CultureInfo.InvariantCulture) },
                    { "Operation", "ReadOnlyLightingBackup" }, { "Layer", RgbBackupLayer }, { "Identity", identity },
                    { "IdentitySha256", identityHash }, { "HardwareWritesPerformed", false }, { "Phase", "Requested" }
                };
                WriteRgbJournal(prefix + ".request.json", journal);
                // ReaderSession releases its short gate before waiting and
                // rejects a replaced/disposed source after the response.
                Tk75RgbSnapshot snapshot;
                try { snapshot = work.Reader.ReadRgbSnapshot(RgbBackupLayer, RgbOperationTimeoutMilliseconds); }
                catch (Exception ex)
                {
                    // Only the initial, entirely read-only request may retry.
                    // Invalid data, identity/recovery decisions and disk failures
                    // must remain stopped until their cause is resolved.
                    lock (work.Gate) work.InitRetryEligible = ex is TimeoutException ||
                        ex is IOException && !(ex is InvalidDataException) && !(ex is MonitorCleanupException);
                    throw;
                }
                if (snapshot.ModelId != work.Model || snapshot.Layer != RgbBackupLayer)
                    throw new InvalidDataException("The lighting response belongs to a different model or layer.");
                string afterHash; RgbDeviceIdentity(work.Reader, work.Model, out afterHash);
                if (afterHash != identityHash || work.Reader.DeviceModelId != work.Model)
                    throw new InvalidDataException("The keyboard identity changed during lighting backup.");
                RgbRecoveryGroup recovery = FindRgbRecovery(work, directory);
                if (recovery != null)
                {
                    journal["Phase"] = "RecoveryObserved";
                    journal["OriginalBackup"] = Path.GetFileName(recovery.BackupFile);
                    PutRgbSnapshot(journal, "Observed", snapshot);
                    WriteRgbJournal(prefix + ".recovery-observed.json", journal);
                    work.Prefix = recovery.Prefix; work.BackupFile = recovery.BackupFile; work.Original = recovery.Original;
                    work.Current = snapshot; work.Sequence = recovery.Sequence; work.Steps.AddRange(recovery.Steps);
                    RgbRecoveryDecision decision = RgbRecoveryDecision.Assess(work.Original, snapshot, work.Steps);
                    if (decision.AtOriginal) ResolveRgbAtOriginal(work, snapshot);
                    lock (work.Gate)
                    {
                        work.RecoveryRequired = decision.NeedsRecovery; work.Error = decision.CanRestore || decision.AtOriginal ? null : decision.Error;
                        work.Applied = !decision.AtOriginal; work.State = 1;
                        // A matching unfinished transaction belongs to this
                        // saved override. Restore it through the usual journaled
                        // exchange before planning any new controller colors.
                        if (decision.NeedsRecovery && decision.CanRestore)
                        { work.Desired = work.Original; work.RestoreRequested = true; }
                    }
                    return;
                }
                byte[] encoded = Tk75RgbProtocol.EncodeSnapshot(snapshot);
                // Verify that the stored representation is independently
                // decodable before publishing the immutable completed backup.
                Tk75RgbSnapshot decoded = Tk75RgbProtocol.DecodeSnapshot(encoded);
                if (decoded.ModelId != snapshot.ModelId || decoded.Profile != snapshot.Profile || decoded.Layer != snapshot.Layer ||
                    !Tk75RgbProtocol.Equal(decoded.RawSettings, snapshot.RawSettings) || !Tk75RgbProtocol.Equal(decoded.Picture, snapshot.Picture))
                    throw new InvalidDataException("The lighting backup representation failed its integrity check.");
                journal["Phase"] = "Complete"; journal["CompletedUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                journal["SnapshotFormat"] = "Tk75RgbProtocol-v1"; journal["SnapshotBytes"] = encoded.Length;
                journal["SnapshotSha256"] = RgbHash(encoded); journal["SnapshotBase64"] = Convert.ToBase64String(encoded);
                journal["BackupOnly"] = true;
                WriteRgbJournal(prefix + ".backup.json", journal);
                lock (work.Gate) { work.Prefix = prefix; work.BackupFile = prefix + ".backup.json"; work.Original = snapshot; work.Current = snapshot; work.State = 1; }
            }
            catch (Exception ex)
            {
                work.Error = BoundedRgbError(ex);
                if (prefix != null && journal != null)
                {
                    try { journal["Phase"] = "Failed"; journal["Error"] = work.Error; WriteRgbJournal(prefix + ".failure.json", journal); }
                    catch (Exception logError) { work.Error += " / " + BoundedRgbError(logError); }
                }
                lock (work.Gate) work.State = 2;
                throw;
            }
        }
        static void RunRgbTransaction(RgbBackupWork work, Tk75RgbSnapshot desired, bool restore)
        {
            RequireRgbIdentity(work); Tk75RgbSnapshot expected = work.Current;
            if (restore)
            {
                expected = work.Reader.ReadRgbSnapshot(RgbBackupLayer, RgbOperationTimeoutMilliseconds); RequireRgbIdentity(work);
                // A failed retry must describe the newly observed context too.
                // Readiness remains closed until assessment and restore succeed.
                lock (work.Gate) work.Current = expected;
                RgbRecoveryDecision recovery = RgbRecoveryDecision.Assess(work.Original, expected, work.Steps);
                if (recovery.AtOriginal)
                {
                    ResolveRgbAtOriginal(work, expected);
                    lock (work.Gate) { work.Current = expected; work.RecoveryRequired = false; work.Applied = false; work.Error = null; work.State = 1; }
                    return;
                }
                if (!recovery.CanRestore) throw new InvalidDataException(recovery.Error ?? "Current keyboard lighting does not match the saved transaction; no unrelated changes were overwritten.");
                desired = work.Original;
            }
            if (Tk75RgbExchange.Equivalent(expected, desired)) return;
            Tk75RgbExchange.Validate(expected, desired);
            int sequence = checked(work.Sequence + 1); string id = Guid.NewGuid().ToString("N");
            // The original backup prefix already contains a unique request ID.
            // Repeating another GUID made the confirmation's temporary path
            // exceed Windows MAX_PATH after the device had already been changed.
            string path = work.Prefix + ".tx-" + sequence.ToString("D8", CultureInfo.InvariantCulture) + ".transaction.json";
            RequireRgbJournalPath(path + ".confirmed.json");
            RequireRgbJournalPath(path + ".failed.json");
            var transaction = new Dictionary<string, object> {
                { "SchemaVersion", 2 }, { "Operation", "CompareExchangeLighting" }, { "IdentitySha256", work.IdentityHash },
                { "OriginalBackup", Path.GetFileName(work.BackupFile) }, { "OriginalSnapshotSha256", RgbHash(Tk75RgbProtocol.EncodeSnapshot(work.Original)) },
                { "Sequence", sequence }, { "RequestId", id }, { "StartedUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) }
            };
            bool automaticRestore = work.Reader.RgbAutomaticRestoreAvailable;
            transaction["AutomaticRestore"] = automaticRestore;
            PutRgbSnapshot(transaction, "Expected", expected); PutRgbSnapshot(transaction, "Desired", desired);
            WriteRgbJournal(path, transaction); // Durable publication precedes ANY hardware write.
            var step = new RgbRecoveryStep { Sequence = sequence, Expected = expected, Desired = desired, AutomaticRestore = automaticRestore };
            work.Sequence = sequence; work.Steps.Add(step); RequireRgbIdentity(work);
            Tk75RgbSnapshot confirmed;
            try { confirmed = work.Reader.CompareExchangeRgb(expected, desired, automaticRestore ? work.Original : null, RgbOperationTimeoutMilliseconds); RequireRgbIdentity(work); }
            catch (Exception error)
            {
                try { WriteRgbJournal(path + ".failed.json", new Dictionary<string, object> {
                    { "TransactionFile", Path.GetFileName(path) }, { "Error", BoundedRgbError(error) }, { "Utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) }
                }); } catch (Exception) { } // The durable request remains the recovery authority.
                throw;
            }
            if (!Tk75RgbExchange.Equivalent(confirmed, desired)) throw new InvalidDataException("Lighting update was not confirmed; recovery journal retained.");
            var completion = new Dictionary<string, object> {
                { "SchemaVersion", 2 }, { "IdentitySha256", work.IdentityHash }, { "OriginalBackup", Path.GetFileName(work.BackupFile) },
                { "TransactionFile", Path.GetFileName(path) }, { "CompletedUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) }
            };
            PutRgbSnapshot(completion, "Confirmed", confirmed); WriteRgbJournal(path + ".confirmed.json", completion);
            step.Confirmed = confirmed;
            lock (work.Gate) { work.Current = confirmed; work.RecoveryRequired = false; work.Error = null; work.State = 1; work.Applied = !Tk75RgbExchange.Equivalent(confirmed, work.Original); }
        }
        static void InitializeRgbWithRetry(RgbBackupWork work)
        {
            while (true)
            {
                lock (work.Gate) { work.InitAttempts++; work.InitRetryEligible = false; work.State = 0; work.Error = null; }
                try { InitializeRgbWork(work); return; }
                catch
                {
                    bool retry;
                    lock (work.Gate)
                    {
                        retry = work.InitRetryEligible && work.InitAttempts < RgbInitializationMaximumAttempts &&
                            work.Original == null && work.BackupFile == null && work.Steps.Count == 0 && !work.Abort && !work.QuitAfterRestore;
                    }
                    if (!retry || !work.Reader.IsReading || !work.Reader.RgbReadAvailable) throw;
                    int delay = RgbInitializationRetryBaseMilliseconds * (1 << (work.InitAttempts - 1));
                    var elapsed = System.Diagnostics.Stopwatch.StartNew();
                    lock (work.Gate)
                    {
                        work.State = 0; work.Error = null;
                        while (!work.Abort && !work.QuitAfterRestore && elapsed.ElapsedMilliseconds < delay)
                            Monitor.Wait(work.Gate, Math.Max(1, delay - (int)elapsed.ElapsedMilliseconds));
                        if (work.Abort || work.QuitAfterRestore) throw;
                    }
                    if (!work.Reader.IsReading || !work.Reader.RgbReadAvailable) throw;
                }
            }
        }
        static void RunRgbWorkQueue(RgbBackupWork work)
        {
            while (true)
            {
                Tk75RgbSnapshot desired; bool restore, retryRestore = false;
                lock (work.Gate)
                {
                    work.Busy = false; Monitor.PulseAll(work.Gate);
                    while (!work.Abort && work.Desired == null && !work.RestoreRequested && !work.QuitAfterRestore)
                        Monitor.Wait(work.Gate);
                    if (work.Abort) return;
                    restore = work.RestoreRequested || work.QuitAfterRestore;
                    desired = restore ? work.Original : work.Desired; work.Desired = null; work.RestoreRequested = false; work.Busy = true;
                    if (restore && work.QuitAfterRestore) work.CloseRestoreAttempts++;
                }
                try
                {
                    if (work.Reader.RgbWriteAvailable && desired != null) RunRgbTransaction(work, desired, restore);
                    else if (restore && work.Applied) throw new IOException("The keyboard is unavailable; reconnect it to restore the saved lighting.");
                }
                catch (Exception ex)
                {
                    lock (work.Gate)
                    {
                        work.Error = BoundedRgbError(ex); work.State = 2; work.RecoveryRequired = work.Steps.Count != 0 || work.Applied;
                        // An uncertain write stops ordinary color updates, but
                        // may not discard a newer manual/shutdown restore request.
                        if (!work.RestoreRequested && !work.QuitAfterRestore) work.Desired = null;
                        // One fresh-read retry can reconcile a lost write/readback
                        // response. It never retries invalid identities or data.
                        retryRestore = restore && work.QuitAfterRestore && work.CloseRestoreAttempts < 2 &&
                            (ex is TimeoutException || ex is IOException && !(ex is InvalidDataException) && !(ex is MonitorCleanupException)) &&
                            !work.Abort && work.Reader.RgbReadAvailable;
                        if (retryRestore) work.RestoreRequested = true;
                    }
                }
                // A close request can arrive while an ordinary write is running.
                // Only the following restore iteration can complete that request.
                lock (work.Gate) if (work.QuitAfterRestore && restore && !retryRestore) return;
            }
        }
        static void RunRgbWorker(RgbBackupWork work)
        {
            try { InitializeRgbWithRetry(work); RunRgbWorkQueue(work); }
            catch (Exception ex) { lock (work.Gate) { work.Error = BoundedRgbError(ex); work.State = 2; } }
            finally { lock (work.Gate) { work.Busy = false; work.Stopped = true; Monitor.PulseAll(work.Gate); } }
        }
        void RestoreRgbBeforeDisconnect(ReaderSession source, int timeoutMs)
        {
            RgbBackupWork work = rgbBackupWork; if (work == null || !Object.ReferenceEquals(work.Reader, source)) return;
            RestoreRgbWorkBeforeDisconnect(work, timeoutMs);
        }
        static bool RestoreRgbWorkBeforeDisconnect(RgbBackupWork work, int timeoutMs)
        {
            if (work == null) return true;
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            lock (work.Gate)
            {
                work.QuitAfterRestore = true; work.RestoreRequested = true; work.Desired = work.Original; Monitor.PulseAll(work.Gate);
                while (!work.Stopped && deadline.ElapsedMilliseconds < timeoutMs) Monitor.Wait(work.Gate, Math.Max(1, timeoutMs - (int)deadline.ElapsedMilliseconds));
                if (!work.Stopped) { work.Abort = true; work.Desired = null; Monitor.PulseAll(work.Gate); }
                return work.Original == null || !(work.Applied || work.RecoveryRequired || !work.Stopped);
            }
        }
        bool BeginRgbCloseRestore()
        {
            if (rgbCloseFinished) return false;
            if (rgbClosePending) return true;
            RgbBackupWork work = rgbBackupWork; ReaderSession source = reader;
            bool restoreLighting = work != null && source != null && Object.ReferenceEquals(work.Reader, source);
            SetClosePhase(2, restoreLighting ? ShutdownPhaseState.Running : ShutdownPhaseState.Completed);
            System.Threading.Tasks.Task connections = runtime.CancelPendingConnections();
            if (!restoreLighting && connections.IsCompleted) return false;
            rgbClosePending = true; Enabled = false; uiTimer.Stop(); StopDeviceDiscovery();
            SetClosePhase(3, ShutdownPhaseState.Running);
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool restored = false;
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    if (restoreLighting) RestoreRgbBeforeDisconnect(source, RgbCloseTimeoutMilliseconds);
                    string restoreFailure = null;
                    if (restoreLighting)
                        lock (work.Gate)
                            if (work.Original != null && (work.Applied || work.RecoveryRequired || !work.Stopped))
                                restoreFailure = work.Error ?? "Lighting restoration did not finish before closing.";
                    if (restoreFailure != null) LogShutdownFailure("Lighting restoration incomplete; original backup retained", new IOException(restoreFailure));
                    SetClosePhase(2, restoreFailure == null ? ShutdownPhaseState.Completed : ShutdownPhaseState.Failed);
                    bool connectionsFinished = connections.Wait(Math.Max(0, NormalCloseTimeoutMilliseconds - (int)elapsed.ElapsedMilliseconds));
                    if (!connectionsFinished)
                    {
                        SetClosePhase(3, ShutdownPhaseState.Failed);
                        LogShutdownFailure("Pending controller cleanup; startup recovery remains unconfirmed", new TimeoutException("Cleanup did not finish before closing."));
                    }
                    restored = restoreFailure == null && connectionsFinished;
                }
                catch (Exception error) { SetClosePhase(2, ShutdownPhaseState.Failed); LogShutdownFailure("Lighting restoration incomplete; original backup retained", error); }
                finally
                {
                    try { BeginInvoke((Action)delegate { if (closing || IsDisposed) return; rgbCloseSucceeded = restored; rgbCloseFinished = true; rgbClosePending = false; Enabled = true; Close(); }); }
                    catch (InvalidOperationException) { }
                }
            });
            return true;
        }
        bool DeferRgbReconnect()
        {
            RgbBackupWork work = rgbBackupWork; ReaderSession source = reader;
            if (source == null || work == null || !Object.ReferenceEquals(work.Reader, source)) return false;
            DetachRemovedReader(source);
            SetDiscoveryStatus("Verbindung wird neu geöffnet …", "Reopening keyboard connection …"); return true;
        }
    }
}
