using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Forms;
using Tk75.Mapping;

namespace Tk75.App
{
    public sealed partial class MainForm
    {
        const int SystemShutdownTimeoutMilliseconds = 3000;
        ShutdownWork systemShutdownWork;
        bool closeProfileSaved, systemShutdownTimedOut, systemShutdownStarted;
        bool applicationResourcesDisposed, resourceCleanupFailed;

        void OnApplicationClosing(object sender, FormClosingEventArgs args)
        {
            // This must precede the ordinary pending-restore/detach checks:
            // Windows logoff cannot depend on a later UI callback or prompt.
            if (args.CloseReason == CloseReason.WindowsShutDown)
            {
                args.Cancel = false;
                try { BeginSystemShutdown(); }
                catch (Exception error)
                {
                    // Never turn an OS shutdown failure into a UI prompt or a
                    // second synchronous Dispose attempt on this same thread.
                    systemShutdownStarted = true; closing = true;
                    System.Threading.ThreadPool.QueueUserWorkItem(delegate { LogShutdownFailure("Windows shutdown preparation", error); });
                }
                return;
            }
            if (closing) return;
            if (rgbClosePending) { args.Cancel = true; return; }
            if (deviceDetachInProgress) { closeAfterDeviceDetach = true; args.Cancel = true; return; }
            if (!rgbCloseFinished)
            {
                CancelMappingDrag();
                SaveControllerReconnectState();
                try { runtime.Disable("Anwendung wird geschlossen"); }
                catch (Exception error) { resourceCleanupFailed = true; LogShutdownFailure("Controller shutdown", error); }
                keyboardSuppression.SetEnabled(false);
                closeProfileSaved = false;
                try { SaveProfile(); closeProfileSaved = true; }
                catch (Exception error)
                {
                    if (MessageBox.Show(this, UiText.Get(error.Message) + Tr("\nOhne Speichern beenden?", "\nExit without saving?"),
                        Tr("Speichern fehlgeschlagen", "Saving failed"), MessageBoxButtons.YesNo) != DialogResult.Yes)
                    { CancelControllerReconnectSave(); args.Cancel = true; return; }
                }
                PersistControllerReconnectState();
                if (BeginRgbCloseRestore()) { args.Cancel = true; return; }
            }
            closing = true; StopDeviceDiscovery(); uiTimer.Stop(); ReleaseShortcutRegistrations();
            bool disposedCleanly = DisposeApplicationResources();
            // An explicit exit without saving, failed output cleanup or failed
            // lighting restore must never bless an unconfirmed clean exit.
            if (closeProfileSaved && disposedCleanly && rgbCloseSucceeded) ConfirmControllerReconnectExit();
        }

        void BeginSystemShutdown()
        {
            if (systemShutdownStarted) return;
            systemShutdownStarted = true;
            var deadline = Stopwatch.StartNew();
            closing = true; closeAfterDeviceDetach = false; Enabled = false;
            CancelStartupReconnect(); CancelControllerReconnectSave();
            uiTimer.Stop(); StopDeviceDiscovery(); ReleaseShortcutRegistrations();
            CancelMappingDrag(); keyboardSuppression.SetEnabled(false);

            // Copy UI-owned edits without Configure or disk I/O. Pending input
            // values are merged into this detached copy, never into live output.
            Profile saved = null; Exception snapshotFailure = null;
            try { saved = MergePendingInput(history.Current); }
            catch (Exception error) { snapshotFailure = error; }
            string savedPath = profilePath;
            ReaderSession ownedReader = reader;
            reader = null; // the cleanup lane, not Form.Dispose, now owns it
            RgbBackupWork lighting = rgbBackupWork;
            systemShutdownWork = new ShutdownWork(
                delegate
                {
                    Task pendingConnections = null;
                    // A previous detach may hold the coordinator lock while
                    // draining output. Even cancellation therefore belongs off
                    // the UI thread so it cannot consume an unbounded pre-wait.
                    try { pendingConnections = runtime.CancelPendingConnections(); }
                    catch (Exception error) { LogShutdownFailure("Pending controller cancellation", error); }
                    try { runtime.Dispose(); }
                    catch (Exception error) { LogShutdownFailure("Controller shutdown", error); }
                    finally
                    {
                        keyboardSuppression.Dispose();
                        if (pendingConnections != null)
                            try { pendingConnections.Wait(); }
                            catch (Exception error) { LogShutdownFailure("Pending controller cleanup", error); }
                    }
                },
                delegate
                {
                    try { RestoreRgbWorkBeforeDisconnect(lighting, SystemShutdownTimeoutMilliseconds); }
                    catch (Exception error) { LogShutdownFailure("Windows shutdown lighting; original backup retained", error); }
                    finally { if (ownedReader != null) ownedReader.Dispose(); }
                },
                delegate
                {
                    if (snapshotFailure != null) LogShutdownFailure("Windows shutdown snapshot", snapshotFailure);
                    if (saved != null && savedPath != null)
                        try { WorkspaceStore.WriteAtomic(savedPath, ProfileJson.Serialize(saved)); }
                        catch (Exception error) { LogShutdownFailure("Windows shutdown profile save", error); }
                    // Deliberately do not confirm the clean-exit reconnect
                    // journal: Windows can terminate these background lanes.
                });
            systemShutdownWork.Start();
            systemShutdownTimedOut = !systemShutdownWork.Wait(Math.Max(0, SystemShutdownTimeoutMilliseconds - (int)deadline.ElapsedMilliseconds));
        }

        void OnSystemSessionEnd(Message message)
        {
            if (message.Msg != 0x0016 || message.WParam != IntPtr.Zero || !systemShutdownStarted || IsDisposed) return;
            // Another application can cancel Windows logoff after our query was
            // accepted. Our resources are already stopping: finish this app's
            // close instead of leaving a dead, disabled editor behind.
            try { BeginInvoke((Action)delegate { if (!IsDisposed) Close(); }); }
            catch (InvalidOperationException) { }
        }

        void LogShutdownFailure(string context, Exception error)
        { try { store.Event(context + ": " + error.GetBaseException().Message); } catch { } }

        bool DisposeApplicationResources()
        {
            // Rejoining here would silently defeat the system-shutdown budget.
            if (systemShutdownStarted) return false;
            if (applicationResourcesDisposed) return !resourceCleanupFailed;
            applicationResourcesDisposed = true;
            ReaderSession ownedReader = reader; reader = null;
            try { keyboardSuppression.Dispose(); }
            catch (Exception error) { resourceCleanupFailed = true; LogShutdownFailure("Keyboard suppression cleanup", error); }
            try { runtime.Dispose(); }
            catch (Exception error) { resourceCleanupFailed = true; LogShutdownFailure("Controller cleanup", error); }
            try { if (ownedReader != null) ownedReader.Dispose(); }
            catch (Exception error) { resourceCleanupFailed = true; LogShutdownFailure("Reader cleanup", error); }
            return !resourceCleanupFailed;
        }
    }
}
