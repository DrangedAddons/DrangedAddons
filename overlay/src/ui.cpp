// What the overlay shows, laid out like the Steam overlay: the game dimmed, a top bar (game and session time, clock,
// back to game), a dock of modules along the bottom, and each module in its own window that can be moved, resized and
// closed. Which windows are open, and where, is kept per game. While the overlay is closed only toasts (and the
// start hint) show, and they take no input.
//
// Modules: Friends, Lobby and Lobby chat (games with lobbies), Game overview. The manager sends their state and runs
// what they ask for (core.h, Act); nothing here talks to the network.
#include "core.h"
#include "overlay.h"

#include <algorithm>
#include <cstdio>
#include <string>

namespace overlay {

namespace {

// ---- Look ----

ImU32 Rgb(unsigned c, int a = 255) { return IM_COL32(c >> 16 & 255, c >> 8 & 255, c & 255, a); }
ImVec4 Rgbf(unsigned c, float a = 1) { return ImVec4((c >> 16 & 255) / 255.f, (c >> 8 & 255) / 255.f, (c & 255) / 255.f, a); }

constexpr unsigned Accent = 0xD4A72C, Muted = 0x8B93A1, Ready = 0x6CC07A, Danger = 0xE05A4F, Line = 0x323844, Panel2 = 0x282D37;

/// A Segoe Fluent Icons glyph (private use area) as UTF-8.
std::string Icon(unsigned cp)
{
    char s[4] = { (char)(0xE0 | cp >> 12), (char)(0x80 | (cp >> 6 & 0x3F)), (char)(0x80 | (cp & 0x3F)), 0 };
    return s;
}

constexpr unsigned IconPeople = 0xE716, IconGroup = 0xE902, IconChat = 0xE8BD, IconInfo = 0xE946, IconClose = 0xE711,
                   IconAddFriend = 0xE8FA, IconLock = 0xE72E, IconCopy = 0xE8C8, IconPlay = 0xE768;

/// The name's letter on a tile coloured from the name, the way the manager draws members without a picture.
ImU32 TileColour(const std::string& name)
{
    unsigned h = 17;
    for (char c : name) h = h * 31 + (unsigned char)toupper((unsigned char)c);
    float hue = (float)(h % 360), s = 0.45f, l = 0.38f;
    float c = (1 - fabsf(2 * l - 1)) * s, x = c * (1 - fabsf(fmodf(hue / 60, 2) - 1)), m = l - c / 2;
    float r, g, b;
    if (hue < 60) r = c, g = x, b = 0;
    else if (hue < 120) r = x, g = c, b = 0;
    else if (hue < 180) r = 0, g = c, b = x;
    else if (hue < 240) r = 0, g = x, b = c;
    else if (hue < 300) r = x, g = 0, b = c;
    else r = c, g = 0, b = x;
    return IM_COL32((int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255), 255);
}

/// A member's picture (or letter tile), size x size, at the cursor.
void Picture(const std::string& id, const std::string& name, float size, float alpha = 1)
{
    auto* draw = ImGui::GetWindowDrawList();
    ImVec2 a = ImGui::GetCursorScreenPos(), b(a.x + size, a.y + size);
    if (auto* tex = Avatar(id))
        draw->AddImageRounded(tex->GetTexRef(), a, b, ImVec2(0, 0), ImVec2(1, 1), IM_COL32(255, 255, 255, (int)(255 * alpha)), size * 0.15f);
    else
    {
        draw->AddRectFilled(a, b, (TileColour(name) & 0x00FFFFFF) | ((ImU32)(255 * alpha) << 24), size * 0.15f);
        char letter[2] = { name.empty() ? '?' : (char)toupper((unsigned char)name[0]), 0 };
        ImGui::PushFont(g_bold, size * 0.5f);
        ImVec2 t = ImGui::CalcTextSize(letter);
        draw->AddText(ImVec2(a.x + (size - t.x) / 2, a.y + (size - t.y) / 2), IM_COL32(255, 255, 255, (int)(255 * alpha)), letter);
        ImGui::PopFont();
    }
    ImGui::Dummy(ImVec2(size, size));
}

unsigned StateColour(const std::string& state)
{
    if (state == "online") return Ready;
    if (state == "ingame") return 0x5DA9E9;
    if (state == "away") return Accent;
    return Muted;
}

bool AccentButton(const char* label, const ImVec2& size = ImVec2(0, 0))
{
    ImGui::PushStyleColor(ImGuiCol_Button, Rgbf(Accent));
    ImGui::PushStyleColor(ImGuiCol_ButtonHovered, Rgbf(0xE0B640));
    ImGui::PushStyleColor(ImGuiCol_ButtonActive, Rgbf(0xB98F1F));
    ImGui::PushStyleColor(ImGuiCol_Text, Rgbf(0x1A1A1A));
    bool clicked = ImGui::Button(label, size);
    ImGui::PopStyleColor(4);
    return clicked;
}

bool DangerButton(const char* label)
{
    ImGui::PushStyleColor(ImGuiCol_Text, Rgbf(Danger));
    bool clicked = ImGui::Button(label);
    ImGui::PopStyleColor();
    return clicked;
}

void Heading(const char* text)
{
    ImGui::PushFont(g_bold, 13);
    ImGui::PushStyleColor(ImGuiCol_Text, Rgbf(Muted));
    ImGui::TextUnformatted(text);
    ImGui::PopStyleColor();
    ImGui::PopFont();
}

void MutedText(const char* text)
{
    ImGui::PushStyleColor(ImGuiCol_Text, Rgbf(Muted));
    ImGui::TextWrapped("%s", text);
    ImGui::PopStyleColor();
}

/// Right-aligns the next item of the given width on the current line.
void AlignRight(float width)
{
    float x = ImGui::GetWindowContentRegionMax().x - width;
    ImGui::SameLine(std::max(x, ImGui::GetCursorPosX()));
}

float ButtonWidth(const char* label) { return ImGui::CalcTextSize(label, nullptr, true).x + ImGui::GetStyle().FramePadding.x * 2; }

std::string Wide(const wchar_t* w)
{
    char out[256];
    int n = WideCharToMultiByte(CP_UTF8, 0, w, -1, out, sizeof out, nullptr, nullptr);
    return n > 0 ? std::string(out) : std::string();
}

std::string Session()
{
    auto minutes = SessionMilliseconds() / 60000;
    if (minutes < 1) return "Just started";
    if (minutes < 60) return std::to_string(minutes) + (minutes == 1 ? " minute" : " minutes") + " this session";
    return std::to_string(minutes / 60) + " h " + std::to_string(minutes % 60) + " min this session";
}

std::string Field(const std::string& name, const std::string& value) { return json::Quote(name) + ":" + json::Quote(value); }

// ---- Windows and confirmations ----

enum Module { Friends, Lobby, Chat, Overview, ModuleCount };
const char* ModuleNames[] = { "friends", "lobby", "chat", "overview" };
bool g_shown[ModuleCount] = { true, true, false, false };
std::wstring g_windowsFile;
bool g_windowsDirty = false;

void SaveWindows()
{
    if (g_windowsFile.empty()) return;
    if (FILE* f = _wfopen(g_windowsFile.c_str(), L"w"))
    {
        for (int i = 0; i < ModuleCount; i++) fprintf(f, "%s=%d\n", ModuleNames[i], g_shown[i] ? 1 : 0);
        fclose(f);
    }
}

struct Confirm
{
    std::string title, text, button, action, fields;
    bool danger = false;
    bool pending = false;
} g_confirm;

void AskFirst(const std::string& title, const std::string& text, const std::string& button, const char* action,
              const std::string& fields = "", bool danger = false)
{
    g_confirm = { title, text, button, action, fields, danger, true };
}

void DrawConfirm()
{
    if (g_confirm.pending)
    {
        ImGui::OpenPopup("##confirm");
        g_confirm.pending = false;
    }
    auto& io = ImGui::GetIO();
    ImGui::SetNextWindowPos(ImVec2(io.DisplaySize.x / 2, io.DisplaySize.y / 2), ImGuiCond_Always, ImVec2(0.5f, 0.5f));
    ImGui::SetNextWindowSize(ImVec2(420, 0));
    if (!ImGui::BeginPopupModal("##confirm", nullptr, ImGuiWindowFlags_NoTitleBar | ImGuiWindowFlags_NoResize | ImGuiWindowFlags_NoMove))
        return;
    ImGui::PushFont(g_bold, 19);
    ImGui::TextUnformatted(g_confirm.title.c_str());
    ImGui::PopFont();
    ImGui::Spacing();
    MutedText(g_confirm.text.c_str());
    ImGui::Spacing();
    ImGui::Spacing();
    float w = ButtonWidth("Cancel") + ButtonWidth(g_confirm.button.c_str()) + ImGui::GetStyle().ItemSpacing.x;
    AlignRight(w);
    ImGui::NewLine();
    ImGui::SameLine(ImGui::GetWindowContentRegionMax().x - w);
    if (ImGui::Button("Cancel")) ImGui::CloseCurrentPopup();
    ImGui::SameLine();
    if (g_confirm.danger ? DangerButton(g_confirm.button.c_str()) : AccentButton(g_confirm.button.c_str()))
    {
        Act(g_confirm.action.c_str(), g_confirm.fields);
        ImGui::CloseCurrentPopup();
    }
    ImGui::EndPopup();
}

/// A module window; false (and nothing drawn) when it's closed or collapsed.
bool BeginModule(Module m, const char* title, ImVec2 position, ImVec2 size)
{
    auto& io = ImGui::GetIO();
    // First time only: then it's wherever the player left it (kept per game).
    ImGui::SetNextWindowPos(ImVec2(std::min(position.x, io.DisplaySize.x - size.x - 20), std::min(position.y, io.DisplaySize.y - size.y - 90)),
                            ImGuiCond_FirstUseEver);
    ImGui::SetNextWindowSize(size, ImGuiCond_FirstUseEver);
    ImGui::SetNextWindowSizeConstraints(ImVec2(280, 160), ImVec2(io.DisplaySize.x, io.DisplaySize.y));
    bool open = true;
    bool visible = ImGui::Begin(title, &open, ImGuiWindowFlags_NoCollapse);
    if (!open)
    {
        g_shown[m] = false;
        g_windowsDirty = true;
    }
    if (!visible) ImGui::End();
    return visible;
}

// ---- Modules ----

void DrawFriends()
{
    auto& io = ImGui::GetIO();
    if (!BeginModule(Friends, (Icon(IconPeople) + "  Friends###friends").c_str(), ImVec2(io.DisplaySize.x * 0.68f, io.DisplaySize.y * 0.14f),
                     ImVec2(360, std::min(640.f, io.DisplaySize.y * 0.66f))))
        return;
    auto& state = State();
    auto& f = state.friends;

    // You, at the top, like Steam's friends window.
    auto& you = state.you;
    if (you.kind == json::Value::Object)
    {
        Picture(you["id"].str(), you["name"].str(), 40);
        ImGui::SameLine();
        ImGui::BeginGroup();
        ImGui::PushFont(g_bold, 16);
        ImGui::TextUnformatted(you["name"].str().c_str());
        ImGui::PopFont();
        ImGui::PushStyleColor(ImGuiCol_Text, Rgbf(0x5DA9E9));
        ImGui::Text("In %s", state.game.c_str());
        ImGui::PopStyleColor();
        ImGui::EndGroup();
        ImGui::Separator();
    }
    if (f.kind != json::Value::Object)
    {
        MutedText(Connected() ? "Loading…" : "Not connected to DrangedAddons.");
        ImGui::End();
        return;
    }

    static char search[64] = "";
    ImGui::SetNextItemWidth(-1);
    ImGui::InputTextWithHint("##search", "Search friends", search, sizeof search);
    auto matches = [](const std::string& name) {
        if (!search[0]) return true;
        std::string a = name, b = search;
        for (auto& c : a) c = (char)tolower((unsigned char)c);
        for (auto& c : b) c = (char)tolower((unsigned char)c);
        return a.find(b) != std::string::npos;
    };

    ImGui::BeginChild("##list", ImVec2(0, 0), ImGuiChildFlags_None, ImGuiWindowFlags_NoBackground);

    // Lobby invites, then friend requests: both want an answer.
    for (auto& invite : f["invites"].items)
    {
        auto& from = invite["from"];
        ImGui::PushID(("invite" + from["id"].str()).c_str());
        Heading("LOBBY INVITE");
        Picture(from["id"].str(), from["name"].str(), 32);
        ImGui::SameLine();
        ImGui::BeginGroup();
        ImGui::Text("%s invited you", from["name"].str().c_str());
        if (AccentButton("Join"))
            AskFirst("Join " + from["name"].str() + "'s lobby?", "Your game restarts to join when they start theirs.", "Join", "acceptInvite",
                     Field("id", from["id"].str()) + "," + Field("code", invite["code"].str()));
        ImGui::SameLine();
        if (ImGui::Button("Decline")) Act("declineInvite", Field("id", from["id"].str()));
        ImGui::EndGroup();
        ImGui::PopID();
        ImGui::Separator();
    }
    if (!f["incoming"].items.empty())
    {
        Heading("FRIEND REQUESTS");
        for (auto& p : f["incoming"].items)
        {
            ImGui::PushID(("req" + p["id"].str()).c_str());
            Picture(p["id"].str(), p["name"].str(), 32);
            ImGui::SameLine();
            ImGui::BeginGroup();
            ImGui::TextUnformatted(p["name"].str().c_str());
            if (AccentButton("Accept")) Act("acceptFriend", Field("id", p["id"].str()));
            ImGui::SameLine();
            if (ImGui::Button("Decline")) Act("declineFriend", Field("id", p["id"].str()));
            ImGui::EndGroup();
            ImGui::PopID();
        }
        ImGui::Separator();
    }

    // Friends: in a game or online first, then away, then offline (folded).
    auto row = [&](const json::Value& p) {
        if (!matches(p["name"].str())) return;
        ImGui::PushID(p["id"].str().c_str());
        auto st = p["state"].str();
        bool offline = st == "offline";
        Picture(p["id"].str(), p["name"].str(), 32, offline ? 0.5f : 1);
        ImGui::SameLine();
        ImGui::BeginGroup();
        ImGui::PushStyleColor(ImGuiCol_Text, offline ? Rgbf(Muted) : Rgbf(0xE6E8EC));
        ImGui::TextUnformatted(p["name"].str().c_str());
        ImGui::PopStyleColor();
        ImGui::PushStyleColor(ImGuiCol_Text, Rgbf(StateColour(st)));
        ImGui::PushFont(nullptr, 13);
        auto& lobby = p["lobby"];
        if (lobby.kind == json::Value::Object)
            ImGui::Text("%s · lobby %d/%d%s", p["label"].str().c_str(), lobby["count"].num(), lobby["size"].num(5), lobby["locked"].flag() ? " · locked" : "");
        else
            ImGui::TextUnformatted(p["label"].str().c_str());
        ImGui::PopFont();
        ImGui::PopStyleColor();
        ImGui::EndGroup();
        if (p["invitable"].flag())
        {
            AlignRight(ButtonWidth("Invite"));
            if (ImGui::Button("Invite")) Act("invite", Field("id", p["id"].str()));
        }
        else if (lobby.kind == json::Value::Object && lobby["joinable"].flag())
        {
            AlignRight(ButtonWidth("Join"));
            if (ImGui::Button("Join"))
                AskFirst("Join " + p["name"].str() + "'s lobby?", "Your game restarts to join when they start theirs.", "Join", "join",
                         Field("code", lobby["code"].str()));
        }
        ImGui::PopID();
    };
    int online = 0, offline = 0;
    for (auto& p : f["friends"].items) (p["state"].str() == "offline" ? offline : online)++;
    char heading[64];
    snprintf(heading, sizeof heading, "ONLINE (%d)", online);
    Heading(heading);
    for (auto& p : f["friends"].items)
        if (p["state"].str() != "offline") row(p);
    if (online == 0) MutedText(f["friends"].items.empty() ? "No friends yet. Add them from DrangedAddons." : "Nobody's online.");
    if (offline > 0)
    {
        snprintf(heading, sizeof heading, "Offline (%d)###offline", offline);
        ImGui::PushStyleColor(ImGuiCol_Text, Rgbf(Muted));
        bool showOffline = ImGui::CollapsingHeader(heading);
        ImGui::PopStyleColor();
        if (showOffline)
            for (auto& p : f["friends"].items)
                if (p["state"].str() == "offline") row(p);
    }
    ImGui::EndChild();
    ImGui::End();
}

void DrawLobby()
{
    auto& io = ImGui::GetIO();
    if (!BeginModule(Lobby, (Icon(IconGroup) + "  Lobby###lobby").c_str(), ImVec2(io.DisplaySize.x * 0.30f, io.DisplaySize.y * 0.14f),
                     ImVec2(560, 470)))
        return;
    auto& l = State().lobby;
    if (l.kind != json::Value::Object)
    {
        ImGui::PushFont(g_bold, 18);
        ImGui::TextUnformatted("You're not in a lobby");
        ImGui::PopFont();
        MutedText("Host one and invite friends, or join with a code.");
        ImGui::Spacing();
        if (AccentButton("Host a lobby")) Act("host");
        ImGui::Spacing();
        static char code[8] = "";
        ImGui::SetNextItemWidth(140);
        ImGui::InputTextWithHint("##code", "Lobby code", code, sizeof code, ImGuiInputTextFlags_CharsUppercase);
        ImGui::SameLine();
        if (ImGui::Button("Join") && code[0])
            AskFirst("Join lobby " + std::string(code) + "?", "Your game restarts to join when the host starts theirs.", "Join", "join",
                     Field("code", code));
        ImGui::End();
        return;
    }

    bool host = l["role"].str() == "host";
    // Header: whose lobby, its code, who can get in, and whether the game's on.
    ImGui::PushFont(g_bold, 20);
    ImGui::TextUnformatted(host ? "Your lobby" : (l["host"].str() + "'s lobby").c_str());
    ImGui::PopFont();
    ImGui::SameLine();
    ImGui::PushStyleColor(ImGuiCol_Text, Rgbf(Accent));
    ImGui::PushFont(g_bold, 20);
    ImGui::Text("  %s", l["code"].str().c_str());
    ImGui::PopFont();
    ImGui::PopStyleColor();
    ImGui::SameLine();
    if (ImGui::SmallButton((Icon(IconCopy) + "##copy").c_str())) ImGui::SetClipboardText(l["code"].str().c_str());
    if (ImGui::IsItemHovered()) ImGui::SetTooltip("Copy the code");
    std::string access = l["locked"].flag() ? Icon(IconLock) + " Locked" : l["password"].flag() ? Icon(IconLock) + " Password" : "Open";
    std::string game = l["started"].flag() ? "Game started" : host ? "Not started" : "Waiting for the host to start";
    MutedText((access + "  ·  " + game).c_str());
    ImGui::Separator();

    // Slots: the host, then each player, then invited members, then open slots.
    int size = l["size"].num(5), filled = 0;
    auto slot = [&](const json::Value& s) {
        ImGui::PushID(s["id"].str().c_str());
        Picture(s["member"].str(), s["name"].str(), 36);
        ImGui::SameLine();
        ImGui::BeginGroup();
        ImGui::PushFont(g_bold, 16);
        ImGui::TextUnformatted(s["name"].str().c_str());
        ImGui::PopFont();
        for (auto [flag, badge] : { std::pair{ "host", "Host" }, std::pair{ "you", "You" } })
            if (s[flag].flag())
            {
                ImGui::SameLine();
                ImGui::TextColored(Rgbf(Accent), "%s", badge);
            }
        unsigned colour = s["failed"].flag() ? Danger : s["ok"].flag() ? Ready : Muted;
        ImGui::PushStyleColor(ImGuiCol_Text, Rgbf(colour));
        ImGui::PushFont(nullptr, 13);
        if (s["ping"].kind == json::Value::Number) ImGui::Text("%s · %d ms", s["state"].str().c_str(), s["ping"].num());
        else ImGui::TextUnformatted(s["state"].str().c_str());
        ImGui::PopFont();
        ImGui::PopStyleColor();
        ImGui::EndGroup();
        if (host && !s["host"].flag())
        {
            AlignRight(ButtonWidth("Remove"));
            if (ImGui::Button("Remove"))
                AskFirst("Remove " + s["name"].str() + "?", s["name"].str() + " can't rejoin this lobby. Their game restarts in solo.", "Remove", "kick",
                         Field("id", s["id"].str()), true);
        }
        ImGui::PopID();
        filled++;
    };
    for (auto& s : l["slots"].items) slot(s);
    for (auto& i : l["invited"].items)
    {
        if (filled >= size) break;
        ImGui::PushID(("inv" + i["member"].str()).c_str());
        Picture(i["member"].str(), i["name"].str(), 36, 0.5f);
        ImGui::SameLine();
        ImGui::BeginGroup();
        ImGui::TextColored(Rgbf(Muted), "%s", i["name"].str().c_str());
        ImGui::PushFont(nullptr, 13);
        ImGui::TextColored(Rgbf(Muted), "Invited");
        ImGui::PopFont();
        ImGui::EndGroup();
        ImGui::PopID();
        filled++;
    }
    for (; filled < size; filled++)
    {
        auto* draw = ImGui::GetWindowDrawList();
        ImVec2 a = ImGui::GetCursorScreenPos();
        draw->AddRect(a, ImVec2(a.x + 36, a.y + 36), Rgb(Line), 5);
        ImGui::Dummy(ImVec2(36, 36));
        ImGui::SameLine();
        ImGui::AlignTextToFramePadding();
        ImGui::TextColored(Rgbf(Muted), "Open slot");
        if (host)
        {
            AlignRight(ButtonWidth("Invite"));
            ImGui::PushID(filled);
            if (ImGui::Button("Invite")) g_shown[Friends] = true, g_windowsDirty = true;
            if (ImGui::IsItemHovered()) ImGui::SetTooltip("Invite from your friends list");
            ImGui::PopID();
        }
    }

    ImGui::Separator();
    if (host)
    {
        if (!l["started"].flag())
        {
            if (AccentButton((Icon(IconPlay) + "  Start game").c_str()))
                AskFirst("Start the game?", "Your game restarts to host co-op, and everyone's starts with it.", "Start game", "start");
            ImGui::SameLine();
        }
        if (DangerButton("Close lobby"))
            AskFirst("Close the lobby?", "Everyone in it is disconnected and your game restarts in solo.", "Close lobby", "close", "", true);
    }
    else if (DangerButton("Leave lobby"))
        AskFirst("Leave the lobby?", "Your game restarts in solo.", "Leave", "leave", "", true);
    ImGui::End();
}

void DrawChat()
{
    auto& io = ImGui::GetIO();
    if (!BeginModule(Chat, (Icon(IconChat) + "  Lobby chat###chat").c_str(), ImVec2(io.DisplaySize.x * 0.30f, io.DisplaySize.y * 0.14f + 490),
                     ImVec2(560, 280)))
        return;
    auto& l = State().lobby;
    if (l.kind != json::Value::Object)
    {
        MutedText("Lobby chat is for the players in your lobby.");
        ImGui::End();
        return;
    }
    float input = ImGui::GetFrameHeightWithSpacing() + ImGui::GetStyle().ItemSpacing.y;
    ImGui::BeginChild("##log", ImVec2(0, -input), ImGuiChildFlags_Borders);
    for (auto& line : l["log"].items)
    {
        ImGui::TextColored(Rgbf(Muted), "%s", line["time"].str().c_str());
        ImGui::SameLine();
        if (line["from"].kind == json::Value::String)
        {
            ImGui::PushFont(g_bold, 0);
            ImGui::TextColored(line["host"].flag() ? Rgbf(Accent) : Rgbf(0xE6E8EC), "%s:", line["from"].str().c_str());
            ImGui::PopFont();
            ImGui::SameLine();
            ImGui::TextWrapped("%s", line["text"].str().c_str());
        }
        else
        {
            ImGui::PushStyleColor(ImGuiCol_Text, Rgbf(Muted));
            ImGui::TextWrapped("%s", line["text"].str().c_str());
            ImGui::PopStyleColor();
        }
    }
    // Follow new lines unless the player scrolled up to read.
    static size_t seen = 0;
    if (l["log"].items.size() != seen && ImGui::GetScrollY() >= ImGui::GetScrollMaxY() - 40) ImGui::SetScrollHereY(1);
    seen = l["log"].items.size();
    ImGui::EndChild();
    static char text[300] = "";
    ImGui::SetNextItemWidth(-ButtonWidth("Send") - ImGui::GetStyle().ItemSpacing.x);
    bool enter = ImGui::InputTextWithHint("##say", "Say something…", text, sizeof text, ImGuiInputTextFlags_EnterReturnsTrue);
    ImGui::SameLine();
    if ((ImGui::Button("Send") || enter) && text[0])
    {
        Act("chat", Field("text", text));
        text[0] = 0;
        ImGui::SetKeyboardFocusHere(-1);
    }
    if (enter) ImGui::SetKeyboardFocusHere(-1); // keep typing
    ImGui::End();
}

void DrawOverview()
{
    auto& io = ImGui::GetIO();
    auto& state = State();
    if (!BeginModule(Overview, (Icon(IconInfo) + "  Game overview###overview").c_str(), ImVec2(io.DisplaySize.x * 0.04f, io.DisplaySize.y * 0.14f),
                     ImVec2(380, 420)))
        return;
    ImGui::PushFont(g_bold, 19);
    ImGui::TextUnformatted(state.game.c_str());
    ImGui::PopFont();
    MutedText(Session().c_str());
    ImGui::Separator();
    auto& addons = state.overview["addons"];
    if (!state.addons)
        MutedText("This game has no addons in DrangedAddons.");
    else if (addons.items.empty())
        MutedText("No addons installed for this game. Browse them in DrangedAddons.");
    else
    {
        int updates = 0;
        for (auto& a : addons.items) updates += a["update"].flag();
        char heading[64];
        snprintf(heading, sizeof heading, "ADDONS (%zu)", addons.items.size());
        Heading(heading);
        if (updates)
        {
            ImGui::PushStyleColor(ImGuiCol_Text, Rgbf(Accent));
            ImGui::TextWrapped("%d update%s available. Update in DrangedAddons; they apply the next time the game starts.", updates, updates == 1 ? "" : "s");
            ImGui::PopStyleColor();
        }
        ImGui::BeginChild("##addons", ImVec2(0, 0), ImGuiChildFlags_None, ImGuiWindowFlags_NoBackground);
        for (auto& a : addons.items)
        {
            ImGui::TextUnformatted(a["name"].str().c_str());
            const char* right = a["update"].flag() ? "Update available" : a["version"].str().c_str();
            AlignRight(ImGui::CalcTextSize(right).x);
            ImGui::TextColored(a["update"].flag() ? Rgbf(Accent) : Rgbf(Muted), "%s", right);
        }
        ImGui::EndChild();
    }
    ImGui::End();
}

// ---- The frame ----

void DrawTopBar()
{
    auto& io = ImGui::GetIO();
    auto& state = State();
    const auto flags = ImGuiWindowFlags_NoDecoration | ImGuiWindowFlags_NoMove | ImGuiWindowFlags_NoSavedSettings | ImGuiWindowFlags_NoBackground |
                       ImGuiWindowFlags_NoBringToFrontOnFocus | ImGuiWindowFlags_AlwaysAutoResize;
    // Left: the game and this session.
    ImGui::SetNextWindowPos(ImVec2(24, 18));
    ImGui::Begin("##game", nullptr, flags);
    ImGui::PushFont(g_bold, 22);
    ImGui::TextUnformatted(state.game.c_str());
    ImGui::PopFont();
    MutedText(Session().c_str());
    ImGui::End();

    // Middle: the clock and date, in the player's format.
    wchar_t time[64] = L"", date[128] = L"";
    GetTimeFormatEx(LOCALE_NAME_USER_DEFAULT, TIME_NOSECONDS, nullptr, nullptr, time, 64);
    GetDateFormatEx(LOCALE_NAME_USER_DEFAULT, DATE_LONGDATE, nullptr, nullptr, date, 128, nullptr);
    ImGui::SetNextWindowPos(ImVec2(io.DisplaySize.x / 2, 14), ImGuiCond_Always, ImVec2(0.5f, 0));
    ImGui::Begin("##clock", nullptr, flags);
    ImGui::PushFont(g_bold, 30);
    auto t = Wide(time);
    ImGui::SetCursorPosX((ImGui::GetWindowWidth() - ImGui::CalcTextSize(t.c_str()).x) / 2);
    ImGui::TextUnformatted(t.c_str());
    ImGui::PopFont();
    auto d = Wide(date);
    ImGui::SetCursorPosX((ImGui::GetWindowWidth() - ImGui::CalcTextSize(d.c_str()).x) / 2);
    ImGui::TextColored(Rgbf(Muted), "%s", d.c_str());
    ImGui::End();

    // Right: back to the game.
    ImGui::SetNextWindowPos(ImVec2(io.DisplaySize.x - 24, 18), ImGuiCond_Always, ImVec2(1, 0));
    ImGui::Begin("##back", nullptr, flags);
    ImGui::BeginGroup();
    ImGui::PushFont(g_bold, 17);
    ImGui::TextUnformatted("Back to game");
    ImGui::PopFont();
    auto key = "(" + HotkeyText() + ")";
    ImGui::SetCursorPosX(ImGui::GetCursorPosX() + ImGui::CalcTextSize("Back to game").x * 17 / 16 - ImGui::CalcTextSize(key.c_str()).x);
    ImGui::TextColored(Rgbf(Muted), "%s", key.c_str());
    ImGui::EndGroup();
    ImGui::SameLine();
    ImGui::PushFont(nullptr, 18);
    if (ImGui::Button((Icon(IconClose) + "##close").c_str(), ImVec2(40, 40))) Close();
    ImGui::PopFont();
    ImGui::End();
}

void DrawDock()
{
    auto& io = ImGui::GetIO();
    auto& state = State();
    struct Item { Module module; unsigned icon; const char* tip; bool available; };
    Item items[] = {
        { Friends, IconPeople, "Friends", true },
        { Lobby, IconGroup, "Lobby", state.lobbies },
        { Chat, IconChat, "Lobby chat", state.lobbies },
        { Overview, IconInfo, "Game overview", true },
    };
    ImGui::SetNextWindowPos(ImVec2(io.DisplaySize.x / 2, io.DisplaySize.y - 24), ImGuiCond_Always, ImVec2(0.5f, 1));
    ImGui::PushStyleVar(ImGuiStyleVar_WindowPadding, ImVec2(10, 8));
    ImGui::PushStyleColor(ImGuiCol_WindowBg, Rgbf(0x16181D, 0.92f));
    ImGui::Begin("##dock", nullptr, ImGuiWindowFlags_NoDecoration | ImGuiWindowFlags_NoMove | ImGuiWindowFlags_NoSavedSettings |
                                        ImGuiWindowFlags_AlwaysAutoResize | ImGuiWindowFlags_NoBringToFrontOnFocus);
    ImGui::PushFont(nullptr, 20);
    bool first = true;
    for (auto& item : items)
    {
        if (!item.available) continue;
        if (!first) ImGui::SameLine();
        first = false;
        bool on = g_shown[item.module];
        ImGui::PushStyleColor(ImGuiCol_Button, on ? Rgbf(Accent, 0.25f) : Rgbf(Panel2));
        ImGui::PushStyleColor(ImGuiCol_Text, on ? Rgbf(Accent) : Rgbf(0xE6E8EC));
        if (ImGui::Button((Icon(item.icon) + "##" + item.tip).c_str(), ImVec2(46, 40)))
        {
            g_shown[item.module] = !on;
            g_windowsDirty = true;
            if (!on) ImGui::SetWindowFocus(item.tip);
        }
        ImGui::PopStyleColor(2);
        if (ImGui::IsItemHovered())
        {
            ImGui::PushFont(nullptr, 15);
            ImGui::SetTooltip("%s", item.tip);
            ImGui::PopFont();
        }
    }
    ImGui::PopFont();
    ImGui::End();
    ImGui::PopStyleColor();
    ImGui::PopStyleVar();
}

void DrawToasts(bool open)
{
    auto& io = ImGui::GetIO();
    auto& toasts = State().toasts;
    float y = io.DisplaySize.y - (open ? 100 : 24);
    for (int i = (int)toasts.size() - 1; i >= 0; i--)
    {
        auto& t = toasts[i];
        ImGui::SetNextWindowPos(ImVec2(io.DisplaySize.x - 24, y), ImGuiCond_Always, ImVec2(1, 1));
        ImGui::SetNextWindowSize(ImVec2(330, 0));
        ImGui::PushStyleColor(ImGuiCol_WindowBg, Rgbf(0x1F232B, 0.96f));
        ImGui::PushStyleColor(ImGuiCol_Border, Rgbf(Accent, 0.8f));
        char name[32];
        snprintf(name, sizeof name, "##toast%d", i);
        ImGui::Begin(name, nullptr, ImGuiWindowFlags_NoDecoration | ImGuiWindowFlags_NoInputs | ImGuiWindowFlags_NoSavedSettings |
                                        ImGuiWindowFlags_NoFocusOnAppearing | ImGuiWindowFlags_NoNav | ImGuiWindowFlags_AlwaysAutoResize);
        ImGui::PushFont(g_bold, 16);
        ImGui::TextUnformatted(t.title.c_str());
        ImGui::PopFont();
        if (!t.text.empty()) MutedText(t.text.c_str());
        if (!open)
        {
            ImGui::PushFont(nullptr, 12);
            ImGui::TextColored(Rgbf(Muted), "%s to answer", HotkeyText().c_str());
            ImGui::PopFont();
        }
        y -= ImGui::GetWindowHeight() + 10;
        ImGui::End();
        ImGui::PopStyleColor(2);
    }
}

void DrawHint()
{
    auto& io = ImGui::GetIO();
    ImGui::SetNextWindowPos(ImVec2(io.DisplaySize.x - 24, 24), ImGuiCond_Always, ImVec2(1, 0));
    ImGui::SetNextWindowBgAlpha(0.92f);
    ImGui::Begin("##hint", nullptr, ImGuiWindowFlags_NoDecoration | ImGuiWindowFlags_AlwaysAutoResize | ImGuiWindowFlags_NoInputs |
                                        ImGuiWindowFlags_NoSavedSettings | ImGuiWindowFlags_NoFocusOnAppearing | ImGuiWindowFlags_NoNav);
    ImGui::PushFont(g_bold, 15);
    ImGui::TextColored(Rgbf(Accent), "DrangedAddons");
    ImGui::PopFont();
    ImGui::SameLine();
    ImGui::Text("%s opens your friends%s", HotkeyText().c_str(), State().lobbies ? " and lobby" : "");
    ImGui::End();
}

} // namespace

void LoadWindows(const std::wstring& file)
{
    g_windowsFile = file;
    if (FILE* f = _wfopen(file.c_str(), L"r"))
    {
        char line[64];
        while (fgets(line, sizeof line, f))
            for (int i = 0; i < ModuleCount; i++)
            {
                auto n = strlen(ModuleNames[i]);
                if (strncmp(line, ModuleNames[i], n) == 0 && line[n] == '=') g_shown[i] = line[n + 1] == '1';
            }
        fclose(f);
    }
}

void DrawUi(bool open, bool hint)
{
    auto& io = ImGui::GetIO();
    if (open)
    {
        ImGui::GetBackgroundDrawList()->AddRectFilled(ImVec2(0, 0), io.DisplaySize, IM_COL32(0, 0, 0, 150));
        DrawTopBar();
        auto& state = State();
        if (g_shown[Friends]) DrawFriends();
        if (state.lobbies && g_shown[Lobby]) DrawLobby();
        if (state.lobbies && g_shown[Chat]) DrawChat();
        if (g_shown[Overview]) DrawOverview();
        DrawDock();
        DrawConfirm();
        if (g_windowsDirty)
        {
            SaveWindows();
            g_windowsDirty = false;
        }
    }
    else if (hint)
        DrawHint();
    DrawToasts(open);
}

} // namespace overlay
