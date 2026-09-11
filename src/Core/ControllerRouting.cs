using System;
using System.Collections.Generic;

namespace Tk75.Mapping
{
    // Pure route selection and profile edits; no controller devices or input I/O.
    public static class ControllerRouting
    {
        public const string DefaultControllerId = "main";
        public const int MaximumControllers = 32;

        static ControllerDefinition Copy(ControllerDefinition value)
        { return new ControllerDefinition { Id = value.Id, Name = value.Name, Kind = value.Kind, RgbColor = value.RgbColor }; }

        public static List<ControllerDefinition> EffectiveControllers(Profile profile)
        {
            MappingValidation.RequireValid(profile);
            var controllers = new List<ControllerDefinition>();
            if (profile.Controllers.Count == 0)
                controllers.Add(new ControllerDefinition { Id = DefaultControllerId, Name = "Controller 1", Kind = profile.Controller });
            else foreach (ControllerDefinition controller in profile.Controllers) controllers.Add(Copy(controller));
            return controllers;
        }

        static ControllerDefinition Find(Profile profile, string id)
        {
            foreach (ControllerDefinition controller in EffectiveControllers(profile))
                if (String.Equals(controller.Id, id, StringComparison.Ordinal)) return controller;
            throw new ArgumentException("Der ausgewählte Controller fehlt.", "id");
        }

        public static Profile ForController(Profile profile, string id)
        {
            ControllerDefinition controller = Find(profile, id);
            Profile result = ProfileJson.Clone(profile);
            result.Controller = controller.Kind;
            result.Controllers = new List<ControllerDefinition> { controller };
            result.Bindings.RemoveAll(delegate(Binding binding) { return binding.ControllerId != id; });
            var needed = new HashSet<int>();
            foreach (Binding binding in result.Bindings) if (binding.Enabled) needed.Add(binding.KeyIndex);
            // Physical settings remain shared by source key. Keep both sides of
            // every needed SOCD pair so route validation stays symmetric; unrelated
            // players' inputs never enter this route's processing state.
            foreach (KeyInputSettings input in result.Inputs)
                if (needed.Contains(input.KeyIndex) && input.OppositeKeyIndex.HasValue) needed.Add(input.OppositeKeyIndex.Value);
            result.Inputs.RemoveAll(delegate(KeyInputSettings input) { return !needed.Contains(input.KeyIndex); });
            MappingValidation.RequireValid(result);
            return result;
        }

        static Profile Explicit(Profile profile)
        {
            Profile result = ProfileJson.Clone(profile);
            result.Controllers = EffectiveControllers(result);
            return result;
        }

        public static Profile Add(Profile profile, string name, ControllerKind kind)
        { return Add(profile, Guid.NewGuid().ToString("N"), name, kind); }

        public static Profile Add(Profile profile, string id, string name, ControllerKind kind)
        {
            Profile result = Explicit(profile);
            result.Controllers.Add(new ControllerDefinition { Id = id, Name = name, Kind = kind });
            MappingValidation.RequireValid(result);
            return result;
        }

        public static Profile Remove(Profile profile, string id)
        {
            Find(profile, id);
            Profile result = Explicit(profile);
            if (result.Controllers.Count == 1) throw new ArgumentException("Mindestens ein Controller muss erhalten bleiben.", "id");
            result.Controllers.RemoveAll(delegate(ControllerDefinition controller) { return controller.Id == id; });
            result.Bindings.RemoveAll(delegate(Binding binding) { return binding.ControllerId == id; });
            MappingValidation.RequireValid(result);
            return result;
        }

        public static Profile Rename(Profile profile, string id, string name)
        {
            Find(profile, id);
            Profile result = Explicit(profile);
            foreach (ControllerDefinition controller in result.Controllers) if (controller.Id == id) controller.Name = name;
            MappingValidation.RequireValid(result);
            return result;
        }

        public static Profile SetKind(Profile profile, string id, ControllerKind kind)
        {
            Find(profile, id);
            Profile result = Explicit(profile);
            foreach (ControllerDefinition controller in result.Controllers) if (controller.Id == id) controller.Kind = kind;
            if (id == DefaultControllerId) result.Controller = kind;
            MappingValidation.RequireValid(result);
            return result;
        }
        public static Profile SetRgbColor(Profile profile, string id, int? color)
        {
            Find(profile, id);
            Profile result = Explicit(profile);
            foreach (ControllerDefinition controller in result.Controllers) if (controller.Id == id) controller.RgbColor = color;
            MappingValidation.RequireValid(result);
            return result;
        }
    }
}
