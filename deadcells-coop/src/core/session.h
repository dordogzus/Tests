/*
 * LAN session: one host, up to DC_MAX_PLAYERS-1 clients in a star topology.
 * The host relays client traffic, owns slot assignment and is the time base.
 *
 * Transport datagram: magic(4) type(1) body...
 *   CONNECT      name, version, client_salt(8)
 *   CHALLENGE    client_salt, server_salt
 *   RESPONSE     client_salt ^ server_salt, name
 *   ACCEPT       slot, token        DENY reason
 *   DATA         token(8) channel packet
 *   DISCONNECT   token
 *   DISCOVER / DISCOVER_REPLY for LAN browsing
 */
#ifndef DCCOOP_SESSION_H
#define DCCOOP_SESSION_H

#include "channel.h"
#include "netplat.h"

#define DC_MAX_PLAYERS 12
#define DC_NAME_MAX 24
#define DC_DEFAULT_PORT 47770
#define DC_DISCOVERY_PORT 47771
#define DC_PROTOCOL_VERSION 1
#define DC_TIMEOUT 8.0

enum { SESS_IDLE, SESS_HOSTING, SESS_CONNECTING, SESS_CONNECTED, SESS_FAILED };

enum { DENY_FULL = 1, DENY_VERSION, DENY_IN_PROGRESS };

typedef struct {
	int active;
	netaddr addr;
	uint64_t token;
	uint64_t client_salt, server_salt;
	int pending;           /* challenge sent, awaiting response */
	double last_recv, last_send;
	char name[DC_NAME_MAX];
	channel ch;
} dc_peer;

typedef struct dc_session dc_session;

typedef struct {
	void (*on_join)(void *ud, int slot, const char *name);
	void (*on_leave)(void *ud, int slot);
	/* from_slot: sender (client slot on host, 0 on clients) */
	void (*on_reliable)(void *ud, int from_slot, const uint8_t *msg, int len);
	void (*on_unreliable)(void *ud, int from_slot, const uint8_t *msg, int len);
	void (*on_connected)(void *ud, int my_slot);
	void (*on_failed)(void *ud, int reason);
	void *ud;
} dc_callbacks;

typedef struct {
	netaddr addr;
	char name[DC_NAME_MAX];
	int players, max_players, version;
	double seen;
} dc_lan_game;

struct dc_session {
	int state;
	int my_slot;
	char my_name[DC_NAME_MAX];
	udpsock sock;
	udpsock disc;           /* host: discovery listener */
	int disc_open;
	int accepting;          /* host: false once a run locks the lobby */
	dc_peer peers[DC_MAX_PLAYERS]; /* host: [1..]; client: [0] = host */
	dc_callbacks cb;
	double now;
	double connect_started, last_connect_try;
	uint64_t client_salt;
	uint32_t rng;
	/* outgoing unreliable payload per peer, flushed by dc_session_flush */
	uint8_t unrel[DC_MAX_PLAYERS][CH_MTU];
	int unrel_len[DC_MAX_PLAYERS];
	dc_lan_game lan[16];
	int nlan;
	/* simulated link conditions for tests */
	float sim_loss;
};

/* Puts a session in the idle state (no sockets). */
void dc_session_init(dc_session *s);
int dc_session_host(dc_session *s, uint16_t port, const char *name, const dc_callbacks *cb);
int dc_session_join(dc_session *s, const netaddr *host, const char *name, const dc_callbacks *cb);
void dc_session_close(dc_session *s);
/* Receives, times out peers, resends. Call every frame. */
void dc_session_update(dc_session *s, double now);
/* Sends one packet per connected peer containing pending reliable messages
 * and the queued unreliable payload. Call at the network tick rate. */
void dc_session_flush(dc_session *s);

/* Host: to_slot = client slot or -1 for all. Client: to_slot ignored (host). */
int dc_send_reliable(dc_session *s, int to_slot, const void *msg, int len);
/* Replaces the pending unreliable payload for a peer (latest state wins). */
int dc_set_unreliable(dc_session *s, int to_slot, const void *msg, int len);

int dc_player_count(const dc_session *s);
int dc_is_host(const dc_session *s);
int dc_slot_active(const dc_session *s, int slot);
const char *dc_slot_name(const dc_session *s, int slot);
double dc_rtt(const dc_session *s, int slot);
double dc_loss(const dc_session *s, int slot);

/* LAN browser (works while idle or client). */
int dc_discover_open(dc_session *s);
void dc_discover_ping(dc_session *s);

#endif
