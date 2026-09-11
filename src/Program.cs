using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace Tk75.Diagnostics
{
    internal static class Program
    {
        static volatile bool stop;
        static volatile bool cancelled;
        static JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = 16777216 };
        static readonly object logLock = new object();
        static int Hex(string value) { return int.Parse(value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value.Substring(2) : value, NumberStyles.HexNumber); }
        static Dictionary<string, string> Options(string[] args)
        {
            Dictionary<string, string> opts = new Dictionary<string, string>();
            for (int i = 1; i < args.Length; i += 2)
            {
                if (!args[i].StartsWith("--") || i + 1 == args.Length) throw new ArgumentException("Expected --option value");
                if (!new[] { "--out", "--vid", "--pid", "--seconds", "--path-contains", "--max-reports", "--keymap", "--label" }.Contains(args[i])) throw new ArgumentException("Unknown option: " + args[i]);
                opts.Add(args[i], args[i + 1]);
            }
            return opts;
        }
        static void Log(StreamWriter writer, object item)
        {
            lock (logLock)
            {
                try { writer.WriteLine(serializer.Serialize(item)); writer.Flush(); }
                catch { stop = true; throw; }
            }
        }
        static void ReportError(StreamWriter writer, object item, string message)
        {
            // A failed output file or stderr must never escape a worker's catch.
            try { Log(writer, item); }
            catch (Exception ex) { ErrorLine("Protokoll konnte nicht geschrieben werden: " + ex.Message); }
            ErrorLine(message);
        }
        static void ErrorLine(string message)
        { try { Console.Error.WriteLine(message); } catch (Exception) { } }
        static string Utc() { return DateTime.UtcNow.ToString("o"); }
        static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            try
            {
                if (args.Length == 0 || args[0] == "help" || args[0] == "--help")
                {
                    Console.WriteLine("TK75 HID-Diagnose | passiv, keine Hardware-Schreibbefehle, kein XInput\n" +
                        "  inventory [--vid HEX --pid HEX] [--out inventory.json]\n" +
                        "  capture --vid HEX --pid HEX --out reports.jsonl [--seconds 30] [--path-contains mi_01] [--max-reports 100000]\n" +
                        "  live --vid HEX --pid HEX --out travel.jsonl [--seconds 300] [--keymap keys.json]\n" +
                        "  learn --vid HEX --pid HEX --out learn.jsonl --keymap keys.json --label W [--seconds 300]\n" +
                        "Capture liest nur herstellerdefinierte Input-Collections (UsagePage >= FF00). Ctrl+C beendet.\n" +
                        "Rohprotokolle werden nie ueberschrieben; learn aktualisiert den Tastenplan. Rohwerte sind keine bestaetigten Tastendistanzen.\n" +
                        "Auch live/learn unterstuetzen --path-contains zur eindeutigen Geraeteauswahl.");
                    return 0;
                }
                if (args[0] != "inventory" && args[0] != "capture" && args[0] != "live" && args[0] != "learn") throw new ArgumentException("Unknown command");
                bool learning = args[0] == "learn";
                bool live = args[0] == "live" || learning;
                Dictionary<string, string> opts = Options(args);
                if (args[0] != "inventory" && (!opts.ContainsKey("--vid") || !opts.ContainsKey("--pid") || !opts.ContainsKey("--out"))) throw new ArgumentException("Capture/live requires explicit --vid, --pid and --out");
                if (learning && (!opts.ContainsKey("--keymap") || !opts.ContainsKey("--label"))) throw new ArgumentException("Learn requires --keymap and --label");
                List<CollectionInfo> devices = HidInventory.Enumerate();
                if (opts.ContainsKey("--vid")) devices = devices.Where(d => d.vendorId == Hex(opts["--vid"])).ToList();
                if (opts.ContainsKey("--pid")) devices = devices.Where(d => d.productId == Hex(opts["--pid"])).ToList();
                if (opts.ContainsKey("--path-contains")) devices = devices.Where(d => d.devicePath.IndexOf(opts["--path-contains"], StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (args[0] == "inventory")
                {
                    object document = new { schemaVersion = 1, utc = Utc(), mode = "metadata-only", descriptorKind = "Windows parsed capabilities; not original USB report descriptor", devices = devices };
                    if (opts.ContainsKey("--out"))
                    {
                        using (StreamWriter writer = new StreamWriter(new FileStream(opts["--out"], FileMode.CreateNew, FileAccess.Write), new UTF8Encoding(false))) writer.WriteLine(serializer.Serialize(document));
                        foreach (CollectionInfo d in devices) Console.WriteLine("{0:X4}:{1:X4} {2} usage={3:X4}/{4:X4} IN={5} OUT={6} FEATURE={7} {8}\n  {9}", d.vendorId, d.productId, d.product, d.usagePage, d.usage, d.inputReportLength, d.outputReportLength, d.featureReportLength, d.error, d.devicePath);
                    }
                    else Console.WriteLine(serializer.Serialize(document));
                    return devices.Count == 0 ? 2 : 0;
                }
                int seconds = opts.ContainsKey("--seconds") ? int.Parse(opts["--seconds"], CultureInfo.InvariantCulture) : (live ? 300 : 30);
                int maxReports = opts.ContainsKey("--max-reports") ? int.Parse(opts["--max-reports"], CultureInfo.InvariantCulture) : 100000;
                if (seconds < 1 || seconds > 3600 || maxReports < 1 || maxReports > 1000000) throw new ArgumentException("seconds: 1..3600; max-reports: 1..1000000");
                devices = devices.Where(TravelProtocols.IsVendorInput).ToList();
                ITravelDecoder decoder = null;
                KeyMapDocument keymap = null;
                KeyLearner learner = learning ? new KeyLearner() : null;
                if (live)
                {
                    devices = devices.Where(d => TravelProtocols.Find(d) != null).ToList();
                    if (devices.Count != 1) throw new InvalidOperationException("Genau eine passende Input-Collection erforderlich. Bei mehreren Geraeten --path-contains verwenden; unbekannte Formate mit capture untersuchen.");
                    decoder = TravelProtocols.Find(devices[0]);
                    string fingerprint = ProtocolFingerprint.Calculate(devices[0], decoder.Id);
                    if (opts.ContainsKey("--keymap") && !File.Exists(opts["--keymap"]) && !learning)
                        throw new FileNotFoundException("Der angegebene Tastenplan wurde nicht gefunden.", opts["--keymap"]);
                    if (opts.ContainsKey("--keymap") && string.Equals(Path.GetFullPath(opts["--out"]), Path.GetFullPath(opts["--keymap"]), StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("Rohprotokoll und Tastenplan brauchen unterschiedliche Dateipfade.");
                    keymap = opts.ContainsKey("--keymap") && File.Exists(opts["--keymap"]) ? KeyMapStore.Load(opts["--keymap"]) : KeyMapStore.Create(fingerprint);
                    if (keymap.ProtocolFingerprint != fingerprint) throw new InvalidOperationException("Tastenplan gehoert zu anderen Geraete-/Protokollmetadaten. Neu anlernen oder den passenden Plan verwenden.");
                    if (learning) KeyMapStore.SetLabel(keymap, 0, opts["--label"]); // Validate before recording.
                }
                if (devices.Count == 0) { Console.Error.WriteLine("Keine passende herstellerdefinierte Input-Collection gefunden."); return 2; }
                Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e) { e.Cancel = true; cancelled = true; stop = true; };
                Stopwatch clock = Stopwatch.StartNew(); int total = 0, opened = 0, failures = 0, parsed = 0, rejected = 0;
                RawLiveView liveView = live ? new RawLiveView() : null;
                using (StreamWriter writer = new StreamWriter(new FileStream(opts["--out"], FileMode.CreateNew, FileAccess.Write), new UTF8Encoding(false)))
                {
                    Log(writer, new { type = "session", schemaVersion = 1, utc = Utc(), mode = "passive-input-only", view = learning ? "learn-key" : live ? "all-keys-live-raw" : "raw-capture", decoder = decoder == null ? null : decoder.Id, seconds = seconds, maxReports = maxReports, note = "Metadata chooses a candidate; every report is validated. No feature requests, no output reports, no controller. First byte includes Windows report ID." });
                    List<Thread> threads = new List<Thread>();
                    try
                    {
                        foreach (CollectionInfo item in devices)
                        {
                            CollectionInfo device = item;
                            Log(writer, new { type = "device", utc = Utc(), device = device });
                            Thread thread = new Thread(delegate()
                            {
                                try
                                {
                                    using (InputReader reader = new InputReader(device))
                                    {
                                        Interlocked.Increment(ref opened);
                                        while (!stop && clock.Elapsed.TotalSeconds < seconds && Volatile.Read(ref total) < maxReports)
                                        {
                                            byte[] report = reader.Read(100);
                                            if (stop) break;
                                            if (report == null) continue;
                                            double elapsed = clock.Elapsed.TotalMilliseconds;
                                            if (Interlocked.Increment(ref total) > maxReports) { Interlocked.Decrement(ref total); break; }
                                            Log(writer, new { type = "report", utc = Utc(), devicePath = device.devicePath, elapsedMs = elapsed, reportId = (int)report[0], reportLength = report.Length, hex = BitConverter.ToString(report) });
                                            if (live)
                                            {
                                                TravelSample sample; string parseError;
                                                if (decoder.TryParse(report, out sample, out parseError))
                                                {
                                                    sample.KeyLabel = keymap.GetLabel(sample.KeyIndex);
                                                    liveView.Publish(sample, elapsed); Interlocked.Increment(ref parsed);
                                                    if (learner != null)
                                                    {
                                                        learner.Feed(sample, elapsed);
                                                        if (learner.State == KeyLearnerState.Completed || learner.State == KeyLearnerState.Ambiguous) stop = true;
                                                    }
                                                }
                                                else
                                                {
                                                    stop = true;
                                                    liveView.Invalidate(parseError);
                                                    if (Interlocked.Increment(ref rejected) <= 5) Log(writer, new { type = "parse-error", utc = Utc(), devicePath = device.devicePath, error = parseError });
                                                }
                                            }
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Interlocked.Increment(ref failures);
                                    if (liveView != null) { stop = true; liveView.Invalidate(ex.Message); }
                                    ReportError(writer, new { type = "error", utc = Utc(), devicePath = device.devicePath, elapsedMs = clock.Elapsed.TotalMilliseconds, error = ex.Message }, "Diagnosefehler: " + ex.Message);
                                }
                            });
                            thread.IsBackground = true; threads.Add(thread); thread.Start();
                        }
                        Console.WriteLine("Passive Diagnose fuer {0} Sek.; {1} Collection(s). Keine automatische Aktivierung des Sensormonitors.", seconds, devices.Count);
                        if (live)
                        {
                            Console.WriteLine("Rohwerte, keine mm/Prozent/Kalibrierung. Noch nie gemeldete Indizes fehlen; alter Wert ist kein Verbindungsnachweis.");
                            Console.WriteLine("Alle gemeldeten Indizes; Tastenlabels nur aus passendem Tastenplan. Diagnose erzeugt keinen Controller.");
                            if (learning) Console.WriteLine("Nur {0} zweimal vollstaendig druecken und loslassen, in eigenem Tempo. Eine weitere Taste bricht das Anlernen ab.", opts["--label"]);
                            while (threads.Any(t => t.IsAlive)) { liveView.Display(clock.Elapsed.TotalMilliseconds); Thread.Sleep(200); }
                            liveView.Display(clock.Elapsed.TotalMilliseconds);
                        }
                    }
                    catch { stop = true; throw; }
                    finally
                    {
                        // Also drain readers when a later device log or Thread.Start fails.
                        // Threads whose Start failed cannot be joined.
                        foreach (Thread thread in threads)
                            if ((thread.ThreadState & System.Threading.ThreadState.Unstarted) == 0) thread.Join();
                        if (liveView != null && liveView.InvalidationReason == null)
                            liveView.Invalidate(cancelled ? "Abgebrochen; alte Werte sind unbekannt." : "Diagnose beendet; alte Werte sind unbekannt.");
                    }
                    if (liveView != null) liveView.Display(clock.Elapsed.TotalMilliseconds);
                    Log(writer, new { type = "end", utc = Utc(), elapsedMs = clock.Elapsed.TotalMilliseconds, reports = total, openedCollections = opened, errors = failures, stopped = stop, limitReached = total >= maxReports, parsedTravelReports = parsed, unsupportedTravelReports = rejected });
                }
                Console.WriteLine("{0} Rohreports, {1} Collection(s) geoeffnet, {2} Fehler. Datei: {3}", total, opened, failures, opts["--out"]);
                if (rejected > 0) ErrorLine(rejected + " Reports passen nicht zum bestaetigten Hubformat; Rohreports wurden erhalten.");
                if (learning)
                {
                    if (cancelled || failures > 0 || rejected > 0 || learner.State != KeyLearnerState.Completed) throw new InvalidOperationException("Tastenplan nicht gespeichert: " + (cancelled ? "Anlernen abgebrochen." : learner.Error ?? "Zwei eindeutige Druck-/Loslasszyklen fehlen."));
                    KeyMapStore.Save(opts["--keymap"], KeyMapStore.SetLabel(keymap, learner.KeyIndex, opts["--label"]));
                    Console.WriteLine("{0} = Index {1}; gespeichert in {2}", opts["--label"], learner.KeyIndex, opts["--keymap"]);
                }
                return failures > 0 ? 3 : (rejected > 0 ? 4 : 0);
            }
            catch (Exception ex) { ErrorLine(ex.Message); return 1; }
        }
    }
}
