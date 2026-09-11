# Archive

**Nothing in this folder describes current behaviour. Do not build against it.**

These documents were correct when written. They are kept because the *reasoning* in them is often
the only record of why something is the way it is — and because deleting documents that other
documents point at is exactly what broke this project's link graph in twenty places
(see [`../Findings-DocumentationAudit.md`](../Findings-DocumentationAudit.md)).

If you need current behaviour, start at [`../README.md`](../README.md).

## What is here and why it is inert

| Doc | Was | Why it is archived |
|---|---|---|
| [`MagicDnsFallback.md`](MagicDnsFallback.md) | A design for recreating `local-ip.sh` ourselves on an owned domain, as insurance if it ever disappeared | It disappeared. The answer taken was different — a single-name certificate on `play.skiprocking.ca`, no VPS nameserver. Never built |
| [`Findings-DirectTransportFlapping.md`](Findings-DirectTransportFlapping.md) | Root-caused `[PeerLink] DIRECT` log spam to one phone re-joining once a second, not a flapping data channel | `PeerLink` no longer exists. The *shape* of the bug — a close code indistinguishable from the host vanishing — is still worth knowing; it is in [`../systems/Networking.md`](../systems/Networking.md) |
| [`Findings-AimPreviewAfterRejoin.md`](Findings-AimPreviewAfterRejoin.md) | The aim preview stayed hidden for the rest of a session after a rejoin, because the phone restarts its sequence number at zero | Fixed in `f3e37cc` |
| [`Findings-DnsAudit.md`](Findings-DnsAudit.md) | "The DNS is broken" — it was not. The host could not resolve the name its own relay is served under, because the script installing the hosts entry was being skipped and nothing checked | Fixed, and both halves of name resolution are now gated separately |
| [`HANDOFF-join-failure-reduction.md`](HANDOFF-join-failure-reduction.md) | Session handoff for the join-failure work | Self-declared CLOSED. Its "traps" list was the only written record of several hard-won lessons; those are now in the tier-4 system docs |
| [`plans/join-failure-reduction.md`](plans/join-failure-reduction.md) | Ten rounds of hardware testing against join drop-off, 1,013 lines, including conclusions later proven wrong | CLOSED. **Still the authority on why the captive portal was abandoned** — read its table before reopening that on a hunch |
| [`plans/remove-direct-transport.md`](plans/remove-direct-transport.md) | Plan to delete the WebRTC DataChannel transport | Executed. `PeerLink.cs` and `com.unity.webrtc` are gone |
| [`plans/seamless-rejoin.md`](plans/seamless-rejoin.md) | One scan per phone, ever | Shipped (PR #8) |
| [`plans/seamless-rejoin-round2.md`](plans/seamless-rejoin-round2.md) | Fixing what round 1 got wrong — a rejoin screen that gated naming | Shipped (PR #9) |
| [`plans/network-mode-selection.md`](plans/network-mode-selection.md) | The LAN vs Hotspot picker | The picker was subsequently deleted; Hotspot is the only mode. Its elevated-setup and DNS-race sections were absorbed into [`../systems/HostingAndCertificates.md`](../systems/HostingAndCertificates.md) |

## Two things in here that are still worth reading

Not because they describe current behaviour — they do not — but because nothing replaced the
argument they make:

1. **`plans/join-failure-reduction.md`'s exclusion table.** Ten rounds of measurement excluded
   every host-side cause of the Android captive-portal failure by direct evidence. Reopening it
   on a hunch would repeat all of it.
2. **The habit these docs share of saying what a measurement *cannot* show.** It is why a wrong
   conclusion was caught twice mid-investigation rather than shipped.

## Cross-references in here are stale by definition

Links inside these documents may point at files that no longer exist. That is expected and is not
worth fixing — repairing links in an archived document only makes it look live.
