# DC-Coop build
#   make test      host unit + integration tests (gcc)
#   make e2e       patch the Haxe mock game and run it under HashLink (needs haxe + HL_DIR)
#   make windows   dist/ package: winmm.dll, dccoop.hdll, tools (needs x86_64-w64-mingw32-gcc)

CC       ?= gcc
MINGW    ?= x86_64-w64-mingw32-gcc
DLLTOOL  ?= x86_64-w64-mingw32-dlltool
HL_DIR   ?= /tmp/hashlink
CFLAGS   ?= -O2 -g
WARN     := -std=c11 -D_DEFAULT_SOURCE -Wall -Wextra -Wno-unused-parameter
SAN      := -fsanitize=address,undefined

HLBC_SRC  := src/hlbc/hlbc.c src/hlbc/patch.c
PATCH_SRC := $(HLBC_SRC) src/patchset/dcpatch.c
CORE_SRC  := $(wildcard src/core/*.c)
HDLL_SRC  := $(CORE_SRC) src/hdll/natives.c

B := build
T := $(B)/test
D := dist

.PHONY: all test unit e2e windows clean

all: test

$(T) $(D):
	mkdir -p $@

# ---------------- host tools & tests ----------------

$(B)/hlbc-dump: tools/hlbc_dump.c $(HLBC_SRC) | $(T)
	$(CC) $(WARN) $(CFLAGS) -o $@ $^

$(B)/dccoop-patch: tools/dccoop_patch.c $(PATCH_SRC) | $(T)
	$(CC) $(WARN) $(CFLAGS) -o $@ $^

$(T)/core: tests/test_core.c $(CORE_SRC) | $(T)
	$(CC) $(WARN) $(SAN) -O1 -g -o $@ $^ -lm

$(T)/coop12: tests/test_coop12.c $(CORE_SRC) | $(T)
	$(CC) $(WARN) $(SAN) -O1 -g -o $@ $^ -lm

$(T)/roundtrip: tests/test_hlbc_roundtrip.c $(HLBC_SRC) | $(T)
	$(CC) $(WARN) $(SAN) -O1 -g -o $@ $^

$(T)/patchtest: tests/test_patch.c $(PATCH_SRC) | $(T)
	$(CC) $(WARN) $(SAN) -O1 -g -o $@ $^

unit: $(T)/core $(T)/coop12
	$(T)/core
	$(T)/coop12

test: unit $(if $(wildcard $(HL_DIR)/hl),e2e,)

# ---------------- end to end under HashLink ----------------

$(T)/mock.hl: $(wildcard tests/hlmock/src/*.hx tests/hlmock/src/en/*.hx) | $(T)
	cd tests/hlmock && haxe build.hxml

$(T)/dccoop.hdll: $(HDLL_SRC) | $(T)
	$(CC) $(WARN) -O1 -g -fPIC -shared -fvisibility=hidden -o $@ $^ -L$(HL_DIR) -lhl -lm

$(T)/mock.coop.hl: $(T)/mock.hl $(B)/dccoop-patch
	$(B)/dccoop-patch $< $@

e2e: $(T)/roundtrip $(T)/patchtest $(T)/mock.coop.hl $(T)/dccoop.hdll $(B)/hlbc-dump
	$(T)/roundtrip $(T)/mock.hl
	$(T)/roundtrip $(T)/mock.coop.hl
	$(T)/patchtest $(T)/mock.hl
	HL=$(HL_DIR)/hl T=$(T) sh tests/e2e.sh

# ---------------- Windows package ----------------

MINGW_FLAGS := $(WARN) -O2 -s -static-libgcc -Wno-cast-function-type

$(B)/win/libhl.a: src/hdll/libhl.def | $(T)
	mkdir -p $(B)/win
	$(DLLTOOL) -d $< -l $@ -D libhl.dll

$(D)/dccoop.hdll: $(HDLL_SRC) $(B)/win/libhl.a | $(D)
	$(MINGW) $(MINGW_FLAGS) -shared -o $@ $(HDLL_SRC) $(B)/win/libhl.a -lws2_32 -lm

$(D)/winmm.dll: src/loader/loader.c src/loader/winmm.def $(PATCH_SRC) | $(D)
	$(MINGW) $(MINGW_FLAGS) -shared -o $@ src/loader/loader.c $(PATCH_SRC) src/loader/winmm.def -Wl,--enable-stdcall-fixup

$(D)/tools/hlbc-dump.exe: tools/hlbc_dump.c $(HLBC_SRC) | $(D)
	mkdir -p $(D)/tools
	$(MINGW) $(MINGW_FLAGS) -o $@ $^

$(D)/tools/dccoop-patch.exe: tools/dccoop_patch.c $(PATCH_SRC) | $(D)
	mkdir -p $(D)/tools
	$(MINGW) $(MINGW_FLAGS) -o $@ $^

windows: $(D)/winmm.dll $(D)/dccoop.hdll $(D)/tools/hlbc-dump.exe $(D)/tools/dccoop-patch.exe
	cp package/dccoop.ini package/dccoop_hooks.txt package/README.txt $(D)/
	@ls -la $(D) $(D)/tools

clean:
	rm -rf $(B) $(D)
