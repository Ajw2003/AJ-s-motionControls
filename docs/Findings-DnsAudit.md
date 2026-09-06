# Findings — DNS audit of the local-only rewrite

**Date:** 2026-09-06
**Branch:** `claude/dns-local-network-audit-eecc36`
**Question asked:** "the DNS on the Unity side seems to be broken" — audit the local-only
(Hotspot) rewrite and get the project back to a state a build can be tested from.

**Answer, in one line:** the relay's DNS responder was never broken. The *host PC* could not
resolve the name its own relay is served under, because the script that installs that hosts
entry was being skipped, and nothing anywhere checked.

---

## 1. What was actually wrong

There are two independent halves to name resolution in Hotspot mode, and they fail in
completely different ways:

| Half | Who resolves | Mechanism | State found |
|---|---|---|---|
| Phone → game | a guest's phone | the DNS responder in `relay/dns.js`, bound on `:53` | **working, verified end to end** |
| Host → game | Unity on the host PC | a hosts-file entry written by `deploy/setup-offline.ps1` | **broken** |

Unity connects to `wss://play.skiprocking.ca:8443` — `LocalServices.SecureSocketUrl`. The host
takes no DHCP from itself, so it never consults the responder running on its own `:53`. It asks
a public resolver instead.

`play.skiprocking.ca` is a real registered name. Its public record is a CNAME to Porkbun's
parking host:

```
play.skiprocking.ca  CNAME  pixie.porkbun.com  ->  207.207.210.229, 207.207.210.107
```

So a missing hosts entry does not present as "unknown host". Unity opens a TLS connection to a
stranger's web server on the internet, fails, and `RelayClient` retries there **forever** at a
4-second cap. Nothing in the code or on the TV ever says the word DNS.

### Why the entry was missing — the root cause

`HotspotService.EnsureOfflineSetup` decided whether this machine needed setup by asking one
cheap question:

```csharp
if (Get-ScheduledTask -TaskName 'RockSkippingDisableForwarding') { 'present' }
```

That was a valid proxy when the scheduled task and the hosts entry were installed together. It
stopped being one on 2026-09-03, when the magic-domain replacement changed *which* hostname the
hosts entry is for and `setup-offline.ps1` grew a captive-portal firewall rule.

On this development machine the task is present — registered in the `local-ip.sh` era and never
removed. So setup was skipped, and the machine still carried:

- two stale `192.168.137.1  192-168-137-1.local-ip.sh` hosts lines, and
- **no** `play.skiprocking.ca` line at all,
- no `RockSkipping captive portal` firewall rule,
- a cached certificate for the dead `CN=*.local-ip.sh`.

A proxy that quietly stops implying what it used to imply is the worst way for this to fail: no
error, no log line, and a symptom pointing at the network.

## 2. What was NOT wrong

Checked and confirmed healthy, so nobody re-investigates these:

- **`relay/dns.js`** — 21/21 tests pass; wire encoding, source guard, rate limiter all fine.
- **`relay/server.js`'s DNS wiring** — binds `0.0.0.0:53`, answers `play.skiprocking.ca` and all
  five captive-portal probe domains with `192.168.137.1`, NXDOMAINs everything else.
- **`/health`'s `dns.bound` field** — reports `true`, under the wire name `bound` that
  `LocalServices.HealthDns` reads (this was wrong once, fixed in `0f6e67f`; it is right now).
- **The certificate pipeline.** `renew-cert.yml` has run. The published asset is a genuine
  production Let's Encrypt certificate (`CN=YE1, O=Let's Encrypt`), SAN `play.skiprocking.ca`,
  87 days left, and both URLs return 200. The `UNTESTED:` note in `MagicDomain.cs` saying these
  URLs 404 is now out of date.
- **`Bootstrap`'s scene wiring** — `startLocalServices: 1` in `Bootstrap.unity`; the managers
  are created.

## 3. Fixes applied

| # | Fix | File |
|---|---|---|
| 1 | Setup check asks `setup-offline.ps1 -DryRun` instead of the stale scheduled-task proxy | `HotspotService.cs` |
| 2 | `-DryRun` exits **3** for "not set up yet", 0 for "already correct", 1 for a real problem | `setup-offline.ps1` |
| 3 | Host resolution is verified explicitly before tethering, and fails loudly with the real cause | `HotspotService.cs` |
| 4 | The TV shows hotspot failures — it previously only rendered `LocalServices` failures | `JoinScreen.cs` |
| 5 | `dns.bound` is polled to a deadline rather than asked once (the bind is not awaited server-side) | `HotspotService.cs` |
| 6 | Dead `*.local-ip.sh` hosts lines are cleaned up, mirroring the obsolete-firewall-rule step | `setup-offline.ps1` |
| 7 | The prerequisite check no longer calls a cert for the wrong hostname "valid" | `setup-offline.ps1` |
| 8 | `Resolve-DnsName` no longer throws under `Set-StrictMode` when the answer chain has a CNAME | `setup-offline.ps1` |
| 9 | The magic-domain reader is shared by both scripts rather than copied per script | `MagicDomain.ps1` (new) |
| 10 | Comments claiming hosting is "development only / hosts connect out to the deployed service" corrected | `Bootstrap.cs`, `LocalServices.cs`, `RelayClient.cs` |

### Item 4 is why none of this was visible

`JoinScreen.DrawFullScreen` rendered its failure panel only for
`LocalServices.State == Failed`. A hotspot that never came up leaves `LocalServices` sitting at
`Ready` — its relay really is running — so the TV showed ordinary "connecting…" progress
forever, under the hint *"Retrying automatically — check this machine's internet connection."*
while the actual cause sat unread in `HotspotService.FailureHint`.

## 4. Verification

`deploy/verify-stack.ps1` (new) proves the stack without Unity and without a phone, by doing
each thing rather than inspecting config: toolchain, certificate, **host resolution**, machine
setup, relay start with the exact environment `LocalServices` supplies, `/health` reporting
`dns.bound`, and four real DNS queries against the running responder.

Needs no elevation — binding `:53` requires no privilege on Windows, which is the premise the
whole design rests on (`deploy/spike/README.md`, question 1).

Current result on this machine: **11 passed, 1 warning, 1 failed** — the single failure being
the hosts entry, which needs one elevated `setup-offline.ps1` run to clear.

Also verified: relay `npm test` 21/21, `npm run lint` clean, and the full C# source set compiles
against Unity 6000.3.10f1's reference assemblies (out of band, with the Editor closed).

## 4b. Second root cause — found after the first fix landed

With the hosts entry applied, `verify-stack.ps1` reported 13/13 and Unity still could not
connect: `Unable to connect to the remote server`, retrying forever, **zero errors in the
console**.

`network-mode.txt` at the Unity project root said `Lan`.

In LAN mode no hotspot starts, so `192.168.137.1` never exists on the machine — but
`ObtainCertificate` hands back `MagicDomain.HostName` *regardless of mode*, because the
certificate is single-name. So `RelayClient` still targets `wss://play.skiprocking.ca:8443`,
which now resolves — via the hosts entry — to an address only the hotspot brings up. Before the
hosts entry it resolved to the parking page. Both fail identically:

- `LocalServices` reaches **Ready** (its relay genuinely is running, on `0.0.0.0:8443`)
- `HotspotService` reports **NotApplicable** ("LAN mode — no hotspot needed")
- nothing calls `Fail()`, so nothing logs an error
- `RelayClient` retries a name pointing at nobody, forever

`NetworkModeStore.Load`'s Hotspot fallback **only applies when the file is missing**. Every
machine that ran a build before 2026-09-03 still has a `network-mode.txt` saying `Lan`, written
back when that was the default, and is pinned to the dead path with nothing on screen to say
why. `StartScreen` deliberately stopped writing that file so it could not stomp a hand-edited
`Lan` — which is correct, but means the deprecation shipped a default change with no migration.

**Fixed:** `LocalServices.LaunchSequence` now refuses LAN mode up front, before a certificate is
fetched or a relay spawned, naming the file and the fix. It reports rather than rewrites — a
leftover and a deliberate hand-edit are indistinguishable from the file alone.

### Why the verifier passed anyway — and what changed

Every other check in `verify-stack.ps1` tests **Hotspot plumbing that LAN mode never uses**, so
all of it passed while the game was dead. Two additions close that:

- a network-mode check, which fails on anything but `Hotspot`
- `-ProjectRoot`, because run from a git worktree the script read the *worktree's* config while
  Unity reads the **main checkout's**. That is the second half of how a clean run coexisted with
  a broken game.

> **Worktree note.** Unity is open on `C:\Users\aj\Desktop\GameDev\RockSkipping` (branch
> `DocsRewrite`). The source fixes here live on `claude/dns-local-network-audit-eecc36` and do
> **not** affect that Editor until merged. The machine-level fixes — hosts entry, firewall
> rules, `network-mode.txt` — are machine-wide and apply immediately.

## 5. Still open — not fixed, needs a decision

- **The docs index points at deleted files.** `0b7c500` removed 39 docs (archived to `.7z`,
  then removed in `77a44f1`). `docs/MASTER-PLAN.md` still indexes ~20 of them, and code comments
  across `Assets/Scripts/Net/` cite `HostingPlan.md`, `SecurityReview.md`,
  `Findings-PhoneUnreachable.md` and others that no longer exist. They are all recoverable from
  git history. Restoring them, pruning the references, or leaving both as-is is a call for the
  owner, not a bug to fix silently.
- **`MagicDomain.cs`'s `UNTESTED:` note** claims the certificate URLs 404 because the renewal
  workflow has never run. It has run; the note is stale. Left alone only because it sits in the
  same paragraph as the seed-certificate rationale, which is still correct.
- **The seed certificate** in `StreamingAssets` is baked at packaging time by
  `deploy/fetch-seed-cert.ps1` and `installer.iss` has never been compiled. Untested path.
- **`renew-cert.yml` defaults to Let's Encrypt STAGING** on both the cron and dispatch paths. The
  currently published asset is production, so someone flipped it deliberately once — but the next
  *scheduled* run will overwrite it with a staging certificate that no phone will trust.
  `verify-stack.ps1` fails loudly if that happens, but the workflow default is a live trap.
