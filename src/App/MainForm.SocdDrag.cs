using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Tk75.Mapping;

namespace Tk75.App
{
    public sealed partial class MainForm
    {
        readonly Label socdDropTarget = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
            AutoEllipsis = true, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(3, 2, 3, 2), AllowDrop = true };
        bool socdDropHighlighted;
        SocdDragContext pendingSocdDrag;
        sealed class SocdDragContext
        {
            public int[] Selection;
            public int? Target;
            public int Source;
            public string Mode, ProfilePath, ControllerId;
            public KeyboardLayout Layout;
            public EditHistory History;
            public object SnapshotToken;
        }
        void CaptureSocdDragContext(KeyboardKeyDefinition source)
        {
            pendingSocdDrag = null;
            if (source == null || !source.KeyIndex.HasValue || activeMappingDrag != null || closing) return;
            int[] selection = SelectedKeys();
            pendingSocdDrag = new SocdDragContext { Selection = selection, Target = selection.Length == 1 ? (int?)selection[0] : null,
                Source = source.KeyIndex.Value, Mode = detailsMode, ProfilePath = profilePath,
                ControllerId = runtime.SelectedControllerId, Layout = keyboard.LayoutModel,
                History = history, SnapshotToken = history.SnapshotToken };
        }
        void AdvanceSocdDraftSnapshot(EditHistory previousHistory, object previousSnapshot)
        {
            // Only the existing draft flush may carry a mouse-down context into its new snapshot.
            // Undo or a replacement history must never reauthorize the old editing target.
            SocdDragContext context = pendingSocdDrag;
            if (context != null && Object.ReferenceEquals(history, previousHistory) &&
                Object.ReferenceEquals(context.History, previousHistory) &&
                Object.ReferenceEquals(context.SnapshotToken, previousSnapshot))
                context.SnapshotToken = history.SnapshotToken;
        }
        bool SocdContextCurrent(SocdDragContext context)
        {
            return context != null && !closing && !deviceDetachInProgress && !IsDisposed &&
                context.ProfilePath == profilePath && context.ControllerId == runtime.SelectedControllerId &&
                Object.ReferenceEquals(context.Layout, keyboard.LayoutModel);
        }
        SocdDragContext TakeSocdDragContext(int[] sourceKeys)
        {
            SocdDragContext context = pendingSocdDrag; pendingSocdDrag = null;
            return SocdContextCurrent(context) && sourceKeys.Contains(context.Source) &&
                Object.ReferenceEquals(history, context.History) &&
                Object.ReferenceEquals(history.SnapshotToken, context.SnapshotToken) ? context : null;
        }
        void BuildSocdDragUi(TableLayoutPanel form, int row)
        {
            UiText.PreserveText(socdDropTarget);
            form.Controls.Add(socdDropTarget, 0, row); form.SetColumnSpan(socdDropTarget, 3);
            socdDropTarget.DragEnter += UpdateSocdDrag; socdDropTarget.DragOver += UpdateSocdDrag;
            socdDropTarget.DragLeave += delegate { SetSocdDropHighlight(false); SetDefaultDragHint(); };
            socdDropTarget.DragDrop += DropOnSocd;
            RefreshSocdDropTarget();
        }
        void BuildSocdDragTabs()
        {
            foreach (Control tab in new[] { keySettingsToggle, keyBehaviorToggle })
            {
                tab.AllowDrop = true; tab.DragEnter += RevealSocdDuringDrag; tab.DragOver += RevealSocdDuringDrag;
            }
            controllerToggle.AllowDrop = true;
            DragEventHandler controllerTab = delegate(object sender, DragEventArgs e) {
                e.Effect = DragDropEffects.None;
                if (!IsOurMappingDrag(e) || activeMappingDrag.Keys == null) return;
                SetSocdDropHighlight(false); SetDetailMode("controller", true, false); SetDefaultDragHint();
            };
            controllerToggle.DragEnter += controllerTab; controllerToggle.DragOver += controllerTab;
        }
        bool CanPairSocdDrag()
        {
            MappingDrag drag = activeMappingDrag;
            return CanContinueMappingDrag() && drag.Keys != null && drag.Keys.Length == 1 && !drag.Target.HasValue &&
                SocdContextCurrent(drag.Socd) && drag.Socd.Target.HasValue && drag.Socd.Target.Value != drag.Keys[0] &&
                keyboard.LayoutModel != null && keyboard.LayoutModel.FindByIndex(drag.Socd.Target.Value) != null &&
                keyboard.LayoutModel.FindByIndex(drag.Keys[0]) != null && Object.ReferenceEquals(history, drag.History) &&
                Object.ReferenceEquals(history.SnapshotToken, drag.SnapshotToken) && !inputDirty;
        }
        void RevealSocdDuringDrag(object sender, DragEventArgs e)
        {
            e.Effect = DragDropEffects.None; // The tab reveals the actual labelled drop area.
            if (!IsOurMappingDrag(e) || !CanPairSocdDrag()) return;
            int target = activeMappingDrag.Socd.Target.Value;
            if (detailsMode != "input" || !SelectedKeys().SequenceEqual(new[] { target }))
            {
                RestoreSocdSelection(new[] { target }); SetDetailMode("input", true, false);
            }
            RefreshSocdDropTarget();
            SetDragHint(string.Format(Tr("{0} als Gegentaste für {1} im markierten Feld ablegen.", "Drop {0} in the marked area as the opposite of {1}."), Label(activeMappingDrag.Keys[0]), Label(target)));
        }
        void RefreshSocdDropTarget()
        {
            int[] selected = SelectedKeys();
            int? target = activeMappingDrag != null && SocdContextCurrent(activeMappingDrag.Socd) ? activeMappingDrag.Socd.Target : selected.Length == 1 ? (int?)selected[0] : null;
            socdDropTarget.Text = target.HasValue ? string.Format(Tr("Gegentaste für {0} hier ablegen", "Drop the opposite key for {0} here"), Label(target.Value)) :
                Tr("Eine Taste auswählen, dann die Gegentaste hierher ziehen.", "Select one key, then drag its opposite here.");
            socdDropTarget.Text += "\n" + Tr("Beim Ziehen über den Reiter Tasten fahren", "While dragging, hover the Keys tab");
            socdDropTarget.AccessibleName = socdDropTarget.Text;
            socdDropTarget.AccessibleDescription = Tr("Beim Ziehen öffnet der Reiter Tasten dieses Feld. Alternativ den Gegenpart oben auswählen.", "While dragging, hover the Keys tab to reveal this area. Alternatively choose the opposite key above.");
            keyCardTips.SetToolTip(socdDropTarget, socdDropTarget.AccessibleDescription);
        }
        void SetSocdDropHighlight(bool value)
        {
            if (socdDropTarget.IsDisposed) return;
            if (socdDropHighlighted == value && socdDropTarget.BackColor == (value ? MappingDragFeedback.DropColor : ModernTheme.Surface)) return;
            socdDropHighlighted = value; socdDropTarget.BackColor = value ? MappingDragFeedback.DropColor : ModernTheme.Surface;
            socdDropTarget.ForeColor = value ? ModernTheme.Background : ModernTheme.Foreground;
        }
        void UpdateSocdDrag(object sender, DragEventArgs e)
        {
            bool accepted = IsOurMappingDrag(e) && CanPairSocdDrag() && detailsMode == "input" && socdDropTarget.Visible;
            e.Effect = accepted ? DragDropEffects.Copy : DragDropEffects.None; SetSocdDropHighlight(accepted);
            if (accepted) SetDragHint(string.Format(Tr("{0} ↔ {1} · als Gegentasten verbinden", "{0} ↔ {1} · pair opposite keys"), Label(activeMappingDrag.Socd.Target.Value), Label(activeMappingDrag.Keys[0])));
        }
        void DropOnSocd(object sender, DragEventArgs e)
        {
            e.Effect = DragDropEffects.None; SetSocdDropHighlight(false);
            if (!IsOurMappingDrag(e) || !CanPairSocdDrag() || detailsMode != "input" || !socdDropTarget.Visible || !DragProfileUnchanged()) return;
            MappingDrag drag = activeMappingDrag;
            int target = drag.Socd.Target.Value, opposite = drag.Keys[0];
            Attempt(delegate {
                Profile before = history.Current;
                InputOpposedPolicy policy = KeyInputEditing.GetOrDefault(before, target).OppositePolicy;
                Commit(KeyInputEditing.SetPairing(before, new[] { target }, opposite, policy));
                drag.Completed = true; RestoreSocdSelection(new[] { target }); SetDetailMode("input", true, false);
                RefreshInputEditor(); e.Effect = DragDropEffects.Copy;
            });
        }
        void RestoreSocdSelection(int[] selection)
        {
            bool wasUpdating = updating; updating = true;
            try { keyboard.SetSelectedKeys(selection); }
            finally { updating = wasUpdating; }
            SelectKeyboardKeys();
        }
        void FinishSocdDrag(MappingDrag drag)
        {
            if (!drag.Completed && SocdContextCurrent(drag.Socd) && Object.ReferenceEquals(history, drag.History) &&
                Object.ReferenceEquals(history.SnapshotToken, drag.SnapshotToken))
            { RestoreSocdSelection(drag.Socd.Selection); SetDetailMode(drag.Socd.Mode, true, false); }
            RefreshSocdDropTarget();
        }
    }
}
