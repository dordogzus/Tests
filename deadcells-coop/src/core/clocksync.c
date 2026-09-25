#include "clocksync.h"

#include <math.h>
#include <string.h>

void clk_init(clocksync *c) { memset(c, 0, sizeof *c); }

static double target_offset(const clocksync *c, double *min_rtt) {
	int best = 0;
	for (int i = 1; i < c->n; i++)
		if (c->rtt[i] < c->rtt[best]) best = i;
	*min_rtt = c->rtt[best];
	return c->off[best];
}

void clk_sample(clocksync *c, double local_send, double host_time, double local_recv) {
	double rtt = local_recv - local_send;
	if (rtt < 0 || rtt > 2.0) return;
	c->rtt[c->next] = rtt;
	c->off[c->next] = host_time + rtt * 0.5 - local_recv;
	c->next = (c->next + 1) % CLK_WINDOW;
	if (c->n < CLK_WINDOW) c->n++;
	double min_rtt, target = target_offset(c, &min_rtt);
	c->jitter += 0.1 * (fabs(rtt - min_rtt) - c->jitter);
	if (!c->synced || fabs(target - c->offset) > 0.25) {
		c->offset = target;
		c->synced = 1;
	}
}

void clk_update(clocksync *c, double dt) {
	if (!c->synced || c->n == 0) return;
	double min_rtt, target = target_offset(c, &min_rtt);
	/* slew at most 5% of elapsed time so time never runs backwards */
	double max_step = dt * 0.05, d = target - c->offset;
	if (d > max_step) d = max_step;
	if (d < -max_step) d = -max_step;
	c->offset += d;
}

double clk_host_time(const clocksync *c, double local_now) { return local_now + c->offset; }
