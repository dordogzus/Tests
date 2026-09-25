/*
 * hlbc - HashLink bytecode (.hl / hlboot.dat) reader, writer and editor.
 *
 * Mirrors the on-disk format read by HashLink's src/code.c (versions 2..6),
 * keeping every table index-based so a parsed file can be modified and
 * re-serialized without losing information.
 */
#ifndef DCCOOP_HLBC_H
#define DCCOOP_HLBC_H

#include <stddef.h>
#include <stdint.h>

enum {
	HLT_VOID = 0, HLT_UI8, HLT_UI16, HLT_I32, HLT_I64, HLT_F32, HLT_F64, HLT_BOOL,
	HLT_BYTES, HLT_DYN, HLT_FUN, HLT_OBJ, HLT_ARRAY, HLT_TYPE, HLT_REF, HLT_VIRTUAL,
	HLT_DYNOBJ, HLT_ABSTRACT, HLT_ENUM, HLT_NULL, HLT_METHOD, HLT_STRUCT, HLT_PACKED,
	HLT_GUID, HLT_LAST
};

/* Opcode numbering matches HashLink's opcodes.h (append-only across versions). */
enum {
	OP_Mov, OP_Int, OP_Float, OP_Bool, OP_Bytes, OP_String, OP_Null,
	OP_Add, OP_Sub, OP_Mul, OP_SDiv, OP_UDiv, OP_SMod, OP_UMod, OP_Shl, OP_SShr,
	OP_UShr, OP_And, OP_Or, OP_Xor, OP_Neg, OP_Not, OP_Incr, OP_Decr,
	OP_Call0, OP_Call1, OP_Call2, OP_Call3, OP_Call4, OP_CallN, OP_CallMethod,
	OP_CallThis, OP_CallClosure, OP_StaticClosure, OP_InstanceClosure,
	OP_VirtualClosure, OP_GetGlobal, OP_SetGlobal, OP_Field, OP_SetField,
	OP_GetThis, OP_SetThis, OP_DynGet, OP_DynSet, OP_JTrue, OP_JFalse, OP_JNull,
	OP_JNotNull, OP_JSLt, OP_JSGte, OP_JSGt, OP_JSLte, OP_JULt, OP_JUGte,
	OP_JNotLt, OP_JNotGte, OP_JEq, OP_JNotEq, OP_JAlways, OP_ToDyn, OP_ToSFloat,
	OP_ToUFloat, OP_ToInt, OP_SafeCast, OP_UnsafeCast, OP_ToVirtual, OP_Label,
	OP_Ret, OP_Throw, OP_Rethrow, OP_Switch, OP_NullCheck, OP_Trap, OP_EndTrap,
	OP_GetI8, OP_GetI16, OP_GetMem, OP_GetArray, OP_SetI8, OP_SetI16, OP_SetMem,
	OP_SetArray, OP_New, OP_ArraySize, OP_Type, OP_GetType, OP_GetTID, OP_Ref,
	OP_Unref, OP_Setref, OP_MakeEnum, OP_EnumAlloc, OP_EnumIndex, OP_EnumField,
	OP_SetEnumField, OP_Assert, OP_RefData, OP_RefOffset, OP_Nop, OP_Prefetch,
	OP_Asm, OP_Catch, OP_LAST
};

typedef struct { int name; int type; } hlbc_field;          /* name: string idx */
typedef struct { int name; int findex; int pindex; } hlbc_proto;
typedef struct { int name; int nparams; int *params; } hlbc_construct;

typedef struct {
	int kind;
	/* HLT_FUN / HLT_METHOD */
	int nargs; int *args; int ret;
	/* HLT_OBJ / HLT_STRUCT (fields also used by HLT_VIRTUAL) */
	int name;            /* string idx: obj, abstract, enum */
	int super;           /* type idx or -1 */
	int global;          /* global idx (+1 encoded as-is from file) */
	int nfields; hlbc_field *fields;
	int nproto; hlbc_proto *proto;
	int nbindings; int *bindings; /* pairs (field idx, findex) */
	/* HLT_ENUM */
	int nconstructs; hlbc_construct *constructs;
	/* HLT_REF / HLT_NULL / HLT_PACKED */
	int tparam;
} hlbc_type;

typedef struct { int lib; int name; int type; int findex; } hlbc_native;

typedef struct {
	int op;
	int p1, p2, p3;
	int nextra;
	int *extra;
} hlbc_op;

typedef struct {
	int type, findex;
	int nregs; int *regs;
	int nops; hlbc_op *ops;
	int *debug;           /* 2 ints per op (file, line) when hasdebug */
	int nassigns; int *assigns; /* 3 ints: name, pos, scope_end (decoded) */
} hlbc_function;

typedef struct { int global; int nfields; int *fields; } hlbc_constant;

typedef struct {
	int version, flags;
	int nints; int32_t *ints;
	int nfloats; double *floats;
	int nstrings; char **strings; int *strings_lens;
	int nbytes; int bytes_size; uint8_t *bytes; int *bytes_pos;
	int ndebugfiles; char **debugfiles; int *debugfiles_lens;
	int ntypes; hlbc_type *types;
	int nglobals; int *globals;
	int nnatives; hlbc_native *natives;
	int nfunctions; hlbc_function *functions;
	int nconstants; hlbc_constant *constants;
	int entrypoint;
} hlbc_code;

#define HLBC_HASDEBUG(c) ((c)->flags & 1)

/* Returns NULL on error and fills *err (static string). */
hlbc_code *hlbc_read(const uint8_t *data, size_t size, const char **err);
/* Serializes to a malloc'd buffer. Returns 0 on success. */
int hlbc_write(const hlbc_code *c, uint8_t **out, size_t *out_size);
void hlbc_free(hlbc_code *c);

const char *hlbc_op_name(int op);
/* Number-of-args class used by the file format (0..3, 4+ fixed, -1 variable). */
int hlbc_op_nargs(int op);

/* ---- lookup helpers ---- */
const char *hlbc_str(const hlbc_code *c, int idx);
int hlbc_find_string(const hlbc_code *c, const char *s);
int hlbc_find_obj_type(const hlbc_code *c, const char *name);
hlbc_function *hlbc_function_by_findex(hlbc_code *c, int findex);
hlbc_native *hlbc_native_by_findex(hlbc_code *c, int findex);
/* "pkg.Class.method" -> findex of instance proto or static binding, -1 if absent. */
int hlbc_find_method(const hlbc_code *c, const char *cls, const char *method);
/* Field name (string idx) by absolute field index across the class hierarchy. */
int hlbc_field_name_abs(const hlbc_code *c, int tidx, int abs);
/* Type index of a function or native by findex, -1 if absent. */
int hlbc_findex_type(hlbc_code *c, int findex);

/* ---- editing ---- */
int hlbc_add_string(hlbc_code *c, const char *s);
/* Finds or appends a type equal to *t (deep-compares fun types / simple kinds). */
int hlbc_add_type(hlbc_code *c, const hlbc_type *t);
int hlbc_add_simple_type(hlbc_code *c, int kind);
int hlbc_add_fun_type(hlbc_code *c, const int *args, int nargs, int ret);
/* Adds a native import; returns its findex. */
int hlbc_add_native(hlbc_code *c, const char *lib, const char *name, int funtype);
/* Next free findex (functions and natives share one index space). */
int hlbc_next_findex(const hlbc_code *c);

#endif
