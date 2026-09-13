using System;
using System.Collections.Generic;
using System.Linq;
using Tk75.Diagnostics;
using Tk75.Mapping;

namespace Tk75.App
{
    // A passive subscription to the already selected keyboard; never owns USB.
    public sealed class TravelLearningSource : ILearnedInputDeviceSource
    {
        readonly ReaderSession reader;
        readonly InputControlDescriptor[] controls;
        bool disposed;
        public string DeviceId { get; private set; }
        public string DisplayName { get; private set; }
        public double DisplayMaximum { get; private set; }
        public bool IsReading { get { return !disposed && reader.IsReading; } }
        public string Status { get { return reader.Status; } }
        public InputControlDescriptor[] Controls { get { return (InputControlDescriptor[])controls.Clone(); } }
        public event Action<InputControlSample> Sample;
        public TravelLearningSource(ReaderSession reader, Func<int, string> label, Func<int, bool> known, double displayMaximum = 385)
        {
            if (reader == null) throw new ArgumentNullException("reader");
            if (Double.IsNaN(displayMaximum) || Double.IsInfinity(displayMaximum) || displayMaximum <= 0 || displayMaximum > 65535) throw new ArgumentOutOfRangeException("displayMaximum");
            DisplayMaximum = displayMaximum;
            this.reader = reader; DeviceId = LearnedInputRouting.TravelDeviceId(reader);
            DisplayName = String.IsNullOrWhiteSpace(reader.Device.product) ? "Analog keyboard" : reader.Device.product;
            controls = Enumerable.Range(0, 256).Select(index => new InputControlDescriptor("key:" + index,
                label(index), InputControlKind.Absolute, 0, 65535, 0, known(index) ? (int?)index : null, 2)).ToArray();
            reader.Sample += Feed;
        }
        void Feed(TravelSample sample, double elapsed)
        {
            if (disposed) return;
            var handler = Sample;
            if (handler != null) handler(new InputControlSample("key:" + sample.KeyIndex, sample.RawValue, HidLearningSource.TimestampMilliseconds));
        }
        public bool TryGetValue(string controlId, out double value)
        {
            value = 0; int index;
            if (!IsReading || controlId == null || !controlId.StartsWith("key:", StringComparison.Ordinal) || !Int32.TryParse(controlId.Substring(4), out index) || (uint)index >= 256) return false;
            foreach (var sample in reader.GetUiSnapshot(MappingSession.MaximumInputAgeMilliseconds))
                if (sample.KeyIndex == index && sample.Known && !sample.Stale) { value = sample.RawValue; return true; }
            return false;
        }
        public void Dispose() { if (disposed) return; disposed = true; reader.Sample -= Feed; Sample = null; }
    }
}
