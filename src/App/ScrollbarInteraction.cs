using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Tk75.App
{
    // One input owner for standalone bars, non-client bars and combo lists.
    // Never enter the system's modal scrollbar tracking/auto-repeat loop: it
    // draws directly to the window DC until mouse-up, outside WM_PAINT.
    internal sealed class ScrollbarInteraction : IDisposable
    {
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
        [StructLayout(LayoutKind.Sequential)]
        struct MouseTracking { internal int Size; internal uint Flags; internal IntPtr Window; internal uint HoverTime; }
        [DllImport("user32.dll", ExactSpelling = true)]
        static extern bool GetScrollInfo(IntPtr window, int bar, ref Range range);
        [DllImport("user32.dll", ExactSpelling = true)]
        static extern IntPtr GetCapture();
        [DllImport("user32.dll", ExactSpelling = true)]
        static extern IntPtr SetCapture(IntPtr window);
        [DllImport("user32.dll", ExactSpelling = true)]
        static extern bool ReleaseCapture();
        [DllImport("user32.dll", ExactSpelling = true)]
        static extern bool TrackMouseEvent(ref MouseTracking tracking);
        [DllImport("user32.dll", ExactSpelling = true)]
        static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll", ExactSpelling = true)]
        static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll", ExactSpelling = true)]
        static extern bool IsWindowEnabled(IntPtr window);
        [DllImport("user32.dll", ExactSpelling = true)]
        static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll", EntryPoint = "SendMessageW", ExactSpelling = true)]
        static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wparam, IntPtr lparam);

        static readonly Dictionary<IntPtr, ScrollbarInteraction> Attached = new Dictionary<IntPtr, ScrollbarInteraction>();
        readonly Control owner;
        readonly bool standalone, standaloneVertical;
        readonly Action repaint;
        readonly Timer repeat = new Timer();
        IntPtr window, previousCapture;
        bool active, dragging, activeVertical, hotVertical, disposed, hiddenDuringUpdate, clientLeaveArmed;
        int hotPart, pressedPart, anchorPixel, anchorPosition, trackingPosition, redrawDepth, generation;
        Point pointer;
        Range dragRange;

        internal ScrollbarInteraction(Control owner, bool standalone, bool vertical, Action repaint)
        {
            this.owner = owner; this.standalone = standalone; standaloneVertical = vertical; this.repaint = repaint;
            repeat.Tick += Repeat;
        }
        internal void Attach(IntPtr handle)
        {
            if (window == handle) return;
            Detach(); window = handle;
            if (window != IntPtr.Zero) Attached[window] = this;
        }
        internal void Detach()
        {
            End(false, true);
            if (window != IntPtr.Zero) Attached.Remove(window);
            window = IntPtr.Zero; hotPart = 0; clientLeaveArmed = false; generation++;
        }
        public void Dispose()
        {
            if (disposed) return;
            Detach(); disposed = true; repeat.Dispose();
        }

        static Point MessagePoint(Message message, bool client)
        {
            long bits = message.LParam.ToInt64();
            Point point = new Point((short)(bits & 65535), (short)((bits >> 16) & 65535));
            if (client) NativeControlPaint.ClientToScreen(message.HWnd, ref point);
            return point;
        }
        bool ReadRange(bool vertical, out Range range)
        {
            range = new Range { Size = Marshal.SizeOf(typeof(Range)), Mask = 7 };
            return window != IntPtr.Zero && GetScrollInfo(window, standalone ? 2 : vertical ? 1 : 0, ref range);
        }
        bool ReadBar(bool vertical, out ScrollbarDrawing.ScrollInfo info)
        { return ScrollbarDrawing.Read(window, standalone ? 0xFFFFFFFC : vertical ? 0xFFFFFFFB : 0xFFFFFFFA, out info); }
        bool FindBar(Point point, out bool vertical, out ScrollbarDrawing.ScrollInfo info)
        {
            vertical = standaloneVertical;
            if (standalone) return ReadBar(vertical, out info) && info.Area.Bounds.Contains(point);
            vertical = true;
            if (ReadBar(true, out info) && info.Area.Bounds.Contains(point) && ScrollbarScrollAdapter.Supports(owner, false, true)) return true;
            vertical = false;
            return ReadBar(false, out info) && info.Area.Bounds.Contains(point) && ScrollbarScrollAdapter.Supports(owner, false, false);
        }
        static int Hit(ScrollbarDrawing.ScrollInfo info, bool vertical, Point point)
        {
            if (!info.Area.Bounds.Contains(point) || (info.State & 1) != 0) return 0;
            int extent = vertical ? info.Area.Bottom - info.Area.Top : info.Area.Right - info.Area.Left;
            int coordinate = vertical ? point.Y - info.Area.Top : point.X - info.Area.Left;
            int arrow = Math.Min(info.ArrowSize, extent / 2);
            if (coordinate < arrow) return (info.FirstArrow & 1) == 0 ? 1 : 0;
            if (coordinate >= extent - arrow) return (info.LastArrow & 1) == 0 ? 5 : 0;
            if (info.ThumbEnd <= info.ThumbStart || (info.Thumb & 0x18001) != 0) return 0;
            if (coordinate < info.ThumbStart) return 2;
            return coordinate < info.ThumbEnd ? 3 : 4;
        }
        void Hover(Point point, bool client)
        {
            bool vertical; ScrollbarDrawing.ScrollInfo info;
            int part = FindBar(point, out vertical, out info) && IsWindowEnabled(window) ? Hit(info, vertical, point) : 0;
            bool changed = part != hotPart || part != 0 && vertical != hotVertical;
            hotPart = part; hotVertical = vertical; pointer = point;
            var tracking = new MouseTracking { Size = Marshal.SizeOf(typeof(MouseTracking)), Window = window, Flags = (uint)(2 | (client ? 0 : 16)) };
            if (TrackMouseEvent(ref tracking) && client) clientLeaveArmed = true;
            if (changed) repaint();
        }

        internal bool Process(ref Message message)
        {
            if (disposed || window == IntPtr.Zero) return false;
            int kind = message.Msg;
            if (kind == 0x0082) { Detach(); return false; }
            if (kind == 0x0018 && message.WParam == IntPtr.Zero)
            { hiddenDuringUpdate = true; End(false, true); hotPart = 0; }
            if (kind == 0x000A && message.WParam == IntPtr.Zero || kind == 0x001F)
            { End(false, true); hotPart = 0; }
            if (kind == 0x0215 && active && message.LParam != window)
            { End(false, false); repaint(); }
            if (kind == 0x0100 && active && message.WParam.ToInt64() == (int)Keys.Escape)
            { End(false, true); message.Result = IntPtr.Zero; return true; }
            bool client = kind >= 0x0200 && kind <= 0x0203;
            bool move = kind == 0x0200 || kind == 0x00A0;
            bool down = kind == 0x0201 || kind == 0x0203 || kind == 0x00A1 || kind == 0x00A3;
            bool up = kind == 0x0202 || kind == 0x00A2;
            if (kind == 0x02A2 || kind == 0x02A3 && (standalone || clientLeaveArmed))
            {
                if (kind == 0x02A3) clientLeaveArmed = false;
                if (!active && hotPart != 0) { hotPart = 0; repaint(); }
                message.Result = IntPtr.Zero; return true;
            }
            if (!move && !down && !up) return false;
            Point point = MessagePoint(message, client);
            bool vertical; ScrollbarDrawing.ScrollInfo info;
            bool overBar = FindBar(point, out vertical, out info);
            if (!active && !overBar && !standalone)
            {
                if (hotPart != 0) { hotPart = 0; repaint(); }
                return false;
            }
            if (move)
            {
                pointer = point;
                if (active && dragging) Drag(point);
                else Hover(point, client);
            }
            else if (down)
            {
                if (!active && overBar && IsWindowEnabled(window)) Begin(vertical, info, point);
            }
            else if (active)
            {
                if (dragging) Drag(point);
                End(true, true); Hover(point, standalone);
            }
            // This is the boundary that prevents the native hover painter and
            // modal thumb/arrow loop from running at all.
            message.Result = IntPtr.Zero; return true;
        }

        void Begin(bool vertical, ScrollbarDrawing.ScrollInfo info, Point point)
        {
            int part = Hit(info, vertical, point); Range range;
            if (part != 0 && standalone && owner.TabStop)
            {
                // Match the native SCROLLBAR click focus contract. Grid-owned
                // bars have TabStop=false and must leave focus on their grid.
                int attachedGeneration = generation; IntPtr target = window;
                // Focus() must see a visible HWND. Its resulting WM_SETFOCUS
                // is buffered by the surface, after the native focus check.
                owner.Focus();
                if (disposed || generation != attachedGeneration || window != target || owner.IsDisposed || !owner.Visible || !owner.Enabled) return;
            }
            if (part == 0 || !ReadRange(vertical, out range) || range.Last <= range.Minimum) return;
            active = true; activeVertical = vertical; hotVertical = vertical;
            pressedPart = hotPart = part; dragging = part == 3; pointer = point;
            anchorPixel = vertical ? point.Y : point.X; anchorPosition = trackingPosition = range.Position;
            dragRange = range;
            previousCapture = GetCapture();
            // Combo lists already capture the pointer while open. Even setting
            // the same capture again can notify their native popup procedure.
            if (previousCapture != window) SetCapture(window);
            if (GetCapture() != window) { End(false, false); return; }
            if (!dragging)
            {
                Step();
                if (active) { repeat.Interval = 400; repeat.Start(); }
            }
            repaint();
        }
        void Drag(Point point)
        {
            ScrollbarDrawing.ScrollInfo info; Range range;
            if (!ReadBar(activeVertical, out info) || !ReadRange(activeVertical, out range)) { End(false, true); return; }
            int extent = activeVertical ? info.Area.Bottom - info.Area.Top : info.Area.Right - info.Area.Left;
            int travel = extent - 2 * Math.Min(info.ArrowSize, extent / 2) - (info.ThumbEnd - info.ThumbStart);
            if (travel <= 0 || range.Last <= range.Minimum) return;
            long delta = (long)(activeVertical ? point.Y : point.X) - anchorPixel;
            long moved = (long)Math.Round(delta * ((double)range.Last - range.Minimum) / travel);
            int next = (int)Math.Max(range.Minimum, Math.Min(range.Last, (long)anchorPosition + moved));
            if (next != trackingPosition)
            { trackingPosition = next; Apply(ScrollEventType.ThumbTrack, next); }
        }
        void Repeat(object sender, EventArgs e)
        {
            if (!active || dragging || window == IntPtr.Zero || GetCapture() != window)
            { repeat.Stop(); return; }
            if (redrawDepth != 0) return;
            repeat.Interval = 50; Step();
        }
        void Step()
        {
            ScrollbarDrawing.ScrollInfo info; Range range;
            if (!active || !ReadBar(activeVertical, out info) || !ReadRange(activeVertical, out range)) return;
            if (Hit(info, activeVertical, pointer) != pressedPart) return;
            ScrollBar bar = owner as ScrollBar;
            ScrollableControl panel = owner as ScrollableControl;
            int small = bar != null ? bar.SmallChange : panel != null ?
                (activeVertical ? panel.VerticalScroll.SmallChange : panel.HorizontalScroll.SmallChange) : 1;
            long amount = pressedPart == 1 || pressedPart == 5 ? Math.Max(1, small) : Math.Max(1L, range.Page);
            bool forward = pressedPart >= 4;
            int next = (int)Math.Max(range.Minimum, Math.Min(range.Last, (long)range.Position + (forward ? amount : -amount)));
            ScrollEventType type = pressedPart == 1 ? ScrollEventType.SmallDecrement : pressedPart == 5 ? ScrollEventType.SmallIncrement :
                pressedPart == 2 ? ScrollEventType.LargeDecrement : ScrollEventType.LargeIncrement;
            Apply(type, next);
        }
        void End(bool commit, bool release)
        {
            if (!active) return;
            repeat.Stop(); bool thumb = dragging; active = false; dragging = false; pressedPart = 0;
            IntPtr ownedWindow = window;
            try
            {
                Range range;
                if (commit && ReadRange(activeVertical, out range))
                {
                    if (thumb) Apply(ScrollEventType.ThumbPosition, trackingPosition);
                    Apply(ScrollEventType.EndScroll, range.Position);
                }
            }
            finally
            {
                // A combo drop-down may already own capture before the gesture;
                // leave that capture in place so item selection still works.
                if (release && GetCapture() == ownedWindow && previousCapture != ownedWindow)
                {
                    if (previousCapture != IntPtr.Zero && IsWindow(previousCapture) && IsWindowVisible(previousCapture)) SetCapture(previousCapture);
                    else ReleaseCapture();
                }
                previousCapture = IntPtr.Zero;
            }
            if (release) repaint();
        }
        void Apply(ScrollEventType type, int position)
        {
            if (window == IntPtr.Zero || disposed) return;
            IntPtr target = window;
            Update(delegate { ScrollbarScrollAdapter.Apply(owner, target, standalone, activeVertical, type, position); });
        }

        // Wheel, keyboard/native scroll notifications and our pointer adapter
        // use the same transaction. Native SetScrollInfo/ScrollWindow drawing
        // stays suspended until the themed frame and contents are ready.
        internal void Update(Action operation)
        {
            if (window == IntPtr.Zero || disposed) { operation(); return; }
            IntPtr target = window; int attachedGeneration = generation;
            bool suppress = redrawDepth == 0 && IsWindowVisible(target);
            redrawDepth++;
            if (suppress) { hiddenDuringUpdate = false; SendMessage(target, 0x000B, IntPtr.Zero, IntPtr.Zero); }
            try { operation(); SynchronizeTracking(); }
            catch { End(false, true); throw; }
            finally
            {
                redrawDepth--;
                if (suppress && generation == attachedGeneration && window == target && IsWindow(target))
                {
                    bool hide = hiddenDuringUpdate || owner != null && (owner.IsDisposed || !owner.Visible);
                    SendMessage(target, 0x000B, (IntPtr)1, IntPtr.Zero);
                    if (hide) ShowWindow(target, 0);
                    else NativeControlPaint.RefreshWindow(target);
                }
            }
            if (!disposed && window == target) repaint();
        }

        void SynchronizeTracking()
        {
            Range range;
            if (!active || !dragging || !ReadRange(activeVertical, out range)) return;
            if (range.Minimum != dragRange.Minimum || range.Maximum != dragRange.Maximum || range.Page != dragRange.Page)
            {
                // A Scroll handler can replace the range or constrain NewValue
                // (the grid also adjusts values to real rows). Rebase the held
                // thumb before painting so the next move has no stale anchor.
                anchorPixel = activeVertical ? pointer.Y : pointer.X;
                anchorPosition = trackingPosition = range.Position; dragRange = range;
            }
            else if (!(owner is ScrollableControl) || SystemInformation.DragFullWindows) trackingPosition = range.Position;
            else trackingPosition = Math.Max(range.Minimum, Math.Min(range.Last, trackingPosition));
        }

        internal static void Appearance(IntPtr window, uint objectId, ref ScrollbarDrawing.ScrollInfo info)
        {
            ScrollbarInteraction input;
            if (!Attached.TryGetValue(window, out input)) return;
            bool vertical = objectId == 0xFFFFFFFC ? input.standaloneVertical : objectId == 0xFFFFFFFB;
            bool hot = input.hotPart != 0 && input.hotVertical == vertical;
            bool pressed = input.active && input.activeVertical == vertical;
            info.FirstArrow = PartState(info.FirstArrow, 1, input, hot, pressed);
            info.FirstPage = PartState(info.FirstPage, 2, input, hot, pressed);
            info.Thumb = PartState(info.Thumb, 3, input, hot, pressed);
            info.LastPage = PartState(info.LastPage, 4, input, hot, pressed);
            info.LastArrow = PartState(info.LastArrow, 5, input, hot, pressed);
            if (pressed && input.dragging)
            {
                // Keep a true tracking position even when Windows is configured
                // to defer scrolling the content until the thumb is released.
                Range range;
                if (input.ReadRange(vertical, out range) && range.Last > range.Minimum)
                {
                    int extent = vertical ? info.Area.Bottom - info.Area.Top : info.Area.Right - info.Area.Left;
                    int arrow = Math.Min(info.ArrowSize, extent / 2), length = info.ThumbEnd - info.ThumbStart;
                    int travel = Math.Max(0, extent - 2 * arrow - length);
                    int position = Math.Max(range.Minimum, Math.Min(range.Last, input.trackingPosition));
                    int start = arrow + (int)Math.Round(((double)position - range.Minimum) * travel / ((double)range.Last - range.Minimum));
                    info.ThumbStart = start; info.ThumbEnd = start + length;
                }
            }
        }
        static uint PartState(uint state, int part, ScrollbarInteraction input, bool hot, bool pressed)
        {
            state &= ~136U;
            if (hot && input.hotPart == part) state |= 128;
            if (pressed && input.pressedPart == part && (input.dragging || input.hotPart == part)) state |= 8;
            return state;
        }
    }
}
