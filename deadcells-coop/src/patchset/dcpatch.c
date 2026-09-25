#include "dcpatch.h"

#include "../hlbc/patch.h"

#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* Built-in table. Class and method names must match the game's bytecode;
 * run hlbc-dump on hlboot.dat to verify them for a given game build. */
static const char *DEFAULT_HOOKS =
	"# native      class       method        mode        signature\n"
	"hook tick         Game        update        notify      o:v\n"
	"hook level_start  Game        onLevelStart  notify      o:v\n"
	"hook hero_update  en.Hero     update        cancel      o:v\n"
	"hook mob_update   en.Mob      update        cancel      o:v\n"
	"hook mob_init     en.Mob      init          exit        o:v\n"
	"hook damage       en.Entity   hit           rewrite:2   ooi:v\n"
	"hook dispose      en.Entity   dispose       notify      o:v\n"
	"spawner en.Hero\n";

static void logf_(dcpatch_log_fn log, void *ud, const char *fmt, ...) {
	if (!log) return;
	char buf[512];
	va_list ap;
	va_start(ap, fmt);
	vsnprintf(buf, sizeof buf, fmt, ap);
	va_end(ap);
	log(ud, buf);
}

static int add_global(hlbc_code *c, int type) {
	int *ng = realloc(c->globals, sizeof(int) * (size_t)(c->nglobals + 1));
	if (!ng) return -1;
	c->globals = ng;
	c->globals[c->nglobals] = type;
	return c->nglobals++;
}

static int add_function(hlbc_code *c, int type, const int *regs, int nregs, const hlbc_op *ops, int nops) {
	hlbc_function *nf = realloc(c->functions, sizeof(hlbc_function) * (size_t)(c->nfunctions + 1));
	if (!nf) return -1;
	c->functions = nf;
	hlbc_function *f = &c->functions[c->nfunctions];
	memset(f, 0, sizeof *f);
	f->type = type;
	f->findex = hlbc_next_findex(c);
	f->nregs = nregs;
	f->regs = malloc(sizeof(int) * (size_t)nregs);
	f->nops = nops;
	f->ops = calloc((size_t)nops, sizeof(hlbc_op));
	if (!f->regs || !f->ops) return -1;
	memcpy(f->regs, regs, sizeof(int) * (size_t)nregs);
	for (int i = 0; i < nops; i++) {
		f->ops[i] = ops[i];
		if (ops[i].nextra) {
			f->ops[i].extra = malloc(sizeof(int) * (size_t)ops[i].nextra);
			memcpy(f->ops[i].extra, ops[i].extra, sizeof(int) * (size_t)ops[i].nextra);
		}
	}
	if (HLBC_HASDEBUG(c)) {
		f->debug = calloc((size_t)nops * 2, sizeof(int));
		if (!f->debug) return -1;
	}
	c->nfunctions++;
	return f->findex;
}

static hlbc_op op3(int op, int p1, int p2, int p3) {
	hlbc_op o = { 0 };
	o.op = op; o.p1 = p1; o.p2 = p2; o.p3 = p3;
	return o;
}

static hlbc_op calln(int dst, int f, int *args, int n) {
	hlbc_op o = op3(OP_CallN, dst, f, n);
	o.nextra = n;
	o.extra = args;
	return o;
}

static int is_ptr_type(const hlbc_code *c, int t) {
	switch (c->types[t].kind) {
	case HLT_VOID: case HLT_UI8: case HLT_UI16: case HLT_I32: case HLT_I64: case HLT_F32: case HLT_F64: case HLT_BOOL:
		return 0;
	default:
		return 1;
	}
}

/*
 * Ghost spawner. Remote players need real hero entities, built by the game's
 * own constructor so sprites, physics and level registration are correct.
 * The constructor's arguments are captured from the last regular hero
 * construction (the local player), and a generated function
 *   $dccoop_spawn() : Hero  { spawning = true; h = new Hero(saved args); spawning = false; ghost_created(h); return h; }
 * is handed to the .hdll as a closure on the first tick.
 */
static int build_spawner(hlbc_code *c, const char *cls, int tick_findex, dcpatch_log_fn log, void *ud) {
	int hero_t = hlbc_find_obj_type(c, cls);
	int ctor = hlbc_find_method(c, cls, "__constructor__");
	hlbc_function *cf = ctor >= 0 ? hlbc_function_by_findex(c, ctor) : NULL;
	if (hero_t < 0 || !cf) { logf_(log, ud, "spawner: %s constructor not found", cls); return -1; }
	if (tick_findex < 0) { logf_(log, ud, "spawner: needs the tick hook"); return -1; }
	hlbc_type ct = c->types[cf->type];
	int nargs = ct.nargs;
	if (nargs < 1 || nargs > 32) return -1;
	int argt[32];
	memcpy(argt, ct.args, sizeof(int) * (size_t)nargs);

	int t_void = hlbc_add_simple_type(c, HLT_VOID), t_bool = hlbc_add_simple_type(c, HLT_BOOL);
	int t_dyn = hlbc_add_simple_type(c, HLT_DYN);
	/* HashLink JITs load globals pointer-sized (Haxe never emits scalar
	 * globals), so flags and scalar args are boxed in dyn globals. */
	int boxed[32], g_args[32];
	for (int i = 1; i < nargs; i++) {
		boxed[i] = !is_ptr_type(c, argt[i]);
		g_args[i] = add_global(c, boxed[i] ? t_dyn : argt[i]);
	}
	int g_spawning = add_global(c, t_dyn), g_registered = add_global(c, t_dyn);

	/* 1) constructor prefix: remember args unless we are the ones spawning */
	{
		hlbc_op ops[80];
		int n = 0, rd = hlbc_add_reg(cf, t_dyn), rbox = hlbc_add_reg(cf, t_dyn);
		ops[n++] = op3(OP_GetGlobal, rd, g_spawning, 0);
		ops[n++] = op3(OP_JNotNull, rd, 0, 0); /* offset patched below */
		int jpos = n - 1;
		for (int i = 1; i < nargs; i++) {
			if (boxed[i]) {
				ops[n++] = op3(OP_ToDyn, rbox, i, 0);
				ops[n++] = op3(OP_SetGlobal, g_args[i], rbox, 0);
			} else {
				ops[n++] = op3(OP_SetGlobal, g_args[i], i, 0);
			}
		}
		ops[jpos].p2 = n - jpos - 1;
		if (hlbc_insert_ops(c, cf, 0, ops, n, 0)) return -1;
	}
	/* 2) the spawn function */
	int created_t = hlbc_add_fun_type(c, &hero_t, 1, t_void);
	int created = hlbc_add_native(c, DCCOOP_LIB, "ghost_created", created_t);
	int spawn_t = hlbc_add_fun_type(c, argt, 0, hero_t);
	int findex;
	{
		/* regs: 0 hero, 1..n-1 ctor args, then dyn, void */
		int regs[36], nregs = 0;
		regs[nregs++] = hero_t;
		for (int i = 1; i < nargs; i++) regs[nregs++] = argt[i];
		int rd = nregs, rv = nregs + 1;
		regs[nregs++] = t_dyn;
		regs[nregs++] = t_void;
		int cargs[32], hargs[1] = { 0 };
		for (int i = 0; i < nargs; i++) cargs[i] = i;
		hlbc_op ops[96];
		int n = 0;
		ops[n++] = op3(OP_New, 0, 0, 0);
		ops[n++] = op3(OP_ToDyn, rd, 0, 0);
		ops[n++] = op3(OP_SetGlobal, g_spawning, rd, 0);
		for (int i = 1; i < nargs; i++) {
			if (boxed[i]) {
				ops[n++] = op3(OP_GetGlobal, rd, g_args[i], 0);
				ops[n++] = op3(OP_SafeCast, i, rd, 0);
			} else {
				ops[n++] = op3(OP_GetGlobal, i, g_args[i], 0);
			}
		}
		ops[n++] = calln(rv, ctor, cargs, nargs);
		ops[n++] = op3(OP_Null, rd, 0, 0);
		ops[n++] = op3(OP_SetGlobal, g_spawning, rd, 0);
		ops[n++] = calln(rv, created, hargs, 1);
		ops[n++] = op3(OP_Ret, 0, 0, 0);
		findex = add_function(c, spawn_t, regs, nregs, ops, n);
		if (findex < 0) return -1;
	}
	/* 3) tick prefix: register the closure once */
	{
		hlbc_function *tf = hlbc_function_by_findex(c, tick_findex);
		int clos_t = spawn_t; /* a static closure has the function's type */
		int set_t = hlbc_add_fun_type(c, &clos_t, 1, t_void);
		int set = hlbc_add_native(c, DCCOOP_LIB, "set_spawner", set_t);
		int rd = hlbc_add_reg(tf, t_dyn), rb = hlbc_add_reg(tf, t_bool);
		int rc = hlbc_add_reg(tf, clos_t), rv = hlbc_add_reg(tf, t_void);
		int sargs[1] = { rc };
		hlbc_op ops[7] = {
			op3(OP_GetGlobal, rd, g_registered, 0),
			op3(OP_JNotNull, rd, 5, 0),
			op3(OP_StaticClosure, rc, findex, 0),
			calln(rv, set, sargs, 1),
			op3(OP_Bool, rb, 1, 0),
			op3(OP_ToDyn, rd, rb, 0),
			op3(OP_SetGlobal, g_registered, rd, 0),
		};
		if (hlbc_insert_ops(c, tf, 0, ops, 7, 0)) return -1;
	}
	logf_(log, ud, "spawner: %s ctor @%d (%d args) -> spawn fn @%d", cls, ctor, nargs - 1, findex);
	return 0;
}

static int parse_mode(const char *s, hlbc_hook_mode *mode, int *arg) {
	*arg = 0;
	if (!strcmp(s, "notify")) *mode = HOOK_ENTRY_NOTIFY;
	else if (!strcmp(s, "cancel")) *mode = HOOK_ENTRY_CANCEL;
	else if (!strcmp(s, "exit")) *mode = HOOK_EXIT;
	else if (!strncmp(s, "rewrite:", 8)) { *mode = HOOK_ENTRY_REWRITE_ARG; *arg = atoi(s + 8); }
	else return -1;
	return 0;
}

int dcpatch_apply(hlbc_code *c, const char *hooks_text, dcpatch_result *res, dcpatch_log_fn log, void *ud) {
	memset(res, 0, sizeof *res);
	if (hlbc_find_string(c, DCCOOP_MARKER) >= 0) { logf_(log, ud, "bytecode already patched"); return 1; }
	const char *text = hooks_text ? hooks_text : DEFAULT_HOOKS;
	int tick_findex = -1;
	char spawner_cls[128] = "";
	const char *p = text;
	while (*p) {
		char line[512];
		size_t n = strcspn(p, "\r\n");
		size_t m = n < sizeof line - 1 ? n : sizeof line - 1;
		memcpy(line, p, m);
		line[m] = 0;
		p += n;
		while (*p == '\r' || *p == '\n') p++;
		char *hash = strchr(line, '#');
		if (hash) *hash = 0;
		char kw[32], native[64], cls[128], meth[128], mode_s[32], sig[64];
		int k = sscanf(line, "%31s %63s %127s %127s %31s %63s", kw, native, cls, meth, mode_s, sig);
		if (k <= 0) continue;
		if (!strcmp(kw, "spawner") && k >= 2) { snprintf(spawner_cls, sizeof spawner_cls, "%s", native); continue; }
		if (strcmp(kw, "hook") || k < 6) { logf_(log, ud, "bad hook line: %s", line); continue; }
		hlbc_hook h = { 0 };
		if (parse_mode(mode_s, &h.mode, &h.arg)) { logf_(log, ud, "bad mode %s", mode_s); res->skipped++; continue; }
		h.target_findex = hlbc_find_method(c, cls, meth);
		if (h.target_findex < 0) { logf_(log, ud, "skip %s: %s.%s not found", native, cls, meth); res->skipped++; continue; }
		if (!hlbc_check_signature(c, h.target_findex, sig)) {
			logf_(log, ud, "skip %s: %s.%s signature differs from %s", native, cls, meth, sig);
			res->skipped++;
			continue;
		}
		h.lib = DCCOOP_LIB;
		h.native = native;
		char err[160] = "";
		if (hlbc_apply_hook(c, &h, err, sizeof err)) {
			logf_(log, ud, "skip %s: %s", native, err);
			res->skipped++;
			continue;
		}
		if (!strcmp(native, "tick")) tick_findex = h.target_findex;
		logf_(log, ud, "hooked %s -> %s.%s @%d", native, cls, meth, h.target_findex);
		res->applied++;
	}
	if (spawner_cls[0]) res->spawner_ok = build_spawner(c, spawner_cls, tick_findex, log, ud) == 0;
	hlbc_add_string(c, DCCOOP_MARKER);
	return 0;
}

int dcpatch_buffer(const uint8_t *in, size_t in_size, const char *hooks_text, uint8_t **out, size_t *out_size,
	dcpatch_log_fn log, void *ud) {
	const char *err = NULL;
	hlbc_code *c = hlbc_read(in, in_size, &err);
	if (!c) { logf_(log, ud, "bytecode parse failed: %s", err); return -1; }
	dcpatch_result r;
	int rc = dcpatch_apply(c, hooks_text, &r, log, ud);
	if (rc == 0) {
		logf_(log, ud, "patch: %d hooks applied, %d skipped, spawner %s", r.applied, r.skipped, r.spawner_ok ? "ok" : "off");
		rc = hlbc_write(c, out, out_size);
	}
	hlbc_free(c);
	return rc;
}
