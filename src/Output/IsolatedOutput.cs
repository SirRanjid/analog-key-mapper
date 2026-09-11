using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Tk75.Mapping;

namespace Tk75.Output
{
    // The UI process never loads ViGEm. Only its explicitly started child may do
    // so; every request has a deadline and the parent owns a kill-on-close job.
    public sealed class IsolatedOutput : IControllerOutput
    {
        readonly object gate = new object();
        readonly string executable, arguments;
        readonly int connectTimeout, commandTimeout, quitTimeout, gracefulStopTimeout;
        readonly Stopwatch clock = Stopwatch.StartNew();
        Process process;
        HostJob job;
        ReplyChannel replies;
        bool connected, disposed, hasLastPacket;
        XInputPacket lastPacket;
        long nextId, lastSentAt, acknowledgements;
        int? lastHostProcessId;
        string status = "Controller aus; separater Ausgabeprozess nicht gestartet.";

        public IsolatedOutput() : this(EntryExecutable(), "--output-host", 3000, 250) { }
        // Alternate executable is solely a test/deployment seam; it is not a
        // command shell. Constructors never start a process or load a client DLL.
        public IsolatedOutput(string executablePath, string hostArguments, int connectTimeoutMs, int commandTimeoutMs)
            : this(executablePath, hostArguments, connectTimeoutMs, commandTimeoutMs, commandTimeoutMs) { }
        // Device removal may take longer than a neutral report. Keep that separate
        // deadline so a slow shutdown never relaxes live input acknowledgements.
        public IsolatedOutput(string executablePath, string hostArguments, int connectTimeoutMs, int commandTimeoutMs, int quitTimeoutMs)
            : this(executablePath, hostArguments, connectTimeoutMs, commandTimeoutMs, quitTimeoutMs, 0) { }
        // Some hosts need time after EOF/watchdog expiry to remove their device.
        // This is cleanup time only; a late live ACK still fails its command.
        public IsolatedOutput(string executablePath, string hostArguments, int connectTimeoutMs, int commandTimeoutMs, int quitTimeoutMs, int gracefulStopTimeoutMs)
        {
            if (String.IsNullOrWhiteSpace(executablePath)) throw new ArgumentNullException("executablePath");
            if (connectTimeoutMs < 1 || connectTimeoutMs > 30000 || commandTimeoutMs < 1 || commandTimeoutMs > 30000 || quitTimeoutMs < 1 || quitTimeoutMs > 30000)
                throw new ArgumentOutOfRangeException("timeout");
            if (gracefulStopTimeoutMs < 0 || gracefulStopTimeoutMs > 30000)
                throw new ArgumentOutOfRangeException("gracefulStopTimeoutMs");
            executable = Path.GetFullPath(executablePath);
            arguments = hostArguments ?? "";
            connectTimeout = connectTimeoutMs; commandTimeout = commandTimeoutMs; quitTimeout = quitTimeoutMs;
            gracefulStopTimeout = gracefulStopTimeoutMs;
        }
        static string EntryExecutable()
        {
            var assembly = Assembly.GetEntryAssembly();
            if (assembly == null || String.IsNullOrEmpty(assembly.Location)) throw new InvalidOperationException("Anwendungspfad fuer Outputprozess fehlt.");
            return assembly.Location;
        }
        public string Status { get { lock (gate) return status; } }
        public bool IsConnected { get { lock (gate) return connected && replies != null && replies.Fault == null; } }
        public int? HostProcessId { get { lock (gate) return process == null ? (int?)null : process.Id; } }
        public int? LastHostProcessId { get { lock (gate) return lastHostProcessId; } }
        public long AcknowledgedCommands { get { lock (gate) return acknowledgements; } }

        public void Connect()
        {
            lock (gate)
            {
                CheckDisposed();
                if (connected && replies != null && replies.Fault == null) return;
                if (process != null) StopChild();
                try
                {
                    if (!File.Exists(executable)) throw new FileNotFoundException("Ausgabeprogramm fehlt.", executable);
                    job = new HostJob();
                    process = new Process { StartInfo = new ProcessStartInfo {
                        FileName = executable, Arguments = arguments, WorkingDirectory = Path.GetDirectoryName(executable),
                        UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
                    } };
                    if (!process.Start()) throw new InvalidOperationException("Ausgabeprozess konnte nicht gestartet werden.");
                    lastHostProcessId = process.Id;
                    process.StandardInput.AutoFlush = true;
                    job.Assign(process);
                    replies = new ReplyChannel(process.StandardOutput, process.StandardError);
                    nextId = acknowledgements = 0;
                    Request("CONNECT", connectTimeout);
                    connected = true;
                    lastPacket = new XInputPacket(); hasLastPacket = true; lastSentAt = clock.ElapsedMilliseconds;
                    status = "Virtueller Controller verbunden; Ausgabeprozess mit Zeitgrenzen aktiv.";
                }
                catch (Exception ex) { throw Fail(ex); }
            }
        }
        public void Submit(ControllerFrame frame)
        {
            lock (gate)
            {
                CheckDisposed();
                try
                {
                    RequireConnected();
                    XInputPacket packet = XInputPacket.FromFrame(frame);
                    // A changed frame is immediate. Identical frames receive an ACK
                    // at least every 100ms while Submit is regularly called.
                    if (hasLastPacket && OutputWire.Equal(lastPacket, packet) && clock.ElapsedMilliseconds - lastSentAt < 100) return;
                    Request("FRAME " + OutputWire.Packet(packet), commandTimeout);
                    lastPacket = packet; hasLastPacket = true; lastSentAt = clock.ElapsedMilliseconds;
                }
                catch (Exception ex) { throw Fail(ex); }
            }
        }
        public void Neutral()
        {
            lock (gate)
            {
                if (disposed || !connected) return;
                try
                {
                    RequireConnected(); Request("NEUTRAL", commandTimeout);
                    lastPacket = new XInputPacket(); hasLastPacket = true; lastSentAt = clock.ElapsedMilliseconds;
                }
                catch (Exception ex) { throw Fail(ex); }
            }
        }
        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                string error = "";
                bool canQuit = connected && replies != null && replies.Fault == null;
                connected = false;
                status = "Controller-Ausgabe wird beendet.";
                if (canQuit)
                    try { Request("QUIT", quitTimeout); } catch (Exception ex) { error = ex.GetBaseException().Message; }
                string cleanup = StopChild();
                status = "Controller-Ausgabe beendet." + (error.Length == 0 ? "" : " " + error) + cleanup;
            }
        }
        void CheckDisposed() { if (disposed) throw new ObjectDisposedException("IsolatedOutput"); }
        void RequireConnected()
        {
            if (!connected || process == null || replies == null) throw new InvalidOperationException("Controller-Ausgabe ist nicht verbunden.");
            if (replies.Fault != null) throw new IOException(replies.Fault);
        }
        InvalidOperationException Fail(Exception exception)
        {
            string error = exception.GetBaseException().Message;
            connected = false;
            status = "Controller aus: " + error;
            string cleanup = StopChild();
            status = "Controller aus: " + error + cleanup;
            return new InvalidOperationException(status, exception);
        }
        void Request(string command, int timeoutMs)
        {
            if (process == null || replies == null) throw new InvalidOperationException("Ausgabeprozess fehlt.");
            long id = checked(++nextId);
            string line = OutputWire.Version + " " + id.ToString(CultureInfo.InvariantCulture) + " " + command;
            if (line.Length > OutputWire.MaximumCommand) throw new InvalidDataException("Outputbefehl ist zu lang.");
            var deadline = Stopwatch.StartNew();
            Task write = process.StandardInput.WriteLineAsync(line);
            // Observe eventual pipe errors even when the deadline kills its child.
            write.ContinueWith(delegate(Task failed) { GC.KeepAlive(failed.Exception); }, TaskContinuationOptions.OnlyOnFaulted);
            if (!write.Wait(Remaining(deadline, timeoutMs))) throw new TimeoutException("Ausgabeprozess antwortet nicht innerhalb der Zeitgrenze.");
            for (;;)
            {
                Reply reply;
                if (replies.TryTake(out reply))
                {
                    if (reply.Id != id) throw new InvalidDataException("Ausgabeantwort hat eine unerwartete Befehlsnummer.");
                    if (reply.Error != null) throw new InvalidOperationException(reply.Error);
                    acknowledgements++; return;
                }
                if (replies.Fault != null) throw new IOException(replies.Fault);
                if (!replies.Wait(Remaining(deadline, timeoutMs))) throw new TimeoutException("Ausgabeprozess antwortet nicht innerhalb der Zeitgrenze.");
            }
        }
        static int Remaining(Stopwatch deadline, int timeoutMs)
        {
            long remaining = timeoutMs - deadline.ElapsedMilliseconds;
            if (remaining <= 0) throw new TimeoutException("Ausgabeprozess antwortet nicht innerhalb der Zeitgrenze.");
            return (int)remaining;
        }
        string StopChild()
        {
            connected = false; hasLastPacket = false; lastPacket = new XInputPacket();
            Process oldProcess = process; HostJob oldJob = job; ReplyChannel oldReplies = replies;
            process = null; job = null; replies = null;
            string warning = "";
            Task inputClose = null;
            if (oldProcess != null && gracefulStopTimeout > 0)
            {
                var deadline = Stopwatch.StartNew();
                if (oldReplies != null) oldReplies.BeginShutdownDrain();
                try
                {
                    if (!oldProcess.HasExited)
                    {
                        // StreamWriter.Close can flush a blocked pipe indefinitely.
                        // Close only its underlying stream, on a background task, so
                        // EOF can reach the helper without extending this deadline.
                        inputClose = CloseInputWithoutFlush(oldProcess.StandardInput.BaseStream);
                        int remaining = (int)Math.Max(0, gracefulStopTimeout - deadline.ElapsedMilliseconds);
                        if (remaining > 0) oldProcess.WaitForExit(remaining);
                    }
                }
                catch (InvalidOperationException) { }
                catch (Exception ex) { warning += " Geordnetes Prozessende: " + ex.Message; }
            }
            // Closing this private job terminates only this session's output host,
            // including on unexpected parent-process exit. Never kill by image name.
            // With a grace deadline, this is the fallback after allowing the host
            // to neutralize and remove its device; stdout/stderr still drain above.
            if (oldJob != null) try { oldJob.Dispose(); } catch (Exception ex) { warning += " Prozessbereinigung: " + ex.Message; }
            if (oldProcess != null)
            {
                try
                {
                    if (!oldProcess.HasExited) oldProcess.Kill();
                    if (!oldProcess.WaitForExit(250)) warning += " Ende des eigenen Ausgabeprozesses noch nicht bestaetigt.";
                }
                catch (InvalidOperationException) { }
                catch (Exception ex) { warning += " Ausgabeprozess konnte nicht sauber beendet werden: " + ex.Message; }
            }
            if (oldReplies != null) oldReplies.Close();
            if (oldProcess != null)
            {
                if (gracefulStopTimeout == 0) DisposeProcess(oldProcess);
                else
                {
                    // Process.Dispose may close/flush its StreamWriter. Never let
                    // that run synchronously on the bounded teardown path either.
                    if (inputClose == null) inputClose = Task.FromResult(0);
                    inputClose.ContinueWith(delegate { DisposeProcess(oldProcess); },
                        CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                }
            }
            return warning;
        }
        internal static Task CloseInputWithoutFlush(Stream input)
        {
            if (input == null) throw new ArgumentNullException("input");
            return Task.Factory.StartNew(delegate
            {
                try { input.Dispose(); } catch (Exception) { }
            }, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
        }
        static void DisposeProcess(Process child)
        { try { child.Dispose(); } catch (Exception) { } }

        sealed class Reply { internal long Id; internal string Error; }
        sealed class ReplyChannel
        {
            readonly object gate = new object();
            readonly Queue<Reply> queue = new Queue<Reply>();
            readonly AutoResetEvent changed = new AutoResetEvent(false);
            readonly Thread stdout, stderr;
            string fault;
            bool closing, draining;
            internal string Fault { get { lock (gate) return fault; } }
            internal ReplyChannel(TextReader output, TextReader errors)
            {
                stdout = new Thread(delegate() { ReadReplies(output); }) { IsBackground = true, Name = "Output ACK reader" };
                stderr = new Thread(delegate() { DrainErrors(errors); }) { IsBackground = true, Name = "Output diagnostic drain" };
                stdout.Start(); stderr.Start();
            }
            void SetFault(string error)
            { lock (gate) { if (closing) return; if (fault == null) fault = error; changed.Set(); } }
            void ReadReplies(TextReader output)
            {
                try
                {
                    for (;;)
                    {
                        string line = OutputWire.ReadLine(output, OutputWire.MaximumReply);
                        if (line == null) { SetFault("Ausgabeprozess wurde unerwartet beendet."); return; }
                        lock (gate) if (draining) continue;
                        string[] fields = line.Split(' '); long id;
                        if (fields.Length < 3 || fields[0] != OutputWire.Version ||
                            !Int64.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out id) || id < 1)
                            throw new InvalidDataException("Ungueltige Ausgabeantwort.");
                        var reply = new Reply { Id = id };
                        if (fields.Length == 4 && fields[2] == "ERROR") reply.Error = Encoding.UTF8.GetString(Convert.FromBase64String(fields[3]));
                        else if (fields.Length != 3 || fields[2] != "ACK") throw new InvalidDataException("Ungueltige Ausgabeantwort.");
                        lock (gate)
                        {
                            if (closing) return;
                            if (draining) continue;
                            if (queue.Count >= 4) throw new InvalidDataException("Zu viele unerwartete Ausgabeantworten.");
                            queue.Enqueue(reply); changed.Set();
                        }
                    }
                }
                catch (Exception ex)
                {
                    SetFault(ex.GetBaseException().Message);
                    // A malformed reply must fail the command, but keep consuming
                    // stdout so the child's own cleanup cannot block on a full pipe.
                    DrainErrors(output);
                }
            }
            void DrainErrors(TextReader errors)
            {
                // Drain bounded chunks so a broken child cannot fill its stderr pipe
                // or consume parent memory. Protocol ERROR carries the visible reason.
                try { var buffer = new char[256]; while (errors.Read(buffer, 0, buffer.Length) != 0) { } }
                catch (Exception) { }
            }
            internal bool TryTake(out Reply reply)
            { lock (gate) { reply = queue.Count == 0 ? null : queue.Dequeue(); return reply != null; } }
            internal bool Wait(int milliseconds) { return changed.WaitOne(milliseconds); }
            internal void BeginShutdownDrain()
            { lock (gate) { draining = true; queue.Clear(); } }
            internal void Close()
            {
                lock (gate) closing = true;
                bool ended = stdout.Join(50); ended = stderr.Join(50) && ended;
                if (ended) changed.Dispose();
                // If Windows has not yet closed a pipe, let its background reader
                // finish naturally; never wait indefinitely or dispose its live event.
            }
        }

        // Windows process containment only, not a keyboard/controller driver.
        // ABI: Microsoft JOBOBJECT_EXTENDED_LIMIT_INFORMATION, info class9.
        sealed class HostJob : IDisposable
        {
            readonly SafeFileHandle handle;
            internal HostJob()
            {
                handle = CreateJobObject(IntPtr.Zero, null);
                if (handle == null || handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Ausgabeprozess konnte nicht abgesichert werden.");
                var limits = new ExtendedLimit(); limits.Basic.LimitFlags = 0x00002000;
                if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf(typeof(ExtendedLimit))))
                { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error, "Ausgabeprozess konnte nicht abgesichert werden."); }
            }
            internal void Assign(Process child)
            { if (!AssignProcessToJobObject(handle, child.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Ausgabeprozess konnte nicht seinem Besitzer zugeordnet werden."); }
            public void Dispose() { handle.Dispose(); }
            [StructLayout(LayoutKind.Sequential)] struct BasicLimit
            {
                internal long ProcessTime, JobTime; internal uint LimitFlags;
                internal UIntPtr MinimumWorkingSet, MaximumWorkingSet; internal uint ActiveProcesses;
                internal UIntPtr Affinity; internal uint PriorityClass, SchedulingClass;
            }
            [StructLayout(LayoutKind.Sequential)] struct IoCounters
            { internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
            [StructLayout(LayoutKind.Sequential)] struct ExtendedLimit
            { internal BasicLimit Basic; internal IoCounters Io; internal UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
            [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
            static extern SafeFileHandle CreateJobObject(IntPtr attributes, string name);
            [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
            static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref ExtendedLimit information, uint length);
            [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
            static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
        }
    }
}
