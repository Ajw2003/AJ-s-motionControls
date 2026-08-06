# Running the WebRTC spike

> **Already run, and it passed** — Unity 6000.3.10f1 + Android Chrome, same Wi-Fi, winning pair
> `local=host remote=host`, no STUN. Phase 2 is unblocked; see [HostingPlan.md](../HostingPlan.md) §5.
> Keep this document for re-testing on a different phone, browser or network — the one case
> still unexercised is a browser that obfuscates its candidates as `.local`.

Answers one question, and nothing else: **can a Unity peer reach a phone browser over the LAN
when the browser refuses to tell it an IP address?** Background: [HostingPlan.md](../HostingPlan.md) §5.

If it passes, Phase 2 is viable and gameplay stops routing through the Fly service. If it
fails, Phase 1 keeps working and Phase 2 needs rethinking. Either result is worth the 15
minutes.

---

## What you need in front of you

| # | Thing | Notes |
|---|---|---|
| 1 | **This PC** | Unity editor + a terminal. Currently `10.0.0.213` on `Wi-Fi 2`. |
| 2 | **An Android phone** | Must be on the **same Wi-Fi** as the PC. Not guest Wi-Fi, not mobile data. |

Already checked and fine on this machine, so ignore them unless something misbehaves: the
network profile is **Private**, and Unity `6000.3.10f1` has inbound firewall Allow rules on the
Private profile. A blocked firewall would produce a false failure that looks exactly like a
real one.

---

## Step 0 — Pre-flight: prove the phone can reach the PC at all

**Do not skip this.** Some routers enable *AP isolation* (also called client isolation), which
blocks device-to-device traffic even on the same Wi-Fi. That would make the spike fail for a
reason that has nothing to do with WebRTC, and you would draw the wrong conclusion.

**On the PC — terminal:**

```bash
cd relay && npm start
```

Leave it running. It prints `Rock Skipping relay listening on 0.0.0.0:3000`.

**On the phone — browser**, type exactly:

```
http://10.0.0.213:3000/health
```

- **You see `{"ok":true,...}`** → the network is fine. Go back to the PC terminal and press
  `Ctrl+C` to stop this relay, then continue to step 1.
- **It times out or refuses** → the phone and PC cannot talk directly. Fix that first: check
  both are on the same SSID, and look for "AP isolation" / "client isolation" / "guest network"
  in the router settings. **The spike cannot pass until this does.**

---

## Step 1 — On the PC, terminal: deploy the spike page

The phone loads `spike.html` from the Fly service over HTTPS, and the deployed copy does not
have that file yet.

```bash
fly deploy
```

Wait for `1 desired, 1 placed, 1 healthy`. Then confirm the page is actually live:

```bash
curl -sI https://rockskipping-relay.fly.dev/spike.html | head -1
```

Expect `HTTP/2 200`. A `404` means the deploy did not include `controller/` — re-run
`fly deploy` **from the repo root**, not from `relay/`.

---

## Step 2 — On the PC, Unity: switch to spike mode

1. **Window switch → Unity editor.**
2. In the Project panel, open the scene `Assets/Scenes/Bootstrap.unity`.
3. In the Hierarchy, click the **Bootstrap** object.
4. In the Inspector, find the **Bootstrap** component and tick **Run Web Rtc Spike**.

That checkbox replaces the game with the spike — no lake, no join screen, nothing drawing over
the readout. Leave every other toggle alone.

---

## Step 3 — On the PC, Unity: press Play

The Game view shows a panel titled **WebRTC spike — HostingPlan §5**.

Within a few seconds it should read:

```
On an Android phone on this same Wi-Fi, open:

    https://rockskipping-relay.fly.dev/spike.html

and type the code:   ABCD
```

**Write down the 4-letter code.** It is different every run.

If it stays on `waiting for a room code…` for more than ~10 seconds, the PC is not reaching
the Fly service — check the Console for `[RelayClient]` warnings.

---

## Step 4 — On the phone: connect

1. **Device switch → phone.**
2. Open Chrome and go to **`rockskipping-relay.fly.dev/spike.html`** (no room code needed in
   the URL).
3. Type the 4-letter code from the Unity screen, tap **Connect**.

The phone then shows a running log. Expect, roughly in order:

```
relay connected
joined room ABCD
offer created (458 bytes)
my candidate: host 2a45….local  (mDNS — no IP given)   ← amber, and the whole point
unity candidate: host 10.0.0.213
ice: checking
pc: connected
DATACHANNEL OPEN
```

The amber line is the thing being tested: your phone hands over a UUID hostname, never an IP.

---

## Step 5 — Read both screens

The phone shows a green **PASS** box with a round-trip time. That is only half the answer.

**Device switch → back to the PC.** The Unity Game view carries the half that matters:

```
PASS — DataChannel open.
winning pair: local=host  remote=prflx
remote is PEER-REFLEXIVE: Unity learned the phone's address from the
incoming connectivity check, exactly as HostingPlan §5 predicted.
```

### What the result means

| Unity says | Verdict |
|---|---|
| `remote=prflx` | **Pass.** The theory held exactly. Build Phase 2. |
| `remote=host` | **Pass.** Unity resolved the mDNS name itself — different route, same outcome. Build Phase 2. |
| `FAIL — timed out` **and** step 0 passed | **The real finding.** Unity cannot reach the phone. Phase 2 needs rethinking; Phase 1 is unaffected. |
| `FAIL — timed out` **and** step 0 was skipped | Inconclusive. Do step 0. |

Note the round-trip time on the phone. On a LAN it should be single-digit milliseconds —
compare that to the round trip through Toronto that Phase 1 pays today, which is the entire
reason Phase 2 exists.

---

## Step 6 — Put it back

**On the PC, Unity:** untick **Run Web Rtc Spike** on `Bootstrap`. The game returns to normal.

Once the question is answered, delete `Assets/Scripts/Spike/` and `controller/spike.html` —
they are scaffolding and shipping them would be shipping a debug endpoint.

---

## If it goes wrong

| Symptom | Likely cause | Do this |
|---|---|---|
| Phone: "No game found with code" | Mistyped, or Unity restarted and issued a new code | Re-read the Unity screen; the field clears itself for a retry |
| Phone: page will not load | Step 1 not done, or phone is on mobile data | `curl -sI …/spike.html`; check the phone's Wi-Fi |
| Unity stuck on "waiting for a room code" | PC cannot reach Fly | Unity Console, look for `[RelayClient] Connect failed` |
| Candidates exchange, then `ice: failed` | AP isolation, or a VPN on either device | Step 0; turn off any VPN on the phone |
| Phone connects but Unity shows nothing | Unity is not receiving signalling | Console should log `offer from SPIKE`; if absent the relay is not routing |
| `pc: connected` but no DataChannel | Genuinely unexpected | Capture both screens — this is a real finding worth recording |

**One caveat on a negative result.** A failure only means something if step 0 passed on the
same network, in the same session. Without that, a firewall, a VPN or an isolated access point
is indistinguishable from the thing being tested — and each of those would send Phase 2 back
to the drawing board for no reason.
