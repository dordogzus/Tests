/*
 * The subset of the HashLink runtime API used by dccoop.hdll. Only exported
 * functions and opaque pointers are used, so the module does not depend on
 * struct layouts that differ between HashLink versions.
 */
#ifndef DCCOOP_HL_MIN_H
#define DCCOOP_HL_MIN_H

#include <stdbool.h>
#include <stdint.h>

#if defined(_WIN32)
#define HLIMPORT __declspec(dllimport)
#define HLEXPORT __declspec(dllexport)
#else
#define HLIMPORT extern
#define HLEXPORT __attribute__((visibility("default")))
#endif

typedef struct hl_type hl_type;
typedef struct vdynamic { hl_type *t; } vdynamic;
typedef struct vclosure vclosure;

HLIMPORT hl_type hlt_i32;
HLIMPORT hl_type hlt_f64;
HLIMPORT hl_type hlt_dyn;
HLIMPORT hl_type hlt_bool;

HLIMPORT int hl_hash_utf8(const char *name);
HLIMPORT bool hl_obj_has_field(vdynamic *obj, int hfield);
HLIMPORT int hl_dyn_geti(vdynamic *d, int hfield, hl_type *t);
HLIMPORT double hl_dyn_getd(vdynamic *d, int hfield);
HLIMPORT void *hl_dyn_getp(vdynamic *d, int hfield, hl_type *t);
HLIMPORT void hl_dyn_seti(vdynamic *d, int hfield, hl_type *t, int value);
HLIMPORT void hl_dyn_setd(vdynamic *d, int hfield, double value);
HLIMPORT void hl_dyn_setp(vdynamic *d, int hfield, hl_type *t, void *value);
HLIMPORT vdynamic *hl_dyn_call(vclosure *c, vdynamic **args, int nargs);
HLIMPORT vdynamic *hl_make_dyn(void *data, hl_type *t);
HLIMPORT void hl_add_root(void *ptr);
HLIMPORT void hl_remove_root(void *ptr);

/* HashLink native export convention: hlp_<name> returns the function and
 * optionally a signature string (NULL skips the check). */
#define DEFINE_NATIVE(name) HLEXPORT void *hlp_##name(const char **sign) { *sign = NULL; return (void *)dc_##name; }

#endif
