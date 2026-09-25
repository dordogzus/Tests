/*
 * Mimics HashLink's main(): imports libhl.dll, then reads hlboot.dat through
 * _wfopen/fseek/ftell/fread exactly like hl's load_code(). Prints whether the
 * bytes it received are the DC-Coop patched bytecode.
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>

__declspec(dllimport) unsigned fake_hl_time(void);

int main(void) {
	printf("libhl time=%u\n", fake_hl_time() > 0 ? 1u : 0u);
	FILE *chk = _wfopen(L"hlboot.dat", L"rb");
	if (!chk) { printf("no hlboot.dat\n"); return 1; }
	fclose(chk);
	FILE *f = _wfopen(L"hlboot.dat", L"rb");
	fseek(f, 0, SEEK_END);
	long size = ftell(f);
	fseek(f, 0, SEEK_SET);
	char *data = malloc((size_t)size);
	size_t got = fread(data, 1, (size_t)size, f);
	fclose(f);
	int patched = 0;
	const char *marker = "$dccoop_patched_v1";
	for (long i = 0; i + (long)strlen(marker) <= (long)got; i++)
		if (memcmp(data + i, marker, strlen(marker)) == 0) { patched = 1; break; }
	printf("hlboot bytes=%ld patched=%d\n", size, patched);
	free(data);
	return 0;
}
