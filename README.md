# MHServerEmu

A fork of [MHServerEmu](https://github.com/Crypto137/MHServerEmu) with
**Phantom Heroes** baked in — server-side hero NPCs you can spawn on demand
that follow, fight alongside you, and can revive you.

Targets the **1.52.0.1700** client (the still-Steam-available "2.16a" build).

## What this fork adds over upstream

- Server-side **phantom heroes**: real Avatar entities (not Agent bots), spawned
  from the loaded client's actual playable roster with random comic-flavored
  names, follow/hunt/attack AI, and revive support.
- `!phantom spawn <n>` and `!phantom clear` chat commands.
- HTTP endpoints under `/webapi/phantom/*` for external tooling.
- Small null-guards to the Player / Entity / Avatar / CommunityRegistry paths
  so phantom Players (which have no client connection) don't crash the engine.

Everything else — the rest of the server, the client compatibility, the
prototype system — is verbatim [Crypto137/MHServerEmu](https://github.com/Crypto137/MHServerEmu).
Full credit to the upstream authors; this fork just adds a mod on top of their
work.

## Try it

```
git clone https://github.com/TruSkillzzRuns/MHServerEmu.git
cd MHServerEmu
```

Then follow **[SETUP.md](SETUP.md)** — you'll need to bring two client files
yourself (this repo ships source only, never proprietary data). Once the
server is running:

```
!phantom spawn 10
!phantom clear
```

## Branches

- **`phantom-heroes`** (default) — vanilla MHServerEmu + the Phantom Heroes mod
  committed in as source. Clone this if you want to run the modded server.
- **`master`** — a clean mirror of upstream `Crypto137/MHServerEmu@master`,
  kept unmodified so future upstream updates can be merged cleanly into
  `phantom-heroes`.

## License

MHServerEmu itself is released under the MIT License. See [LICENSE](LICENSE).
The Phantom Heroes additions on top are released under the same license.
