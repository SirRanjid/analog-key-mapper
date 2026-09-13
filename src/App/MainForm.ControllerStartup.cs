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
        bool startupReconnectPending, startupReconnectSaved;
        bool startupReconnectBusy;
        int startupReconnectGeneration;
        CancellationTokenSource startupReconnectCancellation;
        Queue<string> startupReconnectQueue;
        string startupReconnectProfile;
        object startupReconnectSnapshot;
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
                controllerStartup = controllerReconnectStore.StartupSnapshot ?? controllerReconnectStore.Preferences;
                if (controllerReconnectStore.RecoveryRequired)
                    ReportControllerReconnectNotice(Tr("Die letzte Controller-Sitzung wurde nicht bestätigt. Bitte manuell verbinden; die Startoption bleibt gespeichert.",
                        "The last controller session was not confirmed. Connect manually; your startup preference is still saved."), null);
                // BeginSession has already consumed the last clean exit. Legacy,
                // interrupted, or mismatched records never reach this path.
                if (controllerReconnectStore.StartupSnapshot != null && controllerStartup.ProfileFile != null)
                {
                    string savedProfile = store.RequireLocalProfile(controllerStartup.ProfileFile);
                    if (File.Exists(savedProfile) && savedProfile != profilePath) LoadProfile(savedProfile);
                    startupReconnectPending = File.Exists(savedProfile);
                    if (startupReconnectPending)
                    {
                        startupReconnectProfile = profilePath;
                        startupReconnectSnapshot = history.SnapshotToken;
                    }
                    if (!startupReconnectPending)
                        ReportControllerReconnectNotice(Tr("Das gespeicherte Startprofil fehlt. Bitte Profil wählen und Controller manuell verbinden.",
                            "The saved startup profile is missing. Choose a profile and connect controllers manually."), null);
                }
            }
            catch (Exception ex)
            {
                controllerStartup = controllerReconnectStore == null ? new ControllerReconnectSettings() : controllerReconnectStore.Preferences;
                CancelStartupReconnect();
                ReportControllerReconnectNotice(Tr("Der Controller-Startstatus konnte nicht sicher gespeichert werden. Automatisches Verbinden ist für diese Sitzung pausiert; bitte manuell verbinden.",
                    "Controller startup state could not be saved safely. Automatic connection is paused for this session; connect manually."), ex);
            }
        }

        void AddReconnectStartupItem(ContextMenuStrip menu)
        {
            var item = new ToolStripMenuItem(Tr("Controller beim Start wiederverbinden", "Reconnect controllers at startup"));
            item.Enabled = !previewMode;
            menu.Items.Add(item);
            var notice = new ToolStripMenuItem { Enabled = false, Visible = false };
            controllerReconnectNotices.Add(notice); menu.Items.Add(notice);
            RefreshControllerReconnectNotices();
            menu.Opening += delegate
            {
                item.Checked = controllerStartup.Enabled;
                item.ToolTipText = Tr("Optional: die beim letzten Beenden verbundenen Controller einmalig wiederverbinden, sobald die Tastatur bereit ist.",
                    "Optional: reconnect the controllers connected at the last exit, once the keyboard is ready.");
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
            if (previewMode || startupReconnectSaved || !controllerStartup.Enabled || controllerReconnectStore == null || !controllerReconnectStore.SessionReady) return;
            CancelStartupReconnect();
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
                ReportControllerReconnectNotice(Tr("Die verbundenen Controller konnten nicht für den nächsten Start gespeichert werden. Beim nächsten Start bitte manuell verbinden.",
                    "Connected controllers could not be saved for the next startup. Connect manually next time."), ex);
            }
        }

        void CancelControllerReconnectSave()
        {
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
                ReportControllerReconnectNotice(Tr("Der saubere Controller-Abschluss konnte nicht bestätigt werden. Beim nächsten Start bitte manuell verbinden.",
                    "The clean controller shutdown could not be confirmed. Connect manually next time."), ex);
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

        void CancelStartupReconnect()
        {
            ++startupReconnectGeneration;
            var cancellation = startupReconnectCancellation;
            startupReconnectPending = false;
            startupReconnectQueue = null;
            startupReconnectProfile = null;
            startupReconnectSnapshot = null;
            if (cancellation != null) cancellation.Cancel();
        }

        async void TryReconnectStartupControllers()
        {
            if (!startupReconnectPending && startupReconnectQueue == null) return;
            if (!controllerStartup.Enabled || closing || rgbClosePending || deviceDetachInProgress)
            { CancelStartupReconnect(); return; }
            // A numeric/property edit can still be a UI draft, so its profile
            // snapshot may not have changed yet. Do not let the connection
            // wrapper flush that edit and automatically use the new settings.
            if (inputDirty || settings.IsCurrentCellDirty || startupReconnectProfile != profilePath || !Object.ReferenceEquals(startupReconnectSnapshot, history.SnapshotToken))
            { CancelStartupReconnect(); return; }
            if (!LiveInputReading || !LiveInputSamples)
            {
                // Waiting for the first keyboard is intentional. Losing input
                // after connection started cancels the remaining startup work.
                if (startupReconnectQueue != null) CancelStartupReconnect();
                return;
            }
            if (startupReconnectPending)
            {
                startupReconnectPending = false;
                startupReconnectQueue = new Queue<string>(controllerStartup.Targets(Path.GetFileName(profilePath), UiReadProfile));
            }
            if (startupReconnectQueue == null || (!startupReconnectBusy && startupReconnectQueue.Count == 0) ||
                startupReconnectProfile != profilePath || !Object.ReferenceEquals(startupReconnectSnapshot, history.SnapshotToken))
            { CancelStartupReconnect(); return; }
            if (startupReconnectBusy) return;
            // Exactly one asynchronous connection can be pending. Cancellation
            // owns only this startup request, leaving manual peers untouched.
            string id = startupReconnectQueue.Dequeue();
            int generation = startupReconnectGeneration;
            var cancellation = new CancellationTokenSource();
            startupReconnectCancellation = cancellation; startupReconnectBusy = true;
            try
            {
                var definition = ControllerRouting.ForController(UiReadProfile, id);
                string error = ControllerOutputs.AvailabilityError(definition.Controller);
                if (error != null) throw new InvalidOperationException(UiText.Get(error));
                if (!runtime.IsControllerEnabled(id)) await RequestControllerConnectionAsync(id, true, cancellation.Token);
            }
            catch (OperationCanceledException) { if (generation == startupReconnectGeneration) CancelStartupReconnect(); }
            catch (Exception ex)
            {
                if (generation == startupReconnectGeneration && !IsDisposed && !closing)
                {
                    CancelStartupReconnect();
                    ReportControllerReconnectNotice(Tr("Ein Controller konnte nicht automatisch verbunden werden. Die Startsequenz wurde gestoppt; bitte manuell verbinden.",
                        "A controller could not reconnect automatically. The startup sequence stopped; connect manually."), ex);
                }
            }
            finally
            {
                if (Object.ReferenceEquals(startupReconnectCancellation, cancellation)) startupReconnectCancellation = null;
                startupReconnectBusy = false; cancellation.Dispose();
            }
            if (generation != startupReconnectGeneration || IsDisposed || closing) return;
            if (startupReconnectQueue != null && startupReconnectQueue.Count == 0) CancelStartupReconnect();
            RefreshKeyboardSuppression(false);
        }
    }
}
