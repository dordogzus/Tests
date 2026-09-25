using BepInEx.Configuration;
using BepInEx.Logging;
using MorePlayersMod.Logic;
using System;
using System.Collections.Generic;
using Unity.Entities;
using UnityEngine;

namespace MorePlayersMod;

/// <summary>Snapshot shown by the Frontier HUD.</summary>
internal sealed class FrontierStatus
{
    internal bool Active;
    internal int Reactors, Shields, Vents, Accelerators, Condensers, Weapons, Monitors;
    internal double DoseRate;
    internal double Dose;
    internal HazardLevel Radiation;
    internal double Oxygen = 100;
    internal double OxygenCapacity;
    internal int Crew;
    internal HazardLevel OxygenHazard;
    internal double Plasma, PlasmaCapacity;
    internal int SupplyRuns;
}

/// <summary>
/// Client-side simulation of Frontier systems from the synchronized ECS ship
/// state: every player computes radiation at their own camera and the ship's
/// oxygen/plasma balance from the same replicated block positions, so no extra
/// network traffic is needed. Effects are HUD warnings and screen tint; the mod
/// does not touch vanilla player health.
/// </summary>
internal static class FrontierRuntime
{
    private const float Interval = 0.25f;
    private static ManualLogSource Log;
    private static ConfigEntry<bool> CfgLifeSupport;
    private static float _next;
    private static float _last;
    private static readonly List<LifeSupportBlock> Scratch = new List<LifeSupportBlock>();
    private static readonly List<LobbyMember> Members = new List<LobbyMember>();
    internal static readonly FrontierStatus Status = new FrontierStatus();

    internal static void Bind(ConfigFile config, ManualLogSource log)
    {
        Log = log;
        CfgLifeSupport = config.Bind("Frontier", "EnableLifeSupport", true,
            "Simulate radiation (reactors, shield walls, vents) and oxygen (generators, scrubbers) for ships that carry Frontier life-support blocks.");
    }

    internal static void ResetSession()
    {
        Status.Dose = 0;
        Status.Oxygen = 100;
        Status.Plasma = 0;
        Status.Active = false;
    }

    internal static void Tick()
    {
        float now = Time.realtimeSinceStartup;
        if (now < _next) return;
        _next = now + Interval;
        float dt = _last > 0 ? Math.Min(1f, now - _last) : 0f;
        _last = now;

        int runs = Contracts.SupplyRuns;
        Status.SupplyRuns = runs;
        FrontierBlocks.ApplyUnlocks(runs);
        if (FrontierBlocks.All.Count == 0 || CfgLifeSupport == null || !CfgLifeSupport.Value) { Status.Active = false; return; }

        try { Collect(); }
        catch (Exception e) { Status.Active = false; Log?.LogDebug($"Frontier scan skipped: {e.Message}"); return; }

        Status.Active = LifeSupportModel.HasLifeSupport(Scratch) || Status.Accelerators > 0 || Status.Weapons > 0 || Status.Monitors > 0;
        if (!Status.Active) return;

        var cam = Camera.main;
        if (cam != null)
        {
            var p = cam.transform.position;
            Status.DoseRate = LifeSupportModel.DoseRate(new Vec3(p.x, p.y, p.z), Scratch);
        }
        Status.Radiation = LifeSupportModel.RadiationLevel(Status.DoseRate);
        Status.Dose = Math.Max(0, Status.Dose + (Status.DoseRate > 0.05 ? Status.DoseRate : -0.2) * dt);

        Status.Crew = 1;
        if (SteamLobby.TryRead(Members, out _, out _) && Members.Count > 0) Status.Crew = Members.Count;
        Status.OxygenCapacity = LifeSupportModel.OxygenCapacity(Scratch);
        bool breathingSystem = Status.OxygenCapacity > 0;
        Status.Oxygen = breathingSystem ? LifeSupportModel.StepOxygen(Status.Oxygen, Status.OxygenCapacity, Status.Crew, dt) : 100;
        Status.OxygenHazard = LifeSupportModel.OxygenLevel(Status.Oxygen);

        Status.PlasmaCapacity = PlasmaModel.Capacity(Status.Condensers);
        Status.Plasma = PlasmaModel.Step(Status.Plasma, Status.Accelerators, Status.Condensers, Status.Reactors, dt);
    }

    private static void Collect()
    {
        Scratch.Clear();
        Status.Reactors = Status.Shields = Status.Vents = Status.Accelerators = Status.Condensers = Status.Weapons = Status.Monitors = 0;
        var world = World.DefaultGameObjectInjectionWorld;
        if (world == null || !world.IsCreated) return;
        var em = world.EntityManager;
        var prefabType = Il2CppSystem.Type.GetType("SCPrefab, Assembly-CSharp");
        if (prefabType == null) return;
        var query = em.CreateEntityQuery(new[]
        {
            ComponentType.ReadOnly(prefabType),
            ComponentType.ReadOnly<Unity.Transforms.LocalToWorld>(),
        });
        try
        {
            if (query.CalculateEntityCount() > 20000) return;
            var entities = query.ToEntityArray(Unity.Collections.Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    var entity = entities[i];
                    ulong prefab = em.GetComponentData<SCPrefab>(entity)._prefab;
                    if (!FrontierBlocks.TryGet(prefab, out var r)) continue;
                    var c3 = em.GetComponentData<Unity.Transforms.LocalToWorld>(entity).Value.c3;
                    var pos = new Vec3(c3.x, c3.y, c3.z);
                    var block = r.Block;
                    if (block.Role != LifeSupportRole.None) Scratch.Add(new LifeSupportBlock(block.Role, pos, block.RoleValue));
                    switch (block.Role)
                    {
                        case LifeSupportRole.Reactor: Status.Reactors++; break;
                        case LifeSupportRole.Shield: Status.Shields++; break;
                        case LifeSupportRole.Vent: Status.Vents++; break;
                    }
                    if (block.Id == "MPM_ParticleAccelerator") Status.Accelerators++;
                    else if (block.Id == "MPM_PlasmaCondenser") Status.Condensers++;
                    else if (block.Id == "MPM_FireControlMonitor") Status.Monitors++;
                    else if (block.Category == FrontierCategory.Weapons) Status.Weapons++;
                }
            }
            finally { entities.Dispose(); }
        }
        finally { query.Dispose(); }
    }
}
