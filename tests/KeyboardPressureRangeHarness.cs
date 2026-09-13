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
            var changed = migrated.Apply(original, 10, 600, 385);
            var range = new KeyboardPressureRange(changed);
            Check(range.ScaleMaximum == 600 && range.Resolve().Values.All(c => c.Rest == 10 && c.Bottom == 600), "Higher measurement expands every key and the scale");
            Check(changed.Entries.Count == 2 && original.Entries[0].Bottom == 390, "Original measurements remain available");
            Check(range.Depth(10) == 0 && range.Depth(305) == .5 && range.Depth(600) == 1 && range.Depth(900) == 1, "UI and controller normalization use shared bounds");
            var store = new WorkspaceStore(args[0]); store.SaveCalibration(changed);
            var loaded = store.LoadCalibration(original.DeviceIdentity, original.ProtocolFingerprint);
            Check(loaded.GlobalMinimum == 10 && loaded.GlobalMaximum == 600 && loaded.ScaleMaximum == 600, "Shared range survives restart");
            string file = store.CalibrationPath(original.DeviceIdentity), preserved = File.ReadAllText(file);
            foreach (var invalid in new[] {
                migrated.Apply(original, 100, 100, 385), migrated.Apply(original, -1, 385, 385),
                migrated.Apply(original, 0, 65536, 65536), migrated.Apply(original, Double.NaN, 385, 385) })
            {
                bool rejected = false; try { store.SaveCalibration(invalid); } catch (InvalidDataException) { rejected = true; }
                Check(rejected && File.ReadAllText(file) == preserved, "Invalid range leaves saved values intact");
            }
            foreach (string replacement in new[] { "\"GlobalMaximum\":null", "\"GlobalMaximum\":\"600\"", "\"GlobalMaximum\":600,\"GlobalMaximum\":700", "\"GlobalMaximum\":1e400" })
            {
                File.WriteAllText(file, preserved.Replace("\"GlobalMaximum\":600", replacement));
                bool rejected = false; try { store.LoadCalibration(original.DeviceIdentity, original.ProtocolFingerprint); } catch (InvalidDataException) { rejected = true; }
                Check(rejected, "Malformed shared range JSON rejected");
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
            Console.WriteLine("PASS: " + checks + " shared pressure-range checks; no windows or hardware."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
