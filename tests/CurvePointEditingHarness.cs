using System;
using System.Collections.Generic;
using System.Linq;
using Tk75.Mapping;

public static class CurvePointEditingHarness
{
    static int checks;
    static void Require(bool condition, string reason) { checks++; if (!condition) throw new Exception(reason); }
    static List<CurvePoint> Source()
    { return new List<CurvePoint> { new CurvePoint(0, 0), new CurvePoint(.25, .3), new CurvePoint(.6, .7), new CurvePoint(1, 1) }; }
    static List<CurvePoint> Ends()
    { return new List<CurvePoint> { new CurvePoint(0, 0), new CurvePoint(1, 1) }; }
    static void Valid(IList<CurvePoint> points)
    {
        bool okay = points.Count >= 2 && points.Count <= 64 && points[0].X == 0 && points[0].Y == 0 && points[points.Count - 1].X == 1 && points[points.Count - 1].Y == 1;
        for (int i = 0; i < points.Count; i++)
        {
            CurvePoint p = points[i];
            okay &= !double.IsNaN(p.X) && !double.IsInfinity(p.X) && !double.IsNaN(p.Y) && !double.IsInfinity(p.Y) && p.X >= 0 && p.X <= 1 && p.Y >= 0 && p.Y <= 1;
            if (i != 0) okay &= p.X > points[i - 1].X && p.Y >= points[i - 1].Y;
        }
        Require(okay, "gesture violated the curve model bounds/order/endpoints");
    }
    static void Reject(IEnumerable<CurvePoint> source)
    {
        bool rejected = false;
        try { new CurvePointEditing(source); } catch (ArgumentException) { rejected = true; }
        Require(rejected, "invalid source accepted");
    }
    public static int Run()
    {
        checks = 0;
        var source = Source(); var edit = new CurvePointEditing(source);
        // Screen distance, including Y, defines a hit at every graph aspect ratio.
        foreach (double width in new[] { 100.0, 280.0, 800.0 })
            foreach (double height in new[] { 100.0, 400.0 })
            {
                double x = 42 + .25 * width, y = 36 + .7 * height;
                Require(edit.HitTest(x + 6, y + 6, 42, 36, width, height, 9) == 1, "visible handle not found inside radius");
                Require(edit.HitTest(x + 7, y + 7, 42, 36, width, height, 9) == -1, "square instead of circular hit area");
                Require(edit.HitTest(x, y + 30, 42, 36, width, height, 9) == -1, "same X with distant Y grabbed a point");
            }
        Require(edit.HitTest(42, 136, 42, 36, 100, 100, 9) == 0, "endpoint hit missing");
        Require(!edit.Begin(0, 0, 0, .01) && !edit.Begin(3, 1, 1, .01), "endpoint became movable");
        Require(edit.HitTest(0, 0, 0, 0, 0, 100, 9) == -1, "invalid plot accepted");
        Require(edit.HitTest(double.NaN, 0, 0, 0, 100, 100, 9) == -1, "NaN hit accepted");
        Require(edit.HitTest(0, 0, 0, 0, 100, 100, double.PositiveInfinity) == -1, "infinite radius accepted");
        Require(edit.Begin(1, .29, .34, .01), "point could not be grabbed");
        Require(CurvePointEditing.Same(edit.Preview, source), "grabbing within hit radius jumped the point");
        Require(!edit.Begin(2, .6, .7, .01), "second gesture started during a drag");
        Require(edit.Move(10, 10), "drag did not move"); Valid(edit.Preview);
        Require(edit.Preview[1].X < .6 && edit.Preview[1].Y == .7, "drag crossed upper neighbor");
        Require(edit.Move(-10, -10), "negative drag did not move"); Valid(edit.Preview);
        Require(edit.Preview[1].X > 0 && edit.Preview[1].Y == 0, "drag crossed lower neighbor");
        Require(!edit.Move(double.NaN, .4) && !edit.Move(.4, double.NegativeInfinity), "invalid pointer coordinates changed preview");
        Require(edit.Move(.25, .3), "drag back to original failed");
        Require(edit.Finish() == null && edit.Finish() == null, "unchanged gesture requested an undo entry");
        Require(!edit.Move(.5, .5), "finished gesture still moved");

        edit = new CurvePointEditing(Ends());
        Require(edit.Begin(-1, .42, .63, .01), "new point was not inserted");
        Require(edit.Preview.Count == 3 && edit.Preview[1].X == .42 && edit.Preview[1].Y == .63, "new point location changed");
        var preview = edit.Preview; preview[1].X = .9;
        Require(edit.Preview[1].X == .42, "caller mutated local preview storage");
        var committed = edit.Finish();
        Require(committed != null && committed.Count == 3 && edit.Finish() == null, "gesture did not emit exactly one changed snapshot");
        committed[1].X = .8;
        Require(edit.Preview[1].X == .42, "committed snapshot aliases editor storage");

        edit = new CurvePointEditing(source); source[1].X = .15;
        Require(edit.Preview[1].X == .25, "profile mutation changed the gesture source");
        Require(edit.Begin(1, .25, .3, .01) && edit.Move(.5, .6), "cancel test did not start");
        Require(edit.Remove(1) == null, "deletion interleaved with a drag");
        edit.Cancel(); Require(edit.Finish() == null && CurvePointEditing.Same(edit.Preview, Source()), "cancel committed or retained moved point");
        edit = new CurvePointEditing(Ends()); edit.Begin(-1, .3, .4, .01); edit.Cancel();
        Require(edit.Finish() == null && edit.Preview.Count == 2, "cancelled insertion survived");

        edit = new CurvePointEditing(Source());
        Require(edit.Remove(-1) == null && edit.Remove(0) == null && edit.Remove(3) == null, "miss or endpoint could delete");
        var removed = edit.Remove(1); Valid(removed);
        Require(removed.Count == 3 && removed[1].X == .6 && edit.Preview.Count == 4, "delete mutated the source or removed wrong point");
        var dense = Enumerable.Range(0, 64).Select(i => new CurvePoint(i / 63.0, i / 63.0)).ToList();
        edit = new CurvePointEditing(dense);
        Require(!edit.Begin(-1, .5, .5, .001), "65th point accepted");
        Require(edit.Begin(20, .3, .3, .001) && edit.Move(.4, .4), "64-point curve could not move existing point"); Valid(edit.Finish());
        Require(new CurvePointEditing(dense).Remove(20).Count == 63, "64-point curve could not delete point");

        double middle = .5, next = BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(middle) + 1);
        edit = new CurvePointEditing(new[] { new CurvePoint(0, 0), new CurvePoint(middle, .4), new CurvePoint(next, .6), new CurvePoint(1, 1) });
        Require(edit.Begin(1, middle, .4, .02), "dense imported point could not be selected");
        edit.Move(1, 1); Valid(edit.Preview);
        Require(edit.Preview[1].X < next, "rounded spacing crossed a one-ULP neighbor");

        var random = new Random(7219);
        for (int gesture = 0; gesture < 60; gesture++)
        {
            edit = new CurvePointEditing(Source()); int selected = 1 + gesture % 2;
            Require(edit.Begin(selected, 0, 0, .01), "random sequence could not start");
            for (int step = 0; step < 16; step++)
            {
                edit.Move(random.NextDouble() * 4 - 1.5, random.NextDouble() * 4 - 1.5); Valid(edit.Preview);
            }
            var changed = edit.Finish(); if (changed != null) Valid(changed);
            Require(edit.Finish() == null, "random sequence emitted duplicate commit");
        }
        Require(CurvePointEditing.Same(Source(), Source()), "identical parent refresh compared unequal");
        Require(!CurvePointEditing.Same(Source(), Ends()) && CurvePointEditing.Same(null, null), "settings equivalence invalid");
        edit = new CurvePointEditing(Ends());
        Require(!edit.Begin(-1, .5, .5, 0) && !edit.Begin(-1, double.NaN, .5, .01) && !edit.Begin(-1, .5, .5, .5), "invalid new point request accepted");
        Reject(null); Reject(new CurvePoint[0]); Reject(new CurvePoint[] { null, new CurvePoint(1, 1) });
        Reject(new[] { new CurvePoint(0, 0), new CurvePoint(.5, double.NaN), new CurvePoint(1, 1) });
        Reject(new[] { new CurvePoint(0, 0), new CurvePoint(.5, .7), new CurvePoint(.5, .8), new CurvePoint(1, 1) });
        Reject(new[] { new CurvePoint(0, 0), new CurvePoint(.2, .7), new CurvePoint(.5, .6), new CurvePoint(1, 1) });
        Reject(new[] { new CurvePoint(0, .1), new CurvePoint(1, 1) });
        Reject(Enumerable.Range(0, 65).Select(i => new CurvePoint(i / 64.0, i / 64.0)));
        return checks;
    }
}
