using System;
using System.Linq;
using System.Windows.Forms;
using Tk75.Mapping;

namespace Tk75.App
{
    public sealed partial class MainForm
    {
        readonly Button captureOpposite = new SleekButton { Dock = DockStyle.Fill, Margin = new Padding(3, 2, 3, 2) };
        SocdDragContext socdCapture;
        int? socdCapturePressedKey;
        InputOpposedPolicy socdCapturePolicy;
        string socdCaptureNotice;
        int? socdCaptureNoticeTarget;
        object socdCaptureNoticeSnapshot;

        void BeginSocdCapture()
        {
            int[] selected = SelectedKeys();
            if (selected.Length != 1 || closing || deviceDetachInProgress || activeMappingDrag != null) return;
            FlushInputDraft();
            SetDetailMode("input", true, false); ScrollKeySettingsTo(keySocdPanel);
            socdCapture = new SocdDragContext { Selection = selected, Target = selected[0], ProfilePath = profilePath,
                ControllerId = runtime.SelectedControllerId, Layout = keyboard.LayoutModel,
                History = history, SnapshotToken = history.SnapshotToken, Mode = "input" };
            socdCapturePolicy = KeyInputEditing.GetOrDefault(history.Current, selected[0]).OppositePolicy;
            socdCapturePressedKey = null; socdCaptureNotice = null; socdCaptureNoticeTarget = null; socdCaptureNoticeSnapshot = null;
            RefreshSocdDropTarget();
        }
        bool SocdCaptureCurrent()
        {
            return SocdContextCurrent(socdCapture) && socdCapture.Target.HasValue &&
                Object.ReferenceEquals(history, socdCapture.History) && Object.ReferenceEquals(history.SnapshotToken, socdCapture.SnapshotToken) &&
                keyboard.LayoutModel != null && keyboard.LayoutModel.FindByIndex(socdCapture.Target.Value) != null;
        }
        void TrackSocdCapturePress(KeyboardKeyDefinition source)
        {
            if (socdCapture == null) return;
            if (!SocdCaptureCurrent()) { CancelSocdCapture(false); return; }
            // Selection changes on mouse-down. Keep the original target until
            // mouse-up proves this was a click, or drag preparation cancels it.
            socdCapturePressedKey = source == null ? null : source.KeyIndex;
            socdCaptureNotice = null;
        }
        void FinishSocdCapturePointer()
        {
            if (socdCapture == null || !IsHandleCreated || IsDisposed) return;
            try
            {
                BeginInvoke((Action)delegate {
                    if (IsDisposed || keyboard.Capture || socdCapture == null) return;
                    socdCapturePressedKey = null;
                    if (!SelectedKeys().SequenceEqual(socdCapture.Selection)) CancelSocdCapture(false);
                });
            }
            catch (InvalidOperationException) { }
        }
        void CancelSocdCapture(bool restoreSelection)
        {
            SocdDragContext captured = socdCapture;
            bool restore = restoreSelection && SocdCaptureCurrent();
            socdCapture = null; socdCapturePressedKey = null; socdCaptureNotice = null; socdCaptureNoticeTarget = null; socdCaptureNoticeSnapshot = null;
            if (captured != null && keyboard.Capture) keyboard.Capture = false;
            if (restore) RestoreSocdSelection(captured.Selection);
            RefreshSocdDropTarget();
        }
        bool TryCancelSocdCapture()
        {
            if (socdCapture == null) return false;
            CancelSocdCapture(true); return true;
        }
        bool TryCompleteSocdCapture(KeyboardKeyDefinition opposite)
        {
            if (socdCapture == null) return false;
            if (!SocdCaptureCurrent()) { CancelSocdCapture(false); return false; }
            SocdDragContext captured = socdCapture;
            bool valid = opposite != null && opposite.KeyIndex.HasValue &&
                Object.ReferenceEquals(keyboard.LayoutModel.FindByCode(opposite.Code), opposite);
            if (!valid || opposite.KeyIndex.Value == captured.Target.Value)
            {
                socdCapturePressedKey = null;
                RestoreSocdSelection(captured.Selection); SetDetailMode("input", true, false);
                socdCaptureNotice = Tr("Eine andere Taste anklicken.", "Click a different key.");
                socdCaptureNoticeTarget = captured.Target;
                socdCaptureNoticeSnapshot = history.SnapshotToken;
                RefreshSocdDropTarget(); return true;
            }
            socdCapture = null; socdCapturePressedKey = null;
            string result;
            try
            {
                Commit(KeyInputEditing.SetPairing(history.Current, captured.Selection, opposite.KeyIndex.Value, socdCapturePolicy));
                result = string.Format(Tr("{0} ↔ {1} verbunden", "{0} ↔ {1} paired"), Label(captured.Target.Value), Label(opposite.KeyIndex.Value));
            }
            catch (Exception error)
            {
                result = Tr("Zuordnung nicht möglich: ", "Could not pair keys: ") + UiText.Get(error.Message);
                store.Event("Opposite-key capture: " + error.Message);
            }
            RestoreSocdSelection(captured.Selection); SetDetailMode("input", true, false); RefreshInputEditor();
            socdCaptureNotice = result; socdCaptureNoticeTarget = captured.Target; socdCaptureNoticeSnapshot = history.SnapshotToken; RefreshSocdDropTarget();
            return true;
        }
        void RefreshSocdCaptureUi()
        {
            if (socdCapture != null && (!SocdCaptureCurrent() ||
                !socdCapturePressedKey.HasValue && !SelectedKeys().SequenceEqual(socdCapture.Selection)))
            { socdCapture = null; socdCapturePressedKey = null; socdCaptureNotice = null; socdCaptureNoticeTarget = null; socdCaptureNoticeSnapshot = null; }
            if (socdCapture == null && socdCaptureNoticeTarget.HasValue &&
                (!SelectedKeys().SequenceEqual(new[] { socdCaptureNoticeTarget.Value }) || !Object.ReferenceEquals(socdCaptureNoticeSnapshot, history.SnapshotToken)))
            { socdCaptureNotice = null; socdCaptureNoticeTarget = null; socdCaptureNoticeSnapshot = null; }
            captureOpposite.Text = socdCapture == null ? Tr("Erfassen", "Capture") : Tr("Abbrechen", "Cancel");
            captureOpposite.Enabled = !closing && !deviceDetachInProgress && activeMappingDrag == null && (socdCapture != null || SelectedKeys().Length == 1);
            keyCardTips.SetToolTip(captureOpposite, Tr("Erfassen wählen und die Gegentaste auf der Tastaturabbildung anklicken. Esc bricht ab.",
                "Choose Capture, then click the opposite key on the keyboard. Esc cancels."));
        }
    }
}
