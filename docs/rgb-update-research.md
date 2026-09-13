# TK75 RGB updates: latency and visible interruptions

Reviewed 2026-09-13 from application source and pinned manufacturer JavaScript. No keyboard commands or hardware experiments were performed for this change.

## Implemented changes

Ordinary color transactions now verify an already known, journaled state with ten GETs: profile and settings, every one of the six picture pages, then profile and settings again. All 384 picture bytes must match, including unused LED positions and the six protected trailing bytes. Writable settings must match the expected state; the entire current settings response must remain stable across the bracket. Initial backups and uncertain-state recovery still use the existing eighteen-GET snapshot with two complete picture reads.

This removes sixteen redundant GETs per completed update while retaining both pre-write comparison and post-write confirmation. At the existing 20 ms read delay, the nominal color-only transaction changes from 940 ms to 620 ms; a mode change adds 100 ms. These are calculated settling budgets, not measured USB latency or a promise about visible LED timing. All seven picture write pages, their final marker, immutable backups, durable transaction journals, restoration guards and lease checks remain in place.

RGB settling now waits on the existing input completion rather than sleeping before servicing input. Real pressure reports are forwarded as they arrive throughout the same full 20/100 ms settling interval. A fixed monotonic deadline prevents continuous input from prolonging the wait. Quiet periods block, and cancellation is checked at least every 20 ms.

Opening the shortcut editor previously unregistered the hotkey, which made the color planner remove its marker; closing the dialog added it again. Ordinary color planning now pauses during that editor. Applying a color writes the final marker directly; cancelling causes no lighting write. Source/readiness checks and explicit restoration remain active.

## Manufacturer evidence and limits

The pinned [ry5088 base class](https://gearhub.top/v4/js/9d6437da.js) implements the full seven-page USERPIC upload at character offset 6731. Its final page carries the completion flag. Mode 25 is stored animation through `setUserGifStart`/`setUserGif` at offsets 36179/36343; it is not established as a transient per-key path. Modes 20/21/22 are music/screen effects routed through the vendor's separate native helper.

A different `sendSyncColor` path exists at offset 38706: opcode 0x0F, page number, 56-byte payload blocks and a final-block marker. `supportsLightSync` at offset 39042 requires USB and identify-response payload byte 11 equal to 1. However, the pinned [vendor application](https://gearhub.top/v4/js/index.b1e406e4.js) explicitly enables light sync only for four other company identifiers at offset 1944255; the TK75 records use `gamakay2`. The presence of the inherited method does not establish TK75 support, safe exit behavior or a readable transient framebuffer. This release does not send that command.

The independent [sharkfin protocol research](https://github.com/dniminenn/sharkfin/blob/master/docs/PROTOCOL.md#per-key-colour) describes gen2 USERPIC staging and a final flash commit based on other named firmware images. That supports retaining the complete upload, rather than guessing that unchanged pages can be skipped. It does not prove that the TK75's firmware causes the reported blink. An uninterrupted physical transition remains a hardware verification item.

Offsets above are zero-based UTF-16 character offsets in the pinned local files, not byte offsets. Base-class SHA-256: `F8F20D3B0F44144788315A1A0FBDA7ED6A0D976C116A513666AFCF63D3105158`. Application SHA-256: `2FE7FD6B84C4296C5BB56ACA6D216F61CC5EB2D77C340F45C735885A7975F287`. The original evidence manifest is `research/vendor-v4-evidence.json`.

## Validation scope

Pure regressions cover known-state mismatches at picture page boundaries and protected bytes, both profile/settings brackets, malformed responses, every pre/post read failure boundary, original-state recovery, and deterministic quiet/busy input settling. The synthetic Windows lifecycle regression checks dialog edit/cancel, a direct final-color transaction, source loss and explicit restoration against the real journaled worker. Synthetic results cannot certify firmware rendering or physical LED transitions.
