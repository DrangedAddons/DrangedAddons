#include "overlay.h"
#include "json.h"
#include "imgui.h"
#include "imgui_impl_win32.h"

#include <atomic>
#include <condition_variable>
#include <deque>
#include <memory>
#include <cstdarg>
#include <mutex>
#include <thread>

extern IMGUI_IMPL_API LRESULT ImGui_ImplWin32_WndProcHandler(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam);

namespace overlay {

void DrawUi(bool open, bool hint);

namespace {

std::mutex g_lock; // ImGui and the state below
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

void Handle(const json::Value& m)
{
    const auto& t = m["t"].str();
    if (t == "hello")
    {
        std::lock_guard guard(g_lock);
        if (m["vk"].num() > 0) g_hotkey = { (UINT)m["vk"].num(), m["ctrl"].flag(), m["alt"].flag(), m["shift"].flag() };
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

bool IsHotkey(WPARAM vk)
{
    auto down = [](int key) { return (GetKeyState(key) & 0x8000) != 0; };
    std::lock_guard guard(g_lock);
    return vk == g_hotkey.vk && down(VK_CONTROL) == g_hotkey.ctrl && down(VK_MENU) == g_hotkey.alt && down(VK_SHIFT) == g_hotkey.shift;
}

LRESULT CALLBACK WindowProc(HWND window, UINT message, WPARAM w, LPARAM l)
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
        // The game doesn't see input while the overlay is open, except keys and buttons being let go, so nothing
        // it was holding stays held.
        bool release = message == WM_KEYUP || message == WM_SYSKEYUP || message == WM_LBUTTONUP || message == WM_RBUTTONUP ||
                       message == WM_MBUTTONUP || message == WM_XBUTTONUP;
        if (message == WM_INPUT) return DefWindowProcW(window, message, w, l);
        if (!release && ((message >= WM_KEYFIRST && message <= WM_KEYLAST) || (message >= WM_MOUSEFIRST && message <= WM_MOUSELAST)))
            return 0;
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
    c[ImGuiCol_WindowBg] = rgb(0x16181D, 0.97f);
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
    }
    ImGui_ImplWin32_Init(window);
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
    if (!g_open && !hint) return false;
    newFrame();
    ImGui_ImplWin32_NewFrame();
    ImGui::NewFrame();
    DrawUi(g_open, hint);
    ImGui::Render();
    render();
    return true;
}

// ---- What it shows (a test panel for now) ----

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

void DrawUi(bool open, bool hint)
{
    auto& io = ImGui::GetIO();
    bool connected = g_connected;
    if (open)
    {
        // Dim the game, then the panel in the middle.
        ImGui::GetBackgroundDrawList()->AddRectFilled(ImVec2(0, 0), io.DisplaySize, IM_COL32(0, 0, 0, 140));
        ImGui::SetNextWindowPos(ImVec2(io.DisplaySize.x / 2, io.DisplaySize.y / 2), ImGuiCond_Always, ImVec2(0.5f, 0.5f));
        ImGui::SetNextWindowSize(ImVec2(560, 0), ImGuiCond_Always);
        ImGui::Begin("DrangedAddons", nullptr, ImGuiWindowFlags_NoCollapse | ImGuiWindowFlags_NoResize | ImGuiWindowFlags_NoMove |
                                                   ImGuiWindowFlags_NoSavedSettings);
        ImGui::SetWindowFontScale(1.4f);
        ImGui::TextUnformatted("DrangedAddons");
        ImGui::SetWindowFontScale(1.0f);
        ImGui::TextDisabled("In-game overlay %s · %s", Version, g_api);
        ImGui::Separator();
        ImGui::Text(connected ? "Connected to the manager." : "Not connected to the manager.");
        ImGui::Text("Your lobby, friends and lobby chat will show here.");
        static char chat[200] = "";
        ImGui::SetNextItemWidth(-1);
        ImGui::InputTextWithHint("##chat", "Type here to test the keyboard", chat, sizeof chat);
        if (ImGui::Button("Close")) PostMessageW(g_window, CloseMessage, 0, 0);
        ImGui::SameLine();
        ImGui::TextDisabled("%s or Esc closes this.", HotkeyText().c_str());
        ImGui::End();
    }
    else if (hint)
    {
        ImGui::SetNextWindowPos(ImVec2(io.DisplaySize.x - 16, 16), ImGuiCond_Always, ImVec2(1, 0));
        ImGui::SetNextWindowBgAlpha(0.9f);
        ImGui::Begin("##hint", nullptr, ImGuiWindowFlags_NoDecoration | ImGuiWindowFlags_AlwaysAutoResize | ImGuiWindowFlags_NoInputs |
                                            ImGuiWindowFlags_NoSavedSettings | ImGuiWindowFlags_NoFocusOnAppearing);
        ImGui::Text("DrangedAddons: %s opens your lobby and friends", HotkeyText().c_str());
        ImGui::End();
    }
}

} // namespace overlay
