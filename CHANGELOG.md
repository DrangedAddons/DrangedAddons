# Changelog

## 0.9.5
- App icon: stronger contrast between the three golds (pale gold, rich gold, deep bronze), and each bar has its own
  black outline with the gaps between them kept open, so it stays distinct at small sizes and on light
  backgrounds. Small icon sizes get a proportionally thicker outline.
- The logo is shown, larger, next to the DrangedAddons heading in the window.
- Checking for app updates (Help menu or "Check for updates") reports in the bar under the header instead of a
  pop-up: "Checking for app updates…", then either the update offer or "You're on the latest version", which
  hides itself after a few seconds.
- About, confirmations and error messages use the app's own dark window instead of Windows message boxes, so
  there's no "ding".

## 0.9.4
- The app icon has a transparent background (no black tile), so the triangle fills the whole icon.

## 0.9.3
- The game-version drop-down shows just the count: "Classic Beta (1)".

## 0.9.2
- Fixed "Couldn't update DrangedAddons: Access to the path …DrangedAddons.exe.old is denied" when an older copy
  of the app was still running from a previous update. Leftover old copies are cleaned up on the next launch.

## 0.9.1
- The game-version drop-down shows how many DrangedAddons addons each game has installed
  (e.g. "Classic Beta · 1 installed").

## 0.8.1
- App icon: an impossible (Penrose) triangle in three golds on black, used for the window, taskbar, shortcuts and
  Settings → Apps.
- Dark title bar, matching the app's background.

## 0.8.0
- Installs like a normal app: the first time you run the downloaded `DrangedAddons.exe` it offers to install itself
  to `%LocalAppData%\Programs\DrangedAddons` with Start menu and desktop shortcuts and an entry in Settings → Apps
  (no administrator prompt, and self-updates keep working). Running a downloaded copy again just opens the installed
  one, upgrading it first if the download is newer.
- **File → Uninstall DrangedAddons…** (or Settings → Apps) removes the app; installed addons and app settings are kept.
- Dialogs use the modern Windows style.
- The window is just called "DrangedAddons"; the version is shown bottom-right and in Help → About.
- If the GitHub CLI is signed in on the PC (or `GH_TOKEN` / `GITHUB_TOKEN` is set), the app uses that login:
  5,000 GitHub checks an hour instead of 60 shared by the whole internet connection. Help → About shows which.

## 0.7.0
- Menu bar: **File** (Open AddOns folder, Change WoW folder…, Exit) and **Help** (Check for app updates,
  DrangedAddons on GitHub, About).
- **Help → Check for app updates** always reports back: it offers to update and restart straight away, or says
  you're already on the latest version.

## 0.6.0
- Addons can say which games they're for: an addon whose `.toc` has `## X-DrangedAddons-Games: _classic_beta_, _forever_`
  is only listed in those games (still listed where it's already installed). Without the line, it's listed everywhere.

## 0.5.0
- The app now updates itself: when a newer DrangedAddons release exists, a bar offers "Restart to update".
- Addon icons: `icon.ico` (or `icon.png`) from the root of each addon repo is shown next to its name.

## 0.3.0
- **Pull** button for addon folders that are Git working copies, when GitHub has commits you don't. Pulls are
  fast-forward only, so Git refuses rather than touch local commits or clashing uncommitted edits.

## 0.2.1
- Uninstall is always shown (greyed out with a reason when it doesn't apply).
- Game version picker is now a drop-down.

## 0.2.0
- **Show beta versions**: also offer untagged versions from each addon's main branch.
- **Versions…**: browse every version with its changelog notes and install any of them.

## 0.1.0
- First version: install, update and uninstall DrangedAddons addons from their GitHub releases.
