#include "interp.h"

#include <string.h>

void interp_clear(interp_track *t) { memset(t, 0, sizeof *t); }

static const ent_state *at(const interp_track *t, int age) { /* age 0 = newest */
	return &t->s[(t->head - age + INTERP_CAP) % INTERP_CAP];
}

const ent_state *interp_latest(const interp_track *t) { return t->count ? at(t, 0) : NULL; }

void interp_push(interp_track *t, const ent_state *st) {
	if (t->count == 0) {
		t->head = 0;
		t->s[0] = *st;
		t->count = 1;
		return;
	}
	const ent_state *newest = at(t, 0);
	if (st->t > newest->t) {
		t->head = (t->head + 1) % INTERP_CAP;
		t->s[t->head] = *st;
		if (t->count < INTERP_CAP) t->count++;
		return;
	}
	/* late packet: insert keeping order, drop if too old or duplicate */
	int n = t->count, pos = 0;
	while (pos < n && at(t, pos)->t > st->t) pos++;
	if (pos < n && at(t, pos)->t == st->t) return;
	if (pos == n && n == INTERP_CAP) return;
	int newcount = n < INTERP_CAP ? n + 1 : n;
	/* shift ages [pos, newcount-2] one older to open slot at age pos */
	ent_state tmp[INTERP_CAP];
	for (int i = 0; i < n; i++) tmp[i] = *at(t, i);
	int k = 0;
	for (int i = 0; i < newcount; i++) {
		const ent_state *src = i == pos ? st : &tmp[k++];
		t->s[(t->head - i + INTERP_CAP) % INTERP_CAP] = *src;
	}
	t->count = newcount;
}

static float hermite(float p0, float v0, float p1, float v1, float dt, float u) {
	float u2 = u * u, u3 = u2 * u;
	return (2 * u3 - 3 * u2 + 1) * p0 + (u3 - 2 * u2 + u) * dt * v0 + (-2 * u3 + 3 * u2) * p1 + (u3 - u2) * dt * v1;
}

static float hermite_d(float p0, float v0, float p1, float v1, float dt, float u) {
	float u2 = u * u;
	return ((6 * u2 - 6 * u) * p0 + (3 * u2 - 4 * u + 1) * dt * v0 + (-6 * u2 + 6 * u) * p1 + (3 * u2 - 2 * u) * dt * v1) / dt;
}

int interp_sample(const interp_track *t, double time, ent_state *out) {
	if (t->count == 0) return 0;
	const ent_state *newest = at(t, 0);
	if (time >= newest->t) {
		*out = *newest;
		double dt = time - newest->t;
		if (dt > INTERP_MAX_EXTRAPOLATE) dt = INTERP_MAX_EXTRAPOLATE;
		out->x += newest->vx * (float)dt;
		out->y += newest->vy * (float)dt;
		out->t = time;
		return 2;
	}
	for (int i = 1; i < t->count; i++) {
		const ent_state *a = at(t, i), *b = at(t, i - 1);
		if (a->t > time) continue;
		float dt = (float)(b->t - a->t);
		float u = dt > 0 ? (float)((time - a->t) / dt) : 1.f;
		*out = u < 0.5f ? *a : *b; /* discrete fields switch at midpoint */
		out->t = time;
		float jx = b->x - a->x, jy = b->y - a->y;
		if (jx * jx + jy * jy > INTERP_SNAP_DIST * INTERP_SNAP_DIST) {
			out->x = u < 0.5f ? a->x : b->x;
			out->y = u < 0.5f ? a->y : b->y;
			return 1;
		}
		out->x = hermite(a->x, a->vx, b->x, b->vx, dt, u);
		out->y = hermite(a->y, a->vy, b->y, b->vy, dt, u);
		out->vx = hermite_d(a->x, a->vx, b->x, b->vx, dt, u);
		out->vy = hermite_d(a->y, a->vy, b->y, b->vy, dt, u);
		return 1;
	}
	*out = *at(t, t->count - 1); /* older than everything we have */
	out->t = time;
	return 2;
}

double interp_delay(double snapshot_interval, double jitter) {
	double d = snapshot_interval * 2.0 + jitter * 2.5;
	if (d < 0.05) d = 0.05;
	if (d > 0.35) d = 0.35;
	return d;
}
