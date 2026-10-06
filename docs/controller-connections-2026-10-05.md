# Controller connections — 5 October 2026

**Included in 1.0.0-rc.12.** This page records the 5 October 2026 connection update and its original validation. The rc.12 Windows download includes these changes. See the [background startup guide](background-startup.md#automatic-controller-connections) for the settings and operating behavior.

## Behavior

Configured controllers with enabled mappings connect automatically once the loaded profile's input connection is reading. A first key press is not required: never-observed keys remain unavailable and controller output starts neutral. The application preference defaults to on when none has been saved, and an existing off preference is respected. An unconfirmed previous exit no longer blocks automatic connection.

Mapping, curve, calibration and profile changes preserve controllers with the same ID and controller type. Output is neutralized while settings are applied, keyboard suppression is updated, and held keys must be released before they become active again. Unchanged input sources are reused. Removing a controller or changing its type still requires disconnecting it; desired connections are then restored automatically.

Lost connections are retried. Failed attempts wait 2, 4, 8, 16 and then 30 seconds between retries. A controller lost immediately after connecting also waits before another attempt. A failed slot does not block other slots, and an internally failed session can be rebuilt on the next attempt.

Manual disconnect keeps that slot off until manual connection or the next application start. **Turn all controllers off** suspends automatic connection for the session; later settings changes do not undo it. Input learning, sleep and exit prevent new automatic attempts. Unfinished settings drafts are not applied by the automatic policy.

## Integrated source validation

After integration with the rc.11 source, the strict application build passed with warnings treated as errors. Nine synthetic suites passed **18,164 checks**:

| Suite | Passing checks |
| --- | ---: |
| Controller connection policy | 56 |
| Controller reconnect persistence | 52 |
| Mapping session | 443 |
| Multiple-controller session | 192 |
| Multiple-controller mapping integration | 78 |
| Background interface | 220 |
| Shutdown | 102 |
| Pending controller connections in the interface | 28 |
| Application interface | 16,993 |

The application interface harness passed unchanged using a shorter artifacts directory and a 120-second timeout. Its initial run encountered Windows' path-length limit in the deeply nested checkout. These checks used synthetic inputs and controllers; they did not install drivers or create real virtual devices. No new release package or game acceptance is implied by this source validation.

## Earlier local implementation validation

The local implementation built successfully with warnings treated as errors. The following checks used synthetic inputs and controllers; they did not install drivers or create real virtual devices. These results were recorded before integration with the published rc.11 source and do not describe the rc.11 download.

| Area | Passing checks |
| --- | ---: |
| Mapping session and preserved devices | 443 |
| Multiple-controller coordination and recovery | 192 |
| Connection intent and retry timing | 56 |
| Saved startup settings | 52 |
| Pending connections in the interface | 28 |
| Shutdown and sleep | 102 |
| Multiple-controller mapping integration | 78 |
| Learned input routes | 1,048 |
| Input learning | 40,895 |
| Interface, settings changes and undo/redo | 17,060 |
| Background operation, startup intent and input sources | 220 |

## Local startup check

A local application startup on **5 October 2026** connected the configured controller without manual connection and before the first pressure sample. The interface and event log confirmed the connection, and the output helper was running. This supplements the synthetic checks with evidence of automatic connection during startup. Full hardware, game and long-duration acceptance of this update has not been performed.
