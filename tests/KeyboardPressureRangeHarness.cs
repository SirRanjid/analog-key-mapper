using System;
using System.IO;
using System.Linq;
using Tk75.App;
using Tk75.Mapping;

public static class KeyboardPressureRangeHarness
{
    static int checks;
    static void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }
    public static int Main(string[] args)
    {
        try
        {
            var original = new CalibrationDocument { DeviceIdentity = new string('a', 64), ProtocolFingerprint = new string('b', 64) };
            var standard = new KeyboardPressureRange(original);
            Check(standard.Minimum == 0 && standard.Maximum == 385 && standard.IsDefault, "Default range stays 0..385");
            Check(standard.Resolve().Count == 256 && standard.Resolve().Values.All(c => c.Rest == 0 && c.Bottom == 385), "Every key shares the default range");
            Check(Object.ReferenceEquals(standard.Resolve(), standard.Resolve()) && Object.ReferenceEquals(standard.ForKey(14), standard.Resolve()[14]), "Resolution and key reads reuse one cached snapshot");
            original.Entries.Add(new CalibrationEntry { KeyIndex = 14, Rest = 5, Bottom = 390 });
            original.Entries.Add(new CalibrationEntry { KeyIndex = 9, Rest = 0, Bottom = 410 });
            var migrated = new KeyboardPressureRange(original);
            Check(migrated.Minimum == 0 && migrated.Maximum == 410 && !migrated.IsDefault, "Legacy measurements supply one shared envelope");
            Check(!original.GlobalMinimum.HasValue && original.Entries.Count == 2, "Reading legacy data never rewrites it");
            var unsupported = new CalibrationDocument();
            unsupported.Entries.Add(new CalibrationEntry { KeyIndex = 1, Rest = 500, Bottom = 0 });
            unsupported.Entries.Add(new CalibrationEntry { KeyIndex = 2, Rest = -10, Bottom = 99999 });
            var compatible = new KeyboardPressureRange(unsupported);
            Check(compatible.IsDefault && compatible.HasIgnoredLegacyValues && compatible.Maximum == 385 && unsupported.Entries.Count == 2,
                "Unsupported legacy endpoints remain on disk without breaking the editor or inverting output");
            var changed = migrated.Apply(original, new[] { 14 }, 10, 600, 385);
            var range = new KeyboardPressureRange(changed);
            Check(range.ScaleMaximum == 600 && range.ForKey(14).Rest == 10 && range.ForKey(14).Bottom == 600, "Higher measurement expands the selected key and shared scale");
            Check(range.Resolve().Where(item => item.Key != 14).All(item => item.Value.Rest == 0 && item.Value.Bottom == 410), "Single-key edit preserves every untouched legacy fallback");
            Check(changed.Entries.Count == 2 && original.Entries[0].Bottom == 390, "Original measurements remain available");
            Check(range.Depth(14, 10) == 0 && range.Depth(14, 305) == .5 && range.Depth(14, 600) == 1 && range.Depth(14, 900) == 1 && range.Depth(9, 205) == .5, "UI and controller normalization use the requested key's bounds");
            var rc5 = new CalibrationDocument { DeviceIdentity = original.DeviceIdentity, ProtocolFingerprint = original.ProtocolFingerprint,
                GlobalMinimum = 20, GlobalMaximum = 700, ScaleMaximum = 800, Entries = original.Entries.ToList() };
            var rc5Range = new KeyboardPressureRange(rc5);
            var grouped = rc5Range.Apply(rc5, new[] { 9, 14, 14 }, 5, 500, 800);
            var groupedRange = new KeyboardPressureRange(grouped);
            Check(grouped.KeyRanges.Count == 2 && groupedRange.ForKey(9).Rest == 5 && groupedRange.ForKey(14).Bottom == 500,
                "A group edit changes every selected key once");
            Check(grouped.GlobalMinimum == 20 && grouped.GlobalMaximum == 700 && groupedRange.Resolve().Where(item => item.Key != 9 && item.Key != 14).All(item => item.Value.Rest == 20 && item.Value.Bottom == 700),
                "rc5 shared settings and every unselected key retain their exact effective calibration");
            var reduced = groupedRange.ApplyScale(grouped, 100);
            var reducedRange = new KeyboardPressureRange(reduced);
            Check(reduced.ScaleMaximum == 700 && reducedRange.ForKey(9).Bottom == 500 && reducedRange.ForKey(1).Bottom == 700,
                "Lowering shared scale stops at the largest endpoint without changing any key");
            var secondEdit = new KeyboardPressureRange(reduced).Apply(reduced, new[] { 9 }, 2, 900, 700);
            var secondRange = new KeyboardPressureRange(secondEdit);
            Check(secondRange.ScaleMaximum == 900 && secondRange.ForKey(9).Bottom == 900 && secondRange.ForKey(14).Bottom == 500 && secondRange.ForKey(1).Bottom == 700,
                "A later measurement expands the scale while preserving previously edited and fallback keys");
            var defaultEdited = new KeyboardPressureRange(standard.Apply(original, new[] { 1 }, 0, 385, 385));
            Check(!defaultEdited.IsDefaultForKey(1), "Explicit key calibration carries its own measured/default state");
            var store = new WorkspaceStore(args[0]); store.SaveCalibration(changed);
            var loaded = store.LoadCalibration(original.DeviceIdentity, original.ProtocolFingerprint);
            Check(!loaded.GlobalMinimum.HasValue && loaded.KeyRanges.Count == 1 && loaded.KeyRanges[0].Minimum == 10 && loaded.KeyRanges[0].Maximum == 600 && loaded.ScaleMaximum == 600, "Key override and shared scale survive restart");
            string file = store.CalibrationPath(original.DeviceIdentity), preserved = File.ReadAllText(file);
            foreach (var invalid in new[] {
                migrated.Apply(original, new[] { 14 }, 100, 100, 385), migrated.Apply(original, new[] { 14 }, -1, 385, 385),
                migrated.Apply(original, new[] { 14 }, 0, 65536, 65536), migrated.Apply(original, new[] { 14 }, Double.NaN, 385, 385) })
            {
                bool rejected = false; try { store.SaveCalibration(invalid); } catch (InvalidDataException) { rejected = true; }
                Check(rejected && File.ReadAllText(file) == preserved, "Invalid range leaves saved values intact");
            }
            foreach (string replacement in new[] { "\"Maximum\":null", "\"Maximum\":\"600\"", "\"Maximum\":600,\"Maximum\":700", "\"Maximum\":1e400" })
            {
                File.WriteAllText(file, preserved.Replace("\"Maximum\":600", replacement));
                bool rejected = false; try { store.LoadCalibration(original.DeviceIdentity, original.ProtocolFingerprint); } catch (InvalidDataException) { rejected = true; }
                Check(rejected, "Malformed per-key range JSON rejected");
            }
            File.WriteAllText(file, preserved);
            var capture = new PressureRangeCapture(14, 0, 385, 0);
            capture.Feed(9, 650); Check(!capture.Pressing && !capture.Completed, "Other keys do not interrupt or contaminate capture");
            capture.Feed(14, 100); capture.Feed(14, 800);
            Check(capture.Pressing && !capture.Completed && capture.Maximum == 800, "Hold accumulates maximum without premature save");
            capture.Feed(14, 0); Check(capture.Completed && capture.Minimum == 0 && capture.Maximum == 800, "Single release completes measurement");
            capture.Feed(14, 1000); Check(capture.Maximum == 800, "Completed result cannot drift with later input");
            var quick = new PressureRangeCapture(14, 0, 385, null); quick.Feed(14, 300); quick.Feed(14, 0);
            Check(quick.Completed && quick.Maximum == 300, "One quick press needs no sample-count checkbox or second cycle");
            var held = new PressureRangeCapture(14, 0, 385, 200); held.Feed(14, 0);
            Check(!held.Completed && !held.Pressing, "Releasing an already held key alone does not capture a new press");
            held.Feed(14, 500); held.Feed(14, 0); Check(held.Completed && held.Maximum == 500, "Next full press calibrates an initially held key");
            var offset = new PressureRangeCapture(14, 10, 385, 10); offset.Feed(14, 390); offset.Feed(14, 10);
            Check(offset.Completed && offset.Minimum == 10 && offset.Maximum == 390, "Nonzero released value is supported");
            var repair = new PressureRangeCapture(14, 0, 385, 100); repair.Feed(14, 100); repair.Feed(14, 800); repair.Feed(14, 100);
            Check(repair.Completed && repair.Minimum == 100 && repair.Maximum == 800, "Calibration repairs an incorrect zero minimum from the observed baseline");
            var lower = new PressureRangeCapture(14, 100, 385, 0); lower.Feed(14, 500); lower.Feed(14, 0);
            Check(lower.Completed && lower.Minimum == 0, "Calibration also repairs a minimum set above the actual rest value");
            var noise = new PressureRangeCapture(14, 0, 385, 0); noise.Feed(14, 1); noise.Feed(14, 0);
            Check(!noise.Completed, "A raw-unit idle fluctuation is not a calibration");
            Console.WriteLine("PASS: " + checks + " per-key pressure/shared-scale checks; no windows or hardware."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
