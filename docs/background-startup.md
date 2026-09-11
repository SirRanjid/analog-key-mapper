# Background startup and tray controls

**Unreleased source implementation · 11 September 2026.** This describes changes after the published `0.1.0-preview.2`, not features already available in that download. Publication of this update is pending.

## Open or hide the window

Opening `AnalogKeyMapper.exe` normally shows the editor. Opening the same installation again restores its existing window, including when it is hidden in the tray; it does not start another mapper instance in the same Windows session.

`AnalogKeyMapper.exe --background` starts the same WinForms application in the notification area without showing the editor or a taskbar window. A second background launch leaves the existing window as it is. Click the tray icon, or choose **Open Analog Key Mapper** from its menu, to show the editor.

Choose **Minimize to tray** in the application menu to hide the window while keeping input and connected controllers running. This saves the profile first. Closing the window with **X** exits the application; it does not hide it.

## Start with Windows

**Start with Windows · in tray** is an opt-in setting in the application and tray menus. Enabling it registers this executable with `--background` for your Windows sign-in. Disabling it removes this installation's matching entry. Keep the application at the registered location, or enable the option again after moving it.

The setting uses the current user's `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` entry named `AnalogKeyMapper`. Opening the application or its menu does not enable or repair startup automatically. It does not add a Windows service or register startup for other users.

## Reconnect controllers separately

**Reconnect controllers at startup** is a separate option and starts **off**. Enabling Windows startup alone does not connect virtual controllers. Importing a mapping profile does not enable reconnection.

With reconnection enabled, normal exit remembers the current profile and connected controller slots in `data/controller-startup.json`. At the next start, the app loads that saved local profile and waits for the keyboard to be reading pressure samples. It attempts the remembered slots that still have the same ID and controller type, using the normal connection checks. Missing profiles or removed or changed slots are not silently replaced.

Slots reconnect one at a time. A failure stops the sequence without repeated retries. **Turn all controllers off** in the tray cancels pending reconnection and neutralizes connected outputs. Profile changes or input loss during the sequence also cancel its remaining work. Keys held while a controller connects still require release before they can produce mapped output.

## Exit and background work

Both the window's **X** and tray **Exit** use normal shutdown: save the profile, neutralize and disconnect controllers, and request restoration of the saved keyboard lighting. Allow pending lighting operations to finish; closing can take several seconds, and recovery rules still apply if restoration cannot complete.

Hidden or minimized windows skip live UI refresh and preview calculations. Input safety checks, shortcuts, device handling and lighting maintenance remain active. The maintenance timer remains configured for **33 ms**, and active controller output retains its **4 ms** worker wait. These are scheduling settings, not measured end-to-end latency or a guarantee of zero latency, constant CPU usage or identical performance on every machine.

Disconnected slots without a visible preview sleep until a setting, preview request, connection or shutdown wakes them. They do not repeatedly copy keyboard input or calculate unused frames.

The existing Windows application-control block on the latest optimized controller output helper is separate from tray startup. That helper has not completed live validation on the development machine. Starting in the background does not bypass the block or make controller reconnection succeed; no protection-policy changes are required by these settings.

The new main executable (`0.1.0.3`) was also blocked on normal launch on 11 September 2026 (Code Integrity event 3077). Tray behavior passed the synthetic UI harness, including the real application message-loop entry, but this installed unsigned executable has not started successfully. Autostart registration was therefore not enabled on the development machine. Of 44 local offline suites, 43 passed; Windows blocked the remaining lighting-lifecycle harness before it ran. This is a validation limitation, not a passed lighting test.
