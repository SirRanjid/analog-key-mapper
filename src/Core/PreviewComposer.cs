using System;
using System.Collections.Generic;

namespace Tk75.Mapping
{
    public static partial class MappingEngine
    {
        /// <summary>
        /// Calculate a display-only snapshot from current known input values.
        /// Owners must supply separate persistent preview dictionaries per route;
        /// output dictionaries must never be reused here. Missing input resets
        /// only its bindings and its SOCD pair. No unknown raw value is invented.
        /// Strict Compose, controller arming and output submission are unchanged.
        /// </summary>
        public static PreviewSnapshot ComposePreview(IDictionary<int, double> raw, IDictionary<int, Calibration> calibrations,
            Profile profile, IDictionary<string, SignalState> previewStates, IDictionary<int, KeyInputState> previewInputStates, double dtSeconds)
        {
            var snapshot = new PreviewSnapshot();
            snapshot.Errors.AddRange(MappingValidation.ValidateProfile(profile));
            if (profile != null && profile.Controllers != null && profile.Controllers.Count > 1)
                snapshot.Errors.Add("Mehrere Controller müssen vor der Vorschau einzeln zugeordnet werden.");
            if (previewStates == null) snapshot.Errors.Add("Zustandsverzeichnis für die Vorschau fehlt.");
            if (previewInputStates == null) snapshot.Errors.Add("Zustandsverzeichnis für physische Vorschautasten fehlt.");
            if (!MappingValidation.IsFinite(dtSeconds) || dtSeconds < 0) snapshot.Errors.Add("Ungültige Zeitdifferenz für die Vorschau.");
            if (snapshot.Errors.Count != 0)
            { ResetStates(previewStates); ResetInputStates(previewInputStates); return snapshot; }

            var activeIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (Binding binding in profile.Bindings) activeIds.Add(binding.BindingId);
            var obsolete = new List<string>();
            foreach (string id in previewStates.Keys) if (!activeIds.Contains(id)) obsolete.Add(id);
            foreach (string id in obsolete) previewStates.Remove(id);

            // Reuse the exact physical RT/SOCD calculation. This private scratch
            // frame has no composed axes/buttons and never leaves this method.
            var physical = new ControllerFrame();
            Inputs(raw, calibrations, profile, previewInputStates, physical);
            if (physical.Errors.Count != 0)
            {
                snapshot.Errors.AddRange(physical.Errors);
                ResetStates(previewStates); ResetInputStates(previewInputStates); return snapshot;
            }
            foreach (var item in physical.InputResults) snapshot.InputResults.Add(item.Key, item.Value);

            var unavailable = new HashSet<int>();
            foreach (var item in snapshot.InputResults) if (item.Value.Error != null) unavailable.Add(item.Key);
            // Never infer the result of a two-key policy from just one member.
            // Suppress/reset that pair while allowing unrelated current inputs.
            foreach (KeyInputSettings input in profile.Inputs)
                if (input.OppositeKeyIndex.HasValue && (unavailable.Contains(input.KeyIndex) || unavailable.Contains(input.OppositeKeyIndex.Value)))
                { unavailable.Add(input.KeyIndex); unavailable.Add(input.OppositeKeyIndex.Value); }
            foreach (int key in unavailable)
            {
                KeyInputState state;
                if (previewInputStates.TryGetValue(key, out state) && state != null) state.Reset();
                KeyInputResult result;
                if (!snapshot.InputResults.TryGetValue(key, out result))
                { result = new KeyInputResult(); snapshot.InputResults.Add(key, result); }
                if (result.Error == null) result.Error = "Eine Gegentaste hat keinen gültigen aktuellen Eingabewert.";
                result.Active = result.Allowed = false;
                snapshot.UnavailableKeys.Add(key);
                snapshot.Errors.Add("Taste " + key + ": " + result.Error);
            }
            snapshot.UnavailableKeys.Sort();

            double[] targets = new double[10];
            foreach (Binding binding in profile.Bindings)
            {
                SignalState state;
                if (!previewStates.TryGetValue(binding.BindingId, out state) || state == null)
                { state = new SignalState(); previewStates[binding.BindingId] = state; }
                if (!binding.Enabled) { state.Reset(); continue; }
                SignalResult processed;
                KeyInputResult input;
                snapshot.InputResults.TryGetValue(binding.KeyIndex, out input);
                if (input == null || input.Error != null)
                {
                    state.Reset();
                    processed = new SignalResult { Error = input == null ? "Kein aktueller Eingabewert für diese Taste." : input.Error };
                }
                else if (!input.Allowed)
                { state.Reset(); processed = new SignalResult { Normalized = input.Normalized }; }
                else processed = SignalProcessor.ProcessNormalized(input.Normalized, binding.Processing, state, dtSeconds);
                snapshot.BindingResults.Add(binding.BindingId, processed);
                if (!processed.IsValid)
                {
                    // Physical errors have already been reported once per key.
                    if (input != null && input.Error == null) snapshot.Errors.Add(binding.BindingId + ": " + processed.Error);
                    continue;
                }
                snapshot.AvailableBindingCount++;
                int target = (int)binding.Target;
                if (target < targets.Length) targets[target] = Merge(targets[target], processed.Final, profile.Aggregation);
                else if (processed.Final > 0 && processed.Final >= binding.Processing.ButtonThreshold) snapshot.Buttons |= ButtonMask(binding.Target);
            }
            snapshot.LeftX = Axis(targets[0], targets[1], profile.OpposedPolicy);
            snapshot.LeftY = Axis(targets[2], targets[3], profile.OpposedPolicy);
            snapshot.RightX = Axis(targets[4], targets[5], profile.OpposedPolicy);
            snapshot.RightY = Axis(targets[6], targets[7], profile.OpposedPolicy);
            Shape(ref snapshot.LeftX, ref snapshot.LeftY, profile.StickShape);
            Shape(ref snapshot.RightX, ref snapshot.RightY, profile.StickShape);
            snapshot.LeftTrigger = targets[8]; snapshot.RightTrigger = targets[9];
            if ((snapshot.Buttons & 3) == 3) snapshot.Buttons = (ushort)(snapshot.Buttons & ~3);
            if ((snapshot.Buttons & 12) == 12) snapshot.Buttons = (ushort)(snapshot.Buttons & ~12);
            return snapshot;
        }
    }
}
