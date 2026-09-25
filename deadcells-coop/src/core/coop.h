/*
 * Co-op world model shared by host and clients.
 *
 * Authority model (see docs/NETCODE.md):
 *  - every player owns their hero: movement is simulated locally (zero input
 *    latency) and streamed to the host, which relays it to everyone;
 *  - the host owns enemies: AI, hp and deaths. Clients skip enemy updates and
 *    render host snapshots through interpolation;
 *  - hits are resolved where the attacker is, then forwarded to the owner of
 *    the victim (client hits on enemies go to the host, host enemy hits on a
 *    remote hero go to that hero's owner, who applies them with local i-frames).
 */
#ifndef DCCOOP_COOP_H
#define DCCOOP_COOP_H

#include "clocksync.h"
#include "config.h"
#include "interp.h"
#include "rules.h"
#include "session.h"

#define COOP_MAX_MOBS 1024

/* Game boundary: implemented over HashLink objects by the .hdll, or by a
 * fake world in tests. Entities are opaque handles. */
typedef struct {
	void (*read)(void *ud, void *ent, ent_state *out);
	void (*write)(void *ud, void *ent, const ent_state *st);
	void *(*spawn_ghost)(void *ud, int slot, const ent_state *at);
	void (*despawn)(void *ud, void *ent);
	/* queue damage through the game's own hit logic (animations, death) */
	void (*damage)(void *ud, void *ent, int amount);
	void (*set_max_life)(void *ud, void *ent, int life, int max_life);
	int (*max_life)(void *ud, void *ent);
	void (*award)(void *ud, int cells, int gold);
	void (*notify)(void *ud, const char *text);
	void *ud;
} coop_adapter;

typedef struct {
	uint8_t present;
	char name[DC_NAME_MAX];
	void *ghost;            /* remote players: game entity driven by snapshots */
	interp_track track;
	ent_state last;         /* latest known state */
	life_state life;
	int pending_damage;     /* owner side: queued hits from others */
	double echo_t;          /* host: client clock value to echo for sync */
} coop_player;

typedef struct {
	void *ent;
	uint16_t id;
	uint8_t active, boss, elite, dead;
	float hp_mult;          /* multiplier currently applied */
	int base_max_life;      /* unscaled max life, avoids rounding drift */
	threat_table threat;    /* host */
	interp_track track;     /* clients */
	float prio[DC_MAX_PLAYERS]; /* host: snapshot priority per client */
	int pending_damage;
} coop_mob;

enum { ENT_NONE, ENT_LOCAL_HERO, ENT_GHOST, ENT_MOB };

typedef struct {
	dc_config cfg;
	dc_session sess;
	coop_adapter ad;
	int my_slot;
	int running;
	coop_player players[DC_MAX_PLAYERS];
	void *local_hero;
	coop_mob mobs[COOP_MAX_MOBS];
	int nmobs_spawned;      /* spawn-order ids for the current level */
	uint32_t level_seq;
	uint32_t run_seed;
	balance_mults bal;
	pvp_state pvp;
	clocksync clock;
	double now, last_send, last_frame, host_time;
	double time_req_sent;
	uint32_t snap_seq;
	/* stats */
	uint32_t bytes_out, snaps_in;
} coop_state;

void coop_init(coop_state *c, const dc_config *cfg, const coop_adapter *ad);
int coop_host(coop_state *c);
int coop_join(coop_state *c, const char *addr);
void coop_shutdown(coop_state *c);
/* Per-frame pump: network io, remote entity interpolation, AI targeting. */
void coop_frame(coop_state *c, double now);

int coop_is_host(const coop_state *c);
int coop_active(const coop_state *c);
int coop_player_count(const coop_state *c);

void coop_set_local_hero(coop_state *c, void *hero);
int coop_classify(const coop_state *c, void *ent, int *slot_or_id);

/* Level lifecycle (host decides; clients follow). */
void coop_level_start(coop_state *c, uint32_t seed);
/* Enemy registered by spawn order; returns multiplier to apply to its hp. */
float coop_register_mob(coop_state *c, void *ent, int is_boss, int is_elite);
void coop_unregister_entity(coop_state *c, void *ent);
/* 1 = skip this enemy's own update (clients) */
int coop_mob_skip_update(coop_state *c, void *ent);
/* 1 = skip this hero's own update (ghosts) */
int coop_hero_skip_update(coop_state *c, void *ent);
/* Damage hook: returns the damage the game should apply locally. */
int coop_on_damage(coop_state *c, void *target, void *source, int dmg);
/* Pending damage queued for an entity by remote events (consumed). */
int coop_take_pending_damage(coop_state *c, void *ent);
/* Host: returns the hero entity an enemy should target, NULL = keep game choice. */
void *coop_pick_target(coop_state *c, void *mob);
void coop_toggle_pvp(coop_state *c);
void coop_award(coop_state *c, int cells, int gold);

#endif
