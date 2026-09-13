using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Tk75.Diagnostics;
using Tk75.Mapping;

namespace Tk75.App
{
    public sealed partial class MainForm
    {
        readonly object inputThresholdCaptureGate = new object();
        ReaderSession inputThresholdCaptureReader;
        Action<TravelSample, double> inputThresholdCaptureHandler;
        object inputThresholdCaptureGeneration;
        Dictionary<int, InputThresholdCapture> inputThresholdCandidates;
        InputThresholdCapture inputThresholdCapture;
        InputActivationFields inputThresholdCaptureField;
        int[] inputThresholdCaptureKeys = new int[0];
        int inputThresholdCaptureSource = -1;
        EditHistory inputThresholdCaptureHistory;
        object inputThresholdCaptureSnapshot;
        CalibrationDocument inputThresholdCaptureCalibration;

        void BeginInputThresholdCapture(InputActivationFields field)
        {
            RequireReader();
            if (field != InputActivationFields.Actuation && field != InputActivationFields.Release && field != InputActivationFields.Press) throw new ArgumentOutOfRangeException("field");
            int[] selected = SelectedKeys(); if (selected.Length == 0 || calibration == null) return;
            if (field != InputActivationFields.Actuation && rapidTrigger.CheckState == CheckState.Unchecked) return;
            CancelInputThresholdCapture(); CancelPressureCapture(); CancelInputThresholdGestures(); FlushInputDraft();
            var input = reader;
            var samples = input.GetUiSnapshot(MappingSession.MaximumInputAgeMilliseconds).Where(s => s.Known && !s.Stale).ToDictionary(s => s.KeyIndex);
            var candidates = new Dictionary<int, InputThresholdCapture>();
            foreach (int key in selected)
            {
                KeyStateSnapshot sample; var range = sharedPressureRange.ForKey(key);
                candidates.Add(key, new InputThresholdCapture(key, range.Rest, range.Bottom, samples.TryGetValue(key, out sample) ? (double?)sample.RawValue : null));
            }
            lock (inputThresholdCaptureGate)
            {
                inputThresholdCaptureReader = input; inputThresholdCandidates = candidates; inputThresholdCaptureKeys = selected;
                inputThresholdCaptureField = field; inputThresholdCaptureSource = -1; inputThresholdCapture = null;
                inputThresholdCaptureHistory = history; inputThresholdCaptureSnapshot = history.SnapshotToken; inputThresholdCaptureCalibration = calibration;
                object generation = new object(); inputThresholdCaptureGeneration = generation;
                inputThresholdCaptureHandler = delegate(TravelSample sample, double elapsed) {
                    lock (inputThresholdCaptureGate)
                    {
                        if (!Object.ReferenceEquals(inputThresholdCaptureGeneration, generation) || !Object.ReferenceEquals(inputThresholdCaptureReader, input)) return;
                        FeedInputThresholdCapture(sample, elapsed);
                    }
                };
            }
            input.Sample += inputThresholdCaptureHandler;
            RefreshPressureRangeEditor(); SetInputFieldAvailability(); RefreshInputThresholdCaptureButtons();
            SetInputStatus(candidates.Values.Any(candidate => candidate.WaitingForRelease) ? Tr("Gehaltene Tasten erst loslassen, dann bis zum gewünschten Punkt drücken.", "Release held keys first, then press to the desired point.") :
                Tr("Eine ausgewählte Taste bis zum gewünschten Punkt drücken, dann loslassen.", "Press any selected key to the desired point, then release."));
        }
        void FeedInputThresholdCapture(TravelSample sample, double elapsed)
        {
            lock (inputThresholdCaptureGate)
            {
                if (inputThresholdCapture != null) { inputThresholdCapture.Feed(sample.KeyIndex, sample.RawValue); return; }
                InputThresholdCapture candidate;
                if (inputThresholdCandidates == null || !inputThresholdCandidates.TryGetValue(sample.KeyIndex, out candidate)) return;
                candidate.Feed(sample.KeyIndex, sample.RawValue);
                if (candidate.Pressing || candidate.Completed) { inputThresholdCapture = candidate; inputThresholdCaptureSource = sample.KeyIndex; }
            }
        }
        void CancelInputThresholdCapture()
        {
            ReaderSession previous; Action<TravelSample, double> handler;
            lock (inputThresholdCaptureGate)
            {
                previous = inputThresholdCaptureReader; inputThresholdCaptureReader = null; inputThresholdCapture = null; inputThresholdCandidates = null;
                handler = inputThresholdCaptureHandler; inputThresholdCaptureHandler = null; inputThresholdCaptureGeneration = null;
                inputThresholdCaptureKeys = new int[0]; inputThresholdCaptureSource = -1; inputThresholdCaptureField = InputActivationFields.None;
                inputThresholdCaptureHistory = null; inputThresholdCaptureSnapshot = null; inputThresholdCaptureCalibration = null;
            }
            if (previous == null) return;
            if (handler != null) previous.Sample -= handler;
            if (!IsDisposed && !closing)
            {
                SetInputFieldAvailability(); RefreshInputThresholdCaptureButtons();
                SetInputStatus(InputSelectionLabel() + " · " + Tr("Kalibrierung beendet", "Calibration stopped"));
            }
        }
        void RefreshInputThresholdCaptureButtons()
        {
            bool capturing = inputThresholdCaptureReader != null;
            var buttons = new[] { calibrateActuation, calibrateRelease, calibrateRepress };
            var fields = new[] { InputActivationFields.Actuation, InputActivationFields.Release, InputActivationFields.Press };
            for (int i = 0; i < buttons.Length; i++)
            {
                bool active = capturing && inputThresholdCaptureField == fields[i];
                buttons[i].Text = active ? Tr("Abbrechen", "Cancel") : Tr("Kalibrieren", "Calibrate");
                buttons[i].MinimumSize = System.Drawing.Size.Empty;
                buttons[i].Enabled = active || !capturing && editingInputKeys.Length != 0 && calibration != null && reader != null && reader.IsReading &&
                    (fields[i] == InputActivationFields.Actuation || rapidTrigger.CheckState != CheckState.Unchecked);
                string help = active ? Tr("Messung abbrechen. Der bisherige Wert bleibt erhalten.", "Cancel the measurement and keep the previous value.") :
                    fields[i] == InputActivationFields.Actuation ? Tr("Eine ausgewählte Taste einmal bis zum gewünschten Auslösepunkt drücken und loslassen. Der größte Druck wird in Prozent ihres gespeicherten Bereichs gemessen; Loslassen übernimmt ihn für alle ausgewählten Tasten in einem Rückgängig-Schritt.",
                        "Press any selected key once to the desired actuation point, then release. Peak pressure is measured as a percentage of its saved range; release applies it to every selected key in one undo step.") :
                    Tr("Eine ausgewählte Taste aus der Ruheposition um den gewünschten Bewegungsweg drücken und loslassen. Der gemessene Weg setzt nur diese Rapid-Trigger-Option für alle ausgewählten Tasten: einen Bewegungsabstand, keinen festen Auslösepunkt.",
                        "From rest, press any selected key through the desired movement distance, then release. This sets only this Rapid Trigger option for every selected key: a movement distance, not a fixed actuation point.");
                help += "\n" + Tr("Die gespeicherten Min-/Max-Druckbereiche bleiben erhalten. Loslassen bestätigt; ein zusätzlicher Haken ist nicht nötig.",
                    "Saved min/max pressure ranges are preserved. Releasing confirms the measurement; no checkbox is needed.");
                buttons[i].AccessibleName = buttons[i].Text + " · " + (fields[i] == InputActivationFields.Actuation ? Tr("Auslösen", "Actuation") : fields[i] == InputActivationFields.Release ? Tr("Loslassen", "Release") : Tr("Erneut drücken", "Repress"));
                buttons[i].AccessibleDescription = help; keyCardTips.SetToolTip(buttons[i], help);
            }
        }
        void UpdateInputThresholdCapture()
        {
            ReaderSession input = inputThresholdCaptureReader; if (input == null) return;
            if (!Object.ReferenceEquals(input, reader) || !input.IsReading || closing || deviceDetachInProgress ||
                !SelectedKeys().SequenceEqual(inputThresholdCaptureKeys) || !Object.ReferenceEquals(history, inputThresholdCaptureHistory) ||
                !Object.ReferenceEquals(history.SnapshotToken, inputThresholdCaptureSnapshot) || !Object.ReferenceEquals(calibration, inputThresholdCaptureCalibration))
            { CancelInputThresholdCapture(); return; }
            int source; double maximum; bool complete;
            lock (inputThresholdCaptureGate)
            {
                source = inputThresholdCaptureSource;
                maximum = inputThresholdCapture == null ? 0 : inputThresholdCapture.Maximum;
                complete = inputThresholdCapture != null && inputThresholdCapture.Completed;
            }
            var fresh = input.GetUiSnapshot(MappingSession.MaximumInputAgeMilliseconds).Where(s => s.Known && !s.Stale).ToArray();
            if (source >= 0 && !fresh.Any(s => s.KeyIndex == source))
            {
                CancelInputThresholdCapture(); SetInputStatus(Tr("Messung abgebrochen: Druckwerte sind nicht mehr aktuell.", "Measurement canceled: pressure readings are no longer current.")); return;
            }
            if (source < 0) return;
            double amount = CapturedInputThreshold(sharedPressureRange.ForKey(source), maximum);
            SetInputStatus(string.Format(Tr("{0}: {1:0.##} % · Loslassen übernimmt", "{0}: {1:0.##}% · release to apply"), Label(source), amount * 100));
            if (!complete) return;
            int[] keys = inputThresholdCaptureKeys; InputActivationFields field = inputThresholdCaptureField;
            CancelInputThresholdCapture();
            Attempt(delegate {
                Commit(ApplyCapturedInputThreshold(history.Current, keys, source, field, amount)); RefreshInputEditor();
                SetInputStatus(string.Format(Tr("{0:0.##} % für {1} Tasten übernommen", "Applied {0:0.##}% to {1} keys"), amount * 100, keys.Length));
            });
        }
        static double CapturedInputThreshold(Calibration range, double maximum)
        { return Math.Max(.0001, Math.Min(1, Math.Round((maximum - range.Rest) / (range.Bottom - range.Rest) * 10000) / 10000)); }
        static Profile ApplyCapturedInputThreshold(Profile profile, int[] keys, int source, InputActivationFields field, double amount)
        {
            var value = KeyInputEditing.GetOrDefault(profile, source);
            if (field == InputActivationFields.Actuation) value.ActuationPoint = amount;
            else if (field == InputActivationFields.Release) value.ReleaseMovement = amount;
            else if (field == InputActivationFields.Press) value.PressMovement = amount;
            else throw new ArgumentOutOfRangeException("field");
            return KeyInputEditing.ApplyActivation(profile, keys, value, field);
        }
    }
}
