using System;
using System.Collections.Generic;
using Tk75.Mapping;

public static class LearnedInputProfilesHarness
{
    static int assertions;
    static readonly string DeviceId = new String('a', 64);
    static void Check(bool value, string message)
    { assertions++; if (!value) throw new Exception("Learned input profile: " + message); }
    static void RejectJson(string value, string message)
    {
        try { ProfileJson.Deserialize(value); }
        catch (ArgumentException) { assertions++; return; }
        throw new Exception("Learned input profile: " + message);
    }
    static LearnedKeyBinding Axis()
    {
        return new LearnedKeyBinding { KeyIndex = 17, Backend = "hid", SourceDeviceId = DeviceId,
            SourceName = "Example pedal", ControlId = "report-1:axis-2", Kind = 1,
            Minimum = 0, Maximum = 1000, Rest = 0, Active = 500, Direction = 1 };
    }
    static LearnedKeyBinding Relative()
    {
        var input = Axis(); input.Kind = 2; input.Minimum = -127; input.Maximum = 127; input.Active = 4; return input;
    }
    static LearnedKeyBinding Hat()
    {
        var input = Axis(); input.Kind = 3; input.Minimum = 0; input.Maximum = 7;
        input.Rest = 8; input.Active = 2; input.HatValue = 2; input.Direction = -1; return input;
    }
    static Profile With(LearnedKeyBinding input)
    { return new Profile { Name = "Synthetic learned profile", LearnedInputs = new List<LearnedKeyBinding> { input } }; }
    static void Reject(LearnedKeyBinding input, string message)
    {
        var profile = With(input);
        // ValidateProfile is a non-throwing collector even for malformed input.
        Check(MappingValidation.ValidateProfile(profile).Count > 0, message);
        try { ProfileJson.Serialize(profile); }
        catch (ArgumentException) { assertions++; return; }
        throw new Exception("Learned input profile: serialization accepted " + message);
    }
    static LearnedInputRoute Runtime(LearnedKeyBinding input)
    {
        return new LearnedInputRoute(input.SourceDeviceId, input.ControlId, (InputControlKind)input.Kind,
            input.Minimum, input.Maximum, input.Rest, input.Active, input.Direction, input.HatValue);
    }
    static void RoundTrip(LearnedKeyBinding input)
    {
        var original = With(input);
        string text = ProfileJson.Serialize(original);
        var roundtrip = ProfileJson.Deserialize(text);
        Check(roundtrip.LearnedInputs != null && roundtrip.LearnedInputs.Count == 1, "Learned route remains attached to its profile");
        var saved = roundtrip.LearnedInputs[0];
        Check(saved.KeyIndex == input.KeyIndex && saved.Backend == input.Backend && saved.SourceDeviceId == input.SourceDeviceId &&
            saved.ControlId == input.ControlId && saved.SourceName == input.SourceName && saved.SourceKeyIndex == input.SourceKeyIndex,
            "Source identity and logical target survive serialization");
        Check(saved.Kind == input.Kind && saved.Minimum == input.Minimum && saved.Maximum == input.Maximum &&
            saved.Rest == input.Rest && saved.Active == input.Active && saved.Direction == input.Direction && saved.HatValue == input.HatValue,
            "Source interpretation survives serialization exactly");
        Check(ProfileJson.Serialize(roundtrip) == text, "Serialization is stable after round trip");
        var route = Runtime(saved);
        Check(route.Normalize(saved.Rest) == 0, "A persisted route remains neutral at rest");
        Check(route.Normalize(saved.Active) > 0, "A persisted route activates in its recorded direction");
    }
    public static void Run()
    {
        assertions = 0;
        var original = new Profile { Name = "Before learning" };
        original.Bindings.Add(new Binding { BindingId = "output-a", KeyIndex = 17, Target = OutputTarget.LeftYPositive });
        original.Bindings[0].Processing.Curve = CurveKind.Exponential;
        original.Bindings[0].Processing.Exponent = 2.5;
        original.Inputs.Add(new KeyInputSettings { KeyIndex = 17, ActuationPoint = .15, ReleaseMovement = .03 });
        string before = ProfileJson.Serialize(original);
        Check(!before.Contains("LearnedInputs"), "Profiles without learned sources retain their previous representation");
        Check(ProfileJson.Deserialize(before).LearnedInputs == null, "Legacy profiles load without invented assignments");
        var staged = ProfileJson.Clone(original);
        staged.LearnedInputs = new List<LearnedKeyBinding> { Axis() };
        Check(ProfileJson.Serialize(original) == before, "Staging learning leaves the active profile untouched");
        Check(staged.Bindings[0].KeyIndex == 17 && staged.Bindings[0].Target == OutputTarget.LeftYPositive &&
            staged.Bindings[0].Processing.Exponent == 2.5 && staged.Inputs[0].ActuationPoint == .15 && staged.Inputs[0].ReleaseMovement == .03,
            "Assigning a source preserves logical output and pressure/curve settings");
        var cloned = ProfileJson.Clone(staged);
        cloned.LearnedInputs[0].SourceName = "Edited draft";
        Check(staged.LearnedInputs[0].SourceName == "Example pedal", "Profile clones detach learned binding records");
        Check(ProfileJson.Serialize(ProfileJson.Deserialize(before)) == before, "Cancel/undo snapshot restores exact profile data");

        RoundTrip(Axis());
        var descending = Axis(); descending.Rest = 1000; descending.Active = 400; descending.Direction = -1; RoundTrip(descending);
        var centered = Axis(); centered.Minimum = -1000; centered.Maximum = 1000; centered.Active = -250; centered.Direction = -1; RoundTrip(centered);
        var travel = Axis(); travel.Backend = "travel"; travel.SourceKeyIndex = 42; travel.ControlId = "key:42"; RoundTrip(travel);
        var binary = Axis(); binary.Backend = "keyboard"; binary.Kind = 0; binary.Maximum = binary.Active = 1; RoundTrip(binary);
        RoundTrip(Relative()); var backward = Relative(); backward.Active = -2; backward.Direction = -1; RoundTrip(backward);
        RoundTrip(Hat());

        var invalid = Axis(); invalid.KeyIndex = 256; Reject(invalid, "Out-of-range logical key is rejected");
        invalid = Axis(); invalid.KeyIndex = -1; Reject(invalid, "Negative logical key is rejected");
        invalid = Axis(); invalid.Backend = "arbitrary-bytes"; Reject(invalid, "Unknown decoder backend is rejected");
        invalid = Axis(); invalid.SourceDeviceId = new String('A', 64); Reject(invalid, "Device identity is canonical lower-case hexadecimal");
        invalid = Axis(); invalid.SourceDeviceId = "same-display-name"; Reject(invalid, "A device name is not a sufficient physical identity");
        invalid = Axis(); invalid.SourceName = " \n"; Reject(invalid, "Blank/control-character source names are rejected");
        invalid = Axis(); invalid.ControlId = new String('x', 129); Reject(invalid, "Control identifiers are bounded");
        invalid = Axis(); invalid.Kind = 4; Reject(invalid, "Unrecognized input semantics are rejected");
        invalid = Axis(); invalid.Direction = 0; Reject(invalid, "A missing selected direction is rejected");
        invalid = Axis(); invalid.Active = invalid.Rest; Reject(invalid, "No-excursion axis is rejected");
        invalid = Axis(); invalid.Direction = -1; Reject(invalid, "Axis direction must agree with the learned excursion");
        invalid = Axis(); invalid.Maximum = invalid.Minimum; Reject(invalid, "Collapsed logical range is rejected");
        invalid = Axis(); invalid.Active = 1001; Reject(invalid, "Observed activity must fit the logical range");
        invalid = Axis(); invalid.Rest = -1; Reject(invalid, "Analog rest must fit the logical range");
        invalid = Axis(); invalid.HatValue = 3; Reject(invalid, "An analog route cannot smuggle a discrete hat selection");
        invalid = Axis(); invalid.SourceKeyIndex = 42; Reject(invalid, "An external device cannot claim a physical RGB key index");
        invalid = Axis(); invalid.Backend = "travel"; Reject(invalid, "Travel source requires its physical key identity");
        invalid = Axis(); invalid.Backend = "travel"; invalid.SourceKeyIndex = 42; invalid.ControlId = "key:41"; Reject(invalid, "Travel source index and control identity must match");

        var profile = With(Axis()); profile.LearnedInputs.Add(Axis());
        Check(MappingValidation.ValidateProfile(profile).Count > 0, "A logical key cannot silently receive competing persisted sources");
        profile = With(null); Check(MappingValidation.ValidateProfile(profile).Count > 0, "Null binding entries are rejected");
        profile = new Profile { Name = "All keys", LearnedInputs = new List<LearnedKeyBinding>() };
        for (int index = 0; index < 256; index++) { var input = Axis(); input.KeyIndex = index; input.ControlId = "axis-" + index; profile.LearnedInputs.Add(input); }
        Check(MappingValidation.ValidateProfile(profile).Count == 0, "Every logical key can have one independently identified source");
        Check(ProfileJson.Deserialize(ProfileJson.Serialize(profile)).LearnedInputs.Count == 256, "The complete bounded mapping survives round trip");
        profile.LearnedInputs.Add(Axis());
        Check(MappingValidation.ValidateProfile(profile).Count > 0, "A profile cannot exceed the bounded logical-key count");

        string valid = ProfileJson.Serialize(With(Axis()));
        RejectJson(valid.Replace("\"Kind\":1", "\"Kind\":1.5"), "Fractional kind must not be coerced");
        RejectJson(valid.Replace("\"KeyIndex\":17", "\"KeyIndex\":17.5"), "Fractional logical index must not be coerced");
        RejectJson(valid.Replace("\"Direction\":1", "\"Direction\":\"1\""), "String direction must not be coerced");
        RejectJson(valid.Replace("\"Minimum\":0", "\"Minimum\":null"), "Null numeric metadata must not be silently defaulted");
        RejectJson(valid.Replace("\"Kind\":1", "\"Kind\":1,\"Kind\":1"), "Duplicate learned fields are rejected");
        RejectJson(valid.Replace("\"Kind\":1", "\"Kind\":1,\"RawOffset\":4"), "Unknown byte-decoder metadata is rejected");
        RejectJson(valid.Replace("\"Active\":500", "\"Active\":1e309"), "Non-finite JSON activity is rejected");
        RejectJson(valid.Replace("\"Rest\":0,", ""), "Required neutral metadata cannot be absent");

        // These failures must be collected rather than escaping Math.Sign as an
        // ArithmeticException, and all accepted profiles must instantiate the
        // same strict interpretation used by live controller routing.
        foreach (double malformed in new[] { Double.NaN, Double.NegativeInfinity, Double.PositiveInfinity })
        {
            invalid = Axis(); invalid.Active = malformed; Reject(invalid, "Malformed activity is reported as a validation error");
            invalid = Axis(); invalid.Rest = malformed; Reject(invalid, "Malformed rest is reported as a validation error");
            invalid = Axis(); invalid.Minimum = malformed; Reject(invalid, "Malformed minimum is rejected");
            invalid = Axis(); invalid.Maximum = malformed; Reject(invalid, "Malformed maximum is rejected");
        }
        invalid = Relative(); invalid.Rest = 1; Reject(invalid, "Relative events have neutral delta zero");
        invalid = Relative(); invalid.Active = 0; Reject(invalid, "Relative learning needs a nonzero step");
        invalid = Relative(); invalid.Direction = -1; Reject(invalid, "Relative direction agrees with its step sign");
        invalid = Relative(); invalid.Minimum = 1; Reject(invalid, "Relative logical bounds include zero");
        invalid = Hat(); invalid.Active = 3; Reject(invalid, "Hat activity equals the persisted discrete direction");
        invalid = Hat(); invalid.Rest = 2; Reject(invalid, "Hat direction differs from neutral");
        invalid = Hat(); invalid.Rest = 8.5; Reject(invalid, "Hat neutral is discrete");
        invalid = Hat(); invalid.Minimum = .5; Reject(invalid, "Hat logical minimum is discrete");
        invalid = Hat(); invalid.Maximum = 7.5; Reject(invalid, "Hat logical maximum is discrete");
        Console.WriteLine("PASS: " + assertions + " learned input profile assertions.");
    }
}
