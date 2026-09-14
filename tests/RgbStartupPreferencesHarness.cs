using System;
using System.IO;
using System.Text;
using Tk75.App;

public static class RgbStartupPreferencesHarness
{
    static int checks;
    static void Check(bool value, string reason) { checks++; if (!value) throw new Exception(reason); }
    static void Rejects(string directory, string reason)
    {
        bool rejected = false;
        try { RgbStartupPreferences.Load(directory); }
        catch (InvalidDataException) { rejected = true; }
        catch (DecoderFallbackException) { rejected = true; }
        Check(rejected, reason);
    }
    public static void Run(string testsRoot)
    {
        checks = 0;
        string directory = Path.Combine(Path.GetFullPath(testsRoot), "synthetic-rgb-preferences-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, RgbStartupPreferences.FileName);
        try
        {
            Check(!RgbStartupPreferences.Load(directory), "Missing consent defaults to asking");
            Check(!File.Exists(path), "Reading missing consent does not write a setting");
            RgbStartupPreferences.Save(directory, true);
            Check(RgbStartupPreferences.Load(directory), "Explicit consent survives reload");
            RgbStartupPreferences.Save(directory, false);
            Check(!RgbStartupPreferences.Load(directory), "Revoking consent survives reload");
            Check(Directory.GetFiles(directory).Length == 1, "Atomic save leaves no temporary files");
            foreach (string invalid in new string[] { "", "TRUE", "1", "yes", "true false", "tru", new string(' ', 33) + "true" })
            {
                File.WriteAllText(path, invalid, new UTF8Encoding(false));
                Rejects(directory, "Malformed or oversized consent cannot silently authorize repair");
            }
            File.WriteAllBytes(path, new byte[] { 0xff, 0xfe, 0x74, 0x00 });
            Rejects(directory, "Invalid UTF8 consent is rejected");
            RgbStartupPreferences.Save(directory, true);
            Check(RgbStartupPreferences.Load(directory), "Explicit save replaces corrupt preferences safely");
            Console.WriteLine("PASS: " + checks + " startup RGB preference checks (offline only).");
        }
        finally
        {
            // Only the one file and newly created empty test directory are removed.
            if (File.Exists(path)) File.Delete(path);
            Directory.Delete(directory, false);
        }
    }
}
