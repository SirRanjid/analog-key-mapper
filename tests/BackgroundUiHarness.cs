using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Tk75.App;
using Tk75.Mapping;

namespace Tk75.Tests
{
    // Real MainForm event paths, with synthetic endpoints only. Preview mode
    // prevents reader, hook, discovery and autostart registration side effects.
    public static class BackgroundUiHarness
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
        static int assertions;
        static void Check(bool condition, string message)
        { assertions++; if (!condition) throw new InvalidOperationException(message); }
        static T Field<T>(object owner, string name)
        { return (T)owner.GetType().GetField(name, Fields).GetValue(owner); }
        static void Set(object owner, string name, object value)
        { owner.GetType().GetField(name, Fields).SetValue(owner, value); }
        static object Call(MainForm form, string name, params object[] values)
        {
            MethodInfo method = typeof(MainForm).GetMethods(Fields).Single(m => m.Name == name && m.GetParameters().Length == values.Length);
            try { return method.Invoke(form, values); }
            catch (TargetInvocationException error) { throw error.InnerException; }
        }
        static void Pump(MainForm form) { form.PerformLayout(); Application.DoEvents(); }
        static void Tick(MainForm form) { Call(form, "UpdateLive"); }
        static void Reject(Action action, string message)
        { bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } catch (IOException) { rejected = true; } catch (InvalidDataException) { rejected = true; } Check(rejected, message); }
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool IsWindow(IntPtr window);

        sealed class StartupStore : IStartupRegistrationStore
        {
            internal string Value;
            internal int Writes, Deletes;
            internal bool FailWrite, IgnoreWrite;
            public string Read() { return Value; }
            public void Write(string value) { Writes++; if (FailWrite) throw new IOException("Synthetic denied write."); if (!IgnoreWrite) Value = value; }
            public void Delete() { Deletes++; Value = null; }
        }
        static void StartupRegistration(string directory)
        {
            string executable = Path.Combine(directory, "Folder with spaces", "AnalogKeyMapper.exe");
            string expected = "\"" + Path.GetFullPath(executable) + "\" --background";
            var store = new StartupStore(); var startup = new WindowsStartup(executable, store);
            Check(WindowsStartup.BuildCommand(executable) == expected, "Autostart quotes the complete executable path and adds only --background.");
            Check(!startup.IsRegistered && store.Writes == 0 && store.Deletes == 0, "Constructing and reading startup preferences never registers anything.");
            Check(WindowsStartup.IsBackgroundArgument(new[] { "--background" }), "The exact background option is accepted.");
            foreach (string[] invalid in new[] { new string[0], new[] { "--BACKGROUND" }, new[] { "--background", "extra" }, new[] { "--background " } })
                Check(!WindowsStartup.IsBackgroundArgument(invalid), "Other argument forms are not silently treated as background startup.");
            Check(!WindowsStartup.IsBackgroundArgument(null), "Null arguments are not a background request.");
            startup.SetRegistered(true);
            Check(startup.IsRegistered && store.Value == expected && store.Writes == 1, "Explicit enable writes and verifies the correct startup command.");
            startup.SetRegistered(false);
            Check(!startup.IsRegistered && store.Deletes == 1, "Explicit disable removes this installation's entry.");
            string other = "\"C:\\Another installation\\AnalogKeyMapper.exe\" --background";
            store.Value = other; startup.SetRegistered(false);
            Check(store.Value == other && store.Deletes == 1, "Disabling leaves another installation's startup entry intact.");
            store.FailWrite = true;
            Reject(delegate { startup.SetRegistered(true); }, "Write failure is surfaced rather than reporting registration success.");
            Check(!startup.IsRegistered && store.Value == other, "Failed registration preserves the actual previous setting.");
            store.FailWrite = false; store.IgnoreWrite = true;
            Reject(delegate { startup.SetRegistered(true); }, "A readback mismatch is surfaced rather than reporting registration success.");
            foreach (string invalid in new[] { "relative.exe", executable + "\"", executable + "\n", "" })
                Reject(delegate { WindowsStartup.BuildCommand(invalid); }, "Malformed or relative executable paths cannot form startup commands.");
        }

        static void ReconnectPreferences()
        {
            var profile = new Profile();
            profile.Controllers = ControllerRouting.EffectiveControllers(profile).ToList();
            string first = profile.Controllers[0].Id;
            profile.Controllers.Add(new ControllerDefinition { Id = "second", Name = "Second", Kind = ControllerKind.DualSense });
            var saved = ControllerReconnectSettings.Capture(true, "profile.json", profile, new[] { first, "second" });
            saved = ControllerReconnectSettings.Parse(saved.Serialize());
            Check(saved.Targets("profile.json", profile).SequenceEqual(new[] { first, "second" }), "Reconnect roundtrip retains only the captured matching controllers.");
            Check(saved.Targets("another.json", profile).Length == 0, "Saved connections cannot activate a different profile.");
            saved.Enabled = false;
            Check(saved.Targets("profile.json", profile).Length == 0, "Disabled reconnect preferences produce no targets.");
            saved.Enabled = true;
            Profile edited = ProfileJson.Clone(profile); edited.Controllers.RemoveAt(1);
            Check(saved.Targets("profile.json", edited).SequenceEqual(new[] { first }), "Removed controller IDs are not recreated by startup.");
            edited = ProfileJson.Clone(profile); edited.Controllers[1].Kind = ControllerKind.Xbox360;
            Check(saved.Targets("profile.json", edited).SequenceEqual(new[] { first }), "Changing a controller kind invalidates only that saved target.");
            foreach (string traversal in new[] { "../profile.json", "..\\profile.json", "C:\\profile.json", "sub/profile.json" })
            {
                saved.ProfileFile = traversal;
                Reject(delegate { ControllerReconnectSettings.Parse(saved.Serialize()); }, "Startup JSON cannot select a profile outside the profile directory.");
            }
        }

        sealed class FakeController : IControllerSession, IPreviewDemandSession
        {
            internal Profile Profile;
            internal bool Active, Disposed, PreviewActive;
            internal int PreviewReads, Disables, Disposes, Configures, Enables;
            internal double X;
            public bool Enabled { get { return Active && !Disposed; } }
            public ControllerFrame Frame { get { return new ControllerFrame(); } }
            public PreviewSnapshot Preview
            { get { PreviewReads++; return new PreviewSnapshot { LeftX = X }; } }
            public string Status { get { return "Synthetic endpoint"; } }
            public void Configure(Profile profile, IDictionary<int, Calibration> calibration)
            { Profile = ProfileJson.Clone(profile); Active = false; Configures++; }
            public void SetInputSource(object source) { Active = false; }
            public void SetKeyboardMode(bool value) { }
            public void SetPreviewActive(bool value) { PreviewActive = value; }
            public void Enable() { Active = true; Enables++; }
            public void Disable(string reason) { Active = false; Disables++; }
            public ControllerRelease PrepareDisable(string reason)
            { bool active = Active; Disable(reason); return active ? new ControllerRelease(delegate { }, delegate { }) : null; }
            public void Dispose() { Disposed = true; Active = false; Disposes++; }
        }

        static void CheckPassive(MainForm form)
        {
            Check(Field<object>(form, "reader") == null, "No reader is created by the background UI test.");
            Check(!Field<bool>(form, "discoveryEnabled"), "Preview startup does not enable hardware discovery.");
            Check(!Field<bool>(form, "applicationInputActive"), "Preview startup does not install input suppression.");
            Check(!Field<bool>(form, "modeShortcutRegistrationActive"), "Preview startup does not register global shortcuts.");
            Check(!Field<Timer>(form, "uiTimer").Enabled, "Preview keeps its timer manual for deterministic ticks.");
            Check(Field<Timer>(form, "uiTimer").Interval == 33, "Maintenance retains the 33 ms safety cadence.");
        }

        static void BackgroundAndRestore(string directory)
        {
            var created = new List<FakeController>();
            FakeController first = null, second = null;
            MainForm form = new MainForm(Path.Combine(directory, "data"), true);
            try
            {
                form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-30000, -30000);
                Call(form, "InitializeTray", false);
                Call(form, "StartInBackground");
                Check(form.IsHandleCreated && !form.Visible, "Background startup creates its message handle without showing the window.");
                Check(Field<bool>(form, "sessionStarted"), "Background startup initializes the UI session.");
                Check(!Field<NotifyIcon>(form, "trayIcon").Visible, "The synthetic test does not place an icon in the user's notification area.");
                CheckPassive(form);
                Call(form, "StartInBackground");
                Check(!form.Visible, "Repeated background initialization stays hidden.");

                // Shown applies the example profile once. Install our two-slot
                // profile after that, keeping all further events authentic.
                Call(form, "RestoreFromTray"); Pump(form);
                Check(form.Visible && form.WindowState == FormWindowState.Normal, "Tray restore opens the hidden window.");
                var runtime = new MultiControllerSession(delegate { var value = new FakeController(); created.Add(value); return value; });
                Field<MultiControllerSession>(form, "runtime").Dispose(); Set(form, "runtime", runtime);
                Profile profile = Field<EditHistory>(form, "history").Current;
                profile.Controllers = ControllerRouting.EffectiveControllers(profile).ToList();
                string firstId = profile.Controllers[0].Id;
                profile.Controllers.Add(new ControllerDefinition { Id = "second", Name = "Second controller", Kind = ControllerKind.DualSense });
                Set(form, "history", new EditHistory(profile));
                Call(form, "Configure"); Call(form, "SyncOutputMode"); Call(form, "RefreshBindings");
                first = created.Single(c => !c.Disposed && c.Profile.Controllers[0].Id == firstId);
                second = created.Single(c => !c.Disposed && c.Profile.Controllers[0].Id == "second");
                runtime.EnableController(firstId); runtime.EnableController("second");
                int disables = created.Sum(c => c.Disables), configures = created.Sum(c => c.Configures), enables = created.Sum(c => c.Enables);
                string profileJson = ProfileJson.Serialize(Field<EditHistory>(form, "history").Current);

                Call(form, "ShowPage", "monitor"); Pump(form); Set(form, "ticks", 9); Tick(form);
                Check(first.PreviewActive && !second.PreviewActive, "Only the visible selected controller receives preview demand.");
                Check(first.PreviewReads > 0 && second.PreviewReads == 0, "The monitor reads only the selected preview.");
                Check(Field<DataGridView>(form, "monitor").Rows.Count == 2, "The visible monitor receives its mapping rows.");
                Check(Field<Label>(form, "liveStatus").Text.Contains("LX"), "The visible live display formats the current preview.");

                for (int cycle = 0; cycle < 2; cycle++)
                {
                    Call(form, "MinimizeToTray"); Pump(form);
                    Check(!form.Visible && !first.PreviewActive && !second.PreviewActive, "Hiding immediately clears preview demand on every slot.");
                    int reads = created.Sum(c => c.PreviewReads), ticks = Field<int>(form, "ticks");
                    Field<Label>(form, "liveStatus").Text = "unchanged hidden";
                    Set(form, "lastRgbUiStatus", "pending maintenance");
                    for (int i = 0; i < 30; i++) Tick(form);
                    Check(created.Sum(c => c.PreviewReads) == reads, "Hidden maintenance never requests visual snapshots.");
                    Check(Field<Label>(form, "liveStatus").Text == "unchanged hidden", "Hidden maintenance does not reformat live text.");
                    Check(Field<int>(form, "ticks") == ticks + 30, "Hidden maintenance continues advancing.");
                    Check(Field<string>(form, "lastRgbUiStatus") != "pending maintenance", "RGB maintenance continues while the window is hidden.");
                    Check(runtime.IsControllerEnabled(firstId) && runtime.IsControllerEnabled("second"), "Hiding preserves both independently connected synthetic controllers.");
                    runtime.SelectedControllerId = cycle == 0 ? "second" : firstId;
                    Check(!first.PreviewActive && !second.PreviewActive, "Changing selection while hidden leaves all preview workers idle.");
                    FakeController selected = cycle == 0 ? second : first, other = cycle == 0 ? first : second;
                    selected.X = .75;
                    Call(form, "RestoreFromTray"); Pump(form); Tick(form);
                    Check(form.Visible && selected.PreviewActive && !other.PreviewActive, "Restore resumes only the newly selected controller's preview.");
                    Check(created.Sum(c => c.PreviewReads) > reads && Field<Label>(form, "liveStatus").Text.Contains("0.750"), "Restoring renders fresh state instead of the hidden display's stale value.");
                    CheckPassive(form);
                }

                IntPtr activationHandle;
                using (var instance = SingleInstanceWindow.Acquire(Path.Combine(directory, "unique-instance")))
                {
                    Check(instance.IsOwner, "The synthetic instance owns only its temporary installation identity.");
                    activationHandle = Field<NativeWindow>(instance, "activationWindow").Handle;
                    Check(IsWindow(activationHandle), "The instance exposes a live activation endpoint while the editor is hidden.");
                    instance.OnShowRequested(delegate { Call(form, "RestoreFromTray"); });
                    Call(form, "MinimizeToTray");
                    Check(!form.Visible, "The activation-message test begins with a hidden editor.");
                    Check(PostMessage(activationHandle, 0x8000 + 73, IntPtr.Zero, IntPtr.Zero), "An activation request can be posted to this test's own hidden endpoint.");
                    Pump(form); Tick(form);
                    Check(form.Visible && Object.ReferenceEquals(runtime, Field<MultiControllerSession>(form, "runtime")), "A native activation request restores the existing editor without another runtime.");
                }
                Check(!IsWindow(activationHandle), "Disposal removes the single-instance activation endpoint.");

                form.WindowState = FormWindowState.Minimized; Pump(form);
                Check(!first.PreviewActive && !second.PreviewActive, "Native minimize also clears preview demand without waiting for a tick.");
                int minimizedReads = created.Sum(c => c.PreviewReads);
                for (int i = 0; i < 10; i++) Tick(form);
                Check(created.Sum(c => c.PreviewReads) == minimizedReads, "Minimized maintenance performs no preview reads.");
                Call(form, "RestoreFromTray"); Pump(form); Tick(form);
                Check(form.Visible && form.WindowState != FormWindowState.Minimized && first.PreviewActive, "Restore from native minimize resumes the selected preview.");

                runtime.SelectedControllerId = "second";
                Check(second.PreviewActive && !first.PreviewActive, "Visible controller switching transfers demand immediately and exclusively.");
                Tick(form);
                Check(runtime.IsControllerEnabled(firstId) && runtime.IsControllerEnabled("second"), "Preview demand and selection do not change output connections.");
                Check(created.Sum(c => c.Disables) == disables && created.Sum(c => c.Configures) == configures && created.Sum(c => c.Enables) == enables,
                    "Hide, restore and selection cause no output reset, recreation or enable call.");
                Check(ProfileJson.Serialize(Field<EditHistory>(form, "history").Current) == profileJson, "Background transitions preserve the profile.");
                Check(Object.ReferenceEquals(runtime, Field<MultiControllerSession>(form, "runtime")), "Background transitions preserve the runtime instance.");
                CheckPassive(form);
            }
            finally { form.Dispose(); }
            Check(Field<NotifyIcon>(form, "trayIcon") == null, "Form disposal releases its notification icon.");
            Check(first != null && second != null && first.Disposed && second.Disposed, "Final disposal closes every synthetic controller session.");
            Check(created.All(c => c.Disposes == 1), "Every synthetic endpoint is disposed exactly once.");
        }

        static void ReconnectCancelsForPendingEdits(string directory)
        {
            using (var form = new MainForm(Path.Combine(directory, "startup-edit-data"), true))
            {
                var history = Field<EditHistory>(form, "history");
                var startup = new ControllerReconnectSettings { Enabled = true };
                Set(form, "controllerStartup", startup);
                Set(form, "startupReconnectPending", true);
                Set(form, "startupReconnectProfile", Field<string>(form, "profilePath"));
                Set(form, "startupReconnectSnapshot", history.SnapshotToken);
                Call(form, "TryReconnectStartupControllers");
                Check(Field<bool>(form, "startupReconnectPending"), "Without input or edits, startup waits for the keyboard without discarding the request.");
                string original = ProfileJson.Serialize(history.Current);
                Set(form, "inputDirty", true);
                Call(form, "TryReconnectStartupControllers");
                Check(!Field<bool>(form, "startupReconnectPending") && Field<Queue<string>>(form, "startupReconnectQueue") == null,
                    "A pending input draft cancels startup even though the profile snapshot has not changed.");
                Check(Field<bool>(form, "inputDirty") && ProfileJson.Serialize(history.Current) == original,
                    "Cancelling startup neither flushes nor discards the user's draft.");
                Check(!Field<MultiControllerSession>(form, "runtime").AnyEnabled, "Draft cancellation opens no controller output.");

                Set(form, "inputDirty", false);
                Set(form, "startupReconnectPending", true);
                Set(form, "startupReconnectProfile", Field<string>(form, "profilePath"));
                Set(form, "startupReconnectSnapshot", history.SnapshotToken);
                Profile edited = history.Current; edited.Name = "Manual edit while waiting"; history.Commit(edited);
                Call(form, "TryReconnectStartupControllers");
                Check(!Field<bool>(form, "startupReconnectPending") && Field<Queue<string>>(form, "startupReconnectQueue") == null,
                    "A committed profile edit cancels startup before a keyboard is available.");
                Check(history.Current.Name == "Manual edit while waiting", "The cancelled startup leaves the new profile edit intact.");
                Check(Field<ReaderSession>(form, "reader") == null && !Field<MultiControllerSession>(form, "runtime").AnyEnabled,
                    "Both cancellation paths remain entirely device-free.");
            }
        }

        static void BackgroundMessageLoop(string directory)
        {
            using (var form = new MainForm(Path.Combine(directory, "message-loop-data"), true))
            using (var watchdog = new Timer { Interval = 5000 })
            {
                form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-30000, -30000);
                Call(form, "InitializeTray", false);
                int shown = 0; bool everVisible = false, observed = false, closed = false;
                Exception failure = null;
                form.Shown += delegate { shown++; };
                form.VisibleChanged += delegate { everVisible |= form.Visible; };
                form.FormClosed += delegate { closed = true; };
                IntPtr handle = form.Handle;
                watchdog.Tick += delegate { failure = new TimeoutException("The actual background message loop failed to close."); Application.ExitThread(); };
                form.BeginInvoke((Action)delegate
                {
                    try
                    {
                        Check(!form.Visible && !everVisible && shown == 0, "Entering the real application message loop never shows or flashes the background window.");
                        Check(Field<bool>(form, "sessionStarted") && form.IsHandleCreated, "The background entry initializes its live message target before dispatching callbacks.");
                        CheckPassive(form); observed = true;
                        form.Close();
                    }
                    catch (Exception error) { failure = error; Application.ExitThread(); }
                });
                watchdog.Start();
                MethodInfo entry = typeof(AppProgram).GetMethod("RunInBackground", BindingFlags.Static | BindingFlags.NonPublic);
                Check(entry != null, "The test calls the same background entry used by the real application.");
                try { entry.Invoke(null, new object[] { form }); }
                catch (TargetInvocationException error) { throw error.InnerException; }
                finally { watchdog.Stop(); }
                if (failure != null) throw failure;
                Check(observed && closed && form.IsDisposed, "Closing the hidden form exits its actual message loop and disposes the form.");
                Check(!everVisible && shown == 0, "The whole background start-to-close lifecycle remains invisible.");
            }
        }

        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                System.Threading.Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
                StartupRegistration(Path.GetFullPath(args[0]));
                ReconnectPreferences();
                ReconnectCancelsForPendingEdits(Path.GetFullPath(args[0]));
                BackgroundAndRestore(Path.GetFullPath(args[0]));
                BackgroundMessageLoop(Path.GetFullPath(args[0]));
                Console.WriteLine("PASS: " + assertions + " background UI assertions (no hardware, hooks, registry or helper processes).");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
