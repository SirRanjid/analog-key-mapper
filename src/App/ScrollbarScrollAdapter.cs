using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Tk75.App
{
    // Changes content through the control's real scrolling contract. Painting
    // and pointer capture belong to ScrollbarInteraction, not to this adapter.
    internal static class ScrollbarScrollAdapter
    {
        static readonly MethodInfo ScrollBarOnScroll = typeof(ScrollBar).GetMethod("OnScroll", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly MethodInfo PanelOnScroll = typeof(ScrollableControl).GetMethod("OnScroll", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly MethodInfo PanelSetScrollState = typeof(ScrollableControl).GetMethod("SetScrollState", BindingFlags.Instance | BindingFlags.NonPublic);

        [StructLayout(LayoutKind.Sequential)]
        struct Range
        {
            internal int Size;
            internal uint Mask;
            internal int Minimum, Maximum;
            internal uint Page;
            internal int Position, TrackPosition;
            internal int Last { get { return (int)Math.Max(Minimum, (long)Maximum - Math.Max(0L, (long)Page - 1)); } }
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct TextMetrics
        {
            internal int Height, Ascent, Descent, InternalLeading, ExternalLeading, AverageWidth;
            internal int MaximumWidth, Weight, Overhang, DigitizedAspectX, DigitizedAspectY;
            internal char FirstCharacter, LastCharacter, DefaultCharacter, BreakCharacter;
            internal byte Italic, Underlined, StruckOut, PitchAndFamily, CharacterSet;
        }
        [DllImport("user32.dll", EntryPoint = "SendMessageW", ExactSpelling = true)]
        static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wparam, IntPtr lparam);
        [DllImport("user32.dll", ExactSpelling = true)]
        static extern bool GetScrollInfo(IntPtr window, int bar, ref Range info);
        [DllImport("user32.dll", ExactSpelling = true)]
        static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll", ExactSpelling = true)]
        static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("gdi32.dll", ExactSpelling = true)]
        static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
        [DllImport("gdi32.dll", EntryPoint = "GetTextMetricsW", ExactSpelling = true)]
        static extern bool GetTextMetrics(IntPtr dc, out TextMetrics metrics);

        static void Invoke(MethodInfo method, object owner, params object[] arguments)
        {
            // These are documented protected extension points, not private
            // framework fields or an alternative native tracking procedure.
            try { method.Invoke(owner, arguments); }
            catch (TargetInvocationException error) { throw error.InnerException ?? error; }
        }

        internal static bool Supports(Control owner, bool standalone, bool vertical)
        {
            if (standalone) return owner is ScrollBar;
            ScrollableControl panel = owner as ScrollableControl;
            if (panel != null) return panel.AutoScroll;
            if (owner is TextBox) return true;
            // The app's list and combo popup surfaces are vertical. Native
            // LISTBOX has no documented 32-bit absolute horizontal pixel setter;
            // leave that unsupported surface's native input contract intact.
            return vertical && (owner == null || owner is ListBox);
        }

        internal static void Apply(Control owner, IntPtr window, bool standalone, bool vertical, ScrollEventType type, int requestedPosition)
        {
            if (!Supports(owner, standalone, vertical)) return;
            if (window == IntPtr.Zero || owner != null && (owner.IsDisposed || !owner.IsHandleCreated || owner.Handle != window)) return;
            ScrollBar bar = owner as ScrollBar;
            if (standalone && bar != null) { ApplyBar(bar, vertical, type, requestedPosition); return; }
            ScrollableControl panel = owner as ScrollableControl;
            if (panel != null) { ApplyPanel(panel, vertical, type, requestedPosition); return; }

            if (type == ScrollEventType.EndScroll)
            { SendMessage(window, vertical ? 0x0115 : 0x0114, (IntPtr)8, IntPtr.Zero); return; }
            TextBoxBase edit = owner as TextBoxBase;
            if (edit != null)
            {
                if (vertical)
                {
                    int first = SendMessage(window, 0x00CE, IntPtr.Zero, IntPtr.Zero).ToInt32(); // EM_GETFIRSTVISIBLELINE
                    SendMessage(window, 0x00B6, IntPtr.Zero, (IntPtr)(requestedPosition - first)); // EM_LINESCROLL
                }
                else ApplyHorizontalEdit(window, type, requestedPosition);
                return;
            }
            // A null owner is the native ListBox HWND returned by the owned
            // ComboBox's GetComboBoxInfo. It is never an unrelated window.
            if (owner == null || owner is ListBox)
            {
                if (vertical) SendMessage(window, 0x0197, (IntPtr)requestedPosition, IntPtr.Zero); // LB_SETTOPINDEX
            }
        }

        static void ApplyBar(ScrollBar bar, bool vertical, ScrollEventType type, int requestedPosition)
        {
            int old = bar.Value;
            bool rtl = bar.RightToLeft == RightToLeft.Yes;
            int value = type == ScrollEventType.EndScroll ? old : requestedPosition;
            if (!vertical && rtl && type != ScrollEventType.EndScroll)
                value = (int)((long)bar.Minimum + bar.Maximum - bar.LargeChange + 1 - value);
            value = Math.Max(bar.Minimum, Math.Min(bar.Maximum, value));
            ScrollEventArgs args = new ScrollEventArgs(rtl ? Reflect(type) : type, old, value,
                vertical ? ScrollOrientation.VerticalScroll : ScrollOrientation.HorizontalScroll);
            // Match ScrollBar.DoScroll: consumers receive the proposed value
            // first and may amend it; ValueChanged follows only when it changes.
            Invoke(ScrollBarOnScroll, bar, args);
            if (!bar.IsDisposed && type != ScrollEventType.EndScroll) bar.Value = args.NewValue;
        }
        static ScrollEventType Reflect(ScrollEventType type)
        {
            switch (type)
            {
                case ScrollEventType.First: return ScrollEventType.Last;
                case ScrollEventType.Last: return ScrollEventType.First;
                case ScrollEventType.SmallDecrement: return ScrollEventType.SmallIncrement;
                case ScrollEventType.SmallIncrement: return ScrollEventType.SmallDecrement;
                case ScrollEventType.LargeDecrement: return ScrollEventType.LargeIncrement;
                case ScrollEventType.LargeIncrement: return ScrollEventType.LargeDecrement;
                default: return type;
            }
        }
        static void ApplyPanel(ScrollableControl panel, bool vertical, ScrollEventType type, int requestedPosition)
        {
            // ScrollableControl does not raise Scroll for SB_ENDSCROLL. Unlike
            // ScrollBar, its Scroll event reports an already applied value.
            if (type == ScrollEventType.EndScroll) return;
            Point previous = panel.AutoScrollPosition;
            int old = -(vertical ? previous.Y : previous.X);
            if (SystemInformation.DragFullWindows || type != ScrollEventType.ThumbTrack)
            {
                Invoke(PanelSetScrollState, panel, 8, true); // ScrollStateUserHasScrolled
                // ScrollProperties writes the same position for LTR and RTL;
                // only standalone HScrollBar has a reflected Value contract.
                panel.AutoScrollPosition = vertical ? new Point(-previous.X, requestedPosition) : new Point(requestedPosition, -previous.Y);
            }
            if (!panel.IsDisposed) Invoke(PanelOnScroll, panel, new ScrollEventArgs(type, old, requestedPosition,
                vertical ? ScrollOrientation.VerticalScroll : ScrollOrientation.HorizontalScroll));
        }

        static Range ReadHorizontalRange(IntPtr window)
        {
            Range info = new Range { Size = Marshal.SizeOf(typeof(Range)), Mask = 7 };
            GetScrollInfo(window, 0, ref info); return info;
        }
        static void ApplyHorizontalEdit(IntPtr window, ScrollEventType type, int requestedPosition)
        {
            if (type != ScrollEventType.ThumbTrack && type != ScrollEventType.ThumbPosition)
            { SendMessage(window, 0x0114, (IntPtr)(int)type, IntPtr.Zero); return; }
            Range info = ReadHorizontalRange(window);
            long delta = (long)requestedPosition - info.Position;
            if (delta == 0) return;
            int average = AverageCharacterWidth(window);
            // Standard EDIT exposes horizontal relative movement in characters,
            // while SCROLLINFO uses pixels. EM_LINESCROLL accepts the complete
            // 32-bit distance; never pack an oversized position into HIWORD.
            double steps = delta / (double)average;
            long count = requestedPosition <= info.Minimum || requestedPosition >= info.Last
                ? (long)(delta < 0 ? Math.Floor(steps) : Math.Ceiling(steps))
                : (long)Math.Round(steps, MidpointRounding.AwayFromZero);
            count = Math.Max(Int32.MinValue, Math.Min(Int32.MaxValue, count));
            SendMessage(window, 0x00B6, (IntPtr)(int)count, IntPtr.Zero);
        }
        static int AverageCharacterWidth(IntPtr window)
        {
            IntPtr dc = GetDC(window), previous = IntPtr.Zero;
            if (dc == IntPtr.Zero) return 1;
            try
            {
                IntPtr font = SendMessage(window, 0x0031, IntPtr.Zero, IntPtr.Zero);
                if (font != IntPtr.Zero) previous = SelectObject(dc, font);
                TextMetrics metrics;
                return GetTextMetrics(dc, out metrics) ? Math.Max(1, metrics.AverageWidth) : 1;
            }
            finally { if (previous != IntPtr.Zero) SelectObject(dc, previous); ReleaseDC(window, dc); }
        }
    }
}
