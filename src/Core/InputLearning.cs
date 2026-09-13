using System;
using System.Collections.Generic;

namespace Tk75.Mapping
{
    // Kinds come from a supported device descriptor/decoder, never from the
    // number of distinct values that happened to arrive during one gesture.
    public enum InputControlKind { Button, Absolute, Relative, Hat }
    public enum InputLearningCaptureState { WaitingForNeutral, Ready, Capturing, Ambiguous, Completed }
    public enum InputLearningProblem { None, CompetingControls, DirectionChanged }

    public sealed class InputControlDescriptor
    {
        public string ControlId { get; private set; }
        public string Label { get; private set; }
        public InputControlKind Kind { get; private set; }
        public double LogicalMinimum { get; private set; }
        public double LogicalMaximum { get; private set; }
        public double? Neutral { get; private set; }
        public int? KnownKeyIndex { get; private set; }
        public double? ActivityThreshold { get; private set; }

        public InputControlDescriptor(string controlId, string label, InputControlKind kind,
            double logicalMinimum, double logicalMaximum, double? neutral, int? knownKeyIndex, double? activityThreshold = null)
        {
            InputLearningValues.RequireId(controlId, "controlId");
            InputLearningValues.RequireBounds(kind, logicalMinimum, logicalMaximum);
            if (neutral.HasValue && (!InputLearningValues.Finite(neutral.Value) ||
                (kind != InputControlKind.Hat && (neutral.Value < logicalMinimum || neutral.Value > logicalMaximum)) ||
                (kind == InputControlKind.Hat && neutral.Value != Math.Truncate(neutral.Value))))
                throw new ArgumentOutOfRangeException("neutral");
            if (kind == InputControlKind.Button && neutral.HasValue && neutral.Value != logicalMinimum && neutral.Value != logicalMaximum)
                throw new ArgumentOutOfRangeException("neutral");
            if (kind == InputControlKind.Relative && neutral.HasValue && neutral.Value != 0)
                throw new ArgumentOutOfRangeException("neutral");
            if (knownKeyIndex.HasValue && (knownKeyIndex.Value < 0 || knownKeyIndex.Value > 255))
                throw new ArgumentOutOfRangeException("knownKeyIndex");
            if (activityThreshold.HasValue && (kind != InputControlKind.Absolute || !InputLearningValues.Finite(activityThreshold.Value) ||
                activityThreshold.Value <= 0 || activityThreshold.Value > logicalMaximum - logicalMinimum))
                throw new ArgumentOutOfRangeException("activityThreshold");
            ControlId = controlId; Label = String.IsNullOrWhiteSpace(label) ? controlId : label;
            Kind = kind; LogicalMinimum = logicalMinimum; LogicalMaximum = logicalMaximum;
            Neutral = neutral; KnownKeyIndex = knownKeyIndex; ActivityThreshold = activityThreshold;
        }
    }

    public sealed class InputControlSample
    {
        public string ControlId { get; private set; }
        public double Value { get; private set; }
        public long TimestampMilliseconds { get; private set; }
        public InputControlSample(string controlId, double value, long timestampMilliseconds)
        { ControlId = controlId; Value = value; TimestampMilliseconds = timestampMilliseconds; }
    }

    // Detached learned interpretation. The caller associates it with a logical
    // key and persists all reviewed results atomically. Learning is not pressure
    // calibration: Active records observed travel, while Normalize uses the
    // device's declared range, not the depth of an accidentally shallow press.
    public sealed class LearnedInputRoute
    {
        public string SourceDeviceId { get; private set; }
        public string ControlId { get; private set; }
        public InputControlKind Kind { get; private set; }
        public double LogicalMinimum { get; private set; }
        public double LogicalMaximum { get; private set; }
        public double Rest { get; private set; }
        public double Active { get; private set; }
        public int Direction { get; private set; }
        public double? HatValue { get; private set; }
        public LearnedInputRoute(string sourceDeviceId, string controlId, InputControlKind kind,
            double logicalMinimum, double logicalMaximum, double rest, double active, int direction, double? hatValue)
        {
            InputLearningValues.RequireId(sourceDeviceId, "sourceDeviceId");
            InputLearningValues.RequireId(controlId, "controlId");
            InputLearningValues.RequireBounds(kind, logicalMinimum, logicalMaximum);
            if (!InputLearningValues.Finite(rest) || !InputLearningValues.Finite(active) || direction != -1 && direction != 1)
                throw new ArgumentOutOfRangeException("direction");
            if (kind == InputControlKind.Hat)
            {
                if (rest != Math.Truncate(rest) || !hatValue.HasValue || hatValue.Value != active ||
                    active < logicalMinimum || active > logicalMaximum || active != Math.Truncate(active) || active == rest)
                    throw new ArgumentOutOfRangeException("hatValue");
            }
            else
            {
                if (hatValue.HasValue || rest < logicalMinimum || rest > logicalMaximum || active < logicalMinimum || active > logicalMaximum)
                    throw new ArgumentOutOfRangeException("active");
                if (kind == InputControlKind.Relative)
                {
                    if (rest != 0 || active == 0 || Math.Sign(active) != direction) throw new ArgumentOutOfRangeException("active");
                }
                else if (active == rest || Math.Sign(active - rest) != direction)
                    throw new ArgumentOutOfRangeException("active");
                if (kind == InputControlKind.Button &&
                    !((rest == logicalMinimum && active == logicalMaximum) || (rest == logicalMaximum && active == logicalMinimum)))
                    throw new ArgumentOutOfRangeException("active");
            }
            SourceDeviceId = sourceDeviceId; ControlId = controlId; Kind = kind;
            LogicalMinimum = logicalMinimum; LogicalMaximum = logicalMaximum;
            Rest = rest; Active = active; Direction = direction; HatValue = hatValue;
        }

        public double Normalize(double value)
        {
            if (!InputLearningValues.Finite(value)) return 0;
            if (Kind == InputControlKind.Hat) return value == HatValue.Value ? 1 : 0;
            if (value < LogicalMinimum || value > LogicalMaximum) return 0;
            if (Kind == InputControlKind.Button) return value == Active ? 1 : 0;
            // A relative value is an event, not a held pressure. The input
            // adapter must expire this pulse; retaining the last delta latches it.
            if (Kind == InputControlKind.Relative) return value != 0 && Math.Sign(value) == Direction ? 1 : 0;
            double endpoint = Direction > 0 ? LogicalMaximum : LogicalMinimum;
            double normalized = (value - Rest) / (endpoint - Rest);
            return Math.Max(0, Math.Min(1, normalized));
        }
    }

    static class InputLearningValues
    {
        internal static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
        internal static void RequireId(string value, string name)
        { if (String.IsNullOrWhiteSpace(value) || value.Length > 1024) throw new ArgumentException("A bounded device/control identifier is required.", name); }
        internal static void RequireBounds(InputControlKind kind, double minimum, double maximum)
        {
            if (!Enum.IsDefined(typeof(InputControlKind), kind) || !Finite(minimum) || !Finite(maximum) ||
                maximum <= minimum || !Finite(maximum - minimum) ||
                (kind == InputControlKind.Relative && (minimum > 0 || maximum < 0)) ||
                (kind == InputControlKind.Hat && (minimum != Math.Truncate(minimum) || maximum != Math.Truncate(maximum))))
                throw new ArgumentOutOfRangeException("logicalMaximum");
        }
    }

    // Pure, bounded single-gesture capture. The owner supplies samples and its
    // monotonic clock only while the wizard is open; there is no worker/timer.
    public sealed class InputLearningCapture
    {
        public const int MaximumControls = 4096;
        public const int QuietMilliseconds = 150;
        public const int ReleaseSettleMilliseconds = 80;
        sealed class Channel
        {
            internal InputControlDescriptor Descriptor;
            internal bool HasValue, Armed;
            internal double Value, Rest;
            internal long Timestamp = -1, QuietSince;
        }
        readonly string sourceDeviceId;
        readonly Dictionary<string, Channel> channels = new Dictionary<string, Channel>(StringComparer.Ordinal);
        readonly List<Channel> ordered = new List<Channel>();
        Channel candidate;
        long lastTime, releaseSince = -1;
        double peak;
        int direction;
        public InputLearningCaptureState State { get; private set; }
        public InputLearningProblem Problem { get; private set; }
        public LearnedInputRoute Result { get; private set; }
        public InputControlDescriptor Control { get { return candidate == null ? null : candidate.Descriptor; } }
        public bool CanFinish { get { return State == InputLearningCaptureState.Capturing && candidate != null; } }
        public double LiveValue { get { return candidate == null ? 0 : MakeRoute().Normalize(candidate.Value); } }
        public long IgnoredSampleCount { get; private set; }

        public InputLearningCapture(string sourceDeviceId, IEnumerable<InputControlDescriptor> controls,
            IEnumerable<InputControlSample> initialSamples, long timestampMilliseconds)
        {
            InputLearningValues.RequireId(sourceDeviceId, "sourceDeviceId");
            if (controls == null) throw new ArgumentNullException("controls");
            this.sourceDeviceId = sourceDeviceId;
            foreach (InputControlDescriptor descriptor in controls)
            {
                if (descriptor == null || channels.ContainsKey(descriptor.ControlId)) throw new ArgumentException("Controls must be distinct and non-null.", "controls");
                if (ordered.Count == MaximumControls) throw new ArgumentException("Too many controls.", "controls");
                var channel = new Channel { Descriptor = descriptor };
                channels.Add(descriptor.ControlId, channel); ordered.Add(channel);
            }
            if (ordered.Count == 0) throw new ArgumentException("At least one supported control is required.", "controls");
            Retry(initialSamples, timestampMilliseconds);
        }

        public void Retry(IEnumerable<InputControlSample> initialSamples, long timestampMilliseconds)
        {
            if (timestampMilliseconds < 0) throw new ArgumentOutOfRangeException("timestampMilliseconds");
            State = InputLearningCaptureState.WaitingForNeutral; Problem = InputLearningProblem.None;
            Result = null; candidate = null; peak = 0; direction = 0; releaseSince = -1;
            lastTime = timestampMilliseconds; IgnoredSampleCount = 0;
            foreach (Channel channel in ordered)
            {
                channel.Timestamp = -1; channel.Value = 0;
                channel.Armed = false; channel.QuietSince = timestampMilliseconds;
                channel.HasValue = channel.Descriptor.Kind == InputControlKind.Relative;
                channel.Rest = DefaultRest(channel.Descriptor);
            }
            if (initialSamples == null) return;
            int count = 0;
            foreach (InputControlSample sample in initialSamples)
            {
                if (++count > MaximumControls * 4) throw new ArgumentException("Initial snapshot is too large.", "initialSamples");
                Channel channel;
                if (sample == null || sample.ControlId == null || !channels.TryGetValue(sample.ControlId, out channel) ||
                    sample.TimestampMilliseconds < 0 || sample.TimestampMilliseconds > timestampMilliseconds || !Valid(channel.Descriptor, sample.Value))
                { IgnoredSampleCount++; continue; }
                // Snapshot values are current observations even when a device's
                // last event predates opening the wizard. They cannot be gestures.
                if (sample.TimestampMilliseconds <= channel.Timestamp) continue;
                channel.Timestamp = sample.TimestampMilliseconds; Initialize(channel, sample.Value);
            }
        }

        public void Feed(InputControlSample sample)
        {
            if (State == InputLearningCaptureState.Completed || State == InputLearningCaptureState.Ambiguous) return;
            Channel channel;
            if (sample == null || sample.ControlId == null || !channels.TryGetValue(sample.ControlId, out channel) ||
                sample.TimestampMilliseconds < lastTime || sample.TimestampMilliseconds < 0 ||
                sample.TimestampMilliseconds < channel.Timestamp || !Valid(channel.Descriptor, sample.Value))
            { IgnoredSampleCount++; return; }
            long now = sample.TimestampMilliseconds;
            // Arm before interpreting a new event after a quiet interval. A
            // fresh press must not be swallowed merely because no timer ran.
            AdvanceWaiting(now);
            lastTime = now; channel.Timestamp = now;
            if (!channel.HasValue)
            {
                Initialize(channel, sample.Value); channel.QuietSince = now;
                // An unknown channel cannot itself be learned from its first
                // active report. It can still make another ongoing gesture
                // ambiguous when metadata proves that two controls are active.
                if (candidate != null && (channel.Descriptor.Neutral.HasValue || channel.Descriptor.Kind == InputControlKind.Button) &&
                    Significant(channel, sample.Value)) Ambiguous(InputLearningProblem.CompetingControls);
                return;
            }
            channel.Value = sample.Value;
            if (!channel.Armed)
            {
                if (candidate != null && Significant(channel, sample.Value))
                { Ambiguous(InputLearningProblem.CompetingControls); return; }
                if (!channel.Descriptor.Neutral.HasValue && channel.Descriptor.Kind != InputControlKind.Button &&
                    channel.Descriptor.Kind != InputControlKind.Relative &&
                    Math.Abs(sample.Value - channel.Rest) > QuietTolerance(channel.Descriptor))
                { channel.Rest = sample.Value; channel.QuietSince = now; }
                if (!AtRest(channel, sample.Value) || channel.Descriptor.Kind == InputControlKind.Relative && sample.Value != 0)
                    channel.QuietSince = now;
                return;
            }
            if (State == InputLearningCaptureState.Ready)
            {
                if (!Significant(channel, sample.Value)) return;
                candidate = channel; peak = sample.Value;
                direction = Math.Sign(sample.Value - channel.Rest);
                if (channel.Descriptor.Kind == InputControlKind.Relative) direction = Math.Sign(sample.Value);
                State = InputLearningCaptureState.Capturing;
                return;
            }
            if (candidate != channel)
            {
                if (Significant(channel, sample.Value)) Ambiguous(InputLearningProblem.CompetingControls);
                return;
            }
            if (AtRest(channel, sample.Value))
            {
                if (channel.Descriptor.Kind != InputControlKind.Relative && releaseSince < 0) releaseSince = now;
                return;
            }
            releaseSince = -1;
            if (channel.Descriptor.Kind == InputControlKind.Hat)
            {
                if (sample.Value != peak) Ambiguous(InputLearningProblem.DirectionChanged);
                return;
            }
            if (Significant(channel, sample.Value) && Math.Sign(sample.Value - channel.Rest) != direction)
            { Ambiguous(InputLearningProblem.DirectionChanged); return; }
            if (Math.Abs(sample.Value - channel.Rest) > Math.Abs(peak - channel.Rest)) peak = sample.Value;
        }

        public void Advance(long timestampMilliseconds)
        {
            if (timestampMilliseconds < 0 || timestampMilliseconds < lastTime) return;
            AdvanceWaiting(timestampMilliseconds); lastTime = timestampMilliseconds;
            if (State == InputLearningCaptureState.Capturing && releaseSince >= 0 &&
                timestampMilliseconds - releaseSince >= ReleaseSettleMilliseconds) Complete();
        }

        // For a latching switch, slider that stays where it is, or relative
        // wheel/encoder. Their completion must be an explicit user action.
        public bool Finish(long timestampMilliseconds)
        {
            if (timestampMilliseconds < lastTime || timestampMilliseconds < 0) return false;
            Advance(timestampMilliseconds);
            if (CanFinish) Complete();
            return State == InputLearningCaptureState.Completed;
        }

        void AdvanceWaiting(long now)
        {
            if (State == InputLearningCaptureState.Completed || State == InputLearningCaptureState.Ambiguous) return;
            bool anyArmed = false;
            foreach (Channel channel in ordered)
            {
                // Event-based devices may never send reports for untouched
                // controls. Each channel establishes and settles its own rest;
                // absent channels cannot block a different, known idle input.
                if (!channel.HasValue) continue;
                if (!channel.Armed)
                {
                    if (channel.Descriptor.Kind != InputControlKind.Relative && !AtRest(channel, channel.Value))
                        channel.QuietSince = now;
                    else if (now - channel.QuietSince >= QuietMilliseconds) channel.Armed = true;
                }
                anyArmed |= channel.Armed;
            }
            if (State == InputLearningCaptureState.WaitingForNeutral || State == InputLearningCaptureState.Ready)
                State = anyArmed ? InputLearningCaptureState.Ready : InputLearningCaptureState.WaitingForNeutral;
        }
        void Initialize(Channel channel, double value)
        {
            channel.HasValue = true; channel.Value = value;
            if (!channel.Descriptor.Neutral.HasValue && channel.Descriptor.Kind != InputControlKind.Button && channel.Descriptor.Kind != InputControlKind.Relative)
                channel.Rest = value;
        }
        static double DefaultRest(InputControlDescriptor descriptor)
        { return descriptor.Neutral.HasValue ? descriptor.Neutral.Value : descriptor.Kind == InputControlKind.Relative ? 0 : descriptor.LogicalMinimum; }
        static double QuietTolerance(InputControlDescriptor descriptor)
        {
            if (descriptor.Kind != InputControlKind.Absolute) return 0;
            return descriptor.ActivityThreshold.HasValue ? descriptor.ActivityThreshold.Value * .25 :
                (descriptor.LogicalMaximum - descriptor.LogicalMinimum) * .005;
        }
        static bool AtRest(Channel channel, double value)
        {
            double tolerance = channel.Descriptor.Kind == InputControlKind.Absolute ? channel.Descriptor.ActivityThreshold.HasValue ?
                channel.Descriptor.ActivityThreshold.Value * .5 : (channel.Descriptor.LogicalMaximum - channel.Descriptor.LogicalMinimum) * .01 : 0;
            return Math.Abs(value - channel.Rest) <= tolerance;
        }
        static bool Significant(Channel channel, double value)
        {
            if (channel.Descriptor.Kind == InputControlKind.Relative) return value != 0;
            if (channel.Descriptor.Kind != InputControlKind.Absolute) return value != channel.Rest;
            return Math.Abs(value - channel.Rest) >= (channel.Descriptor.ActivityThreshold ??
                (channel.Descriptor.LogicalMaximum - channel.Descriptor.LogicalMinimum) * .025);
        }
        static bool Valid(InputControlDescriptor descriptor, double value)
        {
            if (!InputLearningValues.Finite(value)) return false;
            if (descriptor.Kind == InputControlKind.Hat && descriptor.Neutral.HasValue && value == descriptor.Neutral.Value) return true;
            if (value < descriptor.LogicalMinimum || value > descriptor.LogicalMaximum) return false;
            if (descriptor.Kind == InputControlKind.Button) return value == descriptor.LogicalMinimum || value == descriptor.LogicalMaximum;
            if (descriptor.Kind == InputControlKind.Hat) return value == Math.Truncate(value);
            return true;
        }
        void Ambiguous(InputLearningProblem problem)
        { State = InputLearningCaptureState.Ambiguous; Problem = problem; Result = null; releaseSince = -1; }
        LearnedInputRoute MakeRoute()
        {
            InputControlDescriptor descriptor = candidate.Descriptor;
            return new LearnedInputRoute(sourceDeviceId, descriptor.ControlId, descriptor.Kind, descriptor.LogicalMinimum,
                descriptor.LogicalMaximum, candidate.Rest, peak, direction, descriptor.Kind == InputControlKind.Hat ? (double?)peak : null);
        }
        void Complete() { Result = MakeRoute(); State = InputLearningCaptureState.Completed; }
    }
}
