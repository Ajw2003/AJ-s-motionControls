# Narrative core

Reusable prose. Write it once here, adapt into each form. Keep this file the source of truth —
when a claim changes, change it here first, or the versions drift apart and one of them becomes
untrue.

Every block is written to be quoted more or less directly. Trim rather than rewrite.

---

## The problem (short — 100 words)

> Playing a game together in one room should not require everyone to own a controller, download
> an app, or have working internet. Phones are the controllers everyone already has, and modern
> browsers can read their motion sensors — but only over a secure connection, which requires a
> certificate, which requires a domain name, which requires a network that can resolve one. On a
> living-room network with no internet, that chain breaks. So every product in this space falls
> back to an app store, a cloud server, or a security warning that disables the sensors it needed.

## The problem (long — 250 words)

> Phone-as-controller is a solved user experience and an unsolved engineering one. Players
> understand it immediately: point your phone at the screen and swing. The obstacle is
> infrastructure.
>
> Browser access to motion sensors requires a secure context. A secure context requires a
> certificate. A certificate is issued for a name, so the network needs a resolver. A private
> address cannot obtain a public certificate on its own. And a page served over HTTPS cannot open
> an insecure WebSocket, so the page and its socket must share one secure origin. Break any link
> and the sensors go dark.
>
> The consequences are visible in what exists. Products either ship a native app — abandoning the
> no-install property that makes the idea work at a party — or route input through a cloud server,
> which fails at the venue with bad Wi-Fi, on the school network that blocks peer traffic, in the
> rural community with no service, and in the hospital or care home where the network is closed.
> Or they ask players to accept a certificate warning, which does not help, because the warning
> is what disables the sensors.
>
> Underneath that sits a second problem developers hit even when the network cooperates. Raw
> phone motion data disagrees between iOS and Android in ways that produce wrong behaviour rather
> than errors — no exception is thrown, the throw simply goes the wrong way. Getting from raw
> sensor events to a value a game engine can trust is weeks of work that every team repeats.

## Why now

> Three things have converged. Phone sensor APIs are mature and near-universal. Certificate
> automation has made obtaining a certificate cheap and scriptable. And certificate lifetimes
> are simultaneously contracting sharply — from years, to 398 days today, toward roughly 47 days
> by 2029 — which means the workarounds local-first software has quietly relied on are expiring.
> The window to establish a durable approach is now, while the problem is tractable and before
> every affected project improvises its own answer.

## What we have built

> RockSkipping is a party game in which the television runs the game and everyone's phone is a
> motion controller, reached through a web page with no download. Building it required solving
> the infrastructure problem above, and that solution turned out to be worth more than the game.
>
> The host PC raises its own Wi-Fi network with no internet connection present — a capability
> Windows was widely believed not to expose, which this project established by measurement that
> it does. Over that network a single process serves the controller page, relays game traffic,
> answers DNS as the network's authority, and responds to the phone's captive-portal probes so
> the operating system opens the join page itself. Phones connect by scanning two codes and
> typing a name. Nothing touches the internet during play.
>
> On the phone, a sensor layer converts raw motion into values a game engine can trust. It is
> quaternion-only, because the pose players naturally hold a phone in when aiming at a television
> sits exactly on the singularity where Euler angles fail. It calibrates on the gesture rather
> than trusting absolute heading, because iOS and Android disagree about heading and agree about
> change. It removes gravity in three tiers ordered by accuracy, because the naive filter lags
> enough during a fast wrist flick to inject roughly 19.6 m/s² of acceleration that never
> happened — enough to make a hard throw and a gentle one read identically.
>
> This is verified on real hardware across multiple iPhone generations and multiple Samsung and
> Pixel generations. The relay and sensor pipeline carry substantial automated test coverage. The
> project documents what it removed and why as carefully as what it kept.

## What we will build

> We will extract this into **a plugin any game developer can install**: a Unity package first,
> built on a documented, engine-agnostic protocol so bindings for other engines can follow
> without rebuilding the core.
>
> The developer adds the package, and their players point a phone at the screen. No app store, no
> server, no accounts, no internet, no configuration — for the developer or the player.
>
> Alongside it we will publish what we learn about the underlying problem: how a local-first
> application holds a browser-trusted secure context with no internet, as certificate lifetimes
> shrink toward weeks. That question is not specific to games. It blocks museum kiosks, classroom
> response systems, clinical devices on isolated networks, and field-research equipment — anything
> that needs a phone browser to talk to a local machine with sensor or camera access.

## Why this applicant

> This project exists. It is not a proposal for a prototype; it is a proposal to generalise a
> working system. Six weeks of concentrated work produced a tested relay, a tested sensor
> pipeline, verified operation across two mobile platforms and multiple device generations, and
> roughly four thousand lines of design and failure documentation.
>
> That documentation is the strongest evidence of how the work is done. It records a WebRTC
> transport that was built and then deleted once measurement showed it bought nothing. It records
> Wi-Fi Direct evaluated, working, and rejected on a single decisive property. It records a
> confidently-held conclusion that Windows could not raise a hotspot offline being re-examined
> and found to be a four-line bug in this project's own code — and names the process failure,
> testing on phones before checking the host, as more expensive than the technical one.
>
> Funding a solo developer is a bet on judgement and follow-through. The evidence for both is
> in the repository, and it is checkable.

## Impact

> **For developers:** weeks of infrastructure work removed from every project that wants phone
> motion input, and a category of game made viable for teams too small to build it.
>
> **For players:** a group plays with hardware they already own. No controller purchase per
> person, no download, no account, no internet.
>
> **Where connectivity is not a given:** rural and remote communities, schools whose networks
> block the traffic this needs, hospitals and care homes, community centres, events with
> unusable Wi-Fi. The project already records a school network that phones could not join being
> solved by the host raising its own.
>
> **For accessibility:** motion input is an alternative modality, and a phone controller inherits
> the player's own accessibility configuration — text size, screen reader, switch access — which
> a purpose-built gamepad cannot. Adjustable sensitivity and reduced-range-of-motion modes are
> funded work, not aspirations.
>
> **For privacy:** nothing leaves the room. There is no server to breach, no telemetry, and no
> cloud dependency that can be discontinued out from under the software.
>
> **Beyond games:** the offline secure-context work applies to any local-first application that
> needs a phone browser and a local host to talk to each other. That write-up may outlast the
> plugin.

---

## Two claims to keep exact

Both are easy to inflate, both will be checked, and both are strong enough without inflation.

**Wi-Fi versus internet.** The phone's Wi-Fi radio is on — it joins a network the host raises.
What is absent is internet, a router, an app store and an account. Write "no internet", never
"no Wi-Fi".

**Offline play versus offline provisioning.** Play sessions never touch the internet. The host
needs the internet at some point within each certificate window — about 90 days today — to
refresh a certificate it then serves locally. A permanently air-gapped machine eventually stops
working. Say this plainly; a bounded claim a reviewer can verify is worth more than an absolute
one they can puncture, and the residual limitation is precisely the research question the
funding addresses.
