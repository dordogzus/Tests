# Architecture

```
deadcells.exe ──loads──> libhl.dll ──imports──> winmm.dll (ours, from game dir)
      │                                             │ DllMain: forward exports, hook exe IAT
      │ _wfopen("hlboot.dat") ─────redirected──────>│ hlboot.dccoop.dat (patched, cached)
      ▼
 HashLink JIT loads patched bytecode ──native "dccoop"──> dccoop.hdll
                                                          ├─ natives.c  (HL object adapter)
                                                          └─ core/      (game-agnostic)
```

## Source layout

| Path | Role |
|---|---|
| `src/hlbc/hlbc.c` | Bytecode reader/writer for format versions 2–6. Unmodified files round-trip byte-for-byte |
| `src/hlbc/patch.c` | Op insertion with jump/switch/trap/debug-info/assign fix-ups, plus hooks: entry-notify, entry-cancel, arg-rewrite, exit |
| `src/patchset/dcpatch.c` | Hook table (text, overridable) + ghost spawner generator |
| `src/loader/loader.c` | `winmm.dll` proxy and `hlboot.dat` redirect |
| `src/hdll/natives.c` | `hlp_*` natives; reads/writes entity fields by name via `hl_dyn_*` |
| `src/core/` | Sockets, reliability, session, clock sync, interpolation, rules, and the co-op world model |
| `tools/` | `hlbc-dump` (class/method listing), `dccoop-patch` (offline fallback) |

## Hook points (default table)

| Native | Target | Mode | Purpose |
|---|---|---|---|
| `tick` | `Game.update` | notify | Network pump, interpolation, forwarded damage, PvP key |
| `level_start` | `Game.onLevelStart` | notify | Reset registries, broadcast level/seed |
| `hero_update` | `en.Hero.update` | cancel | Ghost heroes don't run local logic; downed heroes can't act |
| `mob_update` | `en.Mob.update` | cancel | Clients skip enemy AI and render host snapshots |
| `mob_init` | `en.Mob.init` | exit | Register enemy (spawn order = network id), scale HP |
| `damage` | `en.Entity.hit` | rewrite dmg | Authority routing, PvP, enemy damage scaling, downed instead of death |
| `dispose` | `en.Entity.dispose` | notify | Unregister entities |
| spawner | `en.Hero` constructor | generated | Builds ghost heroes with the game's own constructor |

### Ghost spawner

Remote players must be real hero entities so the game renders and collides
them. The patcher prefixes the hero constructor to capture its arguments
(from the local hero's construction). It then generates
`$dccoop_spawn(): Hero`, which re-runs `new Hero(capturedArgs)` under a guard
flag and hands the ghost to the hdll via a static closure registered on the
first tick. Scalar args and flags are boxed in `dyn` globals, because HashLink
JITs load globals pointer-sized (Haxe never emits scalar globals).

### Robustness rules

- Hooks are resolved by name and checked against a signature (`ooi:v` …).
  A mismatch skips only that hook.
- Patched bytecode carries a marker string, so it's never patched twice.
- If patching fails, the loader opens the vanilla `hlboot.dat`.
- The hdll uses only exported HashLink functions, not struct layouts, so it
  is independent of HashLink version details.
