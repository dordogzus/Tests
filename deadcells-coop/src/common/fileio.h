#ifndef DCCOOP_FILEIO_H
#define DCCOOP_FILEIO_H
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>

static inline uint8_t *dc_read_file(const char *path, size_t *size) {
	FILE *f = fopen(path, "rb");
	if (!f) return NULL;
	fseek(f, 0, SEEK_END);
	long n = ftell(f);
	fseek(f, 0, SEEK_SET);
	uint8_t *b = n > 0 ? malloc((size_t)n) : NULL;
	if (b && fread(b, 1, (size_t)n, f) != (size_t)n) { free(b); b = NULL; }
	fclose(f);
	if (b) *size = (size_t)n;
	return b;
}

static inline int dc_write_file(const char *path, const uint8_t *data, size_t size) {
	FILE *f = fopen(path, "wb");
	if (!f) return -1;
	size_t w = fwrite(data, 1, size, f);
	fclose(f);
	return w == size ? 0 : -1;
}
#endif
