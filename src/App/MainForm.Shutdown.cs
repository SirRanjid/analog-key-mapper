using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Tk75.Mapping;

namespace Tk75.App
{
    public sealed partial class MainForm
    {
        // Windows is told why the final session response is pending. The wait
        // covers the existing 17s lighting restore and cancelled helper cleanup.
        const int SystemShutdownTimeoutMilliseconds = NormalCloseTimeoutMilliseconds;
        const int ReaderCleanupTimeoutMilliseconds = 8000;
        int systemShutdownWaitMilliseconds = SystemShutdownTimeoutMilliseconds;
        ShutdownWork systemShutdownWork;
        bool closeProfileSaved, systemShutdownTimedOut, systemShutdownStarted;
        bool applicationResourcesDisposed, resourceCleanupFailed, shutdownReasonRegistered;
        volatile bool normalReaderCleanupWaited;
        CloseProgressForm closeProgress;
        volatile ShutdownPhaseState systemLightingPhase, systemReaderPhase;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool ShutdownBlockReasonCreate(IntPtr window, string reason);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool ShutdownBlockReasonDestroy(IntPtr window);

        void ShowCloseProgress()
        {
            if (IsDisposed || closeProgress != null && !closeProgress.IsDisposed) return;
            closeProgress = new CloseProgressForm();
            if (previewMode || Left < -10000)
            { closeProgress.StartPosition = FormStartPosition.Manual; closeProgress.Location = new System.Drawing.Point(-30000, -30000); }
            closeProgress.Show(this); closeProgress.PaintProgress();
        }
        void SetClosePhase(int phase, ShutdownPhaseState state)
        {
            if (IsDisposed || closeProgress == null || closeProgress.IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke((Action)delegate { SetClosePhase(phase, state); }); } catch (InvalidOperationException) { }
                return;
            }
            closeProgress.SetPhase(phase, state); closeProgress.PaintProgress();
        }
        void HideCloseProgress()
        { var progress = closeProgress; closeProgress = null; if (progress != null) progress.Dispose(); }

        void OnApplicationClosing(object sender, FormClosingEventArgs args)
        {
            // The real native query is intercepted before WinForms raises this
            // event. This branch also handles an explicit committed system close.
            if (args.CloseReason == CloseReason.WindowsShutDown)
            {
                args.Cancel = false;
                try { BeginSystemShutdown(); }
                catch (Exception error)
                {
                    systemShutdownStarted = true; closing = true;
                    System.Threading.ThreadPool.QueueUserWorkItem(delegate { LogShutdownFailure("Windows shutdown preparation", error); });
                }
                return;
            }
            if (closing) return;
            if (TryMinimizeToTrayOnClosing(args)) return;
            CancelPressureCapture();
            CancelInputThresholdCapture();
            ShowCloseProgress();
            if (rgbClosePending) { args.Cancel = true; return; }
            if (deviceDetachInProgress)
            {
                SetClosePhase(3, ShutdownPhaseState.Running);
                closeAfterDeviceDetach = true; args.Cancel = true; return;
            }
            if (!rgbCloseFinished)
            {
                CancelMappingDrag();
                SaveControllerReconnectState();
                SetClosePhase(1, ShutdownPhaseState.Running);
                try { runtime.Disable("Anwendung wird geschlossen"); SetClosePhase(1, ShutdownPhaseState.Completed); }
                catch (Exception error) { resourceCleanupFailed = true; SetClosePhase(1, ShutdownPhaseState.Failed); LogShutdownFailure("Controller shutdown", error); }
                keyboardSuppression.SetEnabled(false);
                closeProfileSaved = false; SetClosePhase(0, ShutdownPhaseState.Running);
                try { SaveProfile(); closeProfileSaved = true; SetClosePhase(0, ShutdownPhaseState.Completed); }
                catch (Exception error)
                {
                    SetClosePhase(0, ShutdownPhaseState.Failed); HideCloseProgress();
                    if (MessageBox.Show(this, UiText.Get(error.Message) + Tr("\nOhne Speichern beenden?", "\nExit without saving?"),
                        Tr("Speichern fehlgeschlagen", "Saving failed"), MessageBoxButtons.YesNo) != DialogResult.Yes)
                    { CancelControllerReconnectSave(); trayExitRequested = false; args.Cancel = true; return; }
                    ShowCloseProgress(); SetClosePhase(0, ShutdownPhaseState.Failed);
                }
                PersistControllerReconnectState();
                if (BeginRgbCloseRestore()) { args.Cancel = true; return; }
                SetClosePhase(2, ShutdownPhaseState.Completed);
            }
            closing = true; StopDeviceDiscovery(); uiTimer.Stop(); ReleaseShortcutRegistrations();
            // Render the final phase before potentially slow reader/helper Dispose.
            SetClosePhase(3, ShutdownPhaseState.Running);
            bool disposedCleanly = DisposeApplicationResources();
            SetClosePhase(3, disposedCleanly && rgbCloseSucceeded ? ShutdownPhaseState.Completed : ShutdownPhaseState.Failed);
            if (closeProfileSaved && disposedCleanly && rgbCloseSucceeded) ConfirmControllerReconnectExit();
            HideCloseProgress();
        }

        void BeginSystemShutdown()
        {
            if (systemShutdownStarted) return;
            systemShutdownStarted = true;
            var deadline = Stopwatch.StartNew();
            ShowCloseProgress();
            closing = true; closeAfterDeviceDetach = false; Enabled = false;
            CancelPressureCapture();
            CancelInputThresholdCapture();
            CancelStartupReconnect(); CancelControllerReconnectSave();
            uiTimer.Stop(); StopDeviceDiscovery(); ReleaseShortcutRegistrations();
            CancelMappingDrag(); keyboardSuppression.SetEnabled(false);

            Profile saved = null; Exception snapshotFailure = null;
            try { saved = MergePendingInput(history.Current); }
            catch (Exception error) { snapshotFailure = error; }
            string savedPath = profilePath;
            ReaderSession ownedReader = reader;
            reader = null; // Only the cleanup lane owns disposal from this point.
            RgbBackupWork lighting = rgbBackupWork;
            Task detachedReaderCleanup = deviceDetachCleanup;
            systemShutdownWork = new ShutdownWork(
                delegate
                {
                    Task pendingConnections = null;
                    LoggedShutdownCleanup("Controller shutdown", delegate {
                        RunShutdownCleanup(
                            delegate { pendingConnections = runtime.CancelPendingConnections(); },
                            delegate { runtime.Disable("Windows shutdown"); },
                            delegate { runtime.Dispose(); },
                            delegate { keyboardSuppression.Dispose(); },
                            delegate { if (pendingConnections != null) pendingConnections.Wait(); });
                    });
                },
                delegate
                {
                    RunShutdownCleanup(
                        delegate {
                            systemLightingPhase = ShutdownPhaseState.Running;
                            try
                            {
                                if (!RestoreRgbWorkBeforeDisconnect(lighting, RgbCloseTimeoutMilliseconds))
                                    throw new System.IO.IOException("The primary lighting restore was not confirmed; original backup retained.");
                                systemLightingPhase = ShutdownPhaseState.Completed;
                            }
                            catch (Exception error) { systemLightingPhase = ShutdownPhaseState.Failed; LogShutdownFailure("Windows shutdown lighting; original backup retained", error); throw; }
                        },
                        delegate {
                            systemReaderPhase = ShutdownPhaseState.Running;
                            try
                            {
                                RunShutdownCleanup(
                                    DisposeLearnedInputs,
                                    delegate { if (ownedReader != null) ownedReader.DisposeAndWait(ReaderCleanupTimeoutMilliseconds); },
                                    delegate {
                                        if (detachedReaderCleanup == null) return;
                                        detachedReaderCleanup.Wait();
                                        if (deviceDetachCleanupFailure != null) throw deviceDetachCleanupFailure;
                                    });
                                systemReaderPhase = ShutdownPhaseState.Completed;
                            }
                            catch (Exception error) { systemReaderPhase = ShutdownPhaseState.Failed; LogShutdownFailure("Windows shutdown reader/helper cleanup", error); throw; }
                        });
                },
                delegate
                {
                    LoggedShutdownCleanup("Windows shutdown profile save", delegate {
                        if (snapshotFailure != null) throw snapshotFailure;
                        if (saved == null || savedPath == null) throw new System.IO.IOException("No profile snapshot was available for saving.");
                        WorkspaceStore.WriteAtomic(savedPath, ProfileJson.Serialize(saved)); closeProfileSaved = true;
                    });
                    // Keep the reconnect journal unconfirmed during OS shutdown:
                    // forced termination can still interrupt another cleanup lane.
                });
            systemShutdownWork.Start();
            int budget = Math.Max(0, Math.Min(SystemShutdownTimeoutMilliseconds, systemShutdownWaitMilliseconds));
            bool completed;
            do
            {
                int remaining = Math.Max(0, budget - (int)deadline.ElapsedMilliseconds);
                completed = systemShutdownWork.Wait(Math.Min(50, remaining));
                RefreshSystemCloseProgress();
                if (completed || remaining == 0) break;
            } while (true);
            systemShutdownTimedOut = !completed;
            if (systemShutdownTimedOut && closeProgress != null)
            {
                closeProgress.SetDetail(Tr("Zeitlimit erreicht · Sicherung bleibt für die Wiederherstellung erhalten", "Time limit reached · backup retained for recovery"));
                closeProgress.PaintProgress();
                System.Threading.ThreadPool.QueueUserWorkItem(delegate { store.Event("Windows shutdown cleanup reached its deadline; recovery remains unconfirmed."); });
            }
        }
        void RefreshSystemCloseProgress()
        {
            if (systemShutdownWork == null || closeProgress == null || closeProgress.IsDisposed) return;
            var phases = systemShutdownWork.States;
            closeProgress.SetPhase(0, phases[2]); closeProgress.SetPhase(1, phases[0]);
            ShutdownPhaseState lighting = systemLightingPhase;
            if (phases[1] == ShutdownPhaseState.Failed && lighting == ShutdownPhaseState.Pending) lighting = ShutdownPhaseState.Failed;
            closeProgress.SetPhase(2, lighting);
            // Controller candidates may own helper cleanup independently of the
            // reader. The final phase cannot be complete while either is running.
            ShutdownPhaseState resources = systemReaderPhase;
            if (phases[1] == ShutdownPhaseState.Failed && resources == ShutdownPhaseState.Pending) resources = ShutdownPhaseState.Failed;
            if (resources == ShutdownPhaseState.Completed && phases[0] != ShutdownPhaseState.Completed)
                resources = phases[0] == ShutdownPhaseState.Failed ? ShutdownPhaseState.Failed : ShutdownPhaseState.Running;
            closeProgress.SetPhase(3, resources);
            closeProgress.PaintProgress();
        }
        static void RunShutdownCleanup(params Action[] actions)
        {
            var errors = new List<Exception>();
            foreach (Action action in actions) try { action(); } catch (Exception error) { errors.Add(error); }
            if (errors.Count != 0) throw new AggregateException(errors);
        }
        void LoggedShutdownCleanup(string context, Action action)
        { try { action(); } catch (Exception error) { LogShutdownFailure(context, error); throw; } }

        bool HandleSystemSessionMessage(ref Message message)
        {
            if (message.Msg == 0x0011)
            {
                // Register on the window's owning thread, then promptly accept
                // the query. Time-consuming work belongs to WM_ENDSESSION.
                // https://learn.microsoft.com/windows/win32/shutdown/wm-queryendsession
                if (!previewMode && !shutdownReasonRegistered)
                    try { shutdownReasonRegistered = ShutdownBlockReasonCreate(Handle, Tr("Tastaturbeleuchtung wird wiederhergestellt und Verbindungen werden geschlossen.", "Restoring keyboard lighting and closing connections.")); }
                    catch (EntryPointNotFoundException) { }
                message.Result = new IntPtr(1); return true;
            }
            if (message.Msg != 0x0016) return false;
            try
            {
                if (message.WParam != IntPtr.Zero)
                {
                    // Holding this response, rather than merely registering a
                    // reason, gives the workers their bounded restore window.
                    try { BeginSystemShutdown(); }
                    catch (Exception error) { systemShutdownStarted = true; closing = true; LogShutdownFailure("Windows shutdown preparation", error); }
                }
                // FALSE means Windows cancelled logoff. Since the query has not
                // touched resources, the editor remains fully usable.
            }
            finally
            {
                if (shutdownReasonRegistered)
                {
                    try { ShutdownBlockReasonDestroy(Handle); } catch (EntryPointNotFoundException) { }
                    shutdownReasonRegistered = false;
                }
            }
            message.Result = IntPtr.Zero; return true;
        }

        void LogShutdownFailure(string context, Exception error)
        { try { store.Event(context + ": " + error.GetBaseException().Message); } catch { } }

        bool DisposeApplicationResources()
        {
            if (systemShutdownStarted) { HideCloseProgress(); return false; }
            if (applicationResourcesDisposed) return !resourceCleanupFailed;
            applicationResourcesDisposed = true;
            if (deviceDetachCleanupFailure != null) resourceCleanupFailed = true;
            ReaderSession ownedReader = reader; reader = null;
            try { keyboardSuppression.Dispose(); }
            catch (Exception error) { resourceCleanupFailed = true; LogShutdownFailure("Keyboard suppression cleanup", error); }
            try { runtime.Dispose(); }
            catch (Exception error) { resourceCleanupFailed = true; LogShutdownFailure("Controller cleanup", error); }
            try { DisposeLearnedInputs(); }
            catch (Exception error) { resourceCleanupFailed = true; LogShutdownFailure("Learned input cleanup", error); }
            try
            {
                if (ownedReader != null)
                {
                    // The asynchronous normal-close lane already spent this
                    // reader's budget. A timeout must not start another UI wait.
                    if (normalReaderCleanupWaited) ownedReader.Dispose();
                    else ownedReader.DisposeAndWait(ReaderCleanupTimeoutMilliseconds);
                }
            }
            catch (Exception error) { resourceCleanupFailed = true; LogShutdownFailure("Reader cleanup", error); }
            return !resourceCleanupFailed;
        }
    }
}
