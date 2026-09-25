#include "session.h"

#include <stddef.h>
#include <stdlib.h>
#include <string.h>

#define MAGIC 0x44434F50u /* "DCOP" */

enum { PK_CONNECT = 1, PK_CHALLENGE, PK_RESPONSE, PK_ACCEPT, PK_DENY, PK_DATA, PK_DISCONNECT,
	PK_DISCOVER, PK_DISCOVER_REPLY };

static void put32(uint8_t *p, uint32_t v) { for (int i = 0; i < 4; i++) p[i] = (uint8_t)(v >> (8 * i)); }
static uint32_t get32(const uint8_t *p) { return p[0] | (p[1] << 8) | (p[2] << 16) | ((uint32_t)p[3] << 24); }
static void put64(uint8_t *p, uint64_t v) { put32(p, (uint32_t)v); put32(p + 4, (uint32_t)(v >> 32)); }
static uint64_t get64(const uint8_t *p) { return get32(p) | ((uint64_t)get32(p + 4) << 32); }

static uint32_t rnd32(dc_session *s) { /* xorshift32, seeded from time/port */
	uint32_t x = s->rng;
	x ^= x << 13; x ^= x >> 17; x ^= x << 5;
	return s->rng = x ? x : 0x9E3779B9u;
}
static uint64_t rnd64(dc_session *s) { return ((uint64_t)rnd32(s) << 32) | rnd32(s); }

static void seed_rng(dc_session *s) {
	double t = net_time();
	uint64_t bits;
	memcpy(&bits, &t, sizeof bits);
	s->rng = (uint32_t)(bits ^ (bits >> 32) ^ ((uintptr_t)s >> 4) ^ udp_local_port(&s->sock));
	if (!s->rng) s->rng = 1;
}

static int raw_send(dc_session *s, const netaddr *to, const uint8_t *buf, int len) {
	if (s->sim_loss > 0 && (float)(rnd32(s) % 10000) / 10000.f < s->sim_loss) return 0;
	return udp_send(&s->sock, to, buf, len);
}

static int hdr(uint8_t *b, int type) { put32(b, MAGIC); b[4] = (uint8_t)type; return 5; }

static int put_str(uint8_t *b, const char *str) {
	int n = (int)strnlen(str, DC_NAME_MAX - 1);
	b[0] = (uint8_t)n;
	memcpy(b + 1, str, (size_t)n);
	return n + 1;
}

static int get_str(const uint8_t *b, int avail, char *out) {
	if (avail < 1) return -1;
	int n = b[0];
	if (n >= DC_NAME_MAX || n + 1 > avail) return -1;
	memcpy(out, b + 1, (size_t)n);
	out[n] = 0;
	return n + 1;
}

static void reset_peer(dc_peer *p) {
	memset(p, 0, offsetof(dc_peer, ch));
	ch_init(&p->ch);
}

void dc_session_init(dc_session *s) {
	memset(s, 0, sizeof *s);
	s->sock.fd = -1;
	s->disc.fd = -1;
	s->state = SESS_IDLE;
}

int dc_session_host(dc_session *s, uint16_t port, const char *name, const dc_callbacks *cb) {
	dc_session_init(s);
	if (udp_open(&s->sock, port, 0)) return -1;
	s->state = SESS_HOSTING;
	s->my_slot = 0;
	s->accepting = 1;
	s->cb = *cb;
	strncpy(s->my_name, name, DC_NAME_MAX - 1);
	seed_rng(s);
	if (udp_open(&s->disc, DC_DISCOVERY_PORT, 1) == 0) s->disc_open = 1;
	return 0;
}

int dc_session_join(dc_session *s, const netaddr *host, const char *name, const dc_callbacks *cb) {
	dc_session_init(s);
	if (udp_open(&s->sock, 0, 1)) return -1;
	s->state = SESS_CONNECTING;
	s->my_slot = -1;
	s->cb = *cb;
	strncpy(s->my_name, name, DC_NAME_MAX - 1);
	seed_rng(s);
	reset_peer(&s->peers[0]);
	s->peers[0].addr = *host;
	s->client_salt = rnd64(s);
	s->connect_started = net_time();
	s->last_connect_try = 0;
	return 0;
}

void dc_session_close(dc_session *s) {
	uint8_t b[32];
	int n = hdr(b, PK_DISCONNECT);
	for (int i = 0; i < DC_MAX_PLAYERS; i++) {
		if (!s->peers[i].active) continue;
		put64(b + n, s->peers[i].token);
		for (int k = 0; k < 3; k++) udp_send(&s->sock, &s->peers[i].addr, b, n + 8);
	}
	udp_close(&s->sock);
	if (s->disc_open) udp_close(&s->disc);
	s->disc_open = 0;
	s->state = SESS_IDLE;
}

int dc_is_host(const dc_session *s) { return s->state == SESS_HOSTING; }

int dc_slot_active(const dc_session *s, int slot) {
	if (slot < 0 || slot >= DC_MAX_PLAYERS) return 0;
	if (s->state == SESS_HOSTING) return slot == 0 || s->peers[slot].active;
	return 0; /* clients track the roster through game messages */
}

const char *dc_slot_name(const dc_session *s, int slot) {
	if (s->state == SESS_HOSTING && slot == 0) return s->my_name;
	if (slot > 0 && slot < DC_MAX_PLAYERS && s->peers[slot].active) return s->peers[slot].name;
	return "";
}

int dc_player_count(const dc_session *s) {
	if (s->state != SESS_HOSTING) return 1;
	int n = 1;
	for (int i = 1; i < DC_MAX_PLAYERS; i++) n += s->peers[i].active;
	return n;
}

static dc_peer *peer_for(dc_session *s, int slot) {
	if (s->state == SESS_HOSTING) return slot > 0 && slot < DC_MAX_PLAYERS && s->peers[slot].active ? &s->peers[slot] : NULL;
	return s->state == SESS_CONNECTED ? &s->peers[0] : NULL;
}

double dc_rtt(const dc_session *s, int slot) {
	dc_peer *p = peer_for((dc_session *)s, slot);
	return p ? p->ch.rtt : 0;
}

double dc_loss(const dc_session *s, int slot) {
	dc_peer *p = peer_for((dc_session *)s, slot);
	return p ? p->ch.loss : 0;
}

int dc_send_reliable(dc_session *s, int to_slot, const void *msg, int len) {
	if (s->state == SESS_HOSTING && to_slot < 0) {
		int rc = 0;
		for (int i = 1; i < DC_MAX_PLAYERS; i++)
			if (s->peers[i].active && ch_send_reliable(&s->peers[i].ch, msg, len)) rc = -1;
		return rc;
	}
	dc_peer *p = peer_for(s, to_slot);
	return p ? ch_send_reliable(&p->ch, msg, len) : -1;
}

int dc_set_unreliable(dc_session *s, int to_slot, const void *msg, int len) {
	int idx = s->state == SESS_HOSTING ? to_slot : 0;
	if (idx < 0 || idx >= DC_MAX_PLAYERS || len > CH_MTU - 64) return -1;
	memcpy(s->unrel[idx], msg, (size_t)len);
	s->unrel_len[idx] = len;
	return 0;
}

static void send_data(dc_session *s, dc_peer *p, int idx) {
	uint8_t b[CH_MTU + 16];
	int n = hdr(b, PK_DATA);
	put64(b + n, p->token);
	n += 8;
	int len = ch_build_packet(&p->ch, s->now, b + n, CH_MTU - n, s->unrel[idx], s->unrel_len[idx]);
	s->unrel_len[idx] = 0;
	if (len > 0) raw_send(s, &p->addr, b, n + len);
	p->last_send = s->now;
}

void dc_session_flush(dc_session *s) {
	if (s->state == SESS_HOSTING) {
		for (int i = 1; i < DC_MAX_PLAYERS; i++)
			if (s->peers[i].active) send_data(s, &s->peers[i], i);
	} else if (s->state == SESS_CONNECTED) {
		send_data(s, &s->peers[0], 0);
	}
}

static void drop_peer(dc_session *s, int slot) {
	int was = s->peers[slot].active;
	reset_peer(&s->peers[slot]);
	if (was && s->cb.on_leave) s->cb.on_leave(s->cb.ud, slot);
}

static void deliver(dc_session *s, dc_peer *p, int slot, const uint8_t *body, int len) {
	int ulen = 0;
	int off = ch_receive_packet(&p->ch, s->now, body, len, &ulen);
	if (off < 0) return;
	p->last_recv = s->now;
	uint8_t msg[CH_MSG_MAX];
	int n;
	while ((n = ch_pop_reliable(&p->ch, msg, sizeof msg)) >= 0)
		if (s->cb.on_reliable) s->cb.on_reliable(s->cb.ud, slot, msg, n);
	if (ulen > 0 && s->cb.on_unreliable) s->cb.on_unreliable(s->cb.ud, slot, body + off, ulen);
}

static void host_packet(dc_session *s, const netaddr *from, const uint8_t *b, int len) {
	int type = b[4];
	const uint8_t *body = b + 5;
	int blen = len - 5;
	uint8_t out[128];
	int n;
	if (type == PK_CONNECT) {
		char name[DC_NAME_MAX];
		int sl = get_str(body, blen, name);
		if (sl < 0 || blen < sl + 1 + 8) return;
		int version = body[sl];
		uint64_t csalt = get64(body + sl + 1);
		n = hdr(out, PK_DENY);
		if (version != DC_PROTOCOL_VERSION) { out[n++] = DENY_VERSION; raw_send(s, from, out, n); return; }
		int slot = -1, free_slot = -1;
		for (int i = 1; i < DC_MAX_PLAYERS; i++) {
			if ((s->peers[i].active || s->peers[i].pending) && netaddr_eq(&s->peers[i].addr, from)) { slot = i; break; }
			if (!s->peers[i].active && !s->peers[i].pending && free_slot < 0) free_slot = i;
		}
		if (slot < 0) {
			if (!s->accepting) { out[n++] = DENY_IN_PROGRESS; raw_send(s, from, out, n); return; }
			if (free_slot < 0) { out[n++] = DENY_FULL; raw_send(s, from, out, n); return; }
			slot = free_slot;
			reset_peer(&s->peers[slot]);
			s->peers[slot].addr = *from;
			s->peers[slot].pending = 1;
			s->peers[slot].client_salt = csalt;
			s->peers[slot].server_salt = rnd64(s);
			s->peers[slot].last_recv = s->now;
			memcpy(s->peers[slot].name, name, sizeof name);
		}
		dc_peer *p = &s->peers[slot];
		if (p->active) { /* ACCEPT was lost: resend it */
			n = hdr(out, PK_ACCEPT);
			out[n++] = (uint8_t)slot;
			put64(out + n, p->token);
			raw_send(s, from, out, n + 8);
			return;
		}
		n = hdr(out, PK_CHALLENGE);
		put64(out + n, p->client_salt);
		put64(out + n + 8, p->server_salt);
		raw_send(s, from, out, n + 16);
	} else if (type == PK_RESPONSE) {
		if (blen < 8) return;
		uint64_t tok = get64(body);
		for (int i = 1; i < DC_MAX_PLAYERS; i++) {
			dc_peer *p = &s->peers[i];
			if (!netaddr_eq(&p->addr, from) || !(p->pending || p->active)) continue;
			if (tok != (p->client_salt ^ p->server_salt)) return;
			int newly = !p->active;
			p->active = 1;
			p->pending = 0;
			p->token = tok;
			p->last_recv = s->now;
			n = hdr(out, PK_ACCEPT);
			out[n++] = (uint8_t)i;
			put64(out + n, tok);
			raw_send(s, from, out, n + 8);
			if (newly && s->cb.on_join) s->cb.on_join(s->cb.ud, i, p->name);
			return;
		}
	} else if (type == PK_DATA || type == PK_DISCONNECT) {
		if (blen < 8) return;
		uint64_t tok = get64(body);
		for (int i = 1; i < DC_MAX_PLAYERS; i++) {
			dc_peer *p = &s->peers[i];
			if (!p->active || p->token != tok || !netaddr_eq(&p->addr, from)) continue;
			if (type == PK_DISCONNECT) drop_peer(s, i);
			else deliver(s, p, i, body + 8, blen - 8);
			return;
		}
	}
}

static void client_packet(dc_session *s, const netaddr *from, const uint8_t *b, int len) {
	dc_peer *h = &s->peers[0];
	int type = b[4];
	const uint8_t *body = b + 5;
	int blen = len - 5;
	if (type == PK_DISCOVER_REPLY) {
		if (blen < 3) return;
		dc_lan_game g;
		memset(&g, 0, sizeof g);
		g.addr = *from;
		g.version = body[0];
		g.players = body[1];
		g.max_players = body[2];
		if (get_str(body + 3, blen - 3, g.name) < 0) return;
		g.seen = s->now;
		int i = 0;
		while (i < s->nlan && !netaddr_eq(&s->lan[i].addr, from)) i++;
		if (i == s->nlan && s->nlan < 16) s->nlan++;
		if (i < 16) s->lan[i] = g;
		return;
	}
	if (!netaddr_eq(from, &h->addr)) return;
	if (type == PK_CHALLENGE && s->state == SESS_CONNECTING) {
		if (blen < 16 || get64(body) != s->client_salt) return;
		h->server_salt = get64(body + 8);
		h->client_salt = s->client_salt;
		h->pending = 1;
		uint8_t out[64];
		int n = hdr(out, PK_RESPONSE);
		put64(out + n, h->client_salt ^ h->server_salt);
		raw_send(s, from, out, n + 8);
	} else if (type == PK_ACCEPT && s->state == SESS_CONNECTING) {
		if (blen < 9 || get64(body + 1) != (s->client_salt ^ h->server_salt)) return;
		s->my_slot = body[0];
		h->token = get64(body + 1);
		h->active = 1;
		h->pending = 0;
		h->last_recv = s->now;
		s->state = SESS_CONNECTED;
		if (s->cb.on_connected) s->cb.on_connected(s->cb.ud, s->my_slot);
	} else if (type == PK_DENY && s->state == SESS_CONNECTING) {
		s->state = SESS_FAILED;
		if (s->cb.on_failed) s->cb.on_failed(s->cb.ud, blen > 0 ? body[0] : 0);
	} else if (type == PK_DATA && s->state == SESS_CONNECTED) {
		if (blen < 8 || get64(body) != h->token) return;
		deliver(s, h, 0, body + 8, blen - 8);
	} else if (type == PK_DISCONNECT && s->state == SESS_CONNECTED) {
		if (blen < 8 || get64(body) != h->token) return;
		s->state = SESS_FAILED;
		h->active = 0;
		if (s->cb.on_leave) s->cb.on_leave(s->cb.ud, 0);
	}
}

static void answer_discovery(dc_session *s) {
	uint8_t b[256];
	netaddr from;
	int n;
	while ((n = udp_recv(&s->disc, &from, b, sizeof b)) > 0) {
		if (n < 5 || get32(b) != MAGIC || b[4] != PK_DISCOVER) continue;
		uint8_t out[64];
		int o = hdr(out, PK_DISCOVER_REPLY);
		out[o++] = DC_PROTOCOL_VERSION;
		out[o++] = (uint8_t)dc_player_count(s);
		out[o++] = s->accepting ? DC_MAX_PLAYERS : 0;
		o += put_str(out + o, s->my_name);
		/* reply from the game socket so the browser learns the join port */
		udp_send(&s->sock, &from, out, o);
	}
}

void dc_session_update(dc_session *s, double now) {
	s->now = now;
	if (s->state == SESS_IDLE || s->state == SESS_FAILED) {
		if (s->state == SESS_IDLE && s->sock.fd >= 0) {
			uint8_t b[CH_MTU + 64];
			netaddr from;
			int n;
			while ((n = udp_recv(&s->sock, &from, b, sizeof b)) > 0)
				if (n >= 5 && get32(b) == MAGIC) client_packet(s, &from, b, n);
		}
		return;
	}
	uint8_t b[CH_MTU + 64];
	netaddr from;
	int n;
	while ((n = udp_recv(&s->sock, &from, b, sizeof b)) > 0) {
		if (n < 5 || get32(b) != MAGIC) continue;
		if (s->state == SESS_HOSTING) host_packet(s, &from, b, n);
		else client_packet(s, &from, b, n);
	}
	if (s->state == SESS_HOSTING) {
		if (s->disc_open) answer_discovery(s);
		for (int i = 1; i < DC_MAX_PLAYERS; i++) {
			dc_peer *p = &s->peers[i];
			if ((p->active || p->pending) && now - p->last_recv > DC_TIMEOUT) drop_peer(s, i);
			/* a peer whose reliable window is full can never recover */
			else if (p->active && ch_pending_reliable(&p->ch) >= CH_REL_WINDOW) drop_peer(s, i);
		}
	} else if (s->state == SESS_CONNECTING) {
		if (now - s->connect_started > DC_TIMEOUT) {
			s->state = SESS_FAILED;
			if (s->cb.on_failed) s->cb.on_failed(s->cb.ud, 0);
		} else if (now - s->last_connect_try > 0.25) {
			s->last_connect_try = now;
			uint8_t out[64];
			int o = hdr(out, s->peers[0].pending ? PK_RESPONSE : PK_CONNECT);
			if (s->peers[0].pending) {
				put64(out + o, s->peers[0].client_salt ^ s->peers[0].server_salt);
				o += 8;
			} else {
				o += put_str(out + o, s->my_name);
				out[o++] = DC_PROTOCOL_VERSION;
				put64(out + o, s->client_salt);
				o += 8;
			}
			raw_send(s, &s->peers[0].addr, out, o);
		}
	} else if (s->state == SESS_CONNECTED) {
		if (now - s->peers[0].last_recv > DC_TIMEOUT || ch_pending_reliable(&s->peers[0].ch) >= CH_REL_WINDOW) {
			s->state = SESS_FAILED;
			s->peers[0].active = 0;
			if (s->cb.on_leave) s->cb.on_leave(s->cb.ud, 0);
		}
	}
}

int dc_discover_open(dc_session *s) {
	if (s->sock.fd >= 0) return 0;
	return udp_open(&s->sock, 0, 1);
}

void dc_discover_ping(dc_session *s) {
	uint8_t b[8];
	int n = hdr(b, PK_DISCOVER);
	netaddr bc = { 0xFFFFFFFFu, DC_DISCOVERY_PORT };
	udp_send(&s->sock, &bc, b, n);
	netaddr lo = { 0x7F000001u, DC_DISCOVERY_PORT };
	udp_send(&s->sock, &lo, b, n);
	for (int i = 0; i < s->nlan; i++)
		if (s->now - s->lan[i].seen > 5.0) s->lan[i--] = s->lan[--s->nlan];
}
