/* Bit-level serializer with range quantization. Overflow is sticky and
 * checked once per message instead of after every field. */
#ifndef DCCOOP_BITBUF_H
#define DCCOOP_BITBUF_H

#include <stdint.h>

typedef struct {
	uint8_t *data;
	int cap_bits;
	int pos;
	int overflow;
} bitbuf;

void bb_init(bitbuf *b, void *data, int bytes);
void bb_write(bitbuf *b, uint32_t v, int bits);
uint32_t bb_read(bitbuf *b, int bits);
void bb_write_bool(bitbuf *b, int v);
int bb_read_bool(bitbuf *b);
/* Quantizes v into [min,max] with the given number of bits (clamped). */
void bb_write_q(bitbuf *b, float v, float min, float max, int bits);
float bb_read_q(bitbuf *b, float min, float max, int bits);
/* 7-bit groups; small values stay small. */
void bb_write_varu(bitbuf *b, uint32_t v);
uint32_t bb_read_varu(bitbuf *b);
void bb_write_bytes(bitbuf *b, const void *p, int n);
void bb_read_bytes(bitbuf *b, void *p, int n);
void bb_write_str(bitbuf *b, const char *s, int maxlen);
void bb_read_str(bitbuf *b, char *s, int maxlen);
int bb_bytes(const bitbuf *b);
int bb_bits_left(const bitbuf *b);

/* Step size of a quantized range, useful for tolerance checks. */
float bb_q_step(float min, float max, int bits);

#endif
