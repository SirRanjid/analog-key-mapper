using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Tk75.App
{
    // A fixed set of independent cleanup lanes. Windows shutdown may stop
    // waiting without disposing synchronization objects still used by workers.
    internal sealed class ShutdownWork
    {
        readonly Thread[] workers;
        readonly object gate = new object();
        readonly List<Exception> errors = new List<Exception>();
        internal ShutdownWork(params Action[] actions)
        {
            if (actions == null || actions.Length == 0 || actions.Length > 3) throw new ArgumentException("One to three shutdown actions are required.", "actions");
            workers = new Thread[actions.Length];
            for (int i = 0; i < actions.Length; i++)
            {
                Action action = actions[i];
                if (action == null) throw new ArgumentException("Shutdown actions must not be null.", "actions");
                workers[i] = new Thread(delegate()
                {
                    try { action(); }
                    catch (Exception error) { lock (gate) errors.Add(error); }
                }) { IsBackground = true, Name = "Windows shutdown cleanup " + (i + 1) };
            }
        }
        internal void Start()
        {
            foreach (Thread worker in workers)
                try { worker.Start(); }
                catch (Exception error) { lock (gate) errors.Add(error); }
        }
        internal bool Wait(int milliseconds)
        {
            if (milliseconds < 0) throw new ArgumentOutOfRangeException("milliseconds");
            var elapsed = Stopwatch.StartNew();
            foreach (Thread worker in workers)
            {
                if (!worker.IsAlive) continue;
                int remaining = Math.Max(0, milliseconds - (int)elapsed.ElapsedMilliseconds);
                if (!worker.Join(remaining)) return false;
            }
            return true;
        }
        internal Exception[] Errors { get { lock (gate) return errors.ToArray(); } }
    }
}
