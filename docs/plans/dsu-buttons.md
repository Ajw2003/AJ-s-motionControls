# Buttons and sticks over DSU

**Issue:** [#5](https://github.com/Ajw2003/AJ-s-motionControls/issues/5) · **Milestone:** 4 in [`Roadmap.md`](../2-roadmap/Roadmap.md) · **Status:** not started

## Goal
Fill the button, stick, trigger and touch fields of DSU pad data from the on-screen gamepad, so an
emulator that reads buttons from DSU gets a whole controller from one connection.

## Approach
- DSU uses a PlayStation-style layout; each phone layout needs a mapping onto it.
- Find out which emulators read buttons from DSU and which take motion only, and record it in the
  DSU systems doc once it exists. Believed, unchecked: Dolphin and Cemu yes, Ryujinx motion only.

## Done when
The scripted DSU client sees correct button bits and stick values, and Dolphin accepts the phone
as a full controller on the Windows PC.
