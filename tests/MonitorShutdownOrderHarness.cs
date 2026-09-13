using System;
using System.ComponentModel;
using Tk75.Diagnostics;

public static class MonitorShutdownOrderHarness
{
    static int checks;
    static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    public static int Run()
    {
        checks = 0;
        int calls = 0; uint actualLevel = 0, actualFlags = UInt32.MaxValue;
        MonitorShutdownOrder.Configure(delegate(uint level, uint flags) { calls++; actualLevel = level; actualFlags = flags; return true; });
        Check(calls == 1, "A helper establishes shutdown ordering once before starting its work.");
        Check(actualLevel >= 0x100 && actualLevel <= 0x1FF && actualLevel < 0x280,
            "The helper stays in the documented application-last range, after the default parent.");
        Check(actualFlags == 0, "Registration does not force termination, suppress retry or change machine-wide settings.");
        bool continued = false, refused = false;
        try { MonitorShutdownOrder.Configure(delegate { return false; }); continued = true; }
        catch (InvalidOperationException) { refused = true; }
        Check(refused && !continued, "A refused registration prevents subsequent hardware startup.");
        var failure = new Win32Exception(5, "Synthetic policy failure"); Exception observed = null;
        try { MonitorShutdownOrder.Configure(delegate { throw failure; }); }
        catch (Exception error) { observed = error; }
        Check(Object.ReferenceEquals(failure, observed), "Native registration errors reach the existing stream-host error and cleanup path intact.");
        bool missing = false; try { MonitorShutdownOrder.Configure(null); } catch (ArgumentNullException) { missing = true; }
        Check(missing, "An absent registration function cannot silently allow startup.");
        return checks;
    }
}
