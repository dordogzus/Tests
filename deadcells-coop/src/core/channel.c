#include "channel.h"

#include <string.h>

void ch_init(channel *c) {
	memset(c, 0, sizeof *c);
	c->rtt = 0.1;
	c->rtt_var = 0.05;
}

int ch_pending_reliable(const channel *c) { return (uint16_t)(c->rel_out_next - c->rel_out_base); }

double ch_rto(const channel *c) {
	double rto = c->rtt + 4.0 * c->rtt_var + 0.01;
	if (rto < 0.03) rto = 0.03;
	if (rto > 1.0) rto = 1.0;
	return rto;
}

int ch_send_reliable(channel *c, const void *data, int len) {
	if (len <= 0 || len > CH_MSG_MAX) return -1;
	if (ch_pending_reliable(c) >= CH_REL_WINDOW) return -1;
	uint16_t id = c->rel_out_next++;
	ch_relmsg *m = &c->rel_out[id % CH_REL_WINDOW];
	m->id = id;
	m->len = (uint16_t)len;
	m->last_sent = 0;
	memcpy(m->data, data, (size_t)len);
	c->rel_out_acked[id % CH_REL_WINDOW] = 0;
	return 0;
}

static void put16(uint8_t *p, uint16_t v) { p[0] = (uint8_t)v; p[1] = (uint8_t)(v >> 8); }
static void put32(uint8_t *p, uint32_t v) { put16(p, (uint16_t)v); put16(p + 2, (uint16_t)(v >> 16)); }
static uint16_t get16(const uint8_t *p) { return (uint16_t)(p[0] | (p[1] << 8)); }
static uint32_t get32(const uint8_t *p) { return get16(p) | ((uint32_t)get16(p + 2) << 16); }

/* header: seq(2) ack(2) ack_bits(4) nrel(1) [id(2) len(2) data]* unrel... */
#define CH_HDR 9

int ch_build_packet(channel *c, double now, uint8_t *out, int cap, const void *unrel, int unrel_len) {
	if (cap < CH_HDR) return -1;
	uint16_t seq = c->local_seq++;
	put16(out, seq);
	put16(out + 2, c->remote_seq);
	put32(out + 4, c->recv_bits);
	int pos = CH_HDR, nrel = 0;
	ch_sent *s = &c->sent[seq % CH_SENT_HISTORY];
	if (s->valid) c->packets_lost++; /* slot reused without ever being acked */
	s->seq = seq;
	s->valid = 1;
	s->time = now;
	double rto = ch_rto(c);
	/* leave room for the unreliable payload when it fits the MTU budget */
	int rel_budget = cap - (unrel_len + CH_HDR <= cap / 2 ? unrel_len : 0);
	for (uint16_t id = c->rel_out_base; id != c->rel_out_next && nrel < CH_MAX_REL_PER_PACKET; id++) {
		int slot = id % CH_REL_WINDOW;
		if (c->rel_out_acked[slot]) continue;
		ch_relmsg *m = &c->rel_out[slot];
		if (m->last_sent > 0 && now - m->last_sent < rto) continue;
		if (pos + 4 + m->len > rel_budget) break;
		put16(out + pos, m->id);
		put16(out + pos + 2, m->len);
		memcpy(out + pos + 4, m->data, m->len);
		pos += 4 + m->len;
		m->last_sent = now;
		s->rel_ids[nrel++] = m->id;
	}
	/* bit 7: ack fields are meaningful (nothing received yet otherwise) */
	out[8] = (uint8_t)(nrel | (c->have_remote ? 0x80 : 0));
	s->nrel = (uint8_t)nrel;
	if (unrel && unrel_len > 0 && pos + unrel_len <= cap) {
		memcpy(out + pos, unrel, (size_t)unrel_len);
		pos += unrel_len;
	}
	c->packets_sent++;
	return pos;
}

static void on_acked(channel *c, uint16_t seq, double now) {
	ch_sent *s = &c->sent[seq % CH_SENT_HISTORY];
	if (!s->valid || s->seq != seq) return;
	s->valid = 0;
	c->packets_acked++;
	double sample = now - s->time;
	if (!c->rtt_init) {
		c->rtt = sample;
		c->rtt_var = sample / 2;
		c->rtt_init = 1;
	} else { /* RFC 6298 smoothing */
		double d = sample - c->rtt;
		c->rtt_var += 0.25 * ((d < 0 ? -d : d) - c->rtt_var);
		c->rtt += 0.125 * d;
	}
	for (int i = 0; i < s->nrel; i++) {
		uint16_t id = s->rel_ids[i];
		if ((uint16_t)(id - c->rel_out_base) < (uint16_t)(c->rel_out_next - c->rel_out_base))
			c->rel_out_acked[id % CH_REL_WINDOW] = 1;
	}
	while (c->rel_out_base != c->rel_out_next && c->rel_out_acked[c->rel_out_base % CH_REL_WINDOW]) {
		c->rel_out_acked[c->rel_out_base % CH_REL_WINDOW] = 0;
		c->rel_out_base++;
	}
}

int ch_receive_packet(channel *c, double now, const uint8_t *in, int len, int *unrel_len) {
	if (len < CH_HDR) return -1;
	uint16_t seq = get16(in), ack = get16(in + 2);
	uint32_t ack_bits = get32(in + 4);
	int nrel = in[8] & 0x7F, ack_valid = in[8] & 0x80;

	/* incoming sequence bookkeeping; drop duplicates and very old packets */
	if (!c->have_remote) {
		c->have_remote = 1;
		c->remote_seq = seq;
		c->recv_bits = 0;
	} else if (seq_gt(seq, c->remote_seq)) {
		uint16_t shift = (uint16_t)(seq - c->remote_seq);
		c->recv_bits = shift >= 32 ? 0 : (c->recv_bits << shift) | (1u << (shift - 1));
		c->remote_seq = seq;
	} else {
		uint16_t back = (uint16_t)(c->remote_seq - seq);
		if (back == 0 || back > 32) return -1;
		uint32_t bit = 1u << (back - 1);
		if (c->recv_bits & bit) return -1;
		c->recv_bits |= bit;
	}

	if (ack_valid) {
		on_acked(c, ack, now);
		for (int i = 0; i < 32; i++)
			if (ack_bits & (1u << i)) on_acked(c, (uint16_t)(ack - 1 - i), now);
	}
	/* anything older than the ack window without an ack is lost */
	double lost_after = ch_rto(c) * 3 + 0.2;
	for (int i = 0; i < CH_SENT_HISTORY; i++) {
		ch_sent *s = &c->sent[i];
		if (s->valid && now - s->time > lost_after) { s->valid = 0; c->packets_lost++; }
	}
	uint32_t total = c->packets_acked + c->packets_lost;
	if (total >= 16) {
		double inst = (double)c->packets_lost / (double)total;
		c->loss += 0.1 * (inst - c->loss);
		c->packets_acked = c->packets_lost = 0;
	}

	int pos = CH_HDR;
	for (int i = 0; i < nrel; i++) {
		if (pos + 4 > len) return -1;
		uint16_t id = get16(in + pos), mlen = get16(in + pos + 2);
		pos += 4;
		if (mlen > CH_MSG_MAX || pos + mlen > len) return -1;
		uint16_t ahead = (uint16_t)(id - c->rel_in_next);
		if (ahead < CH_REL_WINDOW) {
			int slot = id % CH_REL_WINDOW;
			if (!c->rel_in_have[slot]) {
				c->rel_in_have[slot] = 1;
				c->rel_in_len[slot] = mlen;
				memcpy(c->rel_in[slot], in + pos, mlen);
			}
		}
		pos += mlen;
	}
	*unrel_len = len - pos;
	return pos;
}

int ch_pop_reliable(channel *c, uint8_t *out, int cap) {
	int slot = c->rel_in_next % CH_REL_WINDOW;
	if (!c->rel_in_have[slot]) return -1;
	int n = c->rel_in_len[slot];
	if (n > cap) return -1;
	memcpy(out, c->rel_in[slot], (size_t)n);
	c->rel_in_have[slot] = 0;
	c->rel_in_next++;
	return n;
}
