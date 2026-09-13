using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace Tk75.App
{
    public sealed partial class MainForm
    {
        bool sessionStarted, previewMode;
        bool minimizeToTray, trayPreferenceLoaded, trayExitRequested;
        NotifyIcon trayIcon;
        ContextMenuStrip trayMenu;
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
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(Tr("Beenden", "Exit"), null, delegate { ExitFromTray(); });
            StyleMenu(trayMenu);
            string caption = "Analog Key Mapper";
            var versions = typeof(MainForm).Assembly.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false);
            if (versions.Length != 0) caption += " · " + ((AssemblyInformationalVersionAttribute)versions[0]).InformationalVersion;
            trayIcon = new NotifyIcon { Icon = Icon ?? SystemIcons.Application, Text = caption.Length <= 63 ? caption : caption.Substring(0, 63), ContextMenuStrip = trayMenu, Visible = showIcon };
            trayIcon.MouseClick += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) RestoreFromTray(); };
            Resize += delegate { if (Visible && WindowState != FormWindowState.Minimized) lastVisibleWindowState = WindowState; };
            Disposed += delegate
            {
                if (trayIcon != null) { trayIcon.Visible = false; trayIcon.Dispose(); trayIcon = null; }
                if (trayMenu != null) { trayMenu.Dispose(); trayMenu = null; }
                openFont.Dispose();
            };
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
