using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Tk75.Mapping;
using Tk75.Output;

namespace Tk75.App
{
    public sealed partial class MainForm
    {
        ControllerReconnectSettings controllerStartup = new ControllerReconnectSettings();
        ControllerReconnectSettings shutdownControllerStartup;
        ControllerReconnectStore controllerReconnectStore;
        bool startupReconnectSaved, controllerReconnectReady, controllerReconnectSuspended, controllerReconnectClosing;
        bool startupReconnectBusy;
        int controllerReconnectPauseDepth;
        int startupReconnectGeneration;
        CancellationTokenSource startupReconnectCancellation;
        readonly ControllerConnectionPolicy controllerConnectionPolicy = new ControllerConnectionPolicy();
        string startupReconnectController;
        readonly Dictionary<string, string> controllerReconnectErrors = new Dictionary<string, string>();
        string controllerReconnectNotice;
        readonly List<ToolStripMenuItem> controllerReconnectNotices = new List<ToolStripMenuItem>();

        void ConfigureControllerReconnectStartup()
        {
            try
            {
                controllerReconnectStore = new ControllerReconnectStore(
                    delegate(string file) { string path = Path.Combine(store.Root, file); return File.Exists(path) ? File.ReadAllText(path) : null; },
                    delegate(string file, string content) { WorkspaceStore.WriteAtomic(Path.Combine(store.Root, file), content); });
                controllerReconnectStore.BeginSession();
                controllerStartup = controllerReconnectStore.Preferences;
                // Resolve current configured routes, not a list of devices from
                // the last exit. Interrupted exits do not disable this preference.
                if (controllerStartup.ProfileFile != null)
                {
                    string savedProfile = store.RequireLocalProfile(controllerStartup.ProfileFile);
                    if (File.Exists(savedProfile) && savedProfile != profilePath) LoadProfile(savedProfile);
                }
                controllerReconnectReady = true;
                ConfigureControllerConnections();
            }
            catch (Exception ex)
            {
                controllerReconnectReady = false;
                controllerStartup = controllerReconnectStore == null ? new ControllerReconnectSettings() : controllerReconnectStore.Preferences;
                CancelStartupReconnect();
                ReportControllerReconnectNotice(Tr("Der Controller-Startstatus konnte nicht sicher gespeichert werden. Automatisches Verbinden ist für diese Sitzung pausiert; bitte manuell verbinden.",
                    "Controller startup state could not be saved safely. Automatic connection is paused for this session; connect manually."), ex);
            }
        }

        void AddReconnectStartupItem(ContextMenuStrip menu)
        {
            var item = new ToolStripMenuItem(Tr("Controller automatisch verbinden", "Connect controllers automatically"));
            item.Enabled = !previewMode;
            menu.Items.Add(item);
            var notice = new ToolStripMenuItem { Enabled = false, Visible = false };
            controllerReconnectNotices.Add(notice); menu.Items.Add(notice);
            RefreshControllerReconnectNotices();
            menu.Opening += delegate
            {
                item.Checked = controllerStartup.Enabled;
                item.ToolTipText = Tr("Konfigurierte Controller beim Start verbinden und bei Verbindungsverlust erneut versuchen. Manuelles Trennen bleibt wirksam.",
                    "Connect configured controllers at startup and retry lost connections. Manually disconnected controllers stay off.");
            };
            item.Click += delegate
            {
                CancelStartupReconnect();
                try
                {
                    if (controllerReconnectStore == null || !controllerReconnectStore.SessionReady)
                        throw new InvalidOperationException(Tr("Bitte Speicherzugriff prüfen und die App neu starten.", "Check access to the data folder and restart the app."));
                    controllerReconnectStore.SetEnabled(!controllerStartup.Enabled);
                    controllerStartup = controllerReconnectStore.Preferences;
                    ConfigureControllerConnections();
                    controllerReconnectNotice = null; RefreshControllerReconnectNotices();
                }
                catch (Exception ex)
                {
                    ReportControllerReconnectNotice(Tr("Die Wiederverbindungsoption konnte nicht gespeichert werden. Bitte manuell verbinden.",
                        "The reconnection option could not be saved. Connect controllers manually."), ex);
                }
                item.Checked = controllerStartup.Enabled;
            };
        }

        void SaveControllerReconnectState()
        {
            controllerReconnectClosing = true;
            CancelStartupReconnect();
            if (previewMode || startupReconnectSaved || !controllerStartup.Enabled || controllerReconnectStore == null || !controllerReconnectStore.SessionReady) return;
            // Capture before Disable detaches the controllers. Commit only once
            // the profile save/exit prompt has accepted closing the application.
            shutdownControllerStartup = ControllerReconnectSettings.Capture(true, Path.GetFileName(profilePath), UiReadProfile, runtime.ActiveControllerIds);
            startupReconnectSaved = true;
        }

        void PersistControllerReconnectState()
        {
            if (shutdownControllerStartup == null) return;
            var next = shutdownControllerStartup; shutdownControllerStartup = null;
            try { controllerReconnectStore.PrepareExit(next); }
            catch (Exception ex)
            {
                ReportControllerReconnectNotice(Tr("Das zuletzt verwendete Controller-Profil konnte nicht für den nächsten Start gespeichert werden.",
                    "The last controller profile could not be saved for the next startup."), ex);
            }
        }

        void CancelControllerReconnectSave()
        {
            controllerReconnectClosing = false;
            shutdownControllerStartup = null; startupReconnectSaved = false;
            if (controllerReconnectStore != null) controllerReconnectStore.CancelExit();
        }

        // Called only at the final accepted normal exit after a successful
        // profile save. Windows shutdown deliberately leaves the journal dirty.
        void ConfirmControllerReconnectExit()
        {
            if (previewMode || controllerReconnectStore == null || !controllerReconnectStore.SessionReady) return;
            try { controllerReconnectStore.ConfirmExit(); }
            catch (Exception ex)
            {
                ReportControllerReconnectNotice(Tr("Der Controller-Abschluss konnte nicht bestätigt werden. Die Startoption bleibt gespeichert.",
                    "Controller shutdown could not be confirmed. The startup preference is still saved."), ex);
            }
        }

        void ReportControllerReconnectNotice(string message, Exception error)
        {
            controllerReconnectNotice = message + (error == null ? "" : "\n" + UiText.Get(error.Message));
            store.Event(controllerReconnectNotice);
            RefreshControllerReconnectNotices();
            if (!IsDisposed && trayIcon != null && trayIcon.Visible)
                trayIcon.ShowBalloonTip(8000, "Analog Key Mapper", controllerReconnectNotice, ToolTipIcon.Info);
        }

        void RefreshControllerReconnectNotices()
        {
            foreach (var notice in controllerReconnectNotices)
            {
                if (notice.IsDisposed) continue;
                notice.Visible = controllerReconnectNotice != null;
                notice.Text = controllerReconnectNotice == null ? "" : controllerReconnectNotice.Split('\n')[0];
                notice.ToolTipText = controllerReconnectNotice ?? "";
                notice.AccessibleDescription = controllerReconnectNotice ?? "";
            }
        }

        void ConfigureControllerConnections()
        {
            if (controllerReconnectReady)
            {
                controllerConnectionPolicy.Configure(UiReadProfile, controllerStartup.Enabled);
                foreach (string id in new List<string>(controllerReconnectErrors.Keys))
                    if (!controllerConnectionPolicy.IsDesired(id)) ClearControllerReconnectError(id);
            }
        }

        void ClearControllerReconnectError(string id)
        {
            if (controllerReconnectErrors.Remove(id) && controllerReconnectErrors.Count == 0)
            { controllerReconnectNotice = null; RefreshControllerReconnectNotices(); }
        }

        // Cancels only in-flight work. Desired connections survive profile edits,
        // temporary input loss and suspend; explicit stops have their own path.
        void CancelStartupReconnect()
        {
            ++startupReconnectGeneration;
            var cancellation = startupReconnectCancellation;
            if (cancellation != null) cancellation.Cancel();
        }

        void StopControllerReconnect()
        {
            controllerConnectionPolicy.StopAll();
            CancelStartupReconnect();
            foreach (string id in new List<string>(controllerReconnectErrors.Keys)) ClearControllerReconnectError(id);
        }

        async void TryReconnectStartupControllers()
        {
            if (!controllerReconnectReady || startupReconnectBusy || closing || rgbClosePending || deviceDetachInProgress ||
                controllerReconnectClosing || controllerReconnectSuspended || controllerReconnectPauseDepth != 0 || previewMode || applicationResourcesDisposed) return;
            // Wait for drafts to be committed normally; background work must not
            // force a partly entered number into the live mapping configuration.
            // Ready event-driven keyboards may not have emitted a key value yet.
            // MappingSession already keeps never-observed inputs unavailable and
            // starts the device neutral, so no first key press is required here.
            if (inputDirty || settings.IsCurrentCellDirty || !LiveInputReading) return;
            string id = controllerConnectionPolicy.Next(DateTime.UtcNow, runtime.IsControllerEnabled, runtime.IsControllerConnecting);
            if (id == null) return;
            int generation = startupReconnectGeneration;
            var cancellation = new CancellationTokenSource();
            startupReconnectController = id;
            startupReconnectCancellation = cancellation; startupReconnectBusy = true;
            try
            {
                await RequestControllerConnectionAsync(id, true, cancellation.Token);
                if (generation == startupReconnectGeneration)
                {
                    controllerConnectionPolicy.Succeeded(id);
                    controllerReconnectErrors.Remove(id);
                    if (controllerReconnectErrors.Count == 0) { controllerReconnectNotice = null; RefreshControllerReconnectNotices(); }
                }
            }
            catch (OperationCanceledException)
            {
                if (generation == startupReconnectGeneration) controllerConnectionPolicy.Failed(id, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                if (generation == startupReconnectGeneration && !IsDisposed && !closing)
                {
                    controllerConnectionPolicy.Failed(id, DateTime.UtcNow);
                    string previous;
                    if (!controllerReconnectErrors.TryGetValue(id, out previous) || previous != ex.Message)
                    {
                        controllerReconnectErrors[id] = ex.Message;
                        ReportControllerReconnectNotice(Tr("Ein Controller ist noch nicht verbunden. Die Verbindung wird automatisch erneut versucht.",
                            "A controller is not connected yet. Connection will be retried automatically."), ex);
                    }
                }
            }
            finally
            {
                if (Object.ReferenceEquals(startupReconnectCancellation, cancellation))
                { startupReconnectCancellation = null; startupReconnectController = null; }
                startupReconnectBusy = false; cancellation.Dispose();
            }
            if (generation != startupReconnectGeneration || IsDisposed || closing) return;
            RefreshKeyboardSuppression(false);
        }
    }
}
