using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MorePlayersMod.Logic;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace MorePlayersMod;

/// <summary>
/// Registers Frontier blocks as real native parts. Each block is an Instantiate()
/// copy of a vanilla donor part (same component class, so native blueprint
/// serialization, saving, networking and garage UI already understand it),
/// renamed so its name hashes to a new SCPrefab, and re-tuned by multiplying
/// matching authoring fields before the game bakes the catalog in Core.Initialize.
/// Donors are never modified. Any failure hides only the affected block.
/// </summary>
internal static class FrontierBlocks
{
    internal sealed class Registered
    {
        internal FrontierBlock Block;
        internal EPC_SpaceshipComponent Clone;
        internal ulong Prefab;
        internal string DonorName;
    }

    private static ManualLogSource Log;
    private static ConfigEntry<bool> CfgEnabled;
    private static ConfigEntry<bool> CfgWeaponsPreview;
    private static ConfigEntry<bool> CfgUnlockAll;
    private static GameObject _holder;
    private static readonly Dictionary<IntPtr, Registered> ByPointer = new Dictionary<IntPtr, Registered>();
    private static readonly Dictionary<ulong, Registered> ByPrefab = new Dictionary<ulong, Registered>();
    private static readonly List<Registered> Blocks = new List<Registered>();

    internal static IReadOnlyList<Registered> All => Blocks;
    internal static bool UnlockAll => CfgUnlockAll != null && CfgUnlockAll.Value;

    internal static void Bind(ConfigFile config, ManualLogSource log)
    {
        Log = log;
        CfgEnabled = config.Bind("Frontier", "EnableFrontierBlocks", true,
            "Add the Frontier tech blocks (reactors, star core, plasma drive, life support, radiation shielding, hangar panels). Requires a game restart.");
        CfgWeaponsPreview = config.Bind("Frontier", "EnableWeaponsPreview", false,
            "Also add the weapon preview blocks (plasma lance, railgun, arc projector). They charge from the plasma bank but do not fire yet.");
        CfgUnlockAll = config.Bind("Frontier", "UnlockAllTiers", false,
            "Creative/testing: make every Frontier tier available immediately instead of unlocking through supply runs (1 / 3 / 6 deliveries).");
    }

    internal static void Install(Harmony harmony)
    {
        if (CfgEnabled == null || !CfgEnabled.Value) { Log.LogInfo("Frontier blocks disabled in config."); return; }
        var errors = FrontierCatalog.Validate(FrontierCatalog.All);
        if (errors.Count > 0) { Log.LogError("Frontier catalog invalid, blocks disabled: " + string.Join("; ", errors)); return; }
        try
        {
            harmony.Patch(AccessTools.Method(typeof(Core), nameof(Core.Initialize)),
                prefix: new HarmonyMethod(typeof(FrontierBlocks), nameof(BeforeInitialize)),
                postfix: new HarmonyMethod(typeof(FrontierBlocks), nameof(AfterInitialize)));
            harmony.Patch(AccessTools.Method(typeof(EPC_SpaceshipComponent), nameof(EPC_SpaceshipComponent.GetName)),
                prefix: new HarmonyMethod(typeof(FrontierBlocks), nameof(GetNamePrefix)));
            harmony.Patch(AccessTools.Method(typeof(EPC_SpaceshipComponent), nameof(EPC_SpaceshipComponent.GetDescription)),
                prefix: new HarmonyMethod(typeof(FrontierBlocks), nameof(GetDescriptionPrefix)));
            Log.LogInfo("Frontier blocks: registration hooks installed.");
        }
        catch (Exception e) { Log.LogError($"Frontier blocks unavailable: {e.GetType().Name}: {e.Message}"); }
    }

    internal static bool IsFrontier(EPC_SpaceshipComponent component) =>
        component != null && ByPointer.ContainsKey(component.Pointer);

    internal static bool TryGet(ulong prefab, out Registered registered) => ByPrefab.TryGetValue(prefab, out registered);

    private static bool GetNamePrefix(EPC_SpaceshipComponent __instance, ref string __result)
    {
        if (__instance == null || !ByPointer.TryGetValue(__instance.Pointer, out var r)) return true;
        __result = r.Block.Name;
        return false;
    }

    private static bool GetDescriptionPrefix(EPC_SpaceshipComponent __instance, ref string __result)
    {
        if (__instance == null || !ByPointer.TryGetValue(__instance.Pointer, out var r)) return true;
        __result = $"{r.Block.Description}\n[Frontier tier {r.Block.Tier} - unlocks after {FrontierCatalog.RunsForTier(r.Block.Tier)} supply run(s)]";
        return false;
    }

    private static void BeforeInitialize(Core __instance)
    {
        try
        {
            var originals = __instance._spaceshipComponents;
            if (originals == null || originals.Length < 1 || originals.Length > 8192) throw new InvalidOperationException("missing or oversized native catalog");
            if (Blocks.Count > 0)
            {
                // Core.Initialize can run again (new world); our clones are still in the array.
                if (_holder != null) _holder.SetActive(true);
                return;
            }

            var names = new List<string>();
            var byName = new Dictionary<string, EPC_SpaceshipComponent>(StringComparer.OrdinalIgnoreCase);
            var existing = new HashSet<ulong>();
            foreach (var item in originals)
            {
                if (item == null) continue;
                try { existing.Add(new SCPrefab(item)._prefab); } catch { }
                string n = null;
                try { n = item.GetName(); } catch { }
                if (string.IsNullOrEmpty(n) || byName.ContainsKey(n)) continue;
                byName[n] = item;
                names.Add(n);
            }

            _holder = new GameObject("MPM_FrontierAuthoring");
            _holder.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(_holder);

            var added = new List<EPC_SpaceshipComponent>();
            foreach (var block in FrontierCatalog.All)
            {
                if (block.Preview && !CfgWeaponsPreview.Value) continue;
                try
                {
                    var r = CloneBlock(block, names, byName, existing);
                    if (r == null) continue;
                    added.Add(r.Clone);
                    Blocks.Add(r);
                    ByPointer[r.Clone.Pointer] = r;
                    ByPrefab[r.Prefab] = r;
                }
                catch (Exception e) { Log.LogWarning($"Frontier block {block.Id} skipped: {e.GetType().Name}: {e.Message}"); }
            }
            if (added.Count == 0) { Log.LogWarning("Frontier blocks: no block could be registered."); return; }

            var grown = new Il2CppReferenceArray<EPC_SpaceshipComponent>(originals.Length + added.Count);
            for (int i = 0; i < originals.Length; i++) grown[i] = originals[i];
            for (int i = 0; i < added.Count; i++) grown[originals.Length + i] = added[i];
            _holder.SetActive(true); // visible to the synchronous conversion only; hidden again in the postfix
            __instance._spaceshipComponents = grown;
            Log.LogInfo($"Frontier blocks: {added.Count} parts appended to the native catalog ({originals.Length} -> {grown.Length}).");
        }
        catch (Exception e) { Log.LogError($"Frontier registration failed, vanilla catalog untouched: {e.GetType().Name}: {e.Message}"); }
    }

    private static Registered CloneBlock(FrontierBlock block, List<string> names, Dictionary<string, EPC_SpaceshipComponent> byName, HashSet<ulong> existing)
    {
        string donorName = FrontierCatalog.PickDonor(block, names);
        if (donorName == null || !byName.TryGetValue(donorName, out var donor) || donor == null)
        {
            Log.LogWarning($"Frontier block {block.Id}: no donor among [{string.Join(", ", block.Donors)}]; skipped.");
            return null;
        }
        ulong prefab = new SCPrefab(block.Id)._prefab;
        if (prefab == 0 || existing.Contains(prefab)) throw new InvalidOperationException("prefab hash collision");

        var root = UnityEngine.Object.Instantiate(donor.gameObject, _holder.transform, false);
        root.name = block.Id;
        var clone = root.GetComponent<EPC_SpaceshipComponent>();
        if (clone == null) { UnityEngine.Object.Destroy(root); throw new InvalidOperationException("clone lost its part component"); }
        if (new SCPrefab(clone)._prefab != prefab) { UnityEngine.Object.Destroy(root); throw new InvalidOperationException("clone name does not hash to the expected prefab"); }

        clone._mass = clone._mass * block.MassScale;
        clone._availableAmount = FrontierCatalog.Unlocked(block, 0, UnlockAll) ? block.Available : 0;
        var concrete = Concrete(clone);
        SelfMirror(concrete, clone);
        var scaled = ScaleStats(concrete, block);
        existing.Add(prefab);
        Log.LogInfo($"Frontier block '{block.Name}' <- donor '{donorName}' ({concrete.GetType().Name}); mass x{block.MassScale}; " +
                    (scaled.Count > 0 ? "tuned: " + string.Join(", ", scaled) : "no tunable stat fields matched (acts like its donor)") + ".");
        return new Registered { Block = block, Clone = clone, Prefab = prefab, DonorName = donorName };
    }

    /// <summary>Managed wrapper of the concrete Il2Cpp class (EPC_SCThruster, EPC_SCBattery, ...).</summary>
    private static object Concrete(EPC_SpaceshipComponent clone)
    {
        try
        {
            string full = clone.GetIl2CppType().FullName;
            var type = AccessTools.TypeByName(full);
            if (type != null && typeof(EPC_SpaceshipComponent).IsAssignableFrom(type))
                return Activator.CreateInstance(type, clone.Pointer);
        }
        catch { }
        return clone;
    }

    /// <summary>Mirrored placement of a clone must produce the clone, not the vanilla donor.</summary>
    private static void SelfMirror(object concrete, EPC_SpaceshipComponent clone)
    {
        try
        {
            var p = AccessTools.Property(concrete.GetType(), "_mirroredVersion");
            if (p == null || !p.CanWrite) return;
            if (p.GetValue(concrete) != null) p.SetValue(concrete, clone);
        }
        catch { }
    }

    private static List<string> ScaleStats(object target, FrontierBlock block)
    {
        var changed = new List<string>();
        if (block.StatScale.Count == 0) return changed;
        ScaleObject(target, block, "", changed, 0);
        return changed;
    }

    private static void ScaleObject(object target, FrontierBlock block, string prefix, List<string> changed, int depth)
    {
        var type = target.GetType();
        foreach (var p in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!p.CanRead || !p.CanWrite || p.GetIndexParameters().Length != 0) continue;
            if (!p.Name.StartsWith("_", StringComparison.Ordinal)) continue; // serialized game fields only
            var owner = p.DeclaringType;
            if (owner == null || owner.Assembly != typeof(EPC_SpaceshipComponent).Assembly) continue;
            try
            {
                float factor = FrontierCatalog.ScaleFor(block, p.Name);
                var pt = p.PropertyType;
                if (pt == typeof(float) && factor != 1f)
                {
                    p.SetValue(target, (float)p.GetValue(target) * factor);
                    changed.Add(prefix + p.Name);
                }
                else if (pt == typeof(Unity.Mathematics.float3) && factor != 1f)
                {
                    var v = (Unity.Mathematics.float3)p.GetValue(target);
                    p.SetValue(target, v * factor);
                    changed.Add(prefix + p.Name);
                }
                else if (depth == 0 && pt.Assembly == owner.Assembly && !typeof(UnityEngine.Object).IsAssignableFrom(pt))
                {
                    // One level into owned setup data (serializable classes/structs are deep-copied by Instantiate).
                    var inner = p.GetValue(target);
                    if (inner == null) continue;
                    int before = changed.Count;
                    ScaleObject(inner, block, p.Name + ".", changed, depth + 1);
                    if (pt.IsValueType && changed.Count != before) p.SetValue(target, inner);
                }
            }
            catch { }
        }
    }

    private static void AfterInitialize(Core __instance)
    {
        try
        {
            if (Blocks.Count == 0) return;
            int ok = 0;
            var map = __instance._componentsMap;
            foreach (var r in Blocks)
            {
                bool present = false;
                try { present = map != null && map.ContainsKey(new SCPrefab(r.Clone)); } catch { }
                if (present) ok++;
                else Log.LogWarning($"Frontier block '{r.Block.Name}' was not converted by the game; it will not appear in the garage.");
            }
            Log.LogInfo($"Frontier blocks: {ok}/{Blocks.Count} converted into the native part map.");
        }
        catch (Exception e) { Log.LogWarning($"Frontier verification skipped: {e.Message}"); }
        finally { if (_holder != null) _holder.SetActive(false); }
    }

    /// <summary>Tier gating: stock is 0 until the crew has completed enough supply runs.</summary>
    internal static void ApplyUnlocks(int supplyRuns)
    {
        bool changed = false;
        foreach (var r in Blocks)
        {
            try
            {
                if (r.Clone == null) continue;
                int wanted = FrontierCatalog.Unlocked(r.Block, supplyRuns, UnlockAll) ? r.Block.Available : 0;
                if (r.Clone._availableAmount != wanted) { r.Clone._availableAmount = wanted; changed = true; }
            }
            catch { }
        }
        if (changed) CrewParts.RequestRefresh();
    }
}
