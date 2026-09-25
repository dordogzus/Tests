/*
 * Hook injection into HashLink bytecode.
 *
 * Hooks call a native function exported by an .hdll (resolved by HashLink
 * as <lib>64.hdll / <lib>.hdll, symbol hlp_<name>). The native receives the
 * hooked function's arguments with their exact HL types.
 */
#ifndef DCCOOP_HLBC_PATCH_H
#define DCCOOP_HLBC_PATCH_H

#include "hlbc.h"

typedef enum {
	/* native(args...) -> void, runs before the body */
	HOOK_ENTRY_NOTIFY,
	/* native(args...) -> typeof(args[arg]); result replaces args[arg] */
	HOOK_ENTRY_REWRITE_ARG,
	/* native(args...) -> bool; true skips the body (void functions only) */
	HOOK_ENTRY_CANCEL,
	/* native(args...) -> void, runs before every return of a void function;
	 * for non-void functions: native(args..., ret) -> typeof(ret), result is returned */
	HOOK_EXIT,
} hlbc_hook_mode;

typedef struct {
	int target_findex;
	hlbc_hook_mode mode;
	int arg;               /* HOOK_ENTRY_REWRITE_ARG only */
	const char *lib;
	const char *native;
} hlbc_hook;

/* Returns 0 on success, otherwise writes a message to err. */
int hlbc_apply_hook(hlbc_code *c, const hlbc_hook *h, char *err, size_t errlen);

/*
 * Checks the hooked function's argument kinds against a compact signature:
 *   o = object-like pointer (obj/struct/dyn/virtual/null/array/abstract...)
 *   i = i32   f = f64   s = f32   b = bool   * = anything (rest ignored)
 * e.g. "ooi" for hit(this:Entity, from:Entity, dmg:Int).
 * Optional ":r" suffix checks the return kind with the same letters, v = void.
 */
int hlbc_check_signature(const hlbc_code *c, int findex, const char *sig);

/* Inserts n ops before position pos. If to_inserted, jumps that targeted pos
 * now land on the first inserted op; otherwise they still reach the old op. */
int hlbc_insert_ops(hlbc_code *c, hlbc_function *f, int pos, const hlbc_op *ops, int n, int to_inserted);
int hlbc_add_reg(hlbc_function *f, int type);

#endif
