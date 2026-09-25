/* Parses a bytecode file, re-serializes it and checks the output is identical. */
#include "../src/common/fileio.h"
#include "../src/hlbc/hlbc.h"

#include <string.h>

int main(int argc, char **argv) {
	if (argc < 2) return 2;
	size_t size, osize;
	uint8_t *data = dc_read_file(argv[1], &size), *out;
	if (!data) { printf("FAIL cannot read %s\n", argv[1]); return 1; }
	const char *err = NULL;
	hlbc_code *c = hlbc_read(data, size, &err);
	if (!c) { printf("FAIL parse: %s\n", err); return 1; }
	if (hlbc_write(c, &out, &osize)) { printf("FAIL write\n"); return 1; }
	if (osize != size || memcmp(out, data, size) != 0) {
		size_t i = 0;
		while (i < size && i < osize && out[i] == data[i]) i++;
		printf("FAIL roundtrip differs at byte %zu (sizes %zu vs %zu)\n", i, size, osize);
		return 1;
	}
	printf("ok roundtrip %s (v%d, %zu bytes, %d functions)\n", argv[1], c->version, size, c->nfunctions);
	hlbc_free(c);
	free(out);
	free(data);
	return 0;
}
