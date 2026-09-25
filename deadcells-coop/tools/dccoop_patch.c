/*
 * Offline patcher (fallback when the winmm.dll loader cannot be used):
 *   dccoop-patch hlboot.dat hlboot.coop.dat [dccoop_hooks.txt]
 */
#include "../src/common/fileio.h"
#include "../src/patchset/dcpatch.h"

static void log_stdout(void *ud, const char *m) { (void)ud; printf("%s\n", m); }

int main(int argc, char **argv) {
	if (argc < 3) {
		fprintf(stderr, "usage: dccoop-patch <in.hl|hlboot.dat> <out> [hooks.txt]\n");
		return 2;
	}
	size_t size, osize, hsize;
	uint8_t *in = dc_read_file(argv[1], &size), *out;
	if (!in) { fprintf(stderr, "cannot read %s\n", argv[1]); return 1; }
	char *hooks = NULL;
	if (argc > 3) {
		uint8_t *h = dc_read_file(argv[3], &hsize);
		if (!h) { fprintf(stderr, "cannot read %s\n", argv[3]); return 1; }
		hooks = realloc(h, hsize + 1);
		hooks[hsize] = 0;
	}
	if (dcpatch_buffer(in, size, hooks, &out, &osize, log_stdout, NULL)) return 1;
	if (dc_write_file(argv[2], out, osize)) { fprintf(stderr, "cannot write %s\n", argv[2]); return 1; }
	printf("wrote %s (%zu bytes)\n", argv[2], osize);
	free(out);
	free(in);
	free(hooks);
	return 0;
}
