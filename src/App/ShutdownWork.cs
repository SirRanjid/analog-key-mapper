using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Tk75.App
{
    internal enum ShutdownPhaseState { Pending, Running, Completed, Failed }
    // A fixed set of independent cleanup lanes. Windows shutdown may stop
    // waiting without disposing synchronization objects still used by workers.
    internal sealed class ShutdownWork
    {
        readonly Thread[] workers;
        readonly object gate = new object();
        readonly List<Exception> errors = new List<Exception>();
        readonly ShutdownPhaseState[] states;
        bool started;
        internal ShutdownWork(params Action[] actions)
        {
            if (actions == null || actions.Length == 0 || actions.Length > 3) throw new ArgumentException("One to three shutdown actions are required.", "actions");
            workers = new Thread[actions.Length];
            states = new ShutdownPhaseState[actions.Length];
            for (int i = 0; i < actions.Length; i++)
            {
                Action action = actions[i];
                int phase = i;
                if (action == null) throw new ArgumentException("Shutdown actions must not be null.", "actions");
                workers[i] = new Thread(delegate()
                {
                    lock (gate) states[phase] = ShutdownPhaseState.Running;
                    try { action(); lock (gate) states[phase] = ShutdownPhaseState.Completed; }
                    catch (Exception error) { lock (gate) { errors.Add(error); states[phase] = ShutdownPhaseState.Failed; } }
                }) { IsBackground = true, Name = "Windows shutdown cleanup " + (i + 1) };
            }
        }
        internal void Start()
        {
            lock (gate) { if (started) throw new InvalidOperationException("Shutdown cleanup has already started."); started = true; }
            for (int phase = 0; phase < workers.Length; phase++)
                try { workers[phase].Start(); }
                catch (Exception error) { lock (gate) { errors.Add(error); states[phase] = ShutdownPhaseState.Failed; } }
        }
        internal bool Wait(int milliseconds)
        {
            if (milliseconds < 0) throw new ArgumentOutOfRangeException("milliseconds");
            lock (gate) if (!started) return false;
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
        internal ShutdownPhaseState[] States { get { lock (gate) return (ShutdownPhaseState[])states.Clone(); } }
    }
}
