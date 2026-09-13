using System;

namespace Tk75.App
{
    // Pure geometry: scrollbar visibility is decided from the full, stable
    // viewport, never from ClientSize or a partially updated scrollbar flag.
    internal struct CurveEditorGeometry
    {
        public const int ThresholdWidth = 184;
        public const int HorizontalGap = 12;
        public const int MinimumGraphSide = 184;
        public const int MinimumThresholdHeight = 264;
        public const int StackedGap = 8;
        public const int MinimumSettingsHeight = 198;
        public const int BesideMinimumWidth = ThresholdWidth + HorizontalGap + MinimumGraphSide;

        public readonly int Width, Side, ResponseHeight, SettingsHeight, TotalHeight;
        public readonly bool Beside, NeedsScroll;

        private CurveEditorGeometry(int width, int side, bool beside, int responseHeight,
            int settingsHeight, int totalHeight, bool needsScroll)
        {
            Width = width; Side = side; Beside = beside;
            ResponseHeight = responseHeight; SettingsHeight = settingsHeight;
            TotalHeight = totalHeight; NeedsScroll = needsScroll;
        }

        static int ResponseSize(int width)
        {
            return width >= BesideMinimumWidth
                ? Math.Max(MinimumThresholdHeight, width - ThresholdWidth - HorizontalGap)
                : width + StackedGap + MinimumThresholdHeight;
        }

        public static CurveEditorGeometry Create(int fullWidth, int fullHeight, int toolsHeight, int scrollbarWidth)
        {
            fullWidth = Math.Max(1, fullWidth);
            fullHeight = Math.Max(0, fullHeight);
            toolsHeight = Math.Max(0, toolsHeight);
            scrollbarWidth = Math.Max(0, scrollbarWidth);

            bool needsScroll = (long)toolsHeight + ResponseSize(fullWidth) + MinimumSettingsHeight > fullHeight;
            int width = Math.Max(1, fullWidth - (needsScroll ? scrollbarWidth : 0));
            bool beside = width >= BesideMinimumWidth;
            int side = beside ? width - ThresholdWidth - HorizontalGap : width;
            int responseHeight = ResponseSize(width);
            // Narrowing a square graph can otherwise make its scrollbar
            // disappear again. Keep one extra content pixel while scrolling is
            // needed, so native layout cannot oscillate between two widths.
            int settingsHeight = Math.Max(MinimumSettingsHeight,
                fullHeight - toolsHeight - responseHeight + (needsScroll ? 1 : 0));
            return new CurveEditorGeometry(width, side, beside, responseHeight,
                settingsHeight, toolsHeight + responseHeight + settingsHeight, needsScroll);
        }
    }
}
