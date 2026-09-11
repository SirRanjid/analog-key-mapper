using System;
using System.Collections.Generic;

namespace Tk75.Mapping
{
    public static class ProfileChangeImpact
    {
        // Keyboard suppression and lighting are owned by the app. Changing
        // only these preferences does not reset live controller output.
        public static bool RequiresControllerReset(Profile before, Profile after)
        {
            if (before == null || after == null) throw new ArgumentNullException(before == null ? "before" : "after");
            Profile comparable = ProfileJson.Clone(after);
            comparable.KeyboardSuppressionMode = before.KeyboardSuppressionMode;
            comparable.SuppressedKeyboardKeys = new List<int>(before.SuppressedKeyboardKeys);
            comparable.ModeSwitchLightingEnabled = before.ModeSwitchLightingEnabled;
            comparable.ModeSwitchRgbColor = before.ModeSwitchRgbColor;
            comparable.RgbOverrideEnabled = before.RgbOverrideEnabled;
            List<ControllerDefinition> previousControllers = ControllerRouting.EffectiveControllers(before);
            List<ControllerDefinition> nextControllers = ControllerRouting.EffectiveControllers(comparable);
            bool sameRoutes = previousControllers.Count == nextControllers.Count;
            for (int i = 0; sameRoutes && i < previousControllers.Count; i++)
                sameRoutes = previousControllers[i].Id == nextControllers[i].Id && previousControllers[i].Name == nextControllers[i].Name &&
                    previousControllers[i].Kind == nextControllers[i].Kind;
            if (sameRoutes)
            {
                // Choosing the first color can materialize the legacy implicit
                // route. Preserve that representation too when only RGB differs.
                comparable.Controllers.Clear();
                foreach (ControllerDefinition controller in before.Controllers)
                    comparable.Controllers.Add(new ControllerDefinition { Id = controller.Id, Name = controller.Name, Kind = controller.Kind, RgbColor = controller.RgbColor });
            }
            return ProfileJson.Serialize(before) != ProfileJson.Serialize(comparable);
        }
    }
}
