using System;
using Tk75.Mapping;

namespace Tk75.App
{
    // One explicitly selected collection. Silence is normal for event-based
    // devices; retained values become unknown on disposal or disconnect.
    public interface ILearnedInputDeviceSource : IDisposable
    {
        string DeviceId { get; }
        string DisplayName { get; }
        bool IsReading { get; }
        InputControlDescriptor[] Controls { get; }
        event Action<InputControlSample> Sample;
        bool TryGetValue(string controlId, out double value);
        string Status { get; }
    }
}
