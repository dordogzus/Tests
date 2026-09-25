/* Patch engine checks on real bytecode plus a synthetic jump-remap case. */
#include "../src/common/fileio.h"
#include "../src/hlbc/patch.h"
#include "../src/patchset/dcpatch.h"
#include "check.h"

#include <string.h>

static int jump_targets_valid(hlbc_code *c) {
	for (int i = 0; i < c->nfunctions; i++) {
		hlbc_function *f = &c->functions[i];
		for (int j = 0; j < f->nops; j++) {
			hlbc_op *o = &f->ops[j];
			int off = 0, has = 1;
			switch (o->op) {
			case OP_JTrue: case OP_JFalse: case OP_JNull: case OP_JNotNull: case OP_Trap: off = o->p2; break;
			case OP_JAlways: off = o->p1; break;
			case OP_JSLt: case OP_JSGte: case OP_JSGt: case OP_JSLte: case OP_JULt: case OP_JUGte:
			case OP_JNotLt: case OP_JNotGte: case OP_JEq: case OP_JNotEq: off = o->p3; break;
			default: has = 0;
			}
			if (has && (j + 1 + off < 0 || j + 1 + off > f->nops)) return 0;
		}
	}
	return 1;
}

static void synthetic(void) {
	/* 0: JAlways +1 -> 2 ; 1: Nop ; 2: JFalse r0 -3 -> 0 ; 3: Ret */
	hlbc_code c;
	memset(&c, 0, sizeof c);
	hlbc_function f;
	memset(&f, 0, sizeof f);
	hlbc_op ops[4] = { { OP_JAlways, 1, 0, 0, 0, NULL }, { OP_Nop, 0, 0, 0, 0, NULL },
		{ OP_JFalse, 0, -3, 0, 0, NULL }, { OP_Ret, 0, 0, 0, 0, NULL } };
	f.nops = 4;
	f.ops = malloc(sizeof ops);
	memcpy(f.ops, ops, sizeof ops);
	hlbc_op ins[2] = { { OP_Nop, 0, 0, 0, 0, NULL }, { OP_Nop, 0, 0, 0, 0, NULL } };
	/* insert before op 2, keeping jumps that targeted 2 on the original op */
	CHECK(hlbc_insert_ops(&c, &f, 2, ins, 2, 0) == 0);
	CHECK(f.nops == 6);
	CHECK(f.ops[0].op == OP_JAlways && 0 + 1 + f.ops[0].p1 == 4);
	CHECK(f.ops[4].op == OP_JFalse && 4 + 1 + f.ops[4].p2 == 0);
	/* insert before the Ret with retargeting: a jump to Ret lands on the hook */
	f.ops[0].p1 = 4; /* 0 -> 5 (Ret) */
	CHECK(hlbc_insert_ops(&c, &f, 5, ins, 1, 1) == 0);
	CHECK(0 + 1 + f.ops[0].p1 == 5 && f.ops[6].op == OP_Ret);
	free(f.ops);
}

int main(int argc, char **argv) {
	synthetic();
	if (argc < 2) return CHECK_DONE("test_patch");
	size_t size, osize;
	uint8_t *data = dc_read_file(argv[1], &size), *out;
	CHECK(data != NULL);
	if (!data) return 1;
	const char *err;
	hlbc_code *c = hlbc_read(data, size, &err);
	CHECK(c != NULL);
	dcpatch_result r;
	CHECK(dcpatch_apply(c, NULL, &r, NULL, NULL) == 0);
	CHECK(r.applied == 7 && r.skipped == 0 && r.spawner_ok);
	CHECK(jump_targets_valid(c));
	int hit = hlbc_find_method(c, "en.Entity", "hit");
	hlbc_function *f = hlbc_function_by_findex(c, hit);
	CHECK(f && f->ops[0].op == OP_CallN && f->ops[0].p1 == 2); /* rewrites the dmg register */
	hlbc_native *n = f ? hlbc_native_by_findex(c, f->ops[0].p2) : NULL;
	CHECK(n && !strcmp(hlbc_str(c, n->name), "damage") && !strcmp(hlbc_str(c, n->lib), "dccoop"));
	int upd = hlbc_find_method(c, "en.Mob", "update");
	f = hlbc_function_by_findex(c, upd);
	CHECK(f && f->ops[1].op == OP_JFalse && f->ops[2].op == OP_Ret);
	CHECK(hlbc_write(c, &out, &osize) == 0);
	hlbc_free(c);
	c = hlbc_read(out, osize, &err);
	CHECK(c != NULL);
	CHECK(dcpatch_apply(c, NULL, &r, NULL, NULL) == 1); /* idempotent */
	/* a hook table naming a method with the wrong signature is skipped, not applied */
	hlbc_free(c);
	c = hlbc_read(data, size, &err);
	CHECK(dcpatch_apply(c, "hook damage en.Entity hit rewrite:2 oo:v\nhook x No such notify o:v\n", &r, NULL, NULL) == 0);
	CHECK(r.applied == 0 && r.skipped == 2);
	hlbc_free(c);
	free(out);
	free(data);
	return CHECK_DONE("test_patch");
}
