# Analog Key Mapper 1.0.0-rc.9

This release candidate adds guided input learning for unresolved keys. Choose a source device once, work through the selected keys, and review the results before saving them together.

## Changes

- **Learn unknown inputs…** appears in the keyboard selection's context menu. Known inputs are skipped; matching standard keyboard positions are identified automatically.
- A sequential assistant shows the current key, live input and detected type/direction. Returning controls complete on release; **Finish input** handles suitable wheels, switches and non-returning sliders. Back, skip, retry and cancel keep the process reversible.
- Standard keyboard on/off events and supported standard HID buttons, absolute axes, hats and relative controls can drive logical profile keys. Existing supported analog channels retain their raw pressure values.
- Reviewed routes are saved per profile with matching source/control identities. Applying the sequence creates one undoable change. **Forget learned inputs** removes selected profile routes separately.
- Device disconnection invalidates retained values. Own virtual-controller outputs cannot be selected as physical input sources. Normal mapping uses the saved interpretation; gesture analysis is limited to the assistant.

Existing pressure calibration, controller assignments, curves, lighting restoration and the rc.8 curve editor remain available. See the [input learning guide](input-learning.md) for the workflow and compatibility limits.

## Validation

**Complete Windows CI and checked release packaging: pending.** Targeted synthetic tests and a compilation check have been performed during implementation; they do not establish native device compatibility. The new input backends have not yet completed physical hardware or game acceptance.

This is an unsigned Windows x64 release candidate. Checksums establish file integrity, not a code signature. Vendor-specific protocols still need a supported decoder; binary keyboard events do not contain pressure. E1/Pause, unidentified scan codes, HID usage value arrays and unsupported usages are excluded.

Download artifacts will include the Windows package, a complete source package and SHA-256 checksums after release validation. The existing [build and verification instructions](building.md) apply.
