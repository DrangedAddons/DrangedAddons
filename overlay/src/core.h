// Between the overlay's core (overlay.cpp: pipe, window, frame) and its UI (ui.cpp). Everything here is only used
// under the overlay's lock, which Frame holds while the UI draws.
#pragma once
#include "json.h"
#include "imgui.h"
#include <string>
#include <vector>
#include <windows.h>

namespace overlay {

/// What the manager has told this overlay (docs/overlay.md, "The pipe").
struct Shared
{
    std::string game = "the game";
    std::string gameId;
    bool lobbies = false; // the game has lobbies: the Lobby and Lobby chat modules
    bool addons = false;  // the game has addons: Game overview lists them
    json::Value you;      // {id, name, avatar}
    json::Value friends;  // {friends:[player], incoming:[player], invites:[{from, code}]}
    json::Value lobby;    // null, or the lobby (see OverlayHub.Lobby in the manager)
    json::Value overview; // {addons:[{name, version, update}]}

    struct Toast
    {
        std::string title, text;
        ULONGLONG until = 0;
    };
    std::vector<Toast> toasts;
};

Shared& State();

/// A member's picture as an ImGui texture, or nullptr if there isn't one (yet).
ImTextureData* Avatar(const std::string& id);

/// Sends {"t":"action","a":action, ...fields} to the manager; fields is the rest of a JSON object's body, e.g.
/// "\"id\":\"abc\"" (already quoted), or empty.
void Act(const char* action, const std::string& fields = "");

/// Closes the overlay (on the game window's own thread, which owns the cursor).
void Close();

bool Connected();
std::string HotkeyText();

/// Milliseconds since the game started.
ULONGLONG SessionMilliseconds();

/// The fonts: regular and bold Segoe UI, both with Segoe Fluent Icons merged in (icons in the private use area).
extern ImFont* g_regular;
extern ImFont* g_bold;

/// The UI (ui.cpp): the whole overlay when open, toasts and the start hint when closed.
void DrawUi(bool open, bool hint);

/// ui.cpp: which windows are open, saved per game next to ImGui's window positions.
void LoadWindows(const std::wstring& file);

} // namespace overlay
