# Changelog

## 3.0.0 — "Frontier"

- Players: default cap 24 (max 64). Added a Steam `CreateLobby`/`SetLobbyMemberLimit` fallback
  next to the native patch, and extended the player colour palette so players 5+ get distinct
  colours.
- UI: new UGUI crew roster (a slot for every possible player, host/you tags, player colours,
  invite key, paging), a crew badge and the Frontier HUD. The IMGUI approach was dropped
  because `GUI.Box`/`DrawTexture` are stripped from this game build.
- Progression: supply-run packs now scale with crew size, are rebuilt from the world save
  (they survive restarts), and are recalculated when more players join.
- Garage: multiplier up to 12 (default 12). The floor is tiled at native scale using the whole
  vanilla footprint as the tile step (fixes overlapping A/B/C floor pieces), with 32-bit index
  meshes when needed. Surrounding station structures are moved to the new border. The root
  matrix is widened from the tick (the value-type method patch is no longer needed).
- Frontier: 13 new native parts plus 3 weapon previews, cloned from vanilla donors, with
  tier unlocks at 1/3/6 supply runs. Added the radiation/oxygen/plasma simulation.
- Fixes from v2.20.6: `SafeMode`/`FeatureProfile` no longer silently disable most modules by
  default. Removed the budget postfix that double-counted grants. Removed the diagnostic-only
  modules (SaveWatch, ShipWatch, TeleportWatch, CustomItems, ModBoard).
- Lobby: the game's own lobby member list gets vanilla-style rows (inert clones of its own rows) for
  players beyond the rows it shows.
- Garage: exact placements are also saved to `BepInEx/config/MorePlayers/placements.txt` on every
  world save and restored after a restart (GUID + part type matched).
- Repository: builds without the game through `stubs/`, xUnit tests for the logic, CI.
