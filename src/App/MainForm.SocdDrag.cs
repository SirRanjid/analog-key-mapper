using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using Tk75.Mapping;

namespace Tk75.App
{
    public sealed partial class MainForm
    {
        readonly Label socdDropTarget = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
            AutoEllipsis = true, BorderStyle = BorderStyle.None, Margin = new Padding(3, 2, 3, 2), AllowDrop = true };
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
            TrackSocdCapturePress(source);
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
            UiText.PreserveText(captureOpposite);
            socdDropTarget.Paint += delegate(object sender, PaintEventArgs e) {
                if (socdDropTarget.Width < 3 || socdDropTarget.Height < 3) return;
                SmoothingMode previous = e.Graphics.SmoothingMode; e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = SurfaceDrawing.Round(new RectangleF(.5f, .5f, socdDropTarget.Width - 1.5f, socdDropTarget.Height - 1.5f), 6))
                using (var pen = new Pen(ModernTheme.Border)) e.Graphics.DrawPath(pen, path);
                e.Graphics.SmoothingMode = previous;
            };
            var captureRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            captureRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); captureRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 94));
            captureRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            captureRow.Controls.Add(socdDropTarget, 0, 0); captureRow.Controls.Add(captureOpposite, 1, 0);
            form.Controls.Add(captureRow, 0, row); form.SetColumnSpan(captureRow, 3);
            captureOpposite.Click += delegate { if (socdCapture == null) Attempt(BeginSocdCapture); else CancelSocdCapture(true); };
            keyboard.MouseCaptureChanged += delegate { FinishSocdCapturePointer(); };
            VisibleChanged += delegate { if (!Visible && socdCapture != null) CancelSocdCapture(false); };
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
            ScrollKeySettingsTo(keySocdPanel);
            RefreshSocdDropTarget();
            SetDragHint(string.Format(Tr("{0} als Gegentaste für {1} im markierten Feld ablegen.", "Drop {0} in the marked area as the opposite of {1}."), Label(activeMappingDrag.Keys[0]), Label(target)));
        }
        void RefreshSocdDropTarget()
        {
            RefreshSocdCaptureUi();
            int[] selected = SelectedKeys();
            int? target = activeMappingDrag != null && SocdContextCurrent(activeMappingDrag.Socd) ? activeMappingDrag.Socd.Target : selected.Length == 1 ? (int?)selected[0] : null;
            if (socdCapture != null)
                socdDropTarget.Text = (socdCaptureNotice ?? string.Format(Tr("Gegentaste für {0} anklicken", "Click the opposite key for {0}"), Label(socdCapture.Target.Value))) + "\n" + Tr("Esc bricht ab", "Esc cancels");
            else if (activeMappingDrag != null && target.HasValue)
                socdDropTarget.Text = string.Format(Tr("Gegentaste für {0} hier ablegen", "Drop the opposite key for {0} here"), Label(target.Value));
            else socdDropTarget.Text = socdCaptureNotice ?? (target.HasValue ? string.Format(Tr("Gegentaste für {0} erfassen", "Capture the opposite key for {0}"), Label(target.Value)) :
                Tr("Eine Taste zum Erfassen auswählen.", "Select one key to capture its opposite."));
            socdDropTarget.AccessibleName = socdDropTarget.Text;
            socdDropTarget.AccessibleDescription = Tr("Erfassen wählen, dann die Gegentaste auf der Tastaturabbildung anklicken. Alternativ den Gegenpart oben auswählen.", "Choose Capture, then click the opposite key on the keyboard. Alternatively choose its name above.");
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
