using System;
using System.Collections.Generic;

namespace Tk75.Mapping
{
    // Deliberately unrelated to ControllerFrame: partial preview results must
    // never satisfy an output backend's Submit(ControllerFrame) contract.
    public sealed class PreviewSnapshot
    {
        public double LeftX, LeftY, RightX, RightY, LeftTrigger, RightTrigger;
        public ushort Buttons;
        public readonly List<string> Errors = new List<string>();
        public readonly List<int> UnavailableKeys = new List<int>();
        public readonly Dictionary<string, SignalResult> BindingResults = new Dictionary<string, SignalResult>(StringComparer.Ordinal);
        public readonly Dictionary<int, KeyInputResult> InputResults = new Dictionary<int, KeyInputResult>();
        public int AvailableBindingCount;
        public bool HasValidInput { get { return AvailableBindingCount > 0; } }

        public PreviewSnapshot Copy()
        {
            var copy = new PreviewSnapshot { LeftX = LeftX, LeftY = LeftY, RightX = RightX, RightY = RightY,
                LeftTrigger = LeftTrigger, RightTrigger = RightTrigger, Buttons = Buttons, AvailableBindingCount = AvailableBindingCount };
            copy.Errors.AddRange(Errors); copy.UnavailableKeys.AddRange(UnavailableKeys);
            foreach (var item in BindingResults)
            {
                SignalResult value = item.Value;
                copy.BindingResults.Add(item.Key, value == null ? null : new SignalResult { Normalized = value.Normalized,
                    AfterDeadzone = value.AfterDeadzone, AfterCurve = value.AfterCurve, Final = value.Final, Error = value.Error });
            }
            foreach (var item in InputResults)
            {
                KeyInputResult value = item.Value;
                copy.InputResults.Add(item.Key, value == null ? null : new KeyInputResult { Normalized = value.Normalized,
                    Active = value.Active, Allowed = value.Allowed, Error = value.Error });
            }
            return copy;
        }
    }
}
