// Co-op rendezvous for DrangedAddons' Direct co-op (Azeroth Survivors).
//
// The host's manager and each joiner's manager open a WebSocket to /lobby/<CODE>. This Worker only passes small
// "signal" messages (each side's public UDP address and certificate thumbprint) between them; the game traffic goes
// straight between the players' PCs over a hole-punched UDP (QUIC) link and never passes through here.
//
// Messages, as JSON:
//   to a joiner:  {t:"joined", id}             {t:"signal", from:"host", data}   {t:"closed", why}
//   to the host:  {t:"hosting"}  {t:"join", id} {t:"signal", from:<id>, data}     {t:"leave", id}
//   from anyone:  {t:"signal", to?, data}      (a joiner's always goes to the host; the host's needs `to`)
//   errors:       {t:"error", why} then the socket closes
//
// /presence?id=<install id>&name=<hero name> keeps one socket per running manager, so players can see who's online
// and invite them (one global Durable Object; fine for a friends-sized group):
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

export class Lobby {
  constructor(state) {
    this.state = state;
  }

  async fetch(request) {
    const role = new URL(request.url).searchParams.get("role");
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
      server.serializeAttachment({ role: "host" });
      server.send(JSON.stringify({ t: "hosting" }));
    } else if (role === "join") {
      if (!host) return refuse("no lobby");
      if (joiners.length >= MAX_JOINERS) return refuse("full");
      const id = "p" + crypto.randomUUID().slice(0, 8);
      this.state.acceptWebSocket(server);
      server.serializeAttachment({ role: "join", id });
      server.send(JSON.stringify({ t: "joined", id }));
      host.send(JSON.stringify({ t: "join", id }));
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
    if (message?.t !== "signal") return;
    const me = ws.deserializeAttachment();
    const sockets = this.state.getWebSockets();
    if (me?.role === "host") {
      const peer = sockets.find((s) => s.deserializeAttachment()?.id === message.to);
      peer?.send(JSON.stringify({ t: "signal", from: "host", data: message.data }));
    } else if (me?.role === "join") {
      const host = sockets.find((s) => s.deserializeAttachment()?.role === "host");
      host?.send(JSON.stringify({ t: "signal", from: me.id, data: message.data }));
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

const ID = /^[A-Za-z0-9-]{8,64}$/;

export class Presence {
  constructor(state) {
    this.state = state;
  }

  players() {
    return this.state.getWebSockets().map((ws) => ws.deserializeAttachment()).filter(Boolean);
  }

  async fetch(request) {
    const params = new URL(request.url).searchParams;
    const id = params.get("id") ?? "";
    const name = (params.get("name") ?? "").replace(/[\u0000-\u001f]/g, "").trim().slice(0, 24);
    if (!ID.test(id) || !name) return new Response("id and name required", { status: 400 });

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
    if (message.t === "invite" && typeof message.code === "string" && CODE.test(message.code))
      target.send(JSON.stringify({ t: "invite", from: me, code: message.code }));
    else if (message.t === "decline")
      target.send(JSON.stringify({ t: "declined", from: me }));
  }

  async webSocketClose(ws) {
    this.gone(ws);
  }

  async webSocketError(ws) {
    this.gone(ws);
  }

  gone(ws) {
    const me = ws.deserializeAttachment();
    if (!me) return; // replaced by a newer socket from the same manager
    ws.serializeAttachment(null);
    this.broadcast({ t: "gone", id: me.id }, me.id);
  }
}
