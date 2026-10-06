using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Tk75.Mapping;

namespace Tk75.App
{
    public sealed partial class MainForm
    {
        readonly ControllerPreview gamepadInputPreview = new ControllerPreview { Dock = DockStyle.Fill, CompactStatus = true, Visible = false, IsInputSurface = true };
        readonly SleekComboBox inputSourceMode = new SleekComboBox(), gamepadDevices = new SleekComboBox();
        readonly Dictionary<InputMode, string> inputModeProfiles = new Dictionary<InputMode, string>();
        Label inputDeviceCaption, inputMappingHint;
        SleekButton inputConnectButton;
        bool syncingGamepadMode, gamepadScanRunning;
        InputMode displayedInputMode;
        string gamepadScanError;
        string displayedGamepadProfile;
        bool IsGamepadInput { get { return history != null && UiReadProfile.InputMode != InputMode.Keyboard; } }
        ReaderSession ActiveRoutingReader { get { return IsGamepadInput ? null : reader; } }
        ControllerStyle GamepadInputStyle { get { return UiReadProfile.InputMode == InputMode.PlayStationController ? ControllerStyle.PlayStation5 : ControllerStyle.Xbox; } }
        string GamepadInputShortLabel(int index)
        {
            string[] axes = { "LX+", "LX−", "LY+", "LY−", "RX+", "RX−", "RY+", "RY−" };
            if (index < axes.Length) return axes[index];
            string label = ControllerPresentation.ShortLabel(GamepadInputProfile.Target(index), GamepadInputStyle);
            return String.IsNullOrWhiteSpace(label) ? Label(index) : label;
        }

        sealed class GamepadDeviceItem
        {
            internal GamepadInputDevice Device;
            internal string Text;
            public override string ToString() { return Text; }
        }
        void BuildInputSourcePicker(FlowLayoutPanel legend)
        {
            Combo(inputSourceMode, 170, new object[] { "", "", "" });
            inputSourceMode.AccessibleName = Tr("Eingabequelle", "Input source");
            inputSourceMode.SelectionChangeCommitted += delegate {
                if (syncingGamepadMode || inputSourceMode.SelectedIndex < 0) return;
                Attempt(delegate { SwitchInputSourceMode((InputMode)inputSourceMode.SelectedIndex); });
                RefreshGamepadModeUi();
            };
            gamepadDevices.DropDown += delegate { ScanGamepads(); };
            Shown += delegate { ScanGamepads(); };
            legend.Controls.Add(inputSourceMode);
        }
        void BuildGamepadInputSurface(Panel host)
        {
            host.Controls.Add(gamepadInputPreview);
            gamepadInputPreview.TargetSelected += delegate(OutputTarget control) { SelectGamepadControl(control, (ModifierKeys & Keys.Control) != 0); ShowKeyDetails(); };
            gamepadInputPreview.TargetDragRequested += StartGamepadInputDrag;
            gamepadInputPreview.AllowDrop = true;
            gamepadInputPreview.DragEnter += UpdateGamepadDrop;
            gamepadInputPreview.DragOver += UpdateGamepadDrop;
            gamepadInputPreview.DragLeave += delegate { gamepadInputPreview.SetDropTarget(null); SetDefaultDragHint(); };
            gamepadInputPreview.DragDrop += DropOnGamepad;
            gamepadInputPreview.QueryContinueDrag += delegate(object sender, QueryContinueDragEventArgs e) {
                if (e.EscapePressed || !CanContinueMappingDrag() || !DragFocusIsInMapper()) { CancelMappingDrag(); e.Action = DragAction.Cancel; }
            };
        }
        void SwitchInputSourceMode(InputMode mode)
        {
            if (mode == UiReadProfile.InputMode) return;
            settings.EndEdit(); FlushInputDraft(); SaveProfile(); CancelMappingDrag();
            inputModeProfiles[UiReadProfile.InputMode] = profilePath;
            string path;
            if (inputModeProfiles.TryGetValue(mode, out path) && System.IO.File.Exists(path)) { LoadProfile(path); return; }
            foreach (string candidate in store.Profiles())
            {
                try { if (ReadProfile(candidate).InputMode == mode) { LoadProfile(candidate); return; } }
                catch (Exception error) { store.Event("Input profile unavailable: " + error.Message); }
            }
            var profile = mode == InputMode.Keyboard ? new Profile { Name = Tr("Tastatur", "Keyboard") } :
                GamepadInputProfile.Create(mode, mode == InputMode.XboxController ? "Xbox · Remap" : "PlayStation · Remap");
            path = store.NewProfilePath(); WorkspaceStore.WriteAtomic(path, ProfileJson.Serialize(profile)); LoadProfile(path);
            if (IsGamepadInput) SelectGamepadControl(OutputTarget.A, false);
        }
        void RefreshGamepadModeUi()
        {
            if (inputDeviceCaption == null || history == null) return;
            bool gamepad = IsGamepadInput;
            InputMode mode = UiReadProfile.InputMode;
            bool changed = mode != displayedInputMode; displayedInputMode = mode;
            syncingGamepadMode = true;
            try {
                inputSourceMode.Items[0] = Tr("Eingabe: Tastatur", "Input: Keyboard"); inputSourceMode.Items[1] = "Input: Xbox"; inputSourceMode.Items[2] = "Input: PlayStation";
                inputSourceMode.SelectedIndex = (int)mode;
                keyCardTips.SetToolTip(inputSourceMode, Tr("Wechselt zum Profil dieser Eingabequelle. Beim ersten Wechsel wird ein eigenes Profil angelegt; vorhandene Zuordnungen bleiben erhalten.", "Switches to a profile for this input source. The first switch creates a separate profile and preserves existing mappings."));
                keyboard.Visible = !gamepad; gamepadInputPreview.Visible = gamepad;
                controllerPreview.GamepadMappings = gamepad;
                if (rgbSettingsCard != null) rgbSettingsCard.Visible = !gamepad;
                devices.Visible = !gamepad; gamepadDevices.Visible = gamepad; layoutMode.Visible = !gamepad;
                inputDeviceCaption.Text = gamepad ? Tr("EINGABE-CONTROLLER", "INPUT CONTROLLER") : Tr("TASTATUR", "KEYBOARD");
                keyboardTitle.Text = gamepad ? Tr("Dein Controller", "Your controller") : Tr("Deine Tastatur", "Your keyboard");
                inputMappingHint.Text = gamepad ? Tr("Ziehen: Eingabe ↔ Ausgabe", "Drag: input ↔ output") : Tr("Ziehen: Taste ↔ Controller", "Drag: key ↔ controller");
                inputMappingHint.Width = gamepad ? 280 : 215;
                if (gamepad) {
                    gamepadInputPreview.Style = GamepadInputStyle; gamepadInputPreview.BringToFront(); gamepadDevices.BringToFront();
                    gamepadInputPreview.AccessibleName = Tr("Eingabe-Controller · frei zuordnen", "Input controller · remap controls");
                    string identity = profilePath + "\n" + UiReadProfile.InputDeviceId;
                    if (changed || gamepadDevices.Items.Count == 0 || displayedGamepadProfile != identity) { displayedGamepadProfile = identity; gamepadDevices.Items.Clear(); gamepadDevices.Items.Add(new GamepadDeviceItem { Text = UiReadProfile.InputDeviceName ?? Tr("Controller auswählen …", "Choose a controller…") }); gamepadDevices.SelectedIndex = 0; ScanGamepads(); }
                }
                controllerOnlyInput.Visible = suppressionDetails.Visible = suppressionState.Visible = !gamepad;
                showAll.Enabled = !gamepad;
                RefreshGamepadSelection(); RefreshInputModeUi(); UpdateDetailsButtons();
            } finally { syncingGamepadMode = false; }
        }
        void ScanGamepads()
        {
            if (previewMode || !IsGamepadInput || !IsHandleCreated || discoveryContext == null || gamepadScanRunning || closing) return;
            gamepadScanRunning = true; InputMode mode = UiReadProfile.InputMode;
            ThreadPool.QueueUserWorkItem(delegate {
                GamepadInputDevice[] found = new GamepadInputDevice[0]; string error = null;
                try { found = GamepadInputSource.Enumerate(); } catch (Exception failure) { error = failure.Message; }
                PostDiscovery(delegate {
                    gamepadScanRunning = false; if (closing || !IsGamepadInput) return; if (mode != UiReadProfile.InputMode) { ScanGamepads(); return; }
                    gamepadScanError = error;
                    var previous = gamepadDevices.SelectedItem as GamepadDeviceItem;
                    string selectedId = previous != null && previous.Device != null ? previous.Device.DeviceId : UiReadProfile.InputDeviceId;
                    gamepadDevices.BeginUpdate();
                    try {
                        gamepadDevices.Items.Clear();
                        foreach (var device in found.Where(d => d.Kind == GamepadInputProfile.DisplayKind(mode))) gamepadDevices.Items.Add(new GamepadDeviceItem { Device = device, Text = device.DisplayName });
                        for (int i = 0; i < gamepadDevices.Items.Count; i++) if (((GamepadDeviceItem)gamepadDevices.Items[i]).Device.DeviceId == selectedId) gamepadDevices.SelectedIndex = i;
                        if (gamepadDevices.Items.Count == 1) gamepadDevices.SelectedIndex = 0;
                        if (gamepadDevices.Items.Count == 0) { gamepadDevices.Items.Add(new GamepadDeviceItem { Text = Tr("Kein physischer Controller gefunden", "No physical controller found") }); gamepadDevices.SelectedIndex = 0; }
                    } finally { gamepadDevices.EndUpdate(); }
                });
            });
        }
        void ConnectGamepad()
        {
            var selected = gamepadDevices.SelectedItem as GamepadDeviceItem;
            if (selected == null || selected.Device == null) { ScanGamepads(); throw new InvalidOperationException(Tr("Controller anschließen und oben auswählen. Die Geräteliste wird aktualisiert.", "Connect a controller and select it above. Refreshing the device list.")); }
            FlushInputDraft();
            var profile = GamepadInputProfile.WithDevice(history.Current, selected.Device.DeviceId, selected.Device.DisplayName);
            // Open and persist before replacing the currently working source.
            var source = GamepadInputSource.Open(selected.Device);
            try { WorkspaceStore.WriteAtomic(profilePath, ProfileJson.Serialize(profile)); }
            catch { source.Dispose(); throw; }
            ILearnedInputDeviceSource old;
            if (learnedSources.TryGetValue(source.DeviceId, out old)) old.Dispose();
            learnedSources[source.DeviceId] = source;
            Commit(profile); ConfigureLearnedInputs(); RefreshKeys(); RefreshBindings();
        }
        void SelectGamepadControl(OutputTarget control, bool toggle)
        {
            int index = GamepadInputProfile.KeyIndex(control); bool selected = keys.Rows[index].Selected;
            bool previous = updating; updating = true;
            try { if (!toggle) keys.ClearSelection(); keys.Rows[index].Visible = true; keys.Rows[index].Selected = !toggle || !selected; }
            finally { updating = previous; }
            RefreshBindings(); HighlightKeys();
        }
        void RefreshGamepadSelection()
        {
            if (!IsGamepadInput || gamepadInputPreview.IsDisposed) return;
            gamepadInputPreview.SetSelectedInputs(SelectedKeys().Where(i => i < GamepadInputProfile.ControlCount).Select(GamepadInputProfile.Target));
        }
        void UpdateGamepadLive()
        {
            ControllerFrame frame = null; ILearnedInputDeviceSource source; bool ready = false;
            if (UiReadProfile.InputDeviceId != null && learnedSources.TryGetValue(UiReadProfile.InputDeviceId, out source)) {
                var gamepad = source as GamepadInputSource; if (gamepad != null) ready = gamepad.TryGetFrame(out frame);
            }
            gamepadInputPreview.SetFrame(frame, ready, false);
        }
        string GamepadInputStatus()
        {
            ILearnedInputDeviceSource source;
            if (UiReadProfile.InputDeviceId != null && learnedSources.TryGetValue(UiReadProfile.InputDeviceId, out source)) return source.DisplayName + " · " + UiText.Get(source.Status);
            return gamepadScanError ?? (UiReadProfile.InputDeviceName == null ? Tr("Eingabe-Controller oben auswählen und verbinden", "Select and connect the input controller above") : UiReadProfile.InputDeviceName + Tr(" · nicht verbunden", " · disconnected"));
        }
        void StartGamepadInputDrag(OutputTarget control)
        {
            if (!IsGamepadInput || closing || activeMappingDrag != null) return;
            int index = GamepadInputProfile.KeyIndex(control);
            if (!SelectedKeys().Contains(index)) SelectGamepadControl(control, false);
            using (MappingDragVisual visual = gamepadInputPreview.CreateDragVisual(control)) {
                if (visual == null) return;
                StartMappingDrag(gamepadInputPreview, PrepareKeyMappingDrag(SelectedKeys()), visual);
            }
        }
        int[] GamepadDropKeys(DragEventArgs e)
        {
            var target = gamepadInputPreview.HitTestTarget(gamepadInputPreview.PointToClient(new Point(e.X, e.Y)));
            if (!target.HasValue) return new int[0];
            int index = GamepadInputProfile.KeyIndex(target.Value); int[] selected = SelectedKeys();
            return selected.Contains(index) ? selected : new[] { index };
        }
        void UpdateGamepadDrop(object sender, DragEventArgs e)
        {
            int[] indices = IsOurMappingDrag(e) && activeMappingDrag.Target.HasValue ? GamepadDropKeys(e) : new int[0];
            if (indices.Length != 0) indices = activeMappingDrag.Pairings.MissingKeys(indices, activeMappingDrag.Target.Value);
            gamepadInputPreview.SetDropTarget(indices.Length == 0 ? (OutputTarget?)null : GamepadInputProfile.Target(indices[0]));
            e.Effect = indices.Length == 0 ? DragDropEffects.None : DragDropEffects.Copy;
            if (indices.Length != 0) SetDragHint(DragKeyNames(indices) + " → " + TargetLabel(activeMappingDrag.Target.Value));
        }
        void DropOnGamepad(object sender, DragEventArgs e)
        {
            e.Effect = DragDropEffects.None;
            if (!IsOurMappingDrag(e) || !activeMappingDrag.Target.HasValue || !DragProfileUnchanged()) return;
            int[] indices = GamepadDropKeys(e);
            if (indices.Length == 0 || activeMappingDrag.Pairings.MissingKeys(indices, activeMappingDrag.Target.Value).Length == 0) return;
            Attempt(delegate { var drag = activeMappingDrag; AddDroppedMapping(indices, drag.Target.Value); drag.Completed = true; e.Effect = DragDropEffects.Copy; });
        }
    }
}
