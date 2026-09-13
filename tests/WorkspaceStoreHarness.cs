using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using Tk75.App;
using Tk75.Diagnostics;

public static class WorkspaceStoreHarness
{
    static int assertions;
    static void Check(bool valid, string message) { assertions++; if (!valid) throw new Exception("FAIL: " + message); }
    static void Reject(Action action, string message)
    {
        bool rejected = false;
        try { action(); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, message);
    }
    static string Replace(string json, string oldValue, string newValue)
    { if (!json.Contains(oldValue)) throw new Exception("Fixture replacement missing: " + oldValue); return json.Replace(oldValue, newValue); }
    static void Write(string path, string text) { File.WriteAllText(path, text, new UTF8Encoding(false)); }
    public static int Main(string[] args)
    {
        try { Run(args[0]); Console.WriteLine("PASS: " + assertions + " assertions; strict calibration/focus JSON, safe paths, atomic replacement and physical identity; temporary files only; no hardware calls."); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    static void Run(string root)
    {
        WorkspaceStore store = new WorkspaceStore(Path.Combine(root, "store"));
        string identity = new string('a', 64), fingerprint = new string('b', 64);
        CalibrationDocument document = new CalibrationDocument { DeviceIdentity = identity, ProtocolFingerprint = fingerprint };
        document.Entries.Add(new CalibrationEntry { KeyIndex = 14, Rest = 10, Bottom = 310, UsableMin = 0, UsableMax = 400, MeasuredTravel = 3.5 });
        store.SaveCalibration(document);
        string calibrationPath = store.CalibrationPath(identity), valid = File.ReadAllText(calibrationPath);
        Check(!valid.Contains("SensorOffset"), "derived offset is not a separately persisted calibration field");
        CalibrationDocument loaded = store.LoadCalibration(identity, fingerprint);
        Check(loaded.SchemaVersion == 1 && loaded.Entries.Count == 1 && loaded.Entries[0].Rest == 10 && loaded.Entries[0].Bottom == 310, "calibration required fields roundtrip");
        Check(loaded.Entries[0].UsableMin == 0 && loaded.Entries[0].UsableMax == 400 && loaded.Entries[0].MeasuredTravel == 3.5 && loaded.Entries[0].SensorOffset == 10, "optional calibration fields and derived offset roundtrip");
        loaded.Entries[0].Bottom = 320; store.SaveCalibration(loaded);
        Check(store.LoadCalibration(identity, fingerprint).Entries[0].Bottom == 320, "atomic calibration update succeeds");
        Check(Directory.GetFiles(store.Root, "*.tmp").Length == 0, "no orphan temporary file after atomic update");
        string preserved = File.ReadAllText(calibrationPath);
        loaded.Entries[0].Rest = double.NaN;
        Reject(delegate { store.SaveCalibration(loaded); }, "invalid in-memory calibration rejected");
        Check(File.ReadAllText(calibrationPath) == preserved, "invalid save preserves previous calibration");
        Reject(delegate { store.SaveCalibration(null); }, "null calibration is a validation error");
        string missingIdentity = new string('d', 64);
        Check(store.LoadCalibration(missingIdentity, fingerprint).Entries.Count == 0 && !File.Exists(store.CalibrationPath(missingIdentity)), "missing calibration is explicit empty state, not an invented calibration file");
        foreach (string badHash in new[] { "", "../outside", new string('g', 64), new string('a', 63), (string)null })
        {
            Reject(delegate { store.KeyMapPath(badHash); }, "key-map path rejects malformed fingerprint");
            Reject(delegate { store.CalibrationPath(badHash); }, "calibration path rejects malformed identity");
            Reject(delegate { store.LoadCalibration(identity, badHash); }, "even missing files require a valid expected fingerprint");
        }
        Check(store.KeyMapPath(fingerprint.ToUpperInvariant()) == store.KeyMapPath(fingerprint), "hex filename normalization is canonical");
        CollectionInfo device = new CollectionInfo { serial = "synthetic-serial", devicePath = "synthetic-path-A" };
        string serialIdentity = WorkspaceStore.Identity(device, fingerprint);
        device.devicePath = "synthetic-path-B";
        Check(WorkspaceStore.Identity(device, fingerprint) == serialIdentity, "serial identity survives path change");
        device.serial = null; string pathIdentity = WorkspaceStore.Identity(device, fingerprint); device.devicePath = "synthetic-path-C";
        Check(WorkspaceStore.Identity(device, fingerprint) != pathIdentity, "no-serial calibration requires new identity on changed Windows path");
        device.devicePath = null;
        Reject(delegate { WorkspaceStore.Identity(device, fingerprint); }, "missing serial and path cannot collapse physical identities");

        string entry = new JavaScriptSerializer().Serialize(document.Entries[0]);
        List<string> badCalibration = new List<string> {
            "", "{}", "[]", "{broken",
            Replace(valid, "\"SchemaVersion\":1,", ""),
            Replace(valid, "\"SchemaVersion\":1", "\"SchemaVersion\":2"),
            Replace(valid, "\"SchemaVersion\":1", "\"SchemaVersion\":\"1\""),
            Replace(valid, "\"SchemaVersion\":1", "\"SchemaVersion\":1.5"),
            Replace(valid, "\"SchemaVersion\":1", "\"SchemaVersion\":1.00000000000000000000000001"),
            Replace(valid, "\"SchemaVersion\":1", "\"SchemaVersion\":1,\"SchemaVersion\":1"),
            Replace(valid, "\"SchemaVersion\":1", "\"SchemaVersion\":1,\"Unknown\":true"),
            Replace(valid, "\"SchemaVersion\":1", "\"SchemaVersion\":1,\"__type\":\"CalibrationDocument:#Tk75.App\""),
            Replace(valid, "\"DeviceIdentity\":\"" + identity + "\",", ""),
            Replace(valid, fingerprint, new string('c', 64)),
            Replace(valid, "[" + entry + "]", "null"),
            Replace(valid, "[" + entry + "]", "{}"),
            Replace(valid, "[" + entry + "]", "[null]"),
            Replace(valid, "[" + entry + "]", "[" + entry + "," + entry + "]"),
            Replace(valid, "\"KeyIndex\":14,", ""),
            Replace(valid, "\"KeyIndex\":14", "\"KeyIndex\":14.5"),
            Replace(valid, "\"KeyIndex\":14", "\"KeyIndex\":true"),
            Replace(valid, "\"KeyIndex\":14", "\"KeyIndex\":256"),
            Replace(valid, "\"KeyIndex\":14", "\"KeyIndex\":14,\"KeyIndex\":14"),
            Replace(valid, "\"KeyIndex\":14", "\"KeyIndex\":14,\"Unknown\":1"),
            Replace(valid, "\"Rest\":10,", ""),
            Replace(valid, "\"Bottom\":310,", ""),
            Replace(valid, "\"Rest\":10", "\"Rest\":\"10\""),
            Replace(valid, "\"Rest\":10", "\"Rest\":null"),
            Replace(valid, "\"Rest\":10", "\"Rest\":1e400"),
            Replace(valid, "\"Bottom\":310", "\"Bottom\":10"),
            Replace(valid, "\"MeasuredTravel\":3.5", "\"MeasuredTravel\":0"),
            Replace(valid, "\"UsableMin\":0", "\"UsableMin\":null"),
            Replace(valid, "\"KeyIndex\":14", "\"KeyIndex\":14,\"SensorOffset\":11")
        };
        string[] excessiveEntries = new string[257]; for (int i = 0; i < excessiveEntries.Length; i++) excessiveEntries[i] = entry;
        badCalibration.Add(Replace(valid, "[" + entry + "]", "[" + string.Join(",", excessiveEntries) + "]"));
        foreach (string bad in badCalibration)
        {
            Write(calibrationPath, bad);
            Reject(delegate { store.LoadCalibration(identity, fingerprint); }, "malformed/missing/unknown/duplicate calibration data rejected");
        }
        Write(calibrationPath, Replace(valid, "\"KeyIndex\":14", "\"KeyIndex\":14,\"SensorOffset\":10"));
        Check(store.LoadCalibration(identity, fingerprint).Entries[0].Rest == 10, "consistent legacy derived SensorOffset accepted");
        Write(calibrationPath, new string(' ', 1048577));
        Reject(delegate { store.LoadCalibration(identity, fingerprint); }, "oversized calibration rejected before parsing");
        store.SaveCalibration(document);

        Write(calibrationPath, Replace(valid, ",\"KeyRanges\":[]", ""));
        Check(store.LoadCalibration(identity, fingerprint).KeyRanges.Count == 0, "older calibration files without per-key overrides remain readable");
        var rangeDocument = new CalibrationDocument { DeviceIdentity = identity, ProtocolFingerprint = fingerprint,
            GlobalMinimum = 20, GlobalMaximum = 700, ScaleMaximum = 900 };
        rangeDocument.KeyRanges.Add(new KeyPressureRangeEntry { KeyIndex = 14, Minimum = 10, Maximum = 800 });
        store.SaveCalibration(rangeDocument);
        string validRanges = File.ReadAllText(calibrationPath);
        string rangeEntry = new JavaScriptSerializer().Serialize(rangeDocument.KeyRanges[0]);
        CalibrationDocument readRanges = store.LoadCalibration(identity, fingerprint);
        Check(readRanges.GlobalMinimum == 20 && readRanges.GlobalMaximum == 700 && readRanges.KeyRanges.Count == 1 &&
            readRanges.KeyRanges[0].KeyIndex == 14 && readRanges.KeyRanges[0].Minimum == 10 && readRanges.KeyRanges[0].Maximum == 800 && readRanges.ScaleMaximum == 900,
            "per-key override, untouched shared fallback and visual scale roundtrip together");
        foreach (string bad in new[] {
            Replace(validRanges, "[" + rangeEntry + "]", "null"), Replace(validRanges, "[" + rangeEntry + "]", "{}"),
            Replace(validRanges, "[" + rangeEntry + "]", "[null]"), Replace(validRanges, "[" + rangeEntry + "]", "[" + rangeEntry + "," + rangeEntry + "]"),
            Replace(validRanges, "\"KeyRanges\":", "\"KeyRanges\":[],\"KeyRanges\":"),
            Replace(validRanges, "\"KeyIndex\":14,", ""), Replace(validRanges, "\"KeyIndex\":14", "\"KeyIndex\":14.5"),
            Replace(validRanges, "\"KeyIndex\":14", "\"KeyIndex\":256"), Replace(validRanges, "\"KeyIndex\":14", "\"KeyIndex\":-1"),
            Replace(validRanges, "\"Minimum\":10,", ""), Replace(validRanges, "\"Maximum\":800", "\"Maximum\":\"800\""),
            Replace(validRanges, "\"Minimum\":10", "\"Minimum\":null"), Replace(validRanges, "\"Minimum\":10", "\"Minimum\":-1"),
            Replace(validRanges, "\"Maximum\":800", "\"Maximum\":10"), Replace(validRanges, "\"Maximum\":800", "\"Maximum\":65536"),
            Replace(validRanges, "\"Maximum\":800", "\"Maximum\":1e400"), Replace(validRanges, "\"Maximum\":800", "\"Maximum\":800,\"Unknown\":0"),
            Replace(validRanges, "\"Maximum\":800", "\"Maximum\":800,\"Maximum\":800"),
            Replace(validRanges, "\"ScaleMaximum\":900", "\"ScaleMaximum\":799"),
            Replace(validRanges, "\"GlobalMaximum\":700", "\"GlobalMaximum\":null"),
            Replace(validRanges, "\"GlobalMaximum\":700", "\"GlobalMaximum\":\"700\""),
            Replace(validRanges, "\"GlobalMaximum\":700", "\"GlobalMaximum\":700,\"GlobalMaximum\":700") })
        {
            Write(calibrationPath, bad);
            Reject(delegate { store.LoadCalibration(identity, fingerprint); }, "malformed per-key override or shared fallback is rejected");
        }
        string[] excessiveRanges = new string[257]; for (int i = 0; i < excessiveRanges.Length; i++) excessiveRanges[i] = rangeEntry;
        Write(calibrationPath, Replace(validRanges, "[" + rangeEntry + "]", "[" + string.Join(",", excessiveRanges) + "]"));
        Reject(delegate { store.LoadCalibration(identity, fingerprint); }, "more than 256 per-key ranges are rejected before decoding");
        store.SaveCalibration(rangeDocument); rangeDocument.KeyRanges[0].Maximum = Double.NaN;
        Reject(delegate { store.SaveCalibration(rangeDocument); }, "invalid in-memory override cannot replace valid calibration");
        Check(File.ReadAllText(calibrationPath) == validRanges, "invalid override preserves the exact saved document");
        document.ScaleMaximum = 500; store.SaveCalibration(document);
        Check(store.LoadCalibration(identity, fingerprint).ScaleMaximum == 500, "visual scale can be saved without a shared calibration override");
        document.ScaleMaximum = null; store.SaveCalibration(document);

        FocusSettings focus = new FocusSettings { Enabled = true, DefaultProfileFile = "default.json" };
        focus.Rules.Add(new FocusRule { Executable = "Game.exe", ProfileFile = "default.json" });
        focus.Rules.Add(new FocusRule { Executable = @"C:\Games\Other.exe", ProfileFile = "other.json" });
        store.SaveFocus(focus); string focusPath = Path.Combine(store.Root, "focus.json"), validFocus = File.ReadAllText(focusPath);
        FocusSettings readFocus = store.LoadFocus();
        Check(readFocus.SchemaVersion == 1 && readFocus.Enabled && readFocus.Rules.Count == 2 && readFocus.DefaultProfileFile == "default.json", "focus settings roundtrip");
        readFocus.Enabled = false; store.SaveFocus(readFocus);
        Check(!store.LoadFocus().Enabled, "atomic focus update succeeds");
        preserved = File.ReadAllText(focusPath); readFocus.Rules.Add(new FocusRule { Executable = "GAME.EXE", ProfileFile = "other.json" });
        Reject(delegate { store.SaveFocus(readFocus); }, "case-insensitive duplicate executable rejected");
        Check(File.ReadAllText(focusPath) == preserved, "invalid focus save preserves previous file");
        List<string> badFocus = new List<string> {
            "", "{}", "[]", "{broken",
            Replace(validFocus, "\"SchemaVersion\":1,", ""),
            Replace(validFocus, "\"Enabled\":true,", ""),
            Replace(validFocus, "\"SchemaVersion\":1", "\"SchemaVersion\":2"),
            Replace(validFocus, "\"SchemaVersion\":1", "\"SchemaVersion\":1,\"Unknown\":0"),
            Replace(validFocus, "\"Enabled\":true", "\"Enabled\":true,\"Enabled\":false"),
            Replace(validFocus, "\"Enabled\":true", "\"Enabled\":1"),
            Replace(validFocus, "\"Enabled\":true", "\"Enabled\":\"true\""),
            Replace(validFocus, "\"Executable\":\"Game.exe\",", ""),
            Replace(validFocus, "\"Executable\":\"Game.exe\"", "\"Executable\":\"Game.exe\",\"Unknown\":true"),
            Replace(validFocus, "\"Executable\":\"Game.exe\"", "\"Executable\":null"),
            Replace(validFocus, "\"Executable\":\"Game.exe\"", "\"Executable\":\"..\\\\Game.exe\""),
            Replace(validFocus, "\"DefaultProfileFile\":\"default.json\"", "\"DefaultProfileFile\":\"\""),
            Replace(validFocus, "\"DefaultProfileFile\":\"default.json\"", "\"DefaultProfileFile\":false")
        };
        foreach (string bad in badFocus) { Write(focusPath, bad); Reject(delegate { store.LoadFocus(); }, "malformed/missing/unknown/duplicate focus data rejected"); }
        string rule = "{\"Executable\":\"Game.exe\",\"ProfileFile\":\"default.json\"}";
        string[] excessiveRules = new string[257]; for (int i = 0; i < excessiveRules.Length; i++) excessiveRules[i] = rule;
        Write(focusPath, "{\"SchemaVersion\":1,\"Enabled\":false,\"Rules\":[" + string.Join(",", excessiveRules) + "]}");
        Reject(delegate { store.LoadFocus(); }, "too many focus rules rejected");
        foreach (string badName in new[] { "../outside.json", @"..\outside.json", @"C:\outside.json", "outside:stream.json", "NUL.json", "CON.extra.json", "LPT1.json", "bad?.json", "", (string)null })
            Reject(delegate { store.RequireLocalProfile(badName); }, "profile filename cannot escape folder or open a Windows device");
        foreach (string badExecutable in new[] { "Game", "folder/Game.exe", @"folder\Game.exe", @"C:Game.exe", @"C:\Games\..\Game.exe", @"C:\Games:stream\Game.exe", "Game*.exe", "Game\n.exe", " Game.exe" })
        {
            FocusSettings invalidFocus = new FocusSettings(); invalidFocus.Rules.Add(new FocusRule { Executable = badExecutable, ProfileFile = "default.json" });
            Reject(delegate { store.ValidateFocus(invalidFocus); }, "focus executable requires an unambiguous name or absolute path");
        }
        Write(focusPath, new string(' ', 1048577)); Reject(delegate { store.LoadFocus(); }, "oversized focus file rejected");
        WorkspaceStore.WriteAtomic(Path.Combine(store.Root, "atomic.txt"), "first");
        WorkspaceStore.WriteAtomic(Path.Combine(store.Root, "atomic.txt"), "second");
        Check(File.ReadAllText(Path.Combine(store.Root, "atomic.txt")) == "second", "atomic replacement does not throw after success");
        Check(Directory.GetFiles(store.Root, "*.tmp").Length == 0, "atomic storage leaves no temporary files");
        store.Event(null); store.Event(new string('x', 10000));
        Check(new FileInfo(Path.Combine(store.Root, "events.log")).Length < 5000, "best-effort events accept null and bound individual messages");
    }
}
