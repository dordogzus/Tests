#!/bin/sh
# Loader test under Wine: fake game exe -> fake libhl.dll -> our winmm.dll.
set -eu
W=build/wine
rm -rf "$W" && mkdir -p "$W"
x86_64-w64-mingw32-gcc -shared -o "$W/libhl.dll" tests/wine/fakelibhl.c -lwinmm -Wl,--out-implib,"$W/libfakehl.a"
x86_64-w64-mingw32-gcc -o "$W/fakegame.exe" tests/wine/fakegame.c "$W/libfakehl.a"
cp build/test/mock.hl "$W/hlboot.dat"
WINE="${WINE:-/usr/lib/wine/wine64}"
export WINEDEBUG=-all WINEPREFIX="${WINEPREFIX:-/tmp/dccoop-wineprefix}"

echo "== without the mod"
(cd "$W" && "$WINE" fakegame.exe) | tr -d '\r' | tee "$W/vanilla.out"

echo "== with winmm.dll"
cp dist/winmm.dll "$W/"
(cd "$W" && "$WINE" fakegame.exe) | tr -d '\r' | tee "$W/modded.out"
cat "$W/dccoop_loader.log"

echo "== second launch uses the cache"
(cd "$W" && "$WINE" fakegame.exe) | tr -d '\r' > "$W/cached.out"
tail -1 "$W/dccoop_loader.log"

fail=0
grep -q "patched=0" "$W/vanilla.out" || { echo "FAIL vanilla"; fail=1; }
grep -q "patched=1" "$W/modded.out" || { echo "FAIL modded"; fail=1; }
grep -q "patched=1" "$W/cached.out" || { echo "FAIL cached"; fail=1; }
grep -q "libhl time=1" "$W/modded.out" || { echo "FAIL winmm forwarding"; fail=1; }
grep -q "using cached patched bytecode" "$W/dccoop_loader.log" || { echo "FAIL cache"; fail=1; }
[ "$fail" = 0 ] && echo "wine loader test: ok"
exit $fail
