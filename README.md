# DrangedAddons

A small Windows app that installs and updates the addons published in the
[DrangedAddons](https://github.com/DrangedAddons) GitHub organisation, for World of Warcraft, Azeroth Survivors and
other games, and lets you play those games together with friends.

## Playing together

- **Your account:** pick a name and, if you like, a profile picture the first time the app opens. Friends see your
  status: Online, Away, In game (and which game), or Appear offline when you'd rather not be seen. No sign-up or
  password.
- **Friends:** search for other players by name, send and accept friend requests, and see who's online and what
  they're playing from the friends button in the side bar.
- **Lobbies:** host a lobby for a game and invite friends, or browse and join open lobbies, with friends' lobbies
  listed first. A lobby can be locked with a password. Each player's slot shows their picture, whether they're ready
  or in the game, and their ping to the host.
- **Hosting:** the host's PC runs the game session and players connect straight to it, usually with no port
  forwarding or server setup.
- Games join this as they're set up for it. Azeroth Survivors is the first: its Play button opens the lobby.

## How it works

- **More than one game:** the side bar on the left switches game, and **+** adds another game that has addons in the
  org. A repo belongs to a game by GitHub topic: `azeroth-survivors` for Azeroth Survivors; repos without a game
  topic are World of Warcraft addons. Supported games are defined in `src/Games.cs` (where the game is installed,
  its versions, where addons and their saved settings go).
- Lists every **public** repo in the org that has a GitHub **release with a `.zip` asset**.
  Pushing a version tag (which runs the repo's release Action) is what makes an update show up.
- Finds your WoW folder automatically (registry, then common locations). You pick the game version
  (Retail, Classic Era, Anniversary, …) at the top.
- **Play** starts the picked game version. WoW goes through Battle.net, like its desktop shortcuts:
  `World of Warcraft Launcher.exe --productcode=<product>`, with the product read from the version folder's
  `.flavor.info`. Other games start their own program (`GameDefinition.GetLaunch`).
- **Install / Update** downloads the release zip, stages it in `Interface\.DrangedAddons-staging`, then swaps
  the addon folders into `Interface\AddOns`. If anything fails, the old folders are put back.
  SavedVariables live in `WTF\`, so addon settings are never touched.
- **Show beta versions** (off by default) also offers untagged versions from each repo's default branch.
  A beta is built from the source at that commit with the same excludes as the release workflow
  (`.git*`, `.github`, `tools`, `build`), so nothing is released and nothing is announced.
- Click an addon for its **details page**: downloads (GitHub's count for the release files), last update, size on
  disk and game versions, then tabs for **Overview** (the repo's README), **Changelog** (`CHANGELOG.md`, or the
  release notes), **Gallery** (README images plus any images in a `screenshots`, `gallery`, `media` or `images`
  folder) and **Versions**.
- **Versions** lists every release (and, with betas on, every untagged version) with its CHANGELOG notes,
  and installs any of them. Untagged versions are found from the commits that changed the `.toc`: each version
  installs the last commit before the next version bump.
- **Delete** moves the addon folders to the Recycle Bin.
- A folder containing `.git` is treated as a **Git working copy** and is never overwritten or deleted.

Without signing in, GitHub allows 60 requests an hour per internet connection, so the app checks every 15
minutes. With a free GitHub token (File → GitHub token…, stored encrypted by Windows) or the GitHub CLI signed in
on the PC, it's 5,000 an hour and the app checks every minute; responses are cached with ETags, so unchanged
results don't count against that limit. Once a check has seen the list, a later check where nothing in the org
has changed is a single request.

App data (settings, install records, HTTP cache) lives in `%AppData%\DrangedAddons`.

## Requirements for an addon repo

- Public repo in the DrangedAddons org.
- Each release has a `.zip` asset whose top-level folder(s) are the addon folder(s), each with a `.toc`
  (e.g. `CraftRoute/CraftRoute.toc`).
- The release tag is the version (`v1.2.3`); it's compared numerically with the installed version.

## Getting the app

Download `DrangedAddons.exe` from the [latest release](https://github.com/DrangedAddons/DrangedAddons/releases/latest)
and run it. The first run offers to install it for your Windows account (`%LocalAppData%\Programs\DrangedAddons`,
Start menu and desktop shortcuts, listed in Settings → Apps; no administrator prompt). After that the app keeps
itself up to date: when a newer release exists it shows a "Restart to update" bar. This repo is the app itself, so it
never appears in its own addon list.

Windows SmartScreen may warn about an unrecognised app the first time: choose **More info → Run anyway**.

## Releasing a new version of the app

1. Bump `<Version>` in `src/DrangedAddons.csproj` and add a matching `## x.y.z` section to `CHANGELOG.md`.
2. Commit and push, then push the tag `vx.y.z`. The release workflow builds `DrangedAddons.exe` and publishes the
   release (it fails if the tag and `<Version>` disagree), and every copy of the app offers the update.

Addon icons come from an `icon.ico` (or `icon.png`) at the root of each addon repo.

## Platform dashboard

The admin dashboard for the co-op platform (services, live counts from the Worker's `/stats`, game profiles,
connectivity, security) is its own app in the private repo
[DrangedAddons/Platform](https://github.com/DrangedAddons/Platform). The Worker it reads stays here in
`coop-rendezvous/`.

## Build

```
dotnet build src
dotnet publish src -c Release -o publish   # → publish\DrangedAddons.exe (self-contained, no .NET install needed)
```
