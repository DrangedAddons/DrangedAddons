#include "overlay.h"
#include "core.h"
#include "json.h"
#include "imgui.h"
#include "imgui_impl_win32.h"
#include "imgui_internal.h" // RegisterUserTexture

#include <atomic>
#include <condition_variable>
#include <deque>
#include <memory>
#include <cstdarg>
#include <mutex>
#include <cmath>
#include <map>
#include <set>
#include <thread>

extern IMGUI_IMPL_API LRESULT ImGui_ImplWin32_WndProcHandler(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam);

namespace overlay {

ImFont* g_regular = nullptr;
ImFont* g_bold = nullptr;

namespace {

// ImGui and the state below. Re-entrant: Windows can call the window procedure again from inside ImGui's handling of
// a message (taking mouse capture sends one), on the same thread.
std::recursive_mutex g_lock;
HWND g_window = nullptr;
WNDPROC g_previous = nullptr;
std::atomic<bool> g_open = false;
ULONGLONG g_hintUntil = 0;
const char* g_api = "unknown";

// From the manager's hello.
struct Hotkey
{
    UINT vk = VK_TAB;
    bool ctrl = false, alt = false, shift = true;
} g_hotkey;

std::atomic<bool> g_connected = false;
std::string g_pendingProblem;

std::wstring Env(const wchar_t* name)
{
    wchar_t buffer[512];
    DWORD n = GetEnvironmentVariableW(name, buffer, 512);
    return n > 0 && n < 512 ? std::wstring(buffer, n) : std::wstring();
}

// ---- The pipe: 4-byte little-endian length, then UTF-8 JSON ----
// Opened for overlapped I/O, so the reader waiting for the manager never holds up a write. Messages go out from their
// own thread through a queue: whoever sends (the render thread, the window's thread) never waits on the pipe.

std::mutex g_queueLock;
std::condition_variable g_queued;
std::deque<std::string> g_outbox;

void Send(const std::string& message)
{
    if (!g_connected) return;
    {
        std::lock_guard guard(g_queueLock);
        if (g_outbox.size() > 256) return; // the manager isn't reading; drop rather than grow
        g_outbox.push_back(message);
    }
    g_queued.notify_one();
}

/// One whole read or write on the overlapped pipe handle; false if the pipe broke.
bool Transfer(HANDLE pipe, bool write, void* data, DWORD size)
{
    auto* p = (char*)data;
    while (size > 0)
    {
        OVERLAPPED o{};
        o.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        DWORD done = 0;
        BOOL ok = write ? WriteFile(pipe, p, size, nullptr, &o) : ReadFile(pipe, p, size, nullptr, &o);
        if (!ok && GetLastError() != ERROR_IO_PENDING)
        {
            CloseHandle(o.hEvent);
            return false;
        }
        ok = GetOverlappedResult(pipe, &o, &done, TRUE);
        CloseHandle(o.hEvent);
        if (!ok || done == 0) return false;
        p += done;
        size -= done;
    }
    return true;
}

void WriterThread(HANDLE pipe, std::shared_ptr<std::atomic<bool>> alive)
{
    while (*alive)
    {
        std::string message;
        {
            std::unique_lock lock(g_queueLock);
            g_queued.wait_for(lock, std::chrono::milliseconds(500), [] { return !g_outbox.empty(); });
            if (g_outbox.empty()) continue;
            message = std::move(g_outbox.front());
            g_outbox.pop_front();
        }
        DWORD length = (DWORD)message.size();
        if (!Transfer(pipe, true, &length, 4) || !Transfer(pipe, true, message.data(), length)) break;
    }
}

Shared g_state;
bool g_srgb = false;      // the backend draws into an sRGB image: pictures are converted to linear first
bool g_iniLoaded = false; // window positions for this game read (once its id is known)

struct Picture
{
    std::string hash;
    int width = 0, height = 0;
    std::vector<unsigned char> rgba;
    ImTextureData* texture = nullptr; // made in Frame, where the ImGui context is in use
};
std::map<std::string, Picture> g_pictures;

std::vector<unsigned char> Base64(const std::string& text)
{
    static int table[256];
    static bool ready = false;
    if (!ready)
    {
        for (int& v : table) v = -1;
        const char* letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        for (int i = 0; i < 64; i++) table[(unsigned char)letters[i]] = i;
        ready = true;
    }
    std::vector<unsigned char> out;
    out.reserve(text.size() * 3 / 4);
    int buffer = 0, bits = 0;
    for (unsigned char c : text)
    {
        if (table[c] < 0) continue; // padding, line breaks
        buffer = (buffer << 6 | table[c]) & 0xFFFFFF;
        bits += 6;
        if (bits >= 8)
        {
            bits -= 8;
            out.push_back((unsigned char)(buffer >> bits & 0xFF));
        }
    }
    return out;
}

void Handle(const json::Value& m)
{
    const auto& t = m["t"].str();
    std::lock_guard guard(g_lock);
    if (t == "hello")
    {
        if (m["vk"].num() > 0) g_hotkey = { (UINT)m["vk"].num(), m["ctrl"].flag(), m["alt"].flag(), m["shift"].flag() };
        if (!m["game"].str().empty()) g_state.game = m["game"].str();
        g_state.gameId = m["gameId"].str();
        g_state.lobbies = m["lobbies"].flag();
        g_state.addons = m["addons"].flag();
        g_state.you = m["you"];
    }
    else if (t == "state")
    {
        for (auto& [key, value] : m.fields)
        {
            if (key == "friends") g_state.friends = value;
            else if (key == "lobby") g_state.lobby = value;
            else if (key == "overview") g_state.overview = value;
        }
    }
    else if (t == "avatar")
    {
        auto& p = g_pictures[m["id"].str()];
        if (p.hash == m["hash"].str() && p.texture) return;
        p.hash = m["hash"].str();
        p.width = m["w"].num();
        p.height = m["h"].num();
        p.rgba = Base64(m["rgba"].str());
        if ((int)p.rgba.size() != p.width * p.height * 4) p.rgba.clear();
        p.texture = nullptr; // a new one is made next frame (the old one stays with ImGui: pictures rarely change)
    }
    else if (t == "toast")
    {
        g_state.toasts.push_back({ m["title"].str(), m["text"].str(), GetTickCount64() + 7000 });
        if (g_state.toasts.size() > 4) g_state.toasts.erase(g_state.toasts.begin());
    }
}

/// Turns pictures that arrived into ImGui textures. Under the lock, with the context in use.
void MakeTextures()
{
    static unsigned char linear[256];
    static bool ready = false;
    if (!ready)
    {
        for (int i = 0; i < 256; i++)
        {
            float c = i / 255.f;
            linear[i] = (unsigned char)((c <= 0.04045f ? c / 12.92f : powf((c + 0.055f) / 1.055f, 2.4f)) * 255.f + 0.5f);
        }
        ready = true;
    }
    for (auto& [id, p] : g_pictures)
    {
        if (p.texture || p.rgba.empty()) continue;
        auto* tex = IM_NEW(ImTextureData)();
        tex->Create(ImTextureFormat_RGBA32, p.width, p.height);
        auto* px = (unsigned char*)tex->GetPixels();
        memcpy(px, p.rgba.data(), p.rgba.size());
        if (g_srgb)
            for (size_t i = 0; i < p.rgba.size(); i += 4)
                px[i] = linear[px[i]], px[i + 1] = linear[px[i + 1]], px[i + 2] = linear[px[i + 2]];
        tex->UseColors = true;
        tex->SetStatus(ImTextureStatus_WantCreate);
        ImGui::RegisterUserTexture(tex);
        p.texture = tex;
    }
}

void PipeThread(std::wstring name)
{
    while (true)
    {
        HANDLE pipe = CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, nullptr);
        if (pipe == INVALID_HANDLE_VALUE)
        {
            Sleep(1000);
            continue;
        }
        {
            std::lock_guard guard(g_queueLock);
            g_outbox.clear();
        }
        g_connected = true;
        auto alive = std::make_shared<std::atomic<bool>>(true);
        std::thread writer(WriterThread, pipe, alive);
        Log("connected to the manager");
        Send(std::string("{\"t\":\"hello\",\"version\":") + json::Quote(Version) + ",\"api\":" + json::Quote(g_api) + ",\"pid\":" +
             std::to_string(GetCurrentProcessId()) + "}");
        if (!g_pendingProblem.empty()) Send("{\"t\":\"problem\",\"text\":" + json::Quote(g_pendingProblem) + "}");
        while (true)
        {
            DWORD length = 0;
            if (!Transfer(pipe, false, &length, 4) || length > 16 * 1024 * 1024) break;
            std::string text(length, '?');
            if (!Transfer(pipe, false, text.data(), length)) break;
            json::Value message;
            if (json::Parse(text, message)) Handle(message);
        }
        g_connected = false;
        *alive = false;
        g_queued.notify_one();
        CancelIoEx(pipe, nullptr);
        writer.join();
        CloseHandle(pipe);
        Log("lost the manager");
        Sleep(1000);
    }
}

void StartPipe()
{
    static std::once_flag once;
    std::call_once(once, [] {
        auto name = Env(L"DRANGED_OVERLAY_PIPE");
        if (!name.empty()) std::thread(PipeThread, L"\\\\.\\pipe\\" + name).detach();
    });
}

// ---- The game's window ----

int g_cursorShows = 0; // ShowCursor calls made while open, undone on close
// Posted to the game's window to close the overlay from the render thread (cursor changes belong to the window's thread).
constexpr UINT CloseMessage = WM_APP + 0x4D0;

void SetOpen(bool open)
{
    if (g_open == open) return;
    g_open = open;
    if (open)
    {
        ClipCursor(nullptr);
        while (ShowCursor(TRUE) < 0) g_cursorShows++;
    }
    else
    {
        for (; g_cursorShows > 0; g_cursorShows--) ShowCursor(FALSE);
    }
    Send(open ? "{\"t\":\"opened\"}" : "{\"t\":\"closed\"}");
}

// Keys and mouse buttons the game has seen go down and not yet up. While the overlay is open the game sees no presses,
// and only the releases of these, so it never gets a release it didn't see pressed (winit counts presses and
// releases for mouse capture).
std::set<UINT> g_gameHeld;

/// The virtual key a key or mouse-button message is about; 0 for anything else.
UINT InputKey(UINT message, WPARAM w)
{
    switch (message)
    {
    case WM_KEYDOWN: case WM_KEYUP: case WM_SYSKEYDOWN: case WM_SYSKEYUP: return (UINT)w;
    case WM_LBUTTONDOWN: case WM_LBUTTONUP: case WM_LBUTTONDBLCLK: return VK_LBUTTON;
    case WM_RBUTTONDOWN: case WM_RBUTTONUP: case WM_RBUTTONDBLCLK: return VK_RBUTTON;
    case WM_MBUTTONDOWN: case WM_MBUTTONUP: case WM_MBUTTONDBLCLK: return VK_MBUTTON;
    case WM_XBUTTONDOWN: case WM_XBUTTONUP: case WM_XBUTTONDBLCLK: return HIWORD(w) == XBUTTON1 ? VK_XBUTTON1 : VK_XBUTTON2;
    }
    return 0;
}

bool IsRelease(UINT message)
{
    return message == WM_KEYUP || message == WM_SYSKEYUP || message == WM_LBUTTONUP || message == WM_RBUTTONUP ||
           message == WM_MBUTTONUP || message == WM_XBUTTONUP;
}

bool IsHotkey(WPARAM vk)
{
    auto down = [](int key) { return (GetKeyState(key) & 0x8000) != 0; };
    std::lock_guard guard(g_lock);
    return vk == g_hotkey.vk && down(VK_CONTROL) == g_hotkey.ctrl && down(VK_MENU) == g_hotkey.alt && down(VK_SHIFT) == g_hotkey.shift;
}

LRESULT HandleMessage(HWND window, UINT message, WPARAM w, LPARAM l)
{
    if (message == CloseMessage)
    {
        SetOpen(false);
        return 0;
    }
    bool keyDown = message == WM_KEYDOWN || message == WM_SYSKEYDOWN;
    if (keyDown && !(l & (1 << 30)) && IsHotkey(w))
    {
        SetOpen(!g_open);
        return 0;
    }
    UINT key = InputKey(message, w);
    if (g_open)
    {
        if (keyDown && w == VK_ESCAPE)
        {
            SetOpen(false);
            return 0;
        }
        {
            std::lock_guard guard(g_lock);
            ImGui_ImplWin32_WndProcHandler(window, message, w, l);
        }
        if (message == WM_SETCURSOR && LOWORD(l) == HTCLIENT)
        {
            SetCursor(LoadCursorW(nullptr, MAKEINTRESOURCEW(32512))); // IDC_ARROW
            return TRUE;
        }
        if (message == WM_INPUT) return DefWindowProcW(window, message, w, l); // raw input: the game doesn't get it
        bool input = (message >= WM_KEYFIRST && message <= WM_KEYLAST) || (message >= WM_MOUSEFIRST && message <= WM_MOUSELAST);
        if (input)
        {
            // Only the release of something the game saw pressed; nothing else.
            if (!(key && IsRelease(message) && g_gameHeld.erase(key))) return 0;
        }
    }
    else if (key)
    {
        if (IsRelease(message)) g_gameHeld.erase(key);
        else g_gameHeld.insert(key);
    }
    return CallWindowProcW(g_previous, window, message, w, l);
}

LRESULT CALLBACK WindowProc(HWND window, UINT message, WPARAM w, LPARAM l)
{
    try
    {
        return HandleMessage(window, message, w, l);
    }
    catch (const std::exception& ex)
    {
        // Nothing may unwind into Windows or the game: log it and let the game handle the message.
        Log("window message %u failed: %s", message, ex.what());
    }
    catch (...)
    {
        Log("window message %u failed", message);
    }
    return CallWindowProcW(g_previous, window, message, w, l);
}

void DetachLocked()
{
    if (!g_window) return;
    if ((WNDPROC)GetWindowLongPtrW(g_window, GWLP_WNDPROC) == WindowProc)
        SetWindowLongPtrW(g_window, GWLP_WNDPROC, (LONG_PTR)g_previous);
    ImGui_ImplWin32_Shutdown();
    g_window = nullptr;
}

void Style()
{
    auto rgb = [](unsigned c, float a = 1) { return ImVec4((c >> 16 & 255) / 255.f, (c >> 8 & 255) / 255.f, (c & 255) / 255.f, a); };
    auto& s = ImGui::GetStyle();
    s.WindowRounding = 10;
    s.FrameRounding = 6;
    s.ChildRounding = 8;
    s.WindowPadding = ImVec2(20, 16);
    s.FramePadding = ImVec2(12, 6);
    s.ItemSpacing = ImVec2(10, 8);
    s.WindowBorderSize = 1;
    auto* c = s.Colors;
    c[ImGuiCol_WindowBg] = rgb(0x16181D);
    c[ImGuiCol_ChildBg] = rgb(0x1F232B);
    c[ImGuiCol_PopupBg] = rgb(0x1F232B);
    c[ImGuiCol_Border] = rgb(0x323844);
    c[ImGuiCol_Text] = rgb(0xE6E8EC);
    c[ImGuiCol_TextDisabled] = rgb(0x8B93A1);
    c[ImGuiCol_FrameBg] = rgb(0x282D37);
    c[ImGuiCol_FrameBgHovered] = rgb(0x323844);
    c[ImGuiCol_Button] = rgb(0x282D37);
    c[ImGuiCol_ButtonHovered] = rgb(0x323844);
    c[ImGuiCol_ButtonActive] = rgb(0x3A404C);
    c[ImGuiCol_Header] = rgb(0x282D37);
    c[ImGuiCol_HeaderHovered] = rgb(0x323844);
    c[ImGuiCol_CheckMark] = rgb(0xD4A72C);
    c[ImGuiCol_TitleBg] = rgb(0x16181D);
    c[ImGuiCol_TitleBgActive] = rgb(0x16181D);
    c[ImGuiCol_Separator] = rgb(0x323844);
    c[ImGuiCol_ScrollbarBg] = rgb(0x16181D);
    c[ImGuiCol_ScrollbarGrab] = rgb(0x3A404C);
    c[ImGuiCol_ModalWindowDimBg] = rgb(0x000000, 0.55f);
}

} // namespace

void Log(const char* format, ...)
{
    static std::mutex lock;
    char line[1024];
    va_list args;
    va_start(args, format);
    vsnprintf(line, sizeof line, format, args);
    va_end(args);
    std::lock_guard guard(lock);
    wchar_t path[MAX_PATH];
    if (!GetEnvironmentVariableW(L"LOCALAPPDATA", path, MAX_PATH)) return;
    std::wstring file = std::wstring(path) + L"\\DrangedAddons\\overlay.log";
    if (FILE* f = _wfopen(file.c_str(), L"a"))
    {
        SYSTEMTIME t;
        GetLocalTime(&t);
        fprintf(f, "%04d-%02d-%02d %02d:%02d:%02d [%lu %s] %s\n", t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond,
                GetCurrentProcessId(), g_api, line);
        fclose(f);
    }
}

void Problem(const std::string& what)
{
    Log("problem: %s", what.c_str());
    g_pendingProblem = what;
    Send("{\"t\":\"problem\",\"text\":" + json::Quote(what) + "}");
}

bool Wanted() { return !Env(L"DRANGED_OVERLAY_PIPE").empty() || StartOpen(); }
bool StartOpen() { return Env(L"DRANGED_OVERLAY_OPEN") == L"1"; }

const char* DumpPath()
{
    static std::string path = [] {
        auto w = Env(L"DRANGED_OVERLAY_DUMP");
        std::string utf8(w.size() * 3, '?');
        utf8.resize(WideCharToMultiByte(CP_UTF8, 0, w.data(), (int)w.size(), utf8.data(), (int)utf8.size(), nullptr, nullptr));
        return utf8;
    }();
    return path.empty() ? nullptr : path.c_str();
}

void SetApi(const char* api) { g_api = api; }

void Attach(HWND window)
{
    if (!window) return;
    std::lock_guard guard(g_lock);
    if (window == g_window) return;
    StartPipe();
    DetachLocked();
    if (!ImGui::GetCurrentContext())
    {
        ImGui::CreateContext();
        auto& io = ImGui::GetIO();
        io.IniFilename = nullptr;
        io.LogFilename = nullptr;
        io.ConfigFlags |= ImGuiConfigFlags_NoMouseCursorChange; // the window procedure sets the cursor
        Style();
        // Segoe UI like the manager, with Segoe Fluent Icons (Windows 11; MDL2 Assets on 10) merged in for the icons.
        wchar_t windows[MAX_PATH];
        GetWindowsDirectoryW(windows, MAX_PATH);
        auto font = [&](const wchar_t* name) {
            char path[MAX_PATH * 3];
            WideCharToMultiByte(CP_UTF8, 0, (std::wstring(windows) + L"\\Fonts\\" + name).c_str(), -1, path, sizeof path, nullptr, nullptr);
            return GetFileAttributesA(path) == INVALID_FILE_ATTRIBUTES ? std::string() : std::string(path);
        };
        auto icons = font(L"SegoeIcons.ttf");
        if (icons.empty()) icons = font(L"segmdl2.ttf");
        for (auto [file, out] : { std::pair{ L"segoeui.ttf", &g_regular }, std::pair{ L"segoeuib.ttf", &g_bold } })
        {
            auto path = font(file);
            if (path.empty()) continue;
            *out = io.Fonts->AddFontFromFileTTF(path.c_str());
            if (!icons.empty())
            {
                ImFontConfig merge;
                merge.MergeMode = true;
                merge.GlyphOffset.y = 2;
                io.Fonts->AddFontFromFileTTF(icons.c_str(), 16, &merge); // the offset needs a reference size; it scales with the text
            }
        }
        if (!g_regular) g_regular = io.Fonts->AddFontDefault();
        if (!g_bold) g_bold = g_regular;
        io.FontDefault = g_regular;
        ImGui::GetStyle().FontSizeBase = 16;
    }
    ImGui_ImplWin32_Init(window);
    // ImGui would position the IME window from the render thread; IME calls belong on the window's own thread.
    ImGui::GetPlatformIO().Platform_SetImeDataFn = nullptr;
    g_window = window;
    g_previous = (WNDPROC)SetWindowLongPtrW(window, GWLP_WNDPROC, (LONG_PTR)WindowProc);
    g_hintUntil = GetTickCount64() + 8000;
    if (StartOpen()) g_open = true;
    Log("attached to window %p", window);
}

void Detach()
{
    std::lock_guard guard(g_lock);
    DetachLocked();
}

bool Frame(const std::function<void()>& newFrame, const std::function<void()>& render)
{
    std::lock_guard guard(g_lock);
    if (!g_window) return false;
    bool hint = GetTickCount64() < g_hintUntil;
    auto now = GetTickCount64();
    std::erase_if(g_state.toasts, [now](auto& t) { return t.until < now; });
    if (!g_open && !hint && g_state.toasts.empty()) return false;
    try
    {
        if (!g_iniLoaded && !g_state.gameId.empty())
        {
            // Window positions and which windows were open, per game.
            static std::string ini;
            wchar_t local[MAX_PATH];
            GetEnvironmentVariableW(L"LOCALAPPDATA", local, MAX_PATH);
            std::wstring base = std::wstring(local) + L"\\DrangedAddons\\overlay-" + std::wstring(g_state.gameId.begin(), g_state.gameId.end());
            char path[MAX_PATH * 3];
            WideCharToMultiByte(CP_UTF8, 0, (base + L".ini").c_str(), -1, path, sizeof path, nullptr, nullptr);
            ini = path;
            ImGui::LoadIniSettingsFromDisk(ini.c_str());
            ImGui::GetIO().IniFilename = ini.c_str();
            LoadWindows(base + L"-windows.txt");
            g_iniLoaded = true;
        }
        newFrame();
        MakeTextures();
        ImGui_ImplWin32_NewFrame();
        ImGui::NewFrame();
        DrawUi(g_open, hint);
        ImGui::Render();
        render();
        return true;
    }
    catch (const std::exception& ex)
    {
        Log("frame failed: %s", ex.what());
    }
    catch (...)
    {
        Log("frame failed");
    }
    return false;
}

// ---- For the UI (core.h) ----

Shared& State() { return g_state; }

ImTextureData* Avatar(const std::string& id)
{
    auto it = g_pictures.find(id);
    return it == g_pictures.end() ? nullptr : it->second.texture;
}

void Act(const char* action, const std::string& fields)
{
    Log("action %s", action);
    Send(std::string("{\"t\":\"action\",\"a\":") + json::Quote(action) + (fields.empty() ? "" : "," + fields) + "}");
}

void Close()
{
    if (g_window) PostMessageW(g_window, CloseMessage, 0, 0);
}

bool Connected() { return g_connected; }

ULONGLONG SessionMilliseconds()
{
    FILETIME created, exited, kernel, user, now;
    GetProcessTimes(GetCurrentProcess(), &created, &exited, &kernel, &user);
    GetSystemTimeAsFileTime(&now);
    auto ticks = [](FILETIME f) { return (ULONGLONG)f.dwHighDateTime << 32 | f.dwLowDateTime; };
    return (ticks(now) - ticks(created)) / 10000;
}

void SetSrgbTarget(bool srgb) { g_srgb = srgb; }

std::string HotkeyText()
{
    std::string s;
    if (g_hotkey.ctrl) s += "Ctrl+";
    if (g_hotkey.alt) s += "Alt+";
    if (g_hotkey.shift) s += "Shift+";
    if (g_hotkey.vk == VK_TAB) return s + "Tab";
    if (g_hotkey.vk >= VK_F1 && g_hotkey.vk <= VK_F24) return s + "F" + std::to_string(g_hotkey.vk - VK_F1 + 1);
    char name[64] = {};
    UINT scan = MapVirtualKeyW(g_hotkey.vk, MAPVK_VK_TO_VSC);
    GetKeyNameTextA((LONG)(scan << 16), name, sizeof name);
    return s + (name[0] ? name : "?");
}

} // namespace overlay

// IM_ASSERT (imconfig_overlay.h): logged once per place, never fatal.
void OverlayImGuiAssert(const char* expression, const char* file, int line)
{
    static std::mutex lock;
    static std::set<std::pair<const char*, int>> seen;
    {
        std::lock_guard guard(lock);
        if (!seen.insert({ file, line }).second) return;
    }
    const char* name = strrchr(file, 92); // the last backslash
    overlay::Log("ImGui check failed: %s (%s:%d)", expression, name ? name + 1 : file, line);
}
