using System;
using Tk75.App;

public static class SliderDragPrecisionHarness
{
    static int assertions;
    static void Check(bool value, string message)
    { assertions++; if (!value) throw new Exception("Slider precision: " + message); }
    static void Near(double actual, double expected, string message)
    { Check(Math.Abs(actual - expected) < 1e-10, message + "; actual=" + actual + ", expected=" + expected); }

    public static void Run()
    {
        assertions = 0;
        var gesture = new SliderDragPrecision();
        gesture.Begin(.25, 100, 20, 1);
        Check(!gesture.Move(100, 400, .005, 0, 1), "Moving away from the track does not edit the value.");
        Near(gesture.Value, .25, "Orthogonal pickup retains the exact original");
        Check(!gesture.Move(100, 20, .005, 0, 1), "Returning to the track also does not edit.");
        gesture.Move(120, 20, .005, 0, 1);
        Near(gesture.Value, .35, "Movement on the track remains direct");
        gesture.Move(120, 108, .005, 0, 1);
        Near(gesture.Value, .35, "Entering fine mode never repositions the thumb");
        gesture.Move(140, 108, .005, 0, 1);
        Near(gesture.Value, .40, "At 88 logical pixels the same movement changes half as much");
        gesture.Move(140, 20, .005, 0, 1);
        Near(gesture.Value, .40, "Returning from fine mode preserves its accumulated edit");
        gesture.Move(160, 20, .005, 0, 1);
        Near(gesture.Value, .50, "Direct speed resumes without catching up to the physical pointer");

        double previous = 1;
        for (int distance = 0; distance <= 2400; distance += 3)
        {
            double gain = SliderDragPrecision.Sensitivity(distance, 1);
            Check(gain > 0 && gain <= previous, "Gain decreases continuously without reversing or becoming zero.");
            Near(gain, SliderDragPrecision.Sensitivity(-distance, 1), "Both sides of the track use identical precision");
            Near(gain, SliderDragPrecision.Sensitivity(distance * 2, 2), "Equivalent distance under doubled UI scale feels identical");
            previous = gain;
        }
        Near(SliderDragPrecision.Sensitivity(24, 1), 1, "Pickup tolerance retains direct control");
        Near(SliderDragPrecision.Sensitivity(152, 1), .2, "A larger offset further reduces the speed");

        // Repeated sub-display-step changes must accumulate independently of
        // the number shown by a 0.001-resolution control.
        gesture.Begin(.25, 0, 0, 1);
        double displayed = .25;
        for (int axis = 1; axis <= 100; axis++)
        {
            gesture.Move(axis, 664, .001, 0, 1);
            displayed = Math.Round(gesture.Value, 3);
        }
        Check(displayed > .25 && displayed < .253, "Tiny moves accumulate until the control can display a finer step.");
        var single = new SliderDragPrecision(); single.Begin(.25, 0, 0, 1); single.Move(100, 664, .001, 0, 1);
        Near(gesture.Value, single.Value, "Mouse event frequency does not change constant-offset movement");

        gesture.Begin(.99, 10, 0, 1); gesture.Move(100, 0, .01, 0, 1);
        Near(gesture.Value, 1, "Dragging past a bound stops exactly on it");
        gesture.Move(99, 0, .01, 0, 1);
        Near(gesture.Value, .99, "Reversal responds immediately without hidden overshoot debt");
        gesture.Move(-200, 0, .01, 0, 1); gesture.Move(-199, 0, .01, 0, 1);
        Near(gesture.Value, .01, "The opposite bound has the same immediate reversal");
        gesture.Begin(.5, 100, 12, 1); gesture.Move(120, 12, -.01, .1, .8);
        Near(gesture.Value, .3, "Output rails can reverse the value direction without reversing fine control");
        gesture.Move(120, 100, -.01, .1, .8); gesture.Move(130, 100, -.01, .1, .8);
        Near(gesture.Value, .25, "Vertical and reversed rails use the same precision curve");
        gesture.Begin(.4, 0, 0, 1); gesture.Move(100, 0, 0, .4, .4);
        Near(gesture.Value, .4, "A collapsed allowed range remains stable");
        gesture.Begin(37.125, 700, 50, 1);
        Check(!gesture.Move(700, -900, 1, 0, 100), "A new gesture clears prior pointer history.");
        Near(gesture.Value, 37.125, "A no-op gesture never rounds a saved fractional setting");
        foreach (double[] values in new[] {
            new[] { 200.25, 1.0, 0.0, 1000.0 }, new[] { 25.125, .01, .01, 100.0 },
            new[] { .12345, .01, 0.0, 2.0 }, new[] { .25045, .001, 0.0, 1.0 }
        })
        {
            double origin = values[0], step = values[1], low = values[2], high = values[3];
            foreach (int direction in new[] { -1, 1 })
            {
                gesture.Begin(origin, 0, 0, 1);
                gesture.Move(1, 3000, direction * step / 2, low, high);
                Near(gesture.QuantizedValue(step, low, high), origin,
                    "A first sub-step gesture preserves an off-grid pickup instead of rounding it in either direction");
                double displayedPrevious = origin;
                gesture.Begin(origin, 0, 0, 1);
                for (int axis = 1; axis <= 200; axis++)
                {
                    gesture.Move(axis, 664, direction * step / 2, low, high);
                    double quantized = gesture.QuantizedValue(step, low, high);
                    Check(direction * (quantized - displayedPrevious) >= 0, "Fine movement never rounds an off-grid value against pointer direction.");
                    displayedPrevious = quantized;
                }
                Near(displayedPrevious, origin + direction * step, "Accumulated fine movement retains one whole step relative to the exact pickup");
            }
            gesture.Begin(origin, 0, 0, 1); gesture.Move(100000, 0, step, low, high);
            Near(gesture.QuantizedValue(step, low, high), high, "An off-grid pickup can still reach the exact upper limit");
            gesture.Move(-100000, 0, step, low, high);
            Near(gesture.QuantizedValue(step, low, high), low, "An off-grid pickup can still reach the exact lower limit");
        }
        Console.WriteLine("SLIDER PRECISION PASS: " + assertions + " assertions; relative fine control, DPI, symmetry, accumulation and bounds.");
    }
}
