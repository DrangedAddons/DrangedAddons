# In-game overlay

Shift+Tab (per game, changeable) opens the manager's lobby, friends and lobby chat inside the game, the way the
Steam overlay does. It draws into the game's own frames, so it works in windowed, borderless and exclusive
fullscreen, and never takes focus from the game or minimises it.

Decided with the owner (2026-11-01):
- One overlay for every game that isn't presence only. Turned on or off per game profile in the Platform (off for
  games whose anti-cheat would object); players can turn it off for themselves and change its key per game.
- Drawn inside the game. No separate overlay window, and nothing ever minimises the game.
- The overlay UI is native (Dear ImGui) inside the overlay DLL. The manager's WPF screens can't be reused: WPF
  ignores input forwarded to a window that isn't active and on screen (tested), and activating a window is what
  minimises fullscreen games.
- Vulkan and DirectX are both designed in from the start; OpenGL follows the same pattern.

## Pieces

```
 manager (C#)                              game process
 ┌───────────────────────┐   named pipe    ┌──────────────────────────────────────────┐
 │ OverlayHost           │◄──────────────►│ DrangedOverlay64.dll                       │
 │  launches the game    │  state ↓        │  ┌ core: pipe client, state, UI (ImGui),   │
 │  with the overlay on, │  actions ↑      │  │       input capture (Win32 wndproc)      │
 │  serves lobby/friends │                 │  ├ backend: Vulkan  (implicit layer)        │
 │  /chat state, runs    │                 │  ├ backend: D3D11   (Present hook)          │
 │  actions              │                 │  ├ backend: D3D12   (Present hook + queue)  │
 └───────────────────────┘                 │  └ backend: OpenGL  (wglSwapBuffers hook)   │
                                           └──────────────────────────────────────────┘
```

Everything above the backends is the same for every game. A backend is written once per graphics API, never per
game. Per-game facts live in the game's profile.

### Getting into the game

| API | How the DLL gets in | How it draws |
|---|---|---|
| Vulkan | Registered as an implicit Vulkan layer (HKCU, no admin). Its manifest's `enable_environment` means the Vulkan loader only loads it when the game was started with `DRANGED_OVERLAY=<pipe>`, which only the manager sets. No injection. | Wraps `vkCreateSwapchainKHR` (keeps the images, adds the usage it needs) and `vkQueuePresentKHR` (draws, chained on the game's semaphores). ImGui's Vulkan renderer. |
| D3D11 / D3D12 | The manager starts the game suspended, loads the DLL into it (`LoadLibraryW` on a remote thread), then resumes it. | Hooks `IDXGISwapChain::Present`/`Present1`/`ResizeBuffers` (found from a throwaway swap chain) with MinHook. D3D12 also hooks `ID3D12CommandQueue::ExecuteCommandLists` to learn the swap chain's queue. ImGui's DX11/DX12 renderers. |
| OpenGL | Same injection as DirectX. | Hooks `wglSwapBuffers`. ImGui's OpenGL3 renderer. |

The DLL decides at runtime which backend applies: loaded as a layer means Vulkan; injected means it installs the
DXGI and OpenGL hooks and uses whichever the game calls first. A game using two (rare) gets the overlay on the one
that presents.

The profile says which way in: `overlay` (on/off, done), plus a new `overlayApi`: `vulkan`, `directx`, `opengl` or
`auto` (inject and hook all). And a general `launchEnv` list (name=value) the manager sets when it starts the game.
Azeroth Survivors: `overlayApi: vulkan`, `launchEnv: WGPU_BACKEND=vulkan` (it renders with wgpu, which honours it).

### Input

The core subclasses the game's window procedure (found from the swap chain's window). While the overlay is closed it
only watches for the hotkey and passes everything on. While open it:
- feeds mouse and keyboard messages to ImGui and does not pass them to the game (including `WM_INPUT`, so raw-input
  games stop moving too);
- shows the cursor and lifts the game's cursor clip, and puts both back on close;
- closes on the hotkey or Esc.
The game keeps focus throughout.

### What it shows

Panels, in the manager's colours: the lobby (slots with pictures and pings, invite, remove, start, leave or close,
new lobby), friends online (invite, join a friend's lobby), lobby chat, and a small "Shift+Tab" hint for a few
seconds after the game starts. Invites arriving while playing show as a toast in the corner without opening the
overlay.

### The pipe

`\\.\pipe\DrangedOverlay-<random>`, created by the manager per game launch; the name goes in `DRANGED_OVERLAY`.
Length-prefixed JSON messages.
- Manager → overlay: `hello` (hotkey, colours, game name), `state` (lobby, friends, chat, whole snapshots, sent on
  change), `avatar` (id, hash, PNG bytes), `toast`.
- Overlay → manager: `hello` (DLL version, API, window size), `action` (`invite`, `kick`, `start`, `leave`, `close`,
  `chat`, `join`, `newLobby`...), `opened`/`closed`, `problem` (couldn't hook, unsupported format), for the log.

The manager owns all state and does all networking; the DLL only draws and reports clicks. A DLL from an older
manager ignores messages it doesn't know.

## Safety

- Only processes the manager starts get the overlay: the layer only loads with `DRANGED_OVERLAY` set, and
  injection only happens at a launch the manager makes.
- Profiles turn it off for games with anti-cheat. The player can turn it off too.
- The DLL is signed when code signing is set up; until then antivirus may flag injection (not the Vulkan layer).
- Anything that fails (no hook, unknown format, HDR swap chain) leaves the game untouched and reports a problem.

## Build

`overlay/` in this repo: C++ with Visual Studio 2022 Build Tools (also on GitHub Actions). Third-party code is
vendored (copied in, pinned versions) under `overlay/third_party/`: Dear ImGui (MIT), Vulkan-Headers (Apache-2.0),
MinHook (BSD-2-Clause). The release workflow builds the DLL and the manager ships it next to its exe.

## Order of work

1. DLL skeleton, pipe, manager side (`OverlayHost`), Vulkan layer drawing a test panel. Tested with a small Vulkan
   test program, then in Azeroth Survivors by the owner.
2. Input capture and the real panels.
3. DXGI hooks and injection, tested with a small D3D11 and D3D12 test program.
4. OpenGL.
5. Release wiring and the manager's overlay settings (already written, held back).
