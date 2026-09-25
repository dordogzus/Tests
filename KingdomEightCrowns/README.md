# Kingdom Eight Crowns

BepInEx 6 (be.753, IL2CPP) mod that extends Kingdom Two Crowns 2.1.4 online co-op from 2 to 8 players.

The source here was recovered from the shipped 0.14.20 binaries (the older hand-kept source was out of date) and then fixed. Versions: core `0.6.23-alpha`, companion `0.14.21-alpha`. The player-facing changelog is in [`package/README-KINGDOM-EIGHT-CROWNS.txt`](package/README-KINGDOM-EIGHT-CROWNS.txt).

## Layout

| Path | What |
| --- | --- |
| `src/KingdomEightCrowns` | Core plugin: native connection/player-ID limits, dynamic player bodies, appearance and player lookups. Compiled against the game's interop types. |
| `src/KingdomEightCrowns.AppearanceFlow` | Companion plugin: multi-peer Steam transport, join catch-up, relay, ruler sync, 8-slot menu roster. Reaches the game only through reflection. |
| `stubs/` | Compile-only signature stubs for `Assembly-CSharp`, `UnityEngine.CoreModule` and `Il2Cppmscorlib` (exactly the members the core binds to). Never shipped. |
| `package/` | Config files and README that go into the release zip. |
| `original/` | The shipped 0.14.20 package, untouched, as a reference. |
| `tools/ildiff` | Compares two builds' external bindings and per-method operands. |

## Build

Requires the .NET SDK 8 or newer. NuGet restores BepInEx be.753, HarmonyX 2.10.2 and Il2CppInterop 1.5.0.

```sh
./build.sh                     # -> dist/KingdomEightCrowns-<version>-2.1.4-FULL.zip
```

On Windows without bash, run the two `dotnet build` lines from `build.sh`. To compile the core against the real interop assemblies BepInEx generated for your game instead of the stubs:

```sh
./build.sh -p:KingdomInteropDir="C:/Games/Kingdom Two Crowns/BepInEx/interop"
```

If you add a new direct game-type reference to the core, either build against the real interop folder or add the member to `stubs/` with its exact interop signature.

## Verifying a build

```sh
dotnet run --project tools/ildiff -- original/BepInEx/plugins/KingdomEightCrowns/KingdomEightCrowns.dll build/core/KingdomEightCrowns.dll
```

`ildiff` needs the referenced assemblies next to each input (the BepInEx/Il2CppInterop DLLs and the stubs). It lists every external member a build binds to that the other does not. A rebuild of the unmodified decompiled source is binding-identical to the shipped DLLs, and the fixed builds add only `Il2CppObjectBase.Pointer` (core) and `BepInEx.Paths.ConfigPath` (companion).

The core only patches `GameAssembly.dll` when its SHA-256 matches the 2.1.4 build it was made for (`Plugin.SupportedGameAssemblySha256`). Native offsets must be re-derived for any other game version.
