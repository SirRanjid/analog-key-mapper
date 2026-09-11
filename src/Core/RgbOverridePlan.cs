using System;
using System.Collections.Generic;

namespace Tk75.Mapping
{
    // Pure desired-state calculation from the full profile (before routing).
    // No hardware, original lighting state, writes or restoration are owned here.
    public static class RgbOverridePlan
    {
        static readonly int[] palette = { 0xB2A4FF, 0x79C8AF, 0xF599A6, 0xFFC65C, 0x6EC6FF, 0xE39BFF, 0xA7D86D, 0xFFA477 };

        static int ColorAt(ControllerDefinition controller, int position)
        { return controller.RgbColor ?? palette[position % palette.Length]; }

        public static int GetControllerColor(Profile profile, string controllerId)
        {
            List<ControllerDefinition> controllers = ControllerRouting.EffectiveControllers(profile);
            for (int i = 0; i < controllers.Count; i++)
                if (String.Equals(controllers[i].Id, controllerId, StringComparison.Ordinal)) return ColorAt(controllers[i], i);
            throw new ArgumentException("Der ausgewählte Controller fehlt.", "controllerId");
        }

        public static Dictionary<int, int> Build(Profile profile, IEnumerable<string> activeControllerIds, bool keyboardMode)
        { return Build(profile, activeControllerIds, keyboardMode, null, false); }

        public static Dictionary<int, int> Build(Profile profile, IEnumerable<string> activeControllerIds, bool keyboardMode,
            int? modeSwitchKeyIndex, bool modeShortcutActive)
        {
            List<ControllerDefinition> controllers = ControllerRouting.EffectiveControllers(profile);
            if (activeControllerIds == null) throw new ArgumentNullException("activeControllerIds");
            if (modeSwitchKeyIndex.HasValue && (modeSwitchKeyIndex.Value < 0 || modeSwitchKeyIndex.Value > 255))
                throw new ArgumentOutOfRangeException("modeSwitchKeyIndex");
            var result = new Dictionary<int, int>();
            if (!keyboardMode && profile.RgbOverrideEnabled)
            {
                var active = new HashSet<string>(activeControllerIds, StringComparer.Ordinal);
                // Profile order is the explicit tie-breaker. Stale/unknown active
                // IDs and disabled bindings never contribute controller colors.
                for (int position = 0; position < controllers.Count; position++)
                {
                    ControllerDefinition controller = controllers[position];
                    if (!active.Contains(controller.Id)) continue;
                    int color = ColorAt(controller, position);
                    foreach (Binding binding in profile.Bindings)
                        if (binding.Enabled && String.Equals(binding.ControllerId, controller.Id, StringComparison.Ordinal) && !result.ContainsKey(binding.KeyIndex))
                            result.Add(binding.KeyIndex, color);
                }
            }
            // Keep the functioning mode shortcut discoverable even in keyboard
            // mode or with every controller unplugged. Its explicit marker takes
            // priority over a controller assignment on the same physical key.
            if (profile.ModeSwitchLightingEnabled && profile.ModeSwitchHotkey.Enabled && modeShortcutActive && modeSwitchKeyIndex.HasValue)
                result[modeSwitchKeyIndex.Value] = profile.ModeSwitchRgbColor;
            return result;
        }
    }
}
