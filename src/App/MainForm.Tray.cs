using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace Tk75.App
{
    public sealed partial class MainForm
    {
        bool sessionStarted, previewMode;
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
            AddStartupRegistrationItem(trayMenu);
            AddReconnectStartupItem(trayMenu);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(Tr("Beenden", "Exit"), null, delegate { Close(); });
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
            runtime.SetPreviewActive(true);
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

        void AddStartupMenu(ContextMenuStrip menu)
        {
            menu.Items.Add(new ToolStripSeparator());
            var hide = menu.Items.Add(Tr("In den Infobereich minimieren", "Minimize to tray"), null, delegate { Attempt(MinimizeToTray); });
            hide.Enabled = !previewMode;
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
