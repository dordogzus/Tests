#include "bitbuf.h"

#include <math.h>
#include <string.h>

void bb_init(bitbuf *b, void *data, int bytes) {
	b->data = data;
	b->cap_bits = bytes * 8;
	b->pos = 0;
	b->overflow = 0;
}

void bb_write(bitbuf *b, uint32_t v, int bits) {
	if (b->overflow || b->pos + bits > b->cap_bits) { b->overflow = 1; return; }
	for (int i = 0; i < bits; i++, b->pos++) {
		int byte = b->pos >> 3, bit = b->pos & 7;
		if (bit == 0) b->data[byte] = 0;
		if ((v >> i) & 1) b->data[byte] |= (uint8_t)(1u << bit);
	}
}

uint32_t bb_read(bitbuf *b, int bits) {
	if (b->overflow || b->pos + bits > b->cap_bits) { b->overflow = 1; return 0; }
	uint32_t v = 0;
	for (int i = 0; i < bits; i++, b->pos++)
		if ((b->data[b->pos >> 3] >> (b->pos & 7)) & 1) v |= 1u << i;
	return v;
}

void bb_write_bool(bitbuf *b, int v) { bb_write(b, v ? 1 : 0, 1); }
int bb_read_bool(bitbuf *b) { return (int)bb_read(b, 1); }

float bb_q_step(float min, float max, int bits) {
	return (max - min) / (float)((1u << bits) - 1);
}

void bb_write_q(bitbuf *b, float v, float min, float max, int bits) {
	if (!(v >= min)) v = min; /* also catches NaN */
	if (v > max) v = max;
	uint32_t steps = (1u << bits) - 1;
	uint32_t q = (uint32_t)lroundf((v - min) / (max - min) * (float)steps);
	bb_write(b, q > steps ? steps : q, bits);
}

float bb_read_q(bitbuf *b, float min, float max, int bits) {
	uint32_t steps = (1u << bits) - 1;
	return min + (float)bb_read(b, bits) / (float)steps * (max - min);
}

void bb_write_varu(bitbuf *b, uint32_t v) {
	do {
		uint32_t g = v & 0x7F;
		v >>= 7;
		bb_write(b, g | (v ? 0x80 : 0), 8);
	} while (v && !b->overflow);
}

uint32_t bb_read_varu(bitbuf *b) {
	uint32_t v = 0;
	for (int shift = 0; shift < 35; shift += 7) {
		uint32_t g = bb_read(b, 8);
		v |= (g & 0x7F) << shift;
		if (!(g & 0x80) || b->overflow) return v;
	}
	b->overflow = 1;
	return 0;
}

void bb_write_bytes(bitbuf *b, const void *p, int n) {
	for (int i = 0; i < n; i++) bb_write(b, ((const uint8_t *)p)[i], 8);
}

void bb_read_bytes(bitbuf *b, void *p, int n) {
	for (int i = 0; i < n; i++) ((uint8_t *)p)[i] = (uint8_t)bb_read(b, 8);
}

void bb_write_str(bitbuf *b, const char *s, int maxlen) {
	int n = (int)strnlen(s, (size_t)maxlen);
	bb_write_varu(b, (uint32_t)n);
	bb_write_bytes(b, s, n);
}

void bb_read_str(bitbuf *b, char *s, int maxlen) {
	uint32_t n = bb_read_varu(b);
	if (n >= (uint32_t)maxlen) { b->overflow = 1; s[0] = 0; return; }
	bb_read_bytes(b, s, (int)n);
	s[n] = 0;
}

int bb_bytes(const bitbuf *b) { return (b->pos + 7) >> 3; }
int bb_bits_left(const bitbuf *b) { return b->cap_bits - b->pos; }
