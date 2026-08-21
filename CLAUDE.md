# Rock Skipping — working rules

## Never make someone type or hunt for a network or URL. Render the QR.

This project already has QR tooling. Use it. Do not write another one, and do not fall back to
"look for the network called X in your Wi-Fi list" or "open http://... on your phone".

| Tool | Use it for |
|---|---|
| [`deploy/spike/wifi-qr.js`](deploy/spike/wifi-qr.js) | Terminal ASCII QR — Wi-Fi join payload and/or a URL. For anything run from a console. |
| `relay/server.js` `/qr?data=<string>` | QR module matrix as JSON, for anything drawn on the TV by Unity. Encodes an arbitrary string, so it needs no change to carry a new payload type. |

Both use `qrcode-generator`, already a relay dependency. Nothing new to bundle.

```bash
node deploy/spike/wifi-qr.js --ssid "DIRECT-RockSkip" --pass "skipdirect" --url "http://192.168.137.1/"
```

Joining is **two scans, not one** — a `WIFI:` payload and a URL are different payload types and
cannot be combined. Render both, labelled and in order. `--hidden true` flags the network as
hidden, which makes the phone actively probe for the SSID rather than relying on a passive scan.

**Why this is a rule and not a preference:** the whole product premise is "scan and play, no app
download". A test that asks someone to hunt through a Wi-Fi picker is not testing the real flow —
and it can fail for reasons the real flow would never hit. Phones filter P2P/`DIRECT-` SSIDs out
of the picker UI while the radio sees them fine, so "I couldn't find the network" can mean the
network was working perfectly. Scanning bypasses that entire class of false negative.

Escaping matters: `\ ; , : "` are structural in a `WIFI:` payload. `wifi-qr.js`'s `escapeValue()`
already handles it — another reason not to hand-roll a second implementation.

## Related

- Global rules in `~/.claude/CLAUDE.md` (no hidden background processes, no git actions, commit
  before destructive changes) apply here too.
- [`docs/Findings-WiFiDirect.md`](docs/Findings-WiFiDirect.md) — why the SSID-hunting approach
  wasted two phone test cycles.
