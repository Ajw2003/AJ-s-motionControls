# Findings: iPhones cannot stay on the hotspot (2026-10-02)

**Prompted by:** #77. iPhones fail to connect to the game's Wi-Fi in the current build, and still
try to open a captive-portal ("log in to this network") page. Ayden also asked how to make the
game's network win over another network the phone auto-joins, and to confirm the network name and
password stay saved per host PC.

**Confidence labels:** **MEASURED** = run here, output quoted. **CONFIRMED** = read in the code or
in vendor documentation. **PLAUSIBLE** = the mechanism fits, not yet seen on an iPhone.

No iPhone was available. Everything below was built and checked without one. The iPhone itself is
the last step, listed under "The in-game test".

## What was wrong, and what changed

| # | Problem | Confidence | Fix | Verified |
|---|---|---|---|---|
| #78 | The relay answered Apple's internet check (`captive.apple.com/hotspot-detect.html`) with the bare word `Success`. Apple's real page is `<HTML><HEAD><TITLE>Success</TITLE></HEAD><BODY>Success</BODY></HTML>`. Any other path on an Apple probe host got a **302 redirect to the game, which is how iOS recognises a captive portal**. Tapping Cancel on that sheet disconnects the phone ([Apple](https://support.apple.com/en-us/102554)). | Code CONFIRMED; that it causes the sheet PLAUSIBLE, and it matches Ayden's report | Serve Apple's exact page for any path on an Apple probe host (`relay/server.js` `serveConnectivityProbe`) | MEASURED: `npm test` 55/55; live relay returns the page and a 200 on a random path |
| #79 | With iCloud Private Relay on, Safari resolves names through Apple, not through our DNS. Publicly, `play.skiprocking.ca` is the registrar's parking page (`207.207.210.107`, `.229`, resolved here). | PLAUSIBLE | Answer `mask.icloud.com` and `mask-h2.icloud.com` with NXDOMAIN in both modes. This is Apple's documented signal that a network does not allow Private Relay ([Apple](https://developer.apple.com/support/prepare-your-network-for-icloud-private-relay/)). Apple's page says only that the user "will be alerted" to turn it off for the network or pick another; that iOS switches it off by itself is from third-party guides, not Apple (`relay/dns.js` `blockedNames`). | MEASURED: live relay returns NXDOMAIN for both, isolated and sharing, and never forwards them |
| #80 | The hotspot script never set its security mode. Windows 11 24H2 added WPA3 and WPA3 transition mode for the hotspot ([Microsoft](https://learn.microsoft.com/en-us/uwp/api/windows.networking.networkoperators.tetheringwifiauthenticationkind)). Transition mode is a commonly reported cause of "Unable to join". | PLAUSIBLE | Ask for WPA2 explicitly where Windows offers the setting, and report `security` in the result line the game logs (`hotspot/hotspot-control.ps1`) | Parses in PowerShell 7. **Not run on Windows.** |
| #81 | `hotspot-credentials.json` was saved beside the executable. The installer puts the game in Program Files (`deploy/installer.iss` `DefaultDirName={autopf}`), which the game cannot write. The save failed with only a warning, and **every launch made a new network no phone had saved**. A new build folder did the same. | CONFIRMED in code | Save in `Application.persistentDataPath` (`%USERPROFILE%\AppData\LocalLow\DefaultCompany\RockSkipping\`). An older file beside the exe is copied once, not moved. A failed save is now an error. | **Not compiled:** no Unity here |

**When the iPhone reaches our probe answer at all.** With sharing on and working, Apple's check
goes to the real Apple and passes; our answer is not involved. Ours is used when sharing is off,
when the host has no upstream DNS, or when the upstream takes longer than 1.5 s. Ayden saw the
login sheet, so at least one of those applied. `relay/probe-access.log` will show which.

## Making the game's network win (research)

**There is no setting a web page can change, and no iOS setting a phone owner can set, that orders
Wi-Fi networks.** iOS picks among saved networks it can see, in this order
([Apple, "How Apple devices decide which known wireless networks to auto-join"](https://support.apple.com/en-us/102169)):

1. **Most preferred:** a score raised each time you join a network by hand, and lowered each time
   you leave one by hand.
2. **Private networks, most recently joined first.** The game's hotspot counts as private.
3. Networks set up by a device-management service rank above ones joined by hand.
4. Newer Wi-Fi standard, then band: **6 GHz, then 5 GHz, then 2.4 GHz**, then security, then
   signal strength.

What that means in practice:

- **Scanning the Wi-Fi QR is the switch.** It's a manual join: the phone moves now, and the game's
  network becomes both "most recently joined" and higher-scored. This is the one lever that works
  on both iOS and Android, and the game already shows that QR.
- **iOS chooses only when it is auto-joining** (Wi-Fi turned on, walking into range, waking). It
  does not leave a working network it's already on for another saved one. That matches the S26
  result in `Findings-HotspotForwardingAndInternet-2026-10-01.md` (Test B: it stayed on home
  Wi-Fi).
- **A network that fails the internet check loses** (PLAUSIBLE: Apple developer-forum reports that
  iOS leaves an auto-joined network that fails the check, but not one joined by hand). #78 plus
  sharing is what keeps the network passing.
- **The band is a tie-breaker we lose by default.** With no station link, the script asks for
  2.4 GHz, and most home routers offer 5 GHz, which iOS ranks higher. This only matters once the
  higher rules tie. Changing the default to 5 GHz would cost range and very old phones, so it's a
  separate decision, not made here.
- **Not recommended:** a configuration profile (`.mobileconfig`). It needs a trip through Settings
  and a passcode on every guest's phone, and Apple's ranking names *device-management* networks,
  not hand-installed profiles. Native-app APIs (`NEHotspotConfiguration`) need an App Store app.

**Honest bottom line:** a phone already on a working home Wi-Fi stays there until its owner scans
the Wi-Fi QR. What we can do is make sure that once they have, the phone never gets a reason to
leave (#78, #79, #80), and that the saved network is the same one next time (#81).

## Not changed: the shared-radio slowdown

When the host PC's own internet is on Wi-Fi, the hotspot shares that radio and the aim stream
arrives in bursts. That's covered by `docs/plans/aim-preview-lag-on-shared-radio.md`. Wired
Ethernet on the host avoids it. Nothing in this pass changes it.

## The in-game test (needs the PC, then one iPhone)

On the PC, in Unity, press Play on this branch:

1. **Unity Console:** one `[HotspotCredentialsStore]` line. The first launch says either
   "Copying the saved network …" (your old network kept) or "No saved network; created …". The
   next launch says neither: it just reuses it. A red "Could not save" line is a failure.
2. **Unity Console:** the hotspot result line includes `"security":"Wpa2"`. `"Wpa2 (only option
   before 24H2)"` is also fine.
3. Quit and press Play again: the SSID on the TV is the same as the first time.

On the iPhone:

4. Settings → Wi-Fi → the old Rock Skipping network → **Forget This Network**, so the test starts
   clean.
5. Scan the TV's Wi-Fi QR. **Expect: it joins, with no login sheet.**
6. Settings → Wi-Fi → tap ⓘ on the game network. **Expect:** no "No Internet Connection" under
   the name. iOS may also say Private Relay is off for this network; that's #79 working.
7. Scan the game QR. **Expect:** the game page, not a parked or error page.
8. Turn Wi-Fi off and on with the game still running. **Expect:** it rejoins by itself if no
   other saved network is in range.

If step 5 still shows the login sheet, open `relay/probe-access.log` in the project folder. The
lines from the iPhone's address (`192.168.137.x`) show which Apple address it asked for and what we
answered. That is the next diagnosis.

## Result: iPhone 17 joined and played (2026-10-02, reported by Ayden)

Tested on this branch, in Play mode, by Ayden:

- **iPhone 17 joined the game's network, connected to the game and played a match.**
- The phone had **mobile data on and was connected to another Wi-Fi network** before scanning.
  Scanning the Wi-Fi QR moved it to the game's network, as the research above predicted.
- **The host PC had no network of its own** (no upstream), so nothing was shared. That is the case
  where the phone gets *our* answers rather than Apple's: our copy of Apple's check page (#78) and
  NXDOMAIN for Private Relay (#79). The phone stayed on a network with no internet, which is the
  harder case.

#80 confirmed in the same session: the Unity Console showed `[HotspotService] security: Wpa2`
(Ayden's screenshot, 18:13:33).

Not reported, so still open: the per-step check above for #81 (same SSID after quitting and pressing Play again), and the Wi-Fi off/on rejoin
(step 8). A shared-internet run on an iPhone hasn't been done either.
