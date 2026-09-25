/*
 * dccoop.hdll: natives called from the patched game bytecode, plus the
 * adapter that lets the game-agnostic co-op core read and drive HashLink
 * entities by field name.
 */
#include "hl_min.h"

#include "../core/coop.h"

#include <math.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#if defined(_WIN32)
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#endif

#define GAME_FPS 60.0 /* entity dx/dy are per-frame in the engine */

typedef struct {
	int cx, cy, xr, yr, dx, dy, dir, life, maxlife;
} field_hashes;

static coop_state *S;
static field_hashes H;
static vclosure *g_spawner;
static vdynamic *g_game;
static void *g_drain_target; /* entity currently receiving forwarded damage */
static FILE *g_log;
static int g_boot_state; /* 0 = not started, 1 = discovering, 2 = running/solo */
static double g_boot_until;
#if defined(_WIN32)
static int g_pvp_key_down;
#endif

/* ---------------- logging ---------------- */

static void dlog(const char *fmt, ...) {
	char buf[512];
	va_list ap;
	va_start(ap, fmt);
	vsnprintf(buf, sizeof buf, fmt, ap);
	va_end(ap);
	if (!g_log) g_log = fopen("dccoop.log", "a");
	if (g_log) { fprintf(g_log, "%s\n", buf); fflush(g_log); }
	if (getenv("DCCOOP_STDOUT")) { printf("[dccoop] %s\n", buf); fflush(stdout); }
}

/* ---------------- adapter over HL objects ---------------- */

static int has(vdynamic *o, int h) { return o && hl_obj_has_field(o, h); }

static void ad_read(void *ud, void *ent, ent_state *s) {
	(void)ud;
	vdynamic *o = ent;
	memset(s, 0, sizeof *s);
	if (!o) return;
	s->x = (float)(hl_dyn_geti(o, H.cx, &hlt_i32) + (has(o, H.xr) ? hl_dyn_getd(o, H.xr) : 0.5));
	s->y = (float)(hl_dyn_geti(o, H.cy, &hlt_i32) + (has(o, H.yr) ? hl_dyn_getd(o, H.yr) : 1.0));
	if (has(o, H.dx)) s->vx = (float)(hl_dyn_getd(o, H.dx) * GAME_FPS);
	if (has(o, H.dy)) s->vy = (float)(hl_dyn_getd(o, H.dy) * GAME_FPS);
	if (has(o, H.life)) s->life = hl_dyn_geti(o, H.life, &hlt_i32);
	if (has(o, H.dir)) s->dir = hl_dyn_geti(o, H.dir, &hlt_i32) < 0;
}

static void ad_write(void *ud, void *ent, const ent_state *s) {
	(void)ud;
	vdynamic *o = ent;
	if (!o) return;
	double fx = floor(s->x), fy = floor(s->y);
	hl_dyn_seti(o, H.cx, &hlt_i32, (int)fx);
	hl_dyn_seti(o, H.cy, &hlt_i32, (int)fy);
	if (has(o, H.xr)) hl_dyn_setd(o, H.xr, s->x - fx);
	if (has(o, H.yr)) hl_dyn_setd(o, H.yr, s->y - fy);
	/* velocity is kept for animation state, but ghosts never integrate it */
	if (has(o, H.dx)) hl_dyn_setd(o, H.dx, s->vx / GAME_FPS);
	if (has(o, H.dy)) hl_dyn_setd(o, H.dy, s->vy / GAME_FPS);
	if (has(o, H.life)) hl_dyn_seti(o, H.life, &hlt_i32, s->life);
	if (has(o, H.dir)) hl_dyn_seti(o, H.dir, &hlt_i32, s->dir ? -1 : 1);
}

static vdynamic *call_method(vdynamic *o, const char *name, vdynamic **args, int nargs) {
	if (!o) return NULL;
	/* methods resolve to bound closures; unknown names return NULL */
	vclosure *m = hl_dyn_getp(o, hl_hash_utf8(name), &hlt_dyn);
	return m ? hl_dyn_call(m, args, nargs) : NULL;
}

static void *ad_spawn(void *ud, int slot, const ent_state *at) {
	(void)ud;
	if (!g_spawner) return NULL;
	vdynamic *hero = hl_dyn_call(g_spawner, NULL, 0);
	if (!hero) return NULL;
	ad_write(NULL, hero, at);
	dlog("spawned ghost for slot %d", slot);
	return hero;
}

static void ad_despawn(void *ud, void *ent) {
	(void)ud;
	if (!ent) return;
	coop_unregister_entity(S, ent);
	call_method(ent, "destroy", NULL, 0);
}

static void ad_damage(void *ud, void *ent, int amount) {
	(void)ud; (void)ent; (void)amount; /* damage is drained in dc_tick */
}

static void ad_set_max_life(void *ud, void *ent, int life, int maxl) {
	(void)ud;
	vdynamic *o = ent;
	if (has(o, H.maxlife)) hl_dyn_seti(o, H.maxlife, &hlt_i32, maxl);
	if (has(o, H.life)) hl_dyn_seti(o, H.life, &hlt_i32, life);
}

static int ad_max_life(void *ud, void *ent) {
	(void)ud;
	vdynamic *o = ent;
	return has(o, H.maxlife) ? hl_dyn_geti(o, H.maxlife, &hlt_i32) : 1;
}

static void ad_award(void *ud, int cells, int gold) {
	(void)ud;
	dlog("award cells=%d gold=%d", cells, gold);
	if (!g_game) return;
	vdynamic *hero = S->local_hero;
	int hc = hl_hash_utf8("cells"), hg = hl_hash_utf8("gold");
	if (has(hero, hc)) hl_dyn_seti(hero, hc, &hlt_i32, hl_dyn_geti(hero, hc, &hlt_i32) + cells);
	if (has(hero, hg)) hl_dyn_seti(hero, hg, &hlt_i32, hl_dyn_geti(hero, hg, &hlt_i32) + gold);
}

static void ad_notify(void *ud, const char *text) {
	(void)ud;
	dlog("%s", text);
}

/* ---------------- boot ---------------- */

static void boot(void) {
	static dc_config cfg;
	config_defaults(&cfg);
	config_load(&cfg, "dccoop.ini");
	const char *e;
	if ((e = getenv("DCCOOP_MODE"))) cfg.mode = !strcmp(e, "host") ? 1 : !strcmp(e, "join") ? 2 : !strcmp(e, "off") ? 0 : 3;
	if ((e = getenv("DCCOOP_JOIN"))) snprintf(cfg.join_addr, sizeof cfg.join_addr, "%s", e);
	if ((e = getenv("DCCOOP_NAME"))) snprintf(cfg.name, sizeof cfg.name, "%s", e);
	if ((e = getenv("DCCOOP_PORT"))) cfg.port = atoi(e);
	H.cx = hl_hash_utf8(cfg.f_cx); H.cy = hl_hash_utf8(cfg.f_cy);
	H.xr = hl_hash_utf8(cfg.f_xr); H.yr = hl_hash_utf8(cfg.f_yr);
	H.dx = hl_hash_utf8(cfg.f_dx); H.dy = hl_hash_utf8(cfg.f_dy);
	H.dir = hl_hash_utf8(cfg.f_dir);
	H.life = hl_hash_utf8(cfg.f_life); H.maxlife = hl_hash_utf8(cfg.f_maxlife);
	coop_adapter ad = { ad_read, ad_write, ad_spawn, ad_despawn, ad_damage, ad_set_max_life, ad_max_life, ad_award,
		ad_notify, NULL };
	S = calloc(1, sizeof *S);
	coop_init(S, &cfg, &ad);
	dlog("DC-Coop loaded: name=%s mode=%d port=%d", cfg.name, cfg.mode, cfg.port);
	g_boot_state = 2;
	if (cfg.mode == 1) {
		if (coop_host(S)) dlog("host failed on port %d", cfg.port);
		else dlog("hosting on port %d", cfg.port);
	} else if (cfg.mode == 2) {
		if (coop_join(S, cfg.join_addr)) dlog("bad join address '%s'", cfg.join_addr);
		else dlog("joining %s", cfg.join_addr);
	} else if (cfg.mode == 3) {
		/* auto: look for a LAN game briefly, otherwise host one */
		net_init();
		dc_discover_open(&S->sess);
		dc_discover_ping(&S->sess);
		g_boot_state = 1;
		g_boot_until = net_time() + 1.5;
	}
}

static void boot_discovery(double now) {
	dc_session_update(&S->sess, now);
	if (S->sess.nlan > 0) {
		char addr[32];
		netaddr_str(&S->sess.lan[0].addr, addr, sizeof addr);
		dlog("found LAN game '%s' at %s", S->sess.lan[0].name, addr);
		udp_close(&S->sess.sock);
		coop_join(S, addr);
		g_boot_state = 2;
	} else if (now >= g_boot_until) {
		udp_close(&S->sess.sock);
		dlog(coop_host(S) ? "auto-host failed" : "no LAN game found, hosting");
		g_boot_state = 2;
	}
}

/* ---------------- natives ---------------- */

static void dc_tick(vdynamic *game) {
	if (!g_boot_state) boot();
	g_game = game;
	double now = net_time();
	if (g_boot_state == 1) { boot_discovery(now); return; }
#if defined(_WIN32)
	int down = (GetAsyncKeyState(S->cfg.friendly_fire_key) & 0x8000) != 0;
	if (down && !g_pvp_key_down) coop_toggle_pvp(S);
	g_pvp_key_down = down;
#endif
	coop_frame(S, now);
	/* forwarded hits go through the game's own damage method */
	vdynamic *targets[COOP_MAX_MOBS + 1];
	int nt = 0;
	if (S->local_hero) targets[nt++] = S->local_hero;
	for (int i = 0; i < COOP_MAX_MOBS; i++)
		if (S->mobs[i].active && S->mobs[i].pending_damage) targets[nt++] = S->mobs[i].ent;
	for (int i = 0; i < nt; i++) {
		int d = coop_take_pending_damage(S, targets[i]);
		if (d <= 0) continue;
		int amount = d;
		vdynamic *args[2] = { NULL, hl_make_dyn(&amount, &hlt_i32) };
		g_drain_target = targets[i];
		call_method(targets[i], "hit", args, 2);
		g_drain_target = NULL;
	}
}
DEFINE_NATIVE(tick)

static void dc_level_start(vdynamic *game) {
	if (!g_boot_state) boot();
	g_game = game;
	coop_level_start(S, S->run_seed ? S->run_seed : 1);
	dlog("level start (seq %u)", S->level_seq);
}
DEFINE_NATIVE(level_start)

static bool dc_hero_update(vdynamic *hero) {
	if (!g_boot_state) boot();
	return coop_hero_skip_update(S, hero) != 0;
}
DEFINE_NATIVE(hero_update)

static bool dc_mob_update(vdynamic *mob) {
	if (!S) return false;
	return coop_mob_skip_update(S, mob) != 0;
}
DEFINE_NATIVE(mob_update)

static void dc_mob_init(vdynamic *mob) {
	if (!g_boot_state) boot();
	int maxl = ad_max_life(NULL, mob);
	float mult = coop_register_mob(S, mob, 0, 0);
	if (mult != 1.f && maxl > 0) {
		int nm = (int)lroundf((float)maxl * mult);
		ad_set_max_life(NULL, mob, nm, nm);
	}
}
DEFINE_NATIVE(mob_init)

static int dc_damage(vdynamic *self, vdynamic *from, int dmg) {
	if (!S) return dmg;
	if (getenv("DCCOOP_TRACE")) dlog("damage self=%p from=%p dmg=%d drain=%p", (void *)self, (void *)from, dmg, g_drain_target);
	if (self == g_drain_target) { g_drain_target = NULL; return dmg; }
	return coop_on_damage(S, self, from, dmg);
}
DEFINE_NATIVE(damage)

static void dc_dispose(vdynamic *ent) {
	if (S) coop_unregister_entity(S, ent);
}
DEFINE_NATIVE(dispose)

static void dc_set_spawner(vclosure *c) {
	if (g_spawner) hl_remove_root(&g_spawner);
	g_spawner = c;
	hl_add_root(&g_spawner); /* keep the closure alive across GCs */
	dlog("ghost spawner registered");
}
DEFINE_NATIVE(set_spawner)

static void dc_ghost_created(vdynamic *hero) {
	(void)hero; /* registered by coop when ad_spawn returns */
}
DEFINE_NATIVE(ghost_created)

/* Test/diagnostic hook: lets scripts query co-op state. */
HLEXPORT coop_state *dccoop_state(void) { return S; }
