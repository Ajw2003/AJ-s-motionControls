# Finding — phone cannot reach the relay after Phase 2

**Date:** 2026-08-20
**Symptom:** Unity logs look correct, the relay starts, the QR scans — and the phone shows
*"this site can't be reached"*.
**Status:** Root cause identified. **Nothing fixed yet** — this document is the record, and the
two code changes below are deliberately not implemented.

---

## Root cause: the firewall, on an interface with no network category

Identical in shape to the failure the hotspot spike hit, now on the Wi-Fi adapter instead of the
hotspot adapter. It is invisible from the host side, which is why the logs look healthy.

Evidence, all gathered on the machine while the symptom was live:

```
Get-NetConnectionProfile        ->  only "Ethernet 4" (Private). Wi-Fi 2 is ABSENT.
node inbound firewall rules     ->  "Node.js JavaScript Runtime"  Allow  Private   (x2)
rules mentioning port 8443      ->  none at all
netstat                         ->  TCP 0.0.0.0:8443 LISTENING (relay, pid 20724)
                                    TCP 10.0.0.213:8443 ESTABLISHED (Unity, pid 5016)
```

The chain:

1. The phone resolves `10-0-0-213.local-ip.sh` to **10.0.0.213**.
2. That address belongs to **Wi-Fi 2**.
3. **Wi-Fi 2 does not appear in `Get-NetConnectionProfile`**, so it has no network category, and
   Windows treats an uncategorised interface as **Public**.
4. Node's only inbound rules are scoped to **Private**.
5. Inbound packets on 8443 are therefore **dropped**, silently, with nothing logged anywhere.

The relay is listening correctly on `0.0.0.0:8443` and Unity connects to it over the same
address — but that connection is host-local and never crosses the firewall, which is exactly why
everything looks fine from the PC.

### The fix

An inbound rule that is **not** scoped to `Private`. Needs an elevated shell:

```powershell
New-NetFirewallRule -DisplayName "RockSkipping relay" -Direction Inbound -Protocol TCP -LocalPort 8443 -RemoteAddress LocalSubnet -Action Allow -Profile Any
```

`-Profile Any` is the load-bearing part. `-RemoteAddress LocalSubnet` keeps it off the internet.

To remove it again:

```powershell
Remove-NetFirewallRule -DisplayName "RockSkipping relay"
```

**This is Phase 5 arriving early.** The installer was always going to add this rule; the finding
is that it is required for **LAN mode too**, not only for the hotspot. `ZeroSetupPlan.md` Phase 5
currently scopes its rules to `192.168.137.0/24` (the hotspot subnet) — that is too narrow, and
would leave ordinary LAN play broken in exactly this way. **Phase 5 needs a second rule for the
LAN case, or `LocalSubnet` scoping instead.**

---

## What this confirms about Phase 2

Worth recording, because the symptom made it look like the new code had failed. It had not:

- The certificate provider ran and cached to
  `AppData/LocalLow/<company>/RockSkipping/local-https/` — `server.pem` and `server.key`, both
  written 18:36. **The new cache location works**, and it is outside `%TEMP%`, which is what
  cleared the previous copy.
- `relay-env.txt` is gone, so the provider's values are genuinely in use rather than being
  shadowed by the old file.
- The relay started with TLS on the port the provider chose.
- Unity connected to it.

Every part of Phase 2 that was built is working. The break is entirely inbound-path.

---

## Two code issues found while diagnosing — not fixed

### 1. Dual-homed machines will fail to start

`CertificateProvider.ResolveLanAddress` refuses to choose when more than one non-virtual
interface has a default gateway. This machine currently has two:

```
Ethernet 4   10.0.0.212
Wi-Fi 2      10.0.0.213
```

A docked laptop with Ethernet and Wi-Fi both up is an ordinary setup, not an ambiguous one — and
here both addresses are on the same subnet, so **either would reach the phone**. Refusing to
start is a worse failure than picking one.

Inherited from `Resolve-LanAddress` in the PowerShell script, where refusing was reasonable
because the operator could immediately re-run with `-Ip`. A player cannot.

**Proposed fix, not applied.** Ask the routing table which address Windows would actually use,
and let that break the tie:

```csharp
using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
probe.Connect("203.0.113.1", 9);          // sends nothing; only fixes the route
var preferred = (probe.LocalEndPoint as IPEndPoint)?.Address.ToString();
```

Connecting a UDP socket transmits no packets — it only resolves the route — so this asks the
question directly. .NET does not expose interface metrics fully enough to rank interfaces any
other way. Keep the hard failure for the case where the preferred route matches none of the
candidates, and keep the existing manual override.

*(TEST-NET-3 `203.0.113.0/24` is reserved for documentation and is never contacted.)*

### 2. `RelayClient` still reads `relay-url.txt` rather than the derived URL

This is **Phase 3.1**, known outstanding, but it matters more than its position in the plan
suggests: the join URL and QR come from `relay-url.txt`, while the certificate and relay port
now come from `CertificateProvider`. **Nothing keeps those two in agreement.**

It happens to work on this machine only because the stale `relay-url.txt`
(`wss://10-0-0-213.local-ip.sh:8443`, written 2026-08-13) still matches the Wi-Fi address. If
DHCP moves the machine, the QR will point somewhere the relay is not, and the symptom will look
exactly like this firewall one.

Deleting `relay-url.txt` is **not** a workaround today — `RelayClient.relayUrl` still falls back
to the dead `wss://rockskipping-relay.fly.dev`.

---

## Amendment — Phase 3.1 needs a design decision, not just a wire-up

**Date:** 2026-08-20. Found while verifying this document's logic against the actual code
before implementing anything.

The fix as written ("makes the QR derive from the same source as the certificate") is
underspecified in a way that matters: `RelayClient.Awake()` resolves and caches `RelayUrl`
and `PublicOrigin` **synchronously**, but `LocalServices` does not begin obtaining the
certificate until its own `Start()` coroutine, which Unity always runs after every object's
`Awake()` has completed. A naive "read the certificate-derived address in `RelayClient`"
would read a value that does not exist yet — even on the fast, cached-certificate path —
and would silently reproduce this exact class of bug (right code, wrong timing) in a new
place.

Two ways to resolve it:

- **A.** `RelayClient.Awake()` checks `LocalServices.HasInstance`. Absent (the normal,
  deployed-service path most players are on) → behave exactly as today, no change. Present
  → defer resolving `RelayUrl`/`PublicOrigin`, and defer the first `ConnectAsync()`, until
  `LocalServices.StateChanged` reports `Ready` (then use `SecureSocketUrl`) or `Failed`/HTTPS
  disabled (then fall back to `relay-url.txt`/the configured default).
- **B.** Have `LocalServices` write `relay-url.txt` itself once the certificate resolves, so
  `RelayClient` keeps its existing file-read mechanism as the only source of truth. This still
  needs the same Ready-gated deferral underneath — it just moves the coordination into a file
  instead of an event — so it does not actually avoid the ordering problem, only relocates it.

Leaning toward **A**: it only changes behaviour on the local/dev/zero-setup path where
`LocalServices` is active, and leaves the default path untouched. Not implemented — needs a
decision before either is built.

## Order to take these

1. **Firewall rule** — unblocks testing immediately, and confirms the diagnosis.
2. **Phase 3.1** — makes the QR derive from the same source as the certificate. Without it the
   system has two sources of truth for one address.
3. **Dual-homed selection** — not urgent while the manual override exists, but it will fail for
   the first person who tries this on a docked laptop.
4. **Widen Phase 5's firewall scoping** in `ZeroSetupPlan.md` to cover LAN mode, not only the
   hotspot subnet.
