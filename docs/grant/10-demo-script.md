# Demo video script

Three minutes. This is the artifact that wins Epic-style applications and strengthens every
other one. Reviewers watch videos; they skim prose.

**Prerequisite: WP1.** You cannot record this until a stranger can install the software. Do not
fake it by running from the editor — the install *is* the demonstration.

---

## The single rule

**Show, do not tell.** The claim is "no internet, no app, no setup". Every second spent asserting
that is a second not spent proving it. Three minutes of watching it happen is worth more than
any sentence you could write over the top.

---

## Shot list

### 0:00–0:15 — The problem, stated once

One sentence over a wide shot of a TV and a couch: *"Four people, one room, one screen — and
nobody has a controller."*

Do not explain certificates. Do not explain secure contexts. Nobody has decided to keep watching
yet.

### 0:15–0:35 — Establish the offline condition, physically

This is the most important shot in the video and it must be **visibly, physically verifiable**:

- Unplug the ethernet cable on camera. Hold it up.
- Show the router powered off, or absent from the room entirely.
- Show a phone with mobile data disabled in its settings, on screen, unedited.

Do it in one continuous unbroken shot. A cut here is a place a skeptical viewer assumes something
happened. The whole application rests on this claim, so let them watch it be true.

### 0:35–1:05 — Install

- A clean Windows machine — say so, show the desktop
- Double-click the downloaded installer
- Real time or lightly sped up with the clock visible. **Do not cut.** The install being
  unremarkable is the point
- Launch. The TV shows a lobby with two QR codes

### 1:05–1:40 — Join, with a real stranger

Hand a phone to someone who has not used it before. Film them, not the screen.

- Scan one: the phone joins the network the PC raised
- Scan two: the controller page opens
- They type a name

**If they hesitate, leave it in.** Honest friction is more persuasive than a rehearsed run, and
you have a funnel instrument (`DEVICE_FUNNEL`) precisely because you take this seriously. A
reviewer who has seen a hundred flawless demos notices the one that did not need to be flawless.

Repeat compressed for a second phone — ideally an iPhone and an Android side by side, since
cross-platform verification is a claim you are making.

### 1:40–2:20 — Play

- Someone swings the phone; the rock skips
- Cut between hand and screen so the correspondence is unmistakable
- **Show the aim preview tracking continuously**, not just the throw — that is the 20 Hz cosmetic
  channel doing its job and it reads as responsiveness
- Show a hard throw and a gentle throw producing visibly different results. That difference is
  the tiered gravity removal working, and it is the thing a naive implementation gets wrong

Turn-taking, a score updating, four players. Let it look like a game people are enjoying, because
that is also evidence.

### 2:20–2:45 — The generalisation

Now, and only now, say what it is:

*"That is a Unity plugin. The game is one thing you can build with it — the phone, the network,
the sensors and the protocol are the part we are packaging."*

If a second demo exists by then — a different gesture, a different engine binding — this is where
three seconds of it belongs.

### 2:45–3:00 — Close

What you are asking for and what it produces. One card, readable, no voiceover needed:

- A Unity package, free and openly licensed
- An engine-agnostic protocol specification
- Published research on offline secure contexts for local-first software

---

## Production notes

- **Sound matters more than resolution.** A phone camera with clean audio beats a good camera
  with room echo. Reviewers watch on laptop speakers.
- **No music under speech.** Ever.
- **Caption everything.** Accessibility is a claim you are making in this application; failing it
  in the video is a bad look, and many reviewers watch muted.
- **Show real hardware variety.** Different phone models on screen substantiate the device matrix
  without a word of narration.
- **Do not show a certificate warning, a console window, or an editor.** If any appears, that is
  a WP1 defect, not a video problem — fix it upstream.
- **Keep an unedited long take in reserve.** If a reviewer or a program officer asks whether it
  really works offline, a single continuous three-minute take from unplugging to playing answers
  it completely. You will not need it often, and when you need it nothing else substitutes.

## What to cut if it runs long

In order: the second phone joining, the close card narration, the generalisation shot. Never cut
the offline establishment or the install. Those two are the demonstration; everything else is
illustration.
