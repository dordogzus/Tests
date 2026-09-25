#include "rules.h"

#include <math.h>
#include <string.h>

/* ---------------- balance ---------------- */

void balance_default_cfg(balance_cfg *c) {
	c->density_per_player = 0.20f;
	c->density_cap = 2.5f;
	c->pressure_per_player = 0.03f;
	c->boss_falloff = 0.015f;
	c->dmg_per_player = 0.05f;
	c->dmg_cap = 1.5f;
	c->elite_per_player = 0.04f;
	c->flask_bonus_per_player = 0.0f;
}

static float clampf(float v, float lo, float hi) { return v < lo ? lo : v > hi ? hi : v; }

void balance_compute(const balance_cfg *c, int n, balance_mults *m) {
	if (n < 1) n = 1;
	if (n > RULES_MAX_PLAYERS) n = RULES_MAX_PLAYERS;
	float extra = (float)(n - 1);
	m->players = n;
	/* More players spread over more enemies rather than only tankier ones. */
	m->density = clampf(1.f + c->density_per_player * extra, 1.f, c->density_cap);
	/* Group damage grows ~n; keep total enemy hp pool / group dps near a slowly
	 * rising pressure target R(n): pool = hp * density = n * R(n). */
	float pressure = 1.f + c->pressure_per_player * extra;
	m->mob_hp = (float)n * pressure / m->density;
	m->elite_hp = m->mob_hp * (1.f + 0.02f * extra);
	/* Bosses have no density lever; focus fire has near-100% uptime in groups. */
	m->boss_hp = (float)n * clampf(1.f - c->boss_falloff * extra, 0.5f, 1.f);
	m->mob_dmg = clampf(1.f + c->dmg_per_player * extra, 1.f, c->dmg_cap);
	m->elite_chance = 1.f + c->elite_per_player * extra;
	/* bigger groups revive more often; lower the payoff so downs still matter */
	m->revive_hp = clampf(0.40f - 0.015f * extra, 0.25f, 0.40f);
}

void balance_rescale(int *life, int *max_life, float old_mult, float new_mult) {
	if (*max_life <= 0 || old_mult <= 0) return;
	double base = (double)*max_life / old_mult;
	double ratio = (double)*life / (double)*max_life;
	int nmax = (int)lround(base * new_mult);
	if (nmax < 1) nmax = 1;
	int nlife = (int)lround(ratio * nmax);
	if (*life > 0 && nlife < 1) nlife = 1; /* rescaling never kills */
	*max_life = nmax;
	*life = nlife;
}

float balance_bleedout(int downs) {
	float t = 30.f * powf(0.75f, (float)downs);
	return t < 6.f ? 6.f : t;
}

float balance_revive_time(int revivers) {
	if (revivers < 1) return INFINITY;
	float t = 3.f * powf(0.7f, (float)(revivers - 1));
	return t < 1.2f ? 1.2f : t;
}

/* ---------------- pvp ---------------- */

void pvp_init(pvp_state *p) {
	memset(p, 0, sizeof *p);
	p->mode = PVP_FFA;
	p->damage_scale = 0.35f;
	p->hit_immunity = 0.6f;
}

static int valid_slot(int s) { return s >= 0 && s < RULES_MAX_PLAYERS; }

int pvp_allowed(const pvp_state *p, int a, int v, double now) {
	if (p->mode == PVP_OFF || p->safe_zone) return 0;
	if (!valid_slot(a) || !valid_slot(v) || a == v) return 0;
	/* mutual consent: both must have opted in */
	if (!p->enabled[a] || !p->enabled[v]) return 0;
	if (p->mode == PVP_TEAMS && p->team[a] == p->team[v]) return 0;
	return now >= p->immune_until[v];
}

int pvp_resolve(pvp_state *p, int a, int v, int damage, double now) {
	if (damage <= 0 || !pvp_allowed(p, a, v, now)) return 0;
	int d = (int)lroundf((float)damage * p->damage_scale);
	if (d < 1) d = 1;
	p->immune_until[v] = now + p->hit_immunity;
	return d;
}

void pvp_toggle(pvp_state *p, int player) {
	if (valid_slot(player)) p->enabled[player] = !p->enabled[player];
}

/* ---------------- threat ---------------- */

void threat_init(threat_table *t) {
	memset(t, 0, sizeof *t);
	t->target = -1;
	t->last_switch = -1e9;
}

void threat_add(threat_table *t, int p, float amount) {
	if (valid_slot(p) && amount > 0) t->threat[p] += amount;
}

void threat_decay(threat_table *t, float dt, float half_life) {
	float k = half_life > 0 ? powf(0.5f, dt / half_life) : 0.f;
	for (int i = 0; i < RULES_MAX_PLAYERS; i++) t->threat[i] *= k;
}

#define THREAT_MIN_HOLD 1.5   /* seconds before voluntary retarget */
#define THREAT_HYSTERESIS 1.3f

static float threat_score(const threat_table *t, int i, float ex, float ey, const threat_player *p, const int *load,
	int fair_share, float range, int is_current) {
	float dx = p->x - ex, dy = p->y - ey;
	float dist = sqrtf(dx * dx + dy * dy);
	if (dist > range && t->threat[i] <= 0) return -1.f;
	/* proximity dominates for unengaged enemies, damage threat once engaged */
	float prox = 1.f - clampf(dist / range, 0.f, 1.f);
	float s = (prox * 10.f + t->threat[i] * 0.1f) * (p->taunt > 0 ? p->taunt : 1.f);
	/* spread enemies so one player isn't swarmed while others idle */
	int over = load[i] - (is_current ? 1 : 0) - fair_share;
	if (over >= 0) s *= powf(0.6f, (float)(over + 1));
	return s;
}

int threat_pick(threat_table *t, float ex, float ey, const threat_player *pl, int n, const int *load, int fair_share,
	float range, double now) {
	int best = -1;
	float best_s = 0.f, cur_s = -1.f;
	for (int i = 0; i < n && i < RULES_MAX_PLAYERS; i++) {
		const threat_player *p = &pl[i];
		if (!p->present || p->downed || p->invisible) continue;
		float s = threat_score(t, i, ex, ey, p, load, fair_share, range, i == t->target);
		if (s < 0) continue;
		if (i == t->target) cur_s = s;
		if (s > best_s) { best_s = s; best = i; }
	}
	int cur_valid = cur_s >= 0.f;
	if (cur_valid && best != t->target) {
		int held = now - t->last_switch < THREAT_MIN_HOLD;
		if (held || best_s < cur_s * THREAT_HYSTERESIS) return t->target;
	}
	if (best != t->target) {
		t->target = best;
		t->last_switch = now;
	}
	return best;
}

/* ---------------- downed / revive ---------------- */

void life_init(life_state *l) { memset(l, 0, sizeof *l); }

int life_on_lethal(life_state *l, int others_alive) {
	if (l->state != LIFE_ALIVE) return 0;
	if (others_alive <= 0) { /* nobody can revive: real death */
		l->state = LIFE_DEAD;
		return 0;
	}
	l->state = LIFE_DOWNED;
	l->bleed_left = balance_bleedout(l->downs);
	l->revive_progress = 0.f;
	l->downs++;
	return 1;
}

void life_update(life_state *l, float dt, int revivers) {
	if (l->state != LIFE_DOWNED) return;
	if (revivers > 0) {
		l->revive_progress += dt / balance_revive_time(revivers);
		if (l->revive_progress > 1.f) l->revive_progress = 1.f;
		return; /* bleed-out pauses while being revived */
	}
	/* progress decays instead of resetting, so brief interruptions are forgiving */
	l->revive_progress = clampf(l->revive_progress - dt * 0.15f, 0.f, 1.f);
	l->bleed_left -= dt;
	if (l->bleed_left <= 0) l->state = LIFE_DEAD;
}

int life_take_revived(life_state *l) {
	if (l->state == LIFE_DOWNED && l->revive_progress >= 1.f) {
		l->state = LIFE_ALIVE;
		l->revive_progress = 0.f;
		return 1;
	}
	return 0;
}

void life_new_level(life_state *l) {
	l->downs = 0;
	if (l->state == LIFE_SPECTATING || l->state == LIFE_DEAD) l->state = LIFE_ALIVE; /* respawn at next level */
}

int life_team_wiped(const life_state *all, const uint8_t *present, int n) {
	int any = 0;
	for (int i = 0; i < n; i++) {
		if (!present[i]) continue;
		any = 1;
		if (all[i].state == LIFE_ALIVE) return 0;
	}
	return any;
}
