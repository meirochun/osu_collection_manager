# osu! Collection Manager

A simple app that helps you manage your **osu!stable** collections, download beatmaps, and build practice collections
based on your own profile. It works on Windows and on Linux (with osu! running through Wine).

Everything runs on your own PC. The app starts a tiny server on `localhost` and you use it in your browser. Nothing is
hosted online, and you don't need an osu! API key.

## Quick start

1. Go to the [latest release](https://github.com/meirochun/osu_collection_manager/releases/latest) and download the file
   for your system:
   - **Windows:** `osu_collection_manager-win-x64.zip`
   - **Linux:** `osu_collection_manager-linux-x64.tar.gz`
2. Unzip it anywhere you like. Keep the files together in the same folder.
3. Close osu!, then start the app (details for [Windows](#on-windows) and [Linux](#on-linux) below).
4. Your browser opens the app. If it can't find your osu! folder, it will ask you for it.

That's it. There's nothing to install: the download already includes everything the app needs.

## What you can do with it

| Tab | What it's for |
|---|---|
| **Library** | Browse every difficulty in your osu! install, filter by stars, BPM, length or last played, and add the ones you pick to a collection. |
| **Collections** | Create, rename and delete collections, and remove maps from them. You can **export** a collection to a `.json` file and **import** it again later (or share it with a friend). If some maps from an imported file are missing, the app can find and download them for you. |
| **Search & Download** | Search for beatmaps, listen to a preview, open the map's page on osu!, and download sets, optionally straight into a collection. |
| **Training Planner** | Looks at your public profile, works out what star rating you're comfortable with, and suggests practice collections. You choose what to work on (flow aim, jumps, tech, streams, speed...) and how hard. Then you can review the plan, rename collections, untick or remove maps, and apply it to download everything and create the collections. |
| **Jobs** | Shows progress and logs for downloads, imports and plan generation. |

## Screenshots

![Collections Page](resources/collections_page.png)

![Training Planner Page](resources/training_planner_page.png)

## What you need

- Windows 10 or 11, or a 64-bit Linux desktop
- osu!**stable** installed (the folder that contains `osu!.db` and `Songs`). On Linux, that means osu!stable running
  through Wine, for example with [osu-winello](https://github.com/NelloKudo/osu-winello), Lutris or Bottles
- An internet connection for downloads, search and the Training Planner

osu!lazer is not supported, because it stores its data in a different format.

## Starting the app

### On Windows

1. Unzip the download.
2. Double-click `osu_collection_manager.exe`.
3. A console window opens and your browser opens `http://localhost:5178`. The console window *is* the app, so close it
   when you want to quit.

If you start the app a second time, it just opens the browser tab of the copy that's already running.

Windows SmartScreen may warn you the first time because the app isn't signed. Click **More info**, then **Run anyway**.

### On Linux

The app runs natively on Linux. You don't need Wine for the app itself, only for osu!. It edits the files of your
Wine-based osu! install.

```
mkdir osu_collection_manager
tar xzf osu_collection_manager-linux-x64.tar.gz -C osu_collection_manager
cd osu_collection_manager
./osu_collection_manager
```

It prints `http://localhost:5178` and opens your browser. Press `Ctrl+C` in the terminal to quit. If the file won't run,
some archive tools drop the executable permission. Fix it once with `chmod +x osu_collection_manager`.

Some notes for Wine setups:

- **Finding osu!:** the app checks for a running osu!, then the usual locations (osu-winello's
  `~/.local/share/osu-wine/osu!`, `~/.wine`, Lutris `~/Games/*`, Bottles, Proton) and mounted drives. If yours is
  somewhere else, use **Browse…** in the first-run dialog (folders starting with a dot, like `.local`, are shown), or
  start the app with `--OsuPath="/path/to/osu!"`.
- **Songs folder:** osu! sometimes stores it as a Windows path like `D:\Games\osu!\Songs`. The app translates that using
  the Wine prefix's drive letters, so you don't need to set anything up.
- **Closing osu!:** the "close osu! first" check looks for the `osu!.exe` process, so it works with Wine too.
- Your settings are saved in `~/.local/share/OsuCollectionManager/settings.json`.

## First run

The app needs the folder that contains `osu!.db`. It looks for it in this order:

1. The `--OsuPath=...` option, if you used it.
2. The folder you picked last time (saved in `%LOCALAPPDATA%\OsuCollectionManager\settings.json` on Windows, or
   `~/.local/share/OsuCollectionManager/settings.json` on Linux).
3. Automatic detection. On Windows it checks for a running osu!, then the registry, then the usual install folders on
   your drives. On Linux it checks for a running osu!, then the usual Wine locations and mounted drives.

If it still can't tell, a **"Where is your osu! folder?"** window opens. It lists what it found, lets you click through
your folders with **Browse…** (folders containing `osu!.db` are marked), or lets you paste a path. The app remembers your
choice, and you can change it any time with **Change folder** in the top-right corner.

## Good to know

- **Close osu! before changing collections.** osu! rewrites `collection.db` when it exits and would undo your changes, so
  the app won't write anything while `osu!.exe` is running (this works under Wine too).
- **Your collections are backed up.** Every change to `collection.db` first saves a copy next to it, named
  `collection.db.bak-<date>`.
- **Downloaded maps show up after a restart.** Downloaded `.osz` files go into your `Songs` folder, and osu! imports
  them the next time it starts. Until then, those maps appear as "missing" in your collections.
- **Where the data comes from.** Profile info, top plays and community tags come from the public osu! website, and maps
  are downloaded from beatmap mirrors such as osu.direct. The app slows down its requests to be polite. If a site answers
  "429", just wait a minute and try again.
- **It's private.** The server only answers requests for `localhost` and rejects writes coming from other websites.

## Command-line options

You can add these when starting the app, for example `osu_collection_manager.exe --no-browser`:

| Option | What it does |
|---|---|
| `--no-browser` | Don't open the browser automatically. |
| `--urls=http://localhost:5555` | Use a different address or port (the default is `http://localhost:5178`). |
| `--OsuPath="C:\path\to\osu!"` | Use this osu! folder instead of detecting it or asking. On Linux, for example: `--OsuPath="$HOME/.local/share/osu-wine/osu!"`. |
| `--AutoDetect=false` | Don't try to find osu! automatically. |

## For developers

### Run from source

You'll need the [.NET 10 SDK](https://dotnet.microsoft.com/download), plus osu!stable and an internet connection as above.

```
dotnet run --project src
```

The console prints the address to open (`dotnet run` also opens your browser). To pass options, add `--` first, like
`dotnet run --project src -- --no-browser`.

### Build a release yourself

On Windows:

```
.\publish.ps1
```

This creates a self-contained single-file executable and a zip in `dist\`. The zip contains the `.exe`, the `wwwroot`
folder and `appsettings.json`, which must stay together.

On Linux (or WSL):

```
./publish.sh            # or ./publish.sh linux-arm64
```

This creates `dist/osu_collection_manager-linux-x64.tar.gz` with the same contents. Build it on Linux so the executable
permission is kept. Pushing a tag such as `v1.0.0` builds both downloads on GitHub and attaches them to a release (see
`.github/workflows/release.yml`).

### Tests

```
dotnet test tests/OsuCollectionManager.Tests
```

Some tests only make sense on Linux (Wine prefixes, `/proc`, case-sensitive files), so they're skipped on Windows. The CI
workflow runs them on Ubuntu. `tests/smoke.sh <path to executable>` starts a built copy and checks it over HTTP.

### Project layout

```
src/                            everything the app is made of
  Program.cs                    startup, API endpoints, single-instance + browser launch
  Osu/                          osu!.db reader and collection.db reader/writer
  Services/                     osu! folder detection, mirror and website clients, planner, importer, job manager
  Properties/, appsettings.json, osu_collection_manager.csproj
  wwwroot/                      the web page (plain HTML, CSS and JavaScript modules, no build step)
    index.html
    css/                        one stylesheet per area (base, layout, library, search, planner, ...)
    js/                         one module per tab (library, collections, search, planner, jobs, setup) plus core/ helpers
tests/                          unit tests (OsuCollectionManager.Tests) and smoke.sh, a start-and-check script for built copies
.github/workflows/              CI (tests on Linux and Windows) and the release build
publish.ps1, publish.sh         build the release executable + zip (Windows) or tar.gz (Linux)
```

The C# code serves `src/wwwroot` as static files and exposes the JSON API under `/api`. While developing, edit the files
in `src/wwwroot/` and refresh the browser.

| Piece | File |
|---|---|
| `osu!.db` reader (handles the float32 star ratings of db version 20250107 and later) | `src/Osu/OsuDatabase.cs` |
| `collection.db` read/write, atomic write and timestamped backup | `src/Osu/CollectionDatabase.cs` |
| osu! folder detection (Windows registry, Wine prefixes on Linux) | `src/Services/OsuLocator.cs`, `OsuLocator.Linux.cs`, `OsuInstall.cs` |
| Wine drive-letter translation and case-insensitive file names | `src/Services/WinePrefix.cs`, `PathCase.cs` |
| Mirror search, downloads with fallback and validation | `src/Services/MirrorClient.cs` |
| Profile, top plays and beatmap tags from public osu.ppy.sh pages (no API key) | `src/Services/OsuWebClient.cs` |
| Collection import and restore | `src/Services/CollectionImporter.cs` |
| Training categories and map selection | `src/Services/TrainingPlanner.cs` |
