using System;
using System.IO;
using Tk75.Mapping;

namespace Tk75.Output
{
    // The requested Windows device type is part of the profile, independent of
    // labels. Never substitute an Xbox/DS4 device for a requested DualSense.
    public static class ControllerOutputs
    {
        // The neutral startup/removal probe passed on the installed USB/IP
        // driver. This first runtime retains exclusive, single-device ownership.
        public const int MaximumConnectedXboxControllers = 1;
        static string XboxHelper { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ViiperOutputHost.exe"); } }
        public static string AvailabilityError(ControllerKind kind)
        {
            switch (kind)
            {
                case ControllerKind.Xbox360: return File.Exists(XboxHelper) ? null : "Das Xbox-Ausgabeprogramm fehlt. Bitte die App vollständig aktualisieren.";
                case ControllerKind.DualSense:
                    return "PS5-Ausgabe noch nicht bereit: Der DualSense-Ausgabeweg benötigt den freigegebenen USB/IP-Treiber und einen bestandenen Windows-HID-Start-/Abschalttest.";
                default: return "Unbekannter Controllertyp.";
            }
        }
        public static IControllerOutput Create(ControllerKind kind)
        {
            string error = AvailabilityError(kind);
            if (error != null) throw new InvalidOperationException(error);
            if (kind == ControllerKind.Xbox360) return new IsolatedOutput(XboxHelper, "--xbox-runtime-host", 15000, 250, 3500, 3500);
            // No helper is launched until the DualSense readback/cleanup path
            // has passed real device acceptance. A future release opens only
            // its explicitly selected --dualsense-host path here.
            throw new InvalidOperationException("Der ausgewählte Controller-Ausgabeweg ist noch nicht freigegeben.");
        }
    }
}
