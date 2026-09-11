using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using Tk75.Output;

// Test executable only. This code never constructs ViGEmOutput or OutputHost.
// Every "controller" response below is synthetic protocol behavior.
public static class FakeOutputHost
{
    public static int Main(string[] args)
    {
        string mode = args.Length == 0 ? "success" : args[0];
        if (mode == "owner-exit")
        {
            var owner = new IsolatedOutput(Assembly.GetExecutingAssembly().Location, "success", 2000, 200);
            owner.Connect();
            Console.WriteLine(owner.HostProcessId.Value.ToString(CultureInfo.InvariantCulture)); Console.Out.Flush();
            // Deliberately omit Dispose: the real wrapper's Windows job must kill
            // its own helper when this owner process exits unexpectedly.
            Environment.Exit(0);
        }
        if (mode == "early-eof") return 31;
        for (;;)
        {
            string line = Console.ReadLine(); if (line == null) return 0;
            string[] fields = line.Split(' ');
            if (fields.Length < 3) return 32;
            long id = Int64.Parse(fields[1], CultureInfo.InvariantCulture);
            string command = fields[2];
            if ((mode == "stall-connect" && command == "CONNECT") || (mode == "stall-frame" && command == "FRAME") ||
                (mode == "stall-neutral" && command == "NEUTRAL") || (mode == "stall-quit" && command == "QUIT"))
                using (var forever = new ManualResetEvent(false)) forever.WaitOne();
            if (command == "FRAME")
            {
                if (mode == "route-a" || mode == "route-b")
                {
                    // Deliberately different golden routes identify crossed pipes
                    // or packets; neither child merely acknowledges arbitrary data.
                    bool valid = fields.Length == 10 && (mode == "route-a"
                        ? fields[3] == "4096" && fields[4] == "51" && fields[5] == "0" && fields[6] == "32767" && fields[7] == "0" && fields[8] == "0" && fields[9] == "0"
                        : fields[3] == "8192" && fields[4] == "0" && fields[5] == "204" && fields[6] == "0" && fields[7] == "0" && fields[8] == "0" && (fields[9] == "-32768" || fields[9] == "32767"));
                    if (!valid)
                    {
                        Console.WriteLine("TK75OUT/1 " + id + " ERROR " + Convert.ToBase64String(Encoding.UTF8.GetBytes("Frame reached the wrong synthetic route")));
                        Console.Out.Flush(); continue;
                    }
                }
                if (mode == "crash") return 33;
                if (mode == "malformed") { Console.WriteLine("not an acknowledgement"); Console.Out.Flush(); continue; }
                if (mode == "oversized") { Console.WriteLine(new string('a', 5000)); Console.Out.Flush(); continue; }
                if (mode == "wrong-id") id++;
                if (mode == "error")
                { Console.WriteLine("TK75OUT/1 " + id + " ERROR " + Convert.ToBase64String(Encoding.UTF8.GetBytes("Synthetic backend failure"))); Console.Out.Flush(); continue; }
                if (mode == "stderr-flood") { Console.Error.Write(new string('e', 65536)); Console.Error.Flush(); }
            }
            Console.WriteLine("TK75OUT/1 " + id.ToString(CultureInfo.InvariantCulture) + " ACK"); Console.Out.Flush();
            if (command == "QUIT") return 0;
        }
    }
}
