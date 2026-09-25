# Roadmap and what I need from you

## 1. Bind to the real game (next step)

The only game-specific pieces are names: classes/methods in
`dccoop_hooks.txt` and fields in `dccoop.ini`. To verify them:

```
cd "C:\Program Files (x86)\Steam\steamapps\common\Dead Cells"
tools\hlbc-dump.exe hlboot.dat > dump.txt          (full listing)
tools\hlbc-dump.exe hlboot.dat en.Hero > hero.txt   (filtered)
```

Send `dump.txt` (or `hero.txt`, plus the `Game`, `en.Mob`, and `en.Entity` listings).
It contains only class, field, and method names with signatures, not game
code or assets. Also send `dccoop_loader.log` after one launch with the
mod installed. That confirms the `winmm.dll` injection and shows which
hooks matched.

With that I can finalize: the hero update/damage/death methods, level
generation seed (so clients build the identical level), enemy spawn
registration, the cell/gold pickup hook for shared rewards, and the input
hook for the PvP toggle in the game's own menu.

## 2. Content (needs the dump + data.cdb)

New items need entries in `data.cdb` (the game's CastleDB) plus sprites,
through Motion Twin's official modding tools. The runtime effects are
designed around the existing co-op core:

- **Rallying Banner**: aura that doubles revive speed for allies nearby.
- **Tether Totem**: splits damage taken between two linked players.
- **Duelist's Pennant**: toggles PvP opt-in from the inventory. This is the
  "vanilla UI" way to toggle, alongside F9.
- **Linked Blades** (weapon): bonus damage when an ally hit the same target
  within 1 s, rewarding focus fire.
- **Shepherd's Crook** (weapon): pulls an ally toward you, or revives at range
  with a longer channel.

## 3. Steam P2P

Only `src/core/netplat.c` and the addressing in `session.c` touch sockets.
Steam networking (via the game's own `steam.hdll` / `steam_api64.dll`) becomes
another transport behind the same channel/session API. The reliability,
snapshot, and authority layers stay unchanged.
