# Setup — client data you must provide yourself

This repository ships **source code only**. It does **not** include, and will
never include, any Marvel Heroes game-client files. Those are copyrighted by
the original publisher and you must supply them yourself from a legitimate
copy of the game you already own.

## What you need

A local install of the **Marvel Heroes 1.52.0.1700** client (the "2.16a" version
still available through Steam as of writing). From your client install's
`Data\Game\` folder, copy the following two files:

- `mu_cdata.sip`
- `Calligraphy.sip`

## Where they go

After you build the server (see `README.md` in this repo for `dotnet build`
instructions), place both files here:

```
<repo>\src\MHServerEmu\bin\Release\net8.0\Data\Game\
    Calligraphy.sip
    mu_cdata.sip
```

Create the `Data\Game\` folders if they don't exist.

## Why this repo does not ship them

- They are proprietary game assets and cannot be legally redistributed.
- `.gitignore` in this repo explicitly excludes `*.sip`, `*.tfc`, `*.upk`, and
  `Data/Game/` — attempting to `git add` them is a no-op by design.
- Every user must acquire them from their own client install.

## Verification

Once the two files are in place, run the server executable:

```
src\MHServerEmu\bin\Release\net8.0\MHServerEmu.exe
```

If the startup banner is followed by `Loaded X prototypes` (rather than the
`mu_cdata.sip and/or Calligraphy.sip are missing` fatal error), you're set.
