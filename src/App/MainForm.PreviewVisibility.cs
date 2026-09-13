using System;
using System.Windows.Forms;

namespace Tk75.App
{
    public sealed partial class MainForm
    {
        void UpdatePreviewVisibility()
        {
            if (IsDisposed || Disposing) return;
            bool visible = Visible && WindowState != FormWindowState.Minimized;
            // Recording is an editor gesture. Once its progress/cancel controls
            // are hidden, a later release must not silently change a threshold.
            if (!visible && inputThresholdCaptureReader != null) CancelInputThresholdCapture();
            if (runtime != null && !closing && !deviceDetachInProgress)
                runtime.SetPreviewActive(visible);
        }
        protected override void OnVisibleChanged(EventArgs e)
        { base.OnVisibleChanged(e); UpdatePreviewVisibility(); }
        protected override void OnResize(EventArgs e)
        { base.OnResize(e); UpdatePreviewVisibility(); }
    }
}
