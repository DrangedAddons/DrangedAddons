// The overlay's core, shared by every graphics backend: the link to the manager, the game's window (hotkey and input
// while open), and the ImGui frame. A backend calls Attach with the window it presents to, then Frame once per present.
#pragma once
#include <windows.h>
#include <functional>
#include <string>

namespace overlay {

constexpr const char* Version = "0.1.0";

/// Which backend drew: "vulkan", "d3d11", "d3d12", "opengl". Sent to the manager in hello.
void SetApi(const char* api);

/// The window the game presents to. Subclasses it (hotkey, input while open); a new window replaces the old one.
void Attach(HWND window);

/// Leaves the game's window as it was (its window procedure back); the backend is going away.
void Detach();

/// Builds this frame's UI if there's anything to show: newFrame starts the backend's frame, render draws
/// ImGui::GetDrawData(). Both run under the overlay's lock (the window thread feeds input under it too).
/// Returns false, without calling either, when there's nothing to show.
bool Frame(const std::function<void()>& newFrame, const std::function<void()>& render);

/// Something went wrong that leaves the overlay off (logged and sent to the manager).
void Problem(const std::string& what);

/// A line in %LOCALAPPDATA%\DrangedAddons\overlay.log.
void Log(const char* format, ...);

/// Whether this process was started by the manager with the overlay on (DRANGED_OVERLAY_PIPE set).
bool Wanted();

/// Test hooks: DRANGED_OVERLAY_OPEN=1 starts open; DRANGED_OVERLAY_DUMP=<file.bmp> asks the backend for one frame.
bool StartOpen();
const char* DumpPath();

} // namespace overlay
