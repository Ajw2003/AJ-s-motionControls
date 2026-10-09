# Virtual Xbox controller through ViGEmBus

**Issue:** [#6](https://github.com/Ajw2003/AJ-s-motionControls/issues/6) · **Milestone:** 5 in [`Roadmap.md`](../2-roadmap/Roadmap.md) · **Status:** not started

## Goal
One virtual Xbox 360 controller per phone through the free ViGEmBus driver, so Windows, Steam and
any game see the on-screen buttons. Motion stays on DSU: Xbox pads have no motion sensors.

## Approach
- **Check first** that ViGEmBus (maintenance-ended in 2023) still installs and works on the Windows
  PC. If it does not, log that in `6-decisions/Decisions.md` and look at alternatives before
  building more.
- Fold the driver install into a one-time setup script, like `deploy/setup-offline.ps1`, not a
  manual step.
- Decide how Node drives it: a Node binding to ViGEmClient (native module, licence and version
  pinned) or a small helper process.

## Done when
On the Windows PC a phone appears in Windows' game controller settings as an Xbox controller and its
buttons register (screenshot).
