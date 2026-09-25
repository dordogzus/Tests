/*
 * hlbc-dump: lists classes, fields and methods of a HashLink bytecode file.
 * Output contains only names/signatures (no game code), which is what is
 * needed to map DC-Coop hooks onto a specific game build.
 *
 *   hlbc-dump hlboot.dat [filter]        classes whose name contains filter
 *   hlbc-dump --ops hlboot.dat Cls.meth  disassemble one method
 */
#include "../src/common/fileio.h"
#include "../src/hlbc/hlbc.h"

#include <string.h>

static void type_name(const hlbc_code *c, int t, char *buf, size_t n) {
	static const char *simple[] = { "void", "u8", "u16", "i32", "i64", "f32", "f64", "bool", "bytes", "dyn",
		"fun", "obj", "array", "type", "ref", "virtual", "dynobj", "abstract", "enum", "null", "method",
		"struct", "packed", "guid" };
	if (t < 0 || t >= c->ntypes) { snprintf(buf, n, "?%d", t); return; }
	const hlbc_type *ty = &c->types[t];
	switch (ty->kind) {
	case HLT_OBJ: case HLT_STRUCT: case HLT_ENUM: case HLT_ABSTRACT:
		snprintf(buf, n, "%s", hlbc_str(c, ty->name)); break;
	case HLT_NULL: case HLT_REF: {
		char in[128];
		type_name(c, ty->tparam, in, sizeof in);
		snprintf(buf, n, "%s<%s>", ty->kind == HLT_NULL ? "null" : "ref", in);
		break;
	}
	case HLT_FUN: case HLT_METHOD: {
		size_t o = (size_t)snprintf(buf, n, "(");
		for (int i = 0; i < ty->nargs && o < n; i++) {
			char a[128];
			type_name(c, ty->args[i], a, sizeof a);
			o += (size_t)snprintf(buf + o, n - o, "%s%s", i ? "," : "", a);
		}
		char r[128];
		type_name(c, ty->ret, r, sizeof r);
		if (o < n) snprintf(buf + o, n - o, ")->%s", r);
		break;
	}
	default:
		snprintf(buf, n, "%s", ty->kind < HLT_LAST ? simple[ty->kind] : "?");
	}
}

static void dump_ops(hlbc_code *c, int findex) {
	hlbc_function *f = hlbc_function_by_findex(c, findex);
	if (!f) { printf("findex %d is not a bytecode function\n", findex); return; }
	char buf[512];
	type_name(c, f->type, buf, sizeof buf);
	printf("fn@%d %s regs=%d ops=%d\n", findex, buf, f->nregs, f->nops);
	for (int i = 0; i < f->nregs; i++) { type_name(c, f->regs[i], buf, sizeof buf); printf("  r%d: %s\n", i, buf); }
	for (int i = 0; i < f->nops; i++) {
		hlbc_op *o = &f->ops[i];
		printf("  %4d %-16s %d %d %d", i, hlbc_op_name(o->op), o->p1, o->p2, o->p3);
		for (int j = 0; j < o->nextra; j++) printf(" %d", o->extra[j]);
		printf("\n");
	}
}

int main(int argc, char **argv) {
	int ops = argc > 1 && strcmp(argv[1], "--ops") == 0;
	if (argc < 2 + ops) {
		fprintf(stderr, "usage: hlbc-dump [--ops] <hlboot.dat> [filter|Class.method]\n");
		return 2;
	}
	size_t size;
	uint8_t *data = dc_read_file(argv[1 + ops], &size);
	if (!data) { fprintf(stderr, "cannot read %s\n", argv[1 + ops]); return 1; }
	const char *err = NULL;
	hlbc_code *c = hlbc_read(data, size, &err);
	free(data);
	if (!c) { fprintf(stderr, "parse error: %s\n", err); return 1; }
	const char *filter = argc > 2 + ops ? argv[2 + ops] : NULL;

	if (ops) {
		if (!filter) return 2;
		const char *dot = strrchr(filter, '.');
		if (!dot) return 2;
		char cls[256];
		snprintf(cls, sizeof cls, "%.*s", (int)(dot - filter), filter);
		int fi = hlbc_find_method(c, cls, dot + 1);
		if (fi < 0) { fprintf(stderr, "method not found\n"); return 1; }
		dump_ops(c, fi);
		hlbc_free(c);
		return 0;
	}

	printf("# hlbc-dump version=%d types=%d functions=%d natives=%d strings=%d debug=%d\n", c->version,
		c->ntypes, c->nfunctions, c->nnatives, c->nstrings, HLBC_HASDEBUG(c) ? 1 : 0);
	char buf[1024];
	for (int i = 0; i < c->ntypes; i++) {
		hlbc_type *t = &c->types[i];
		if (t->kind != HLT_OBJ && t->kind != HLT_STRUCT) continue;
		const char *name = hlbc_str(c, t->name);
		if (filter && !strstr(name, filter)) continue;
		printf("class %s", name);
		if (t->super >= 0) printf(" extends %s", hlbc_str(c, c->types[t->super].name));
		printf("\n");
		for (int j = 0; j < t->nfields; j++) {
			type_name(c, t->fields[j].type, buf, sizeof buf);
			printf("  var %s : %s\n", hlbc_str(c, t->fields[j].name), buf);
		}
		for (int j = 0; j < t->nproto; j++) {
			type_name(c, hlbc_findex_type(c, t->proto[j].findex), buf, sizeof buf);
			printf("  method %s%s @%d\n", hlbc_str(c, t->proto[j].name), buf, t->proto[j].findex);
		}
		for (int j = 0; j < t->nbindings; j++) {
			type_name(c, hlbc_findex_type(c, t->bindings[j * 2 + 1]), buf, sizeof buf);
			printf("  static %s%s @%d\n", hlbc_str(c, hlbc_field_name_abs(c, i, t->bindings[j * 2])), buf,
				t->bindings[j * 2 + 1]);
		}
	}
	if (!filter) {
		for (int i = 0; i < c->nnatives; i++) {
			hlbc_native *n = &c->natives[i];
			type_name(c, n->type, buf, sizeof buf);
			printf("native %s@%s%s @%d\n", hlbc_str(c, n->lib), hlbc_str(c, n->name), buf, n->findex);
		}
	}
	hlbc_free(c);
	return 0;
}
