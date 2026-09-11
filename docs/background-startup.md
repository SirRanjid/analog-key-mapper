# Background startup and tray controls

**1.0.0-rc.1 · 11 September 2026.** [Current validation status](status.md).

## Open or hide the window

Opening `AnalogKeyMapper.exe` normally shows the editor. Opening the same installation again restores its existing window, including when it is hidden in the tray; it does not start another mapper instance in the same Windows session.

`AnalogKeyMapper.exe --background` starts the same WinForms application in the notification area without showing the editor or a taskbar window. A second background launch leaves the existing window as it is. Click the tray icon, or choose **Open Analog Key Mapper** from its menu, to show the editor.

Choose **Minimize to tray** in the application menu to hide the window while keeping input and connected controllers running. This saves the profile first. Closing the window with **X** exits the application; it does not hide it.

## Start with Windows

**Start with Windows · in tray** is an opt-in setting in the application and tray menus. Enabling it registers this executable with `--background` for your Windows sign-in. Disabling it removes this installation's matching entry. Keep the application at the registered location, or enable the option again after moving it.

The setting uses the current user's `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` entry named `AnalogKeyMapper`. Opening the application or its menu does not enable or repair startup automatically. It does not add a Windows service or register startup for other users.

## Reconnect controllers separately

**Reconnect controllers at startup** is a separate option and starts **off**. Enabling Windows startup alone does not connect virtual controllers. Importing a mapping profile does not enable reconnection.

With reconnection enabled, a successful normal exit remembers the current profile and connected controller slots in `data/controller-startup.json`. A separate `data/controller-startup-session.json` journal confirms that exact session after the profile was saved and normal shutdown was accepted. Startup consumes this confirmation before any automatic connection, so a failed later save cannot reactivate an older list. The option stays saved independently of the one-use controller list.

An interrupted session, unconfirmed Windows shutdown, legacy record or failed persistence check requires manual connection. A tray notification and a persistent menu notice explain the failure. No automatic retry loop is started. If the startup journal cannot be updated and verified, automatic connection stays paused for that session.

For a confirmed record, the app loads its saved local profile and waits for the keyboard to be reading pressure samples. It attempts only remembered slots with the same ID and controller type, using the normal connection checks. Missing profiles or removed or changed slots are not silently replaced.

Slots reconnect asynchronously, one at a time, while the window remains responsive. A failure stops the sequence without repeated retries. Manual connection actions and **Turn all controllers off** cancel pending startup reconnection; the latter also neutralizes connected outputs. Profile edits while waiting for the keyboard, profile changes, or input loss during the sequence cancel its remaining work. Keys held while a controller connects still require release before they can produce mapped output.

## Exit and background work

Both the window's **X** and tray **Exit** use normal shutdown: save the profile, neutralize and disconnect controllers, and request restoration of the saved keyboard lighting. Allow pending lighting operations to finish; closing can take several seconds, and recovery rules still apply if restoration cannot complete.

Windows shutdown or sign-out uses a separate three-second total cleanup budget and does not ask a save question. Controller cleanup, lighting restoration and profile saving run independently. If Windows ends the process first, lighting recovery records remain available and automatic reconnection requires manual intervention at the next start. If another application cancels Windows shutdown after cleanup has begun, the mapper finishes closing.

Hidden or minimized windows skip live UI refresh and preview calculations. Input safety checks, shortcuts, device handling and lighting maintenance remain active. The maintenance timer remains configured for **33 ms**, and active controller output retains its **4 ms** worker wait. These are scheduling settings, not measured end-to-end latency or a guarantee of zero latency, constant CPU usage or identical performance on every machine.

Disconnected slots without a visible preview sleep until a setting, preview request, connection or shutdown wakes them. They do not repeatedly copy keyboard input or calculate unused frames.

The existing Windows application-control block on the latest optimized controller output helper is separate from tray startup. That helper has not completed live validation on the development machine. Starting in the background does not bypass the block or make controller reconnection succeed; no protection-policy changes are required by these settings.

The preceding unsigned main executable (`0.1.0.3`) was blocked on normal launch on 11 September 2026 (Code Integrity event 3077). Autostart registration was therefore left disabled on the development machine. See [current validation](status.md) for the candidate's automated checks and live-test boundary. A background launch does not change the Windows trust decision.
