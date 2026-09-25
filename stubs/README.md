# Interop stubs (compile-only)

These projects produce assemblies named exactly like the game's Il2CppInterop
assemblies (`Assembly-CSharp`, `UnityEngine.CoreModule`, `Unity.Entities`, ...)
so the mod can be compiled and CI-checked without a copy of the game.

- They contain **only** members the mod uses, with signatures taken from the
  metadata of the v2.20.6 build (which was compiled against the real interop),
  or from public open-source mods that compile against the same game
  (Core.Initialize registration, Core._steam, Steam lobby helpers, UGUI).
- Bodies are `throw null`. They are never copied into the game.
- Anything the mod needs beyond this surface is resolved by name at runtime
  (HarmonyLib `AccessTools`/`Traverse`: Steamworks lobby calls, player colours,
  availability refresh, concrete part classes) so a missing member disables
  one feature instead of breaking the build.

Building against the real game (`-p:GameDir=...`) ignores this folder.
