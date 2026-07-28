# MHServerEmu

A fork of [MHServerEmu](https://github.com/Crypto137/MHServerEmu) with
**Phantom Heroes** baked in — server-side hero NPCs you can spawn on demand
that follow, fight alongside you, and can revive you.

The following versions of the game client are supported:

- **1.52.0.1700** (the still-Steam-available "2.16a" build) - Full Support

- **1.48.0.1712** (Pre-BUE) - Preliminary Support

- **1.53.0.203** (Test Center) - Preliminary Support

Build via separate MSBuild configurations (`Release`, `Release 1.48`,
`Release 1.53`) from the same shared source tree.

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

The nemesis system's best-in-slot loadouts (worn/dropped by rank-5 nemeses)
are courtesy of **AlexBond's** [itembase.mhbugle.com](https://itembase.mhbugle.com/)
— the Marvel Heroes Omega Item Base — used with permission. See
[`src/MHServerEmu.Games/Data/Game/PhantomHeroes/NOTICE.md`](src/MHServerEmu.Games/Data/Game/PhantomHeroes/NOTICE.md)
for full attribution.

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

MHServerEmu itself is released under the GNU Affero General Public License
v3.0 (AGPL-3.0). See [LICENSE](LICENSE). The Phantom Heroes additions on top
are released under the same license.

## Upstream FAQ

**Where can I download the game client?**

We do not provide download links for the game client for legal reasons. If you have played the game through Steam when it was live, you should be able to download it in your Steam library.

**How to update the server?**

Download the latest stable or nightly build and overwrite your existing files. Nightly builds can be potentially unstable, so it is recommended to back up your account database file located in `MHServerEmu\Data\Account.db` before updating.

**Are you going to support other versions of the game, like the ones from before the Biggest Update Ever (BUE) came out?**

Preliminary support for game versions 1.48 (pre-BUE) and 1.53 (final test center version) is available if you build the source code using respective build configuration. Nightly builds will be provided at a later date. Support for these versions is still very early, and you will likely encounter game breaking bugs. For now, you should keep using 1.52 for normal play.

Some early work has also been done to support version 1.10 from mid 2013. You can find the code for it in the [MHServerEmu2013](https://github.com/Crypto137/MHServerEmu2013) repository.

**Are you going to add new content to the game (heroes, team-ups, powers, etc.)?**

The scope of this project is restoring the game to its original state. We do not have any plans to create custom content. However, all of our research on the game is completely open-source, and it can be potentially used by others in such endeavors.

**Are you going to make improvements to the game client (e.g. upgrade graphics)?**

No, we do not touch the client side of the game in any way. This project is a recreation of only the server backend needed to run the game.

**I have problems with setting the server up.**

Feel free to join our [Discord](https://discord.gg/hjR8Bj52t3) and ask for help in the `#setup-help` channel.
