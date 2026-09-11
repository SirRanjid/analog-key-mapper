using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Tk75.App;
using Tk75.Mapping;

namespace Tk75.Tests
{
    // Real controls and MainForm status routing, without native output helpers.
    public static class ControllerPendingUiHarness
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        static int assertions;
        static void Check(bool value, string message) { assertions++; if (!value) throw new InvalidOperationException(message); }
        static T Field<T>(object owner, string name) { return (T)owner.GetType().GetField(name, Private).GetValue(owner); }
        static void Set(object owner, string name, object value) { owner.GetType().GetField(name, Private).SetValue(owner, value); }
        static object Call(object owner, string method, params object[] values)
        {
            try { return owner.GetType().GetMethods(Private).Single(m => m.Name == method && m.GetParameters().Length == values.Length).Invoke(owner, values); }
            catch (TargetInvocationException error) { throw error.InnerException; }
        }
        static RectangleF Plug(ControllerConnector value) { return (RectangleF)typeof(ControllerConnector).GetProperty("PlugBounds", Private).GetValue(value, null); }
        static Point Center(RectangleF value) { return Point.Round(new PointF(value.Left + value.Width / 2, value.Top + value.Height / 2)); }
        static void Mouse(ControllerConnector value, string method, Point point) { Call(value, method, new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0)); }
        static void Click(ControllerConnector value) { Point point = Center(Plug(value)); Mouse(value, "OnMouseDown", point); Mouse(value, "OnMouseUp", point); }
        static bool Busy(ControllerConnector value) { return (value.AccessibilityObject.State & AccessibleStates.Busy) != 0; }
        static void Capture(ControllerConnector value, string path)
        { using (var image = new Bitmap(value.Width, value.Height)) { value.DrawToBitmap(image, value.ClientRectangle); image.Save(path, ImageFormat.Png); } }

        static void GestureStates(string directory, bool vertical)
        {
            string label = vertical ? "mini" : "main";
            using (var window = new Form { Location = new Point(-30000, -30000), StartPosition = FormStartPosition.Manual, ShowInTaskbar = false, Size = new Size(700, 200) })
            using (var connector = new ControllerConnector { Vertical = vertical, Compact = true, Size = vertical ? new Size(56, 64) : new Size(560, 84) })
            {
                window.Controls.Add(connector); window.Show(); Application.DoEvents();
                var requests = new List<bool>();
                connector.ConnectionRequested += delegate(bool value) { requests.Add(value); connector.SetConnection(false, null, value); };
                RectangleF rest = Plug(connector);
                connector.SetConnection(true, null); RectangleF dock = Plug(connector); connector.SetConnection(false, null);
                Point pickup = Center(rest), end = new Point(pickup.X + (int)Math.Round(dock.X - rest.X), pickup.Y + (int)Math.Round(dock.Y - rest.Y));
                Mouse(connector, "OnMouseDown", pickup); Mouse(connector, "OnMouseMove", end); Mouse(connector, "OnMouseUp", end);
                Check(requests.SequenceEqual(new[] { true }) && Plug(connector) == dock, label + ": an asynchronous drop remains docked after its event returns.");
                Check(Busy(connector) && connector.AccessibilityObject.Value == "Connecting…" && connector.AccessibilityObject.DefaultAction == "Cancel connection", label + ": pending is distinct from successful connection and exposes cancellation.");
                for (int i = 0; i < 4; i++) { connector.SetConnection(false, null, true); Application.DoEvents(); }
                Check(Plug(connector) == dock && requests.Count == 1, label + ": repeated owner refresh does not move or resubmit the pending plug.");
                Capture(connector, Path.Combine(directory, label + "-pending.png"));

                // Escape cancels the local gesture, not the owner's in-flight job.
                Point held = Center(dock); Mouse(connector, "OnMouseDown", held);
                Point outward = new Point(held.X + (vertical ? 0 : -40), held.Y + (vertical ? 14 : 0));
                Mouse(connector, "OnMouseMove", outward); Call(connector, "OnKeyDown", new KeyEventArgs(Keys.Escape));
                Mouse(connector, "OnMouseUp", outward);
                Check(Plug(connector) == dock && requests.Count == 1, label + ": cancelled local dragging retains the pending runtime state.");
                connector.Enabled = false; connector.Enabled = true;
                Check(Plug(connector) == dock && Busy(connector), label + ": temporary disabling does not invent a connection result.");
                Click(connector);
                Check(requests.SequenceEqual(new[] { true, false }) && Plug(connector) == rest && !Busy(connector), label + ": second click cancels and owner acknowledgement restores the resting plug.");

                connector.SetConnection(false, null, true); connector.SetConnection(true, null, false);
                Check(Plug(connector) == dock && !Busy(connector) && connector.AccessibilityObject.Value == "Connected", label + ": successful completion preserves position and clears pending feedback.");
                connector.SetConnection(false, "Synthetic connection failure", false);
                Check(Plug(connector) == rest && !Busy(connector) && connector.AccessibleDescription.Contains("Synthetic connection failure"), label + ": failed completion resets position and retains its reason.");
                Capture(connector, Path.Combine(directory, label + "-failed.png"));
                connector.SetConnection(false, null, true);
                connector.AccessibilityObject.DoDefaultAction();
                Check(!requests.Last() && Plug(connector) == rest, label + ": accessibility activation cancels a pending attempt.");
                connector.SetConnection(false, null, true);
                int before = requests.Count;
                Call(connector, "OnKeyDown", new KeyEventArgs(Keys.Space)); Call(connector, "OnKeyDown", new KeyEventArgs(Keys.Space)); Call(connector, "OnKeyUp", new KeyEventArgs(Keys.Space));
                Check(requests.Count == before + 1 && !requests.Last(), label + ": a held keyboard activation cancels once without reconnecting.");
                connector.SetConnection(false, null, true);
                Mouse(connector, "OnMouseDown", Center(Plug(connector))); connector.SetConnection(false, "Failed while held", false);
                Mouse(connector, "OnMouseUp", Center(dock));
                Check(Plug(connector) == rest && requests.Count == before + 1, label + ": owner failure during a pressed gesture cannot emit a stale request.");
            }
        }

        sealed class FakeController : IControllerSession, IAsyncControllerSession
        {
            internal Profile Profile;
            internal bool Disposed;
            internal int Cancellations;
            TaskCompletionSource<object> pending;
            public bool Enabled { get; private set; }
            public bool Connecting { get; private set; }
            public ControllerFrame Frame { get { return new ControllerFrame(); } }
            public PreviewSnapshot Preview { get { return new PreviewSnapshot(); } }
            public string Status { get { return "Synthetic pending endpoint"; } }
            public void Configure(Profile profile, IDictionary<int, Calibration> calibration) { Profile = ProfileJson.Clone(profile); }
            public void SetInputSource(object value) { }
            public void SetKeyboardMode(bool value) { }
            public void Enable() { Enabled = true; }
            public Task EnableAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); pending = new TaskCompletionSource<object>(); Connecting = true; return pending.Task; }
            public Task CancelPendingConnection() { Cancellations++; Connecting = false; if (pending != null) pending.TrySetCanceled(); return Task.FromResult(0); }
            internal void Finish(bool success) { Connecting = false; Enabled = success; if (success) pending.TrySetResult(null); else pending.TrySetCanceled(); }
            public void Disable(string reason) { Enabled = false; }
            public ControllerRelease PrepareDisable(string reason) { bool active = Enabled; Enabled = false; return active ? new ControllerRelease(delegate { }, delegate { }) : null; }
            public void Dispose() { CancelPendingConnection(); Enabled = false; Disposed = true; }
        }

        static void FooterRouting(string directory)
        {
            using (var form = new MainForm(Path.Combine(directory, "data"), true) { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000) })
            {
                form.StartInBackground(); form.RestoreFromTray(); Application.DoEvents();
                var created = new List<FakeController>();
                var runtime = new MultiControllerSession(delegate { var endpoint = new FakeController(); created.Add(endpoint); return endpoint; });
                Field<MultiControllerSession>(form, "runtime").Dispose(); Set(form, "runtime", runtime);
                Profile profile = Field<EditHistory>(form, "history").Current;
                profile.Controllers = ControllerRouting.EffectiveControllers(profile).ToList();
                string firstId = profile.Controllers[0].Id;
                profile.Controllers.Add(new ControllerDefinition { Id = "peer", Name = "Peer", Kind = ControllerKind.DualSense });
                Set(form, "history", new EditHistory(profile));
                Call(form, "Configure"); Call(form, "SyncOutputMode");
                FakeController first = created.Single(c => !c.Disposed && c.Profile.Controllers[0].Id == firstId);
                FakeController peer = created.Single(c => !c.Disposed && c.Profile.Controllers[0].Id == "peer");
                Task firstJob = runtime.EnableControllerAsync(firstId, CancellationToken.None);
                Task peerJob = runtime.EnableControllerAsync("peer", CancellationToken.None);
                Call(form, "UpdateControllerConnectionUi");
                var main = Field<ControllerConnector>(form, "controllerConnector");
                var footer = Field<Dictionary<string, ControllerConnector>>(form, "footerConnectors");
                Check(Busy(main) && Busy(footer[firstId]) && Busy(footer["peer"]), "Main and both footer slots show their genuine in-flight jobs.");
                first.Finish(true); Call(form, "UpdateControllerConnectionUi");
                Check(!Busy(main) && !Busy(footer[firstId]) && Busy(footer["peer"]) && firstJob.IsCompleted && !peerJob.IsCompleted, "Completing one output leaves its pending peer intact.");
                runtime.SelectedControllerId = "peer"; Call(form, "SyncOutputMode"); Call(form, "UpdateControllerConnectionUi");
                Check(Busy(main) && !Busy(footer[firstId]) && Busy(footer["peer"]), "Selecting another controller changes only the main connector's displayed state.");
                int cancellations = peer.Cancellations;
                // The actual footer callback and async MainForm cancellation path
                // run here; connecting is injected via the fake runtime only.
                footer["peer"].AccessibilityObject.DoDefaultAction(); Application.DoEvents();
                Check(peerJob.IsCanceled && peer.Cancellations > cancellations && !Busy(main) && !Busy(footer["peer"]), "Footer cancellation routes to the pending slot and refreshes main and mini status.");
                Check(runtime.IsControllerEnabled(firstId) && runtime.SelectedControllerId == "peer", "Cancelling a peer preserves the connected output and editing selection.");
                Check(Field<object>(form, "reader") == null && !Field<bool>(form, "discoveryEnabled") && !Field<bool>(form, "applicationInputActive"), "The integration uses no reader, discovery or input hooks.");
            }
        }

        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); UiText.SetLanguage("en");
                string directory = args[0]; GestureStates(directory, false); GestureStates(directory, true); FooterRouting(directory);
                Console.WriteLine("PASS: " + assertions + " pending connector assertions; synthetic UI and endpoints only."); return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
