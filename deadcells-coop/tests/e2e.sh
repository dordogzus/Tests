#!/bin/sh
# Runs the patched mock game as host and client in two HashLink processes.
set -eu
cd "$T"
export LD_LIBRARY_PATH=".:$(dirname "$HL")"
rm -f dccoop.log

echo "== vanilla bytecode"
"$HL" mock.hl

echo "== patched, co-op off"
DCCOOP_MODE=off "$HL" mock.coop.hl

PORT=$((48000 + $$ % 1000))
echo "== host + client on port $PORT"
DCCOOP_STDOUT=1 DCCOOP_MODE=host DCCOOP_PORT=$PORT DCCOOP_NAME=Host \
	MOCK_FRAMES=240 MOCK_DX=0.05 MOCK_HIT_FRAME=150 "$HL" mock.coop.hl > host.out 2>&1 &
HPID=$!
sleep 0.3
DCCOOP_STDOUT=1 DCCOOP_MODE=join DCCOOP_JOIN=127.0.0.1:$PORT DCCOOP_NAME=Client \
	MOCK_FRAMES=200 MOCK_DX=-0.05 MOCK_HIT_FRAME=150 "$HL" mock.coop.hl > client.out 2>&1
wait $HPID
echo "-- host"; cat host.out
echo "-- client"; cat client.out

fail=0
check() { if grep -q "$2" "$1"; then echo "ok   $1: $2"; else echo "FAIL $1: $2"; fail=1; fi; }
check host.out "ghost spawner registered"
check host.out "Player 1 joined: Client"
check host.out "spawned ghost for slot 1"
check client.out "spawned ghost for slot 0"
check host.out "heroes=2"
check client.out "heroes=2"
# 2 players: max life 100 -> 172; host hit 7 + client hit 7 forwarded = 158
check host.out "mob life=158 mob maxLife=172"
check client.out "mob maxLife=172"
exit $fail
