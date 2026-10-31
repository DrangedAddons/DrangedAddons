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
#include <set>
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
                   IconAddFriend = 0xE8FA, IconLock = 0xE72E, IconCopy = 0xE8C8, IconPlay = 0xE768, IconMore = 0xE712;

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
    if (state == "online") return 0x3BA55D;
    if (state == "ingame") return 0x4C9BF0;
    if (state == "away") return 0xE8A33A;
    return 0x747F8D;
}

/// A member in a friends list: round picture (or initials) with a status dot, as the manager's Friends window draws them.
void Round(const std::string& id, const std::string& name, float size, const std::string& state)
{
    auto* draw = ImGui::GetWindowDrawList();
    ImVec2 a = ImGui::GetCursorScreenPos(), b(a.x + size, a.y + size), c(a.x + size / 2, a.y + size / 2);
    float alpha = state == "offline" ? 0.55f : 1;
    if (auto* tex = Avatar(id))
        draw->AddImageRounded(tex->GetTexRef(), a, b, ImVec2(0, 0), ImVec2(1, 1), IM_COL32(255, 255, 255, (int)(255 * alpha)), size / 2);
    else
    {
        draw->AddCircleFilled(c, size / 2, Rgb(Panel2));
        draw->AddCircle(c, size / 2, Rgb(Line));
        std::string initials;
        for (size_t i = 0; i < name.size() && initials.size() < 2; i++)
            if (i == 0 || name[i - 1] == ' ') initials += (char)toupper((unsigned char)name[i]);
        ImGui::PushFont(g_bold, size * 0.4f);
        ImVec2 t = ImGui::CalcTextSize(initials.c_str());
        draw->AddText(ImVec2(c.x - t.x / 2, c.y - t.y / 2), Rgb(Accent, (int)(255 * alpha)), initials.c_str());
        ImGui::PopFont();
    }
    if (!state.empty())
    {
        float r = size * 0.18f;
        ImVec2 dot(b.x - r + 1, b.y - r + 1);
        draw->AddCircleFilled(dot, r + 2, Rgb(0x16181D));
        draw->AddCircleFilled(dot, r, Rgb(StateColour(state)));
    }
    ImGui::Dummy(ImVec2(size, size));
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

enum Module { Friends, Lobby, Chat, Overview, Stage, ModuleCount };
const char* ModuleNames[] = { "friends", "lobby", "chat", "overview", "stage" };
bool g_shown[ModuleCount] = { true, true, false, false, false };

/// What the New lobby window has chosen so far.
struct Staging
{
    bool locked = false;
    char password[64] = "";
    std::set<std::string> bring;  // party ids from this lobby coming along
    std::set<std::string> invite; // member ids to invite
} g_stage;

/// Opens the New lobby window: everyone in this lobby (if hosting) ticked to come along, and <invite> ticked to invite.
void OpenStage(const std::string& invite)
{
    g_stage = {};
    auto& l = State().lobby;
    if (l.kind == json::Value::Object && l["role"].str() == "host")
    {
        g_stage.locked = l["locked"].flag();
        for (auto& s : l["slots"].items)
            if (!s["host"].flag()) g_stage.bring.insert(s["id"].str());
    }
    if (!invite.empty()) g_stage.invite.insert(invite);
    g_shown[Stage] = true;
    ImGui::SetWindowFocus("###stage");
}
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
bool BeginModule(Module m, const char* title, ImVec2 position, ImVec2 size, ImGuiWindowFlags flags = 0)
{
    auto& io = ImGui::GetIO();
    // First time only: then it's wherever the player left it (kept per game).
    ImGui::SetNextWindowPos(ImVec2(std::min(position.x, io.DisplaySize.x - size.x - 20), std::min(position.y, io.DisplaySize.y - size.y - 90)),
                            ImGuiCond_FirstUseEver);
    ImGui::SetNextWindowSize(size, ImGuiCond_FirstUseEver);
    ImGui::SetNextWindowSizeConstraints(ImVec2(280, 160), ImVec2(io.DisplaySize.x, io.DisplaySize.y));
    bool open = true;
    bool visible = ImGui::Begin(title, &open, ImGuiWindowFlags_NoCollapse | flags);
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
        Round(you["id"].str(), you["name"].str(), 40, "ingame");
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

    // Filters friends; from two letters the manager also searches members (anyone who's used it) to add.
    static char search[64] = "";
    static std::string asked;
    static ULONGLONG typedAt = 0;
    ImGui::SetNextItemWidth(-1);
    if (ImGui::InputTextWithHint("##search", "Search friends, or find members to add", search, sizeof search)) typedAt = GetTickCount64();
    std::string query = search;
    if (query != asked && GetTickCount64() - typedAt > 350)
    {
        asked = query;
        Act("search", Field("q", query.size() >= 2 ? query : ""));
    }
    auto matches = [](const std::string& name) {
        if (!search[0]) return true;
        std::string a = name, b = search;
        for (auto& c : a) c = (char)tolower((unsigned char)c);
        for (auto& c : b) c = (char)tolower((unsigned char)c);
        return a.find(b) != std::string::npos;
    };

    ImGui::BeginChild("##list", ImVec2(0, 0), ImGuiChildFlags_None, ImGuiWindowFlags_NoBackground);

    // Members found who aren't friends yet.
    auto& found = f["search"];
    if (query.size() >= 2 && found.kind == json::Value::Object && found["query"].str() == query)
    {
        Heading("MEMBERS");
        if (found["results"].items.empty()) MutedText(("Nobody called \u201C" + query + "\u201D has used DrangedAddons yet.").c_str());
        for (auto& p : found["results"].items)
        {
            ImGui::PushID(("found" + p["id"].str()).c_str());
            Round(p["id"].str(), p["name"].str(), 32, "");
            ImGui::SameLine();
            ImGui::BeginGroup();
            ImGui::TextUnformatted(p["name"].str().c_str());
            auto relation = p["relation"].str();
            if (!relation.empty())
            {
                ImGui::PushFont(nullptr, 13);
                ImGui::TextColored(Rgbf(Muted), "%s", relation == "incoming" ? "Asked to be your friend" : "Request sent");
                ImGui::PopFont();
            }
            ImGui::EndGroup();
            if (relation != "outgoing")
            {
                const char* label = relation == "incoming" ? "Accept" : "Add friend";
                AlignRight(ButtonWidth(label));
                if (AccentButton(label)) Act(relation == "incoming" ? "acceptFriend" : "addFriend", Field("id", p["id"].str()));
            }
            ImGui::PopID();
        }
        ImGui::Separator();
    }

    // Lobby invites, then friend requests: both want an answer.
    for (auto& invite : f["invites"].items)
    {
        auto& from = invite["from"];
        ImGui::PushID(("invite" + from["id"].str()).c_str());
        Heading("LOBBY INVITE");
        Round(from["id"].str(), from["name"].str(), 32, "");
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
            Round(p["id"].str(), p["name"].str(), 32, "");
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
    bool hosting = f["hosting"].flag();
    auto row = [&](const json::Value& p) {
        if (!matches(p["name"].str())) return;
        ImGui::PushID(p["id"].str().c_str());
        auto st = p["state"].str();
        bool offline = st == "offline";
        Round(p["id"].str(), p["name"].str(), 32, st);
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
        // Invite (to your lobby) or Invite to lobby (a new one, set up in the New lobby window); Join their open lobby;
        // the rest in a menu.
        bool canInvite = !offline && !p["inLobby"].flag();
        bool canJoin = lobby.kind == json::Value::Object && lobby["joinable"].flag();
        const char* inviteLabel = hosting ? "Invite" : "Invite to lobby";
        auto more = Icon(IconMore) + "##more";
        float width = ButtonWidth(more.c_str()) + (canInvite ? ButtonWidth(inviteLabel) + 6 : 0) + (canJoin ? ButtonWidth("Join") + 6 : 0);
        AlignRight(width);
        if (canJoin)
        {
            if (AccentButton("Join"))
                AskFirst("Join " + p["name"].str() + "'s lobby?", "Your game restarts to join when they start theirs.", "Join", "join",
                         Field("code", lobby["code"].str()));
            ImGui::SameLine(0, 6);
        }
        if (canInvite)
        {
            if (ImGui::Button(inviteLabel))
            {
                if (hosting) Act("invite", Field("id", p["id"].str()));
                else OpenStage(p["id"].str());
            }
            ImGui::SameLine(0, 6);
        }
        if (ImGui::Button(more.c_str())) ImGui::OpenPopup("##menu");
        if (ImGui::BeginPopup("##menu"))
        {
            if (ImGui::MenuItem(("Remove " + p["name"].str() + " from friends").c_str()))
                AskFirst("Remove " + p["name"].str() + " from your friends?",
                         "You'll both stop seeing each other in Friends. You can add each other again later.", "Remove", "removeFriend",
                         Field("id", p["id"].str()), true);
            ImGui::EndPopup();
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
        if (showOffline || search[0])
            for (auto& p : f["friends"].items)
                if (p["state"].str() == "offline") row(p);
    }
    if (!f["outgoing"].items.empty())
    {
        ImGui::Spacing();
        Heading("SENT");
        for (auto& p : f["outgoing"].items)
        {
            if (!matches(p["name"].str())) continue;
            ImGui::PushID(("out" + p["id"].str()).c_str());
            Round(p["id"].str(), p["name"].str(), 32, "");
            ImGui::SameLine();
            ImGui::BeginGroup();
            ImGui::TextUnformatted(p["name"].str().c_str());
            ImGui::PushFont(nullptr, 13);
            ImGui::TextColored(Rgbf(Muted), "Waiting for them");
            ImGui::PopFont();
            ImGui::EndGroup();
            AlignRight(ButtonWidth("Cancel"));
            if (ImGui::Button("Cancel")) Act("cancelRequest", Field("id", p["id"].str()));
            ImGui::PopID();
        }
    }
    ImGui::EndChild();
    ImGui::End();
}

// ---- Lobby: laid out like the app's lobby window (CoopWindow), with the same things in the same places ----

/// A card as the app draws them: a panel with a rounded border, accent-edged when it's you.
bool BeginCard(const char* id, ImVec2 size, bool highlight = false, unsigned background = 0x1F232B)
{
    ImGui::PushStyleColor(ImGuiCol_ChildBg, Rgbf(background));
    ImGui::PushStyleColor(ImGuiCol_Border, highlight ? Rgbf(Accent) : Rgbf(Line));
    ImGui::PushStyleVar(ImGuiStyleVar_ChildRounding, 8);
    ImGui::PushStyleVar(ImGuiStyleVar_WindowPadding, ImVec2(14, 10));
    bool open = ImGui::BeginChild(id, size, ImGuiChildFlags_Borders | ImGuiChildFlags_AlwaysUseWindowPadding,
                                  ImGuiWindowFlags_NoScrollbar | ImGuiWindowFlags_NoScrollWithMouse);
    ImGui::PopStyleVar(2);
    ImGui::PopStyleColor(2);
    return open;
}

/// A small outlined label after the name ("Host", "You"), as the app's badges.
void Badge(const char* text)
{
    ImGui::SameLine(0, 8);
    ImGui::PushFont(nullptr, 12);
    ImVec2 size = ImGui::CalcTextSize(text), a = ImGui::GetCursorScreenPos();
    a.y = ImGui::GetWindowPos().y + ImGui::GetWindowHeight() / 2 - size.y / 2 - 1; // the middle of the card's row
    ImVec2 b(a.x + size.x + 12, a.y + size.y + 2);
    auto* draw = ImGui::GetWindowDrawList();
    draw->AddRect(a, b, Rgb(Accent), 4);
    draw->AddText(ImVec2(a.x + 6, a.y + 1), Rgb(Accent), text);
    ImGui::Dummy(ImVec2(b.x - a.x, b.y - a.y));
    ImGui::PopFont();
}

/// Text centred in the current card.
void Centered(const char* text, unsigned colour)
{
    ImGui::SetCursorPosX((ImGui::GetWindowWidth() - ImGui::CalcTextSize(text).x) / 2);
    ImGui::TextColored(Rgbf(colour), "%s", text);
}

/// Invite members: friends online first, then everyone else online; opened from an open slot, as in the app.
void DrawInvitePopup(const json::Value& l)
{
    ImGui::SetNextWindowSize(ImVec2(340, 0));
    if (!ImGui::BeginPopup("##invite")) return;
    ImGui::PushFont(g_bold, 16);
    ImGui::TextUnformatted("Invite members");
    ImGui::PopFont();
    static char search[48] = "";
    ImGui::SetNextItemWidth(-1);
    ImGui::InputTextWithHint("##find", "Search", search, sizeof search);
    std::set<std::string> invited, inLobby;
    for (auto& i : l["invited"].items) invited.insert(i["member"].str());
    for (auto& s : l["slots"].items) inLobby.insert(s["member"].str());
    int filled = (int)l["slots"].items.size() + (int)invited.size();
    bool full = filled >= l["size"].num(5);
    auto& f = State().friends;
    ImGui::BeginChild("##people", ImVec2(0, 280), ImGuiChildFlags_None, ImGuiWindowFlags_NoBackground);
    bool anyone = false;
    for (auto [list, title] : { std::pair{ "friends", "FRIENDS" }, std::pair{ "others", "OTHERS ONLINE" } })
    {
        bool headed = false;
        for (auto& p : f[list].items)
        {
            auto name = p["name"].str(), id = p["id"].str(), st = p["state"].str();
            if (st == "offline") continue;
            std::string a = name, b = search;
            for (auto& c : a) c = (char)tolower((unsigned char)c);
            for (auto& c : b) c = (char)tolower((unsigned char)c);
            if (a.find(b) == std::string::npos) continue;
            if (!headed) Heading(title), headed = true;
            anyone = true;
            ImGui::PushID(("inv" + id).c_str());
            Round(id, name, 28, st);
            ImGui::SameLine();
            ImGui::BeginGroup();
            ImGui::TextUnformatted(name.c_str());
            ImGui::PushFont(nullptr, 12);
            ImGui::TextColored(Rgbf(StateColour(st)), "%s", p["label"].str().c_str());
            ImGui::PopFont();
            ImGui::EndGroup();
            const char* label = inLobby.count(id) ? "In lobby" : invited.count(id) ? "Invited" : "Invite";
            AlignRight(ButtonWidth(label));
            ImGui::BeginDisabled(label != std::string("Invite") || full);
            if (ImGui::Button(label)) Act("invite", Field("id", id));
            ImGui::EndDisabled();
            ImGui::PopID();
        }
    }
    if (!anyone) MutedText(search[0] ? "No matches." : "Nobody else is online.");
    ImGui::EndChild();
    ImGui::EndPopup();
}

/// The lobby chat, inside a card: what happens in the lobby and what players say, and a box to talk.
void ChatBox(const json::Value& l, ImVec2 size)
{
    if (!BeginCard("##chat", size, false, 0x1F232B))
    {
        ImGui::EndChild();
        return;
    }
    float input = ImGui::GetFrameHeight() + ImGui::GetStyle().ItemSpacing.y * 2 + 4;
    ImGui::BeginChild("##log", ImVec2(0, -input), ImGuiChildFlags_None, ImGuiWindowFlags_NoBackground);
    for (auto& line : l["log"].items)
    {
        ImGui::PushFont(nullptr, 12);
        ImGui::TextColored(Rgbf(Muted), "%s", line["time"].str().c_str());
        ImGui::PopFont();
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
    ImGui::Separator();
    static char text[300] = "";
    ImGui::SetNextItemWidth(-ButtonWidth("Send") - ImGui::GetStyle().ItemSpacing.x);
    bool enter = ImGui::InputTextWithHint("##say", "Say something…", text, sizeof text, ImGuiInputTextFlags_EnterReturnsTrue);
    ImGui::SameLine();
    if ((ImGui::Button("Send") || enter) && text[0])
    {
        Act("chat", Field("text", text));
        text[0] = 0;
    }
    if (enter) ImGui::SetKeyboardFocusHere(-1); // keep typing
    ImGui::EndChild();
}

void DrawLobby()
{
    auto& io = ImGui::GetIO();
    if (!BeginModule(Lobby, (Icon(IconGroup) + "  Lobby###lobby").c_str(), ImVec2(io.DisplaySize.x / 2 - 470, io.DisplaySize.y * 0.11f),
                     ImVec2(940, 680), ImGuiWindowFlags_NoScrollbar | ImGuiWindowFlags_NoScrollWithMouse))
        return;
    auto& l = State().lobby;
    if (l.kind != json::Value::Object)
    {
        ImGui::PushFont(g_bold, 20);
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
    float buttons = ImGui::GetFrameHeight() + 18;

    // Top: the code, and who can join (host) or whose lobby and whether the game's on (joined).
    const float top = 118;
    if (BeginCard("##code", ImVec2(210, top)))
    {
        ImGui::PushFont(nullptr, 12);
        Centered("Lobby code", Muted);
        ImGui::PopFont();
        ImGui::PushFont(g_bold, 30);
        Centered(l["code"].str().c_str(), Accent);
        ImGui::PopFont();
        static ULONGLONG copied = 0;
        const char* label = GetTickCount64() - copied < 2000 ? "Copied" : "Copy";
        ImGui::SetCursorPosX((ImGui::GetWindowWidth() - ButtonWidth(label)) / 2);
        if (ImGui::Button(label))
        {
            ImGui::SetClipboardText(l["code"].str().c_str());
            copied = GetTickCount64();
        }
    }
    ImGui::EndChild();
    ImGui::SameLine(0, 12);
    if (BeginCard("##who", ImVec2(0, top)))
    {
        if (host)
        {
            ImGui::PushFont(nullptr, 12);
            ImGui::TextColored(Rgbf(Muted), "Who can join");
            ImGui::PopFont();
            // Changes go to the manager as the app's lobby window would make them; it shows them too.
            static char password[64] = "";
            static std::string shownPassword;
            if (l["passwordText"].str() != shownPassword && !ImGui::IsAnyItemActive())
            {
                shownPassword = l["passwordText"].str();
                snprintf(password, sizeof password, "%s", shownPassword.c_str());
            }
            bool locked = l["locked"].flag();
            auto send = [&] { Act("settings", std::string("\"locked\":") + (locked ? "true" : "false") + "," + Field("password", password)); };
            ImGui::AlignTextToFramePadding();
            if (ImGui::Checkbox("Locked", &locked)) send();
            if (ImGui::IsItemHovered()) ImGui::SetTooltip("Only members you invite can join");
            ImGui::SameLine(0, 24);
            ImGui::TextColored(Rgbf(Muted), "Password");
            ImGui::SameLine();
            ImGui::SetNextItemWidth(-1);
            ImGui::InputTextWithHint("##password", "None", password, sizeof password, ImGuiInputTextFlags_Password);
            if (ImGui::IsItemDeactivatedAfterEdit()) send(); // on Enter or clicking away, as the app applies after typing
            ImGui::PushFont(nullptr, 12);
            ImGui::TextColored(Rgbf(Muted), "%s", l["access"].str().c_str());
            ImGui::PopFont();
        }
        else
        {
            ImGui::PushFont(nullptr, 12);
            ImGui::TextColored(Rgbf(Muted), "Host");
            ImGui::PopFont();
            ImGui::PushFont(g_bold, 20);
            ImGui::TextUnformatted(l["host"].str().c_str());
            ImGui::PopFont();
            MutedText(l["started"].flag() ? "Game started" : "Waiting for the host to start the game");
        }
    }
    ImGui::EndChild();
    ImGui::Spacing();

    // Players (left) and Lobby chat (right), as in the app.
    float chatWidth = 340, gap = 16;
    float left = ImGui::GetContentRegionAvail().x - chatWidth - gap;
    ImGui::BeginChild("##players", ImVec2(left, -buttons), ImGuiChildFlags_None, ImGuiWindowFlags_NoBackground);
    int size = l["size"].num(5);
    int filled = (int)l["slots"].items.size();
    ImGui::PushFont(g_bold, 16);
    ImGui::TextUnformatted("Players");
    ImGui::PopFont();
    {
        // Dots: filled for each player in, as the app shows; then the count.
        ImGui::SameLine(0, 10);
        auto* draw = ImGui::GetWindowDrawList();
        ImVec2 p = ImGui::GetCursorScreenPos();
        p.y += ImGui::GetTextLineHeight() / 2 + 2;
        for (int i = 0; i < size; i++)
        {
            ImVec2 c(p.x + 5 + i * 14, p.y);
            if (i < filled) draw->AddCircleFilled(c, 4.5f, Rgb(Accent));
            else draw->AddCircle(c, 4.5f, Rgb(Line), 0, 1.5f);
        }
        ImGui::Dummy(ImVec2(size * 14.f, 4));
        ImGui::SameLine(0, 6);
        ImGui::PushFont(nullptr, 12);
        ImGui::TextColored(Rgbf(Muted), "%d/%d", filled, size);
        ImGui::PopFont();
    }
    ImGui::Spacing();
    const float slotHeight = 64;
    int shown = 0;
    for (auto& s : l["slots"].items)
    {
        ImGui::PushID(s["id"].str().c_str());
        bool you = s["you"].flag();
        if (BeginCard("##slot", ImVec2(0, slotHeight), you, 0x282D37))
        {
            ImGui::SetCursorPosY((slotHeight - 40) / 2);
            Picture(s["member"].str(), s["name"].str(), 40);
            ImGui::SameLine(0, 14);
            ImGui::PushFont(g_bold, 17);
            ImGui::SetCursorPosY((slotHeight - ImGui::GetTextLineHeight()) / 2);
            ImGui::TextUnformatted(s["name"].str().c_str());
            ImGui::PopFont();
            if (s["host"].flag()) Badge("Host");
            if (you) Badge("You");
            // The state on the right, then the host's Remove, as in the app.
            std::string state = s["state"].str();
            if (s["ping"].kind == json::Value::Number) state += " · " + std::to_string(s["ping"].num()) + " ms";
            bool removable = host && !s["host"].flag();
            float right = ImGui::CalcTextSize(state.c_str()).x + (removable ? ButtonWidth("Remove") + 14 : 0);
            ImGui::SameLine(ImGui::GetWindowContentRegionMax().x - right);
            unsigned colour = s["failed"].flag() ? Danger : s["ok"].flag() ? Ready : Muted;
            ImGui::PushFont(g_bold, 0);
            ImGui::SetCursorPosY((slotHeight - ImGui::GetTextLineHeight()) / 2);
            ImGui::TextColored(Rgbf(colour), "%s", state.c_str());
            ImGui::PopFont();
            if (removable)
            {
                ImGui::SameLine(0, 14);
                ImGui::SetCursorPosY((slotHeight - ImGui::GetFrameHeight()) / 2);
                if (ImGui::Button("Remove"))
                    AskFirst("Remove " + s["name"].str() + "?", s["name"].str() + " can't rejoin this lobby. Their game restarts in solo.", "Remove", "kick",
                             Field("id", s["id"].str()), true);
            }
        }
        ImGui::EndChild();
        ImGui::PopID();
        shown++;
    }
    for (auto& i : l["invited"].items)
    {
        if (shown >= size) break;
        ImGui::PushID(("inv" + i["member"].str()).c_str());
        if (BeginCard("##invited", ImVec2(0, slotHeight), false, 0x1F232B))
        {
            ImGui::SetCursorPosY((slotHeight - 40) / 2);
            Picture(i["member"].str(), i["name"].str(), 40, 0.5f);
            ImGui::SameLine(0, 14);
            ImGui::PushFont(g_bold, 17);
            ImGui::SetCursorPosY((slotHeight - ImGui::GetTextLineHeight()) / 2);
            ImGui::TextColored(Rgbf(Muted), "%s", i["name"].str().c_str());
            ImGui::PopFont();
            ImGui::SameLine(ImGui::GetWindowContentRegionMax().x - ImGui::CalcTextSize("Invited").x);
            ImGui::SetCursorPosY((slotHeight - ImGui::GetTextLineHeight()) / 2);
            ImGui::TextColored(Rgbf(Muted), "Invited");
        }
        ImGui::EndChild();
        ImGui::PopID();
        shown++;
    }
    for (; shown < size; shown++)
    {
        ImGui::PushID(shown);
        if (BeginCard("##open", ImVec2(0, slotHeight), false, 0x16181D))
        {
            ImGui::SetCursorPosY((slotHeight - 40) / 2);
            ImVec2 a = ImGui::GetCursorScreenPos();
            ImGui::GetWindowDrawList()->AddRect(a, ImVec2(a.x + 40, a.y + 40), Rgb(Line), 6);
            ImGui::Dummy(ImVec2(40, 40));
            ImGui::SameLine(0, 14);
            ImGui::SetCursorPosY((slotHeight - ImGui::GetTextLineHeight()) / 2);
            ImGui::TextColored(Rgbf(Muted), "Open slot");
            if (host)
            {
                ImGui::SameLine(ImGui::GetWindowContentRegionMax().x - ButtonWidth("Invite"));
                ImGui::SetCursorPosY((slotHeight - ImGui::GetFrameHeight()) / 2);
                if (ImGui::Button("Invite")) ImGui::OpenPopup("##invite");
                DrawInvitePopup(l);
            }
        }
        ImGui::EndChild();
        ImGui::PopID();
    }
    ImGui::EndChild();

    ImGui::SameLine(0, gap);
    ImGui::BeginGroup();
    ImGui::PushFont(g_bold, 16);
    ImGui::TextUnformatted("Lobby chat");
    ImGui::PopFont();
    ImGui::Spacing();
    ChatBox(l, ImVec2(chatWidth, -buttons));
    ImGui::EndGroup();

    // Bottom right, in the app's order: the main action last.
    ImGui::SetCursorPosY(ImGui::GetWindowHeight() - buttons + 4);
    if (host)
    {
        bool started = l["started"].flag();
        const char* start = started ? "Game started" : "Start game";
        float w = ButtonWidth("New lobby") + ButtonWidth("Close lobby") + ButtonWidth(start) + 24 + ImGui::GetStyle().FramePadding.x * 4;
        ImGui::SetCursorPosX(ImGui::GetWindowContentRegionMax().x - w);
        if (ImGui::Button("New lobby")) OpenStage("");
        if (ImGui::IsItemHovered()) ImGui::SetTooltip("Set up a new lobby, bring players from this one, then switch to it");
        ImGui::SameLine(0, 12);
        if (ImGui::Button("Close lobby"))
            AskFirst("Close the lobby?", "Everyone in it is disconnected and your game restarts in solo.", "Close lobby", "close", "", true);
        ImGui::SameLine(0, 12);
        ImGui::BeginDisabled(started);
        ImGui::PushStyleVar(ImGuiStyleVar_FramePadding, ImVec2(ImGui::GetStyle().FramePadding.x * 2, ImGui::GetStyle().FramePadding.y));
        if (AccentButton(start))
            AskFirst("Start the game?", "Your game restarts to host co-op, and everyone's starts with it.", "Start game", "start");
        ImGui::PopStyleVar();
        ImGui::EndDisabled();
    }
    else
    {
        ImGui::SetCursorPosX(ImGui::GetWindowContentRegionMax().x - ButtonWidth("Leave lobby"));
        if (ImGui::Button("Leave lobby")) AskFirst("Leave the lobby?", "Your game restarts in solo.", "Leave", "leave", "", true);
    }
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

/// The New lobby window: the in-game version of the manager's (CoopWindow, Screen.Stage). Who comes along from this
/// lobby (when hosting), who can join, and friends to invite; Create lobby sends it all to the manager.
void DrawStage()
{
    auto& io = ImGui::GetIO();
    if (!BeginModule(Stage, (Icon(IconGroup) + "  New lobby###stage").c_str(), ImVec2(io.DisplaySize.x / 2 - 220, io.DisplaySize.y * 0.45f - 280),
                     ImVec2(440, 560)))
        return;
    auto& state = State();
    auto& l = state.lobby;
    auto& f = state.friends;
    bool hosting = l.kind == json::Value::Object && l["role"].str() == "host";
    bool joined = l.kind == json::Value::Object && !hosting;
    int count = 1 + (int)g_stage.bring.size() + (int)g_stage.invite.size();
    const int size = 5;

    ImGui::BeginChild("##choices", ImVec2(0, -ImGui::GetFrameHeightWithSpacing() - 8), ImGuiChildFlags_None, ImGuiWindowFlags_NoBackground);
    if (joined) MutedText(("You leave " + l["host"].str() + "'s lobby when you create this one, and your game restarts in solo.").c_str());
    if (hosting)
    {
        Heading("BRING FROM THIS LOBBY");
        bool anyone = false;
        for (auto& s : l["slots"].items)
        {
            if (s["host"].flag()) continue;
            anyone = true;
            auto id = s["id"].str();
            bool on = g_stage.bring.count(id) > 0;
            ImGui::BeginDisabled(!on && count >= size);
            if (ImGui::Checkbox((s["name"].str() + "##bring" + id).c_str(), &on))
            {
                if (on) g_stage.bring.insert(id);
                else g_stage.bring.erase(id);
            }
            ImGui::EndDisabled();
        }
        if (!anyone) MutedText("Nobody else is in this lobby.");
        ImGui::Spacing();
    }

    Heading("WHO CAN JOIN");
    ImGui::Checkbox("Locked", &g_stage.locked);
    if (ImGui::IsItemHovered()) ImGui::SetTooltip("Only members you invite can join");
    ImGui::SameLine(0, 24);
    ImGui::AlignTextToFramePadding();
    ImGui::TextColored(Rgbf(Muted), "Password");
    ImGui::SameLine();
    ImGui::SetNextItemWidth(-1);
    ImGui::InputTextWithHint("##password", "None", g_stage.password, sizeof g_stage.password, ImGuiInputTextFlags_Password);
    ImGui::Spacing();

    char heading[48];
    snprintf(heading, sizeof heading, "INVITE  ·  %d/%d WITH YOU", count, size);
    Heading(heading);
    bool anyFriend = false;
    for (auto& p : f["friends"].items)
    {
        if (p["state"].str() == "offline" || p["inLobby"].flag()) continue;
        anyFriend = true;
        auto id = p["id"].str();
        ImGui::PushID(("stage" + id).c_str());
        bool on = g_stage.invite.count(id) > 0;
        ImGui::BeginDisabled(!on && count >= size);
        ImGui::AlignTextToFramePadding();
        if (ImGui::Checkbox("##on", &on))
        {
            if (on) g_stage.invite.insert(id);
            else g_stage.invite.erase(id);
        }
        ImGui::SameLine();
        Round(id, p["name"].str(), 26, p["state"].str());
        ImGui::SameLine();
        ImGui::AlignTextToFramePadding();
        ImGui::TextUnformatted(p["name"].str().c_str());
        ImGui::EndDisabled();
        ImGui::PopID();
    }
    if (!anyFriend) MutedText("None of your friends are online.");
    ImGui::EndChild();

    ImGui::Separator();
    float w = ButtonWidth("Cancel") + ButtonWidth("Create lobby") + ImGui::GetStyle().ItemSpacing.x;
    ImGui::SetCursorPosX(ImGui::GetWindowContentRegionMax().x - w);
    if (ImGui::Button("Cancel")) g_shown[Stage] = false, g_windowsDirty = true;
    ImGui::SameLine();
    if (AccentButton("Create lobby"))
    {
        auto list = [](const std::set<std::string>& ids) {
            std::string out = "[";
            for (auto& id : ids) out += (out.size() > 1 ? "," : "") + json::Quote(id);
            return out + "]";
        };
        auto fields = std::string("\"locked\":") + (g_stage.locked ? "true" : "false") + "," + Field("password", g_stage.password) +
                      ",\"bring\":" + list(g_stage.bring) + ",\"invite\":" + list(g_stage.invite);
        // Anything that restarts the game or leaves people behind is asked first, as in the manager.
        std::string what;
        if (joined) what = "You leave " + l["host"].str() + "'s lobby, and your game restarts in solo. ";
        if (hosting)
        {
            what += "Your game closes, and starts again when you start the new lobby's game. ";
            int left = 0;
            for (auto& s : l["slots"].items) left += !s["host"].flag() && !g_stage.bring.count(s["id"].str());
            if (!g_stage.bring.empty()) what += std::to_string(g_stage.bring.size()) + (g_stage.bring.size() == 1 ? " player moves" : " players move") + " with you. ";
            if (left) what += "Everyone else leaves the party and their game restarts in solo.";
        }
        if (what.empty()) Act("createLobby", fields);
        else AskFirst("Create the new lobby?", what, "Create lobby", "createLobby", fields);
        g_shown[Stage] = false;
        g_windowsDirty = true;
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
            if (!on) ImGui::SetWindowFocus((std::string("###") + ModuleNames[item.module]).c_str());
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
        ImGui::GetBackgroundDrawList()->AddRectFilled(ImVec2(0, 0), io.DisplaySize, IM_COL32(0, 0, 0, 205));
        DrawTopBar();
        auto& state = State();
        if (g_shown[Friends]) DrawFriends();
        if (state.lobbies && g_shown[Lobby]) DrawLobby();
        if (g_shown[Overview]) DrawOverview();
        if (state.lobbies && g_shown[Stage]) DrawStage();
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
