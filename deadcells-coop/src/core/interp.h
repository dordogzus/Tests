/*
 * Snapshot interpolation for remote entities. States are timestamped in host
 * time; rendering samples at (host_now - delay) with cubic Hermite curves
 * built from positions and velocities, which removes the velocity kinks of
 * linear interpolation. Short gaps are extrapolated, then clamped.
 */
#ifndef DCCOOP_INTERP_H
#define DCCOOP_INTERP_H

#include <stdint.h>

#define INTERP_CAP 32
#define INTERP_MAX_EXTRAPOLATE 0.12

typedef struct {
	double t;
	float x, y, vx, vy;
	int32_t life;
	uint16_t anim;
	uint8_t dir, flags;
} ent_state;

typedef struct {
	ent_state s[INTERP_CAP];
	int head, count;   /* ring, head = newest */
} interp_track;

void interp_clear(interp_track *t);
/* Out-of-order states are inserted in time order; duplicates are ignored. */
void interp_push(interp_track *t, const ent_state *st);
/* Returns 0 if no data, 1 interpolated, 2 extrapolated/clamped. */
int interp_sample(const interp_track *t, double time, ent_state *out);
const ent_state *interp_latest(const interp_track *t);

/* Adaptive render delay: two snapshot intervals plus a jitter margin. */
double interp_delay(double snapshot_interval, double jitter);

/* Teleport threshold (world units) above which blending would look wrong. */
#define INTERP_SNAP_DIST 6.0f

#endif
