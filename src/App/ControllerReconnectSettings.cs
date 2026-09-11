using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using Tk75.Mapping;

namespace Tk75.App
{
    // Startup preferences are separate from mappings and are never enabled by
    // importing a profile. Only a normal exit records the connected devices.
    public sealed class ControllerReconnectSettings
    {
        public bool Enabled;
        // A controller list is eligible only when the separate session journal
        // confirms this exact session's completed, successful normal exit.
        public string SessionId;
        public string ProfileFile;
        public List<ControllerDefinition> Controllers = new List<ControllerDefinition>();

        public static ControllerReconnectSettings Parse(string json)
        {
            var value = new JavaScriptSerializer { MaxJsonLength = 65536 }.Deserialize<ControllerReconnectSettings>(json);
            if (value == null || value.Controllers == null || value.Controllers.Count > ControllerRouting.MaximumControllers ||
                value.SessionId != null && !ValidSessionId(value.SessionId) ||
                value.ProfileFile != null && (value.ProfileFile.Length == 0 || value.ProfileFile != Path.GetFileName(value.ProfileFile) || value.ProfileFile.IndexOfAny(new[] { '/', '\\', ':' }) >= 0) ||
                value.Controllers.Any(c => c == null || string.IsNullOrWhiteSpace(c.Id) || c.Id.Length > 128 || !Enum.IsDefined(typeof(ControllerKind), c.Kind)) ||
                value.Controllers.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != value.Controllers.Count)
                throw new InvalidDataException("Invalid controller startup preferences.");
            return value;
        }

        public string Serialize() { return new JavaScriptSerializer().Serialize(this); }

        internal static bool ValidSessionId(string value)
        { Guid parsed; return value != null && value.Length == 32 && Guid.TryParseExact(value, "N", out parsed); }

        public string[] Targets(string profileFile, Profile profile)
        {
            if (!Enabled || !String.Equals(ProfileFile, profileFile, StringComparison.OrdinalIgnoreCase)) return new string[0];
            var current = ControllerRouting.EffectiveControllers(profile);
            return Controllers.Where(saved => current.Any(c => c.Id == saved.Id && c.Kind == saved.Kind)).Select(c => c.Id).ToArray();
        }

        public static ControllerReconnectSettings Capture(bool enabled, string profileFile, Profile profile, IEnumerable<string> activeIds)
        {
            var ids = new HashSet<string>(activeIds, StringComparer.Ordinal);
            return new ControllerReconnectSettings { Enabled = enabled, ProfileFile = profileFile,
                Controllers = enabled ? ControllerRouting.EffectiveControllers(profile).Where(c => ids.Contains(c.Id)).ToList() : new List<ControllerDefinition>() };
        }
    }
}
