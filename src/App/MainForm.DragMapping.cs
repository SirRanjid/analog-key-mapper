using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Tk75.Mapping;
using Tk75.Output;

namespace Tk75.App
{
    public sealed partial class MainForm
    {
        readonly SleekComboBox controllerKindPicker = new SleekComboBox { IgnoreClosedTextInput = true };
        readonly SleekComboBox controllerSlotPicker = new SleekComboBox { IgnoreClosedTextInput = true };
        readonly SleekComboBox mainControllerSlotPicker = new SleekComboBox { IgnoreClosedTextInput = true };
        readonly Button addControllerSlot = new SleekButton { Text = "+", IconOnly = true, Width = 34, Height = 34, Margin = new Padding(3, 0, 3, 0) };
        readonly Button removeControllerSlot = new SleekButton { Text = "−", IconOnly = true, Width = 34, Height = 34, Margin = new Padding(3, 0, 9, 0) };
        readonly Label mappingDragHint = new Label { Dock = DockStyle.Bottom, Height = 36, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleCenter, Tag = "muted" };
        readonly LinkLabel controllerSetup = new LinkLabel { AutoSize = true, Margin = new Padding(14, 9, 0, 0) };
        readonly ControllerConnector controllerConnector = new ControllerConnector { Dock = DockStyle.Bottom, Compact = true, Height = 62 };
        readonly Label controllerNameCaption = new Label { AutoSize = true, Margin = new Padding(0, 7, 8, 0), Tag = "muted" };
        readonly LinkLabel controllerNameEdit = new LinkLabel { AutoSize = true, MaximumSize = new Size(520, 28), AutoEllipsis = true, Margin = new Padding(0, 7, 0, 0) };
        bool syncingController;
        ControllerKind selectedControllerKind;
        MappingDrag activeMappingDrag;

        sealed class MappingDrag
        {
            public string Token, ProfilePath, ProfileJson, ControllerId;
            public int[] Keys;
            public OutputTarget? Target;
            public KeyboardLayout Layout;
            public MappingPairingPolicy Pairings;
            public DragPreviewWindow Preview;
            public bool Cancelled;
        }
        sealed class ControllerSlotItem
        {
            public string Id, Name;
            public int Number;
            public override string ToString() { return Number + " · " + Name; }
        }
        ControllerStyle CurrentControllerStyle { get { return selectedControllerKind == ControllerKind.DualSense ? ControllerStyle.PlayStation5 : ControllerStyle.Xbox; } }
        string OutputAvailability { get { return ControllerOutputs.AvailabilityError(selectedControllerKind); } }
        string TargetLabel(OutputTarget target) { return ControllerPresentation.Label(target, CurrentControllerStyle); }
        ControllerDefinition SelectedControllerDefinition
        {
            get
            {
                var definitions = ControllerRouting.EffectiveControllers(UiReadProfile);
                return definitions.FirstOrDefault(d => d.Id == runtime.SelectedControllerId) ?? definitions[0];
            }
        }
        string ControllerDisplayName { get { return SelectedControllerDefinition.Name; } }

        void BuildControllerMapping()
        {
            var bar = new TableLayoutPanel { Dock = DockStyle.Top, Height = 42, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52)); bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40)); bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44)); bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
            bar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Combo(controllerSlotPicker, 175, new object[0]);
            controllerSlotPicker.Dock = DockStyle.Fill; controllerSlotPicker.Margin = new Padding(3, 0, 3, 0); bar.Controls.Add(controllerSlotPicker, 0, 0);
            bar.Controls.Add(addControllerSlot, 1, 0); bar.Controls.Add(removeControllerSlot, 2, 0);
            controllerSlotPicker.SelectedIndexChanged += delegate { ChangeControllerSelection(controllerSlotPicker); };
            mainControllerSlotPicker.SelectedIndexChanged += delegate { ChangeControllerSelection(mainControllerSlotPicker); };
            addControllerSlot.Click += delegate { Attempt(AddControllerSlot); };
            removeControllerSlot.Click += delegate { Attempt(RemoveControllerSlot); };
            Combo(controllerKindPicker, 175, new object[] { "Xbox 360", "PS5 · DualSense" });
            controllerKindPicker.Dock = DockStyle.Fill; controllerKindPicker.Margin = new Padding(3, 0, 3, 0); bar.Controls.Add(controllerKindPicker, 3, 0);
            controllerKindPicker.SelectedIndexChanged += delegate
            {
                if (syncingController || controllerKindPicker.SelectedIndex < 0) return;
                Attempt(delegate
                {
                    Commit(ControllerRouting.SetKind(history.Current, runtime.SelectedControllerId,
                        controllerKindPicker.SelectedIndex == 1 ? ControllerKind.DualSense : ControllerKind.Xbox360));
                });
            };
            controllerSetup.Text = Tr("Einrichtung offen", "Setup pending");
            controllerSetup.LinkClicked += delegate { MessageBox.Show(this, UiText.Get(OutputAvailability), Tr("Controller einrichten", "Controller setup"), MessageBoxButtons.OK, MessageBoxIcon.Information); };
            controllerPanel.Controls.Add(bar);
            UiText.PreserveText(mappingDragHint); SetDefaultDragHint();
            UiText.PreserveText(controllerNameEdit);
            controllerNameEdit.LinkClicked += delegate { Attempt(RenameControllerDisplay); };
            controllerPanel.Controls.Add(controllerConnector);
            controllerPanel.Controls.Add(BuildRgbUi());
            // Dock the figure after its surrounding controls have reserved space.
            controllerPreview.BringToFront();
            controllerConnector.ConnectionRequested += SetControllerConnection;

            keyboard.AllowDrop = controllerPreview.AllowDrop = true;
            keyboard.KeyDragRequested += delegate(int[] indices) { Attempt(delegate { StartKeyDrag(indices); }); };
            controllerPreview.TargetDragRequested += delegate(OutputTarget target) { Attempt(delegate { StartControllerDrag(target); }); };
            controllerPreview.TargetSelected += delegate(OutputTarget target) {
                SelectTarget(target);
                if (SelectedKeys().Length != 0) SetDetailMode(null, true);
            };
            controllerPreview.DragEnter += UpdateControllerDrag;
            controllerPreview.DragOver += UpdateControllerDrag;
            controllerPreview.DragLeave += delegate { controllerPreview.SetDropTarget(null); SetDefaultDragHint(); };
            controllerPreview.DragDrop += DropOnController;
            keyboard.DragEnter += UpdateKeyboardDrag;
            keyboard.DragOver += UpdateKeyboardDrag;
            keyboard.DragLeave += delegate { keyboard.SetDropKeys(new int[0]); SetDefaultDragHint(); };
            keyboard.DragDrop += DropOnKeyboard;
            QueryContinueDragEventHandler cancel = delegate(object sender, QueryContinueDragEventArgs e)
            {
                if (e.EscapePressed || !CanContinueMappingDrag() || !DragFocusIsInMapper())
                { CancelMappingDrag(); e.Action = DragAction.Cancel; }
            };
            keyboard.QueryContinueDrag += cancel; controllerPreview.QueryContinueDrag += cancel;
            SyncOutputMode();
        }
        void SyncOutputMode()
        {
            var definitions = ControllerRouting.EffectiveControllers(UiReadProfile);
            var selectedDefinition = SelectedControllerDefinition;
            selectedControllerKind = selectedDefinition.Kind;
            syncingController = true;
            try
            {
                SyncControllerPicker(controllerSlotPicker, definitions, selectedDefinition.Id);
                SyncControllerPicker(mainControllerSlotPicker, definitions, selectedDefinition.Id);
                controllerKindPicker.SelectedIndex = selectedControllerKind == ControllerKind.DualSense ? 1 : 0; controllerPreview.Style = CurrentControllerStyle;
            }
            finally { syncingController = false; }
            addControllerSlot.Enabled = definitions.Count < ControllerRouting.MaximumControllers; removeControllerSlot.Enabled = definitions.Count > 1;
            keyCardTips.SetToolTip(addControllerSlot, Tr("Weiteren Controller hinzufügen", "Add another controller"));
            keyCardTips.SetToolTip(removeControllerSlot, Tr("Ausgewählten Controller und seine Zuordnungen entfernen", "Remove the selected controller and its mappings"));
            addControllerSlot.AccessibleName = Tr("Weiteren Controller hinzufügen", "Add another controller");
            removeControllerSlot.AccessibleName = Tr("Ausgewählten Controller entfernen", "Remove selected controller");
            mainControllerSlotPicker.AccessibleName = Tr("Spieler für Zuordnungen", "Player for mappings");
            controllerSlotPicker.AccessibleName = mainControllerSlotPicker.AccessibleName;
            string pickerHint = Tr("Neue Zuordnungen gelten für diesen Controller. Die Zahlen auf den Tasten zeigen die Controller in diesem Profil; +N bedeutet weitere Zuordnungen.", "New mappings apply to this controller. Numbers on keys identify controllers in this profile; +N means more assignments.");
            keyCardTips.SetToolTip(mainControllerSlotPicker, pickerHint); keyCardTips.SetToolTip(controllerSlotPicker, pickerHint);
            targets.Invalidate(); controllerSetup.Visible = OutputAvailability != null;
            controllerSetup.Text = Tr("Einrichtung offen", "Setup pending");
            keyCardTips.SetToolTip(controllerSetup, UiText.Get(OutputAvailability));
            RefreshKeyControllerAssignments();
            SetDefaultDragHint(); RefreshControllerNameUi(); controllerConnector.RefreshLanguage(); UpdateControllerConnectionUi(); UpdateDetailsTitle();
            RefreshRgbUi();
        }
        void ChangeControllerSelection(SleekComboBox picker)
        {
            var item = picker.SelectedItem as ControllerSlotItem;
            if (syncingController || item == null || closing || deviceDetachInProgress || item.Id == runtime.SelectedControllerId) return;
            Attempt(delegate
            {
                try
                {
                    settings.EndEdit(); FlushInputDraft(); CancelMappingDrag();
                    runtime.SelectedControllerId = item.Id;
                    RefreshKeys(); RefreshBindings(); SetDetailMode("controller", true);
                }
                finally { SyncOutputMode(); }
            });
        }
        static void SyncControllerPicker(SleekComboBox picker, System.Collections.Generic.IList<ControllerDefinition> definitions, string selectedId)
        {
            bool rebuild = picker.Items.Count != definitions.Count;
            for (int i = 0; !rebuild && i < definitions.Count; i++)
            {
                var item = picker.Items[i] as ControllerSlotItem;
                if (item == null || item.Id != definitions[i].Id || item.Name != definitions[i].Name || item.Number != i + 1) rebuild = true;
            }
            if (rebuild)
            {
                picker.BeginUpdate();
                try
                {
                    picker.Items.Clear();
                    for (int i = 0; i < definitions.Count; i++) picker.Items.Add(new ControllerSlotItem { Id = definitions[i].Id, Name = definitions[i].Name, Number = i + 1 });
                }
                finally { picker.EndUpdate(); }
            }
            for (int i = 0; i < picker.Items.Count; i++)
                if (((ControllerSlotItem)picker.Items[i]).Id == selectedId) { picker.SelectedIndex = i; break; }
        }
        void RefreshKeyControllerAssignments()
        {
            Profile profile = history.Current; var definitions = ControllerRouting.EffectiveControllers(profile);
            var byKey = profile.Bindings.ToLookup(b => b.KeyIndex);
            for (int index = 0; index < 256; index++)
            {
                var badges = new System.Collections.Generic.List<KeyboardControllerBadge>();
                for (int slot = 0; slot < definitions.Count; slot++)
                {
                    ControllerDefinition definition = definitions[slot];
                    var assigned = byKey[index].Where(b => (string.IsNullOrEmpty(b.ControllerId) ? ControllerRouting.DefaultControllerId : b.ControllerId) == definition.Id).ToArray();
                    if (assigned.Length == 0) continue;
                    ControllerStyle style = definition.Kind == ControllerKind.DualSense ? ControllerStyle.PlayStation5 : ControllerStyle.Xbox;
                    badges.Add(new KeyboardControllerBadge { Number = slot + 1, Name = definition.Name, Selected = definition.Id == runtime.SelectedControllerId, Tint = RgbColor(RgbOverridePlan.GetControllerColor(profile, definition.Id)),
                        Targets = string.Join(", ", assigned.Select(b => ControllerPresentation.Label(b.Target, style) + (b.Enabled ? "" : Tr(" (aus)", " (off)")))) });
                }
                keyboard.SetControllerAssignments(index, badges);
            }
        }
        void AddControllerSlot()
        {
            settings.EndEdit(); FlushInputDraft();
            string id = Guid.NewGuid().ToString("N");
            var definitions = ControllerRouting.EffectiveControllers(UiReadProfile);
            int number = definitions.Count + 1;
            string name = "Controller " + number;
            while (definitions.Any(d => d.Name == name)) name = "Controller " + (++number);
            Commit(ControllerRouting.Add(history.Current, id, name, selectedControllerKind));
            runtime.SelectedControllerId = id;
            RefreshKeys(); RefreshBindings(); SyncOutputMode();
        }
        void RemoveControllerSlot()
        {
            settings.EndEdit(); FlushInputDraft();
            Commit(ControllerRouting.Remove(history.Current, runtime.SelectedControllerId));
            RefreshKeys(); RefreshBindings(); SyncOutputMode();
        }
        void RefreshControllerNameUi()
        {
            controllerNameCaption.Text = Tr("Name in dieser App", "Name in this app");
            controllerNameEdit.Text = ControllerDisplayName;
            keyCardTips.SetToolTip(controllerNameEdit, Tr("Zum Umbenennen klicken. Der Gerätename in Windows bleibt unverändert.", "Click to rename. The device name in Windows stays unchanged."));
        }
        void RenameControllerDisplay()
        {
            string value = Ask(Tr("Name in dieser App", "Name in this app"), ControllerDisplayName);
            if (value == null) return;
            Commit(ControllerRouting.Rename(history.Current, runtime.SelectedControllerId, value));
            SyncOutputMode(); UpdateDetailsTitle();
        }
        void SetControllerConnection(bool connect)
        { SetControllerConnectionForId(runtime.SelectedControllerId, connect); }
        void SetControllerConnectionForId(string controllerId, bool connect)
        {
            if (closing || deviceDetachInProgress || rgbClosePending) return;
            try
            {
                Attempt(delegate
                {
                    if (!connect) { runtime.DisableController(controllerId, Tr("Controller getrennt", "Controller disconnected")); return; }
                    ControllerDefinition definition = ControllerRouting.EffectiveControllers(UiReadProfile).FirstOrDefault(d => d.Id == controllerId);
                    if (definition == null) throw new InvalidOperationException(Tr("Dieser Controller ist nicht mehr im Profil vorhanden.", "This controller is no longer in the profile."));
                    string error = ControllerOutputs.AvailabilityError(definition.Kind);
                    if (error != null) throw new InvalidOperationException(UiText.Get(error));
                    settings.EndEdit(); FlushInputDraft(); runtime.EnableController(controllerId);
                });
            }
            finally { RefreshKeyboardSuppression(false); RefreshInputModeUi(); UpdateControllerConnectionUi(); }
        }
        // Called by the owner's live refresh after a global stop, input loss or a profile edit.
        // Never query MappingSession while its reader-detach transition is pending.
        void UpdateControllerConnectionUi()
        {
            if (closing || deviceDetachInProgress || rgbClosePending) { controllerConnector.Enabled = false; UpdateControllerFooterConnections(false, null); return; }
            if (!controllerConnector.Enabled) controllerConnector.Enabled = true;
            string availability = UiText.Get(OutputAvailability);
            controllerConnector.SetConnection(runtime.Enabled, availability);
            UpdateControllerFooterConnections(true, availability);
        }
        void SetDefaultDragHint()
        {
            if (activeMappingDrag != null && !activeMappingDrag.Cancelled)
            {
                bool any = activeMappingDrag.Keys != null ? activeMappingDrag.Pairings.AvailableTargets(activeMappingDrag.Keys).Length != 0 :
                    activeMappingDrag.Pairings.MissingKeys(LayoutIndices(), activeMappingDrag.Target.Value).Length != 0;
                SetDragHint(any ? Tr("Hell markierte Partner sind noch frei · Esc bricht ab.", "Highlighted partners are available · Esc cancels.") :
                    Tr("Alle möglichen Paare sind bereits zugeordnet · Esc bricht ab.", "Every possible pair is already mapped · Esc cancels."));
                return;
            }
            SetDragHint(Tr("Taste auf Controller ziehen – oder Controller-Button auf Taste.", "Drag a key onto the controller — or a controller button onto a key."));
        }
        void SetDragHint(string text)
        {
            if (mappingDragHint.Text == text) return;
            mappingDragHint.Text = text; keyCardTips.SetToolTip(mappingDragHint, text);
            keyCardTips.SetToolTip(controllerPanel, text);
        }
        void SelectTarget(OutputTarget target)
        { foreach (TargetItem item in targets.Items) if (item.Target == target) { targets.SelectedItem = item; break; } }
        void StartKeyDrag(int[] indices)
        {
            if (closing || activeMappingDrag != null || indices == null) return;
            int[] keysToMap = indices.Distinct().Where(i => keyboard.LayoutModel != null && keyboard.LayoutModel.FindByIndex(i) != null).ToArray();
            if (keysToMap.Length == 0) return;
            using (MappingDragVisual visual = keyboard.CreateDragVisual(keysToMap))
            {
                if (visual == null) return;
                FlushInputDraft(); SetDetailMode("controller", true, false); controllerPanel.PerformLayout();
                StartMappingDrag(keyboard, new MappingDrag { Keys = keysToMap }, visual);
            }
        }
        void StartControllerDrag(OutputTarget target)
        {
            if (closing || activeMappingDrag != null || !Enum.IsDefined(typeof(OutputTarget), target)) return;
            using (MappingDragVisual visual = controllerPreview.CreateDragVisual(target))
            {
                if (visual == null) return;
                FlushInputDraft(); ShowPage("mapping");
                StartMappingDrag(controllerPreview, new MappingDrag { Target = target }, visual);
            }
        }
        void StartMappingDrag(Control source, MappingDrag drag, MappingDragVisual visual)
        {
            var profile = history.Current;
            drag.Token = "AnalogKeyMapper:" + Guid.NewGuid().ToString("N");
            drag.ProfilePath = profilePath; drag.ProfileJson = ProfileJson.Serialize(profile);
            drag.ControllerId = runtime.SelectedControllerId;
            drag.Layout = keyboard.LayoutModel; drag.Pairings = new MappingPairingPolicy(profile, drag.ControllerId);
            activeMappingDrag = drag;
            // Only an opaque random string crosses OLE. Key indices and output
            // objects stay in this process; no external serialized objects load.
            Form owner = source.FindForm(); Exception feedbackError = null;
            EventHandler lostFocus = delegate
            {
                // OLE may activate our other owned form while hovering. Wait
                // until that transition completes, then reject external focus.
                if (IsDisposed || !IsHandleCreated) { CancelMappingDrag(); return; }
                try { BeginInvoke((MethodInvoker)delegate { if (object.ReferenceEquals(activeMappingDrag, drag) && !DragFocusIsInMapper()) CancelMappingDrag(); }); }
                catch (InvalidOperationException) { CancelMappingDrag(); }
            };
            EventHandler unavailable = delegate { if (source.IsDisposed || !source.Enabled || !source.Visible) CancelMappingDrag(); };
            GiveFeedbackEventHandler feedback = delegate(object sender, GiveFeedbackEventArgs e)
            {
                try
                {
                    UpdateMappingDragFeedback(e);
                }
                catch (Exception ex) { feedbackError = ex; CancelMappingDrag(); }
            };
            try
            {
                var data = new DataObject(); data.SetData(DataFormats.UnicodeText, false, drag.Token);
                source.GiveFeedback += feedback; source.LostFocus += lostFocus;
                source.EnabledChanged += unavailable; source.VisibleChanged += unavailable; source.Disposed += unavailable;
                Deactivate += lostFocus;
                keyboard.SetAvailableKeys(drag.Target.HasValue ? drag.Pairings.MissingKeys(LayoutIndices(), drag.Target.Value) : new int[0]);
                controllerPreview.SetAvailableTargets(drag.Keys != null ? drag.Pairings.AvailableTargets(drag.Keys) : new OutputTarget[0]);
                SetDefaultDragHint();
                drag.Preview = new DragPreviewWindow(visual);
                drag.Preview.FollowCursor(Cursor.Position); drag.Preview.Show(owner ?? this);
                if (!drag.Cancelled)
                {
                    Cursor.Current = DragCursors.Grabbing;
                    source.DoDragDrop(data, DragDropEffects.Copy);
                }
            }
            finally
            {
                source.GiveFeedback -= feedback; source.LostFocus -= lostFocus;
                source.EnabledChanged -= unavailable; source.VisibleChanged -= unavailable; source.Disposed -= unavailable;
                Deactivate -= lostFocus;
                activeMappingDrag = null;
                try { ClearMappingDragVisuals(); }
                finally { if (drag.Preview != null) drag.Preview.Dispose(); Cursor.Current = Cursors.Default; }
            }
            if (feedbackError != null) throw new InvalidOperationException(Tr("Die Ziehvorschau wurde beendet. Bitte erneut versuchen.", "The drag preview stopped. Please try again."), feedbackError);
        }
        void UpdateMappingDragFeedback(GiveFeedbackEventArgs e)
        {
            e.UseDefaultCursors = true;
            if (!CanContinueMappingDrag()) { CancelMappingDrag(); return; }
            var drag = activeMappingDrag;
            // Keep hold of either source while travelling between drop targets.
            // Target highlighting still shows where a mapping can be added.
            e.UseDefaultCursors = false; Cursor.Current = DragCursors.Grabbing;
            if (drag.Preview != null && !drag.Preview.IsDisposed) drag.Preview.FollowCursor(Cursor.Position);
        }
        bool CanContinueMappingDrag()
        {
            return activeMappingDrag != null && !activeMappingDrag.Cancelled && !closing && !deviceDetachInProgress &&
                activeMappingDrag.ControllerId == runtime.SelectedControllerId && activeMappingDrag.ProfilePath == profilePath &&
                object.ReferenceEquals(activeMappingDrag.Layout, keyboard.LayoutModel);
        }
        bool DragFocusIsInMapper()
        {
            IntPtr foreground = GetForegroundWindow();
            return IsHandleCreated && foreground == Handle;
        }
        void CancelMappingDrag()
        {
            var drag = activeMappingDrag; if (drag == null) return;
            drag.Cancelled = true;
            try { ClearMappingDragVisuals(); }
            finally { if (drag.Preview != null && !drag.Preview.IsDisposed) drag.Preview.Hide(); }
        }
        void ClearMappingDragVisuals()
        {
            if (!controllerPreview.IsDisposed) { controllerPreview.SetDropTarget(null); controllerPreview.SetAvailableTargets(new OutputTarget[0]); }
            if (!keyboard.IsDisposed) { keyboard.SetDropKeys(new int[0]); keyboard.SetAvailableKeys(new int[0]); }
            if (!IsDisposed && !Disposing && !mappingDragHint.IsDisposed) SetDefaultDragHint();
        }
        bool IsOurMappingDrag(DragEventArgs e)
        {
            if (!CanContinueMappingDrag() || e.Data == null || (e.AllowedEffect & DragDropEffects.Copy) == 0) return false;
            try
            {
                return e.Data.GetDataPresent(DataFormats.UnicodeText, false) &&
                    string.Equals(e.Data.GetData(DataFormats.UnicodeText, false) as string, activeMappingDrag.Token, StringComparison.Ordinal);
            }
            catch (System.Runtime.InteropServices.ExternalException) { CancelMappingDrag(); return false; }
        }
        void UpdateControllerDrag(object sender, DragEventArgs e)
        {
            OutputTarget? target = IsOurMappingDrag(e) && activeMappingDrag.Keys != null ? controllerPreview.HitTestTarget(controllerPreview.PointToClient(new Point(e.X, e.Y))) : null;
            if (target.HasValue && activeMappingDrag.Pairings.MissingKeys(activeMappingDrag.Keys, target.Value).Length == 0) target = null;
            controllerPreview.SetDropTarget(target); e.Effect = target.HasValue ? DragDropEffects.Copy : DragDropEffects.None;
            if (target.HasValue) SetDragHint(string.Format(Tr("{0} → {1}", "{0} → {1}"), DragKeyNames(activeMappingDrag.Keys), TargetLabel(target.Value)));
            else SetDefaultDragHint();
        }
        int[] KeyboardDropKeys(DragEventArgs e)
        {
            var key = keyboard.HitTest(keyboard.PointToClient(new Point(e.X, e.Y)));
            if (key == null || !key.KeyIndex.HasValue) return new int[0];
            int[] selected = keyboard.SelectedKeyIndices;
            return selected.Contains(key.KeyIndex.Value) ? selected : new[] { key.KeyIndex.Value };
        }
        void UpdateKeyboardDrag(object sender, DragEventArgs e)
        {
            int[] indices = IsOurMappingDrag(e) && activeMappingDrag.Target.HasValue ? KeyboardDropKeys(e) : new int[0];
            if (indices.Length != 0) indices = activeMappingDrag.Pairings.MissingKeys(indices, activeMappingDrag.Target.Value);
            keyboard.SetDropKeys(indices); e.Effect = indices.Length != 0 ? DragDropEffects.Copy : DragDropEffects.None;
            if (indices.Length != 0) SetDragHint(TargetLabel(activeMappingDrag.Target.Value) + " → " + DragKeyNames(indices)); else SetDefaultDragHint();
        }
        string DragKeyNames(int[] indices)
        { return indices.Length <= 3 ? string.Join(" + ", indices.Select(Label).ToArray()) : string.Format(Tr("{0} Tasten", "{0} keys"), indices.Length); }
        bool DragProfileUnchanged()
        { return CanContinueMappingDrag() && activeMappingDrag.ProfileJson == ProfileJson.Serialize(history.Current); }
        void DropOnController(object sender, DragEventArgs e)
        {
            e.Effect = DragDropEffects.None;
            if (!IsOurMappingDrag(e) || activeMappingDrag.Keys == null || !DragProfileUnchanged()) return;
            var target = controllerPreview.HitTestTarget(controllerPreview.PointToClient(new Point(e.X, e.Y)));
            if (!target.HasValue || activeMappingDrag.Pairings.MissingKeys(activeMappingDrag.Keys, target.Value).Length == 0) return;
            Attempt(delegate { AddDroppedMapping(activeMappingDrag.Keys, target.Value); e.Effect = DragDropEffects.Copy; });
        }
        void DropOnKeyboard(object sender, DragEventArgs e)
        {
            e.Effect = DragDropEffects.None;
            if (!IsOurMappingDrag(e) || !activeMappingDrag.Target.HasValue || !DragProfileUnchanged()) return;
            int[] indices = KeyboardDropKeys(e); if (indices.Length == 0) return;
            if (activeMappingDrag.Pairings.MissingKeys(indices, activeMappingDrag.Target.Value).Length == 0) return;
            Attempt(delegate { AddDroppedMapping(indices, activeMappingDrag.Target.Value); e.Effect = DragDropEffects.Copy; });
        }
        void AddDroppedMapping(int[] indices, OutputTarget target)
        {
            if (keyboard.LayoutModel == null || indices.Any(i => keyboard.LayoutModel.FindByIndex(i) == null)) throw new InvalidOperationException(Tr("Die Tastaturabbildung hat sich geändert. Bitte erneut zuordnen.", "The keyboard layout changed. Please map again."));
            var before = history.Current; var next = MappingAssignments.Add(before, indices, target, runtime.SelectedControllerId);
            string[] added = next.Bindings.Skip(before.Bindings.Count).Select(b => b.BindingId).ToArray();
            if (added.Length != 0) Commit(next);
            else added = next.Bindings.Where(b => b.ControllerId == runtime.SelectedControllerId && indices.Contains(b.KeyIndex) && b.Target == target).Select(b => b.BindingId).ToArray();
            updating = true; try { keyboard.SetSelectedKeys(indices); } finally { updating = false; }
            SelectKeyboardKeys(); SelectTarget(target);
            updating = true;
            try
            {
                bindings.ClearSelection();
                foreach (DataGridViewRow row in bindings.Rows) if (added.Contains((string)row.Tag)) row.Selected = true;
            }
            finally { updating = false; }
            RefreshSettings(); UpdateKeyCard();
        }
    }
}
