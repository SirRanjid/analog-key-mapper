using System;
using System.Globalization;
using System.IO;
using System.Text;
using Tk75.Mapping;

namespace Tk75.Output
{
    // Private process protocol: ASCII envelope, invariant integer fields and
    // bounded UTF-8/base64 errors. No raw keyboard reports or local secrets.
    internal static class OutputWire
    {
        internal const string Version = "TK75OUT/1";
        internal const int MaximumCommand = 512, MaximumReply = 2048;
        internal static string ReadLine(TextReader input, int maximum)
        {
            var line = new StringBuilder();
            for (;;)
            {
                int value = input.Read();
                if (value < 0) return line.Length == 0 ? null : line.ToString();
                if (value == '\n')
                { if (line.Length != 0 && line[line.Length - 1] == '\r') line.Length--; return line.ToString(); }
                if (line.Length >= maximum) throw new InvalidDataException("Output-Protokollzeile ist zu lang.");
                line.Append((char)value);
            }
        }
        internal static string Error(string message)
        {
            message = (message ?? "Unbekannter Outputfehler.").Replace('\r', ' ').Replace('\n', ' ');
            if (message.Length > 256) message = message.Substring(0, 256);
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(message));
        }
        internal static string Packet(XInputPacket value)
        {
            return String.Format(CultureInfo.InvariantCulture, "{0} {1} {2} {3} {4} {5} {6}",
                value.Buttons, value.LeftTrigger, value.RightTrigger, value.LeftX, value.LeftY, value.RightX, value.RightY);
        }
        internal static bool Equal(XInputPacket left, XInputPacket right)
        {
            return left.Buttons == right.Buttons && left.LeftTrigger == right.LeftTrigger && left.RightTrigger == right.RightTrigger &&
                left.LeftX == right.LeftX && left.LeftY == right.LeftY && left.RightX == right.RightX && left.RightY == right.RightY;
        }
        internal static int Integer(string text, int minimum, int maximum)
        {
            int value;
            if (!Int32.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value) || value < minimum || value > maximum)
                throw new InvalidDataException("Ungueltiger Output-Paketwert.");
            return value;
        }
        internal static ControllerFrame Frame(string[] fields)
        {
            if (fields.Length != 10) throw new InvalidDataException("Ungueltige Output-Paketlaenge.");
            int buttons = Integer(fields[3], 0, 65535);
            if ((buttons & ~0xF3FF) != 0) throw new InvalidDataException("Reservierte Controllerbuttons sind unzulaessig.");
            return new ControllerFrame {
                Buttons = (ushort)buttons,
                LeftTrigger = Integer(fields[4], 0, 255) / 255.0,
                RightTrigger = Integer(fields[5], 0, 255) / 255.0,
                LeftX = Axis(Integer(fields[6], -32768, 32767)), LeftY = Axis(Integer(fields[7], -32768, 32767)),
                RightX = Axis(Integer(fields[8], -32768, 32767)), RightY = Axis(Integer(fields[9], -32768, 32767))
            };
        }
        static double Axis(int value) { return value / (value < 0 ? 32768.0 : 32767.0); }
    }

    public static class OutputHost
    {
        // Dispatch this before any WinForms initialization. No native client is
        // loaded until the isolated process receives an explicit CONNECT command.
        public static int Run(string[] args)
        {
            if (args == null || args.Length != 1 || args[0] != "--output-host") return 64;
            return Run(Console.In, Console.Out, delegate { return new ViGEmOutput(); });
        }

        // Fully fake/offline host tests can supply both streams and the backend.
        public static int Run(TextReader input, TextWriter output, Func<IControllerOutput> factory)
        {
            if (input == null || output == null || factory == null) throw new ArgumentNullException("Host input/output/factory");
            IControllerOutput backend = null;
            long previousId = 0;
            try
            {
                for (;;)
                {
                    long id = 0;
                    try
                    {
                        string line = OutputWire.ReadLine(input, OutputWire.MaximumCommand);
                        if (line == null) return 0;
                        string[] fields = line.Split(' ');
                        if (fields.Length < 3 || fields[0] != OutputWire.Version ||
                            !Int64.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out id) || id <= previousId)
                            throw new InvalidDataException("Ungueltige Output-Protokollversion oder Befehlsnummer.");
                        previousId = id;
                        bool quit = false;
                        switch (fields[2])
                        {
                            case "CONNECT":
                                RequireLength(fields, 3);
                                if (backend != null) throw new InvalidOperationException("Output ist bereits verbunden.");
                                backend = factory();
                                if (backend == null) throw new InvalidOperationException("Output-Backend fehlt.");
                                backend.Connect();
                                if (!backend.IsConnected) throw new InvalidOperationException("Output-Backend hat keine Verbindung bestaetigt.");
                                backend.Neutral();
                                break;
                            case "FRAME":
                                ControllerFrame frame = OutputWire.Frame(fields);
                                RequireConnected(backend);
                                backend.Submit(frame);
                                if (!backend.IsConnected) throw new InvalidOperationException("Output-Verbindung ging verloren.");
                                break;
                            case "NEUTRAL":
                                RequireLength(fields, 3); RequireConnected(backend); backend.Neutral();
                                break;
                            case "QUIT":
                                RequireLength(fields, 3); Release(ref backend); quit = true;
                                break;
                            default: throw new InvalidDataException("Unbekannter Outputbefehl.");
                        }
                        output.WriteLine(OutputWire.Version + " " + id.ToString(CultureInfo.InvariantCulture) + " ACK");
                        output.Flush();
                        if (quit) return 0;
                    }
                    catch (Exception ex)
                    {
                        try { output.WriteLine(OutputWire.Version + " " + id.ToString(CultureInfo.InvariantCulture) + " ERROR " + OutputWire.Error(ex.Message)); output.Flush(); }
                        catch (Exception) { }
                        return 1;
                    }
                }
            }
            finally { try { Release(ref backend); } catch (Exception) { } }
        }
        static void RequireLength(string[] fields, int expected)
        { if (fields.Length != expected) throw new InvalidDataException("Ungueltige Befehlslaenge."); }
        static void RequireConnected(IControllerOutput backend)
        { if (backend == null || !backend.IsConnected) throw new InvalidOperationException("Output ist nicht verbunden."); }
        static void Release(ref IControllerOutput backend)
        {
            var previous = backend; backend = null;
            if (previous == null) return;
            Exception failure = null;
            try { previous.Neutral(); } catch (Exception ex) { failure = ex; }
            try { previous.Dispose(); } catch (Exception ex) { if (failure == null) failure = ex; }
            if (failure != null) throw failure;
        }
    }
}
