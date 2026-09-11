using System;
using System.IO;
using Tk75.Diagnostics;

public static class Tk75InputEventHarness
{
    static int checks;
    static void Check(bool condition, string message)
    { checks++; if (!condition) throw new InvalidOperationException(message); }
    static byte[] Report(byte opcode, byte first, byte second)
    { byte[] report = new byte[32]; report[0] = 5; report[1] = opcode; report[2] = first; report[3] = second; return report; }
    static void Reject(byte[] report, string rawPart)
    {
        try { Tk75InputEvent.Parse(report); }
        catch (InvalidDataException ex)
        {
            Check(ex.Message.Contains("Raw report (") && ex.Message.Contains(rawPart), "Rejection retains raw report evidence");
            Check(ex.Message.Length < 300, "Raw report evidence remains bounded");
            return;
        }
        throw new InvalidOperationException("Malformed/unknown report was accepted");
    }
    static void Notification(byte[] report, Tk75InputEventKind kind)
    {
        Tk75InputEvent value = Tk75InputEvent.Parse(report);
        Check(value.Kind == kind, "Notification reaches the correct non-pressure branch");
        Check(value.Sample.KeyLabel == null && value.Sample.RawValue == 0, "Notification never fabricates a pressure sample");
    }
    public static string Run()
    {
        checks = 0;
        foreach (int raw in new[] { 0, 1, 385, 65535 })
        foreach (byte key in new byte[] { 0, 14, 255 })
        {
            byte[] report = Report(0x1b, (byte)raw, (byte)(raw >> 8)); report[4] = key;
            Tk75InputEvent value = Tk75InputEvent.Parse(report);
            Check(value.Kind == Tk75InputEventKind.Travel && value.Sample.RawValue == raw && value.Sample.KeyIndex == key, "Pressure sample survives dispatcher exactly");
        }
        for (byte opcode = 4; opcode <= 7; opcode++)
        {
            Notification(Report(opcode, 0, 0), Tk75InputEventKind.LightingChanged);
            Notification(Report(opcode, 255, 255), Tk75InputEventKind.LightingChanged);
        }
        Notification(Report(0x0f, 1, 0), Tk75InputEventKind.BusyBegin);
        Notification(Report(0x0f, 0, 0), Tk75InputEventKind.BusyEnd);
        Notification(Report(1, 255, 255), Tk75InputEventKind.StateInvalidated);
        Notification(Report(0x0d, 0, 0), Tk75InputEventKind.StateInvalidated);
        Notification(Report(0x0d, 0, 1), Tk75InputEventKind.StateInvalidated);
        foreach (byte opcode in new byte[] { 0, 2, 3, 8, 11, 12, 0x1c, 0xff })
            Reject(Report(opcode, 0, 0), "05" + opcode.ToString("X2"));
        Reject(Report(0x0f, 2, 0), "050F0200");
        Reject(Report(0x0f, 1, 1), "050F0101");
        Reject(Report(0x0d, 1, 0), "050D0100");
        Reject(Report(0x0d, 0, 2), "050D0002");
        foreach (byte opcode in new byte[] { 4, 7, 0x0f, 1, 0x0d })
        for (int offset = 4; offset < 32; offset++)
        {
            byte[] report = Report(opcode, 0, 0); report[offset] = 1;
            Reject(report, BitConverter.ToString(report).Replace("-", ""));
        }
        for (int offset = 5; offset < 32; offset++)
        {
            byte[] report = Report(0x1b, 1, 0); report[offset] = 1;
            Reject(report, BitConverter.ToString(report).Replace("-", ""));
        }
        byte[] wrongId = Report(0x1b, 1, 0); wrongId[0] = 0;
        Reject(wrongId, "001B0100");
        Reject(null, "<null>");
        Reject(new byte[0], "0 bytes");
        Reject(new byte[31], "31 bytes");
        Reject(new byte[33], "...");
        Reject(new byte[4096], "...");
        return "PASS: " + checks + " pure input-event assertions; no device/process access.";
    }
}
