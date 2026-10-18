# Changelog

## 0.17.1
- **Auto-update DrangedAddons** now updates the app whether its window is open or in the tray: the update bar
  shows the download filling, then it restarts and reopens the same way. It waits while an install is running or
  another window (Versions…, GitHub token, a dialog) is open, and tries again on the next check.

## 0.17.0
- **Auto-update checkbox** right under Update all (the same setting as File → Auto-update addons).
- **Download progress in the button.** Install / Update (in the list and on the details page) and Restart to update
  fill with gold as the download goes, with "Downloading 45%" (or "… 1.2 MB" when the size isn't known). The fill is
  quick (empty to full in about 0.6 s) but never ahead of the real download, so instant downloads still visibly
  fill and slow ones are followed exactly. The buttons keep a fixed width while their label changes.

## 0.16.0
- **File → Auto-update addons** (off by default): after each check, installs available updates for installed
  addons in every game version. Git working copies are skipped, and so is any addon where you picked an older
  version on purpose in Versions… (updating it yourself turns auto-update back on for it). Reports in the bar, or
  as a notification while the window is hidden.
- **File → Auto-update DrangedAddons** (off by default): updates the app itself, but only while it's hidden in the
  tray, and restarts straight back into the tray.

## 0.15.0
- **Right-click → Reinstall** puts a fresh copy of the installed version back (the same release, or the same beta
  commit), keeping settings. If the installed version can't be identified, it installs the latest.
- **GitHub usage meter** in the bottom bar: how many of this hour's GitHub checks are used (e.g. "12 / 5,000"),
  gold above 75% and red above 90%. Hover for details; click to open the GitHub token window.

## 0.14.0
- **Checks every minute** when signed in to GitHub (a saved token, or the GitHub CLI login), every 15 minutes
  without (GitHub only allows 60 anonymous checks an hour). Background checks are silent and only redraw the list
  when something changed.
- **File → GitHub token…**: shows how the app is connected and how many checks are left this hour, explains how
  to make a free token (5,000 checks an hour), checks it with GitHub and saves it encrypted by Windows. It can be
  removed again.
- When GitHub's limit is hit, a red bar under the header says so, when it resets, and offers **Add GitHub token**.

## 0.13.0
- **My Addons / Browse tabs.** My Addons lists only what's installed in the selected game version; Browse lists
  every available addon (installed ones included). Each tab shows its count, and the app remembers the last one
  (starting on Browse if nothing is installed yet).
- **Search box** on both tabs: filters by name, description or author as you type. Ctrl+F jumps to it, Esc clears it.

## 0.12.1
- A purple **BETA** bubble next to an addon's name when the version shown is an untagged beta build rather than a
  full tagged release (hover it for details). It replaces the "(beta)" text after version numbers.
- Hovering an addon in the list no longer pops up its description (it's on the details page).

## 0.12.0
- Each row in the list has one thing on the right: an **Install / Update / Pull** button when there's something to
  do, otherwise "✓ Up to date" (or "Git working copy"). The details page does the same.
- **Right-click an addon** for Open details, Browse folder, View changelog and, set apart in red, **Delete addon**.
- Deleting an addon asks to confirm, then whether to delete its saved settings too (account-wide and per
  character); "Keep settings" is the default. Everything goes to the Recycle Bin. The details page's Uninstall
  button uses the same steps.

## 0.11.0
- **Compact addon list.** Each addon is one slim row: icon, name, version (with "→ new version" when there's an
  update), author (from the addon's `.toc`) and its status.
- **Details page.** Click an addon for its details: a larger icon, description, version info and links, and the
  Install / Update / Pull, Versions… and Uninstall buttons. The back arrow (or Esc, or the mouse's back button)
  returns to the list.

## 0.10.0
- **Runs in the system tray.** Closing the window keeps DrangedAddons running in the tray; click the tray icon to
  open it again, right-click it for Open / Check for updates / Exit. File → Exit quits for real.
- While running it checks for addon and app updates every hour and shows a Windows notification for anything new
  (only while the window is hidden). Clicking the notification opens the app.
- File menu settings (both on by default): **Keep running in the tray when closed** and **Show update
  notifications**.
- Only one copy runs at a time: opening DrangedAddons again (e.g. from a pinned shortcut) brings the running
  window back instead of starting a second copy.

## 0.9.7
- After an update, the app tells Windows to refresh its cached icons, so Start menu, taskbar and desktop shortcuts
  pick up a new app icon without waiting for a sign-out.

## 0.9.6
- About no longer mentions GitHub sign-in.

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
