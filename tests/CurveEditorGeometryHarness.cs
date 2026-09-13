using System;
using Tk75.App;

public static class CurveEditorGeometryHarness
{
    static int assertions;

    static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new Exception("Curve editor geometry: " + message);
    }

    static void CheckSize(int fullWidth, int fullHeight, int toolsHeight, int scrollbarWidth)
    {
        CurveEditorGeometry value = CurveEditorGeometry.Create(fullWidth, fullHeight, toolsHeight, scrollbarWidth);
        string context = " (viewport " + fullWidth + "x" + fullHeight + ", tools " + toolsHeight + ", scrollbar " + scrollbarWidth + ")";
        Check(value.Width > 0 && value.Side > 0, "Transient sizes retain a positive drawing surface" + context);
        Check(value.Width <= Math.Max(1, fullWidth), "Content never demands a horizontal scrollbar" + context);
        Check(value.SettingsHeight >= 198, "All six compact setting rows have at least 192px plus their margins" + context);
        Check(value.TotalHeight == Math.Max(0, toolsHeight) + value.ResponseHeight + value.SettingsHeight,
            "Explicit parent bounds contain every child row without hidden row allocation" + context);
        Check(value.NeedsScroll == (value.TotalHeight > Math.Max(0, fullHeight)), "Content and scrollbar state agree immediately" + context);
        if (!value.NeedsScroll)
        {
            Check(value.Width == Math.Max(1, fullWidth), "An unnecessary scrollbar never removes graph width" + context);
            Check(value.TotalHeight == Math.Max(0, fullHeight), "Settings fill remaining viewport height exactly" + context);
        }
        else
            Check(value.TotalHeight >= Math.Max(0, fullHeight) + 1, "Necessary scroll extent survives graph shrinkage" + context);

        int graphLeft = value.Beside ? 184 + 12 : 0;
        int graphRight = graphLeft + value.Side;
        int graphBottom = value.Side;
        Check(graphRight == value.Width, "A square graph fills its complete horizontal allotment" + context);
        Check(graphBottom <= value.ResponseHeight, "Square graph fits inside the response parent" + context);
        if (value.Beside)
        {
            Check(value.Width >= 380 && value.Side >= 184, "Graph and controls both meet the beside minimum width" + context);
            Check(value.ResponseHeight >= 264, "Vertical controls retain their full height" + context);
        }
        else
        {
            Check(value.Width < 380, "Narrow layouts stack instead of clipping vertical controls" + context);
            Check(value.Side + 8 + 264 == value.ResponseHeight, "Stacked controls fit exactly below the graph" + context);
        }

        // Model all native visibility transitions. Client width may change,
        // but borderless outer viewport bounds stay fixed. Neither the old bar
        // flag nor an intermediate client width is fed into the helper.
        foreach (bool oldScrollbarVisible in new[] { false, true, false, true })
        {
            int oldClientWidth = fullWidth - (oldScrollbarVisible ? scrollbarWidth : 0);
            int stableOuterWidth = oldClientWidth + (oldScrollbarVisible ? scrollbarWidth : 0);
            CurveEditorGeometry repeated = CurveEditorGeometry.Create(stableOuterWidth, fullHeight, toolsHeight, scrollbarWidth);
            Check(repeated.Width == value.Width && repeated.Side == value.Side && repeated.Beside == value.Beside &&
                repeated.ResponseHeight == value.ResponseHeight && repeated.SettingsHeight == value.SettingsHeight &&
                repeated.TotalHeight == value.TotalHeight && repeated.NeedsScroll == value.NeedsScroll,
                "Scrollbar insertion/removal converges to identical geometry from either prior state" + context);
        }
    }

    public static void Run()
    {
        assertions = 0;
        CurveEditorGeometry oscillation = CurveEditorGeometry.Create(484, 550, 78, 17);
        Check(oscillation.NeedsScroll && oscillation.Width == 467 && oscillation.Side == 271,
            "The formerly oscillating 484x550 viewport chooses the narrowed graph once.");
        Check(oscillation.SettingsHeight == 202 && oscillation.TotalHeight == 551,
            "A one-pixel scroll extent prevents the old 564 -> 550 -> 564 height cycle.");

        CurveEditorGeometry exactFit = CurveEditorGeometry.Create(380, 540, 78, 17);
        Check(!exactFit.NeedsScroll && exactFit.Beside && exactFit.Side == 184 && exactFit.TotalHeight == 540,
            "The exact beside breakpoint fits without reserving a redundant scrollbar.");
        CurveEditorGeometry narrowAfterScroll = CurveEditorGeometry.Create(380, 539, 78, 17);
        Check(narrowAfterScroll.NeedsScroll && !narrowAfterScroll.Beside && narrowAfterScroll.Width == 363,
            "Crossing the beside breakpoint after scrollbar insertion deliberately stacks the controls.");

        // Dense sweep of both width breakpoints (before and after a 17px bar)
        // and the complete feedback interval around each minimum content height.
        for (int width = 350; width <= 510; width++)
        {
            int fitHeight = width >= 380 ? 78 + Math.Max(264, width - 196) + 198 : 78 + width + 8 + 264 + 198;
            for (int offset = -35; offset <= 35; offset++)
                CheckSize(width, fitHeight + offset, 78, 17);
        }
        // Other window sizes, font-dependent toolbar heights and DPI-dependent
        // scrollbar widths must use the same bounded calculation.
        foreach (int tools in new[] { 0, 78, 117, 156 })
            foreach (int bar in new[] { 0, 17, 26, 34 })
                for (int width = 1; width <= 1600; width += 53)
                    for (int height = 0; height <= 1600; height += 71)
                        CheckSize(width, height, tools, bar);
        CheckSize(0, 0, 78, 17);
        CheckSize(-1, -1, -1, -1);
        Console.WriteLine("CURVE EDITOR GEOMETRY PASS: " + assertions + " assertions; square graph, child containment, minimum rows and stable scrollbar transitions.");
    }
}
