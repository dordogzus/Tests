#include "hlbc.h"

#include <stdlib.h>
#include <string.h>

/* ------------------------------------------------------------------ */
/* opcode table (argument classes copied from HashLink's opcodes.h)    */

#define X 0
#define R 1
#define R_NW 2
#define C 3
#define G 4
#define AR 5
#define J 6
#define VAR_ARGS -1
#define NARGS(a, b, c) ((b) == AR ? (c) : ((c) == X ? ((b) == X ? ((a) == X ? 0 : 1) : 2) : 3))

static const struct { const char *name; int nargs; } op_table[OP_LAST] = {
#define OP(n, a, b, c) { #n, NARGS(a, b, c) },
	OP(OMov,R,R,X) OP(OInt,R,G,X) OP(OFloat,R,G,X) OP(OBool,R,C,X) OP(OBytes,R,G,X)
	OP(OString,R,G,X) OP(ONull,R,X,X) OP(OAdd,R,R,R) OP(OSub,R,R,R) OP(OMul,R,R,R)
	OP(OSDiv,R,R,R) OP(OUDiv,R,R,R) OP(OSMod,R,R,R) OP(OUMod,R,R,R) OP(OShl,R,R,R)
	OP(OSShr,R,R,R) OP(OUShr,R,R,R) OP(OAnd,R,R,R) OP(OOr,R,R,R) OP(OXor,R,R,R)
	OP(ONeg,R,R,X) OP(ONot,R,R,X) OP(OIncr,R,X,X) OP(ODecr,R,X,X) OP(OCall0,R,C,X)
	OP(OCall1,R,C,R) OP(OCall2,R,AR,4) OP(OCall3,R,AR,5) OP(OCall4,R,AR,6)
	OP(OCallN,R,AR,VAR_ARGS) OP(OCallMethod,R,AR,VAR_ARGS) OP(OCallThis,R,AR,VAR_ARGS)
	OP(OCallClosure,R,AR,VAR_ARGS) OP(OStaticClosure,R,G,X) OP(OInstanceClosure,R,C,R)
	OP(OVirtualClosure,R,R,G) OP(OGetGlobal,R,G,X) OP(OSetGlobal,G,R,X) OP(OField,R,R,G)
	OP(OSetField,R_NW,G,R) OP(OGetThis,R,G,X) OP(OSetThis,G,R,X) OP(ODynGet,R,R,C)
	OP(ODynSet,R_NW,C,R) OP(OJTrue,R_NW,J,X) OP(OJFalse,R_NW,J,X) OP(OJNull,R_NW,J,X)
	OP(OJNotNull,R_NW,J,X) OP(OJSLt,R_NW,R,J) OP(OJSGte,R_NW,R,J) OP(OJSGt,R_NW,R,J)
	OP(OJSLte,R_NW,R,J) OP(OJULt,R_NW,R,J) OP(OJUGte,R_NW,R,J) OP(OJNotLt,R_NW,R,J)
	OP(OJNotGte,R_NW,R,J) OP(OJEq,R_NW,R,J) OP(OJNotEq,R_NW,R,J) OP(OJAlways,J,X,X)
	OP(OToDyn,R,R,X) OP(OToSFloat,R,R,X) OP(OToUFloat,R,R,X) OP(OToInt,R,R,X)
	OP(OSafeCast,R,R,X) OP(OUnsafeCast,R,R,X) OP(OToVirtual,R,R,X) OP(OLabel,X,X,X)
	OP(ORet,R_NW,X,X) OP(OThrow,R_NW,X,X) OP(ORethrow,R_NW,X,X) OP(OSwitch,R_NW,AR,VAR_ARGS)
	OP(ONullCheck,R_NW,X,X) OP(OTrap,R_NW,J,X) OP(OEndTrap,R,X,X) OP(OGetI8,R,R,R)
	OP(OGetI16,R,R,R) OP(OGetMem,R,R,R) OP(OGetArray,R,R,R) OP(OSetI8,R,R,R)
	OP(OSetI16,R,R,R) OP(OSetMem,R,R,R) OP(OSetArray,R,R,R) OP(ONew,R,X,X)
	OP(OArraySize,R,R,X) OP(OType,R,G,X) OP(OGetType,R,R,X) OP(OGetTID,R,R,X)
	OP(ORef,R,R,X) OP(OUnref,R,R,X) OP(OSetref,R_NW,R,X) OP(OMakeEnum,R,AR,VAR_ARGS)
	OP(OEnumAlloc,R,R,X) OP(OEnumIndex,R,R,X) OP(OEnumField,R,AR,4) OP(OSetEnumField,R_NW,R,C)
	OP(OAssert,X,X,X) OP(ORefData,R,R,X) OP(ORefOffset,R,R,C) OP(ONop,X,X,X)
	OP(OPrefetch,R_NW,C,C) OP(OAsm,C,C,C) OP(OCatch,J,X,X)
#undef OP
};

const char *hlbc_op_name(int op) { return op >= 0 && op < OP_LAST ? op_table[op].name : "?"; }
int hlbc_op_nargs(int op) { return op >= 0 && op < OP_LAST ? op_table[op].nargs : 0; }

/* ------------------------------------------------------------------ */
/* reader                                                              */

typedef struct {
	const uint8_t *b;
	size_t size, pos;
	const char *err;
} rd;

#define FAIL(r, m) do { if (!(r)->err) (r)->err = (m); } while (0)

static int rd_b(rd *r) {
	if (r->pos >= r->size) { FAIL(r, "unexpected end of data"); return 0; }
	return r->b[r->pos++];
}

static int32_t rd_i32(rd *r) {
	if (r->pos + 4 > r->size) { FAIL(r, "unexpected end of data"); return 0; }
	const uint8_t *p = r->b + r->pos;
	r->pos += 4;
	return (int32_t)((uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24));
}

static int rd_index(rd *r) {
	int b = rd_b(r);
	if ((b & 0x80) == 0) return b & 0x7F;
	if ((b & 0x40) == 0) {
		int v = rd_b(r) | ((b & 31) << 8);
		return (b & 0x20) == 0 ? v : -v;
	}
	int c = rd_b(r), d = rd_b(r), e = rd_b(r);
	int v = ((b & 31) << 24) | (c << 16) | (d << 8) | e;
	return (b & 0x20) == 0 ? v : -v;
}

static int rd_uindex(rd *r) {
	int i = rd_index(r);
	if (i < 0) { FAIL(r, "negative index"); return 0; }
	return i;
}

/* Guards allocation sizes derived from untrusted counts. */
static void *rd_alloc(rd *r, size_t count, size_t elem) {
	if (r->err) return NULL;
	if (count > r->size * 8 + 64) { FAIL(r, "count too large"); return NULL; }
	void *p = calloc(count ? count : 1, elem);
	if (!p) FAIL(r, "out of memory");
	return p;
}

static char **rd_strings(rd *r, int n, int **lens_out) {
	int size = rd_i32(r);
	if (r->err) return NULL;
	if (size < 0 || r->pos + (size_t)size > r->size) { FAIL(r, "bad string block"); return NULL; }
	char *base = malloc((size_t)size + 1);
	char **strs = rd_alloc(r, (size_t)n, sizeof(char *));
	int *lens = rd_alloc(r, (size_t)n, sizeof(int));
	if (!base || !strs || !lens) { free(base); free(strs); free(lens); FAIL(r, "out of memory"); return NULL; }
	memcpy(base, r->b + r->pos, (size_t)size);
	base[size] = 0;
	r->pos += (size_t)size;
	int off = 0;
	for (int i = 0; i < n && !r->err; i++) {
		int sz = rd_uindex(r);
		if (off + sz >= size || base[off + sz] != 0) { FAIL(r, "invalid string"); break; }
		strs[i] = malloc((size_t)sz + 1);
		memcpy(strs[i], base + off, (size_t)sz + 1);
		lens[i] = sz;
		off += sz + 1;
	}
	free(base);
	*lens_out = lens;
	return strs;
}

static void rd_type(rd *r, hlbc_type *t) {
	t->kind = rd_b(r);
	t->super = -1;
	switch (t->kind) {
	case HLT_FUN: case HLT_METHOD:
		t->nargs = rd_b(r);
		t->args = rd_alloc(r, (size_t)t->nargs, sizeof(int));
		for (int i = 0; i < t->nargs && !r->err; i++) t->args[i] = rd_index(r);
		t->ret = rd_index(r);
		break;
	case HLT_OBJ: case HLT_STRUCT:
		t->name = rd_index(r);
		t->super = rd_index(r);
		t->global = rd_uindex(r);
		t->nfields = rd_uindex(r);
		t->nproto = rd_uindex(r);
		t->nbindings = rd_uindex(r);
		t->fields = rd_alloc(r, (size_t)t->nfields, sizeof(hlbc_field));
		t->proto = rd_alloc(r, (size_t)t->nproto, sizeof(hlbc_proto));
		t->bindings = rd_alloc(r, (size_t)t->nbindings * 2, sizeof(int));
		for (int i = 0; i < t->nfields && !r->err; i++) {
			t->fields[i].name = rd_index(r);
			t->fields[i].type = rd_index(r);
		}
		for (int i = 0; i < t->nproto && !r->err; i++) {
			t->proto[i].name = rd_index(r);
			t->proto[i].findex = rd_uindex(r);
			t->proto[i].pindex = rd_index(r);
		}
		for (int i = 0; i < t->nbindings * 2 && !r->err; i++) t->bindings[i] = rd_uindex(r);
		break;
	case HLT_VIRTUAL:
		t->nfields = rd_uindex(r);
		t->fields = rd_alloc(r, (size_t)t->nfields, sizeof(hlbc_field));
		for (int i = 0; i < t->nfields && !r->err; i++) {
			t->fields[i].name = rd_index(r);
			t->fields[i].type = rd_index(r);
		}
		break;
	case HLT_ABSTRACT:
		t->name = rd_index(r);
		break;
	case HLT_ENUM:
		t->name = rd_index(r);
		t->global = rd_uindex(r);
		t->nconstructs = rd_uindex(r);
		t->constructs = rd_alloc(r, (size_t)t->nconstructs, sizeof(hlbc_construct));
		for (int i = 0; i < t->nconstructs && !r->err; i++) {
			hlbc_construct *k = &t->constructs[i];
			k->name = rd_index(r);
			k->nparams = rd_uindex(r);
			k->params = rd_alloc(r, (size_t)k->nparams, sizeof(int));
			for (int j = 0; j < k->nparams && !r->err; j++) k->params[j] = rd_index(r);
		}
		break;
	case HLT_REF: case HLT_NULL: case HLT_PACKED:
		t->tparam = rd_index(r);
		break;
	default:
		if (t->kind >= HLT_LAST) FAIL(r, "invalid type kind");
		break;
	}
}

static void rd_op(rd *r, hlbc_op *o) {
	o->op = rd_b(r);
	if (o->op >= OP_LAST) { FAIL(r, "invalid opcode"); return; }
	int n = op_table[o->op].nargs;
	switch (n) {
	case 0: break;
	case 1: o->p1 = rd_index(r); break;
	case 2: o->p1 = rd_index(r); o->p2 = rd_index(r); break;
	case 3: o->p1 = rd_index(r); o->p2 = rd_index(r); o->p3 = rd_index(r); break;
	case -1:
		if (o->op == OP_Switch) {
			o->p1 = rd_uindex(r);
			o->p2 = rd_uindex(r);
			o->nextra = o->p2;
			o->extra = rd_alloc(r, (size_t)o->nextra, sizeof(int));
			for (int i = 0; i < o->nextra && !r->err; i++) o->extra[i] = rd_uindex(r);
			o->p3 = rd_uindex(r);
		} else {
			o->p1 = rd_index(r);
			o->p2 = rd_index(r);
			o->p3 = rd_b(r);
			o->nextra = o->p3;
			o->extra = rd_alloc(r, (size_t)o->nextra, sizeof(int));
			for (int i = 0; i < o->nextra && !r->err; i++) o->extra[i] = rd_index(r);
		}
		break;
	default: /* fixed 4..6: p1 p2 p3 + (n-3) extra */
		o->p1 = rd_index(r);
		o->p2 = rd_index(r);
		o->p3 = rd_index(r);
		o->nextra = n - 3;
		o->extra = rd_alloc(r, (size_t)o->nextra, sizeof(int));
		for (int i = 0; i < o->nextra && !r->err; i++) o->extra[i] = rd_index(r);
		break;
	}
}

static int *rd_debug(rd *r, int nops, int ndebugfiles) {
	int curfile = -1, curline = 0, i = 0;
	int *dbg = rd_alloc(r, (size_t)nops * 2, sizeof(int));
	while (i < nops && !r->err) {
		int c = rd_b(r);
		if (c & 1) {
			c >>= 1;
			curfile = (c << 8) | rd_b(r);
			if (curfile >= ndebugfiles) FAIL(r, "invalid debug file");
		} else if (c & 2) {
			int delta = c >> 6, count = (c >> 2) & 15;
			if (i + count > nops) { FAIL(r, "debug info out of range"); break; }
			while (count--) { dbg[i * 2] = curfile; dbg[i * 2 + 1] = curline; i++; }
			curline += delta;
		} else if (c & 4) {
			curline += c >> 3;
			dbg[i * 2] = curfile; dbg[i * 2 + 1] = curline; i++;
		} else {
			int b2 = rd_b(r), b3 = rd_b(r);
			curline = (c >> 3) | (b2 << 5) | (b3 << 13);
			dbg[i * 2] = curfile; dbg[i * 2 + 1] = curline; i++;
		}
	}
	return dbg;
}

hlbc_code *hlbc_read(const uint8_t *data, size_t size, const char **err) {
	rd rr = { data, size, 0, NULL }, *r = &rr;
	hlbc_code *c = calloc(1, sizeof(hlbc_code));
	if (!c) { *err = "out of memory"; return NULL; }
	if (rd_b(r) != 'H' || rd_b(r) != 'L' || rd_b(r) != 'B') { *err = "not a HashLink bytecode file"; free(c); return NULL; }
	c->version = rd_b(r);
	if (c->version < 2 || c->version > 6) { *err = "unsupported bytecode version"; free(c); return NULL; }
	c->flags = rd_uindex(r);
	c->nints = rd_uindex(r);
	c->nfloats = rd_uindex(r);
	c->nstrings = rd_uindex(r);
	if (c->version >= 5) c->nbytes = rd_uindex(r);
	c->ntypes = rd_uindex(r);
	c->nglobals = rd_uindex(r);
	c->nnatives = rd_uindex(r);
	c->nfunctions = rd_uindex(r);
	c->nconstants = c->version >= 4 ? rd_uindex(r) : 0;
	c->entrypoint = rd_uindex(r);

	c->ints = rd_alloc(r, (size_t)c->nints, sizeof(int32_t));
	for (int i = 0; i < c->nints && !r->err; i++) c->ints[i] = rd_i32(r);
	c->floats = rd_alloc(r, (size_t)c->nfloats, sizeof(double));
	for (int i = 0; i < c->nfloats && !r->err; i++) {
		if (r->pos + 8 > r->size) { FAIL(r, "unexpected end of data"); break; }
		memcpy(&c->floats[i], r->b + r->pos, 8);
		r->pos += 8;
	}
	if (!r->err) c->strings = rd_strings(r, c->nstrings, &c->strings_lens);
	if (c->version >= 5 && !r->err) {
		c->bytes_size = rd_i32(r);
		if (c->bytes_size < 0 || r->pos + (size_t)c->bytes_size > r->size) FAIL(r, "bad bytes block");
		c->bytes = rd_alloc(r, (size_t)c->bytes_size, 1);
		if (!r->err) { memcpy(c->bytes, r->b + r->pos, (size_t)c->bytes_size); r->pos += (size_t)c->bytes_size; }
		c->bytes_pos = rd_alloc(r, (size_t)c->nbytes, sizeof(int));
		for (int i = 0; i < c->nbytes && !r->err; i++) c->bytes_pos[i] = rd_uindex(r);
	}
	if (HLBC_HASDEBUG(c) && !r->err) {
		c->ndebugfiles = rd_uindex(r);
		c->debugfiles = rd_strings(r, c->ndebugfiles, &c->debugfiles_lens);
	}
	c->types = rd_alloc(r, (size_t)c->ntypes, sizeof(hlbc_type));
	for (int i = 0; i < c->ntypes && !r->err; i++) rd_type(r, &c->types[i]);
	c->globals = rd_alloc(r, (size_t)c->nglobals, sizeof(int));
	for (int i = 0; i < c->nglobals && !r->err; i++) c->globals[i] = rd_index(r);
	c->natives = rd_alloc(r, (size_t)c->nnatives, sizeof(hlbc_native));
	for (int i = 0; i < c->nnatives && !r->err; i++) {
		hlbc_native *n = &c->natives[i];
		n->lib = rd_index(r);
		n->name = rd_index(r);
		n->type = rd_index(r);
		n->findex = rd_uindex(r);
	}
	c->functions = rd_alloc(r, (size_t)c->nfunctions, sizeof(hlbc_function));
	for (int i = 0; i < c->nfunctions && !r->err; i++) {
		hlbc_function *f = &c->functions[i];
		f->type = rd_index(r);
		f->findex = rd_uindex(r);
		f->nregs = rd_uindex(r);
		f->nops = rd_uindex(r);
		f->regs = rd_alloc(r, (size_t)f->nregs, sizeof(int));
		for (int j = 0; j < f->nregs && !r->err; j++) f->regs[j] = rd_index(r);
		f->ops = rd_alloc(r, (size_t)f->nops, sizeof(hlbc_op));
		for (int j = 0; j < f->nops && !r->err; j++) rd_op(r, &f->ops[j]);
		if (HLBC_HASDEBUG(c) && !r->err) {
			f->debug = rd_debug(r, f->nops, c->ndebugfiles);
			if (c->version >= 3) {
				f->nassigns = rd_uindex(r);
				f->assigns = rd_alloc(r, (size_t)f->nassigns * 3, sizeof(int));
				for (int j = 0; j < f->nassigns && !r->err; j++) {
					f->assigns[j * 3] = rd_uindex(r);
					f->assigns[j * 3 + 1] = rd_index(r) - 1;
					f->assigns[j * 3 + 2] = c->version >= 6 ? rd_index(r) - 1 : -1;
				}
			}
		}
	}
	c->constants = rd_alloc(r, (size_t)c->nconstants, sizeof(hlbc_constant));
	for (int i = 0; i < c->nconstants && !r->err; i++) {
		hlbc_constant *k = &c->constants[i];
		k->global = rd_uindex(r);
		k->nfields = rd_uindex(r);
		k->fields = rd_alloc(r, (size_t)k->nfields, sizeof(int));
		for (int j = 0; j < k->nfields && !r->err; j++) k->fields[j] = rd_uindex(r);
	}
	if (r->err) {
		*err = r->err;
		hlbc_free(c);
		return NULL;
	}
	return c;
}

void hlbc_free(hlbc_code *c) {
	if (!c) return;
	free(c->ints);
	free(c->floats);
	for (int i = 0; c->strings && i < c->nstrings; i++) free(c->strings[i]);
	free(c->strings); free(c->strings_lens);
	free(c->bytes); free(c->bytes_pos);
	for (int i = 0; c->debugfiles && i < c->ndebugfiles; i++) free(c->debugfiles[i]);
	free(c->debugfiles); free(c->debugfiles_lens);
	for (int i = 0; c->types && i < c->ntypes; i++) {
		hlbc_type *t = &c->types[i];
		free(t->args); free(t->fields); free(t->proto); free(t->bindings);
		for (int j = 0; t->constructs && j < t->nconstructs; j++) free(t->constructs[j].params);
		free(t->constructs);
	}
	free(c->types);
	free(c->globals);
	free(c->natives);
	for (int i = 0; c->functions && i < c->nfunctions; i++) {
		hlbc_function *f = &c->functions[i];
		for (int j = 0; f->ops && j < f->nops; j++) free(f->ops[j].extra);
		free(f->regs); free(f->ops); free(f->debug); free(f->assigns);
	}
	free(c->functions);
	for (int i = 0; c->constants && i < c->nconstants; i++) free(c->constants[i].fields);
	free(c->constants);
	free(c);
}

/* ------------------------------------------------------------------ */
/* writer (encodings match Haxe's genhl so unmodified files round-trip) */

typedef struct { uint8_t *b; size_t len, cap; int oom; } wr;

static void w_byte(wr *w, int v) {
	if (w->len == w->cap) {
		size_t nc = w->cap ? w->cap * 2 : 1 << 16;
		uint8_t *nb = realloc(w->b, nc);
		if (!nb) { w->oom = 1; return; }
		w->b = nb; w->cap = nc;
	}
	w->b[w->len++] = (uint8_t)v;
}

static void w_raw(wr *w, const void *p, size_t n) {
	for (size_t i = 0; i < n; i++) w_byte(w, ((const uint8_t *)p)[i]);
}

static void w_i32(wr *w, int32_t v) {
	uint32_t u = (uint32_t)v;
	w_byte(w, u & 0xFF); w_byte(w, (u >> 8) & 0xFF); w_byte(w, (u >> 16) & 0xFF); w_byte(w, u >> 24);
}

static void w_index(wr *w, int i) {
	if (i < 0) {
		unsigned v = (unsigned)-i;
		if (v < 0x2000) { w_byte(w, (int)((v >> 8) | 0xA0)); w_byte(w, v & 0xFF); }
		else { w_byte(w, (int)((v >> 24) | 0xE0)); w_byte(w, (v >> 16) & 0xFF); w_byte(w, (v >> 8) & 0xFF); w_byte(w, v & 0xFF); }
	} else if (i < 0x80) {
		w_byte(w, i);
	} else if (i < 0x2000) {
		w_byte(w, (i >> 8) | 0x80); w_byte(w, i & 0xFF);
	} else {
		w_byte(w, (i >> 24) | 0xC0); w_byte(w, (i >> 16) & 0xFF); w_byte(w, (i >> 8) & 0xFF); w_byte(w, i & 0xFF);
	}
}

static void w_strings(wr *w, char **strs, const int *lens, int n) {
	int32_t total = 0;
	for (int i = 0; i < n; i++) total += lens[i] + 1;
	w_i32(w, total);
	for (int i = 0; i < n; i++) w_raw(w, strs[i], (size_t)lens[i] + 1);
	for (int i = 0; i < n; i++) w_index(w, lens[i]);
}

static void w_type(wr *w, const hlbc_type *t) {
	w_byte(w, t->kind);
	switch (t->kind) {
	case HLT_FUN: case HLT_METHOD:
		w_byte(w, t->nargs);
		for (int i = 0; i < t->nargs; i++) w_index(w, t->args[i]);
		w_index(w, t->ret);
		break;
	case HLT_OBJ: case HLT_STRUCT:
		w_index(w, t->name); w_index(w, t->super); w_index(w, t->global);
		w_index(w, t->nfields); w_index(w, t->nproto); w_index(w, t->nbindings);
		for (int i = 0; i < t->nfields; i++) { w_index(w, t->fields[i].name); w_index(w, t->fields[i].type); }
		for (int i = 0; i < t->nproto; i++) {
			w_index(w, t->proto[i].name); w_index(w, t->proto[i].findex); w_index(w, t->proto[i].pindex);
		}
		for (int i = 0; i < t->nbindings * 2; i++) w_index(w, t->bindings[i]);
		break;
	case HLT_VIRTUAL:
		w_index(w, t->nfields);
		for (int i = 0; i < t->nfields; i++) { w_index(w, t->fields[i].name); w_index(w, t->fields[i].type); }
		break;
	case HLT_ABSTRACT:
		w_index(w, t->name);
		break;
	case HLT_ENUM:
		w_index(w, t->name); w_index(w, t->global); w_index(w, t->nconstructs);
		for (int i = 0; i < t->nconstructs; i++) {
			const hlbc_construct *k = &t->constructs[i];
			w_index(w, k->name); w_index(w, k->nparams);
			for (int j = 0; j < k->nparams; j++) w_index(w, k->params[j]);
		}
		break;
	case HLT_REF: case HLT_NULL: case HLT_PACKED:
		w_index(w, t->tparam);
		break;
	default: break;
	}
}

static void w_op(wr *w, const hlbc_op *o) {
	w_byte(w, o->op);
	int n = op_table[o->op].nargs;
	switch (n) {
	case 0: break;
	case 1: w_index(w, o->p1); break;
	case 2: w_index(w, o->p1); w_index(w, o->p2); break;
	case 3: w_index(w, o->p1); w_index(w, o->p2); w_index(w, o->p3); break;
	case -1:
		if (o->op == OP_Switch) {
			w_index(w, o->p1); w_index(w, o->nextra);
			for (int i = 0; i < o->nextra; i++) w_index(w, o->extra[i]);
			w_index(w, o->p3);
		} else {
			w_index(w, o->p1); w_index(w, o->p2); w_byte(w, o->nextra);
			for (int i = 0; i < o->nextra; i++) w_index(w, o->extra[i]);
		}
		break;
	default:
		w_index(w, o->p1); w_index(w, o->p2); w_index(w, o->p3);
		for (int i = 0; i < n - 3; i++) w_index(w, o->extra[i]);
		break;
	}
}

static void w_debug(wr *w, const int *dbg, int nops) {
	int curfile = -1, curpos = 0, rcount = 0;
#define FLUSH_REPEAT(p) do { \
		while (rcount > 15) { w_byte(w, (15 << 2) | 2); rcount -= 15; } \
		if (rcount > 0) { \
			int delta_ = (p) - curpos; \
			if (!(delta_ > 0 && delta_ < 4)) delta_ = 0; \
			w_byte(w, (delta_ << 6) | (rcount << 2) | 2); \
			rcount = 0; curpos += delta_; \
		} \
	} while (0)
	for (int i = 0; i < nops; i++) {
		int f = dbg[i * 2], p = dbg[i * 2 + 1];
		if (f != curfile) {
			FLUSH_REPEAT(p);
			curfile = f;
			w_byte(w, ((f >> 7) | 1) & 0xFF);
			w_byte(w, f & 0xFF);
		}
		if (p != curpos) FLUSH_REPEAT(p);
		if (p == curpos) {
			rcount++;
		} else {
			int delta = p - curpos;
			if (delta > 0 && delta < 32) {
				w_byte(w, (delta << 3) | 4);
			} else {
				w_byte(w, (p << 3) & 0xFF);
				w_byte(w, (p >> 5) & 0xFF);
				w_byte(w, (p >> 13) & 0xFF);
			}
			curpos = p;
		}
	}
	FLUSH_REPEAT(curpos);
#undef FLUSH_REPEAT
}

int hlbc_write(const hlbc_code *c, uint8_t **out, size_t *out_size) {
	wr ww = { 0 }, *w = &ww;
	w_byte(w, 'H'); w_byte(w, 'L'); w_byte(w, 'B'); w_byte(w, c->version);
	w_index(w, c->flags);
	w_index(w, c->nints); w_index(w, c->nfloats); w_index(w, c->nstrings);
	if (c->version >= 5) w_index(w, c->nbytes);
	w_index(w, c->ntypes); w_index(w, c->nglobals); w_index(w, c->nnatives);
	w_index(w, c->nfunctions);
	if (c->version >= 4) w_index(w, c->nconstants);
	w_index(w, c->entrypoint);
	for (int i = 0; i < c->nints; i++) w_i32(w, c->ints[i]);
	for (int i = 0; i < c->nfloats; i++) w_raw(w, &c->floats[i], 8);
	w_strings(w, c->strings, c->strings_lens, c->nstrings);
	if (c->version >= 5) {
		w_i32(w, c->bytes_size);
		w_raw(w, c->bytes, (size_t)c->bytes_size);
		for (int i = 0; i < c->nbytes; i++) w_index(w, c->bytes_pos[i]);
	}
	if (HLBC_HASDEBUG(c)) {
		w_index(w, c->ndebugfiles);
		w_strings(w, c->debugfiles, c->debugfiles_lens, c->ndebugfiles);
	}
	for (int i = 0; i < c->ntypes; i++) w_type(w, &c->types[i]);
	for (int i = 0; i < c->nglobals; i++) w_index(w, c->globals[i]);
	for (int i = 0; i < c->nnatives; i++) {
		const hlbc_native *n = &c->natives[i];
		w_index(w, n->lib); w_index(w, n->name); w_index(w, n->type); w_index(w, n->findex);
	}
	for (int i = 0; i < c->nfunctions; i++) {
		const hlbc_function *f = &c->functions[i];
		w_index(w, f->type); w_index(w, f->findex); w_index(w, f->nregs); w_index(w, f->nops);
		for (int j = 0; j < f->nregs; j++) w_index(w, f->regs[j]);
		for (int j = 0; j < f->nops; j++) w_op(w, &f->ops[j]);
		if (HLBC_HASDEBUG(c)) {
			w_debug(w, f->debug, f->nops);
			if (c->version >= 3) {
				w_index(w, f->nassigns);
				for (int j = 0; j < f->nassigns; j++) {
					w_index(w, f->assigns[j * 3]);
					w_index(w, f->assigns[j * 3 + 1] + 1);
					if (c->version >= 6) w_index(w, f->assigns[j * 3 + 2] + 1);
				}
			}
		}
	}
	for (int i = 0; i < c->nconstants; i++) {
		const hlbc_constant *k = &c->constants[i];
		w_index(w, k->global); w_index(w, k->nfields);
		for (int j = 0; j < k->nfields; j++) w_index(w, k->fields[j]);
	}
	if (w->oom) { free(w->b); return -1; }
	*out = w->b;
	*out_size = w->len;
	return 0;
}

/* ------------------------------------------------------------------ */
/* lookup                                                              */

const char *hlbc_str(const hlbc_code *c, int idx) {
	return idx >= 0 && idx < c->nstrings ? c->strings[idx] : "";
}

int hlbc_find_string(const hlbc_code *c, const char *s) {
	for (int i = 0; i < c->nstrings; i++)
		if (strcmp(c->strings[i], s) == 0) return i;
	return -1;
}

int hlbc_find_obj_type(const hlbc_code *c, const char *name) {
	for (int i = 0; i < c->ntypes; i++) {
		const hlbc_type *t = &c->types[i];
		if ((t->kind == HLT_OBJ || t->kind == HLT_STRUCT) && strcmp(hlbc_str(c, t->name), name) == 0) return i;
	}
	return -1;
}

hlbc_function *hlbc_function_by_findex(hlbc_code *c, int findex) {
	for (int i = 0; i < c->nfunctions; i++)
		if (c->functions[i].findex == findex) return &c->functions[i];
	return NULL;
}

hlbc_native *hlbc_native_by_findex(hlbc_code *c, int findex) {
	for (int i = 0; i < c->nnatives; i++)
		if (c->natives[i].findex == findex) return &c->natives[i];
	return NULL;
}

int hlbc_findex_type(hlbc_code *c, int findex) {
	hlbc_function *f = hlbc_function_by_findex(c, findex);
	if (f) return f->type;
	hlbc_native *n = hlbc_native_by_findex(c, findex);
	return n ? n->type : -1;
}

static int count_fields_hierarchy(const hlbc_code *c, int tidx) {
	int n = 0;
	for (int guard = 0; tidx >= 0 && tidx < c->ntypes && guard < 256; guard++) {
		n += c->types[tidx].nfields;
		tidx = c->types[tidx].super;
	}
	return n;
}

int hlbc_field_name_abs(const hlbc_code *c, int tidx, int abs) {
	const hlbc_type *t = &c->types[tidx];
	int base = t->super >= 0 ? count_fields_hierarchy(c, t->super) : 0;
	if (abs >= base) return abs - base < t->nfields ? t->fields[abs - base].name : -1;
	return t->super >= 0 ? hlbc_field_name_abs(c, t->super, abs) : -1;
}
#define field_name_abs hlbc_field_name_abs

int hlbc_find_method(const hlbc_code *c, const char *cls, const char *method) {
	int t = hlbc_find_obj_type(c, cls);
	if (t >= 0) {
		const hlbc_type *ty = &c->types[t];
		for (int i = 0; i < ty->nproto; i++)
			if (strcmp(hlbc_str(c, ty->proto[i].name), method) == 0) return ty->proto[i].findex;
		for (int i = 0; i < ty->nbindings; i++) {
			int fn = field_name_abs(c, t, ty->bindings[i * 2]);
			if (fn >= 0 && strcmp(hlbc_str(c, fn), method) == 0) return ty->bindings[i * 2 + 1];
		}
	}
	/* statics live on the class object type "pkg.$Class" */
	size_t n = strlen(cls);
	char *sname = malloc(n + 2);
	if (!sname) return -1;
	const char *dot = strrchr(cls, '.');
	size_t pre = dot ? (size_t)(dot - cls) + 1 : 0;
	memcpy(sname, cls, pre);
	sname[pre] = '$';
	memcpy(sname + pre + 1, cls + pre, n - pre + 1);
	int st = hlbc_find_obj_type(c, sname);
	free(sname);
	if (st >= 0) {
		const hlbc_type *ty = &c->types[st];
		for (int i = 0; i < ty->nbindings; i++) {
			int fn = field_name_abs(c, st, ty->bindings[i * 2]);
			if (fn >= 0 && strcmp(hlbc_str(c, fn), method) == 0) return ty->bindings[i * 2 + 1];
		}
	}
	return -1;
}

/* ------------------------------------------------------------------ */
/* editing                                                             */

#define GROW(ptr, count, extra) grow_array((void **)&(ptr), (size_t)(count), (size_t)(extra), sizeof(*(ptr)))

static int grow_array(void **p, size_t count, size_t extra, size_t elem) {
	void *np = realloc(*p, (count + extra) * elem);
	if (!np) return -1;
	memset((char *)np + count * elem, 0, extra * elem);
	*p = np;
	return 0;
}

int hlbc_add_string(hlbc_code *c, const char *s) {
	int i = hlbc_find_string(c, s);
	if (i >= 0) return i;
	if (GROW(c->strings, c->nstrings, 1) || GROW(c->strings_lens, c->nstrings, 1)) return -1;
	size_t n = strlen(s);
	c->strings[c->nstrings] = malloc(n + 1);
	if (!c->strings[c->nstrings]) return -1;
	memcpy(c->strings[c->nstrings], s, n + 1);
	c->strings_lens[c->nstrings] = (int)n;
	return c->nstrings++;
}

static int type_equal(const hlbc_type *a, const hlbc_type *b) {
	if (a->kind != b->kind) return 0;
	switch (a->kind) {
	case HLT_FUN: case HLT_METHOD:
		if (a->nargs != b->nargs || a->ret != b->ret) return 0;
		for (int i = 0; i < a->nargs; i++) if (a->args[i] != b->args[i]) return 0;
		return 1;
	case HLT_REF: case HLT_NULL: case HLT_PACKED:
		return a->tparam == b->tparam;
	case HLT_OBJ: case HLT_STRUCT: case HLT_VIRTUAL: case HLT_ABSTRACT: case HLT_ENUM:
		return 0; /* nominal types are never synthesized */
	default:
		return 1;
	}
}

int hlbc_add_type(hlbc_code *c, const hlbc_type *t) {
	for (int i = 0; i < c->ntypes; i++)
		if (type_equal(&c->types[i], t)) return i;
	if (GROW(c->types, c->ntypes, 1)) return -1;
	hlbc_type *nt = &c->types[c->ntypes];
	*nt = *t;
	nt->super = -1;
	nt->args = NULL;
	if (t->kind == HLT_FUN || t->kind == HLT_METHOD) {
		nt->args = malloc(sizeof(int) * (size_t)(t->nargs ? t->nargs : 1));
		if (!nt->args) return -1;
		if (t->nargs) memcpy(nt->args, t->args, sizeof(int) * (size_t)t->nargs);
	}
	nt->fields = NULL; nt->proto = NULL; nt->bindings = NULL; nt->constructs = NULL;
	return c->ntypes++;
}

int hlbc_add_simple_type(hlbc_code *c, int kind) {
	hlbc_type t = { 0 };
	t.kind = kind;
	return hlbc_add_type(c, &t);
}

int hlbc_add_fun_type(hlbc_code *c, const int *args, int nargs, int ret) {
	hlbc_type t = { 0 };
	t.kind = HLT_FUN;
	t.nargs = nargs;
	t.args = (int *)args;
	t.ret = ret;
	return hlbc_add_type(c, &t);
}

int hlbc_next_findex(const hlbc_code *c) {
	int m = -1;
	for (int i = 0; i < c->nfunctions; i++) if (c->functions[i].findex > m) m = c->functions[i].findex;
	for (int i = 0; i < c->nnatives; i++) if (c->natives[i].findex > m) m = c->natives[i].findex;
	return m + 1;
}

int hlbc_add_native(hlbc_code *c, const char *lib, const char *name, int funtype) {
	int l = hlbc_add_string(c, lib), n = hlbc_add_string(c, name);
	if (l < 0 || n < 0) return -1;
	for (int i = 0; i < c->nnatives; i++)
		if (c->natives[i].lib == l && c->natives[i].name == n) return c->natives[i].findex;
	int fidx = hlbc_next_findex(c);
	if (GROW(c->natives, c->nnatives, 1)) return -1;
	/* HashLink caches the lib handle per consecutive run of identical lib
	 * pointers, so natives from the same lib must stay adjacent: insert after
	 * the last native of this lib if present. */
	int pos = c->nnatives;
	for (int i = c->nnatives - 1; i >= 0; i--)
		if (c->natives[i].lib == l) { pos = i + 1; break; }
	memmove(&c->natives[pos + 1], &c->natives[pos], sizeof(hlbc_native) * (size_t)(c->nnatives - pos));
	c->natives[pos].lib = l;
	c->natives[pos].name = n;
	c->natives[pos].type = funtype;
	c->natives[pos].findex = fidx;
	c->nnatives++;
	return fidx;
}
