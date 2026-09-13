using System.Drawing;
using System.Windows.Forms;

namespace Tk75.App
{
    internal sealed class CurveEditorScrollPanel : Panel
    {
        protected override Point ScrollToControl(Control activeControl)
        {
            // A rail or shape button is part of the graph, not an independent
            // row. Keep its title and axes visible when WinForms reveals focus.
            for (Control target = activeControl; target != null && target != this; target = target.Parent)
                if (target is CurveCanvas) return base.ScrollToControl(target);
            return base.ScrollToControl(activeControl);
        }
    }
}
