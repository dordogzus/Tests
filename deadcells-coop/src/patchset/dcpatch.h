/*
 * DC-Coop patch set: maps co-op natives onto game methods by class/method
 * name, validating each target's signature so a game update that changes a
 * method disables that single hook instead of crashing.
 *
 * The table can be overridden at runtime by a text file (dccoop_hooks.txt):
 *   hook <native> <Class> <method> <notify|rewrite:N|cancel|exit> <sig>
 *   spawner <HeroClass>
 */
#ifndef DCCOOP_DCPATCH_H
#define DCCOOP_DCPATCH_H

#include "../hlbc/hlbc.h"

#define DCCOOP_LIB "dccoop"

typedef void (*dcpatch_log_fn)(void *ud, const char *msg);

typedef struct {
	int applied, skipped;
	int spawner_ok;
} dcpatch_result;

/* hooks_text may be NULL to use the built-in table. */
int dcpatch_apply(hlbc_code *c, const char *hooks_text, dcpatch_result *res, dcpatch_log_fn log, void *ud);

/* Convenience: parse, patch, serialize. Returns 0 on success. */
int dcpatch_buffer(const uint8_t *in, size_t in_size, const char *hooks_text, uint8_t **out, size_t *out_size,
	dcpatch_log_fn log, void *ud);

/* Marker string added to patched bytecode so it is never patched twice. */
#define DCCOOP_MARKER "$dccoop_patched_v1"

#endif
