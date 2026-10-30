// Co-op rendezvous for DrangedAddons' Direct co-op (Azeroth Survivors).
//
// The host's manager and each joiner's manager open a WebSocket to /lobby/<CODE>. This Worker only passes small
// "signal" messages (each side's public UDP address and certificate thumbprint) between them; the game traffic goes
// straight between the players' PCs over a hole-punched UDP (QUIC) link and never passes through here.
//
// Every socket stays open for the lobby's life, so the host can keep everyone's member list current, start everyone's
// game together, and remove a player. The lobby's lock, password and removals are enforced here, not just in the
// host's app, so a modified client can't get past them. Messages, as JSON:
//   to a joiner:   {t:"joined", id}  {t:"signal", from:"host", data}  {t:"party", data}  {t:"start"}
//                  {t:"kicked"}  {t:"closed", why}
//   to the host:   {t:"hosting"}  {t:"join", id, name, member}  {t:"signal", from:<id>, data}  {t:"leave", id}
//   from a joiner: {t:"signal", data}           (always to the host)
//   from the host: {t:"signal", to, data}  {t:"party", data}  {t:"start"}  {t:"kick", to}
//   from anyone:   {t:"chat", text}  ->  to everyone, sender included: {t:"chat", from, host, text}. The sender's
//                  name is the one it joined (or hosted) under, not anything in the message; at most 300 characters,
//                  5 messages per 5 seconds per player.
//                  {t:"settings", locked, pw}   (pw: the password's hash, or null for none)
//                  {t:"allow", member}          (an invited member gets in even when locked or passworded)
//   errors:        {t:"error", why} then the socket closes. why: taken, no lobby, full, locked, password,
//                  wrong password, removed, slow down, update
// Joining needs the joiner's install key (so the host sees which member it really is, and a removed member stays
// out) and, for a passworded lobby, pw = SHA-256("drangedaddons-lobby:" + code + ":" + password) in hex: the
// password itself never leaves the players' PCs.
// A signal's data.local (an address on the sender's home network) is only passed on when both sides connect from
// the same internet address: nobody else can use it, so it isn't theirs to see.
//
// /presence?key=<install secret>&name=<account name>&state=<state> keeps one socket per running manager, so members
// can see who's online and who's hosting (one global Durable Object; fine for a friends-sized group). Others only
// ever see a hash of the key as the member's id, so nobody can copy an id from the list and connect as that member.
// Older managers sent their key as ?id=, which still works, and no state (online).
//   state:        online, away, ingame, or offline ("appear offline": still connected and sees the others, but
//                 nobody sees it, and invites to it are dropped). &game=<game id> says which game while ingame.
// Presence, accounts and friends are for every game the manager supports; a listed lobby carries the game it's for.
//   to a member:  {t:"online", players:[{id,name,lobby,state,game,avatar}]}   {t:"here", player:{…}}   {t:"gone", id}
//                 {t:"invite", from:{id,name}, code}  {t:"declined", from:{id,name}}
//   from anyone:  {t:"invite", to, code}  {t:"decline", to}
//                 {t:"status", lobby, state, game}   lobby: null, or {code, locked, password, count, game} while
//                                              hosting; the code is only listed while the lobby is unlocked.
//                                              state and game: optional.
// Friends (kept in storage, so they last across restarts and work while either side is offline):
//   from anyone:  {t:"search", q}      -> {t:"found", q, players:[{id,name,avatar,relation}]}: known members whose name
//                                         contains q (2+ characters), at most 20. relation: friend, incoming (they
//                                         asked you), outgoing (you asked them) or null. Appearing offline doesn't
//                                         hide a member from search, only their status.
//                 {t:"friend", to}     asks to be friends; if they already asked you, you're friends now
//                 {t:"unfriend", to}   removes a friend, or declines / cancels a request
//   to a member:  {t:"friends", friends:[{id,name,avatar}], incoming:[…], outgoing:[…]} on connect and whenever
//                 that changes. Which friends are online comes from the online / here / gone messages.
// GET /stats with "Authorization: Bearer <STATS_TOKEN>" (a Worker secret) returns totals for the platform dashboard:
// how many are connected, by status and game, open lobbies per game, members, friendships. Counts only: no names,
// ids or lobby codes.
// avatar is a short hash of the member's picture, or null. PUT /avatar?key=<install secret> (a PNG or JPEG of at most
// 64 KB) sets it and DELETE /avatar?key=… removes it; GET /avatar/<id> returns it.

const CODE = /^[A-HJ-NP-Z2-9]{6}$/;
const KEY = /^[A-Za-z0-9-]{16,64}$/;
const HASH = /^[0-9a-f]{64}$/;
const MAX_JOINERS = 4; // the game's party is 5
const MAX_MESSAGE = 4096;
const WRONG_PASSWORDS_PER_MINUTE = 5;
const CHAT_LENGTH = 300;
const CHATS_PER_5S = 5;
const INVITE_GAP_MS = 5000; // the same member, at most once per 5 seconds
const INVITES_PER_MINUTE = 20;
const AVATAR_BYTES = 64 * 1024;
const AVATAR_GAP_MS = 5000; // one picture change per 5 seconds per member
const SEARCH_LENGTH = 24;
const SEARCHES_PER_10S = 20;
const FRIENDS_MAX = 200;
const REQUESTS_MAX = 50; // pending each way

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (url.pathname === "/presence") {
      if (request.headers.get("Upgrade") !== "websocket") return new Response("expected a WebSocket", { status: 426 });
      return env.PRESENCE.get(env.PRESENCE.idFromName("global")).fetch(request);
    }
    if (url.pathname === "/avatar" || /^\/avatar\/[0-9a-f]{16}$/.test(url.pathname)) {
      return env.PRESENCE.get(env.PRESENCE.idFromName("global")).fetch(request);
    }
    if (url.pathname === "/stats") {
      if (!(await sameSecret(request.headers.get("Authorization") ?? "", "Bearer " + (env.STATS_TOKEN ?? "")))
          || !env.STATS_TOKEN) {
        return new Response("not allowed", { status: 401 });
      }
      return env.PRESENCE.get(env.PRESENCE.idFromName("global")).fetch(request);
    }
    const match = url.pathname.match(/^\/lobby\/([A-Za-z0-9]+)$/);
    if (!match) return new Response("DrangedAddons co-op rendezvous", { status: url.pathname === "/" ? 200 : 404 });
    if (request.headers.get("Upgrade") !== "websocket") return new Response("expected a WebSocket", { status: 426 });
    const code = match[1].toUpperCase();
    if (!CODE.test(code)) return new Response("bad lobby code", { status: 400 });
    return env.LOBBY.get(env.LOBBY.idFromName(code)).fetch(request);
  },
};

/** Compares two secrets without the time taken saying how much of one matched. */
async function sameSecret(a, b) {
  const hash = async (s) => new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(s)));
  const [x, y] = await Promise.all([hash(a), hash(b)]);
  let diff = 0;
  for (let i = 0; i < x.length; i++) diff |= x[i] ^ y[i];
  return diff === 0;
}

/** The id others see: a hash of the secret key, so seeing it doesn't let anyone connect as that member. */
async function publicId(key) {
  const hash = await crypto.subtle.digest("SHA-256", new TextEncoder().encode("drangedaddons-coop:" + key));
  return [...new Uint8Array(hash).slice(0, 8)].map((b) => b.toString(16).padStart(2, "0")).join("");
}

/** Hero names: no control, zero-width or text-direction characters (they could disguise one name as another). */
function cleanName(name) {
  return (name ?? "").replace(/[\u0000-\u001f\u007f-\u009f\u200b-\u200f\u202a-\u202e\u2066-\u2069]/g, "").trim().slice(0, 24);
}

function parse(raw) {
  if (typeof raw !== "string" || raw.length > MAX_MESSAGE) return null;
  try {
    return JSON.parse(raw);
  } catch {
    return null;
  }
}

/** Same household: the same IPv4 address, or the same IPv6 /64 (each device gets its own IPv6 address). */
function sameNetwork(a, b) {
  if (!a || !b) return false;
  if (a.includes(":") && b.includes(":")) return a.split(":").slice(0, 4).join(":") === b.split(":").slice(0, 4).join(":");
  return a === b;
}

function withoutLocal(data, from, to) {
  if (!data || typeof data !== "object" || sameNetwork(from, to)) return data;
  const { local, ...rest } = data;
  return rest;
}

export class Lobby {
  constructor(state) {
    this.state = state;
    this.wrong = new Map(); // ip -> times of wrong passwords this minute; lost on hibernation, fine
    this.chats = new Map(); // player id -> times of recent chat messages; same
  }

  sockets(role) {
    return this.state.getWebSockets().filter((ws) => ws.deserializeAttachment()?.role === role);
  }

  async fetch(request) {
    const params = new URL(request.url).searchParams;
    const role = params.get("role");
    const ip = request.headers.get("CF-Connecting-IP") ?? "";
    const [client, server] = Object.values(new WebSocketPair());
    const host = this.sockets("host")[0];

    const refuse = (why) => {
      server.accept();
      server.send(JSON.stringify({ t: "error", why }));
      server.close(1008, why);
      return new Response(null, { status: 101, webSocket: client });
    };

    if (role === "host") {
      if (host) return refuse("taken");
      this.state.acceptWebSocket(server);
      server.serializeAttachment({ role: "host", id: "host", name: cleanName(params.get("name")) || "Host", ip, locked: false, pw: null, allow: [], banned: [] });
      server.send(JSON.stringify({ t: "hosting" }));
    } else if (role === "join") {
      if (!host) return refuse("no lobby");
      const key = params.get("key") ?? "";
      if (!KEY.test(key)) return refuse("update");
      const member = await publicId(key);
      const settings = host.deserializeAttachment();
      if (settings.banned.includes(member)) return refuse("removed");
      if (this.sockets("join").length >= MAX_JOINERS) return refuse("full");
      if (!settings.allow.includes(member)) {
        if (settings.locked) return refuse("locked");
        if (settings.pw) {
          const now = Date.now();
          const tries = (this.wrong.get(ip) ?? []).filter((t) => now - t < 60000);
          if (tries.length >= WRONG_PASSWORDS_PER_MINUTE) return refuse("slow down");
          const pw = params.get("pw") ?? "";
          if (!pw) return refuse("password");
          if (pw !== settings.pw) {
            this.wrong.set(ip, [...tries, now]);
            return refuse("wrong password");
          }
        }
      }
      const id = "p" + crypto.randomUUID().slice(0, 8);
      this.state.acceptWebSocket(server);
      const name = cleanName(params.get("name")) || "A player";
      server.serializeAttachment({ role: "join", id, ip, member, name });
      server.send(JSON.stringify({ t: "joined", id }));
      host.send(JSON.stringify({ t: "join", id, member, name }));
    } else {
      return new Response("role must be host or join", { status: 400 });
    }
    return new Response(null, { status: 101, webSocket: client });
  }

  async webSocketMessage(ws, raw) {
    const message = parse(raw);
    const me = ws.deserializeAttachment();
    if (!message || !me) return;
    if (message.t === "chat" && (me.role === "host" || me.role === "join")) return this.chat(me, message.text);
    if (me.role === "host") this.fromHost(ws, me, message);
    else if (me.role === "join" && message.t === "signal") {
      const host = this.sockets("host")[0];
      const data = withoutLocal(message.data, me.ip, host?.deserializeAttachment()?.ip);
      host?.send(JSON.stringify({ t: "signal", from: me.id, data }));
    }
  }

  chat(me, text) {
    if (typeof text !== "string") return;
    text = text.replace(/[\u0000-\u0009\u000b-\u001f\u007f-\u009f\u202a-\u202e\u2066-\u2069]/g, "").trim().slice(0, CHAT_LENGTH);
    if (!text) return;
    const now = Date.now();
    const recent = (this.chats.get(me.id) ?? []).filter((t) => now - t < 5000);
    if (recent.length >= CHATS_PER_5S) return;
    this.chats.set(me.id, [...recent, now]);
    const out = JSON.stringify({ t: "chat", from: me.name ?? "A player", host: me.role === "host", text });
    for (const ws of this.state.getWebSockets()) {
      const role = ws.deserializeAttachment()?.role;
      if (role === "host" || role === "join") ws.send(out);
    }
  }

  fromHost(ws, me, message) {
    const joiners = this.sockets("join");
    switch (message.t) {
      case "signal": {
        const peer = joiners.find((s) => s.deserializeAttachment()?.id === message.to);
        const data = withoutLocal(message.data, me.ip, peer?.deserializeAttachment()?.ip);
        peer?.send(JSON.stringify({ t: "signal", from: "host", data }));
        break;
      }
      case "party":
      case "start": {
        const text = JSON.stringify(message.t === "party" ? { t: "party", data: message.data } : { t: "start" });
        for (const joiner of joiners) joiner.send(text);
        break;
      }
      case "settings":
        ws.serializeAttachment({
          ...me,
          locked: message.locked === true,
          pw: typeof message.pw === "string" && HASH.test(message.pw) ? message.pw : null,
        });
        break;
      case "allow":
        if (typeof message.member === "string" && /^[0-9a-f]{16}$/.test(message.member) && !me.allow.includes(message.member))
          ws.serializeAttachment({ ...me, allow: [...me.allow, message.member].slice(-20) });
        break;
      case "kick": {
        const target = joiners.find((s) => s.deserializeAttachment()?.id === message.to);
        if (!target) return;
        const removed = target.deserializeAttachment();
        // Removed for the lobby's life: they can't rejoin with the code or an old invite.
        ws.serializeAttachment({ ...me, banned: [...me.banned, removed.member].slice(-20), allow: me.allow.filter((m) => m !== removed.member) });
        target.send(JSON.stringify({ t: "kicked" }));
        target.serializeAttachment({ ...removed, role: "kicked" });
        target.close(1000, "kicked");
        break;
      }
    }
  }

  async webSocketClose(ws) {
    this.gone(ws);
  }

  async webSocketError(ws) {
    this.gone(ws);
  }

  gone(ws) {
    const me = ws.deserializeAttachment();
    for (const other of this.state.getWebSockets()) {
      if (other === ws) continue;
      if (me?.role === "host") {
        other.send(JSON.stringify({ t: "closed", why: "the host closed the lobby" }));
        other.close(1000, "host left");
      } else if (me?.role === "join" && other.deserializeAttachment()?.role === "host") {
        other.send(JSON.stringify({ t: "leave", id: me.id }));
      }
    }
  }
}

/** A hosting member's lobby as listed to others: only open (unlocked) lobbies list their code. */
const STATES = ["online", "away", "ingame", "offline"];

/** A member's chosen or automatic state; anything unknown (older managers send none) is online. */
function cleanState(state) {
  return STATES.includes(state) ? state : "online";
}

/** A game id ("wow", "azeroth-survivors"), or null. */
function cleanGame(game) {
  return typeof game === "string" && /^[a-z0-9-]{1,32}$/.test(game) ? game : null;
}

function visible(player) {
  return player.state !== "offline";
}

/** "image/png" or "image/jpeg" by the file's first bytes, else null. */
function imageType(bytes) {
  if (bytes[0] === 0x89 && bytes[1] === 0x50 && bytes[2] === 0x4e && bytes[3] === 0x47) return "image/png";
  if (bytes[0] === 0xff && bytes[1] === 0xd8 && bytes[2] === 0xff) return "image/jpeg";
  return null;
}

function cleanLobby(lobby) {
  if (!lobby || typeof lobby !== "object" || typeof lobby.code !== "string" || !CODE.test(lobby.code)) return null;
  const locked = lobby.locked === true;
  return {
    code: locked ? null : lobby.code,
    locked,
    password: lobby.password === true,
    count: Math.max(1, Math.min(5, Number.isInteger(lobby.count) ? lobby.count : 1)),
    game: cleanGame(lobby.game),
  };
}

export class Presence {
  constructor(state) {
    this.state = state;
    this.sent = new Map(); // sender id -> recent invite times and last invite per target; lost on hibernation, fine
    this.avatarAt = new Map(); // member id -> last picture change, same
    this.searches = new Map(); // member id -> recent search times, same
  }

  /** Everyone others may see: connected and not appearing offline. */
  players() {
    return this.state.getWebSockets().map((ws) => ws.deserializeAttachment()).filter((p) => p && visible(p));
  }

  async fetch(request) {
    const url = new URL(request.url);
    if (url.pathname.startsWith("/avatar")) return this.avatar(request, url);
    if (url.pathname === "/stats") return this.stats(); // the token was checked before it got here
    const params = url.searchParams;
    const key = params.get("key") ?? params.get("id") ?? "";
    const name = cleanName(params.get("name"));
    if (!KEY.test(key) || !name) return new Response("key and name required", { status: 400 });
    const id = await publicId(key);

    // The same manager reconnecting replaces its old socket (without a "gone": it's still here).
    let wasVisible = false;
    for (const old of this.state.getWebSockets()) {
      const who = old.deserializeAttachment();
      if (who?.id === id) {
        wasVisible ||= visible(who);
        old.serializeAttachment(null);
        old.close(1000, "replaced");
      }
    }
    const [client, server] = Object.values(new WebSocketPair());
    this.state.acceptWebSocket(server);
    const stored = await this.state.storage.get("avatar:" + id);
    const state = cleanState(params.get("state"));
    const me = { id, name, lobby: null, state, game: state === "ingame" ? cleanGame(params.get("game")) : null, avatar: stored?.hash ?? null };
    server.serializeAttachment(me);
    server.send(JSON.stringify({ t: "online", players: this.players().filter((p) => p.id !== id) }));
    if (visible(me)) this.broadcast({ t: "here", player: me }, id);
    else if (wasVisible) this.broadcast({ t: "gone", id }, id);
    // Known members, for search and for friends' names while they're offline.
    await this.state.storage.put("member:" + id, { name, seen: Date.now() });
    server.send(JSON.stringify(await this.friendsMessage(id)));
    return new Response(null, { status: 101, webSocket: client });
  }

  /** PUT/DELETE /avatar?key=… (the member's own picture), GET /avatar/<id> (anyone's). */
  async avatar(request, url) {
    if (request.method === "GET") {
      const id = url.pathname.slice("/avatar/".length);
      const stored = await this.state.storage.get("avatar:" + id);
      if (!stored) return new Response("no picture", { status: 404 });
      return new Response(stored.data, {
        headers: { "Content-Type": stored.type, "Cache-Control": "public, max-age=300", ETag: `"${stored.hash}"` },
      });
    }
    const key = url.searchParams.get("key") ?? "";
    if (url.pathname !== "/avatar" || !KEY.test(key)) return new Response("key required", { status: 400 });
    const id = await publicId(key);
    const now = Date.now();
    if (now - (this.avatarAt.get(id) ?? 0) < AVATAR_GAP_MS) return new Response("slow down", { status: 429 });
    this.avatarAt.set(id, now);

    let hash = null;
    if (request.method === "PUT") {
      const data = await request.arrayBuffer();
      const type = imageType(new Uint8Array(data));
      if (!type || data.byteLength > AVATAR_BYTES) return new Response("a PNG or JPEG of at most 64 KB", { status: 400 });
      const digest = await crypto.subtle.digest("SHA-256", data);
      hash = [...new Uint8Array(digest).slice(0, 6)].map((b) => b.toString(16).padStart(2, "0")).join("");
      await this.state.storage.put("avatar:" + id, { hash, type, data });
    } else if (request.method === "DELETE") {
      await this.state.storage.delete("avatar:" + id);
    } else {
      return new Response("PUT, DELETE or GET", { status: 405 });
    }

    // Show the new picture straight away to everyone who can see this member.
    for (const ws of this.state.getWebSockets()) {
      const who = ws.deserializeAttachment();
      if (who?.id !== id) continue;
      const updated = { ...who, avatar: hash };
      ws.serializeAttachment(updated);
      if (visible(updated)) this.broadcast({ t: "here", player: updated }, id);
    }
    return new Response(JSON.stringify({ avatar: hash }), { headers: { "Content-Type": "application/json" } });
  }

  broadcast(message, except) {
    const text = JSON.stringify(message);
    for (const ws of this.state.getWebSockets()) {
      const who = ws.deserializeAttachment();
      if (who && who.id !== except) ws.send(text);
    }
  }

  async webSocketMessage(ws, raw) {
    const message = parse(raw);
    const me = ws.deserializeAttachment();
    if (!message || !me) return;
    if (message.t === "status") {
      const state = message.state === undefined ? me.state : cleanState(message.state);
      const updated = {
        ...me,
        lobby: cleanLobby(message.lobby),
        state,
        game: state !== "ingame" ? null : message.game === undefined ? me.game : cleanGame(message.game),
      };
      ws.serializeAttachment(updated);
      if (visible(updated)) this.broadcast({ t: "here", player: updated }, me.id);
      else if (visible(me)) this.broadcast({ t: "gone", id: me.id }, me.id); // just chose to appear offline
      return;
    }
    if (message.t === "search") return this.search(ws, me, message.q);
    if (message.t === "friend" || message.t === "unfriend") {
      if (typeof message.to !== "string" || !/^[0-9a-f]{16}$/.test(message.to) || message.to === me.id) return;
      return message.t === "friend" ? this.befriend(me.id, message.to) : this.unfriend(me.id, message.to);
    }
    if (typeof message.to !== "string") return;
    // Someone appearing offline can't be reached: an invite to them would show they're there.
    const target = this.state.getWebSockets().find((s) => {
      const who = s.deserializeAttachment();
      return who?.id === message.to && visible(who);
    });
    if (!target) return;
    const from = { id: me.id, name: me.name };
    if (message.t === "invite" && typeof message.code === "string" && CODE.test(message.code)) {
      if (!this.allowInvite(me.id, message.to)) return;
      target.send(JSON.stringify({ t: "invite", from, code: message.code }));
    } else if (message.t === "decline") {
      target.send(JSON.stringify({ t: "declined", from }));
    }
  }

  async webSocketClose(ws) {
    this.gone(ws);
  }

  async webSocketError(ws) {
    this.gone(ws);
  }

  /** Totals for the platform dashboard (see /stats at the top). */
  async stats() {
    const connected = this.state.getWebSockets().map((ws) => ws.deserializeAttachment()).filter(Boolean);
    const byState = { online: 0, away: 0, ingame: 0, offline: 0 };
    const inGame = {};
    const lobbies = {};
    for (const p of connected) {
      byState[p.state ?? "online"] = (byState[p.state ?? "online"] ?? 0) + 1;
      if (p.state === "ingame" && p.game) inGame[p.game] = (inGame[p.game] ?? 0) + 1;
      if (p.lobby) {
        // Lobbies listed before games were tagged came from Azeroth Survivors, the only game with lobbies then.
        const game = p.lobby.game ?? "azeroth-survivors";
        const l = (lobbies[game] ??= { lobbies: 0, players: 0, locked: 0, password: 0 });
        l.lobbies++;
        l.players += p.lobby.count;
        if (p.lobby.locked) l.locked++;
        if (p.lobby.password) l.password++;
      }
    }
    const now = Date.now();
    let known = 0, seenDay = 0, seenWeek = 0;
    for (const [, m] of await this.state.storage.list({ prefix: "member:" })) {
      known++;
      if (now - m.seen < 86400000) seenDay++;
      if (now - m.seen < 7 * 86400000) seenWeek++;
    }
    let friendLinks = 0, pending = 0;
    for (const [, l] of await this.state.storage.list({ prefix: "links:" })) {
      friendLinks += l.friends.length;
      pending += l.incoming.length;
    }
    const pictures = (await this.state.storage.list({ prefix: "avatar:" })).size;
    const body = {
      at: new Date(now).toISOString(),
      connected: connected.length,
      visible: connected.filter(visible).length,
      byState,
      inGame,
      lobbies,
      members: { known, seenDay, seenWeek, pictures },
      friendships: friendLinks / 2, // each friendship is stored on both members
      pendingRequests: pending,
    };
    return new Response(JSON.stringify(body), { headers: { "Content-Type": "application/json", "Cache-Control": "no-store" } });
  }

  // ---- Friends ----

  async links(id) {
    return (await this.state.storage.get("links:" + id)) ?? { friends: [], incoming: [], outgoing: [] };
  }

  /** Names and pictures for member ids, from the directory (members never seen have no entry and are left out). */
  async profiles(ids) {
    const out = [];
    for (let i = 0; i < ids.length; i += 64) {
      const chunk = ids.slice(i, i + 64);
      const stored = await this.state.storage.get(chunk.flatMap((id) => ["member:" + id, "avatar:" + id]));
      for (const id of chunk) {
        const member = stored.get("member:" + id);
        if (member) out.push({ id, name: member.name, avatar: stored.get("avatar:" + id)?.hash ?? null });
      }
    }
    return out;
  }

  async friendsMessage(id) {
    const links = await this.links(id);
    return {
      t: "friends",
      friends: await this.profiles(links.friends),
      incoming: await this.profiles(links.incoming),
      outgoing: await this.profiles(links.outgoing),
    };
  }

  /** Sends both members their friends lists again (whichever of them is connected). */
  async tellFriends(...ids) {
    for (const id of ids) {
      const sockets = this.state.getWebSockets().filter((s) => s.deserializeAttachment()?.id === id);
      if (sockets.length === 0) continue;
      const text = JSON.stringify(await this.friendsMessage(id));
      for (const s of sockets) s.send(text);
    }
  }

  async search(ws, me, q) {
    const query = typeof q === "string" ? cleanName(q).slice(0, SEARCH_LENGTH).toLowerCase() : "";
    if (query.length < 2) return;
    const now = Date.now();
    const times = (this.searches.get(me.id) ?? []).filter((t) => now - t < 10000);
    if (times.length >= SEARCHES_PER_10S) return;
    times.push(now);
    this.searches.set(me.id, times);

    const members = await this.state.storage.list({ prefix: "member:", limit: 5000 });
    const ids = [];
    for (const [key, member] of members) {
      const id = key.slice("member:".length);
      if (id !== me.id && member.name.toLowerCase().includes(query)) ids.push(id);
      if (ids.length >= 20) break;
    }
    const links = await this.links(me.id);
    const relation = (id) =>
      links.friends.includes(id) ? "friend" : links.incoming.includes(id) ? "incoming" : links.outgoing.includes(id) ? "outgoing" : null;
    const players = (await this.profiles(ids)).map((p) => ({ ...p, relation: relation(p.id) }));
    ws.send(JSON.stringify({ t: "found", q, players }));
  }

  async befriend(me, to) {
    if (!(await this.state.storage.get("member:" + to))) return; // nobody by that id has ever been here
    const mine = await this.links(me);
    const theirs = await this.links(to);
    if (mine.friends.includes(to)) return;
    if (mine.incoming.includes(to)) {
      // They asked first: accepting.
      if (mine.friends.length >= FRIENDS_MAX || theirs.friends.length >= FRIENDS_MAX) return;
      mine.incoming = mine.incoming.filter((id) => id !== to);
      theirs.outgoing = theirs.outgoing.filter((id) => id !== me);
      mine.friends.push(to);
      theirs.friends.push(me);
    } else {
      if (mine.outgoing.includes(to) || mine.outgoing.length >= REQUESTS_MAX || theirs.incoming.length >= REQUESTS_MAX) return;
      mine.outgoing.push(to);
      theirs.incoming.push(me);
    }
    await this.state.storage.put({ ["links:" + me]: mine, ["links:" + to]: theirs });
    await this.tellFriends(me, to);
  }

  /** Remove a friend, decline their request, or cancel yours: whichever link there is, both ways. */
  async unfriend(me, to) {
    const mine = await this.links(me);
    const theirs = await this.links(to);
    const drop = (list, id) => list.filter((x) => x !== id);
    mine.friends = drop(mine.friends, to);
    mine.incoming = drop(mine.incoming, to);
    mine.outgoing = drop(mine.outgoing, to);
    theirs.friends = drop(theirs.friends, me);
    theirs.incoming = drop(theirs.incoming, me);
    theirs.outgoing = drop(theirs.outgoing, me);
    await this.state.storage.put({ ["links:" + me]: mine, ["links:" + to]: theirs });
    await this.tellFriends(me, to);
  }

  /** Keeps one member from flooding others with invite notifications. */
  allowInvite(from, to) {
    const now = Date.now();
    const mine = this.sent.get(from) ?? { times: [], last: new Map() };
    mine.times = mine.times.filter((t) => now - t < 60000);
    if (mine.times.length >= INVITES_PER_MINUTE || now - (mine.last.get(to) ?? 0) < INVITE_GAP_MS) return false;
    mine.times.push(now);
    mine.last.set(to, now);
    this.sent.set(from, mine);
    return true;
  }

  gone(ws) {
    const me = ws.deserializeAttachment();
    if (!me) return; // replaced by a newer socket from the same manager
    ws.serializeAttachment(null);
    if (visible(me)) this.broadcast({ t: "gone", id: me.id }, me.id);
  }
}
