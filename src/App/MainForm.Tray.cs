using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Tk75.App
{
    public sealed partial class MainForm
    {
        bool sessionStarted, previewMode;
        bool minimizeToTray, trayPreferenceLoaded, trayExitRequested;
        NotifyIcon trayIcon;
        ContextMenuStrip trayMenu;
        readonly Dictionary<TrayStatus, Icon> trayStatusIcons = new Dictionary<TrayStatus, Icon>();
        TrayStatus? displayedTrayStatus;
        string displayedTrayTooltip;
        FormWindowState lastVisibleWindowState = FormWindowState.Normal;

        // Also used by a hidden startup: Shown is never raised until the user
        // opens the window, but input and device discovery must already work.
        void StartUiSession()
        {
            if (sessionStarted) return;
            sessionStarted = true;
            ChooseInitialDetails();
            if (!previewMode)
            {
                applicationInputActive = true;
                modeShortcutRegistrationActive = true;
                RefreshModeShortcutRegistration();
                ApplyKeyboardSuppressionPreference();
                ConfigureControllerReconnectStartup();
                StartDeviceDiscovery();
                uiTimer.Start();
            }
            UiText.Apply(this); RefreshInputModeUi(); RefreshStopShortcutUi();
            RefreshTrayStatus();
        }

        internal void InitializeTray(bool showIcon)
        {
            if (trayIcon != null) return;
            LoadTrayPreference();
            trayMenu = new ContextMenuStrip();
            var open = new ToolStripMenuItem(Tr("Analog Key Mapper öffnen", "Open Analog Key Mapper"));
            var openFont = new Font(open.Font, FontStyle.Bold);
            open.Font = openFont;
            open.Click += delegate { RestoreFromTray(); };
            trayMenu.Items.Add(open);
            trayMenu.Items.Add(Tr("Alle Controller ausschalten", "Turn all controllers off"), null, delegate
            {
                CancelStartupReconnect();
                try { Attempt(delegate { runtime.Disable("Manuell deaktiviert"); }); }
                finally { RefreshKeyboardSuppression(false); UpdateControllerConnectionUi(); }
            });
            trayMenu.Items.Add(new ToolStripSeparator());
            AddMinimizeToTrayItem(trayMenu);
            AddStartupRegistrationItem(trayMenu);
            AddReconnectStartupItem(trayMenu);
            AddRgbStartupPreferenceItem(trayMenu);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(Tr("Beenden", "Exit"), null, delegate { ExitFromTray(); });
            StyleMenu(trayMenu);
            trayIcon = new NotifyIcon { ContextMenuStrip = trayMenu };
            RefreshTrayStatus();
            trayIcon.Visible = showIcon;
            trayIcon.MouseClick += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) RestoreFromTray(); };
            Resize += delegate { if (Visible && WindowState != FormWindowState.Minimized) lastVisibleWindowState = WindowState; };
            Disposed += delegate
            {
                if (trayIcon != null) { trayIcon.Visible = false; trayIcon.Dispose(); trayIcon = null; }
                foreach (Icon statusIcon in trayStatusIcons.Values) statusIcon.Dispose();
                trayStatusIcons.Clear();
                if (trayMenu != null) { trayMenu.Dispose(); trayMenu = null; }
                openFont.Dispose();
            };
        }

        void RefreshTrayStatus()
        {
            if (trayIcon == null || IsDisposed || Disposing) return;
            // Cleanup may hold controller locks. Its status must not query the
            // detached runtime or rebuild input routing while those locks drain.
            if (closing || rgbClosePending || deviceDetachInProgress)
            {
                SetTrayStatus(TrayStatus.Starting, Tr("Analog Key Mapper · Verbindung wird beendet…", "Analog Key Mapper · Closing connection…"));
                return;
            }
            bool reading = LiveInputReading, active = runtime.AnyEnabled, keyboardMode = runtime.KeyboardMode;
            bool starting = !sessionStarted || learnedSourcesOpening || reader != null && reader.IsStarting ||
                lastInventory == null && discoveryEnabled && (discoveryRunning || discoveryPending);
            if (!active && !starting)
            {
                var controllers = UiReadProfile.Controllers;
                if (controllers == null || controllers.Count == 0)
                    starting = runtime.IsControllerConnecting(Tk75.Mapping.ControllerRouting.DefaultControllerId);
                else foreach (var controller in controllers)
                    if (runtime.IsControllerConnecting(controller.Id)) { starting = true; break; }
            }
            bool attention = discoveryFailed || reader != null && reader.HasFault ||
                !active && controllerReconnectNotice != null ||
                modeShortcutRegistrationActive && !modeShortcutDialogOpen && (modeShortcutError != null || stopShortcutError != null) ||
                active && !keyboardMode && keyboardSuppression.IsEnabled && !keyboardSuppression.IsInstalled;
            var lighting = rgbBackupWork;
            if (lighting != null && Object.ReferenceEquals(lighting.Reader, reader))
                lock (lighting.Gate) attention |= lighting.Error != null || lighting.StartupApprovalPending || lighting.RecoveryRequired && !lighting.Busy;
            TrayStatus status = TrayStatusPolicy.Resolve(starting, reading, keyboardMode, active, attention);
            SetTrayStatus(status, TrayStatusPolicy.Tooltip(status, reading, active, UiText.Language == "en"));
        }

        void SetTrayStatus(TrayStatus status, string tooltip)
        {
            if (displayedTrayStatus != status)
            {
                Icon statusIcon;
                if (!trayStatusIcons.TryGetValue(status, out statusIcon))
                {
                    statusIcon = AppStatusIcon.Create(status);
                    trayStatusIcons.Add(status, statusIcon);
                }
                trayIcon.Icon = statusIcon;
                displayedTrayStatus = status;
            }
            if (displayedTrayTooltip != tooltip)
            {
                trayIcon.Text = tooltip;
                displayedTrayTooltip = tooltip;
            }
        }

        internal void StartInBackground()
        {
            // Creating the handle registers shortcuts and gives asynchronous
            // device events a UI target, without ever showing a taskbar window.
            IntPtr window = Handle;
            runtime.SetPreviewActive(false);
            StartUiSession();
        }

        internal void RestoreFromTray()
        {
            if (IsDisposed || closing) return;
            if (!deviceDetachInProgress) runtime.SetPreviewActive(true);
            if (!Visible) { WindowState = lastVisibleWindowState; Show(); }
            else if (WindowState == FormWindowState.Minimized) WindowState = lastVisibleWindowState;
            Activate();
        }

        void MinimizeToTray()
        {
            if (trayIcon == null || closing || rgbClosePending || deviceDetachInProgress) return;
            settings.EndEdit(); FlushInputDraft(); SaveProfile(); CancelMappingDrag();
            if (WindowState != FormWindowState.Minimized) lastVisibleWindowState = WindowState;
            Hide();
            runtime.SetPreviewActive(false);
        }

        // Only a user close of the editor is a request to hide. Explicit Exit,
        // deferred cleanup and Windows shutdown always retain the real exit path.
        bool TryMinimizeToTrayOnClosing(FormClosingEventArgs args)
        {
            LoadTrayPreference();
            if (args.CloseReason != CloseReason.UserClosing || !minimizeToTray || trayIcon == null ||
                trayExitRequested || closing || systemShutdownStarted || rgbClosePending || rgbCloseFinished) return false;
            args.Cancel = true;
            if (deviceDetachInProgress)
            {
                // Input/output cleanup owns the runtime lock during a detach.
                // Hiding is safe without touching that worker or its draft.
                CancelMappingDrag();
                if (WindowState != FormWindowState.Minimized) lastVisibleWindowState = WindowState;
                Hide();
            }
            else Attempt(MinimizeToTray);
            return true;
        }

        void ExitFromTray()
        {
            if (IsDisposed || closing) return;
            trayExitRequested = true;
            Close();
        }

        void LoadTrayPreference()
        {
            if (trayPreferenceLoaded) return;
            trayPreferenceLoaded = true;
            try { minimizeToTray = TrayPreferences.Load(store.Root); }
            catch (Exception error) { store.Event("Minimize-to-tray preference unavailable: " + error.Message); }
        }

        void SetMinimizeToTray(bool enabled)
        {
            TrayPreferences.Save(store.Root, enabled);
            minimizeToTray = enabled;
            trayPreferenceLoaded = true;
        }

        void AddMinimizeToTrayItem(ContextMenuStrip menu)
        {
            LoadTrayPreference();
            var option = new ToolStripMenuItem { Checked = minimizeToTray, Enabled = !previewMode };
            Action refresh = delegate
            {
                option.Text = Tr("In den Infobereich minimieren", "Minimize to tray");
                option.ToolTipText = Tr("Mit × im Infobereich weiterlaufen. Über Beenden im Infobereich vollständig schließen.",
                    "Keep running in the tray when you close with ×. Choose Exit in the tray to quit completely.");
                option.Checked = minimizeToTray;
            };
            refresh();
            option.Click += delegate { Attempt(delegate { SetMinimizeToTray(!minimizeToTray); }); refresh(); };
            menu.Items.Add(option);
            menu.Opening += delegate { refresh(); };
        }

        void AddStartupMenu(ContextMenuStrip menu)
        {
            menu.Items.Add(new ToolStripSeparator());
            AddMinimizeToTrayItem(menu);
            AddStartupRegistrationItem(menu);
            AddReconnectStartupItem(menu);
            AddRgbStartupPreferenceItem(menu);
        }

        void AddStartupRegistrationItem(ContextMenuStrip menu)
        {
            var startup = new ToolStripMenuItem(Tr("Mit Windows starten · im Infobereich", "Start with Windows · in tray"));
            startup.Enabled = !previewMode;
            startup.Click += delegate { Attempt(delegate { WindowsStartup.Current().SetRegistered(!WindowsStartup.Current().IsRegistered); startup.Checked = WindowsStartup.Current().IsRegistered; }); };
            menu.Items.Add(startup);
            menu.Opening += delegate
            {
                if (previewMode) return;
                try { startup.Checked = WindowsStartup.Current().IsRegistered; startup.Enabled = true; startup.ToolTipText = ""; }
                catch (Exception ex) { startup.Enabled = false; startup.ToolTipText = UiText.Get(ex.Message); }
            };
        }
    }
}
