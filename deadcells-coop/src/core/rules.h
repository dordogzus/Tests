/*
 * Co-op gameplay rules: player-count balance, PvP consent, enemy threat and
 * the downed/revive state machine. Pure logic, no game or network access, so
 * every formula is unit tested. See docs/BALANCE.md for the rationale.
 */
#ifndef DCCOOP_RULES_H
#define DCCOOP_RULES_H

#include <stdint.h>

#define RULES_MAX_PLAYERS 12

/* ---------------- balance ---------------- */

typedef struct {
	float density_per_player;   /* extra spawn density per extra player */
	float density_cap;          /* max spawn density multiplier */
	float pressure_per_player;  /* target group effort growth (coordination bonus) */
	float boss_falloff;         /* boss hp per-player efficiency loss */
	float dmg_per_player;       /* enemy damage growth per extra player */
	float dmg_cap;
	float elite_per_player;     /* additive elite chance multiplier */
	float flask_bonus_per_player;
} balance_cfg;

typedef struct {
	int players;
	float mob_hp;       /* regular enemy max hp multiplier */
	float elite_hp;
	float boss_hp;
	float mob_dmg;      /* enemy -> player damage multiplier */
	float density;      /* spawn count multiplier (level generator) */
	float elite_chance;
	float revive_hp;    /* fraction of max hp restored on revive */
} balance_mults;

void balance_default_cfg(balance_cfg *c);
void balance_compute(const balance_cfg *c, int players, balance_mults *out);
/* Rescale a live enemy when the player count changes, preserving hp ratio. */
void balance_rescale(int *life, int *max_life, float old_mult, float new_mult);
/* Downed-state bleed out time, shrinking with repeated downs in a level. */
float balance_bleedout(int downs_this_level);
/* Revive channel time for a number of simultaneous revivers. */
float balance_revive_time(int revivers);

/* ---------------- pvp ---------------- */

enum { PVP_OFF = 0, PVP_FFA, PVP_TEAMS };

typedef struct {
	int mode;                 /* server-wide mode */
	uint8_t enabled[RULES_MAX_PLAYERS]; /* per-player opt-in */
	uint8_t team[RULES_MAX_PLAYERS];
	float damage_scale;       /* player vs player damage multiplier */
	float hit_immunity;       /* seconds of pvp immunity after a pvp hit */
	double immune_until[RULES_MAX_PLAYERS];
	uint8_t safe_zone;        /* transitions / shops: no pvp */
} pvp_state;

void pvp_init(pvp_state *p);
/* Returns scaled damage (0 = blocked). Registers the hit for immunity. */
int pvp_resolve(pvp_state *p, int attacker, int victim, int damage, double now);
int pvp_allowed(const pvp_state *p, int attacker, int victim, double now);
void pvp_toggle(pvp_state *p, int player);

/* ---------------- threat / aggro ---------------- */

typedef struct {
	float threat[RULES_MAX_PLAYERS];
	int target;          /* current player slot, -1 none */
	double last_switch;
} threat_table;

typedef struct {
	float x, y;
	uint8_t present, downed, invisible;
	float taunt;         /* multiplier from taunt items, 1 = none */
} threat_player;

void threat_init(threat_table *t);
void threat_add(threat_table *t, int player, float amount);
/* Exponential decay with the given half-life. */
void threat_decay(threat_table *t, float dt, float half_life);
/*
 * Chooses a target for one enemy.
 *   load[i]: how many enemies already target player i (for spreading)
 *   fair_share: ceil(enemies / players); players above it are penalized
 * Keeps the current target unless another is clearly better (hysteresis) and
 * a minimum time has passed, so enemies do not jitter between players.
 */
int threat_pick(threat_table *t, float ex, float ey, const threat_player *pl, int n, const int *load,
	int fair_share, float aggro_range, double now);

/* ---------------- downed / revive ---------------- */

enum { LIFE_ALIVE = 0, LIFE_DOWNED, LIFE_DEAD, LIFE_SPECTATING };

typedef struct {
	uint8_t state;
	uint8_t downs;          /* this level */
	float bleed_left;       /* seconds until death while downed */
	float revive_progress;  /* 0..1 */
} life_state;

void life_init(life_state *l);
/* Called when damage would kill the player. Returns 1 if downed instead. */
int life_on_lethal(life_state *l, int others_alive);
/* Advances bleed-out and revive; revivers = allies channeling nearby. */
void life_update(life_state *l, float dt, int revivers);
/* Returns 1 exactly once when revive completes. */
int life_take_revived(life_state *l);
void life_new_level(life_state *l);
/* Whole team downed or dead -> run over. */
int life_team_wiped(const life_state *all, const uint8_t *present, int n);

#endif
