#include "../src/core/bitbuf.h"
#include "../src/core/channel.h"
#include "../src/core/clocksync.h"
#include "../src/core/config.h"
#include "../src/core/interp.h"
#include "../src/core/rules.h"
#include "check.h"

#include <stdlib.h>
#include <string.h>

static uint32_t rng = 12345;
static uint32_t rnd(void) { rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5; return rng; }
static float frnd(void) { return (float)(rnd() % 100000) / 100000.f; }

static void test_bitbuf(void) {
	uint8_t buf[64];
	bitbuf w, r;
	bb_init(&w, buf, sizeof buf);
	bb_write(&w, 5, 3);
	bb_write_varu(&w, 300000);
	bb_write_q(&w, 123.456f, -64.f, 4032.f, 18);
	bb_write_q(&w, 1e9f, 0.f, 1.f, 8); /* clamps */
	bb_write_str(&w, "Beheaded", 24);
	bb_write_bool(&w, 1);
	CHECK(!w.overflow);
	bb_init(&r, buf, bb_bytes(&w));
	CHECK(bb_read(&r, 3) == 5);
	CHECK(bb_read_varu(&r) == 300000);
	CHECK_NEAR(bb_read_q(&r, -64.f, 4032.f, 18), 123.456, bb_q_step(-64.f, 4032.f, 18));
	CHECK_NEAR(bb_read_q(&r, 0.f, 1.f, 8), 1.0, 1e-6);
	char s[24];
	bb_read_str(&r, s, 24);
	CHECK(strcmp(s, "Beheaded") == 0);
	CHECK(bb_read_bool(&r) == 1);
	CHECK(!r.overflow);
	bb_read(&r, 32);
	CHECK(r.overflow); /* reading past the end is detected */
	uint8_t small[2];
	bb_init(&w, small, 2);
	bb_write(&w, 0xFFFF, 16);
	bb_write(&w, 1, 1);
	CHECK(w.overflow);
}

/* Two channels over a lossy, reordering in-memory link. */
typedef struct { uint8_t data[CH_MTU]; int len; double deliver_at; } pkt;

static void test_channel(void) {
	static channel a, b;
	ch_init(&a);
	ch_init(&b);
	static pkt q_ab[4096], q_ba[4096];
	int n_ab = 0, n_ba = 0;
	int sent = 0, received = 0, order_ok = 1;
	const int TOTAL = 2000;
	double t = 0;
	for (int step = 0; step < 20000 && received < TOTAL; step++, t += 1.0 / 60) {
		for (int k = 0; k < 3 && sent < TOTAL; k++) {
			uint8_t m[8];
			memcpy(m, &sent, 4);
			memset(m + 4, 0xAB, 4);
			if (ch_send_reliable(&a, m, 8) == 0) sent++;
		}
		uint8_t p[CH_MTU];
		int len = ch_build_packet(&a, t, p, CH_MTU, "u", 1);
		if (frnd() > 0.25f && n_ab < 4096) { /* 25% loss, 0-80ms jitter */
			memcpy(q_ab[n_ab].data, p, (size_t)len);
			q_ab[n_ab].len = len;
			q_ab[n_ab++].deliver_at = t + 0.02 + frnd() * 0.08;
		}
		len = ch_build_packet(&b, t, p, CH_MTU, NULL, 0);
		if (frnd() > 0.25f && n_ba < 4096) {
			memcpy(q_ba[n_ba].data, p, (size_t)len);
			q_ba[n_ba].len = len;
			q_ba[n_ba++].deliver_at = t + 0.02 + frnd() * 0.08;
		}
		for (int i = 0; i < n_ab; i++) {
			if (q_ab[i].deliver_at > t) continue;
			int ul;
			ch_receive_packet(&b, t, q_ab[i].data, q_ab[i].len, &ul);
			q_ab[i--] = q_ab[--n_ab];
		}
		for (int i = 0; i < n_ba; i++) {
			if (q_ba[i].deliver_at > t) continue;
			int ul;
			ch_receive_packet(&a, t, q_ba[i].data, q_ba[i].len, &ul);
			q_ba[i--] = q_ba[--n_ba];
		}
		uint8_t m[CH_MSG_MAX];
		int n;
		while ((n = ch_pop_reliable(&b, m, sizeof m)) >= 0) {
			int v;
			memcpy(&v, m, 4);
			if (v != received || n != 8) order_ok = 0;
			received++;
		}
	}
	CHECK(received == TOTAL);
	CHECK(order_ok);
	CHECK(a.rtt > 0.03 && a.rtt < 0.3);
	CHECK(a.loss > 0.1 && a.loss < 0.6);
	/* duplicate packets are rejected */
	channel c;
	ch_init(&c);
	uint8_t p[CH_MTU];
	channel d;
	ch_init(&d);
	int len = ch_build_packet(&d, 0, p, CH_MTU, "x", 1), ul;
	CHECK(ch_receive_packet(&c, 0, p, len, &ul) >= 0);
	CHECK(ch_receive_packet(&c, 0, p, len, &ul) < 0);
}

static void test_clock(void) {
	clocksync c;
	clk_init(&c);
	const double true_offset = 1234.5678;
	double local = 10.0;
	for (int i = 0; i < 200; i++) {
		double up = 0.010 + frnd() * 0.030, down = 0.010 + frnd() * 0.030;
		double host_t = local + up + true_offset;
		clk_sample(&c, local, host_t, local + up + down);
		clk_update(&c, 0.05);
		local += 0.05;
	}
	/* error bounded by asymmetry of the best sample */
	CHECK_NEAR(c.offset, true_offset, 0.01);
	double prev = clk_host_time(&c, local);
	int monotonic = 1;
	for (int i = 0; i < 100; i++) {
		local += 0.016;
		clk_sample(&c, local - 0.03, local - 0.015 + true_offset + 0.003, local);
		clk_update(&c, 0.016);
		double h = clk_host_time(&c, local);
		if (h < prev) monotonic = 0;
		prev = h;
	}
	CHECK(monotonic);
}

static void test_interp(void) {
	interp_track t;
	interp_clear(&t);
	ent_state s;
	memset(&s, 0, sizeof s);
	CHECK(interp_sample(&t, 0, &s) == 0);
	/* constant velocity: hermite must reproduce the line exactly */
	for (int i = 0; i < 10; i++) {
		ent_state st = { 0 };
		st.t = i * 0.1;
		st.x = 5.f + 3.f * (float)st.t;
		st.vx = 3.f;
		st.life = 100 - i;
		if (i == 4) continue; /* push 4 late, out of order */
		interp_push(&t, &st);
	}
	ent_state late = { 0 };
	late.t = 0.4; late.x = 5.f + 1.2f; late.vx = 3.f; late.life = 96;
	interp_push(&t, &late);
	interp_push(&t, &late); /* duplicate ignored */
	CHECK(t.count == 10);
	CHECK(interp_sample(&t, 0.43, &s) == 1);
	CHECK_NEAR(s.x, 5.f + 3.f * 0.43f, 1e-4);
	CHECK_NEAR(s.vx, 3.0, 1e-3);
	CHECK(interp_sample(&t, 0.9 + 0.05, &s) == 2);
	CHECK_NEAR(s.x, 5.f + 3.f * 0.95f, 1e-4);
	interp_sample(&t, 5.0, &s); /* extrapolation is capped */
	CHECK_NEAR(s.x, 5.f + 3.f * (0.9f + INTERP_MAX_EXTRAPOLATE), 1e-4);
	/* teleports snap instead of sweeping across the map */
	interp_clear(&t);
	ent_state a = { 0 }, b = { 0 };
	a.t = 0; a.x = 0;
	b.t = 0.1; b.x = 100;
	interp_push(&t, &a);
	interp_push(&t, &b);
	interp_sample(&t, 0.02, &s);
	CHECK(s.x == 0.f);
	CHECK(interp_delay(1.0 / 30, 0) >= 0.066);
}

static void test_balance(void) {
	balance_cfg cfg;
	balance_default_cfg(&cfg);
	balance_mults m1, m2, m4, m12;
	balance_compute(&cfg, 1, &m1);
	balance_compute(&cfg, 2, &m2);
	balance_compute(&cfg, 4, &m4);
	balance_compute(&cfg, 12, &m12);
	CHECK_NEAR(m1.mob_hp, 1.0, 1e-6);
	CHECK_NEAR(m1.boss_hp, 1.0, 1e-6);
	CHECK_NEAR(m1.density, 1.0, 1e-6);
	CHECK_NEAR(m1.mob_dmg, 1.0, 1e-6);
	CHECK_NEAR(m2.mob_hp, 2 * 1.03 / 1.2, 1e-4);
	CHECK_NEAR(m12.density, 2.5, 1e-6);
	CHECK_NEAR(m12.mob_hp, 12 * 1.33 / 2.5, 1e-4);
	CHECK_NEAR(m12.boss_hp, 12 * (1 - 0.015 * 11), 1e-4);
	CHECK_NEAR(m12.mob_dmg, 1.5, 1e-6);
	/* total enemy effort per player stays within 1.0..1.35 of solo */
	for (int n = 1; n <= 12; n++) {
		balance_mults m;
		balance_compute(&cfg, n, &m);
		float pressure = m.mob_hp * m.density / (float)n;
		CHECK(pressure >= 0.999f && pressure <= 1.35f);
	}
	CHECK(m4.mob_hp > m2.mob_hp && m12.mob_hp > m4.mob_hp);
	int life = 50, maxl = 100;
	balance_rescale(&life, &maxl, 1.f, 2.f);
	CHECK(maxl == 200 && life == 100);
	life = 1; maxl = 1000;
	balance_rescale(&life, &maxl, 4.f, 1.f);
	CHECK(life >= 1 && maxl == 250);
	CHECK_NEAR(balance_bleedout(0), 30.0, 1e-4);
	CHECK(balance_bleedout(1) < balance_bleedout(0));
	CHECK(balance_bleedout(20) >= 6.f);
	CHECK_NEAR(balance_revive_time(1), 3.0, 1e-4);
	CHECK(balance_revive_time(3) < balance_revive_time(2));
	CHECK(balance_revive_time(12) >= 1.2f);
}

static void test_pvp(void) {
	pvp_state p;
	pvp_init(&p);
	CHECK(pvp_resolve(&p, 0, 1, 100, 0) == 0); /* nobody opted in */
	pvp_toggle(&p, 0);
	CHECK(pvp_resolve(&p, 0, 1, 100, 0) == 0); /* victim did not opt in */
	pvp_toggle(&p, 1);
	CHECK(pvp_resolve(&p, 0, 1, 100, 0) == 35);
	CHECK(pvp_resolve(&p, 0, 1, 100, 0.3) == 0); /* hit immunity */
	CHECK(pvp_resolve(&p, 0, 1, 1, 1.0) == 1);   /* minimum 1 */
	CHECK(pvp_resolve(&p, 1, 1, 100, 5.0) == 0); /* self */
	p.mode = PVP_TEAMS;
	p.team[0] = p.team[1] = 1;
	CHECK(pvp_resolve(&p, 0, 1, 100, 9.0) == 0);
	p.team[1] = 0;
	CHECK(pvp_resolve(&p, 0, 1, 100, 9.0) == 35);
	p.safe_zone = 1;
	CHECK(pvp_resolve(&p, 0, 1, 100, 20.0) == 0);
}

static void test_threat(void) {
	threat_table t;
	threat_init(&t);
	threat_player pl[3] = { 0 };
	for (int i = 0; i < 3; i++) { pl[i].present = 1; pl[i].taunt = 1; pl[i].y = 0; }
	pl[0].x = 2; pl[1].x = 10; pl[2].x = 20;
	int load[3] = { 0, 0, 0 };
	CHECK(threat_pick(&t, 0, 0, pl, 3, load, 1, 24, 0) == 0); /* nearest */
	threat_add(&t, 2, 400); /* heavy damage from far player */
	CHECK(threat_pick(&t, 0, 0, pl, 3, load, 1, 24, 0.5) == 0); /* held: min hold time */
	CHECK(threat_pick(&t, 0, 0, pl, 3, load, 1, 24, 2.0) == 2); /* then switches */
	pl[2].downed = 1;
	CHECK(threat_pick(&t, 0, 0, pl, 3, load, 1, 24, 2.1) != 2); /* downed not targeted */
	pl[2].downed = 0;
	threat_init(&t);
	threat_decay(&t, 1, 4);
	int crowd[3] = { 6, 0, 0 }; /* player 0 swarmed */
	pl[1].x = 4;
	CHECK(threat_pick(&t, 0, 0, pl, 3, crowd, 2, 24, 10) == 1); /* spread to next */
	threat_add(&t, 0, 100);
	threat_decay(&t, 4, 4);
	CHECK_NEAR(t.threat[0], 50.0, 1e-3);
}

static void test_life(void) {
	life_state l;
	life_init(&l);
	CHECK(life_on_lethal(&l, 0) == 0 && l.state == LIFE_DEAD); /* solo: real death */
	life_new_level(&l);
	CHECK(l.state == LIFE_ALIVE);
	CHECK(life_on_lethal(&l, 2) == 1 && l.state == LIFE_DOWNED);
	life_update(&l, 1.f, 1);
	life_update(&l, 1.f, 0); /* interruption: progress decays, bleed resumes */
	CHECK(l.revive_progress > 0.1f && l.revive_progress < 0.34f);
	for (int i = 0; i < 40 && !life_take_revived(&l); i++) life_update(&l, 0.1f, 2);
	CHECK(l.state == LIFE_ALIVE);
	CHECK(life_on_lethal(&l, 1) == 1);
	CHECK_NEAR(l.bleed_left, 22.5, 1e-3); /* second down bleeds faster */
	for (int i = 0; i < 300; i++) life_update(&l, 0.1f, 0);
	CHECK(l.state == LIFE_DEAD);
	life_state all[3];
	uint8_t present[3] = { 1, 1, 0 };
	life_init(&all[0]); life_init(&all[1]); life_init(&all[2]);
	all[0].state = LIFE_DOWNED;
	CHECK(!life_team_wiped(all, present, 3));
	all[1].state = LIFE_DEAD;
	CHECK(life_team_wiped(all, present, 3));
}

static void test_config(void) {
	dc_config c;
	config_defaults(&c);
	config_parse(&c, "# comment\n[net]\nname = Kinslayer ; trailing\nmode=join\njoin = 192.168.1.20:5000\n"
		"tick_rate = 500\npvp = teams\ndensity_cap = 3.0\nfield_life = hp\n");
	CHECK(strcmp(c.name, "Kinslayer") == 0);
	CHECK(c.mode == 2);
	CHECK(strcmp(c.join_addr, "192.168.1.20:5000") == 0);
	CHECK(c.tick_rate == 60);
	CHECK(c.pvp_mode == PVP_TEAMS);
	CHECK_NEAR(c.balance.density_cap, 3.0, 1e-6);
	CHECK(strcmp(c.f_life, "hp") == 0);
}

int main(void) {
	test_bitbuf();
	test_channel();
	test_clock();
	test_interp();
	test_balance();
	test_pvp();
	test_threat();
	test_life();
	test_config();
	return CHECK_DONE("test_core");
}
