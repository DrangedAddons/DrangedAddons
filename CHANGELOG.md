# Changelog

## 0.36.1
- About DrangedAddons and the README now describe playing together: your account, friends, and hosting or joining
  game lobbies.
- Re-release of 0.36.0 (the welcome prompt), which GitHub had listed behind 0.35.1 because both were published at
  the same time.

## 0.36.0
- **Welcome.** The first time the app opens it asks for the name friends see, filled in with your last Azeroth
  Survivors hero or Windows user name, and an optional profile picture, before you pick your first game. You can
  change both later from the account button. Anyone who never set a name or picture sees it once too.

## 0.35.1
- The co-op lobby shows each player's profile picture in their slot (the host's too), falling back to the letter tile
  for players without one.

## 0.35.0
- **Games from the platform.** Add a game (+) now also lists the games set up as Live profiles in DrangedAddons
  Platform, even when they have no addons yet. Adding one finds its install (in the folders its profile names, or
  the one you pick), and Play starts it with the profile's launch arguments. Its addons go in the profile's addons
  folder, and their saved settings are offered for deleting with them.
- The list is read at start, every 15 minutes and when Add a game opens, and kept for when the service can't be
  reached. A game already in your side bar keeps working if its profile is withdrawn.
- Addons tagged with a platform game's topic are filed under that game.
- A profile's name, description and icon also show for World of Warcraft and Azeroth Survivors when the platform sets
  them.

## 0.34.0
- **Pings in co-op lobbies.** While you host, your manager times each player's round trip to you every 2 seconds over
  their direct link, and every slot shows it beside Ready or In game ("In game · 34 ms"), for everyone in the lobby.
  Players on an older manager show no ping.
- The host's manager reports those pings to the lobby on the co-op service every 10 seconds, for the platform
  dashboard's session view (session time, players, roughly where each connects from, and ping over time). Only the
  dashboard's admin key can read it.

## 0.33.0
- **Friends.** The people icon above your picture in the side bar opens Friends; a red badge counts friend requests
  waiting for you.
  - **Search** finds members by name (anyone who has used DrangedAddons, online or not), with **Add friend**. Two
    members with the same name are told apart by a short tag.
  - **Requests** to you: Accept or Decline. Requests you sent wait under **Sent** (Cancel to take one back), and reach
    the other person even if they're offline when you send it.
  - **Friends** lists online friends first, with their picture, status, game and lobby ("In game · Azeroth Survivors ·
    hosting 2/5"). **Remove** ends the friendship for both of you.
- **Friends in co-op lobbies.** The invite list shows your friends first, under Friends, then everyone else under
  Others online. Open lobbies lists friends' lobbies first, marked Friend.
- Your account and friends are for every game now, not just Azeroth Survivors: you're online whenever DrangedAddons
  runs (still subject to Appear offline), and **In game** names the game, for WoW too.

## 0.32.0
- **Account.** Your picture sits at the bottom of the side bar, with a dot for your status; click it (or File → Account…)
  to set what other members see. Nothing to sign up for: it's kept on this PC, and the app's own install key is who
  you are.
  - **Picture**: any image, cut to the middle square. **Remove** goes back to your initials.
  - **Name**: left empty, it's the hero you last played in Azeroth Survivors, as before.
  - **Status**: Online, Away or Appear offline. **In game** shows on its own while Azeroth Survivors runs, and Away
    after 10 minutes without mouse or keyboard.
  - **Appear offline** keeps you connected, so you still see who's online and their lobbies, but nobody sees you
    and invites to you don't arrive. You can still invite others.
- Members' pictures and status dots show in the co-op window's lists, with their status beside their name when
  inviting.
- "Show me online for co-op" is gone from the menu: if it was off, you start on Appear offline.

## 0.31.2
- For Azeroth Survivors, ▶ opens one window with **Play**, **Host a lobby** and **Join a lobby**, instead of a dropdown with Play and Direct co-op…. Play starts the game the ordinary way and closes the window.

## 0.31.1
- The co-op window has no "Show me online" box: hosting keeps you online while the lobby is open, whatever the setting (still in the settings menu).

## 0.31.0
- **The lobby is laid out like a game lobby.** Hosting, and being in someone's lobby, opens a wider window:
  - A **Lobby code** card with Copy, and beside it **Who can join** for the host (Locked, Password, and a line saying who that lets in) or the host's name and whether the game has started for everyone else.
  - **Players ●●○○○ 2/5**: five slots in join order. Each filled slot has a coloured letter tile, the name with **Host** / **You** badges, and its state: Connecting…, Ready, In game or Couldn't connect. The host gets **Remove** on each player. Invited members hold a slot, dimmed, as Invited. Empty slots say Open slot; the host's **Invite** on one opens a pop-up of members online, with search.
  - **Lobby chat** beside the slots, for everyone in the lobby: what happens (who opened it, joined, left, was invited, declined or removed; lock and password changes; the game starting) and what players say. Messages go through the co-op service, under the name each player joined with; at most 300 characters, and 5 messages per 5 seconds each.
  - **Start game** / **Close lobby** for the host, **Leave lobby** for everyone else.
- Fixed: joining sometimes failed with "Couldn't connect" (about one in three on one PC). The joiner's first attempt could reach the host's port a moment before it was listening; Windows answered "unreachable" and that one answer ended the join. It now keeps trying, as it already did for other early failures. A connection that fails its security check now stops straight away instead of retrying.
- Co-op failures are written to `%LocalAppData%\DrangedAddons\coop.log` with their full details, so a "Couldn't connect" can be explained. It holds no keys or passwords and stays under 256 KB.
- DrangedAddons.exe now carries a copyright line and a plain version number (0.31.0, not 0.31.0+commit) in its file properties. Windows names the firewall rule it creates from the program's name, so it reads "DrangedAddons", the way Steam's reads "Steam".

## 0.30.0
- **Direct co-op is split into Host and Join.** ▶ → Direct co-op… opens on two buttons, **Host a lobby** and **Join a lobby**. Invites show above them on every screen.
  - **Host a lobby**: the lobby code with Copy; **Locked** (nobody new joins except members you invite); **Password**; who's in the lobby, with **Remove**; **Invite members** with search; **Start game** and **Close lobby**.
  - **Join a lobby**: enter a code, or pick from **Open lobbies** (members online who are hosting, with player count and whether there's a password). A password box appears when the lobby needs one. Once in, the screen shows the lobby's member list and **Leave lobby**.
  - Online users are called members for now.
  - Most explanation text is gone; labels and short errors only.
- **Show me online for co-op** is also in the settings menu now, as well as at the bottom of the co-op window. Both change the same setting.
- Lock, password and Remove are enforced by the co-op service, not just the host's app, so a modified client can't get past them.
  - The password never leaves the players' PCs: the host and joiner each send a hash of it salted with the lobby code. Five wrong passwords a minute from one address and it stops checking for a minute.
  - Inviting a member lets them in even when the lobby is locked or has a password.
  - A removed member can't rejoin that lobby, with the code, the password or an old invite.
  - The host sees which member actually joined (the co-op service checks it), not just the name they sent.
  - A locked lobby shows in Open lobbies without its code.
- Everyone needs 0.30.0: joining now sends the install's key, and older managers are told to update.

## 0.29.0
- **Party lobby for Direct co-op.** Inviting a friend (or clicking Create party) opens your party instead of starting your game straight away.
  - The party list shows you as host, each player as Connecting…, Ready or Couldn't connect, and friends you've invited who haven't joined yet. Everyone in the party sees the same list.
  - **Remove** takes a player out: their connection to your game closes and they're told they were removed.
  - **Start game** starts your game and everyone else's together. Anyone who joins after that starts straight away.
  - **Disband party** (host) and **Leave party** (others) end it; the others are told.
  - The party code is in the lobby, with Copy, for friends not in the online list.
  - The game password box is gone: only players in your party can reach your game now (below), so it wasn't protecting anything.
- **Security**
  - Players can't be impersonated. The online list used to show the same id a manager signs in with, so anyone could copy a friend's id, sign in as them, knock them offline and receive their invites. Each install now has a secret key; others only ever see a hash of it. Copying an id from the list gets you a different player.
  - Both ends of a co-op connection check each other. The joiner already only accepted the host's announced certificate; now the host also only accepts the certificate each joiner announced, and closes its listening port as soon as that player is in. Nobody else can connect to your game, even if they learn your address.
  - Invites are rate-limited (the same player at most once every 5 seconds, 20 a minute), so nobody can flood you with notifications.
  - Your address on your home network is only passed to players behind the same router (the only ones who could use it).
  - Names in the online list can't use invisible or right-to-left characters to look like someone else's. If two players online share a name, the list shows part of each one's id to tell them apart.
  - `msquic.dll` is checked byte-for-byte against the copy inside the exe before it's loaded, since the folder it's written to can be written by other programs.
  - When Windows asks about network access: allow DrangedAddons; Azeroth Survivors can be refused, as the game only talks to DrangedAddons on your PC.

## 0.28.2
- **Fixed: Direct co-op said "needs Windows 11" on Windows 11.** The released app is a single exe, and .NET leaves the QUIC library (`msquic.dll`) out of single-exe builds. The co-op window then believed QUIC wasn't available, so Invite and Host were greyed out. The exe now carries `msquic.dll` itself and writes it to `%LocalAppData%\DrangedAddons\msquic` the first time co-op is used.

## 0.28.1
- **Direct co-op moved to the Play button.** For Azeroth Survivors, ▶ now opens a small menu: **Play** starts the game as before, **Direct co-op…** opens the co-op window. It's no longer in the ⋯ menu. WoW's ▶ still starts the game straight away.

## 0.28.0
- **Direct co-op works without touching anyone's router, and friends invite each other.** 0.27.0 needed the host's router to open a port (UPnP), which plenty of routers don't allow. Now the two managers find each other through a small co-op service and punch a direct path through both routers, the way voice chat apps do. The game's connection then runs inside an encrypted link between the two PCs; nothing else needs installing, and nobody needs an account.
  - **Friends online**: while DrangedAddons runs, with Azeroth Survivors in the side bar, you show as online under the hero you last played. ⋯ → Direct co-op… lists who else is online, with a search box.
  - **Invite**: one click starts your game hosting and invites them. They get a notification ("Inami invites you to co-op"); clicking it and then **Join** connects and starts their game in the host's party. **Decline** tells the host. Invites don't open windows over a game you're playing.
  - **Lobby codes** still work for anyone not in the list: hosting shows a 6-character code, and "Join with a code" takes it.
  - "Show me as online so friends can invite me" (in the co-op window, on by default) turns your listing off.
  - Keep the co-op window open while you play: the connection runs through the app. Closing it, or the game, ends co-op.
  - If Windows asks whether DrangedAddons may use the network, allow it.
  - If a player's internet provider blocks direct connections (some mobile and shared-address connections), the window says so; they can still use the game's own co-op.
  - The co-op service only passes each side's address and the host's certificate fingerprint to the other side; the game traffic never goes through it. The joiner only accepts the host whose fingerprint it was given. Its code is in `coop-rendezvous/` (a Cloudflare Worker).
  - Experimental: it starts the game with settings it reads at start but doesn't document (`WOW_ROGUE_COOP_HOST`, `WOW_ROGUE_COOP_JOIN`, `WOW_ROGUE_COOP_PASSWORD`), so a game update can change them.
- Removed 0.27.0's UPnP port opening and typed addresses: the direct path replaces both.

## 0.27.0
- **Direct co-op for Azeroth Survivors** (game menu ⋯ → Direct co-op…). Starts the game connected straight to the host instead of through the game's co-op relay. The relay is overseas, so two players in Australia were seeing 500+ ms; a direct connection should be tens of ms. Nothing else needs installing.
  - **Host**: pick a port (27420 by default). The window asks your router for its internet address and shows it with the port and a Copy button, to send the others. Starting asks the router to forward that TCP port to this PC for 8 hours (UPnP), so there's no router setup by hand.
  - When the router doesn't answer UPnP, or refuses, the window says so and offers **Start anyway**: then the port has to be forwarded in the router's settings. When the internet provider shares one address between customers (CGNAT), nobody can connect in at all, and the window says to let someone else host.
  - **Join**: paste the host's `ip:port` and start. The last port and address are remembered.
  - Optional password, the same for everyone.
  - Experimental. It sets the environment variables the game reads at start (`WOW_ROGUE_COOP_HOST`, `WOW_ROGUE_COOP_JOIN`, `WOW_ROGUE_COOP_PASSWORD`). They're in the game's exe but not documented, so a game update can change or remove them.

## 0.26.0
- **Checking for updates does much less when nothing changed.** After the app has loaded the addon list once, a later check asks GitHub only whether the organisation's repo list changed. A "nothing changed" answer stops there, instead of asking again about every addon's release and branch. A new release still shows up: for 15 minutes after any repo is pushed (a tag push included), every check still asks each addon, because the release workflow publishes the release a minute or two after the tag and that doesn't change the repo list. Editing a release's notes without pushing anything waits until the next real change; that's an accepted trade for not polling every repo every minute. Signed-in checks are still every minute, and unsigned ones every 15 minutes. An unsigned "nothing changed" still counts as one of the 60 anonymous requests an hour, which is why that interval stays slow. The first check after the app opens still looks at each addon, because the list isn't in memory yet.
- **An addon's .toc is downloaded once.** Version, which game versions it's for, and the author all come from that one file. They used to be three separate downloads of the same file, one after another, every time a commit was new.
- **Icons are kept per commit.** The app remembers which commit an icon was saved from. A refresh, including a manual one, doesn't download it again while that commit is still the branch head. One addon changing no longer re-downloads every other addon's icon. A repo with no icon is remembered the same way, so it isn't asked about every time either.
- **Long version histories write their cache once.** Reading an addon's old versions (CraftRoute is the long one) used to rewrite the whole on-disk cache every 20 versions, including the unrelated HTTP cache, and did that work on the UI thread. The version cache is now written when the walk finishes, in a compact file, off the UI thread. Opening several addons' Versions tabs reads one history at a time, so they don't all hit GitHub together.
- **Reinstalling an older release doesn't read the whole history.** A tagged release is found in the release list. A beta walks commits newest-first and stops when that commit appears. Previously both read every historical .toc first.
- **Reinstalling several addons reports failures together.** If some of a selection can't be reinstalled, one message lists them. It used to open a dialog for each one.
- **A changelog and a screenshot are fetched once per session.** The Versions tab and the Changelog tab were each downloading CHANGELOG.md. A gallery thumbnail and the large view were each downloading the image. Both are shared until the org list actually changes.
- **The window does less work while you type in search.** The "N installed" counts in the version picker were recounting every addon in every game version on each keystroke, and the logo was decoded again each time. Counts are recounted when an install changes or the version list changes. The logo is kept and only redrawn when the game or version changes.
- **Deleting an addon looks through its saved settings once.** The confirm dialog and the delete itself were each walking the game's WTF (or saved-settings) folder.
- **A Git working copy isn't fetched from GitHub on every redraw** when its HEAD hasn't moved and it was fetched in the last two minutes. Status (uncommitted files, ahead, behind) is still read locally. After a pull, or any new local commit, the next check fetches again. HEAD is the commit git itself reports, so a worktree (where `.git` is a file, not a folder) counts the same as a normal checkout.
- Removed leftovers from earlier versions that nothing used anymore: the old "Released…" / "Release notes" line, the Versions-window enable flag, the uninstall tooltip (it also claimed settings were always kept), an unused "does this file exist?" GitHub call, and the side-bar flag that used to stop you removing the last game. The two global usings stay: the XAML compiler's temporary project doesn't get `System.IO` or `System.Net.Http` from the SDK's implicit usings, so deleting that file breaks the build.
- The line under an addon's name still shows its version and author, and the Installed, Latest and Author columns show them too. That's a layout choice left for a pass on the list, not a behavior change.
- "Show beta versions" still also reveals a GitHub release marked pre-release. Those are a different thing from an untagged beta build, and `/releases/latest` never offers a pre-release, so the list and the Versions tab can disagree for one. Left as it is: nobody in the group publishes pre-releases, and changing it would show or hide releases people already have.

## 0.25.3
- **Play** (a small ▶ next to the game version, like CurseForge's): starts the game from the app.
  - WoW goes through Battle.net, the same way Battle.net's own desktop shortcuts do
    (`World of Warcraft Launcher.exe --productcode=…`), so you're signed in and the version you've picked at the top
    is the one that starts. The product comes from the version folder's `.flavor.info` (`wow`, `wow_classic_era`,
    `wowf` …).
  - Games without versions start their own program (Azeroth Survivors: `AzerothSurvivors.exe`).
  - Greyed out, with a tooltip saying why, when the picked version isn't installed in the game folder.
- The WoW version picker shows each version's icon: Battle.net's version icons (Retail gold, Classic MoP jade,
  Classic Era Hardcore grey, Anniversary BC green) and Forever's own program icon. PTRs and betas show their live
  version's, like the big logos (Classic Beta shows Forever's).
- WoW's side bar icon is the icon of the version picked for it, and changes when you pick another.
- When none of a game's addons support the picked WoW version, the list says so, instead of saying no addons were
  found at all.

## 0.24.2
- A new install starts with no games: instead of an empty list it explains how to add one (+ in the side bar, or the
  **Add a game** button). Nothing is added for you. If you already use DrangedAddons, WoW stays in your side bar.
- The last game in the side bar can be removed too, which brings that page back.

## 0.24.1
- Any supported game can be added from **Add a game**, even before it has addons; they show up in its list as
  they're published. Its page says when none have been published yet.
- **Hide games already added** (ticked by default) next to the search box in Add a game.

## 0.24.0
- **More than one game.** A side bar on the left holds a button per game (with the game's own icon); click one to
  switch, and everything (list, versions, folder, settings) follows that game. Each game remembers its own folder
  and version. Right-click a game to remove it from the side bar.
- **+** in the side bar opens **Add a game**: a searchable grid of every supported game with its icon, a
  description and how many addons it has. **Add** then asks where the game is installed, filled in with the folder
  found on your PC (or the usual place), with Browse… and a check that the game really is there.
- Each game in the side bar shows its own icon (bundled with the app, so it's there before the game is found).
- **Azeroth Survivors** is the first game after WoW: addons go in `benilla-config\AddOns` and their saved settings
  live in `benilla-config\saved`. It has no versions, so the version picker is hidden for it. It shows up under +
  once its first addon is published.
- Auto-update covers every game in the side bar.
- "Change WoW folder…" is now "Change game folder…". Your WoW folder setting carries over.

## 0.23.6
- Scrollbars match the dark theme: a slim rounded grey bar (lighter when you hover or drag it) instead of Windows'
  white track and arrow buttons. Applies everywhere: the addon list, the details page and dialogs.

## 0.23.5
- A Git working copy no longer shows BETA when its .toc version is the latest release and it has no uncommitted
  changes or unpushed commits. Commits after the release tag that don't change the version (screenshots, workflow
  edits) used to flag it as a beta.

## 0.23.4
- The window can't be made so narrow that the list squashes: its minimum width leaves room for every column you've
  chosen plus about 24 characters of addon name (935 px with the default columns; more as you add columns, and the
  window widens to fit if it's narrower). Longer names still end in "…".

## 0.23.3
- Details page: the About box beside the README is gone. Its details are in the header's stats line instead: when it
  was installed, its folders (when they aren't just the addon's name), other game versions it's installed in,
  "Auto-update off" for a version picked in Versions, and a GitHub link. The README now uses the full width.

## 0.23.2
- The **Versions** tab shows the total, e.g. "Versions (320)". After the first page appears, the rest of the
  history is read in the background (about 15 seconds for CraftRoute the first time) and the count fills in; from
  then on it's remembered, so later opens show it straight away and **Show 20 more** doesn't wait.

## 0.23.1
- The **Versions** tab opens much faster on addons with a long history (CraftRoute with betas: 3 seconds the first
  time instead of 21). It shows 20 versions, with **Show 20 more** at the bottom; only the versions shown are looked
  up, and the releases, commit list and changelog are fetched at the same time instead of one after another.

## 0.23.0
- **New addon details page** (click an addon). The header shows its status, the action button and a ⋯ menu
  (Reinstall, Browse folder, View on GitHub, Delete), plus downloads, when it was last updated, its size on disk and
  the game versions it's for. Below that are four tabs:
  - **Overview**: the addon's README, with an About panel (installed and latest version, when it was installed, its
    folders, other game versions it's installed in, and a link to the repo).
  - **Changelog**: its CHANGELOG.md, or the release notes if it doesn't have one.
  - **Gallery**: images from the README and from a `screenshots`, `gallery`, `media` or `images` folder in the
    repo. Click one to see it large; ← → to move between them, Esc to close.
  - **Versions**: what the Versions window showed, now on the page. Click a version to see what changed.
- Downloads is GitHub's count of downloads of the addon's release files (including the app's own installs).
- "View changelog" in an addon's right-click menu opens the Changelog tab.

## 0.22.2
- Moving columns: drag a header and a ghost of it follows the cursor, while the column itself (header and rows,
  dimmed) moves to show where it will land. Let go to drop it; Esc cancels and puts it back.
- Columns can only be dragged in column edit mode (the columns icon), so they can't be moved by accident.

## 0.22.1
- Columns: the Version column is gone (Installed and Latest cover it); the default columns are now Installed, Latest
  and Author. "Released" is now **Release date**.
- Clicking **⋯** (or the filter icon) while its menu is open now closes the menu, instead of closing and reopening it.

## 0.22.0
- **Columns:** the list has Version and Author columns (still also shown under each addon's name), under a proper
  header bar. The My Addons / Browse tabs sit on a full-width line.
- **Choose your columns:** the new columns icon (next to filter and refresh) turns on column edit mode: ✕ on a header
  hides that column, and the bar under the header adds hidden ones back (Version, Author, Installed, Latest,
  Released). Reset goes back to Version and Author; Done or Esc leaves edit mode.
- **Drag a column header** left or right to move the column, any time. Your columns and their order are remembered.

## 0.21.1
- **Update all** turns grey when there's nothing to update, instead of a dimmed gold.
- Tickboxes are a quiet dark outline instead of bright white, and turn gold when ticked (select-all shows a gold
  dash when only some rows are ticked).

## 0.21.0
- **Update all** now sits between the tabs and the list. Check for updates is a refresh icon at the right of that row
  (hover it to see when it last checked); the app still checks on its own every so often.
- New **filter** icon next to refresh: Updates available, Up to date, Not installed, Beta versions or Git working
  copies, on top of the tab and search. The icon turns gold while a filter is on.
- **Show beta versions** moved to the File menu, next to Auto-update addons (the Auto-update tickbox is gone; the
  File menu item is the same setting).
- Classic Beta shows the WoW Forever logo with a BETA tag (that folder held the Forever beta).

## 0.20.0
- **New top bar:** the selected game version's logo, a larger search box, the game-version picker, and a **⋯** menu
  with Scan game folder, Open AddOns folder and Change WoW folder… (still in the File menu too; hover an item to
  see its folder). The old game-version panel and the big DrangedAddons header are gone, leaving more room for the list.
- Logos: Midnight for Retail, Mists of Pandaria Classic for Classic, Classic Hardcore for Classic Era, Burning Crusade
  Classic Anniversary for Anniversary, and WoW Forever. PTR, XPTR and Beta folders use their game's logo with a tag.
- Show beta versions, Auto-update, Check for updates and Update all moved to the right of the My Addons / Browse tabs.

## 0.19.0
- **Multi-select:** each addon row has a tickbox on the left, and the column header above the list has a
  select-all box for the addons currently shown. Ticked rows get a gold background.
- With anything ticked, a bar slides up from the bottom with **Update**, **Install**, **Reinstall** and **Delete**
  for the selection (each shows how many it applies to), plus ✕ to clear. Esc also clears the selection.
- Bulk delete asks once, lists the folders, and asks once whether to keep saved settings.

## 0.18.0
- **Scan game folder** (next to Open AddOns folder, and in the File menu) looks in your WoW folder again for game
  versions, e.g. one you've just installed, and keeps the selected game. The bar says what it found.
- The WoW folder path is no longer shown in the game-version bar (there wasn't room); hover **Open AddOns folder**,
  **Change WoW folder…** or **Scan game folder** to see the folder each one uses.

## 0.17.6
- Git working copies get the **BETA** bubble too, when the local code isn't exactly a tagged release: commits after
  the last release tag, or uncommitted changes. Exactly on a release tag (e.g. just after tagging) shows no bubble.

## 0.17.5
- An installed beta whose version has since been published as a tagged release is now treated as that release:
  the BETA bubble goes away (e.g. CraftRoute 0.83.3 installed from main just before v0.83.3 was tagged).

## 0.17.4
- Fixed "Couldn't load versions: … 403 (Forbidden)". A GitHub token that isn't allowed to see something (e.g. one
  tied to the org and awaiting approval) is now skipped in favour of a normal anonymous request, and GitHub's
  short-term "too many requests" limit is recognised and shown as a limit message instead of a raw error.
- When signed in, the token is also used for addon files (versions, icons, changelogs) on GitHub's own servers,
  which raises GitHub's limits there too. Errors now say which request failed.
- **Versions… for Git working copies**, view-only: browse every version and its changes, with no Install buttons.
- GitHub token steps corrected: GitHub requires an expiration date, and the resource owner should be your own
  account (choosing DrangedAddons makes the token wait for an admin's approval). An expired or revoked saved token
  is now called out in the token window and the usage meter.

## 0.17.3
- Fixed "This copy can't update itself: download it from the releases page" for copies not named exactly
  `DrangedAddons.exe` (browsers save repeat downloads as `DrangedAddons (1).exe`). Any release copy now updates
  itself, whatever it's called.
- A copy that can't replace itself where it is (e.g. in a protected folder) no longer sends you to GitHub: it
  downloads the new version and hands over to the installer, which upgrades or installs the proper copy.
- Clicking Restart to update while a check was still running did nothing; it now waits for the check and then
  updates.

## 0.17.2
- No changes: a release to test the app updating and restarting itself automatically.

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
