namespace Tk75.App
{
    internal enum TrayStatus { Starting, Disconnected, KeyboardMode, ControllerActive, Attention }

    internal static class TrayStatusPolicy
    {
        internal static TrayStatus Resolve(bool starting, bool reading, bool keyboardMode, bool controllerActive, bool attention)
        {
            if (attention) return TrayStatus.Attention;
            if (controllerActive) return keyboardMode ? TrayStatus.KeyboardMode : TrayStatus.ControllerActive;
            if (starting) return TrayStatus.Starting;
            if (reading && keyboardMode) return TrayStatus.KeyboardMode;
            return TrayStatus.Disconnected;
        }

        // Fixed, localized captions stay within NotifyIcon's 63-character limit.
        // Ready input alone never claims that a virtual controller is active.
        internal static string Tooltip(TrayStatus status, bool reading, bool controllerActive, bool english)
        {
            switch (status)
            {
                case TrayStatus.Starting:
                    return english ? "Analog Key Mapper · Connecting…" : "Analog Key Mapper · Wird verbunden…";
                case TrayStatus.ControllerActive:
                    return english ? "Analog Key Mapper · Controller active" : "Analog Key Mapper · Controller aktiv";
                case TrayStatus.KeyboardMode:
                    return controllerActive
                        ? english ? "Analog Key Mapper · Keyboard mode · Controller neutral" : "Analog Key Mapper · Tastaturmodus · Controller neutral"
                        : english ? "Analog Key Mapper · Keyboard mode · Controllers off" : "Analog Key Mapper · Tastaturmodus · Controller aus";
                case TrayStatus.Attention:
                    return english ? "Analog Key Mapper · Action needed · Open app" : "Analog Key Mapper · Aktion nötig · App öffnen";
                default:
                    return reading
                        ? english ? "Analog Key Mapper · Ready · Controllers off" : "Analog Key Mapper · Bereit · Controller aus"
                        : english ? "Analog Key Mapper · No input connection" : "Analog Key Mapper · Keine Eingabeverbindung";
            }
        }
    }
}
