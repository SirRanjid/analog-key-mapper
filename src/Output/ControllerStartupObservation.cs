using System;

namespace Tk75.Output
{
    // Pure observation of the four public XInput slots. No device operations.
    public sealed class ControllerStartupObservation
    {
        public sealed class Slot
        {
            public int Index;
            public uint Status;
            public ushort Buttons;
            public int LT, RT, LX, LY, RX, RY;
            public bool IsNeutral { get { return Status == 0 && Buttons == 0 && LT == 0 && RT == 0 && LX == 0 && LY == 0 && RX == 0 && RY == 0; } }
            public Slot Copy() { return (Slot)MemberwiseClone(); }
        }

        readonly Slot[] before;
        Slot first, last;
        public int NewSlot { get; private set; }
        public int Samples { get; private set; }
        public long? FirstObservedMs { get; private set; }
        public long? LastObservedMs { get; private set; }
        public long LargestGapMs { get; private set; }
        long? previousCallMs;
        public string Failure { get; private set; }
        public Slot First { get { return first == null ? null : first.Copy(); } }
        public Slot Last { get { return last == null ? null : last.Copy(); } }

        public ControllerStartupObservation(Slot[] baseline)
        {
            Validate(baseline);
            before = Copy(baseline); NewSlot = -1;
            bool free = false;
            foreach (var slot in before) if (slot.Status == 1167) free = true;
            if (!free) throw new InvalidOperationException("Alle vier XInput-Plätze sind bereits belegt.");
        }

        public void Add(Slot[] snapshot)
        {
            Validate(snapshot);
            int found = -1;
            for (int i = 0; i < 4; i++)
                if (before[i].Status == 1167 && snapshot[i].Status == 0)
                {
                    if (found >= 0) { Fail("Mehrere neue XInput-Plätze: Zuordnung ist nicht eindeutig."); return; }
                    found = i;
                }
            if (found < 0)
            {
                if (NewSlot >= 0) Fail("Der neue Controller verschwand während der Startprüfung.");
                return;
            }
            if (NewSlot >= 0 && NewSlot != found) { Fail("Der neue Controller wechselte unerwartet den XInput-Platz."); return; }
            NewSlot = found; Samples++;
            last = snapshot[found].Copy();
            if (first == null) first = last.Copy();
            if (!last.IsNeutral) Fail("Der neue Controller meldete während der Startprüfung einen aktiven Wert.");
        }

        public void Add(Slot[] snapshot, long elapsedMs)
        {
            if (elapsedMs < 0 || (previousCallMs.HasValue && elapsedMs < previousCallMs.Value))
                throw new ArgumentOutOfRangeException("elapsedMs", "Beobachtungszeit muss monoton sein.");
            int oldSamples = Samples;
            Add(snapshot); previousCallMs = elapsedMs;
            if (Samples == oldSamples) return;
            if (LastObservedMs.HasValue)
            {
                LargestGapMs = Math.Max(LargestGapMs, elapsedMs - LastObservedMs.Value);
                if (LargestGapMs > 100) Fail("XInput-Beobachtung war über 100 ms unterbrochen; Startprüfung ist unvollständig.");
            }
            if (!FirstObservedMs.HasValue) FirstObservedMs = elapsedMs;
            LastObservedMs = elapsedMs;
        }

        public bool AllNewSlotsRemoved(Slot[] snapshot)
        {
            Validate(snapshot);
            for (int i = 0; i < 4; i++) if (before[i].Status == 1167 && snapshot[i].Status == 0) return false;
            return true;
        }

        void Fail(string message) { if (Failure == null) Failure = message; }
        public static Slot[] Copy(Slot[] slots)
        {
            Validate(slots);
            var copy = new Slot[4];
            for (int i = 0; i < 4; i++) copy[i] = slots[i].Copy();
            return copy;
        }
        static void Validate(Slot[] slots)
        {
            if (slots == null || slots.Length != 4) throw new ArgumentException("Vier XInput-Plätze erforderlich.");
            for (int i = 0; i < 4; i++)
                if (slots[i] == null || slots[i].Index != i || (slots[i].Status != 0 && slots[i].Status != 1167))
                    throw new ArgumentException("Ungültige XInput-Beobachtung oder unerwarteter API-Fehler.");
        }
    }
}
