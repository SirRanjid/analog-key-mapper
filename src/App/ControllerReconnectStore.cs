using System;
using System.IO;
using System.Web.Script.Serialization;

namespace Tk75.App
{
    // I/O is injected so every write/readback failure and interrupted close can
    // be checked without a registry, UI, device, or helper process.
    internal sealed class ControllerReconnectStore
    {
        internal const string SettingsFile = "controller-startup.json";
        internal const string SessionFile = "controller-startup-session.json";
        readonly Func<string, string> read;
        readonly Action<string, string> write;
        string sessionId;
        string preparedSettings;
        bool prepared;
        internal ControllerReconnectSettings Preferences { get; private set; }
        internal ControllerReconnectSettings StartupSnapshot { get; private set; }
        internal bool RecoveryRequired { get; private set; }
        internal bool SessionReady { get { return sessionId != null; } }

        internal ControllerReconnectStore(Func<string, string> read, Action<string, string> write)
        {
            if (read == null || write == null) throw new ArgumentNullException();
            this.read = read; this.write = write;
            Preferences = new ControllerReconnectSettings();
        }

        internal void BeginSession()
        {
            if (SessionReady) throw new InvalidOperationException("Controller startup session was already initialized.");
            string settingsJson = read(SettingsFile);
            var settings = settingsJson == null ? new ControllerReconnectSettings() : ControllerReconnectSettings.Parse(settingsJson);
            Preferences = new ControllerReconnectSettings { Enabled = settings.Enabled };
            Session previous = null;
            try { previous = ParseSession(read(SessionFile)); }
            catch (InvalidDataException) { }
            bool trusted = previous != null && previous.Completed && settings.SessionId == previous.Id;
            string nextId = Guid.NewGuid().ToString("N");
            // Consume the prior clean exit durably BEFORE exposing even an
            // in-memory target list. Old settings can survive a failed save,
            // but they cannot match this uncompleted session on the next run.
            WriteSession(nextId, false);
            sessionId = nextId;
            StartupSnapshot = trusted && settings.Enabled ? Clone(settings) : null;
            RecoveryRequired = settings.Enabled && !trusted &&
                (previous != null || settings.SessionId != null || settings.Controllers.Count != 0);
        }

        internal void SetEnabled(bool enabled)
        {
            prepared = false; StartupSnapshot = null;
            RequireSession();
            var next = new ControllerReconnectSettings { Enabled = enabled };
            WriteSettings(next);
            Preferences = next; RecoveryRequired = false;
        }

        internal void PrepareExit(ControllerReconnectSettings snapshot)
        {
            prepared = false; RequireSession();
            if (snapshot == null) throw new ArgumentNullException("snapshot");
            var next = Clone(snapshot);
            next.Enabled = Preferences.Enabled;
            if (!next.Enabled) { next.ProfileFile = null; next.Controllers.Clear(); }
            next.SessionId = sessionId;
            preparedSettings = WriteSettings(next);
            prepared = true;
        }

        internal void CancelExit() { prepared = false; }

        internal void ConfirmExit()
        {
            if (!prepared) return;
            // The caller invokes this only after a successful profile save and
            // accepted normal close, never for Windows logoff/shutdown.
            prepared = false;
            RequireSession();
            if (!String.Equals(read(SettingsFile), preparedSettings, StringComparison.Ordinal))
                throw new IOException("Controller startup preferences changed before shutdown was confirmed.");
            WriteSession(sessionId, true);
        }

        string WriteSettings(ControllerReconnectSettings settings)
        {
            string json = settings.Serialize(); write(SettingsFile, json);
            if (!String.Equals(read(SettingsFile), json, StringComparison.Ordinal))
                throw new IOException("Controller startup preferences could not be verified after saving.");
            return json;
        }

        void WriteSession(string id, bool completed)
        {
            string json = new JavaScriptSerializer().Serialize(new Session { Id = id, Completed = completed });
            write(SessionFile, json);
            var actual = ParseSession(read(SessionFile));
            if (actual == null || actual.Id != id || actual.Completed != completed)
                throw new IOException("Controller startup session could not be verified after saving.");
        }

        static Session ParseSession(string json)
        {
            if (json == null) return null;
            try
            {
                var session = new JavaScriptSerializer { MaxJsonLength = 4096, RecursionLimit = 8 }.Deserialize<Session>(json);
                if (session == null || !ControllerReconnectSettings.ValidSessionId(session.Id))
                    throw new InvalidDataException("Invalid controller startup session.");
                return session;
            }
            catch (ArgumentException ex) { throw new InvalidDataException("Invalid controller startup session.", ex); }
            catch (InvalidOperationException ex) { throw new InvalidDataException("Invalid controller startup session.", ex); }
        }

        static ControllerReconnectSettings Clone(ControllerReconnectSettings value)
        { return ControllerReconnectSettings.Parse(value.Serialize()); }

        void RequireSession()
        {
            if (!SessionReady) throw new InvalidOperationException("Controller startup session is unavailable; connect controllers manually.");
            var current = ParseSession(read(SessionFile));
            if (current == null || current.Id != sessionId || current.Completed)
                throw new InvalidOperationException("Controller startup session changed; connect controllers manually.");
        }

        sealed class Session
        {
            public string Id;
            public bool Completed;
        }
    }
}
