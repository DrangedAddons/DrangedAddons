// ImGui settings for the overlay (IMGUI_USER_CONFIG). An ImGui check that fails must never end the game: it's written
// to the overlay log once per place instead of calling abort().
#pragma once

void OverlayImGuiAssert(const char* expression, const char* file, int line);
#define IM_ASSERT(_EXPR) ((_EXPR) ? (void)0 : OverlayImGuiAssert(#_EXPR, __FILE__, __LINE__))
