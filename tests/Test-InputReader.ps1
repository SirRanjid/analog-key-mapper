#requires -Version 5.1
<#
.SYNOPSIS
Prueft den passiven InputReader mit einer lokalen NamedPipe ohne HID-Hardware.
.DESCRIPTION
Laedt ausschliesslich src/HidNative.cs und den folgenden Test-Harness in den
aktuellen PowerShell-Prozess. Kein Monitor, keine EXE, keine Feature-Reports,
keine Aenderung von Windows-Schutz oder Ausfuehrungsrichtlinien.
Bei einem Add-Type-/Richtlinienfehler wird der Test beendet; kein Ersatzpfad.
#>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Dieser NamedPipe/PInvoke-Test erfordert Windows.' }
if ('Tk75.Diagnostics.InputReaderPipeTest' -as [type]) {
    throw 'Der Testtyp ist bereits geladen. Fuer eine erneute Quellcodepruefung eine neue PowerShell-Sitzung verwenden.'
}
$nativePath = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\HidNative.cs'
$source = [System.IO.File]::ReadAllText($nativePath)
$harness = @'

namespace Tk75.Diagnostics
{
    using System;
    using System.Diagnostics;
    using System.IO.Pipes;
    using System.Reflection;
    using System.Runtime.InteropServices;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Win32.SafeHandles;

    public sealed class InputReaderPipeResult
    {
        public bool synthetic = true;
        public int assertions;
        public int verifiedReports;
        public double emptyReadElapsedMs;
        public double pendingDisposeElapsedMs;
        public bool inputHandleClosed;
        public bool nativeEventClosed;
        public bool serverHandleClosed;
    }

    public static class InputReaderPipeTest
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetHandleInformation(IntPtr handle, out uint flags);

        static void Check(InputReaderPipeResult result, bool condition, string message)
        {
            result.assertions++;
            if (!condition) throw new InvalidOperationException("TEST FEHLGESCHLAGEN: " + message);
        }

        static bool SameBytes(byte[] expected, byte[] actual)
        {
            if (actual == null || actual.Length != expected.Length) return false;
            for (int i = 0; i < expected.Length; i++)
                if (expected[i] != actual[i]) return false;
            return true;
        }

        public static InputReaderPipeResult Run()
        {
            var result = new InputReaderPipeResult();
            string pipeName = "tk75-inputreader-synthetic-" + Guid.NewGuid().ToString("N");
            var server = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096);
            InputReader reader = null;
            Thread disposingThread = null;
            Exception disposingError = null;
            SafePipeHandle serverHandle = server.SafePipeHandle;
            try
            {
                Task connecting = server.WaitForConnectionAsync();
                var device = new CollectionInfo();
                device.devicePath = @"\\.\pipe\" + pipeName;
                device.inputReportLength = 32;
                reader = new InputReader(device);
                Check(result, connecting.Wait(3000), "NamedPipe-Verbindung nicht rechtzeitig hergestellt.");

                var clock = Stopwatch.StartNew();
                byte[] empty = reader.Read(40);
                clock.Stop();
                result.emptyReadElapsedMs = clock.Elapsed.TotalMilliseconds;
                Check(result, empty == null, "Ohne Daten muss Read null liefern.");
                Check(result, clock.ElapsedMilliseconds >= 10 && clock.ElapsedMilliseconds < 2000,
                    "Die kurze Wartezeit muss begrenzt ablaufen.");

                // Erfundene Muster. Sie behaupten weder Report-IDs noch Tastenwerte.
                byte[] first = new byte[32];
                byte[] second = new byte[32];
                for (int i = 0; i < 32; i++)
                {
                    first[i] = (byte)((i * 7 + 3) & 255);
                    second[i] = (byte)(255 - i * 5);
                }
                first[0] = 0; first[31] = 255;
                second[0] = 165; second[31] = 0;

                // Der erste Read wartet bereits seit dem Timeout auf I/O.
                Task firstWrite = server.WriteAsync(first, 0, first.Length);
                byte[] firstRead = reader.Read(3000);
                Check(result, firstWrite.Wait(3000), "Erstes asynchrones Schreiben blieb offen.");
                Check(result, SameBytes(first, firstRead), "Erster 32-Byte-Report stimmt nicht bytegenau.");
                result.verifiedReports++;

                // Jetzt werden die Bytes vor dem naechsten Read bereitgestellt.
                // Dies prueft einen neuen Read bei bereits verfuegbaren Daten.
                Task secondWrite = server.WriteAsync(second, 0, second.Length);
                Check(result, secondWrite.Wait(3000), "Zweites asynchrones Schreiben blieb offen.");
                byte[] secondRead = reader.Read(3000);
                Check(result, SameBytes(second, secondRead), "Zweiter 32-Byte-Report stimmt nicht bytegenau.");
                Check(result, SameBytes(first, firstRead), "Der zweite Read hat den ersten Ergebnispuffer veraendert.");
                result.verifiedReports++;

                Check(result, reader.Read(30) == null, "Vor Dispose muss wieder ein Read ohne Daten warten.");

                // Reflection dient nur der externen Freigabepruefung. Der Reader
                // selbst ist unveraendert und fuehrt die echten Win32-Aufrufe aus.
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                SafeFileHandle inputHandle = (SafeFileHandle)typeof(InputReader).GetField("handle", flags).GetValue(reader);
                IntPtr nativeEvent = (IntPtr)typeof(InputReader).GetField("ready", flags).GetValue(reader);
                uint ignoredFlags;
                Check(result, !inputHandle.IsClosed && GetHandleInformation(nativeEvent, out ignoredFlags),
                    "Die zu pruefenden Handles muessen vor Dispose offen sein.");

                InputReader readerToDispose = reader;
                disposingThread = new Thread(delegate()
                {
                    try { readerToDispose.Dispose(); }
                    catch (Exception ex) { disposingError = ex; }
                });
                disposingThread.IsBackground = true;
                clock.Restart();
                disposingThread.Start();
                bool completed = disposingThread.Join(3000);
                clock.Stop();
                result.pendingDisposeElapsedMs = clock.Elapsed.TotalMilliseconds;
                Check(result, completed, "Dispose hat den ausstehenden Read nicht innerhalb von 3 s beendet.");
                Check(result, disposingError == null, "Dispose meldete einen Fehler: " + disposingError);
                result.inputHandleClosed = inputHandle.IsClosed;
                bool eventStillValid = GetHandleInformation(nativeEvent, out ignoredFlags);
                int eventError = Marshal.GetLastWin32Error();
                result.nativeEventClosed = !eventStillValid && eventError == 6;
                Check(result, result.inputHandleClosed, "Der Client-Dateihandle blieb offen.");
                Check(result, result.nativeEventClosed, "Der native Event-Handle blieb offen oder lieferte einen unerwarteten Fehler.");

                // Mehrfaches Dispose muss nach abgeschlossenem Abbruch harmlos sein.
                reader.Dispose();
                reader = null;
                server.Dispose();
                result.serverHandleClosed = serverHandle.IsClosed;
                Check(result, result.serverHandleClosed, "Der Testserver-Handle blieb offen.");
                return result;
            }
            finally
            {
                // Bei einem fehlgeschlagenen Timeout loest das Schliessen des
                // Servers die Pipe. Nie parallel zweimal denselben Reader freigeben.
                server.Dispose();
                if (disposingThread != null && disposingThread.IsAlive)
                    disposingThread.Join(3000);
                if (reader != null && (disposingThread == null || !disposingThread.IsAlive))
                    reader.Dispose();
            }
        }
    }
}
'@
Add-Type -TypeDefinition ($source + [Environment]::NewLine + $harness) -Language CSharp -ErrorAction Stop
$result = [Tk75.Diagnostics.InputReaderPipeTest]::Run()
Write-Output ('PASS: {0} Assertions; {1} synthetische 32-Byte-Reports; Timeout {2:N1} ms; Pending-Dispose {3:N1} ms; alle geprueften Handles geschlossen.' -f $result.assertions, $result.verifiedReports, $result.emptyReadElapsedMs, $result.pendingDisposeElapsedMs)
$result
