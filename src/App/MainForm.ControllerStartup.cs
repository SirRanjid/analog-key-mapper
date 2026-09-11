using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using Tk75.Mapping;
using Tk75.Output;

namespace Tk75.App
{
    public sealed partial class MainForm
    {
        ControllerReconnectSettings controllerStartup = new ControllerReconnectSettings();
        ControllerReconnectSettings shutdownControllerStartup;
        bool startupReconnectPending, startupReconnectSaved;
        Queue<string> startupReconnectQueue;
        string startupReconnectProfile;
        object startupReconnectSnapshot;
        string ControllerStartupPath { get { return Path.Combine(store.Root, "controller-startup.json"); } }

        void ConfigureControllerReconnectStartup()
        {
            try
            {
                if (File.Exists(ControllerStartupPath)) controllerStartup = ControllerReconnectSettings.Parse(File.ReadAllText(ControllerStartupPath));
                // The remembered profile is loaded only with the explicit option.
                if (controllerStartup.Enabled && controllerStartup.ProfileFile != null)
                {
                    string savedProfile = store.RequireLocalProfile(controllerStartup.ProfileFile);
                    if (File.Exists(savedProfile) && savedProfile != profilePath) LoadProfile(savedProfile);
                    startupReconnectPending = File.Exists(savedProfile);
                }
            }
            catch (Exception ex) { controllerStartup = new ControllerReconnectSettings(); CancelStartupReconnect(); store.Event("Controller startup preferences left unchanged: " + ex.Message); }
        }

        void AddReconnectStartupItem(ContextMenuStrip menu)
        {
            var item = new ToolStripMenuItem(Tr("Controller beim Start wiederverbinden", "Reconnect controllers at startup"));
            item.Enabled = !previewMode;
            menu.Items.Add(item);
            menu.Opening += delegate
            {
                item.Checked = controllerStartup.Enabled;
                item.ToolTipText = Tr("Optional: die beim letzten Beenden verbundenen Controller einmalig wiederverbinden, sobald die Tastatur bereit ist.",
                    "Optional: reconnect the controllers connected at the last exit, once the keyboard is ready.");
            };
            item.Click += delegate
            {
                Attempt(delegate
                {
                    var next = ControllerReconnectSettings.Capture(!controllerStartup.Enabled, Path.GetFileName(profilePath), UiReadProfile, runtime.ActiveControllerIds);
                    WorkspaceStore.WriteAtomic(ControllerStartupPath, next.Serialize());
                    controllerStartup = next; CancelStartupReconnect(); item.Checked = next.Enabled;
                });
            };
        }

        void SaveControllerReconnectState()
        {
            if (previewMode || startupReconnectSaved || !controllerStartup.Enabled) return;
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
            try { WorkspaceStore.WriteAtomic(ControllerStartupPath, next.Serialize()); controllerStartup = next; }
            catch (Exception ex) { store.Event("Could not remember connected controllers: " + ex.Message); }
        }

        void CancelControllerReconnectSave()
        { shutdownControllerStartup = null; startupReconnectSaved = false; }

        void CancelStartupReconnect()
        {
            startupReconnectPending = false;
            startupReconnectQueue = null;
            startupReconnectProfile = null;
            startupReconnectSnapshot = null;
        }

        void TryReconnectStartupControllers()
        {
            if (!startupReconnectPending && startupReconnectQueue == null) return;
            if (!controllerStartup.Enabled || closing || rgbClosePending || deviceDetachInProgress)
            { CancelStartupReconnect(); return; }
            if (reader == null || !reader.IsReading || !reader.HasReceivedSamples)
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
                startupReconnectProfile = profilePath;
                startupReconnectSnapshot = history.SnapshotToken;
            }
            if (startupReconnectQueue == null || startupReconnectQueue.Count == 0 ||
                startupReconnectProfile != profilePath || !Object.ReferenceEquals(startupReconnectSnapshot, history.SnapshotToken))
            { CancelStartupReconnect(); return; }
            // Use the existing, acknowledged connection path for one controller
            // per tick. The message loop can process a manual stop between them;
            // no failed controller is retried and there is no unbounded batch.
            string id = startupReconnectQueue.Dequeue();
            try
            {
                var definition = ControllerRouting.ForController(UiReadProfile, id);
                string error = ControllerOutputs.AvailabilityError(definition.Controller);
                if (error != null) throw new InvalidOperationException(UiText.Get(error));
                if (!runtime.IsControllerEnabled(id)) runtime.EnableController(id);
            }
            catch (Exception ex)
            {
                CancelStartupReconnect();
                store.Event("Automatic controller connection stopped: " + ex.Message);
                if (trayIcon != null && trayIcon.Visible)
                    trayIcon.ShowBalloonTip(6000, "Analog Key Mapper", Tr("Controller konnte nicht automatisch verbunden werden. Öffne die App für Details.",
                        "A controller could not reconnect automatically. Open the app for details."), ToolTipIcon.Info);
            }
            if (startupReconnectQueue != null && startupReconnectQueue.Count == 0) CancelStartupReconnect();
            RefreshKeyboardSuppression(false);
        }
    }
}
