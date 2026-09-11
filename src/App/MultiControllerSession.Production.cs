using System;
using System.Collections.Generic;
using Tk75.Mapping;

namespace Tk75.App
{
    public sealed partial class MultiControllerSession
    {
        public MultiControllerSession(WorkspaceStore store) : this(Factory(store), Tk75.Output.ControllerOutputs.MaximumConnectedXboxControllers) { }
        public void SetReader(ReaderSession value) { SetInputSource(value); }
        static Func<IControllerSession> Factory(WorkspaceStore store)
        {
            if (store == null) throw new ArgumentNullException("store");
            return delegate { return new ProductionSession(store); };
        }
        sealed class ProductionSession : IControllerSession
        {
            readonly MappingSession inner;
            public ProductionSession(WorkspaceStore store) { inner = new MappingSession(store); }
            public bool Enabled { get { return inner.Enabled; } }
            public ControllerFrame Frame { get { return inner.Frame; } }
            public PreviewSnapshot Preview { get { return inner.Preview; } }
            public string Status { get { return inner.Status; } }
            public void Configure(Profile profile, IDictionary<int, Calibration> calibration) { inner.Configure(profile, calibration); }
            public void SetInputSource(object value)
            {
                if (value != null && !(value is ReaderSession)) throw new ArgumentException("A keyboard reader is required.", "value");
                inner.SetReader((ReaderSession)value);
            }
            public void Enable() { inner.Enable(); }
            public void SetKeyboardMode(bool value) { inner.SetKeyboardMode(value); }
            public void Disable(string reason) { inner.Disable(reason); }
            public void Dispose() { inner.Dispose(); }
        }
    }
}
