/*
 * Per-connection reliability layer over UDP.
 *
 * Every datagram carries (seq, ack, ack_bits) so each packet acknowledges the
 * last 33 received packets. Reliable messages are piggybacked on packets and
 * resent until a packet containing them is acked; they are delivered in order.
 * Unreliable payload (snapshots) fills whatever space remains.
 */
#ifndef DCCOOP_CHANNEL_H
#define DCCOOP_CHANNEL_H

#include <stdint.h>

#define CH_MTU 1200
#define CH_MSG_MAX 480
#define CH_REL_WINDOW 256      /* max unacked reliable messages */
#define CH_SENT_HISTORY 256    /* packets remembered for ack processing */
#define CH_MAX_REL_PER_PACKET 16

typedef struct {
	uint16_t id;
	uint16_t len;
	double last_sent;   /* 0 = never sent */
	uint8_t data[CH_MSG_MAX];
} ch_relmsg;

typedef struct {
	uint16_t seq;
	uint8_t valid;
	uint8_t nrel;
	uint16_t rel_ids[CH_MAX_REL_PER_PACKET];
	double time;
} ch_sent;

typedef struct {
	/* outgoing packet sequencing */
	uint16_t local_seq;
	ch_sent sent[CH_SENT_HISTORY];
	/* incoming packet acks */
	uint16_t remote_seq;
	uint32_t recv_bits;
	int have_remote;
	/* reliable out: ring indexed by id % window, oldest = rel_out_base */
	uint16_t rel_out_base, rel_out_next;
	uint8_t rel_out_acked[CH_REL_WINDOW];
	ch_relmsg rel_out[CH_REL_WINDOW];
	/* reliable in */
	uint16_t rel_in_next;
	uint8_t rel_in_have[CH_REL_WINDOW];
	uint16_t rel_in_len[CH_REL_WINDOW];
	uint8_t rel_in[CH_REL_WINDOW][CH_MSG_MAX];
	/* link stats */
	double rtt, rtt_var;
	int rtt_init;
	uint32_t packets_sent, packets_acked, packets_lost;
	double loss; /* smoothed 0..1 */
} channel;

void ch_init(channel *c);
/* Queues a reliable message; returns -1 when the window is full (link dead). */
int ch_send_reliable(channel *c, const void *data, int len);
/* Builds a DATA packet body (after the transport header) into out.
 * unrel/unrel_len is appended if it fits. Returns packet length. */
int ch_build_packet(channel *c, double now, uint8_t *out, int cap, const void *unrel, int unrel_len);
/* Parses a packet body. Returns the offset of the unreliable payload (or -1
 * on malformed/duplicate packets) and its length through *unrel_len. */
int ch_receive_packet(channel *c, double now, const uint8_t *in, int len, int *unrel_len);
/* Pops the next in-order reliable message; returns its length or -1. */
int ch_pop_reliable(channel *c, uint8_t *out, int cap);
/* Unacked reliable messages pending. */
int ch_pending_reliable(const channel *c);
/* Resend timeout derived from rtt. */
double ch_rto(const channel *c);

/* seq comparison with wraparound */
static inline int seq_gt(uint16_t a, uint16_t b) { return (int16_t)(a - b) > 0; }

#endif
