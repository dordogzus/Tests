/*
 * winmm.dll proxy loader.
 *
 * libhl.dll imports winmm.dll at load time and winmm is not a KnownDLL, so a
 * winmm.dll placed next to the game exe is loaded before the game's main()
 * reads hlboot.dat. This DLL:
 *   1. forwards every winmm export to the real System32 winmm.dll;
 *   2. patches the exe's import table so opening "hlboot.dat" opens a
 *      co-op patched copy (hlboot.dccoop.dat, regenerated when the original
 *      changes, e.g. after a game update);
 * The patched bytecode then loads dccoop.hdll through HashLink's normal
 * native library mechanism. Any failure falls back to the vanilla game.
 */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>

#include "../patchset/dcpatch.h"

/* ---------------- export forwarding ---------------- */

#define X(name) \
	FARPROC p_##name; \
	__attribute__((naked)) void fwd_##name(void) { __asm__ volatile("jmp *%0" : : "m"(p_##name)); }
#include "winmm_exports.h"
#undef X

static HMODULE g_real;

static void resolve_exports(void) {
	wchar_t path[MAX_PATH];
	UINT n = GetSystemDirectoryW(path, MAX_PATH);
	if (n == 0 || n > MAX_PATH - 12) return;
	wcscat(path, L"\\winmm.dll");
	g_real = LoadLibraryW(path);
	if (!g_real) return;
#define X(name) p_##name = GetProcAddress(g_real, #name);
#include "winmm_exports.h"
#undef X
}

/* ---------------- logging ---------------- */

static wchar_t g_dir[MAX_PATH]; /* exe directory with trailing backslash */

static void llog(const char *fmt, ...) {
	wchar_t path[MAX_PATH + 32];
	_snwprintf(path, MAX_PATH + 32, L"%lsdccoop_loader.log", g_dir);
	FILE *f = _wfopen(path, L"a");
	if (!f) return;
	va_list ap;
	va_start(ap, fmt);
	vfprintf(f, fmt, ap);
	va_end(ap);
	fputc('\n', f);
	fclose(f);
}

static void log_cb(void *ud, const char *m) { (void)ud; llog("%s", m); }

/* ---------------- patched bytecode cache ---------------- */

static wchar_t g_patched[MAX_PATH + 32];
static int g_patch_state; /* 0 untried, 1 ready, -1 failed */

static uint8_t *read_all(const wchar_t *path, size_t *size) {
	HANDLE h = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
	if (h == INVALID_HANDLE_VALUE) return NULL;
	LARGE_INTEGER sz;
	uint8_t *buf = NULL;
	if (GetFileSizeEx(h, &sz) && sz.QuadPart > 0 && sz.QuadPart < (1LL << 31)) {
		buf = malloc((size_t)sz.QuadPart);
		DWORD got = 0;
		if (buf && (!ReadFile(h, buf, (DWORD)sz.QuadPart, &got, NULL) || got != (DWORD)sz.QuadPart)) {
			free(buf);
			buf = NULL;
		}
		*size = (size_t)sz.QuadPart;
	}
	CloseHandle(h);
	return buf;
}

static int write_all(const wchar_t *path, const void *data, size_t size) {
	HANDLE h = CreateFileW(path, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, 0, NULL);
	if (h == INVALID_HANDLE_VALUE) return -1;
	DWORD w = 0;
	BOOL ok = WriteFile(h, data, (DWORD)size, &w, NULL);
	CloseHandle(h);
	return ok && w == size ? 0 : -1;
}

/* FNV-1a over the source bytecode plus the hook table: the cache is rebuilt
 * whenever the game or the hooks change. */
static uint64_t fnv(const uint8_t *p, size_t n, uint64_t h) {
	for (size_t i = 0; i < n; i++) { h ^= p[i]; h *= 1099511628211ULL; }
	return h;
}

static const wchar_t *patched_path_for(const wchar_t *original) {
	if (g_patch_state == 1) return g_patched;
	if (g_patch_state == -1) return NULL;
	g_patch_state = -1;
	size_t size = 0, hsize = 0;
	uint8_t *src = read_all(original, &size);
	if (!src) { llog("cannot read %ls", original); return NULL; }
	wchar_t hooks_path[MAX_PATH + 32];
	_snwprintf(hooks_path, MAX_PATH + 32, L"%lsdccoop_hooks.txt", g_dir);
	char *hooks = (char *)read_all(hooks_path, &hsize);
	if (hooks) {
		char *z = realloc(hooks, hsize + 1);
		if (z) { hooks = z; hooks[hsize] = 0; }
	}
	uint64_t key = fnv(src, size, 14695981039346656037ULL);
	if (hooks) key = fnv((const uint8_t *)hooks, hsize, key);

	_snwprintf(g_patched, MAX_PATH + 32, L"%lshlboot.dccoop.dat", g_dir);
	wchar_t key_path[MAX_PATH + 32];
	_snwprintf(key_path, MAX_PATH + 32, L"%lshlboot.dccoop.key", g_dir);
	size_t ksize = 0;
	uint64_t *old = (uint64_t *)read_all(key_path, &ksize);
	int fresh = old && ksize == sizeof(uint64_t) && *old == key && GetFileAttributesW(g_patched) != INVALID_FILE_ATTRIBUTES;
	free(old);
	if (fresh) {
		llog("using cached patched bytecode");
		g_patch_state = 1;
	} else {
		uint8_t *out = NULL;
		size_t osize = 0;
		DWORD t0 = GetTickCount();
		if (dcpatch_buffer(src, size, hooks, &out, &osize, log_cb, NULL) == 0 && write_all(g_patched, out, osize) == 0) {
			write_all(key_path, &key, sizeof key);
			llog("patched %u -> %u bytes in %lu ms", (unsigned)size, (unsigned)osize, GetTickCount() - t0);
			g_patch_state = 1;
		} else {
			llog("patch failed: running the vanilla game");
		}
		free(out);
	}
	free(src);
	free(hooks);
	return g_patch_state == 1 ? g_patched : NULL;
}

static int is_hlboot_w(const wchar_t *p) {
	if (!p) return 0;
	const wchar_t *b = p + wcslen(p);
	while (b > p && b[-1] != L'\\' && b[-1] != L'/') b--;
	return _wcsicmp(b, L"hlboot.dat") == 0;
}

static const wchar_t *redirect_w(const wchar_t *p) {
	if (!is_hlboot_w(p)) return p;
	const wchar_t *r = patched_path_for(p);
	return r ? r : p;
}

/* ---------------- open hooks ---------------- */

typedef FILE *(__cdecl *wfopen_t)(const wchar_t *, const wchar_t *);
typedef FILE *(__cdecl *fopen_t)(const char *, const char *);
typedef HANDLE(WINAPI *createfilew_t)(LPCWSTR, DWORD, DWORD, LPSECURITY_ATTRIBUTES, DWORD, DWORD, HANDLE);
typedef HANDLE(WINAPI *createfilea_t)(LPCSTR, DWORD, DWORD, LPSECURITY_ATTRIBUTES, DWORD, DWORD, HANDLE);

static wfopen_t o_wfopen;
static fopen_t o_fopen;
static createfilew_t o_CreateFileW;
static createfilea_t o_CreateFileA;

static FILE *__cdecl h_wfopen(const wchar_t *p, const wchar_t *m) { return o_wfopen(redirect_w(p), m); }

static FILE *__cdecl h_fopen(const char *p, const char *m) {
	wchar_t wp[MAX_PATH];
	if (p && MultiByteToWideChar(CP_ACP, 0, p, -1, wp, MAX_PATH) && is_hlboot_w(wp)) {
		const wchar_t *r = redirect_w(wp);
		char ap[MAX_PATH * 2];
		if (r != wp && WideCharToMultiByte(CP_ACP, 0, r, -1, ap, sizeof ap, NULL, NULL)) return o_fopen(ap, m);
	}
	return o_fopen(p, m);
}

static HANDLE WINAPI h_CreateFileW(LPCWSTR p, DWORD a, DWORD s, LPSECURITY_ATTRIBUTES sa, DWORD c, DWORD f, HANDLE t) {
	return o_CreateFileW(redirect_w(p), a, s, sa, c, f, t);
}

static HANDLE WINAPI h_CreateFileA(LPCSTR p, DWORD a, DWORD s, LPSECURITY_ATTRIBUTES sa, DWORD c, DWORD f, HANDLE t) {
	wchar_t wp[MAX_PATH];
	if (p && MultiByteToWideChar(CP_ACP, 0, p, -1, wp, MAX_PATH) && is_hlboot_w(wp)) {
		const wchar_t *r = redirect_w(wp);
		if (r != wp) return o_CreateFileW(r, a, s, sa, c, f, t);
	}
	return o_CreateFileA(p, a, s, sa, c, f, t);
}

/* Replaces imports by name in one module's import table. */
static int patch_iat(HMODULE mod, const char *fname, void *hook, void **orig) {
	uint8_t *base = (uint8_t *)mod;
	IMAGE_DOS_HEADER *dos = (IMAGE_DOS_HEADER *)base;
	if (dos->e_magic != IMAGE_DOS_SIGNATURE) return 0;
	IMAGE_NT_HEADERS *nt = (IMAGE_NT_HEADERS *)(base + dos->e_lfanew);
	IMAGE_DATA_DIRECTORY dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
	if (!dir.VirtualAddress) return 0;
	int count = 0;
	for (IMAGE_IMPORT_DESCRIPTOR *d = (IMAGE_IMPORT_DESCRIPTOR *)(base + dir.VirtualAddress); d->Name; d++) {
		if (!d->OriginalFirstThunk) continue;
		IMAGE_THUNK_DATA *names = (IMAGE_THUNK_DATA *)(base + d->OriginalFirstThunk);
		IMAGE_THUNK_DATA *iat = (IMAGE_THUNK_DATA *)(base + d->FirstThunk);
		for (; names->u1.AddressOfData; names++, iat++) {
			if (IMAGE_SNAP_BY_ORDINAL(names->u1.Ordinal)) continue;
			IMAGE_IMPORT_BY_NAME *ibn = (IMAGE_IMPORT_BY_NAME *)(base + names->u1.AddressOfData);
			if (strcmp((const char *)ibn->Name, fname) != 0) continue;
			DWORD old;
			if (!VirtualProtect(&iat->u1.Function, sizeof(void *), PAGE_READWRITE, &old)) continue;
			if (!*orig) *orig = (void *)iat->u1.Function;
			iat->u1.Function = (ULONG_PTR)hook;
			VirtualProtect(&iat->u1.Function, sizeof(void *), old, &old);
			count++;
		}
	}
	return count;
}

static void install_hooks(void) {
	HMODULE exe = GetModuleHandleW(NULL);
	int n = 0;
	n += patch_iat(exe, "_wfopen", (void *)h_wfopen, (void **)&o_wfopen);
	n += patch_iat(exe, "fopen", (void *)h_fopen, (void **)&o_fopen);
	n += patch_iat(exe, "CreateFileW", (void *)h_CreateFileW, (void **)&o_CreateFileW);
	n += patch_iat(exe, "CreateFileA", (void *)h_CreateFileA, (void **)&o_CreateFileA);
	/* originals for paths we call without an exe import */
	if (!o_CreateFileW) o_CreateFileW = CreateFileW;
	if (!o_CreateFileA) o_CreateFileA = CreateFileA;
	if (!o_wfopen) o_wfopen = _wfopen;
	if (!o_fopen) o_fopen = fopen;
	llog("DC-Coop loader: %d import(s) hooked, real winmm %s", n, g_real ? "loaded" : "MISSING");
}

BOOL WINAPI DllMain(HINSTANCE inst, DWORD reason, LPVOID reserved) {
	(void)reserved;
	if (reason == DLL_PROCESS_ATTACH) {
		DisableThreadLibraryCalls(inst);
		DWORD n = GetModuleFileNameW(NULL, g_dir, MAX_PATH);
		while (n > 0 && g_dir[n - 1] != L'\\' && g_dir[n - 1] != L'/') n--;
		g_dir[n] = 0;
		resolve_exports();
		/* only act inside a HashLink game; other processes get a plain proxy */
		wchar_t boot[MAX_PATH + 16];
		_snwprintf(boot, MAX_PATH + 16, L"%lshlboot.dat", g_dir);
		if (GetFileAttributesW(boot) != INVALID_FILE_ATTRIBUTES && !GetEnvironmentVariableW(L"DCCOOP_DISABLE", NULL, 0))
			install_hooks();
	} else if (reason == DLL_PROCESS_DETACH && reserved == NULL && g_real) {
		FreeLibrary(g_real);
	}
	return TRUE;
}
