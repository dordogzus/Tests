#include "patch.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

int hlbc_add_reg(hlbc_function *f, int type) {
	int *nr = realloc(f->regs, sizeof(int) * (size_t)(f->nregs + 1));
	if (!nr) return -1;
	f->regs = nr;
	f->regs[f->nregs] = type;
	return f->nregs++;
}

/* Pointers to every relative jump offset stored in an op (max: switch). */
static int op_jumps(hlbc_op *o, int **out, int max) {
	int n = 0;
	switch (o->op) {
	case OP_JTrue: case OP_JFalse: case OP_JNull: case OP_JNotNull: case OP_Trap:
		out[n++] = &o->p2; break;
	case OP_JSLt: case OP_JSGte: case OP_JSGt: case OP_JSLte: case OP_JULt: case OP_JUGte:
	case OP_JNotLt: case OP_JNotGte: case OP_JEq: case OP_JNotEq:
		out[n++] = &o->p3; break;
	case OP_JAlways:
		out[n++] = &o->p1; break;
	case OP_Switch:
		for (int i = 0; i < o->nextra && n < max - 1; i++) out[n++] = &o->extra[i];
		out[n++] = &o->p3;
		break;
	default: break;
	}
	return n;
}

static int remap(int old, int pos, int n, int to_inserted) {
	if (old < pos) return old;
	if (old == pos && to_inserted) return pos;
	return old + n;
}

int hlbc_insert_ops(hlbc_code *c, hlbc_function *f, int pos, const hlbc_op *ops, int n, int to_inserted) {
	if (pos < 0 || pos > f->nops || n <= 0) return -1;
	/* fix relative jumps before moving anything */
	for (int i = 0; i < f->nops; i++) {
		int *js[512];
		hlbc_op *o = &f->ops[i];
		int nj = op_jumps(o, js, 512);
		if (nj == 0) continue;
		if (o->op == OP_Switch && o->nextra + 1 > 512) return -1;
		int ni = remap(i, pos, n, 0);
		for (int k = 0; k < nj; k++) {
			/* switch offsets of 0 mean "no case" and must stay 0 */
			if (o->op == OP_Switch && js[k] != &o->p3 && *js[k] == 0) continue;
			int target = i + 1 + *js[k];
			*js[k] = remap(target, pos, n, to_inserted) - (ni + 1);
		}
	}
	hlbc_op *nops = realloc(f->ops, sizeof(hlbc_op) * (size_t)(f->nops + n));
	if (!nops) return -1;
	f->ops = nops;
	memmove(&f->ops[pos + n], &f->ops[pos], sizeof(hlbc_op) * (size_t)(f->nops - pos));
	for (int i = 0; i < n; i++) {
		f->ops[pos + i] = ops[i];
		f->ops[pos + i].extra = NULL;
		if (ops[i].nextra) {
			f->ops[pos + i].extra = malloc(sizeof(int) * (size_t)ops[i].nextra);
			if (!f->ops[pos + i].extra) return -1;
			memcpy(f->ops[pos + i].extra, ops[i].extra, sizeof(int) * (size_t)ops[i].nextra);
		}
	}
	if (HLBC_HASDEBUG(c) && f->debug) {
		int *nd = realloc(f->debug, sizeof(int) * 2 * (size_t)(f->nops + n));
		if (!nd) return -1;
		f->debug = nd;
		int src = pos < f->nops ? pos : pos - 1;
		int file = src >= 0 ? nd[src * 2] : 0, line = src >= 0 ? nd[src * 2 + 1] : 0;
		memmove(&nd[(pos + n) * 2], &nd[pos * 2], sizeof(int) * 2 * (size_t)(f->nops - pos));
		for (int i = 0; i < n; i++) { nd[(pos + i) * 2] = file; nd[(pos + i) * 2 + 1] = line; }
	}
	for (int i = 0; i < f->nassigns; i++) {
		for (int k = 1; k <= 2; k++) {
			int *v = &f->assigns[i * 3 + k];
			if (*v >= 0) *v = remap(*v, pos, n, 0);
		}
	}
	f->nops += n;
	return 0;
}

static char kind_letter(const hlbc_code *c, int t) {
	if (t < 0 || t >= c->ntypes) return '?';
	switch (c->types[t].kind) {
	case HLT_VOID: return 'v';
	case HLT_I32: return 'i';
	case HLT_F64: return 'f';
	case HLT_F32: return 's';
	case HLT_BOOL: return 'b';
	case HLT_UI8: case HLT_UI16: case HLT_I64: return '?';
	default: return 'o';
	}
}

int hlbc_check_signature(const hlbc_code *c, int findex, const char *sig) {
	int ft = hlbc_findex_type((hlbc_code *)c, findex);
	if (ft < 0) return 0;
	const hlbc_type *t = &c->types[ft];
	int i = 0;
	const char *p = sig;
	for (; *p && *p != ':'; p++, i++) {
		if (*p == '*') { while (*p && *p != ':') p++; i = t->nargs; break; }
		if (i >= t->nargs || kind_letter(c, t->args[i]) != *p) return 0;
	}
	if (i != t->nargs) return 0;
	if (*p == ':' && p[1] && kind_letter(c, t->ret) != p[1]) return 0;
	return 1;
}

static hlbc_op make_calln(int dst, int findex, const int *args, int nargs) {
	hlbc_op o = { 0 };
	o.op = OP_CallN;
	o.p1 = dst;
	o.p2 = findex;
	o.p3 = nargs;
	o.nextra = nargs;
	o.extra = (int *)args;
	return o;
}

int hlbc_apply_hook(hlbc_code *c, const hlbc_hook *h, char *err, size_t errlen) {
	hlbc_function *f = hlbc_function_by_findex(c, h->target_findex);
	if (!f) { snprintf(err, errlen, "findex %d is not a bytecode function", h->target_findex); return -1; }
	/* copy: adding types may realloc c->types */
	hlbc_type ft = c->types[f->type];
	if (ft.kind != HLT_FUN && ft.kind != HLT_METHOD) { snprintf(err, errlen, "target is not a function"); return -1; }
	int nargs = ft.nargs, ret_t = ft.ret;
	if (nargs > 250) { snprintf(err, errlen, "too many arguments"); return -1; }
	int *targs = malloc(sizeof(int) * (size_t)(nargs + 1));
	int *regs = malloc(sizeof(int) * (size_t)(nargs + 1));
	if (!targs || !regs) { free(targs); free(regs); snprintf(err, errlen, "out of memory"); return -1; }
	memcpy(targs, ft.args, sizeof(int) * (size_t)nargs);
	for (int i = 0; i < nargs; i++) regs[i] = i;

	int rc = -1;
	int t_void = hlbc_add_simple_type(c, HLT_VOID);
	int ret_is_void = c->types[ret_t].kind == HLT_VOID;

	switch (h->mode) {
	case HOOK_ENTRY_NOTIFY: {
		int nt = hlbc_add_fun_type(c, targs, nargs, t_void);
		int nf = hlbc_add_native(c, h->lib, h->native, nt);
		int rv = hlbc_add_reg(f, t_void);
		hlbc_op op = make_calln(rv, nf, regs, nargs);
		rc = hlbc_insert_ops(c, f, 0, &op, 1, 0);
		break;
	}
	case HOOK_ENTRY_REWRITE_ARG: {
		if (h->arg < 0 || h->arg >= nargs) { snprintf(err, errlen, "bad arg index %d", h->arg); break; }
		int nt = hlbc_add_fun_type(c, targs, nargs, targs[h->arg]);
		int nf = hlbc_add_native(c, h->lib, h->native, nt);
		hlbc_op op = make_calln(h->arg, nf, regs, nargs);
		rc = hlbc_insert_ops(c, f, 0, &op, 1, 0);
		break;
	}
	case HOOK_ENTRY_CANCEL: {
		if (!ret_is_void) { snprintf(err, errlen, "cancel hooks need a void function"); break; }
		int t_bool = hlbc_add_simple_type(c, HLT_BOOL);
		int nt = hlbc_add_fun_type(c, targs, nargs, t_bool);
		int nf = hlbc_add_native(c, h->lib, h->native, nt);
		int rb = hlbc_add_reg(f, t_bool), rv = hlbc_add_reg(f, t_void);
		hlbc_op ops[3] = { make_calln(rb, nf, regs, nargs), { 0 }, { 0 } };
		ops[1].op = OP_JFalse; ops[1].p1 = rb; ops[1].p2 = 1;
		ops[2].op = OP_Ret; ops[2].p1 = rv;
		rc = hlbc_insert_ops(c, f, 0, ops, 3, 0);
		break;
	}
	case HOOK_EXIT: {
		int nt, nf, rv = -1;
		if (ret_is_void) {
			nt = hlbc_add_fun_type(c, targs, nargs, t_void);
			rv = hlbc_add_reg(f, t_void);
		} else {
			targs[nargs] = ret_t;
			nt = hlbc_add_fun_type(c, targs, nargs + 1, ret_t);
		}
		nf = hlbc_add_native(c, h->lib, h->native, nt);
		rc = 0;
		for (int i = 0; i < f->nops && rc == 0; i++) {
			if (f->ops[i].op != OP_Ret) continue;
			hlbc_op op;
			if (ret_is_void) {
				op = make_calln(rv, nf, regs, nargs);
			} else {
				regs[nargs] = f->ops[i].p1;
				op = make_calln(f->ops[i].p1, nf, regs, nargs + 1);
			}
			rc = hlbc_insert_ops(c, f, i, &op, 1, 1);
			i++; /* skip the Ret we just moved */
		}
		break;
	}
	}
	if (rc && !err[0]) snprintf(err, errlen, "failed to insert hook");
	free(targs);
	free(regs);
	return rc;
}
