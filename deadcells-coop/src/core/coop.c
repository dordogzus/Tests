#include "coop.h"

#include "bitbuf.h"

#include <math.h>
#include <stdio.h>
#include <string.h>

enum {
	/* reliable */
	M_HELLO = 1,     /* host->client: slot, run seed, level seq, roster, pvp */
	M_JOIN,          /* host->all: slot, name */
	M_LEAVE,         /* host->all: slot */
	M_LEVEL,         /* host->all: level seq, seed */
	M_HIT_MOB,       /* client->host: mob id, dmg */
	M_HIT_PLAYER,    /* ->owner (relayed by host): target slot, attacker slot (255 = enemy), dmg */
	M_MOB_DEAD,      /* host->all: mob id */
	M_PVP,           /* client->host: toggle; host->all: slot, enabled, team */
	M_LIFE,          /* owner->host->all: slot, life state */
	M_AWARD,         /* ->host->all: cells, gold */
	/* unreliable */
	U_PLAYER = 64,   /* client->host: own state + time request */
	U_SNAPSHOT,      /* host->client */
};

#define POS_MIN -64.f
#define POS_MAX 4032.f
#define POS_BITS 18        /* 1/64 cell */
#define VEL_MAX 96.f
#define VEL_BITS 12
#define SNAP_BUDGET 1000
#define ENEMY_SLOT 255

/* ---------------- helpers ---------------- */

static void w_state(bitbuf *b, const ent_state *s) {
	bb_write_q(b, s->x, POS_MIN, POS_MAX, POS_BITS);
	bb_write_q(b, s->y, POS_MIN, POS_MAX, POS_BITS);
	int moving = s->vx != 0.f || s->vy != 0.f;
	bb_write_bool(b, moving);
	if (moving) {
		bb_write_q(b, s->vx, -VEL_MAX, VEL_MAX, VEL_BITS);
		bb_write_q(b, s->vy, -VEL_MAX, VEL_MAX, VEL_BITS);
	}
	bb_write_varu(b, (uint32_t)(s->life < 0 ? 0 : s->life));
	bb_write(b, s->anim, 10);
	bb_write(b, s->dir ? 1 : 0, 1);
	bb_write(b, s->flags, 8);
}

static void r_state(bitbuf *b, ent_state *s) {
	s->x = bb_read_q(b, POS_MIN, POS_MAX, POS_BITS);
	s->y = bb_read_q(b, POS_MIN, POS_MAX, POS_BITS);
	if (bb_read_bool(b)) {
		s->vx = bb_read_q(b, -VEL_MAX, VEL_MAX, VEL_BITS);
		s->vy = bb_read_q(b, -VEL_MAX, VEL_MAX, VEL_BITS);
	} else {
		s->vx = s->vy = 0.f;
	}
	s->life = (int32_t)bb_read_varu(b);
	s->anim = (uint16_t)bb_read(b, 10);
	s->dir = (uint8_t)bb_read(b, 1);
	s->flags = (uint8_t)bb_read(b, 8);
}

static coop_mob *mob_by_ent(coop_state *c, void *ent) {
	if (!ent) return NULL;
	for (int i = 0; i < COOP_MAX_MOBS; i++)
		if (c->mobs[i].active && c->mobs[i].ent == ent) return &c->mobs[i];
	return NULL;
}

static coop_mob *mob_by_id(coop_state *c, int id) {
	if (id < 0 || id >= COOP_MAX_MOBS) return NULL;
	return c->mobs[id].active ? &c->mobs[id] : NULL;
}

static int send_rel(coop_state *c, int to, const uint8_t *m, int n) { return dc_send_reliable(&c->sess, to, m, n); }

static void log_note(coop_state *c, const char *fmt, int a, const char *s) {
	char buf[128];
	snprintf(buf, sizeof buf, fmt, a, s ? s : "");
	if (c->ad.notify) c->ad.notify(c->ad.ud, buf);
}

static void recompute_balance(coop_state *c) {
	balance_mults old = c->bal;
	balance_compute(&c->cfg.balance, coop_player_count(c), &c->bal);
	if (old.players == c->bal.players || !c->ad.read || !c->ad.max_life || !c->ad.set_max_life) return;
	/* both sides rescale so hp bars agree; host snapshots stay authoritative for life */
	for (int i = 0; i < COOP_MAX_MOBS; i++) {
		coop_mob *m = &c->mobs[i];
		if (!m->active || m->dead) continue;
		float nm = m->boss ? c->bal.boss_hp : m->elite ? c->bal.elite_hp : c->bal.mob_hp;
		ent_state s;
		c->ad.read(c->ad.ud, m->ent, &s);
		int life = s.life, maxl = c->ad.max_life(c->ad.ud, m->ent);
		if (m->base_max_life <= 0) m->base_max_life = (int)lround(maxl / (double)m->hp_mult);
		balance_rescale(&life, &maxl, (float)maxl / (float)m->base_max_life, nm);
		c->ad.set_max_life(c->ad.ud, m->ent, life, maxl);
		m->hp_mult = nm;
	}
}

/* ---------------- session callbacks ---------------- */

static void add_player(coop_state *c, int slot, const char *name) {
	coop_player *p = &c->players[slot];
	memset(p, 0, sizeof *p);
	p->present = 1;
	snprintf(p->name, sizeof p->name, "%s", name);
	life_init(&p->life);
	interp_clear(&p->track);
}

static void remove_player(coop_state *c, int slot) {
	coop_player *p = &c->players[slot];
	if (p->ghost && c->ad.despawn) c->ad.despawn(c->ad.ud, p->ghost);
	memset(p, 0, sizeof *p);
	c->pvp.enabled[slot] = 0;
	for (int i = 0; i < COOP_MAX_MOBS; i++) {
		c->mobs[i].threat.threat[slot] = 0;
		if (c->mobs[i].threat.target == slot) c->mobs[i].threat.target = -1;
	}
}

static void cb_join(void *ud, int slot, const char *name) {
	coop_state *c = ud;
	add_player(c, slot, name);
	/* hello to the new player, roster update to everyone else */
	uint8_t m[CH_MSG_MAX];
	bitbuf b;
	bb_init(&b, m, sizeof m);
	bb_write(&b, M_HELLO, 8);
	bb_write(&b, (uint32_t)slot, 8);
	bb_write(&b, c->run_seed, 32);
	bb_write(&b, c->level_seq, 32);
	bb_write(&b, (uint32_t)c->pvp.mode, 2);
	for (int i = 0; i < DC_MAX_PLAYERS; i++) {
		bb_write_bool(&b, c->players[i].present);
		if (!c->players[i].present) continue;
		bb_write_str(&b, c->players[i].name, DC_NAME_MAX);
		bb_write_bool(&b, c->pvp.enabled[i]);
		bb_write(&b, c->pvp.team[i], 1);
	}
	send_rel(c, slot, m, bb_bytes(&b));
	for (int i = 1; i < DC_MAX_PLAYERS; i++) {
		if (i == slot || !c->players[i].present) continue;
		bb_init(&b, m, sizeof m);
		bb_write(&b, M_JOIN, 8);
		bb_write(&b, (uint32_t)slot, 8);
		bb_write_str(&b, name, DC_NAME_MAX);
		send_rel(c, i, m, bb_bytes(&b));
	}
	/* alternate teams for team pvp */
	c->pvp.team[slot] = (uint8_t)(slot & 1);
	recompute_balance(c);
	log_note(c, "Player %d joined: %s", slot, name);
}

static void cb_leave(void *ud, int slot) {
	coop_state *c = ud;
	if (coop_is_host(c)) {
		remove_player(c, slot);
		uint8_t m[2] = { M_LEAVE, (uint8_t)slot };
		send_rel(c, -1, m, 2);
		recompute_balance(c);
		log_note(c, "Player %d left%s", slot, NULL);
	} else {
		/* lost the host: fall back to solo so the run can continue */
		for (int i = 0; i < DC_MAX_PLAYERS; i++)
			if (i != c->my_slot && c->players[i].present) remove_player(c, i);
		c->running = 0;
		recompute_balance(c);
		log_note(c, "Disconnected from host (%d)%s", slot, NULL);
	}
}

static void cb_connected(void *ud, int slot) {
	coop_state *c = ud;
	c->my_slot = slot;
}

static void cb_failed(void *ud, int reason) {
	coop_state *c = ud;
	c->running = 0;
	log_note(c, "Connection failed (reason %d)%s", reason, NULL);
}

static void apply_hit_player(coop_state *c, int target, int attacker, int dmg) {
	if (target == c->my_slot) {
		c->players[target].pending_damage += dmg;
		(void)attacker;
	} else if (coop_is_host(c) && target > 0 && target < DC_MAX_PLAYERS && c->players[target].present) {
		uint8_t m[8];
		bitbuf b;
		bb_init(&b, m, sizeof m);
		bb_write(&b, M_HIT_PLAYER, 8);
		bb_write(&b, (uint32_t)target, 8);
		bb_write(&b, (uint32_t)attacker, 8);
		bb_write_varu(&b, (uint32_t)dmg);
		send_rel(c, target, m, bb_bytes(&b));
	}
}

static void cb_reliable(void *ud, int from, const uint8_t *msg, int len) {
	coop_state *c = ud;
	bitbuf b;
	bb_init(&b, (void *)msg, len);
	int type = (int)bb_read(&b, 8);
	int host = coop_is_host(c);
	switch (type) {
	case M_HELLO: {
		if (host) return;
		c->my_slot = (int)bb_read(&b, 8);
		c->run_seed = bb_read(&b, 32);
		c->level_seq = bb_read(&b, 32);
		c->pvp.mode = (int)bb_read(&b, 2);
		for (int i = 0; i < DC_MAX_PLAYERS; i++) {
			if (!bb_read_bool(&b)) continue;
			char name[DC_NAME_MAX];
			bb_read_str(&b, name, DC_NAME_MAX);
			if (b.overflow) return;
			add_player(c, i, name);
			c->pvp.enabled[i] = (uint8_t)bb_read_bool(&b);
			c->pvp.team[i] = (uint8_t)bb_read(&b, 1);
		}
		recompute_balance(c);
		break;
	}
	case M_JOIN: {
		int slot = (int)bb_read(&b, 8);
		char name[DC_NAME_MAX];
		bb_read_str(&b, name, DC_NAME_MAX);
		if (host || b.overflow || slot >= DC_MAX_PLAYERS) return;
		add_player(c, slot, name);
		recompute_balance(c);
		log_note(c, "Player %d joined: %s", slot, name);
		break;
	}
	case M_LEAVE: {
		int slot = (int)bb_read(&b, 8);
		if (host || slot >= DC_MAX_PLAYERS) return;
		remove_player(c, slot);
		recompute_balance(c);
		break;
	}
	case M_LEVEL: {
		if (host) return;
		c->level_seq = bb_read(&b, 32);
		c->run_seed = bb_read(&b, 32);
		break;
	}
	case M_HIT_MOB: {
		int id = (int)bb_read_varu(&b), dmg = (int)bb_read_varu(&b);
		coop_mob *m = mob_by_id(c, id);
		if (!host || b.overflow || !m || m->dead || dmg <= 0) return;
		m->pending_damage += dmg;
		threat_add(&m->threat, from, (float)dmg);
		break;
	}
	case M_HIT_PLAYER: {
		int target = (int)bb_read(&b, 8), attacker = (int)bb_read(&b, 8), dmg = (int)bb_read_varu(&b);
		if (b.overflow || dmg <= 0 || target >= DC_MAX_PLAYERS) return;
		if (host && attacker != ENEMY_SLOT) {
			/* validate pvp on the host, which owns the authoritative pvp state */
			attacker = from;
			dmg = pvp_resolve(&c->pvp, attacker, target, dmg, c->now);
			if (!dmg) return;
		}
		apply_hit_player(c, target, attacker, dmg);
		break;
	}
	case M_MOB_DEAD: {
		coop_mob *m = mob_by_id(c, (int)bb_read_varu(&b));
		if (host || !m || m->dead) return;
		m->dead = 1;
		m->pending_damage = 1 << 28; /* let the game play its own death */
		break;
	}
	case M_PVP: {
		if (host) {
			pvp_toggle(&c->pvp, from);
			uint8_t out[4] = { M_PVP, (uint8_t)from, c->pvp.enabled[from], c->pvp.team[from] };
			send_rel(c, -1, out, 4);
			log_note(c, c->pvp.enabled[from] ? "Player %d enabled PvP%s" : "Player %d disabled PvP%s", from, NULL);
		} else if (len >= 4) {
			int slot = msg[1];
			if (slot < DC_MAX_PLAYERS) { c->pvp.enabled[slot] = msg[2]; c->pvp.team[slot] = msg[3]; }
		}
		break;
	}
	case M_LIFE: {
		int slot = (int)bb_read(&b, 8), st = (int)bb_read(&b, 3);
		if (host) slot = from;
		if (slot >= DC_MAX_PLAYERS) return;
		c->players[slot].life.state = (uint8_t)st;
		if (host) {
			uint8_t out[4];
			bitbuf o;
			bb_init(&o, out, sizeof out);
			bb_write(&o, M_LIFE, 8);
			bb_write(&o, (uint32_t)slot, 8);
			bb_write(&o, (uint32_t)st, 3);
			for (int i = 1; i < DC_MAX_PLAYERS; i++)
				if (i != slot && c->players[i].present) send_rel(c, i, out, bb_bytes(&o));
		}
		break;
	}
	case M_AWARD: {
		int cells = (int)bb_read_varu(&b), gold = (int)bb_read_varu(&b);
		if (b.overflow) return;
		if (host) { /* relay to everyone except the picker */
			for (int i = 1; i < DC_MAX_PLAYERS; i++)
				if (i != from && c->players[i].present) send_rel(c, i, msg, len);
		}
		if (c->ad.award) c->ad.award(c->ad.ud, cells, gold);
		break;
	}
	default: break;
	}
}

static void cb_unreliable(void *ud, int from, const uint8_t *msg, int len) {
	coop_state *c = ud;
	bitbuf b;
	bb_init(&b, (void *)msg, len);
	int type = (int)bb_read(&b, 8);
	if (type == U_PLAYER && coop_is_host(c)) {
		double t_req;
		bb_read_bytes(&b, &t_req, 8);
		ent_state s;
		memset(&s, 0, sizeof s);
		r_state(&b, &s);
		if (b.overflow || !c->players[from].present) return;
		/* stamp with host time minus one-way latency estimate */
		s.t = c->now - dc_rtt(&c->sess, from) * 0.5;
		c->players[from].last = s;
		c->players[from].echo_t = t_req;
		interp_push(&c->players[from].track, &s);
	} else if (type == U_SNAPSHOT && !coop_is_host(c)) {
		double host_t, echo;
		bb_read_bytes(&b, &host_t, 8);
		bb_read_bytes(&b, &echo, 8);
		if (b.overflow) return;
		if (echo > 0) clk_sample(&c->clock, echo, host_t, c->now);
		c->snaps_in++;
		int np = (int)bb_read(&b, 4);
		for (int i = 0; i < np; i++) {
			int slot = (int)bb_read(&b, 4);
			ent_state s;
			r_state(&b, &s);
			if (b.overflow || slot >= DC_MAX_PLAYERS) return;
			s.t = host_t;
			if (slot == c->my_slot) continue;
			c->players[slot].last = s;
			interp_push(&c->players[slot].track, &s);
		}
		int nm = (int)bb_read_varu(&b);
		for (int i = 0; i < nm; i++) {
			int id = (int)bb_read_varu(&b);
			ent_state s;
			r_state(&b, &s);
			if (b.overflow) return;
			s.t = host_t;
			coop_mob *m = mob_by_id(c, id);
			if (m) interp_push(&m->track, &s);
		}
	}
}

/* ---------------- lifecycle ---------------- */

void coop_init(coop_state *c, const dc_config *cfg, const coop_adapter *ad) {
	memset(c, 0, sizeof *c);
	c->cfg = *cfg;
	if (ad) c->ad = *ad;
	dc_session_init(&c->sess);
	pvp_init(&c->pvp);
	c->pvp.mode = cfg->pvp_mode;
	c->pvp.damage_scale = cfg->pvp_damage;
	clk_init(&c->clock);
	c->my_slot = 0;
	add_player(c, 0, cfg->name);
	balance_compute(&c->cfg.balance, 1, &c->bal);
}

static dc_callbacks make_cb(coop_state *c) {
	dc_callbacks cb = { cb_join, cb_leave, cb_reliable, cb_unreliable, cb_connected, cb_failed, c };
	return cb;
}

int coop_host(coop_state *c) {
	dc_callbacks cb = make_cb(c);
	if (net_init() || dc_session_host(&c->sess, (uint16_t)c->cfg.port, c->cfg.name, &cb)) return -1;
	c->my_slot = 0;
	c->running = 1;
	return 0;
}

int coop_join(coop_state *c, const char *addr) {
	netaddr a;
	if (netaddr_parse(addr, (uint16_t)c->cfg.port, &a)) return -1;
	dc_callbacks cb = make_cb(c);
	if (net_init() || dc_session_join(&c->sess, &a, c->cfg.name, &cb)) return -1;
	memset(&c->players[0], 0, sizeof c->players[0]); /* slot 0 is the host now */
	c->running = 1;
	return 0;
}

void coop_shutdown(coop_state *c) {
	if (c->running) dc_session_close(&c->sess);
	c->running = 0;
}

int coop_is_host(const coop_state *c) { return c->sess.state == SESS_HOSTING; }
int coop_active(const coop_state *c) { return c->running && (c->sess.state == SESS_HOSTING || c->sess.state == SESS_CONNECTED); }

int coop_player_count(const coop_state *c) {
	int n = 0;
	for (int i = 0; i < DC_MAX_PLAYERS; i++) n += c->players[i].present;
	return n ? n : 1;
}

void coop_set_local_hero(coop_state *c, void *hero) { c->local_hero = hero; }

int coop_classify(const coop_state *c, void *ent, int *out) {
	if (!ent) return ENT_NONE;
	if (ent == c->local_hero) { *out = c->my_slot; return ENT_LOCAL_HERO; }
	for (int i = 0; i < DC_MAX_PLAYERS; i++)
		if (c->players[i].ghost == ent) { *out = i; return ENT_GHOST; }
	coop_mob *m = mob_by_ent((coop_state *)c, ent);
	if (m) { *out = m->id; return ENT_MOB; }
	return ENT_NONE;
}

void coop_level_start(coop_state *c, uint32_t seed) {
	for (int i = 0; i < COOP_MAX_MOBS; i++) c->mobs[i].active = 0;
	c->nmobs_spawned = 0;
	for (int i = 0; i < DC_MAX_PLAYERS; i++) {
		coop_player *p = &c->players[i];
		life_new_level(&p->life);
		p->ghost = NULL; /* the old level disposed its entities */
		interp_clear(&p->track);
	}
	c->local_hero = NULL;
	if (coop_is_host(c)) {
		c->level_seq++;
		c->run_seed = seed;
		uint8_t m[16];
		bitbuf b;
		bb_init(&b, m, sizeof m);
		bb_write(&b, M_LEVEL, 8);
		bb_write(&b, c->level_seq, 32);
		bb_write(&b, seed, 32);
		send_rel(c, -1, m, bb_bytes(&b));
	}
}

float coop_register_mob(coop_state *c, void *ent, int is_boss, int is_elite) {
	if (mob_by_ent(c, ent)) return 1.f;
	/* both sides generate the level from the same seed, so spawn order matches */
	int id = c->nmobs_spawned++;
	if (id >= COOP_MAX_MOBS) return 1.f;
	coop_mob *m = &c->mobs[id];
	memset(m, 0, sizeof *m);
	m->ent = ent;
	m->id = (uint16_t)id;
	m->active = 1;
	m->boss = (uint8_t)is_boss;
	m->elite = (uint8_t)is_elite;
	threat_init(&m->threat);
	m->hp_mult = is_boss ? c->bal.boss_hp : is_elite ? c->bal.elite_hp : c->bal.mob_hp;
	/* the caller applies hp_mult to the game's unscaled max life */
	if (c->ad.max_life) m->base_max_life = c->ad.max_life(c->ad.ud, ent);
	return m->hp_mult;
}

void coop_unregister_entity(coop_state *c, void *ent) {
	coop_mob *m = mob_by_ent(c, ent);
	if (m) {
		if (coop_is_host(c) && !m->dead && coop_active(c)) {
			uint8_t out[8];
			bitbuf b;
			bb_init(&b, out, sizeof out);
			bb_write(&b, M_MOB_DEAD, 8);
			bb_write_varu(&b, m->id);
			send_rel(c, -1, out, bb_bytes(&b));
		}
		m->active = 0;
	}
	if (ent == c->local_hero) c->local_hero = NULL;
	for (int i = 0; i < DC_MAX_PLAYERS; i++)
		if (c->players[i].ghost == ent) c->players[i].ghost = NULL;
}

int coop_mob_skip_update(coop_state *c, void *ent) {
	if (!coop_active(c) || coop_is_host(c)) return 0;
	return mob_by_ent(c, ent) != NULL;
}

int coop_hero_skip_update(coop_state *c, void *ent) {
	int slot;
	if (coop_classify(c, ent, &slot) == ENT_GHOST) return 1;
	if (!c->local_hero && coop_classify(c, ent, &slot) == ENT_NONE) c->local_hero = ent;
	/* downed heroes can't act; the game still renders them */
	return ent == c->local_hero && c->players[c->my_slot].life.state == LIFE_DOWNED;
}

static int others_alive(const coop_state *c) {
	int n = 0;
	for (int i = 0; i < DC_MAX_PLAYERS; i++)
		if (i != c->my_slot && c->players[i].present && c->players[i].life.state == LIFE_ALIVE) n++;
	return n;
}

static void broadcast_life(coop_state *c) {
	uint8_t m[4];
	bitbuf b;
	bb_init(&b, m, sizeof m);
	bb_write(&b, M_LIFE, 8);
	bb_write(&b, (uint32_t)c->my_slot, 8);
	bb_write(&b, c->players[c->my_slot].life.state, 3);
	send_rel(c, -1, m, bb_bytes(&b));
}

int coop_on_damage(coop_state *c, void *target, void *source, int dmg) {
	if (!coop_active(c) || dmg <= 0) return dmg;
	int ts = -1, ss = -1;
	int tk = coop_classify(c, target, &ts), sk = coop_classify(c, source, &ss);
	int host = coop_is_host(c);

	if (tk == ENT_LOCAL_HERO) {
		if (sk == ENT_MOB) dmg = (int)lroundf((float)dmg * c->bal.mob_dmg);
		else if (sk == ENT_GHOST) return 0; /* pvp arrives as M_HIT_PLAYER, not local collisions */
		if (c->cfg.revive) {
			ent_state s;
			c->ad.read(c->ad.ud, target, &s);
			if (dmg >= s.life && life_on_lethal(&c->players[c->my_slot].life, others_alive(c))) {
				broadcast_life(c);
				return s.life > 1 ? s.life - 1 : 0; /* stay at 1 hp while downed */
			}
		}
		return dmg;
	}
	if (tk == ENT_GHOST) {
		/* the owner applies it; only the host forwards enemy hits */
		if (sk == ENT_MOB && host) apply_hit_player(c, ts, ENEMY_SLOT, (int)lroundf((float)dmg * c->bal.mob_dmg));
		if (sk == ENT_LOCAL_HERO) {
			int d = pvp_allowed(&c->pvp, c->my_slot, ts, c->now) ? dmg : 0;
			if (d && host) apply_hit_player(c, ts, c->my_slot, pvp_resolve(&c->pvp, c->my_slot, ts, d, c->now));
			else if (d) {
				uint8_t m[8];
				bitbuf b;
				bb_init(&b, m, sizeof m);
				bb_write(&b, M_HIT_PLAYER, 8);
				bb_write(&b, (uint32_t)ts, 8);
				bb_write(&b, (uint32_t)c->my_slot, 8);
				bb_write_varu(&b, (uint32_t)d);
				send_rel(c, 0, m, bb_bytes(&b));
			}
		}
		return 0;
	}
	if (tk == ENT_MOB) {
		coop_mob *m = mob_by_id(c, ts);
		if (sk == ENT_GHOST) return 0; /* remote players' hits come as messages */
		if (host) {
			if (sk == ENT_LOCAL_HERO) threat_add(&m->threat, c->my_slot, (float)dmg);
			return dmg;
		}
		if (sk == ENT_LOCAL_HERO || sk == ENT_NONE) {
			uint8_t out[12];
			bitbuf b;
			bb_init(&b, out, sizeof out);
			bb_write(&b, M_HIT_MOB, 8);
			bb_write_varu(&b, m->id);
			bb_write_varu(&b, (uint32_t)dmg);
			send_rel(c, 0, out, bb_bytes(&b));
		}
		return dmg; /* local prediction; host snapshots correct hp */
	}
	return dmg;
}

int coop_take_pending_damage(coop_state *c, void *ent) {
	int slot, d = 0;
	int k = coop_classify(c, ent, &slot);
	if (k == ENT_LOCAL_HERO) { d = c->players[slot].pending_damage; c->players[slot].pending_damage = 0; }
	else if (k == ENT_MOB) { coop_mob *m = mob_by_id(c, slot); d = m->pending_damage; m->pending_damage = 0; }
	return d;
}

void *coop_pick_target(coop_state *c, void *mob) {
	if (!coop_active(c) || !coop_is_host(c)) return NULL;
	coop_mob *m = mob_by_ent(c, mob);
	if (!m) return NULL;
	threat_player pl[DC_MAX_PLAYERS];
	int load[DC_MAX_PLAYERS] = { 0 }, nm = 0, np = 0;
	memset(pl, 0, sizeof pl);
	for (int i = 0; i < COOP_MAX_MOBS; i++) {
		if (!c->mobs[i].active || c->mobs[i].dead) continue;
		nm++;
		int t = c->mobs[i].threat.target;
		if (t >= 0) load[t]++;
	}
	ent_state ms;
	c->ad.read(c->ad.ud, mob, &ms);
	for (int i = 0; i < DC_MAX_PLAYERS; i++) {
		coop_player *p = &c->players[i];
		void *e = i == c->my_slot ? c->local_hero : p->ghost;
		if (!p->present || !e) continue;
		ent_state s;
		c->ad.read(c->ad.ud, e, &s);
		pl[i].x = s.x; pl[i].y = s.y;
		pl[i].present = 1;
		pl[i].downed = p->life.state != LIFE_ALIVE;
		pl[i].taunt = 1.f;
		np++;
	}
	if (np == 0) return NULL;
	int fair = (nm + np - 1) / np;
	int t = threat_pick(&m->threat, ms.x, ms.y, pl, DC_MAX_PLAYERS, load, fair, 24.f, c->now);
	if (t < 0) return NULL;
	return t == c->my_slot ? c->local_hero : c->players[t].ghost;
}

void coop_toggle_pvp(coop_state *c) {
	if (coop_is_host(c) || !coop_active(c)) {
		pvp_toggle(&c->pvp, c->my_slot);
		if (coop_active(c)) {
			uint8_t out[4] = { M_PVP, (uint8_t)c->my_slot, c->pvp.enabled[c->my_slot], c->pvp.team[c->my_slot] };
			send_rel(c, -1, out, 4);
		}
		log_note(c, c->pvp.enabled[c->my_slot] ? "PvP enabled (slot %d)%s" : "PvP disabled (slot %d)%s", c->my_slot, NULL);
	} else {
		uint8_t out[1] = { M_PVP };
		send_rel(c, 0, out, 1);
	}
}

void coop_award(coop_state *c, int cells, int gold) {
	if (!coop_active(c) || !c->cfg.shared_cells || (cells <= 0 && gold <= 0)) return;
	uint8_t m[16];
	bitbuf b;
	bb_init(&b, m, sizeof m);
	bb_write(&b, M_AWARD, 8);
	bb_write_varu(&b, (uint32_t)(cells > 0 ? cells : 0));
	bb_write_varu(&b, (uint32_t)(gold > 0 ? gold : 0));
	send_rel(c, -1, m, bb_bytes(&b));
}

/* ---------------- per-frame ---------------- */

static void send_client_state(coop_state *c) {
	if (!c->local_hero) return;
	uint8_t m[128];
	bitbuf b;
	bb_init(&b, m, sizeof m);
	bb_write(&b, U_PLAYER, 8);
	bb_write_bytes(&b, &c->now, 8);
	ent_state s;
	c->ad.read(c->ad.ud, c->local_hero, &s);
	s.flags = (uint8_t)((s.flags & ~3u) | (c->players[c->my_slot].life.state & 3u));
	w_state(&b, &s);
	dc_set_unreliable(&c->sess, 0, m, bb_bytes(&b));
}

static void send_snapshots(coop_state *c) {
	ent_state host_hero;
	int have_host = c->local_hero != NULL;
	if (have_host) {
		c->ad.read(c->ad.ud, c->local_hero, &host_hero);
		host_hero.flags = (uint8_t)((host_hero.flags & ~3u) | (c->players[0].life.state & 3u));
	}
	float dt = 1.f / (float)c->cfg.tick_rate;
	for (int to = 1; to < DC_MAX_PLAYERS; to++) {
		if (!c->players[to].present || !c->sess.peers[to].active) continue;
		uint8_t m[SNAP_BUDGET + 64];
		bitbuf b;
		bb_init(&b, m, SNAP_BUDGET);
		bb_write(&b, U_SNAPSHOT, 8);
		bb_write_bytes(&b, &c->now, 8);
		double echo = c->players[to].echo_t;
		bb_write_bytes(&b, &echo, 8);
		int np = 0;
		for (int i = 0; i < DC_MAX_PLAYERS; i++)
			if (i != to && c->players[i].present && (i == 0 ? have_host : c->players[i].track.count > 0)) np++;
		bb_write(&b, (uint32_t)np, 4);
		for (int i = 0; i < DC_MAX_PLAYERS; i++) {
			if (i == to || !c->players[i].present) continue;
			if (i == 0 ? !have_host : c->players[i].track.count == 0) continue;
			bb_write(&b, (uint32_t)i, 4);
			const ent_state *st = i == 0 ? &host_hero : interp_latest(&c->players[i].track);
			w_state(&b, st);
		}
		/* enemies by priority: grows each tick, faster when near the receiver */
		const ent_state *rcv = interp_latest(&c->players[to].track);
		int order[COOP_MAX_MOBS], n = 0;
		for (int i = 0; i < COOP_MAX_MOBS; i++) {
			coop_mob *mb = &c->mobs[i];
			if (!mb->active || mb->dead) continue;
			ent_state s;
			c->ad.read(c->ad.ud, mb->ent, &s);
			float w = 1.f;
			if (rcv) {
				float dx = s.x - rcv->x, dy = s.y - rcv->y, d = sqrtf(dx * dx + dy * dy);
				w = d < 30.f ? 4.f : d < 80.f ? 1.f : 0.25f;
			}
			mb->prio[to] += w * dt * 30.f;
			order[n++] = i;
		}
		for (int i = 1; i < n; i++) { /* insertion sort by descending priority */
			int v = order[i], j = i - 1;
			while (j >= 0 && c->mobs[order[j]].prio[to] < c->mobs[v].prio[to]) { order[j + 1] = order[j]; j--; }
			order[j + 1] = v;
		}
		/* reserve the count field: write mobs into a side buffer first */
		uint8_t mb_buf[SNAP_BUDGET];
		bitbuf mbb;
		int room = bb_bits_left(&b) / 8 - 3;
		if (room < 0) room = 0;
		bb_init(&mbb, mb_buf, room);
		int written = 0;
		for (int k = 0; k < n; k++) {
			coop_mob *mb = &c->mobs[order[k]];
			ent_state s;
			c->ad.read(c->ad.ud, mb->ent, &s);
			int save = mbb.pos;
			bb_write_varu(&mbb, mb->id);
			w_state(&mbb, &s);
			if (mbb.overflow) { mbb.pos = save; mbb.overflow = 0; break; }
			mb->prio[to] = 0.f;
			written++;
		}
		bb_write_varu(&b, (uint32_t)written);
		for (int bit = 0; bit < mbb.pos; bit += 8) {
			int nb = mbb.pos - bit < 8 ? mbb.pos - bit : 8;
			bb_write(&b, (mb_buf[bit >> 3]) & ((1u << nb) - 1), nb);
		}
		if (!b.overflow) {
			dc_set_unreliable(&c->sess, to, m, bb_bytes(&b));
			c->bytes_out += (uint32_t)bb_bytes(&b);
		}
	}
}

static void drive_remote(coop_state *c) {
	double render_t;
	if (coop_is_host(c)) render_t = c->now - interp_delay(1.0 / c->cfg.tick_rate, 0.005);
	else render_t = clk_host_time(&c->clock, c->now) - interp_delay(1.0 / c->cfg.tick_rate, c->clock.jitter);
	for (int i = 0; i < DC_MAX_PLAYERS; i++) {
		coop_player *p = &c->players[i];
		if (i == c->my_slot || !p->present || p->track.count == 0) continue;
		ent_state s;
		if (!interp_sample(&p->track, render_t, &s)) continue;
		if (!p->ghost && c->ad.spawn_ghost && c->local_hero) p->ghost = c->ad.spawn_ghost(c->ad.ud, i, &s);
		if (p->ghost && c->ad.write) c->ad.write(c->ad.ud, p->ghost, &s);
		if (coop_is_host(c)) p->life.state = (uint8_t)(s.flags & 3u);
	}
	if (coop_is_host(c)) return;
	for (int i = 0; i < COOP_MAX_MOBS; i++) {
		coop_mob *m = &c->mobs[i];
		if (!m->active || m->dead || m->track.count == 0) continue;
		ent_state s;
		if (interp_sample(&m->track, render_t, &s) && c->ad.write) c->ad.write(c->ad.ud, m->ent, &s);
	}
}

static void revive_tick(coop_state *c, float dt) {
	coop_player *me = &c->players[c->my_slot];
	if (me->life.state != LIFE_DOWNED || !c->local_hero) return;
	ent_state mine;
	c->ad.read(c->ad.ud, c->local_hero, &mine);
	int revivers = 0;
	for (int i = 0; i < DC_MAX_PLAYERS; i++) {
		coop_player *p = &c->players[i];
		if (i == c->my_slot || !p->present || p->life.state != LIFE_ALIVE || p->track.count == 0) continue;
		const ent_state *s = &p->last;
		float dx = s->x - mine.x, dy = s->y - mine.y;
		/* allies within 2.5 cells that hold still count as reviving */
		if (dx * dx + dy * dy < 2.5f * 2.5f && fabsf(s->vx) < 0.5f) revivers++;
	}
	uint8_t before = me->life.state;
	life_update(&me->life, dt, revivers);
	if (life_take_revived(&me->life)) {
		if (c->ad.max_life && c->ad.set_max_life) {
			int maxl = c->ad.max_life(c->ad.ud, c->local_hero);
			int hp = (int)lroundf((float)maxl * c->bal.revive_hp);
			c->ad.set_max_life(c->ad.ud, c->local_hero, hp < 1 ? 1 : hp, maxl);
		}
		log_note(c, "Revived (slot %d)%s", c->my_slot, NULL);
		broadcast_life(c);
	} else if (before != me->life.state) {
		broadcast_life(c);
	}
}

void coop_frame(coop_state *c, double now) {
	float dt = c->last_frame > 0 ? (float)(now - c->last_frame) : 0.f;
	if (dt > 0.25f) dt = 0.25f;
	c->last_frame = now;
	c->now = now;
	if (!c->running) return;
	dc_session_update(&c->sess, now);
	clk_update(&c->clock, dt);
	if (!coop_active(c)) return;
	for (int i = 0; i < COOP_MAX_MOBS; i++)
		if (c->mobs[i].active) threat_decay(&c->mobs[i].threat, dt, 4.f);
	if (c->cfg.revive) revive_tick(c, dt);
	drive_remote(c);
	if (now - c->last_send >= 1.0 / c->cfg.tick_rate) {
		c->last_send = now;
		if (coop_is_host(c)) send_snapshots(c);
		else send_client_state(c);
		dc_session_flush(&c->sess);
	}
}
