using System;
using System.Collections.Generic;
using System.Linq;
using Tk75.Mapping;

public static class BezierGestureHarness
{
    static int checks;
    static void Require(bool value, string reason) { checks++; if (!value) throw new Exception(reason); }
    static bool Near(double a, double b) { return Math.Abs(a - b) < 1e-10; }
    static List<CurvePoint> Source()
    { return new List<CurvePoint> { new CurvePoint(0, 0), new CurvePoint(.4, .4), new CurvePoint(1, 1) }; }
    static void Valid(CurvePointEditing edit)
    {
        var points = edit.Preview; double[] slopes = BezierCurve.EffectiveTangents(points);
        Require(points[0].X == 0 && points[0].Y == 0 && points[points.Count - 1].X == 1 && points[points.Count - 1].Y == 1, "handle drag moved endpoints");
        Require(slopes.All(s => !double.IsNaN(s) && !double.IsInfinity(s) && s >= 0), "gesture created an invalid effective slope");
        for (int i = 1; i < points.Count; i++) Require(points[i].X > points[i - 1].X && points[i].Y >= points[i - 1].Y, "gesture changed node order");
    }
    public static int Run()
    {
        checks = 0; var source = Source(); var edit = new CurvePointEditing(source); int index;
        CurvePoint handle = edit.GetHandle(1, true);
        Require(edit.HitPart(1, 40 + handle.X * 400, 30 + (1 - handle.Y) * 160, 40, 30, 400, 160, 9, out index) == 2 && index == 1, "outgoing handle was not hit");
        handle = edit.GetHandle(1, false);
        Require(edit.HitPart(1, 40 + handle.X * 400, 30 + (1 - handle.Y) * 160, 40, 30, 400, 160, 9, out index) == 1 && index == 1, "incoming handle was not hit");
        Require(edit.HitPart(-1, 40 + handle.X * 400, 30 + (1 - handle.Y) * 160, 40, 30, 400, 160, 9, out index) == -1, "unselected handles became hit targets");
        Require(edit.HitPart(1, 200, 126, 40, 30, 400, 160, 9, out index) == 0 && index == 1, "node hit was replaced with a handle");
        Require(edit.HitPart(1, double.NaN, 0, 0, 0, 400, 160, 9, out index) == -1, "invalid hit geometry accepted");
        var close = new CurvePointEditing(new[] { new CurvePoint(0, 0), new CurvePoint(.01, .01), new CurvePoint(1, 1) });
        handle = close.GetHandle(0, true);
        Require(close.HitPart(0, handle.X * 300, (1 - handle.Y) * 150, 0, 0, 300, 150, 9, out index) == 2 && index == 0, "near-node handle incorrectly started a node drag");
        Require(close.HitPart(0, 0, 150, 0, 0, 300, 150, 9, out index) == 0 && index == 0, "endpoint center failed to select the node");

        Require(!edit.Begin(0, 0, 0, .01), "endpoint node became movable");
        Require(!edit.BeginHandle(0, false) && !edit.BeginHandle(2, true), "nonexistent exterior handle accepted");
        Require(edit.BeginHandle(0, true) && edit.EditingHandle, "first endpoint handle cannot be edited");
        Require(edit.Move(.1, 0), "endpoint handle did not flatten"); Valid(edit);
        var result = edit.Finish(); Require(result != null && result[0].Tangent == 0 && edit.Finish() == null, "endpoint edit did not finish once");
        Require(source[0].Tangent == null, "handle changed the original profile");
        edit = new CurvePointEditing(Source());
        Require(edit.BeginHandle(2, false) && edit.Move(.9, 1), "last endpoint handle cannot be edited"); Valid(edit); edit.Cancel();
        Require(edit.Finish() == null && edit.Preview[2].Tangent == null, "cancelled endpoint edit survived");

        edit = new CurvePointEditing(Source()); handle = edit.GetHandle(1, true);
        Require(edit.BeginHandle(1, true), "interior handle did not start");
        Require(!edit.BeginHandle(1, false) && !edit.Begin(1, .4, .4, .01), "overlapping gestures allowed");
        Require(edit.Move(.8, .6), "cursor angle did not change tangent");
        var right = edit.GetHandle(1, true); var left = edit.GetHandle(1, false);
        Require(Near(right.X, handle.X) && Near(edit.Preview[1].Tangent.Value, .5), "handle X length changed or cursor X direction ignored");
        Require(Near((.4 - left.Y) / (.4 - left.X), (right.Y - .4) / (right.X - .4)), "two grips no longer share one tangent");
        Require(!edit.Move(double.NaN, .4) && !edit.Move(.5, double.PositiveInfinity), "nonfinite cursor changed preview");
        Require(edit.Move(handle.X, handle.Y), "return to original handle failed");
        Require(edit.Finish() == null && edit.Preview[1].Tangent == null, "return-to-origin converted auto tangent or added undo");

        edit = new CurvePointEditing(Source()); edit.BeginHandle(1, false);
        Require(edit.Move(.2, .4), "incoming handle did not flatten");
        result = edit.Finish(); Require(result != null && result[1].Tangent == 0, "incoming edit was not committed");
        result[1].Tangent = 10; Require(edit.Preview[1].Tangent == 0, "commit result aliases gesture state");
        edit = new CurvePointEditing(result); var reset = edit.ResetTangent(1);
        Require(reset != null && reset[1].Tangent == null && edit.Preview[1].Tangent == 10, "automatic reset mutated original or failed");
        Require(new CurvePointEditing(reset).ResetTangent(1) == null, "already automatic reset produced a no-op edit");
        Require(edit.ResetTangent(-1) == null && edit.ResetTangent(9) == null, "out-of-range reset accepted");
        edit.BeginHandle(1, true); Require(edit.ResetTangent(1) == null, "reset interleaved with drag"); edit.Cancel();
        Require(edit.Preview[1].Tangent == 10 && edit.Finish() == null, "cancel lost manual tangent");

        var manual = Source(); manual[1].Tangent = .7; edit = new CurvePointEditing(manual);
        var snapshot = edit.Preview; snapshot[1].Tangent = 20;
        Require(edit.Preview[1].Tangent == .7, "preview tangent aliases state");
        Require(edit.Begin(1, .4, .4, .01) && edit.Move(.5, .6), "Bezier node could not move");
        result = edit.Finish(); Require(result[1].Tangent == .7, "moving node discarded its manual tangent");
        Require(!CurvePointEditing.Same(Source(), manual), "parent refresh equality ignored tangent");
        Require(CurvePointEditing.Same(manual, new CurvePointEditing(manual).Preview), "cloned tangent compared unequal");
        edit = new CurvePointEditing(manual); edit.Begin(-1, .7, .8, .01); result = edit.Finish();
        Require(result[1].Tangent == .7 && result[2].Tangent == null, "new node overwrote tangent or failed to default to auto");
        Require(new CurvePointEditing(result).Remove(2)[1].Tangent == .7, "removing node discarded another tangent");

        var random = new Random(53);
        for (int node = 0; node < 3; node++)
            for (int side = 0; side < 2; side++)
            {
                edit = new CurvePointEditing(Source()); if (!edit.BeginHandle(node, side == 1)) continue;
                for (int step = 0; step < 20; step++)
                {
                    edit.Move(random.NextDouble() * 3 - 1, random.NextDouble() * 3 - 1); Valid(edit);
                    double? tangent = edit.Preview[node].Tangent;
                    Require(!tangent.HasValue || Near(tangent.Value, BezierCurve.EffectiveTangent(edit.Preview, node)), "clamped manual tangent changed after reevaluation");
                }
                edit.Cancel(); Require(edit.Finish() == null && CurvePointEditing.Same(edit.Preview, Source()), "random cancelled handle sequence survived");
            }
        return checks;
    }
}
