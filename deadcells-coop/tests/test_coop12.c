/*
 * 1 host + 11 clients over real localhost UDP with 10% simulated loss.
 * Each instance has a fake game world implementing coop_adapter.
 * Verifies joining, a 13th player being refused, ghost tracking accuracy,
 * enemy replication, client hits reaching the host, balance scaling,
 * pvp consent, and downed/revive flow.
 */
#include "../src/core/coop.h"
#include "check.h"

#include <stdlib.h>
#include <string.h>

#define N 12
#define NMOBS 40

typedef struct { ent_state s; int max_life; int is_ghost; int slot; int queued_damage; } fake_ent;

typedef struct {
	fake_ent hero;
	fake_ent mobs[NMOBS];
	fake_ent ghosts[N];
	int cells;
} fake_world;

static fake_world W[N + 1];
static coop_state *C[N + 1];

static void f_read(void *ud, void *e, ent_state *out) { (void)ud; *out = ((fake_ent *)e)->s; }
static void f_write(void *ud, void *e, const ent_state *st) {
	(void)ud;
	fake_ent *f = e;
	f->s.x = st->x; f->s.y = st->y; f->s.vx = st->vx; f->s.vy = st->vy;
	f->s.life = st->life; f->s.flags = st->flags;
}
static void *f_spawn(void *ud, int slot, const ent_state *at) {
	fake_world *w = ud;
	fake_ent *g = &w->ghosts[slot];
	memset(g, 0, sizeof *g);
	g->s = *at;
	g->is_ghost = 1;
	g->slot = slot;
	return g;
}
static void f_despawn(void *ud, void *e) { (void)ud; memset(e, 0, sizeof(fake_ent)); }
static void f_damage(void *ud, void *e, int a) { (void)ud; ((fake_ent *)e)->queued_damage += a; }
static void f_setmax(void *ud, void *e, int life, int maxl) { (void)ud; ((fake_ent *)e)->s.life = life; ((fake_ent *)e)->max_life = maxl; }
static int f_maxlife(void *ud, void *e) { (void)ud; return ((fake_ent *)e)->max_life; }
static void f_award(void *ud, int cells, int gold) { (void)gold; ((fake_world *)ud)->cells += cells; }
static void f_notify(void *ud, const char *t) { (void)ud; (void)t; }

static void hero_pos(int i, double t, ent_state *s) {
	double a = t * 1.5 + i;
	s->x = (float)(100 + i * 10 + 5 * cos(a));
	s->y = (float)(50 + 3 * sin(a));
	s->vx = (float)(-5 * 1.5 * sin(a));
	s->vy = (float)(3 * 1.5 * cos(a));
}

static int park_on[N]; /* 0 = free movement, else 1 + slot to stand on */

static void step_all(int count, double t) {
	for (int i = 0; i < count; i++) {
		if (!C[i]) continue;
		if (park_on[i]) {
			ent_state *tgt = &W[park_on[i] - 1].hero.s;
			W[i].hero.s.x = tgt->x; W[i].hero.s.y = tgt->y;
			W[i].hero.s.vx = W[i].hero.s.vy = 0;
		} else if (C[i]->players[C[i]->my_slot].life.state != LIFE_DOWNED) {
			hero_pos(i, t, &W[i].hero.s);
		} else {
			W[i].hero.s.vx = W[i].hero.s.vy = 0; /* downed heroes can't move */
		}
		coop_frame(C[i], t);
		/* game loop: consume damage queued for the local hero */
		int d = coop_take_pending_damage(C[i], &W[i].hero);
		if (d) W[i].hero.s.life -= coop_on_damage(C[i], &W[i].hero, NULL, d);
	}
}

int main(void) {
	setvbuf(stdout, NULL, _IONBF, 0);
	dc_config cfg;
	config_defaults(&cfg);
	cfg.port = 47790 + (int)(net_time() * 1000) % 100;
	cfg.tick_rate = 30;
	char join[32];
	snprintf(join, sizeof join, "127.0.0.1:%d", cfg.port);

	for (int i = 0; i <= N; i++) {
		coop_adapter ad = { f_read, f_write, f_spawn, f_despawn, f_damage, f_setmax, f_maxlife, f_award, f_notify, &W[i] };
		C[i] = calloc(1, sizeof(coop_state));
		snprintf(cfg.name, sizeof cfg.name, "P%d", i);
		coop_init(C[i], &cfg, &ad);
		W[i].hero.s.life = W[i].hero.max_life = 200;
		coop_set_local_hero(C[i], &W[i].hero);
	}
	CHECK(coop_host(C[0]) == 0);
	C[0]->sess.sim_loss = 0.10f;
	/* host generates the level: enemies registered in spawn order */
	coop_level_start(C[0], 777);
	coop_set_local_hero(C[0], &W[0].hero);
	for (int m = 0; m < NMOBS; m++) {
		W[0].mobs[m].s.x = 90.f + (float)m * 3;
		W[0].mobs[m].s.y = 50;
		W[0].mobs[m].s.life = W[0].mobs[m].max_life = 100;
		coop_register_mob(C[0], &W[0].mobs[m], m == 0, 0);
	}
	double t = 1.0;
	/* join one at a time so slot numbers equal client indices */
	for (int i = 1; i < N; i++) {
		CHECK(coop_join(C[i], join) == 0);
		C[i]->sess.sim_loss = 0.10f;
		for (int k = 0; k < 120 && C[i]->sess.state != SESS_CONNECTED; k++, t += 1.0 / 60) step_all(i + 1, t);
	}
	for (int k = 0; k < 120; k++, t += 1.0 / 60) step_all(N, t);
	CHECK(coop_player_count(C[0]) == N);
	int all_joined = 1;
	for (int i = 1; i < N; i++) if (coop_player_count(C[i]) != N || C[i]->my_slot != i) {
		all_joined = 0;
		printf("  client %d: slot %d sees %d players:", i, C[i]->my_slot, coop_player_count(C[i]));
		for (int j = 0; j < N; j++) printf("%d", C[i]->players[j].present);
		printf("\n  client rel_in_next=%d have_remote=%d state=%d | host peer active=%d out base=%d next=%d tok=%llx/%llx\n",
			C[i]->sess.peers[0].ch.rel_in_next, C[i]->sess.peers[0].ch.have_remote, C[i]->sess.state,
			C[0]->sess.peers[i].active, C[0]->sess.peers[i].ch.rel_out_base, C[0]->sess.peers[i].ch.rel_out_next,
			(unsigned long long)C[0]->sess.peers[i].token, (unsigned long long)C[i]->sess.peers[0].token);
	}
	CHECK(all_joined);

	/* 13th player is refused */
	CHECK(coop_join(C[N], join) == 0);
	for (int k = 0; k < 120 && C[N]->running; k++, t += 1.0 / 60) { step_all(N, t); coop_frame(C[N], t); }
	CHECK(C[N]->sess.state == SESS_FAILED);

	/* balance scaled on host at registration time: mobs were registered solo,
	 * then rescaled as players joined */
	balance_mults m12;
	balance_compute(&cfg.balance, N, &m12);
	CHECK(W[0].mobs[5].max_life == (int)lround(100 * m12.mob_hp));
	CHECK(W[0].mobs[0].max_life == (int)lround(100 * m12.boss_hp));

	/* clients generate the same level: register their enemies in the same order */
	for (int i = 1; i < N; i++) {
		for (int m = 0; m < NMOBS; m++) {
			W[i].mobs[m].s.life = W[i].mobs[m].max_life = 100;
			coop_register_mob(C[i], &W[i].mobs[m], m == 0, 0);
		}
	}
	/* host moves enemies; run 3 seconds */
	for (int k = 0; k < 180; k++, t += 1.0 / 60) {
		for (int m = 0; m < NMOBS; m++) { W[0].mobs[m].s.x += 0.05f; W[0].mobs[m].s.vx = 3.f; }
		step_all(N, t);
	}
	/* ghost accuracy: every client sees every other player near the true
	 * position at (now - interpolation delay) */
	double worst = 0;
	int ghosts_ok = 1;
	for (int i = 0; i < N; i++) {
		for (int j = 0; j < N; j++) {
			if (i == j) continue;
			fake_ent *g = &W[i].ghosts[j];
			if (!g->is_ghost) { ghosts_ok = 0; continue; }
			double best = 1e9;
			for (double back = 0.0; back <= 0.3; back += 0.002) {
				ent_state s;
				hero_pos(j, t - 1.0 / 60 - back, &s);
				double d = hypot(s.x - g->s.x, s.y - g->s.y);
				if (d < best) best = d;
			}
			if (best > worst) worst = best;
		}
	}
	CHECK(ghosts_ok);
	printf("  worst ghost path deviation: %.4f cells\n", worst);
	CHECK(worst < 0.1);
	/* enemy replication: client copies follow the host */
	double mob_err = 0;
	for (int i = 1; i < N; i++)
		for (int m = 0; m < NMOBS; m++) {
			double d = fabs(W[i].mobs[m].s.x - W[0].mobs[m].s.x);
			if (d > mob_err) mob_err = d;
		}
	printf("  worst enemy lag distance: %.3f cells (host moves 3 cells/s)\n", mob_err);
	CHECK(mob_err < 1.2);
	CHECK(coop_mob_skip_update(C[3], &W[3].mobs[7]) == 1);
	CHECK(coop_mob_skip_update(C[0], &W[0].mobs[7]) == 0);

	/* a client hits enemy 7: forwarded to the host, which queues it for the game */
	int dealt = coop_on_damage(C[5], &W[5].mobs[7], &W[5].hero, 33);
	CHECK(dealt == 33); /* local prediction */
	for (int k = 0; k < 30; k++, t += 1.0 / 60) step_all(N, t);
	CHECK(coop_take_pending_damage(C[0], &W[0].mobs[7]) == 33);
	CHECK(C[0]->mobs[7].threat.threat[5] > 0);

	/* host enemy hits the ghost of player 4 -> player 4's hero takes scaled damage */
	int life_before = W[4].hero.s.life;
	CHECK(coop_on_damage(C[0], &W[0].ghosts[4], &W[0].mobs[3], 10) == 0);
	for (int k = 0; k < 30; k++, t += 1.0 / 60) step_all(N, t);
	CHECK(W[4].hero.s.life == life_before - (int)lroundf(10 * m12.mob_dmg));

	/* pvp requires both players to opt in */
	life_before = W[2].hero.s.life;
	coop_on_damage(C[1], &W[1].ghosts[2], &W[1].hero, 100);
	for (int k = 0; k < 30; k++, t += 1.0 / 60) step_all(N, t);
	CHECK(W[2].hero.s.life == life_before);
	coop_toggle_pvp(C[1]);
	coop_toggle_pvp(C[2]);
	for (int k = 0; k < 30; k++, t += 1.0 / 60) step_all(N, t);
	CHECK(C[7]->pvp.enabled[1] && C[7]->pvp.enabled[2]);
	coop_on_damage(C[1], &W[1].ghosts[2], &W[1].hero, 100);
	for (int k = 0; k < 30; k++, t += 1.0 / 60) step_all(N, t);
	CHECK(W[2].hero.s.life == life_before - 35);

	/* lethal hit downs instead of killing; a nearby still ally revives */
	W[6].hero.s.life = 5;
	int applied = coop_on_damage(C[6], &W[6].hero, &W[6].mobs[1], 50);
	W[6].hero.s.life -= applied;
	CHECK(W[6].hero.s.life == 1);
	CHECK(C[6]->players[6].life.state == LIFE_DOWNED);
	for (int k = 0; k < 20; k++, t += 1.0 / 60) step_all(N, t);
	CHECK(C[0]->players[6].life.state == LIFE_DOWNED);
	CHECK(C[9]->players[6].life.state == LIFE_DOWNED);
	/* player 7 walks over and stands on player 6 */
	park_on[7] = 1 + 6;
	int revived = 0, frames = 0;
	for (; frames < 600 && !revived; frames++, t += 1.0 / 60) {
		step_all(N, t);
		revived = C[6]->players[6].life.state == LIFE_ALIVE;
	}
	park_on[7] = 0;
	CHECK(revived);
	printf("  revive completed after %.2fs\n", frames / 60.0);
	for (int k = 0; k < 20; k++, t += 1.0 / 60) step_all(N, t);
	CHECK(C[0]->players[6].life.state == LIFE_ALIVE);
	CHECK(W[6].hero.s.life == (int)lroundf(200 * m12.revive_hp));

	/* shared cells reach everybody */
	coop_award(C[8], 25, 0);
	for (int k = 0; k < 30; k++, t += 1.0 / 60) step_all(N, t);
	int shared = 1;
	for (int i = 0; i < N; i++) if (i != 8 && W[i].cells != 25) shared = 0;
	CHECK(shared);

	/* a client leaves: roster and balance update everywhere */
	coop_shutdown(C[11]);
	free(C[11]);
	C[11] = NULL;
	for (int k = 0; k < 30; k++, t += 1.0 / 60) step_all(N, t);
	CHECK(coop_player_count(C[0]) == N - 1);
	CHECK(coop_player_count(C[3]) == N - 1);
	CHECK(C[0]->bal.players == N - 1);

	double kbps = C[0]->bytes_out / (t - 1.0) / 1024.0;
	printf("  host snapshot upload: %.1f KiB/s for %d clients\n", kbps, N - 1);
	for (int i = 0; i <= N; i++) if (C[i]) { coop_shutdown(C[i]); free(C[i]); }
	return CHECK_DONE("test_coop12");
}
