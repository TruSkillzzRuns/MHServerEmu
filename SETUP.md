# Setup

Start-to-finish guide to get this MHServerEmu fork running on Windows with a
1.52 client connecting locally. Should take about 15–20 minutes end-to-end.

## Prerequisites

- **Windows 10 or 11** (Linux works for the server itself but this guide
  covers Windows only).
- **.NET 10 SDK** — download from <https://dotnet.microsoft.com/download/dotnet/10.0>.
- **Marvel Heroes 1.52.0.1700 client** — the "2.16a" build still available
  in your Steam library if you played the game before it shut down in 2017.
  This repo does not distribute the client.
- **Apache HTTP Server 2.4** for Windows — download from
  <https://www.apachelounge.com/download/> (any recent 2.4 build works).
  Any HTTP server that can serve a static XML file on port 80 works; Apache
  is what these instructions assume.

## 1. Clone this repo

```
git clone https://github.com/TruSkillzzRuns/MHServerEmu.git
cd MHServerEmu
```

You'll land on the `phantom-heroes` branch by default.

## 2. Build the server

From the repo root:

```
dotnet build MHServerEmu.sln -c Release
```

Should end with `Build succeeded. 0 Error(s)` (some warnings are normal).

The built server will be at
`src\MHServerEmu\bin\Release\net10.0\MHServerEmu.exe`.

## 3. Bring the two client `.sip` files

From your Marvel Heroes 1.52 install (typically
`...\Steam\steamapps\common\Marvel Heroes\Data\Game\`), copy:

- `mu_cdata.sip`
- `Calligraphy.sip`

into:

```
<repo>\src\MHServerEmu\bin\Release\net10.0\Data\Game\
```

Create the `Data\Game\` folders if they don't exist.

Why manually: these are proprietary game assets — this repo cannot legally
ship them. `.gitignore` also refuses to track `*.sip` anywhere in the tree.

## 4. Set up Apache to serve SiteConfig.xml

The client asks `http://localhost/SiteConfig.xml` on startup to find the
server. You need Apache (or any HTTP server) responding on port 80 with a
correctly-configured `SiteConfig.xml`.

MHServerEmu ships a template `SiteConfig.xml` you can use. Two ways to get
Apache running:

**Easy path** — if you already have MHServerEmu installed anywhere on this
machine (e.g. from a stable release download), reuse its `Apache24/` folder.
Copy the `SiteConfig.xml` shipped with this repo (or from an existing
install) into `Apache24\htdocs\SiteConfig.xml`.

> **If you copied `Apache24/` to a different path than it was installed at,
> you must fix `SRVROOT` or Apache will not start.** `httpd.conf` records the
> folder it was configured for, so a copied config still points at the old
> location. Open `Apache24\conf\httpd.conf` and find the line starting
> `Define SRVROOT`:
>
> - If it reads `Define SRVROOT "c:/some/old/path"`, change it to your actual
>   `Apache24` folder, using **forward slashes** — e.g.
>   `Define SRVROOT "C:/Apache24"`.
> - If it reads `Define SRVROOT "${APACHE_SERVER_ROOT}"`, it expects an
>   environment variable instead — see the `APACHE_SERVER_ROOT` step under
>   **Fresh install** below.
>
> This is the single most common setup failure: Apache's window opens and
> closes instantly with no visible error. See
> [Apache opens and closes immediately](#apache-opens-and-closes-immediately)
> in step 5 to read the real message.

**Fresh install** — download an Apache 2.4 Win64 build, unpack to a folder
you'll remember (e.g. `C:\Apache24`), then:

- Copy your `SiteConfig.xml` to `C:\Apache24\htdocs\SiteConfig.xml`.
- If Apache's default `httpd.conf` uses `${APACHE_SERVER_ROOT}` for its
  `ServerRoot` directive (some pre-configured MHServerEmu Apache packages
  do this), set the environment variable so it resolves. Open PowerShell
  as Administrator and run:

  ```powershell
  [System.Environment]::SetEnvironmentVariable(
      'APACHE_SERVER_ROOT',
      'C:/Apache24',
      'User')
  ```

  Sign out and back in so the new user-env-var takes effect. Otherwise you'll
  see `AH00111: Config variable ${APACHE_SERVER_ROOT} is not defined` and
  Apache will refuse to start.

## 5. Start Apache

Double-click `Apache24\bin\httpd.exe`, or from PowerShell:

```powershell
Start-Process 'C:\Apache24\bin\httpd.exe' -WindowStyle Hidden
```

Verify it's up: <http://localhost/SiteConfig.xml> in a browser should return
an XML file, not "Can't reach this page."

### Apache opens and closes immediately

`httpd.exe` prints its error and exits, so double-clicking it hides the
message. Run it from a Command Prompt that stays open to see what's actually
wrong:

```
cd C:\Apache24\bin
httpd.exe -t
```

The two usual answers:

- **`ServerRoot must be a valid directory`**, or
  `Config variable ${APACHE_SERVER_ROOT} is not defined` — the `SRVROOT` fix
  in step 4 applies. This is what you get after copying `Apache24/` from
  another install.
- **`(OS 10048) ... make_sock: could not bind to address 0.0.0.0:80`** —
  something else already owns port 80. Find it with:

  ```
  netstat -ano | findstr :80
  ```

  Common culprits on Windows are IIS, the "World Wide Web Publishing Service",
  and VMware. Stop the offender, or change Apache's `Listen 80`
  (you'd then need matching changes in `SiteConfig.xml`).

If `httpd.exe -t` prints `Syntax OK` but Apache still won't stay up, check
`Apache24\logs\error.log`.

## 6. Start the server

```
src\MHServerEmu\bin\Release\net10.0\MHServerEmu.exe
```

If everything above is right you'll see the ASCII banner, then messages like
`Loaded N prototypes`, `PlayerManagerService: Started`, etc.

If you see `mu_cdata.sip and/or Calligraphy.sip are missing`, go back to
step 3.

## 7. Launch the client

Point the client at your local server. Example command line for a Steam
install at `E:\SteamLibrary`:

```
"E:\SteamLibrary\steamapps\common\Marvel Heroes\UnrealEngine3\Binaries\Win64\MarvelHeroesOmega.exe" -robocopy -nosteam -siteconfigurl=localhost/SiteConfig.xml
```

Save that as a `.bat` file for convenience.

If the client shows "Site Config Not Available Due To HttpRequest Error: 1",
Apache isn't serving on port 80 — go back to step 5.

## 8. Create an account

At the login screen, use the **Register Account** button, or type this in
the login field before hitting Login:

```
!account create <email> <password>
```

Then log in with those credentials.

## 9. Try the phantom heroes

Once you're in a zone (Avengers Tower, a story chapter, wherever), open chat
and try:

```
!phantom spawn 10
```

Ten phantoms with random comic-book names should appear near you and start
following. Then:

```
!phantom clear
```

to remove them.

## Account database (optional)

`Account.db` — the SQLite file where MHServerEmu stores accounts and
characters — is also gitignored and never shipped by this repo.

- **First-time users**: do nothing. On first server launch the server will
  create a fresh empty `Account.db` at
  `src\MHServerEmu\bin\Release\net10.0\Data\Account.db`.
- **Migrating from another install**: copy your existing `Account.db` into
  that same folder before starting the server. Your accounts and characters
  come with you.

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| `Build succeeded` then no exe | Wrong project or configuration | Check step 2 — must be `MHServerEmu.sln` with `-c Release` |
| `mu_cdata.sip missing` fatal | Client files not copied | Step 3 |
| `Site Config Not Available` in client | Apache not running or wrong docroot | Steps 4–5, then curl `localhost/SiteConfig.xml` |
| `AH00111: APACHE_SERVER_ROOT is not defined` in Apache log | Missing env var | Step 4's user-env-var block |
| Client connects but "Server List Empty" | `SiteConfig.xml` points at wrong server host/port | Check `SiteConfig.xml` — should point at `localhost:4306` for frontend |
| `!phantom spawn` says "command not found" | Server built from wrong branch | Confirm you're on `phantom-heroes` (default): `git branch --show-current` |
