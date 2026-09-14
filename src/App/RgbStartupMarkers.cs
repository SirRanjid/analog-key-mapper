using System;
using System.Collections.Generic;
using System.IO;
using Tk75.Diagnostics;

namespace Tk75.App
{
    public sealed class RgbStartupMarkerDecision
    {
        readonly int[] markerKeys;
        public bool HasMarkers { get { return markerKeys.Length != 0; } }
        public bool CanRepair { get { return Repaired != null; } }
        public int[] MarkerKeys { get { return (int[])markerKeys.Clone(); } }
        public Tk75RgbSnapshot Repaired { get; private set; }
        public string Reason { get; private set; }
        internal RgbStartupMarkerDecision(int[] keys, Tk75RgbSnapshot repaired, string reason)
        { markerKeys = keys; Repaired = repaired; Reason = reason; }
    }

    // Pure startup inspection. The caller supplies a fresh stable snapshot and
    // physical positions from configuration, including disabled marker options.
    // A color match is evidence of a marker, never proof of its original color.
    public static class RgbStartupMarkers
    {
        public static RgbStartupMarkerDecision Assess(Tk75RgbSnapshot current,
            IDictionary<int, HashSet<int>> configuredKeysByColor, Tk75RgbSnapshot trustedOriginal = null)
        {
            if (current == null) throw new ArgumentNullException("current");
            if (configuredKeysByColor == null) throw new ArgumentNullException("configuredKeysByColor");
            if (configuredKeysByColor.Count > 256) throw new InvalidDataException("Too many configured marker colors.");

            byte[] settings = current.RawSettings;
            // USERPIC stores artwork even when a solid color or another effect
            // is selected. Only our visible layer can contain active leftovers.
            if (current.Layer != 4 || settings[1] != 13 || settings[4] != 0x40 || settings[3] == 0)
                return None("No visible application light picture is selected.");

            int[] supported = Tk75RgbProtocol.GetSupportedKeyIndices(current.ModelId);
            var supportedSet = new HashSet<int>(supported);
            var candidates = new Dictionary<int, HashSet<int>>();
            var configuredKeys = new HashSet<int>();
            int colorCount = 0;
            foreach (KeyValuePair<int, HashSet<int>> entry in configuredKeysByColor)
            {
                if (++colorCount > 256 || entry.Key < 0 || entry.Key > 0xffffff || entry.Value == null || entry.Value.Count > 128)
                    throw new InvalidDataException("Invalid configured marker color or key positions.");
                var allowed = new HashSet<int>();
                foreach (int key in entry.Value)
                    if (supportedSet.Contains(key)) { allowed.Add(key); configuredKeys.Add(key); }
                if (allowed.Count != 0) candidates.Add(entry.Key, allowed);
            }
            // Require a genuinely uninvolved comparison key. A configuration
            // claiming every LED cannot establish that the background differs.
            if (configuredKeys.Count == 0 || configuredKeys.Count == supported.Length)
                return None("No independent keyboard keys are available for comparison.");

            byte[] picture = current.Picture;
            var markers = new HashSet<int>();
            foreach (KeyValuePair<int, HashSet<int>> entry in candidates)
            {
                bool appearsElsewhere = false;
                var matching = new List<int>();
                foreach (int key in supported)
                    if (Color(picture, key) == entry.Key)
                    {
                        if (!entry.Value.Contains(key)) { appearsElsewhere = true; break; }
                        matching.Add(key);
                    }
                if (!appearsElsewhere) foreach (int key in matching) markers.Add(key);
            }
            if (markers.Count == 0) return None("Configured marker colors are absent or also appear on other keys.");

            int[] targets = new int[markers.Count]; markers.CopyTo(targets); Array.Sort(targets);
            var repairs = new Dictionary<int, int>();
            byte[] trustedBackground;
            if (TryTrustedBackground(current, trustedOriginal, supported, markers, out trustedBackground))
            {
                foreach (int key in targets) repairs.Add(key, Color(trustedBackground, key));
            }
            else
            {
                int? uniform = null;
                foreach (int key in supported)
                    if (!markers.Contains(key))
                    {
                        int color = Color(picture, key);
                        if (!uniform.HasValue) uniform = color;
                        else if (uniform.Value != color)
                            return new RgbStartupMarkerDecision(targets, null,
                                "Matching markers were found, but their original colors cannot be inferred from the varied keyboard background.");
                    }
                if (!uniform.HasValue) return new RgbStartupMarkerDecision(targets, null, "No unchanged keyboard background is available.");
                foreach (int key in targets) repairs.Add(key, uniform.Value);
            }

            // Preserve the freshly observed overall color, every unrelated LED,
            // every setting, unknown positions, and trailing picture bytes.
            var repaired = new Tk75RgbSnapshot(current.ModelId, current.Profile, current.Layer,
                settings, Tk75RgbProtocol.Overlay(current.ModelId, picture, repairs));
            return new RgbStartupMarkerDecision(targets, repaired, null);
        }

        static bool TryTrustedBackground(Tk75RgbSnapshot current, Tk75RgbSnapshot original,
            int[] supported, HashSet<int> markers, out byte[] background)
        {
            background = null;
            if (original == null || current.ModelId != original.ModelId || current.Profile != original.Profile || current.Layer != original.Layer)
                return false;
            byte[] currentSettings = current.RawSettings, originalSettings = original.RawSettings;
            // Speed and brightness survive our overlay, except that a dark
            // baseline may have been made visible at the standard brightness.
            if (currentSettings[2] != originalSettings[2] ||
                currentSettings[3] != originalSettings[3] && !(originalSettings[3] == 0 && currentSettings[3] == 4)) return false;
            try { background = Tk75RgbProtocol.OverlayVisibleLighting(original, new Dictionary<int, int>()); }
            catch (InvalidDataException) { return false; }
            byte[] picture = current.Picture;
            foreach (int key in supported)
                if (!markers.Contains(key) && Color(picture, key) != Color(background, key)) return false;
            // Unknown slots cannot be explained by app markers either.
            var known = new HashSet<int>(supported);
            for (int i = 0; i < picture.Length; i++)
                if (!known.Contains(i / 3) && picture[i] != background[i]) return false;
            return true;
        }
        static int Color(byte[] picture, int key)
        { return (picture[key * 3] << 16) | (picture[key * 3 + 1] << 8) | picture[key * 3 + 2]; }
        static RgbStartupMarkerDecision None(string reason)
        { return new RgbStartupMarkerDecision(new int[0], null, reason); }
    }
}
