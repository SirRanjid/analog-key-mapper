# Analog Key Mapper 1.0.0-rc.10

This release adds the official app logo, compact tray status indicators, and startup cleanup for recognized leftover key colors. Closing and Windows shutdown now wait for the keyboard reader's actual cleanup, including its helper's final restore and exit.

## Recognize and review leftover lighting

The first lighting read after startup or reconnect checks the configured mode-switch and controller colors, including disabled lighting options, disabled bindings and disconnected controllers. A candidate color must occur on its configured keys and nowhere else among the supported keys. An independent, uninvolved key must be available for comparison.

When the replacement colors are known, a confirmation window lists the affected keys and proposed changes. Choose **Clean up** once, or also enable **Clean up matching color patterns automatically in future**. Automatic cleanup starts off and can be disabled again from the application or tray menu. Profile imports do not grant this permission.

An externally changed uniform background is preserved. A varied background needs a matching original backup; otherwise lighting stays paused rather than guessing replacement colors. An externally identical marking pattern remains indistinguishable from an app marking, so recognition is not a guarantee of origin. If the keyboard changes while the question is open, the fresh pre-write comparison rejects the outdated proposal. An unanswered question does not delay exit or shutdown.

After cleanup, the current lighting options apply. A configured, enabled mode-switch light can therefore return while the app runs in the tray. Declining leaves lighting unchanged and pauses lighting updates until reconnect.

## Official logo and visible status

The same keycap and analog-curve logo now appears in the Windows executable, the app window and project documentation. The tray combines it with distinct shapes: a connecting arc, an off-state minus, keyboard-mode pause bars, a controller-mode check, or an attention triangle. The attention badge has a triangular border matching the other badges' outline width.

All six documentation screenshots have been refreshed from the current English interface. Startup cleanup and tray status examples are included as well. Screenshots use synthetic example data, without real connected hardware.

## Cleanup fixes

- Real exits wait for the reader worker to finish disposing its input source and keyboard helper. A short interactive stop request is no longer mistaken for completed cleanup. Windows shutdown keeps its existing bounded cleanup budget.
- Failure while cleaning a learned input source no longer skips cleanup of other sources and the primary keyboard reader.
- A delayed release from a retired controller cannot reset the newly connected session's held-key protection.
- Shutdown includes already-running asynchronous controller removals.
- Startup reconciliation journals retain the fresh observation and the derived clean baseline separately. Interrupted writes retain verifiable recovery records; closing cannot restore the leftover markers as the original lighting.

## Validation and downloads

The release repository contains **62 offline suites**. The targeted startup, recovery, background and shutdown runs passed **1,765 assertions** before packaging. Both dialog languages were visually reviewed, and the optimized x64 application compiled with warnings treated as errors. The release is built and packaged by the [Windows validation workflow](https://github.com/SirRanjid/analog-key-mapper/actions/workflows/build.yml), with checksums tying the Windows binaries to their source package.

These are synthetic checks. This release has no new physical shutdown/reboot, keyboard/controller or game acceptance claim. A forced shutdown, unplugged device or expired cleanup deadline can still prevent restoration; the recovery record is retained.

The Windows download and complete source ZIP are available on the [rc.10 release page](https://github.com/SirRanjid/analog-key-mapper/releases/tag/v1.0.0-rc.10). This remains an unsigned release candidate. See [build and update instructions](building.md), [background startup](background-startup.md), and [compatibility and validation](status.md).
