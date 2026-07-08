# PhantomHeroes

Standalone installer that adds server-side NPC bots — rendering as real playable-roster heroes with real powers and AI — to an MHServerEmu source tree.

Works with both 1.53-generation and 1.52-generation MHServerEmu branches. Detects which one you have and applies the correct patch set automatically.

## What it does not do

- Never reads, copies, redistributes, or modifies **any** game-client file (SIP archives, UPK, TFC textures, EXE, prototypes).
- Never talks to the network. It is a local file patcher only.
- Never bundles hero-specific data. All prototype paths are resolved at runtime by your running server, from the client data directory your server already reads.

Everything the tool ships is C# source that gets dropped into your MHServerEmu source tree. That source calls MHServerEmu APIs that already exist. No new client interaction is introduced.

## Prerequisites

- **.NET 8 SDK** installed (`dotnet --version` should print 8.x.y).
- A cloned MHServerEmu source tree on disk. The tool needs to see `src/MHServerEmu.Games/*.csproj`.
- A backup of your source tree, or at minimum a clean git working tree so you can `git diff` after the patch runs.

## Build the installer

From this folder:

```
dotnet publish -c Release -r win-x64 --self-contained false
```

The single-file exe lands under `bin/Release/net8.0/win-x64/publish/PhantomHeroes.exe`. Copy it wherever you want.

## Usage

```
PhantomHeroes detect    --source <path-to-mhserveremu-source>
PhantomHeroes install   --source <path-to-mhserveremu-source> [--version auto|153|152] [--dry-run] [--no-backup]
PhantomHeroes uninstall --source <path-to-mhserveremu-source>
```

`--source` is the root of the MHServerEmu source tree — the folder that contains `src/`.

### Recommended first run

```
PhantomHeroes detect --source C:\path\to\MHServerEmu
```

Reads your tree, prints which files it found, and tells you which generation (1.52 or 1.53) it will patch as. **Nothing is written on `detect`.**

### Install (dry run first)

```
PhantomHeroes install --source C:\path\to\MHServerEmu --dry-run
```

Prints every file that would be edited and every `.phbak` backup that would be created. **No changes to your tree.**

Once the dry run looks right:

```
PhantomHeroes install --source C:\path\to\MHServerEmu
```

For every file it edits, the installer first copies the original to `<file>.phbak` so you can roll back cleanly.

### Uninstall

```
PhantomHeroes uninstall --source C:\path\to\MHServerEmu
```

Restores every `.phbak` back over its target and deletes the phantom source files the installer wrote. Idempotent — safe to run twice.

## What gets patched on 1.53

Six one-line null guards + one new partial file + one mailbox routing note:

```
src/MHServerEmu.Games/Entities/Avatars/Avatar.PhantomHero.cs       [new]
src/MHServerEmu.Games/Entities/Avatars/Avatar.cs                    [edit]
src/MHServerEmu.Games/Entities/Player.cs                            [edit]
src/MHServerEmu.Games/Entities/Entity.cs                            [edit]
src/MHServerEmu.Games/Network/GameServiceMailbox.cs                 [notes]
```

Each edit is anchor-matched against verbatim vanilla text. If the anchor is missing (your tree has drifted from a vanilla checkout), the installer refuses to touch that file and tells you which one. No half-patched trees.

## What gets patched on 1.52

Zero engine edits. One new partial template file dropped next to `Avatar.cs`:

```
src/MHServerEmu.Games/Entities/Avatars/Avatar.PhantomHeroRender.cs  [new]
```

You then uncomment one of three lines in the template — the render-override API name varies across 1.52 forks. The installer will not guess for you.

## After installing

Rebuild:

```
dotnet build src/MHServerEmu/MHServerEmu.csproj -c Release
```

Then wire a `SpawnPhantomHero` call to a webapi endpoint, chat command, or a UI button — whatever your server exposes. Anything that can call `avatar.SpawnPhantomHero(level, username, out error)` for a live avatar will work.

## Rolling back

```
PhantomHeroes uninstall --source C:\path\to\MHServerEmu
```

Restores every `.phbak` back over its target and deletes the phantom source files. If you skipped backups (`--no-backup`) on install, use `git` to roll back instead.

## Safety guarantees

- All changes are limited to files under the `--source` directory you provided. Nothing outside it is touched.
- Every edit is anchor-matched to verbatim vanilla text — the installer refuses to patch a file whose surrounding code has drifted.
- Every editable file is backed up to `<file>.phbak` before rewrite (opt out with `--no-backup`).
- `--dry-run` shows the exact plan without touching anything.
- No client-side files (SIP / UPK / TFC / EXE / prototypes) are read or written under any command.

## What each recipe actually does at runtime

See [PHANTOM_HERO_MOD.md](../../../Desktop/MHO%20Files/PhantomHeroMod/PHANTOM_HERO_MOD.md) if it lives on your machine, or the sections in the source of this installer, for the full technical writeup:

- 1.53: fabricates a synthetic in-memory `Player` entity that owns a real `AvatarPrototype`, cascades `IsInGame`, clears loading-screen state, flips `IsMovementAuthoritative` so the server can drive locomotion.
- 1.52: sets `Agent.ClientPrototypeRefOverride` on a hero-shaped Agent shell so the client renders it as any playable AvatarPrototype. All AI / powers / physics run off the underlying Agent.
