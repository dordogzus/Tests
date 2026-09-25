using BepInEx.Configuration;
using BepInEx.Logging;
using System;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace MorePlayersMod;

/// <summary>
/// Moves the station structures that surround the garage (walls, cranes, lamps,
/// fences, buildings) from the vanilla border out to the enlarged border, so the
/// bigger yard is open ground instead of having the old surroundings standing
/// inside it. Meshes are translated, never scaled, so textures keep native size.
///
/// Rules: only static entities without a Parent (children follow their parent),
/// never ship parts (SCGuid) or dynamic bodies, never the floor or our collision box, never
/// planet-sized geometry. Offsets are applied per axis in station space, so the
/// layout of each side is preserved. Everything is restored on session reset.
/// </summary>
internal static class Perimeter
{
    private sealed class Moved
    {
        internal Entity Entity;
        internal bool UsedLocalTransform;
        internal Unity.Transforms.LocalTransform OriginalLocal;
        internal Unity.Transforms.LocalToWorld OriginalLtw;
        internal bool HadWorldBounds;
        internal CRPWorldBounds OriginalWorldBounds;
    }

    private const int MaxCandidates = 60000;
    private static ManualLogSource Log;
    private static ConfigEntry<bool> CfgEnabled;
    private static ConfigEntry<float> CfgRing;
    private static readonly Dictionary<Entity, Moved> MovedEntities = new Dictionary<Entity, Moved>();
    private static World _world;
    private static int _stationId;
    private static int _appliedMultiplier = 1;

    internal static void Bind(ConfigFile config, ManualLogSource log)
    {
        Log = log;
        CfgEnabled = config.Bind("Garage", "RelocateSurroundings", true,
            "Move the station structures around the garage out to the enlarged build border (translation only, native texture scale).");
        CfgRing = config.Bind("Garage", "SurroundingsRing", 3f,
            new ConfigDescription("How far from the vanilla border (in vanilla half-widths) structures count as garage surroundings.",
                new AcceptableValueRange<float>(1.5f, 6f)));
    }

    internal static void Apply(PlanetStation station, float3 min, float3 max, int multiplier, List<Entity> exclude)
    {
        if (CfgEnabled == null || !CfgEnabled.Value || station == null) return;
        var world = World.DefaultGameObjectInjectionWorld;
        if (world == null || !world.IsCreated) return;
        int stationId = station.GetInstanceID();
        if (_world != null && (_world.Pointer != world.Pointer || _stationId != stationId)) RestoreAll("active station changed");
        if (_appliedMultiplier == multiplier && _stationId == stationId && _world != null) return;
        if (MovedEntities.Count > 0) RestoreAll("multiplier changed");
        if (multiplier <= 1) return;

        var em = world.EntityManager;
        em.CompleteAllTrackedJobs();
        if (!em.Exists(station._entity) || !em.HasComponent<Unity.Transforms.LocalToWorld>(station._entity)) return;
        var stationLtw = em.GetComponentData<Unity.Transforms.LocalToWorld>(station._entity).Value;
        var stationM = ToMatrix(stationLtw);
        var inv = stationM.inverse;
        var excluded = new HashSet<Entity>(exclude) { station._entity };

        float3 center = (min + max) * 0.5f;
        float3 half = (max - min) * 0.5f;
        double ring = CfgRing.Value;
        float heightBand = Math.Max(20f, (max.y - min.y) * 3f);

        var query = em.CreateEntityQuery(new EntityQueryDesc[]
        {
            new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Unity.Transforms.LocalToWorld>() },
                Any = new[] { ComponentType.ReadOnly(Il2CppSystem.Type.GetType("CRPWorldBounds, Assembly-CSharp")),
                              ComponentType.ReadOnly<Unity.Physics.PhysicsCollider>() },
                // Dynamic bodies (players, animals, loose cargo) and ship parts are never moved.
                None = new[] { ComponentType.ReadOnly(Il2CppSystem.Type.GetType("SCGuid, Assembly-CSharp")),
                               ComponentType.ReadOnly<Unity.Transforms.Parent>(),
                               ComponentType.ReadOnly<Unity.Physics.PhysicsVelocity>() },
            }
        });
        int moved = 0, considered = 0;
        try
        {
            if (query.CalculateEntityCount() > MaxCandidates)
            {
                Log.LogWarning("Garage perimeter: too many static entities near the station; surroundings left in place.");
                return;
            }
            var entities = query.ToEntityArray(Unity.Collections.Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    var entity = entities[i];
                    if (excluded.Contains(entity) || MovedEntities.ContainsKey(entity)) continue;
                    var ltw = em.GetComponentData<Unity.Transforms.LocalToWorld>(entity);
                    var worldPos = new Vector3(ltw.Value.c3.x, ltw.Value.c3.y, ltw.Value.c3.z);
                    float sizeX = 0f, sizeZ = 0f;
                    bool hasBounds = em.HasComponent<CRPWorldBounds>(entity);
                    CRPWorldBounds wb = default;
                    if (hasBounds)
                    {
                        wb = em.GetComponentData<CRPWorldBounds>(entity);
                        var bmin = wb._worldBounds.Min;
                        var bmax = wb._worldBounds.Max;
                        var c = (bmin + bmax) * 0.5f;
                        worldPos = new Vector3(c.x, c.y, c.z);
                        var ls = inv.MultiplyVector(new Vector3(bmax.x - bmin.x, bmax.y - bmin.y, bmax.z - bmin.z));
                        sizeX = Math.Abs(ls.x);
                        sizeZ = Math.Abs(ls.z);
                    }
                    var local = inv.MultiplyPoint3x4(worldPos);
                    if (Math.Abs(local.y - center.y) > heightBand) continue;
                    if (!Logic.ZoneMath.IsSurroundingStructure(local.x, local.z, center.x, center.z, half.x, half.z, sizeX, sizeZ, ring)) continue;
                    considered++;
                    Logic.ZoneMath.PerimeterOffset(local.x, local.z, center.x, center.z, half.x, half.z, multiplier, out double dx, out double dz);
                    if (dx == 0 && dz == 0) continue;
                    var offset = stationM.MultiplyVector(new Vector3((float)dx, 0f, (float)dz));
                    var f = new float3(offset.x, offset.y, offset.z);
                    var record = new Moved { Entity = entity, OriginalLtw = ltw, HadWorldBounds = hasBounds, OriginalWorldBounds = wb };
                    if (em.HasComponent<Unity.Transforms.LocalTransform>(entity))
                    {
                        record.UsedLocalTransform = true;
                        record.OriginalLocal = em.GetComponentData<Unity.Transforms.LocalTransform>(entity);
                        var lt = record.OriginalLocal;
                        lt.Position = lt.Position + f;
                        em.SetComponentData(entity, lt);
                    }
                    var shifted = ltw;
                    shifted.Value.c3 = new float4(ltw.Value.c3.x + f.x, ltw.Value.c3.y + f.y, ltw.Value.c3.z + f.z, ltw.Value.c3.w);
                    em.SetComponentData(entity, shifted);
                    if (hasBounds && em.HasComponent<CRPLocalBounds>(entity))
                        em.SetComponentData(entity, new CRPWorldBounds(shifted.Value, em.GetComponentData<CRPLocalBounds>(entity)));
                    MovedEntities[entity] = record;
                    moved++;
                }
            }
            finally { entities.Dispose(); }
        }
        finally { query.Dispose(); }
        _world = world;
        _stationId = stationId;
        _appliedMultiplier = multiplier;
        Log.LogInfo($"Garage perimeter: moved {moved}/{considered} surrounding structures out to the x{multiplier} border (translation only).");
    }

    internal static void RestoreAll(string reason)
    {
        int restored = 0;
        try
        {
            if (_world != null && _world.IsCreated)
            {
                var em = _world.EntityManager;
                em.CompleteAllTrackedJobs();
                foreach (var m in MovedEntities.Values)
                {
                    try
                    {
                        if (!em.Exists(m.Entity)) continue;
                        if (m.UsedLocalTransform && em.HasComponent<Unity.Transforms.LocalTransform>(m.Entity))
                            em.SetComponentData(m.Entity, m.OriginalLocal);
                        em.SetComponentData(m.Entity, m.OriginalLtw);
                        if (m.HadWorldBounds && em.HasComponent<CRPWorldBounds>(m.Entity))
                            em.SetComponentData(m.Entity, m.OriginalWorldBounds);
                        restored++;
                    }
                    catch { }
                }
            }
        }
        catch (Exception e) { Log?.LogWarning($"Garage perimeter restore issue: {e.GetType().Name}: {e.Message}"); }
        if (MovedEntities.Count > 0) Log?.LogInfo($"Garage perimeter: restored {restored}/{MovedEntities.Count} structures ({reason}).");
        MovedEntities.Clear();
        _world = null;
        _stationId = 0;
        _appliedMultiplier = 1;
    }

    private static Matrix4x4 ToMatrix(float4x4 m) => new Matrix4x4(
        new Vector4(m.c0.x, m.c0.y, m.c0.z, m.c0.w),
        new Vector4(m.c1.x, m.c1.y, m.c1.z, m.c1.w),
        new Vector4(m.c2.x, m.c2.y, m.c2.z, m.c2.w),
        new Vector4(m.c3.x, m.c3.y, m.c3.z, m.c3.w));
}
