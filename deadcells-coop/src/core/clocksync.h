/*
 * Estimates host time on a client (NTP-style). Each sample gives
 * offset = host_time + rtt/2 - local_receive_time; the sample with the lowest
 * rtt in a sliding window has the least queueing error, so it is the target.
 * The applied offset slews toward the target to keep interpolation smooth.
 */
#ifndef DCCOOP_CLOCKSYNC_H
#define DCCOOP_CLOCKSYNC_H

#define CLK_WINDOW 16

typedef struct {
	double rtt[CLK_WINDOW], off[CLK_WINDOW];
	int n, next;
	double offset;      /* applied: host = local + offset */
	double jitter;      /* smoothed |rtt - min_rtt| */
	int synced;
} clocksync;

void clk_init(clocksync *c);
void clk_sample(clocksync *c, double local_send, double host_time, double local_recv);
/* Advance slewing by dt seconds of local time. */
void clk_update(clocksync *c, double dt);
double clk_host_time(const clocksync *c, double local_now);

#endif
