# Learn unknown inputs

![Input learning review with two detected controls ready to apply together](images/input-learning.png)

Input learning connects a hardware control to a logical key in the current profile. That key keeps its controller outputs, curve and behavior settings. For example, an unresolved W key can listen to a supported pedal axis, while an ordinary keyboard W can be identified automatically.

This feature fills missing input assignments. It does not replace inputs that are already known. An empty controller-output list does not make a key's physical input unknown.

## Learn a selection

1. Select the keys on the keyboard. Use Ctrl+click or drag a selection rectangle for several keys.
2. Right-click the selection and choose **Learn unknown inputs…**. The count shows how many unresolved keys need an input. **Inputs already identified** means there is nothing to learn in this selection.
3. Choose the input hardware in the assistant. Already known assignments are skipped. Matching standard keyboard positions are filled automatically and marked **automatic**.
4. For each remaining key, press or move the control you want to use, then release it or return it to rest. The assistant shows the current key, progress, live value and detected input type. It advances after a complete, unambiguous gesture.
5. Check the assignment list and choose **Apply**. All accepted assignments are saved together as one undoable change. **Cancel** discards the pending assignments.

**Back**, **Skip** and **Try again** let you revise the sequence. For a wheel, latching switch or a slider that does not return to rest, use **Finish input** when it becomes available. There are no per-key confirmation checkboxes.

Controller output is stopped before learning. Reconnect the controllers when you have finished. During capture, use the assistant's buttons with the mouse so that keyboard navigation cannot accidentally become an assignment.

If two controls move together, the assistant asks you to repeat the gesture instead of choosing the stronger signal. Reusing an input already assigned in the same learning sequence is also flagged. Let the controls rest before trying again; moving only the intended axis helps with centered sticks.

## What can be recognized

| Source | Interpretation | What learning does |
| --- | --- | --- |
| Standard keyboard | Physical scan code and pressed/released state | Automatically identifies matching keyboard positions, including left/right modifiers. Other readable positions can fill unresolved keys. |
| Supported analog keyboard decoder | Physical report channel and pressure value | Uses known identities automatically; can associate a decoded but unresolved channel with a logical key. |
| Standard HID button | On/off | Records the control used for the selected key. |
| Standard HID axis, trigger or pedal | Absolute value with declared bounds | Records its resting value and intended increasing or decreasing direction. |
| Standard HID hat switch | Discrete direction and neutral | Records the chosen direction. A null hat state is kept distinct from direction zero. |
| Standard HID relative axis, wheel or dial | Signed steps | Records the intended direction; matching movement produces short pulses. |

The HID backend accepts readable standard joystick, gamepad, multi-axis and simulation-device collections with supported control descriptors. Choosing a device starts passive input reading only; the mapper does not send activation, feature or output reports to it. Its own ViGEm and USB/IP virtual-controller outputs are excluded as input sources to prevent feedback.

Input type comes from a supported decoder or the device's declared metadata. A quick analog press that happens to produce only two values is not reclassified as an on/off button. Likewise, an arbitrary sequence of numbers is not assumed to represent pressure or a turning wheel.

## Profiles, ranges and reconnection

Learned routes belong to the current profile. They retain a hashed device identity and a control identity, rather than choosing any device with the same display name. Identical devices on different collection paths remain distinct. Moving a device to another USB port, changing its firmware or changing its descriptor can therefore require learning again.

The existing analog reader keeps its original raw pressure values when they are routed to another logical key. Standard external controls use their declared bounds and learned direction to provide a normalized input to the key's existing settings. Learning does not replace pressure calibration or reduce the keyboard-wide scale to the depth reached during one gesture.

On disconnection, retained input values become unavailable. Learning stops visibly and pending assignments are not applied. Select the device again or use **Try again** to restart capture. Saved profiles only reconnect matching source identities; they do not silently adopt a replacement with a similar name. Reconnect the same device; reopen the profile if input remains unavailable after device discovery.

To remove a learned route, select its key and choose **Forget learned inputs** from the context menu. This separate action removes the selected learned routes while retaining their controller assignments and settings. Automatic identification of a known keyboard is not erased by forgetting a profile route.

## Scope and validation

The rc.9 Windows checks cover 59 offline suites, including the actual modal assistant, staged Apply/Cancel ownership, profile routing, disconnection and reconnect races. Build and package verification passed in the [recorded validation](https://github.com/SirRanjid/analog-key-mapper/actions/runs/34781600962). The new HID and Raw Input source backends have not yet completed physical hardware or game acceptance. Existing TK75 hardware observations remain tied to the older builds documented in [validation status](status.md).

Limits of this release:

- Vendor-specific analog keyboard reports and wrapping counters need a matching decoder. Generic learning does not discover an unknown binary protocol.
- A standard digital keyboard supplies on/off events, not actual pressure. Adding a learned route cannot reconstruct missing travel values.
- HID usage value arrays, unsupported usages and out-of-range scalar reports are not guessed. Not every joystick, pedal, keyboard or firmware exposes a compatible collection.
- E1/Pause events and keyboard events without a reliable physical scan code are excluded from this input backend. Fn and device-local controls may never reach Windows.
- Hardware input timing, compatibility and resource use still require device-specific validation. There is no zero-latency or universal-device claim.

Connected devices are read through events. Runtime mapping uses the saved interpretation; gesture analysis runs only while the learning assistant is active. Device discovery and continuous signal classification are not part of the normal mapping loop.

For the underlying Windows contracts, see [RAWKEYBOARD](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-rawkeyboard), [HID value capabilities](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/hidpi/ns-hidpi-_hidp_value_caps), and [HidP_GetData](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/hidpi/nf-hidpi-hidp_getdata).
