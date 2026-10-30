// Co-op rendezvous for DrangedAddons' Direct co-op (Azeroth Survivors).
//
// The host's manager and each joiner's manager open a WebSocket to /lobby/<CODE>. This Worker only passes small
// "signal" messages (each side's public UDP address and certificate thumbprint) between them; the game traffic goes
// straight between the players' PCs over a hole-punched UDP (QUIC) link and never passes through here.
//
// Every socket stays open for the party's life, so the host can keep everyone's party list current, start everyone's
// game together, and remove a player. Messages, as JSON:
//   to a joiner:  {t:"joined", id}  {t:"signal", from:"host", data}  {t:"party", data}  {t:"start"}
//                 {t:"kicked"}  {t:"closed", why}
//   to the host:  {t:"hosting"}  {t:"join", id, name}  {t:"signal", from:<id>, data}  {t:"leave", id}
//   from a joiner: {t:"signal", data}           (always to the host)
//   from the host: {t:"signal", to, data}  {t:"party", data}  {t:"start"}  {t:"kick", to}
//   errors:       {t:"error", why} then the socket closes
// A signal's data.local (an address on the sender's home network) is only passed on when both sides connect from
// the same internet address: nobody else can use it, so it isn't theirs to see.
//
// /presence?key=<install secret>&name=<hero name> keeps one socket per running manager, so players can see who's
// online and invite them (one global Durable Object; fine for a friends-sized group). Others only ever see a hash of
// the key as the player's id, so nobody can copy an id from the list and connect as that player. Older managers
// sent their key as ?id=, which still works.
//   to a player:  {t:"online", players:[{id,name}]}   {t:"here", player:{id,name}}   {t:"gone", id}
//                 {t:"invite", from:{id,name}, code}  {t:"declined", from:{id,name}}
//   from anyone:  {t:"invite", to, code}  {t:"decline", to}

const CODE = /^[A-HJ-NP-Z2-9]{6}$/;
const MAX_JOINERS = 4; // the game's party is 5
const MAX_MESSAGE = 4096;

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (url.pathname === "/presence") {
      if (request.headers.get("Upgrade") !== "websocket") return new Response("expected a WebSocket", { status: 426 });
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
  }

  async fetch(request) {
    const params = new URL(request.url).searchParams;
    const role = params.get("role");
    const ip = request.headers.get("CF-Connecting-IP") ?? "";
    const [client, server] = Object.values(new WebSocketPair());
    const sockets = this.state.getWebSockets();
    const host = sockets.find((ws) => ws.deserializeAttachment()?.role === "host");
    const joiners = sockets.filter((ws) => ws.deserializeAttachment()?.role === "join");

    const refuse = (why) => {
      server.accept();
      server.send(JSON.stringify({ t: "error", why }));
      server.close(1008, why);
      return new Response(null, { status: 101, webSocket: client });
    };

    if (role === "host") {
      if (host) return refuse("taken");
      this.state.acceptWebSocket(server);
      server.serializeAttachment({ role: "host", ip });
      server.send(JSON.stringify({ t: "hosting" }));
    } else if (role === "join") {
      if (!host) return refuse("no lobby");
      if (joiners.length >= MAX_JOINERS) return refuse("full");
      const id = "p" + crypto.randomUUID().slice(0, 8);
      this.state.acceptWebSocket(server);
      server.serializeAttachment({ role: "join", id, ip });
      server.send(JSON.stringify({ t: "joined", id }));
      host.send(JSON.stringify({ t: "join", id, name: cleanName(params.get("name")) || "A player" }));
    } else {
      return new Response("role must be host or join", { status: 400 });
    }
    return new Response(null, { status: 101, webSocket: client });
  }

  async webSocketMessage(ws, raw) {
    if (typeof raw !== "string" || raw.length > MAX_MESSAGE) return;
    let message;
    try {
      message = JSON.parse(raw);
    } catch {
      return;
    }
    const me = ws.deserializeAttachment();
    const sockets = this.state.getWebSockets();
    if (me?.role === "host" && message?.t !== "signal") {
      const joiners = sockets.filter((s) => s.deserializeAttachment()?.role === "join");
      if (message?.t === "party" || message?.t === "start") {
        const text = JSON.stringify(message.t === "party" ? { t: "party", data: message.data } : { t: "start" });
        for (const joiner of joiners) joiner.send(text);
      } else if (message?.t === "kick") {
        const target = joiners.find((s) => s.deserializeAttachment()?.id === message.to);
        if (!target) return;
        target.send(JSON.stringify({ t: "kicked" }));
        target.serializeAttachment({ ...target.deserializeAttachment(), role: "kicked" });
        target.close(1000, "kicked");
      }
      return;
    }
    if (message?.t !== "signal") return;
    if (me?.role === "host") {
      const peer = sockets.find((s) => s.deserializeAttachment()?.id === message.to);
      const data = withoutLocal(message.data, me.ip, peer?.deserializeAttachment()?.ip);
      peer?.send(JSON.stringify({ t: "signal", from: "host", data }));
    } else if (me?.role === "join") {
      const host = sockets.find((s) => s.deserializeAttachment()?.role === "host");
      const data = withoutLocal(message.data, me.ip, host?.deserializeAttachment()?.ip);
      host?.send(JSON.stringify({ t: "signal", from: me.id, data }));
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
        other.send(JSON.stringify({ t: "closed", why: "the host left" }));
        other.close(1000, "host left");
      } else if (me?.role === "join" && other.deserializeAttachment()?.role === "host") {
        other.send(JSON.stringify({ t: "leave", id: me.id }));
      }
    }
  }
}

const KEY = /^[A-Za-z0-9-]{16,64}$/;
const INVITE_GAP_MS = 5000; // the same player, at most once per 5 seconds
const INVITES_PER_MINUTE = 20;

/** The id others see: a hash of the secret key, so seeing it doesn't let anyone connect as that player. */
async function publicId(key) {
  const hash = await crypto.subtle.digest("SHA-256", new TextEncoder().encode("drangedaddons-coop:" + key));
  return [...new Uint8Array(hash).slice(0, 8)].map((b) => b.toString(16).padStart(2, "0")).join("");
}

/** Hero names: no control, zero-width or text-direction characters (they could disguise one name as another). */
function cleanName(name) {
  return (name ?? "").replace(/[\u0000-\u001f\u007f-\u009f​-‏‪-‮⁦-⁩]/g, "").trim().slice(0, 24);
}

export class Presence {
  constructor(state) {
    this.state = state;
    this.sent = new Map(); // sender id -> recent invite times and last invite per target; lost on hibernation, fine
  }

  players() {
    return this.state.getWebSockets().map((ws) => ws.deserializeAttachment()).filter(Boolean);
  }

  async fetch(request) {
    const params = new URL(request.url).searchParams;
    const key = params.get("key") ?? params.get("id") ?? "";
    const name = cleanName(params.get("name"));
    if (!KEY.test(key) || !name) return new Response("key and name required", { status: 400 });
    const id = await publicId(key);

    // The same manager reconnecting replaces its old socket.
    for (const old of this.state.getWebSockets()) {
      if (old.deserializeAttachment()?.id === id) {
        old.serializeAttachment(null);
        old.close(1000, "replaced");
      }
    }
    const [client, server] = Object.values(new WebSocketPair());
    this.state.acceptWebSocket(server);
    const me = { id, name };
    server.serializeAttachment(me);
    server.send(JSON.stringify({ t: "online", players: this.players().filter((p) => p.id !== id) }));
    this.broadcast({ t: "here", player: me }, id);
    return new Response(null, { status: 101, webSocket: client });
  }

  broadcast(message, except) {
    const text = JSON.stringify(message);
    for (const ws of this.state.getWebSockets()) {
      const who = ws.deserializeAttachment();
      if (who && who.id !== except) ws.send(text);
    }
  }

  async webSocketMessage(ws, raw) {
    if (typeof raw !== "string" || raw.length > MAX_MESSAGE) return;
    let message;
    try {
      message = JSON.parse(raw);
    } catch {
      return;
    }
    const me = ws.deserializeAttachment();
    if (!me || typeof message?.to !== "string") return;
    const target = this.state.getWebSockets().find((s) => s.deserializeAttachment()?.id === message.to);
    if (!target) return;
    if (message.t === "invite" && typeof message.code === "string" && CODE.test(message.code)) {
      if (!this.allowInvite(me.id, message.to)) return;
      target.send(JSON.stringify({ t: "invite", from: me, code: message.code }));
    }
    else if (message.t === "decline")
      target.send(JSON.stringify({ t: "declined", from: me }));
  }

  async webSocketClose(ws) {
    this.gone(ws);
  }

  async webSocketError(ws) {
    this.gone(ws);
  }

  /** Keeps one player from flooding others with invite notifications. */
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
    this.broadcast({ t: "gone", id: me.id }, me.id);
  }
}
