# DC-Coop — 12-player LAN co-op for Dead Cells

A drag-and-drop mod that adds co-op for up to 12 players to Dead Cells (a
HashLink game), with no BepInEx, no mod loader, and no third-party libraries.

```
winmm.dll  dccoop.hdll  dccoop.ini  dccoop_hooks.txt   -> copy next to deadcells.exe
```

## How it works

1. **`winmm.dll`** is a proxy DLL. The game's `libhl.dll` imports `winmm.dll`
   at startup, and Windows loads it from the game folder first. The proxy
   forwards all 209 winmm exports to the real System32 DLL and redirects the
   game's read of `hlboot.dat` to a patched copy (`hlboot.dccoop.dat`,
   regenerated automatically when the game or the hook table changes). The
   original `hlboot.dat` is never modified.
2. **The patcher** (`src/hlbc`, `src/patchset`) parses HashLink bytecode,
   injects calls to co-op natives at chosen methods, generates a ghost-hero
   spawner function, and re-serializes. Hooks are resolved by class/method
   name and validated by signature. A hook that doesn't match is skipped and
   logged; it never crashes the game.
3. **`dccoop.hdll`** is loaded by HashLink itself (the standard native-library
   mechanism). It holds the co-op runtime: networking, sync, balance, PvP,
   aggro, and revive.

## Features

| Area | What it does |
|---|---|
| Netcode | UDP with a reliability layer (ack bitfields, ordered reliable messages, RTT/loss estimation), a challenge handshake, LAN discovery, and 30 Hz snapshots with quantization and a per-client priority accumulator |
| Sync | Heroes are owner-authoritative, so your own movement has zero input latency. Enemies are host-authoritative. Remote entities use cubic-Hermite snapshot interpolation with adaptive delay, and client clocks sync NTP-style (min-RTT filter, slewed) |
| Balance | Enemy HP, spawn density, damage, elite chance, and boss HP scale with player count. Scaling updates live on join/leave and preserves each enemy's HP ratio (see [docs/BALANCE.md](docs/BALANCE.md)) |
| Co-op | Downed state + revive (faster with more revivers, bleed-out shrinks with repeated downs), shared cells/gold, threat-based enemy targeting that spreads enemies across players |
| PvP | Per-player opt-in toggle (F9). Damage only happens when **both** players opted in. Supports FFA or teams, scaled PvP damage, hit immunity, and safe zones |

## Status (important)

Every component is implemented and tested **against real HashLink bytecode
and the real HashLink VM**, using a Haxe mock game with the same class shape
(`Game`, `en.Entity`, `en.Hero`, `en.Mob`). I don't have Dead Cells'
`hlboot.dat`, so the hook table's class/method/field names are unverified
against the actual game. They are the only game-specific part, and they're
editable in `dccoop_hooks.txt` / `dccoop.ini` without rebuilding. The next step
is in [docs/ROADMAP.md](docs/ROADMAP.md): run `hlbc-dump` on your
`hlboot.dat` and share the output (names only, no game code).

## Build

```sh
make test      # unit + 12-player loopback tests (gcc)
make e2e       # patch the Haxe mock and run host+client under HashLink (needs haxe, HL_DIR)
make windows   # dist/: winmm.dll, dccoop.hdll, tools/*.exe (needs mingw-w64)
```

More docs: [architecture](docs/ARCHITECTURE.md) · [netcode](docs/NETCODE.md) ·
[balance](docs/BALANCE.md) · [roadmap](docs/ROADMAP.md)
