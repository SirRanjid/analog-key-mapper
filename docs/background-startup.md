# Background startup and tray controls

**1.0.0-rc.10 · 14 September 2026.** [Current validation status](status.md).

## Open or hide the window

Opening `AnalogKeyMapper.exe` normally shows the editor. Opening the same installation again restores its existing window, including when it is hidden in the tray; it does not start another mapper instance in the same Windows session.

`AnalogKeyMapper.exe --background` starts the same WinForms application in the notification area without showing the editor or a taskbar window. A second background launch leaves the existing window as it is. Click the tray icon, or choose **Open Analog Key Mapper** from its menu, to show the editor.

Enable **Minimize to tray** in the application menu to make the window's **X** hide the editor while keeping input and connected controllers running. Clicking the option changes the saved checkbox without immediately hiding the window. Closing to the tray saves the profile first. The option initially starts off; when disabled, **X** exits the app.

## Read the tray status

The tray uses the official Analog Key Mapper logo with a compact badge. Hover over it to read the specific status; click it to open the editor.

| Badge | Meaning |
| --- | --- |
| Blue arc | The input connection is starting. |
| Gray minus | Controllers are off, or there is no input connection; the tooltip distinguishes these states. |
| Amber pause | Keyboard mode. Connected controllers remain neutral. |
| Green check | A controller is active in controller mode. |
| Red warning triangle | Action is needed, such as a lighting confirmation or an error. Open the app for details. |

![Official app logo with connection, controller, keyboard-mode and attention badges](images/tray-status.png)

The badge reports app state. A green check does not establish that a game has accepted the virtual controller.

## Start with Windows

**Start with Windows · in tray** is an opt-in setting in the application and tray menus. Enabling it registers this executable with `--background` for your Windows sign-in. Disabling it removes this installation's matching entry. Keep the application at the registered location, or enable the option again after moving it.

The setting uses the current user's `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` entry named `AnalogKeyMapper`. Opening the application or its menu does not enable or repair startup automatically. It does not add a Windows service or register startup for other users.

## Reconnect controllers separately

**Reconnect controllers at startup** is a separate option and starts **off**. Enabling Windows startup alone does not connect virtual controllers. Importing a mapping profile does not enable reconnection.

With reconnection enabled, a successful normal exit remembers the current profile and connected controller slots in `data/controller-startup.json`. A separate `data/controller-startup-session.json` journal confirms that exact session after the profile was saved and normal shutdown was accepted. Startup consumes this confirmation before any automatic connection, so a failed later save cannot reactivate an older list. The option stays saved independently of the one-use controller list.

An interrupted session, unconfirmed Windows shutdown, legacy record or failed persistence check requires manual connection. A tray notification and a persistent menu notice explain the failure. No automatic retry loop is started. If the startup journal cannot be updated and verified, automatic connection stays paused for that session.

For a confirmed record, the app loads its saved local profile and waits for the keyboard to be reading pressure samples. It attempts only remembered slots with the same ID and controller type, using the normal connection checks. Missing profiles or removed or changed slots are not silently replaced.

Slots reconnect asynchronously, one at a time, while the window remains responsive. A failure stops the sequence without repeated retries. Manual connection actions and **Turn all controllers off** cancel pending startup reconnection; the latter also neutralizes connected outputs. Profile edits while waiting for the keyboard, profile changes, or input loss during the sequence cancel its remaining work. Keys held while a controller connects still require release before they can produce mapped output.

## Confirm leftover key colors

The first lighting check after startup or keyboard reconnection also runs when the editor is hidden. It looks for the configured mode-switch and controller colors on their configured keys, independently of enabled lighting options, mappings and controller connections. The same color on an unrelated key prevents that color from being treated as a leftover marker.

When matching markers have known replacement colors, a **Clean up old key colors?** window shows the proposed changes. **Clean up** approves them once. **Keep unchanged**, closing the question or exiting grants no permission; lighting changes pause until the keyboard reconnects. Input and controller handling continue while the question is open.

Future automatic cleanup starts **off**. The question offers **Clean up matching color patterns automatically in future**; this permission can be revoked in either menu with **Automatically clean recognized key colors at startup**. It is saved separately from mapping profiles, Windows startup and controller reconnection. Importing a profile cannot enable it.

Only detected markers are corrected, and other colors are preserved. A compatible backup or a uniform current background must establish their replacement colors; otherwise the app leaves lighting unchanged even with automatic cleanup enabled. An identical external pattern cannot be distinguished from earlier app markers, so matching colors are not proof of their origin. See the [lighting guide](user-guide.md#startup-check-for-leftover-colors) for the full checks and recovery behavior.

## Exit and background work

Tray **Exit** always closes the app, even when **Minimize to tray** is enabled. The window's **X** also exits when that option is off. A real exit saves the profile, neutralizes and disconnects controllers, and restores the lighting baseline. Normally that is the lighting read at startup; after approved startup cleanup, it is the corrected state. A small status window reports these phases and final helper cleanup. Its progress advances when work completes. The keyboard helper retains the same baseline and attempts restoration before releasing the keyboard if the app connection is interrupted. The backup remains available if the keyboard becomes unavailable.

Windows shutdown or sign-out performs a real exit without a save question once Windows commits to ending the session. The app promptly accepts the initial query and registers a short cleanup reason with Windows. The final session response waits up to **25 seconds** while controller cleanup, lighting restoration and profile saving run independently. rc.10 waits for the actual keyboard-reader and helper cleanup to finish and includes controller removals already in progress. The keyboard helper requests the documented later shutdown order so it remains available while the editor restores lighting. Windows can show the registered reason if cleanup takes longer. An unanswered startup lighting question is dismissed without permission and does not block cleanup. If the initial shutdown query is canceled, the app stays usable. Forced termination, power loss or an unplugged keyboard can prevent cleanup; recovery records remain available and an unconfirmed shutdown requires manual controller connection at the next start.

Hidden or minimized windows skip live UI refresh and preview calculations. Input safety checks, shortcuts, device handling and lighting maintenance remain active. The maintenance timer remains configured for **33 ms**, and active controller output retains its **4 ms** worker wait. These are scheduling settings, not measured end-to-end latency or a guarantee of zero latency, constant CPU usage or identical performance on every machine.

Disconnected slots without a visible preview sleep until a setting, preview request, connection or shutdown wakes them. They do not repeatedly copy keyboard input or calculate unused frames.

Windows still decides whether the unsigned app and its helpers may run. Starting in the background does not alter that decision. The new startup-color and shutdown behavior has synthetic test coverage; physical shutdown, hardware and game acceptance of rc.10 remain open. See [current validation](status.md).
