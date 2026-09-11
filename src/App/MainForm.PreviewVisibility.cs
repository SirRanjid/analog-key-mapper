using System;
using System.Windows.Forms;

namespace Tk75.App
{
    public sealed partial class MainForm
    {
        void UpdatePreviewVisibility()
        {
            if (runtime != null && !closing && !IsDisposed && !Disposing)
                runtime.SetPreviewActive(Visible && WindowState != FormWindowState.Minimized);
        }
        protected override void OnVisibleChanged(EventArgs e)
        { base.OnVisibleChanged(e); UpdatePreviewVisibility(); }
        protected override void OnResize(EventArgs e)
        { base.OnResize(e); UpdatePreviewVisibility(); }
    }
}
