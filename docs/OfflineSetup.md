# Offline play — current state, and what setup automation has to do

> **Update — see [docs/plans/magic-domain-replacement.md](plans/magic-domain-replacement.md).**
> The certificate hostname below (`192-168-137-1.local-ip.sh`) is stale — local-ip.sh is
> permanently dead. The certificate is now single-name for `play.skiprocking.ca` (or whatever
> `magic-domain.txt` overrides it to), renewed by `.github/workflows/renew-cert.yml`, and the
> hosts-entry fix still applies but now maps that domain to `192.168.137.1` instead of an
> IP-encoded local-ip.sh label — `deploy/setup-offline.ps1` reads the same override file.
> LAN mode is deprecated; Hotspot is the only mode `StartScreen` launches.

**Date:** 2026-08-21
**Status: working.** A match runs on this machine with no network of any kind — no internet, no
router, no Wi-Fi association. Phones join by QR and reach the game.

Background and evidence: [Decision-OfflineHosting.md](6-decisions/Decision-OfflineHosting.md).
Why Wi-Fi Direct was tried and dropped: `Findings-WiFiDirect.md` (deleted; in git history).

---

## What works now

| Piece | State |
|---|---|
| Tethering with no uplink | Works — `hotspot-control.ps1` tethers from any connection profile, not just an internet-bearing one |
| Hotspot address | `192.168.137.1`, fixed, matches the pinned certificate |
| DHCP for phones | ICS, automatic |
| DNS for phones | `relay/dns.js` on `0.0.0.0:53`, bound *before* tethering by `HotspotService` waiting on `LocalServices.ProbeDnsBound` |
| DNS for the **host** | `hosts` file entry (see below) — the host does not take DHCP from itself |
| Certificate | Cached single-name cert for `MagicDomain.HostName` (`play.skiprocking.ca`), renewed by `.github/workflows/renew-cert.yml`; no network needed to *use* it |
| Phone join | Two QRs: Wi-Fi payload, then the game URL |

### The three things that made it work

Recorded because each was a genuine blocker and none was obvious:

1. **`hotspot-control.ps1` only asked `GetInternetConnectionProfile()`**, which returns null with
   no internet — by definition, not by limitation. Tethering needs *a* profile, not a working
   one. Fixed.
2. **The host could not resolve its own relay.** Unity connects to
   `wss://192-168-137-1.local-ip.sh:8443`; offline, the host's configured resolvers are
   unreachable and there is no local answer. Fixed with a `hosts` entry.
3. **The Wi-Fi radio must be on but need not be associated.** A software-off radio fails
   tethering with the status `Unknown`, which reads exactly like "Windows cannot do this
   offline". This confounded two separate investigations.

---

## Prerequisites

What a machine needs before offline play works. All of it is now done by
[`deploy/setup-offline.ps1`](../deploy/setup-offline.ps1) — the commands are kept here because
knowing what the script does to a machine matters more than the script being convenient.

### Elevated (needs Administrator)

**1. Scheduled task — disables IP forwarding so guests are isolated**

```powershell
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument @'
-NoProfile -WindowStyle Hidden -Command "Get-NetIPAddress -AddressFamily IPv4 -IPAddress 192.168.137.* -ErrorAction SilentlyContinue | ForEach-Object { Set-NetIPInterface -InterfaceIndex $_.InterfaceIndex -AddressFamily IPv4 -Forwarding Disabled }"
'@
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -RunLevel Highest -LogonType S4U
Register-ScheduledTask -TaskName "RockSkippingDisableForwarding" -Action $action -Principal $principal -Force
```

**2. Firewall — the relay, and the DNS responder**

```powershell
New-NetFirewallRule -DisplayName "RockSkipping relay" -Direction Inbound -Protocol TCP -LocalPort 8443 -RemoteAddress LocalSubnet -Action Allow -Profile Any
New-NetFirewallRule -DisplayName "RockSkipping hotspot DNS" -Direction Inbound -Protocol UDP -LocalPort 53 -RemoteAddress 192.168.137.0/24 -Action Allow -Profile Any
```

**3. Hosts entry — so the host can resolve its own relay offline**

```powershell
Add-Content -Path "$env:SystemRoot\System32\drivers\etc\hosts" -Value "`n192.168.137.1  play.skiprocking.ca" -Encoding ASCII
```

> **The hostname is not a literal to copy.** It is whatever `MagicDomain.HostName` resolves to on
> this machine — `play.skiprocking.ca` by default, or the override in `magic-domain.txt`.
> `deploy/setup-offline.ps1` reads the same override file and is the supported way to install this
> entry; the command is shown only so the effect on the machine is legible.

The entry is safe because Hotspot mode always uses the fixed ICS address `192.168.137.1`, which is
the same answer `relay/dns.js` gives every guest — so the host and its guests resolve the name
identically. TLS validation is unaffected: the name still matches the certificate.

This is a change from the original design, where the name was an IP-encoding `local-ip.sh` label
that duplicated a public DNS answer by construction. That service is dead; the safety argument now
rests on the address being fixed rather than on the name encoding it.

### Not elevated

- `network-mode.txt` containing `Hotspot` (written by the main menu picker)

~~`npm install` in `relay/`~~ and ~~Node 20+ on PATH~~ are no longer prerequisites — `LocalServices`
now runs both automatically the first time a match starts (`NodeProvisioner`: PATH, else a
downloaded portable copy; `InstallDependencies`: run through that same node.exe rather than
requiring `npm` on PATH). A machine with neither installed still needs internet the *first* time,
to fetch Node and the two runtime dependencies; both are cached afterward.

---

## Setup automation — [`deploy/setup-offline.ps1`](../deploy/setup-offline.ps1)

**Written and verified.** One idempotent script, run once per machine, replacing everything in
the section above.

```powershell
./deploy/setup-offline.ps1 -DryRun      # inspect; needs no elevation, changes nothing
./deploy/setup-offline.ps1              # apply; needs Administrator
./deploy/setup-offline.ps1 -Uninstall   # return a test machine to clean
```

`-DryRun` deliberately requires no elevation, because it writes nothing — which makes it the
right way to check a machine before committing to changing it, and the way this script was
tested.

Its first real run found, on the development machine: a duplicate `RockSkipping relay` rule,
three obsolete spike rules, and two copies of the hosts entry — all created by hand-running the
individual commands during this investigation. That is the case idempotency exists for, and it
is not hypothetical.

### Requirements it was built to, in the order they matter

**Idempotent above all.** It will be run repeatedly during testing, on machines in every possible
half-configured state. Every step must check before acting and report `already present` rather
than duplicating. This is not hypothetical: running the hosts command twice already produced a
duplicate entry on this machine. `Register-ScheduledTask -Force` is safe; `New-NetFirewallRule`
and `Add-Content` are **not** — both happily create duplicates.

**Verify, don't assume.** After each step, confirm the result — task present and `Ready`, rules
enabled with the right port and scope, hosts entry resolving via `Resolve-DnsName`. A setup script
that reports success without checking is worth less than no script, because it converts a
detectable failure into a silent one.

**Report what it changed, and what it found already correct.** Distinguishing the two is what
makes it safe to re-run.

**Elevate deliberately, once.** Check `IsInRole(Administrator)` at the top and exit with the
re-run instruction rather than failing three steps in. Do not self-elevate silently.

**Provide `-Uninstall`.** Removes the task, the rules, and the hosts line. Test machines need to
get back to clean, and a hosts entry nobody remembers adding is exactly the kind of thing that
confuses a future debugging session.

**Clean up the spike rules.** `RockSkipping spike HTTP` and `RockSkipping spike DNS` exist on this
machine from testing and are not used by the game. `RockSkipping relay` and `RockSkipping spike
DNS` are each currently registered twice.

### Verification, for regression testing

`deploy/spike/hotspot-offline-spike.ps1` already does the whole check unattended, and should stay
the regression test rather than being reimplemented:

```
./deploy/spike/hotspot-offline-spike.ps1 -PreferNoConnectivity -EnableRadio -Dns
```

Runs **online** and still proves the offline mechanism, by deliberately tethering from a
zero-connectivity profile. No cable pulling, no phone. It reports profile selection, tethering
status, address, DHCP, and who holds `:53`.

---

## Robustness gaps to close before this is unattended

All three produce failures that name the wrong cause, which is what makes them worth fixing rather
than documenting.

**1. Stale tethering wedges the next launch, and restarting does not clear it.**
Tethering outlives the process that started it. A crashed Editor leaves the hotspot up, ICS keeps
`0.0.0.0:53`, and the next launch cannot bind the DNS responder. `HotspotService` then reports
*"The local DNS responder never bound… Restart and try again"* — advice that cannot work, because
the cause is still running. Observed twice during testing.
*Fix:* call `hotspot-control.ps1 -Action Stop` at startup in Hotspot mode, before `LocalServices`
spawns the relay.

**2. A software-off radio reports as `tethering-failed:Unknown`.**
*Fix:* check `Windows.Devices.Radios.Radio` in `hotspot-control.ps1`, return a distinct
`radio-off` error, optionally turn it on — `RequestAccessAsync` returns `Allowed` unelevated and
`SetStateAsync` needs no prompt. Both spikes already do this; it needs porting across.

**3. Certificate renewal is silent until it isn't.**
Renewal itself is a GitHub Actions cron, so it does not depend on the host being online — but
*collecting* a renewed certificate does.
`CertificateProvider` already falls back to the cache correctly, but a machine that is *always*
offline will eventually run one party past expiry with no warning. Surface days-remaining on the
join screen below some threshold, rather than discovering it at a party.

---

## Deployment — separate, and still blocked

None of the above helps a build handed to someone else. `ZeroSetupPlan.md` blocker 3 is unchanged;
blockers 1–2 are closed for the *editor* loop but the underlying gap for a **build** is the same
one:

1. ~~**Node is not bundled**~~ — `NodeProvisioner` downloads a portable copy on first use, but that
   still needs internet the first time, same as (2) below.
2. ~~**Dependencies install at runtime**~~ — no longer needs `npm` on PATH, but still needs
   internet the first time to fetch the two runtime dependencies.
3. **`relay/` and `controller/` are not in the build** — they live outside `Assets/`, so Unity
   never copies them.

A downloaded build fails at (3) before reaching any of this. The offline work is complete for
**development and local testing**; distribution is its own piece, and `setup-offline.ps1` is a
prerequisite for it rather than a replacement.

---

## Fixed alongside this: QR console spam

`JoinScreen` re-derived the join URL every frame and refetched whenever it differed from what was
on screen — but only recorded success, so a failed fetch left the condition true and retried the
next frame, logging every time.

In Hotspot mode that fires reliably at startup: the QR is fetched from `192.168.137.1`, which does
not exist until tethering finishes. A few seconds of console flood, self-healing, meaning nothing.

Now: exponential backoff from 0.5s to 5s, each **distinct** error logged once, and a single
`Debug.Log` on recovery so the console's last word is not a warning about a problem that resolved
itself.
