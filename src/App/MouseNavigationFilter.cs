using System;
using System.Windows.Forms;

namespace Tk75.App
{
    // An explicit shortcut editor may capture keys while its displayed text is
    // read-only. Ordinary read-only text boxes do not get this exception.
    internal interface IShortcutCaptureControl { }

    // Filters only messages on this application's UI thread. Physical keyboard
    // reports and registered global hotkeys use separate paths and are untouched.
    internal sealed class MouseNavigationFilter : IMessageFilter, IDisposable
    {
        bool disposed;
        internal MouseNavigationFilter() { Application.AddMessageFilter(this); }

        internal static bool ShouldBlock(int message, int key, bool editableText, bool shortcutCapture, bool valueEditor)
        {
            bool navigation;
            switch (message)
            {
                case 0x0100: // WM_KEYDOWN
                case 0x0101: // WM_KEYUP (buttons can activate on release)
                case 0x0104: // WM_SYSKEYDOWN
                case 0x0105: // WM_SYSKEYUP
                    navigation = key == (int)Keys.Space || key >= (int)Keys.Left && key <= (int)Keys.Down;
                    break;
                case 0x0102: // WM_CHAR
                case 0x0106: // WM_SYSCHAR
                    navigation = key == (int)Keys.Space;
                    break;
                default:
                    return false;
            }
            if (!navigation || shortcutCapture) return false;
            return valueEditor || !editableText;
        }

        public bool PreFilterMessage(ref Message message)
        {
            if (disposed) return false;
            int key = unchecked((int)message.WParam.ToInt64());
            // Skip control lookup for mouse, paint, hotkey and unrelated keys.
            if (!ShouldBlock(message.Msg, key, false, false, false)) return false;
            Control target = Control.FromChildHandle(message.HWnd);
            bool editableText = false, shortcutCapture = false, valueEditor = false;
            for (Control control = target; control != null; control = control.Parent)
            {
                TextBoxBase text = control as TextBoxBase;
                if (text != null && !text.ReadOnly && text.Enabled) editableText = true;
                if (control is IShortcutCaptureControl) shortcutCapture = true;
                // NumericUpDown owns a writable TextBox internally. Arrow presses
                // must not change its value just because that child has focus.
                if (control is UpDownBase) valueEditor = true;
            }
            return ShouldBlock(message.Msg, key, editableText, shortcutCapture, valueEditor);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Application.RemoveMessageFilter(this);
        }
    }
}
