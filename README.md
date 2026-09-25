# More Players: Frontier (v3.0.0) — Approximately Up mod

BepInEx 6 (IL2CPP) mod for **Approximately Up** that turns the 4-player game into
a crewed-ship game for up to **24 players**, gives every planet garage a **12×
wider and deeper build area**, and adds **Frontier tech** blocks: reactors, a
star core, a plasma drive, oxygen and radiation systems, and hangar panels.

> Status: this version compiles against the game's interop surface and its game-independent
> logic is unit tested. **It has not been run in the game yet.** Test it on a copy of your save
> first, and send `BepInEx/LogOutput.log` if something doesn't work. The log has a line for
> every module saying what it did.

## Features

| Area | What you get |
| --- | --- |
| **24 players** | Lobby size and the server's "server is full" check go from 4 to `MaxPlayers` (default 24, up to 64). This uses a native patch plus a Steam `CreateLobby` / `SetLobbyMemberLimit` fallback. Players 5+ each get their own distinct colour: `Core._availablePlayerColors` is extended, and the vanilla colours stay. |
| **Vanilla lobby list** | The game's own lobby member list gets a row for every player it doesn't list yet. Each added row is a copy of one of the game's own rows, so it keeps the vanilla look; its buttons, avatar and row scripts are disabled so it can't act on the wrong player. If the game already lists everyone, nothing is changed. |
| **Crew roster UI** | `F6` opens a roster with a slot for every possible player (3 columns × 8 rows for 24). Each slot shows the player's name, colour, `HOST`/`YOU` tags and open slots. `F5` opens the Steam invite dialog, and `PgUp`/`PgDn` pages through larger lobbies. While you're in a lobby, a `CREW n / 24` badge shows in the top-right corner. |
| **Seats you earn** | Nothing is handed out at start. Each vanilla **package delivery** pays a pack sized for the current crew: Moon runs pay 4 seats + 2 consoles per 4 players, Earth runs pay consoles and power, and outer-rim runs pay frames, glass, thrusters and seats. Progress is rebuilt from the world save, so it survives restarts. When more players join, the packs are recalculated for the bigger crew. |
| **12× build area** | The yellow build border for the active station is widened on X and Z (height stays vanilla, and the area stays centred). The floor is tiled 12×12 at native texture scale, and a solid floor collider is added. The station structures around the garage (walls, lamps, cranes…) move out to the new border without being scaled. Exact placements are restored after a flight → garage return **and after a game restart**: the game's compact save loses precision far out, so every world save also writes `BepInEx/config/MorePlayers/placements.txt`. |
| **Frontier blocks** | 13 new parts plus 3 optional weapon previews. Each is a real native part cloned from a vanilla donor, then renamed and re-tuned (see below). They unlock in tiers after **1 / 3 / 6** supply runs. |
| **Life support** | Ships that carry Frontier life-support blocks are simulated. Reactors leak radiation, **radiation walls** within 6 m absorb some of it, and **radiation vents** pipe some of it outside. **Oxygen generators and scrubbers** each support a number of crew. `F7` shows the HUD, and the screen tints when radiation or oxygen gets dangerous. |

### Frontier blocks

| Tier | Block | Donor | Effect |
| --- | --- | --- | --- |
| 1 | FR-1 Fission Reactor | Large Battery | 4× power/capacity, radiation 1 |
| 1 | Lead-Lined Radiation Wall | Frame | 4× mass, absorbs ~22% per wall near a reactor |
| 1 | Radiation Vent Pipe | Frame | exhausts ~35% of what remains |
| 1 | O2 Electrolysis Generator | Battery | supports 4 crew |
| 1 | CO2 Scrubber | Battery | supports 2 crew |
| 2 | FX-7 Fusion Reactor | Large Battery | 12× power, radiation 3 |
| 2 | Plasma Drive | Large Electric Thruster | 6× thrust |
| 2 | Energy Gate Panel | Glass | transparent hangar wall panel |
| 2 | Fire Control Monitor | Datameter | bridge display for the Frontier HUD |
| 3 | Star Core Generator | Large Battery | 40× power, radiation 8 |
| 3 | Particle Accelerator Ring | Large Battery | charges the plasma bank (needs a reactor) |
| 3 | Plasma Condenser | Large Battery | +100 plasma bank capacity |
| 3 | Plasma Lance / 80mm Railgun / Tesla Arc *(preview, off by default)* | — | charge from the plasma bank; **they don't fire yet** |

How the blocks are added: in a prefix on `Core.Initialize`, each donor's GameObject is
instantiated, renamed so its name hashes to a new `SCPrefab`, and appended to
`Core._spaceshipComponents`. The game then bakes, saves and syncs the new block like any
vanilla part (the same technique the public AU-08 computer mod uses). Matching `float`
authoring fields (`*power*`, `*capacity*`, `*force*`…) are multiplied before baking.
The log lists the fields that were actually tuned for each block. If a donor has no
matching field, the block behaves like its donor.

## Install

1. Install BepInEx 6 IL2CPP (bleeding edge) into the game and start the game once, so
   `BepInEx/interop` gets generated.
2. Install the [.NET 6+ SDK](https://dotnet.microsoft.com/download).
3. Drag the game EXE (or game folder) onto **`INSTALL - DRAG GAME HERE.bat`**. The installer
   builds the mod against *your* game files, backs up any older DLL and copies the new one to
   `BepInEx/plugins`.

Every player should run the same version. The **host's** `MaxPlayers` decides the lobby size.

Manual build: `dotnet build src/MorePlayersMod -c Release -p:GameDir="C:\...\Approximately Up"`.
Without `GameDir` the project compiles against `stubs/`, which is only good for checking that
the code compiles. Use the game build for the DLL you actually play with.

## Keys

| Key | Action |
| --- | --- |
| `F6` | crew roster (it also opens automatically when you enter a lobby) |
| `F5` | Steam invite dialog (while the roster is open) |
| `PgUp` / `PgDn` | roster pages (lobbies larger than one screen) |
| `F7` | Frontier ship HUD |

## Configuration (`BepInEx/config/com.bondi.moreplayers.cfg`)

| Section | Key | Default | Meaning |
| --- | --- | --- | --- |
| General | `MaxPlayers` | 24 | lobby size + server accept limit (2–64) |
| General | `SafeMode` | false | keep only the lobby cap patch (troubleshooting) |
| Garage | `ZoneMultiplier` | 12 | build-area width/depth multiplier (1–12) |
| Garage | `RelocateSurroundings` | true | move station structures out to the new border |
| Garage | `SurroundingsRing` | 3 | how far out (in vanilla half-widths) structures count as surroundings |
| Garage | `PreserveExtendedTransforms` | true | restore exact far placements after flight → garage |
| Garage | `PersistExactPlacements` | true | keep exact placements across restarts (`MorePlayers/placements.txt`) |
| UI | `ExtendVanillaLobbyList` | true | add vanilla-style rows to the game's lobby list for players beyond its rows |
| Contracts | `EnableSupplyRuns` | true | package deliveries pay crew packs |
| Frontier | `EnableFrontierBlocks` | true | register Frontier parts (restart required) |
| Frontier | `EnableLifeSupport` | true | radiation/oxygen simulation + HUD |
| Frontier | `EnableWeaponsPreview` | false | also register the weapon preview parts |
| Frontier | `UnlockAllTiers` | false | creative/testing: all tiers immediately |
| UI | `RosterKey` / `InviteKey` / `FrontierHudKey` | F6 / F5 / F7 | key bindings |

Configs from v2.x are migrated once (12 → 24 players, ×2 → ×12 garage, SafeMode/diagnostic
profiles off).

## Known limits

- **Native patch offsets** are for the build the v2.20.6 mod targeted (Unity 6000.4.7f1). After a
  game update the byte check fails safely and the log says so. The Steam lobby fallback still
  raises the lobby size, but the server-full check then stays at 4.
- **The vanilla lobby list** is found at runtime from the texts that show member names. If the game draws that list in a way the mod can't detect, the log says nothing about extending it, and the F6 roster is still the 24-slot view. A very long list can run past the bottom of its panel.
- **Life support effects** are warnings, the HUD and the screen tint only. The mod doesn't change
  vanilla player health.
- **Weapons** are previews: they're parts plus plasma charging. Firing needs a projectile and
  damage layer that hasn't been written yet.
- **Far placements across a restart** are restored from the side-car file, matched by part GUID and
  type. The file is written on every world save; if the game is closed without saving, the
  last saved layout is restored, just as the game itself would.
- Each Frontier block's stat tuning depends on the donor's field names in your build. Check the
  log line `Frontier block '…' <- donor '…'; tuned: …`.

## Repository layout

```
src/MorePlayersMod/            the plugin
  Logic/                       game-independent rules (unit tested)
  Players/                     lobby cap, Steam roster access, player colours
  Garage/ + Zone.cs + ExtendedTransformStore.cs   12x build area
  Frontier/                    block registration + life-support runtime
  UI/ModOverlay.cs             UGUI roster / badge / HUD
stubs/                         compile-only interop stand-ins (never shipped)
tests/MorePlayersMod.Tests/    xUnit tests for Logic/
```

`dotnet test` runs the logic tests. CI builds against the stubs and runs the tests.
