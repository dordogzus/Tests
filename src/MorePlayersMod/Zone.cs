using BepInEx.Configuration;
using BepInEx.Logging;
using System;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace MorePlayersMod;

internal static class Zone
{
    private static ManualLogSource Log;
    private static ConfigEntry<float> CfgMult;
    private static ConfigEntry<bool> CfgExpand;
    private static ConfigEntry<bool> CfgDiag;
    private static readonly HashSet<int> ScaledStations = new HashSet<int>();
    private static readonly Dictionary<int, StationState> StationOriginals = new Dictionary<int, StationState>();
    private static readonly List<RootState> RootHistory = new List<RootState>();
    private static readonly Dictionary<int, FloorTransform> FloorTransforms = new Dictionary<int, FloorTransform>();
    private static readonly HashSet<string> FloorMessages = new HashSet<string>();
    private static int _rootPatchLogCount;
    private static int _warningCount;
    private static int _diagnosticLines;
    private static string _lastYardSig;
    private static string _lastProbeSig;
    private static Entity _activeStationEntity;
    private static bool _suspended;

    private sealed class FloorTransform
    {
        internal CRPRenderer Renderer;
        internal PlanetStation Station;
        internal int MeshId;
        internal Unity.Entities.World World;
        internal Unity.Entities.Entity Entity;
        internal bool SharedDraw;
        internal bool OriginalSharedDraw;
        internal bool Dirty;
        internal CRPRendererData OriginalDraw;
        internal CRPRendererData AppliedDraw;
        internal CRPLocalBounds OriginalLocal;
        internal CRPLocalBounds AppliedLocal;
        internal CRPWorldBounds OriginalWorld;
        internal CRPWorldBounds AppliedWorld;
        internal Matrix4x4 OriginalLocalToWorld;
        internal Mesh OriginalMesh;
        internal Mesh AppliedMesh;
        internal float Applied;
        internal bool Blocked;
    }

    private sealed class StationState
    {
        internal PlanetStation Station;
        internal float3 Min;
        internal float3 Max;
        internal float Applied;
        internal bool Diagnosed;
        internal Unity.Entities.World CollisionWorld;
        internal Unity.Entities.Entity CollisionEntity;
        internal float CollisionApplied;
    }

    private sealed class RootState
    {
        internal double4x4 Input;
        internal double4x4 Output;
        internal float3 BaseSize;
        internal float3 TargetSize;
    }

    internal static bool Done => ScaledStations.Count > 0;

    internal static float Multiplier
    {
        get
        {
            float value = 2f;
            try { if (CfgMult != null) value = CfgMult.Value; } catch { }
            if (!Finite((double)value)) value = 2f;
            value = (float)Math.Round(value, MidpointRounding.AwayFromZero);
            return Math.Max(1f, Math.Min(4f, value));
        }
    }

    internal static void Bind(ConfigFile config, ManualLogSource log)
    {
        Log = log;
        CfgMult = config.Bind("Garage", "ZoneMultiplier", 2f,
            "Garage build-volume width and depth scale as a whole number from 1 to 4; height stays vanilla and the area remains centered.");
        CfgExpand = config.Bind("Garage", "EnableGarageExpansion", true,
            "Enable deferred garage floor/collider expansion only while the garage is active; planet scene loading never mutates station renderers.");
        CfgDiag = config.Bind("Garage", "DiagPlatforms", false,
            "Bounded garage floor renderer, mesh, transform, and physics registration diagnostics.");
    }

    internal static void Migrate()
    {
        try
        {
            CfgMult.Value = 2f;
            CfgExpand.Value = true;
            CfgDiag.Value = false;
            Log.LogInfo("Garage: v2.20 config migrated (centered whole-number width/depth scaling, tiled floor meshes, registered static collision).");
        }
        catch (Exception e) { Warn($"Garage migration issue: {e.GetType().Name}: {e.Message}"); }
    }

    internal static void ResetSession()
    {
        try
        {
            foreach (var floor in FloorTransforms.Values) RestoreFloorVisual(floor, "session reset");
            foreach (var station in StationOriginals.Values)
            {
                if (station.Station != null)
                {
                    try
                    {
                        station.Station._garageMin = station.Min;
                        station.Station._garageMax = station.Max;
                    }
                    catch { }
                }
                DestroyStationCollision(station);
            }
            FloorTransforms.Clear();
            StationOriginals.Clear();
            ScaledStations.Clear();
            RootHistory.Clear();
            FloorMessages.Clear();
            _rootPatchLogCount = 0;
            _warningCount = 0;
            _diagnosticLines = 0;
            _lastYardSig = null;
            _lastProbeSig = null;
            _activeStationEntity = default;
            _suspended = false;
        }
        catch (Exception e) { Warn($"Garage reset issue: {e.GetType().Name}: {e.Message}"); }
    }

    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    private static bool Finite(float3 value) => Finite((double)value.x) && Finite((double)value.y) && Finite((double)value.z);
    private static bool Finite(double4 value) => Finite(value.x) && Finite(value.y) && Finite(value.z) && Finite(value.w);
    private static bool Finite(double4x4 value) => Finite(value.c0) && Finite(value.c1) && Finite(value.c2) && Finite(value.c3);
    private static bool Near(float3 a, float3 b, float epsilon) =>
        Math.Abs(a.x - b.x) <= epsilon && Math.Abs(a.y - b.y) <= epsilon && Math.Abs(a.z - b.z) <= epsilon;
    private static bool Same(double4 a, double4 b) => a.x == b.x && a.y == b.y && a.z == b.z && a.w == b.w;
    private static bool Same(double4x4 a, double4x4 b) =>
        Same(a.c0, b.c0) && Same(a.c1, b.c1) && Same(a.c2, b.c2) && Same(a.c3, b.c3);
    private static bool ValidBounds(float3 min, float3 max) =>
        Finite(min) && Finite(max) && Finite(max - min) &&
        max.x - min.x > 0.01f && max.y - min.y > 0.01f && max.z - min.z > 0.01f;
    private static float3 HorizontalSize(float3 size, float multiplier)
    {
        float3 target = size;
        target.x *= multiplier;
        target.z *= multiplier;
        return target;
    }

    private static void Warn(string text)
    {
        if (_warningCount >= 12) return;
        _warningCount++;
        try { Log.LogWarning(text); } catch { }
    }

    internal static void AdjustRootMatrix(ref double4x4 matrix, ref float3 boundsSize)
    {
        try
        {
            if (!Finite(matrix) || !ValidBounds(default(float3), boundsSize)) return;
            float multiplier = Multiplier;
            double4x4 input = matrix;
            float3 baseSize = boundsSize;
            int match = -1;
            for (int i = RootHistory.Count - 1; i >= 0; i--)
            {
                var previous = RootHistory[i];
                if ((Same(matrix, previous.Output) && Near(boundsSize, previous.TargetSize, 0f)) ||
                    (Same(matrix, previous.Input) && Near(boundsSize, previous.BaseSize, 0f)))
                {
                    input = previous.Input;
                    baseSize = previous.BaseSize;
                    match = i;
                    break;
                }
            }
            if (match < 0)
            {
                foreach (var previous in RootHistory)
                {
                    if (Near(previous.BaseSize, previous.TargetSize, 0f) || !Near(boundsSize, previous.TargetSize, 0.02f)) continue;
                    Warn("Garage root unchanged: size matches a prior expanded root but matrix differs; refusing possible double-scaling.");
                    return;
                }
                foreach (var station in StationOriginals.Values)
                {
                    if (station.Station == null || station.Applied <= 1f) continue;
                    if (!Near(boundsSize, HorizontalSize(station.Max - station.Min, station.Applied), 0.02f)) continue;
                    Warn("Garage root unchanged: input size matches an expanded station; its matrix provenance is unknown.");
                    return;
                }
                if (multiplier <= 1f) return;
            }
            float3 target = HorizontalSize(baseSize, multiplier);
            if (!Finite(target)) return;
            double3 shift = new double3(
                -((double)target.x - baseSize.x) * 0.5,
                0,
                -((double)target.z - baseSize.z) * 0.5);
            double4x4 output = input;
            output.c3.x += input.c0.x * shift.x + input.c2.x * shift.z;
            output.c3.y += input.c0.y * shift.x + input.c2.y * shift.z;
            output.c3.z += input.c0.z * shift.x + input.c2.z * shift.z;
            if (!Finite(output)) return;
            matrix = output;
            boundsSize = target;
            if (match >= 0) RootHistory.RemoveAt(match);
            if (RootHistory.Count >= 64) RootHistory.RemoveAt(0);
            RootHistory.Add(new RootState { Input = input, Output = output, BaseSize = baseSize, TargetSize = target });
            if (_rootPatchLogCount < 4)
            {
                _rootPatchLogCount++;
                Log.LogInfo($"Garage root centered: base={baseSize} target={target}, exact prior call={match >= 0}.");
            }
        }
        catch (Exception e) { Warn($"Garage root centering skipped: {e.GetType().Name}: {e.Message}"); }
    }

    internal static void ApplyRootAtRuntime()
    {
        if (_suspended || !ExpansionEnabled || !ExtendedTransformStore.GarageBuildMode) return;
        try
        {
            var world = Unity.Entities.World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated) return;
            var em = world.EntityManager;
            var type = Il2CppSystem.Type.GetType("GarageGrabberSingleton, Assembly-CSharp");
            if (type == null) return;
            var query = em.CreateEntityQuery(new[] { Unity.Entities.ComponentType.ReadWrite(type) });
            try
            {
                if (query.CalculateEntityCount() != 1) return;
                var entity = query.GetSingletonEntity();
                var singleton = em.GetComponentData<GarageGrabberSingleton>(entity);
                var matrix = singleton._Matrix;
                var size = singleton._boundsSize;
                if (!Finite(matrix) || !ValidBounds(default(float3), size)) return;
                AdjustRootMatrix(ref matrix, ref size);
                if (Same(matrix, singleton._Matrix) && Near(size, singleton._boundsSize, 0f)) return;
                singleton.SetRootMatrix(matrix, size);
                em.SetComponentData(entity, singleton);
                Log.LogInfo($"Garage root expanded safely after garage activation: bounds={size}.");
            }
            finally { query.Dispose(); }
        }
        catch (Exception e) { Warn($"Garage runtime root expansion skipped: {e.GetType().Name}: {e.Message}"); }
    }

    internal static void ScaleYard()
    {
        try { EnsureScaled(); } catch { }
    }

    private static bool TryGetActiveStation(EntityManager em, out Entity entity)
    {
        entity = default;
        try
        {
            var type = Il2CppSystem.Type.GetType("MainPlanetStation, Assembly-CSharp");
            if (type == null) return false;
            var query = em.CreateEntityQuery(new[] { Unity.Entities.ComponentType.ReadOnly(type) });
            try
            {
                if (query.CalculateEntityCount() != 1) return false;
                entity = query.GetSingletonEntity();
                return em.Exists(entity);
            }
            finally { query.Dispose(); }
        }
        catch { return false; }
    }

    internal static bool ExpansionEnabled
    {
        get
        {
            try { return CfgExpand != null && CfgExpand.Value; } catch { return false; }
        }
    }

    internal static void SuspendForTransition()
    {
        _suspended = true;
    }

    internal static void ResumeForBuild()
    {
        if (!_suspended) return;
        _suspended = false;
        foreach (var station in StationOriginals.Values) DestroyStationCollision(station);
    }

    internal static void EnsureScaled()
    {
        try
        {
            if (_suspended || !ExpansionEnabled) return;
            var stale = new List<int>();
            foreach (var entry in StationOriginals)
                if (entry.Value.Station == null) stale.Add(entry.Key);
            foreach (int id in stale)
            {
                DestroyStationCollision(StationOriginals[id]);
                StationOriginals.Remove(id);
                ScaledStations.Remove(id);
            }
            var world = Unity.Entities.World.DefaultGameObjectInjectionWorld;
            Entity activeStation = default;
            bool hasActiveStation = world != null && world.IsCreated && TryGetActiveStation(world.EntityManager, out activeStation);
            _activeStationEntity = hasActiveStation ? activeStation : default;
            if (!ExtendedTransformStore.GarageBuildMode)
            {
                foreach (var station in StationOriginals.Values) DestroyStationCollision(station);
                return;
            }
            float multiplier = Multiplier;
            int changed = 0;
            foreach (var station in Resources.FindObjectsOfTypeAll<PlanetStation>())
            {
                try
                {
                    if (!Live(station)) continue;
                    int id = station.GetInstanceID();
                    if (!hasActiveStation || !station._entity.Equals(activeStation))
                    {
                        if (StationOriginals.TryGetValue(id, out var inactive)) DestroyStationCollision(inactive);
                        continue;
                    }
                    if (!StationOriginals.TryGetValue(id, out var original) || original.Station != station)
                    {
                        if (!ValidBounds(station._garageMin, station._garageMax)) continue;
                        original = new StationState { Station = station, Min = station._garageMin, Max = station._garageMax };
                        StationOriginals[id] = original;
                    }
                    float3 center = original.Min * 0.5f + original.Max * 0.5f;
                    float3 half = (original.Max - original.Min) * 0.5f;
                    half.x *= multiplier;
                    half.z *= multiplier;
                    if (!ValidBounds(center - half, center + half)) continue;
                    station._garageMin = center - half;
                    station._garageMax = center + half;
                    if (original.Applied != multiplier) changed++;
                    original.Applied = multiplier;
                    ScaledStations.Add(id);
                }
                catch (Exception e) { Warn($"Garage station skipped: {e.GetType().Name}: {e.Message}"); }
            }
            if (changed > 0)
                Log.LogInfo($"Garage active station centered: {changed} build volume(s) now x{multiplier} horizontally; vertical bounds unchanged.");
        }
        catch (Exception e) { Warn($"Garage station scaling skipped: {e.GetType().Name}: {e.Message}"); }
    }

    private static bool Live(Component component) => component != null && component.gameObject != null &&
        component.gameObject.activeInHierarchy && component.gameObject.scene.IsValid() && component.gameObject.scene.isLoaded;

    internal static void ScaleBoundaryVisuals()
    {
        try
        {
            if (_suspended || !ExpansionEnabled) return;
            EnsureScaled();
            var dead = new List<int>();
            foreach (var entry in FloorTransforms)
                if (entry.Value.Renderer == null || entry.Value.World == null || !entry.Value.World.IsCreated ||
                    !entry.Value.World.EntityManager.Exists(entry.Value.Entity)) dead.Add(entry.Key);
            foreach (int id in dead)
            {
                var floor = FloorTransforms[id];
                RestoreFloorVisual(floor, "renderer or ECS world disappeared");
                if (floor.Station != null) DestroyStationCollision(StationOriginals.TryGetValue(floor.Station.GetInstanceID(), out var station) ? station : null);
                FloorTransforms.Remove(id);
            }
            if (!ExtendedTransformStore.GarageBuildMode)
            {
                foreach (var station in StationOriginals.Values) DestroyStationCollision(station);
                return;
            }
            var world = Unity.Entities.World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated || !TryGetActiveStation(world.EntityManager, out var activeStation)) return;
            float multiplier = Multiplier;
            foreach (var station in StationOriginals.Values)
            {
                if (!Live(station.Station) || !station.Station._entity.Equals(activeStation)) continue;
                int candidates = 0;
                foreach (var renderer in station.Station.GetComponentsInChildren<CRPRenderer>(true))
                {
                    try
                    {
                        if (!Live(renderer) || !renderer.enabled ||
                            renderer.GetComponentInParent<PlanetStation>(true) != station.Station) continue;
                        if (!GarageMesh(renderer._mesh) || !GarageMaterial(renderer._material)) continue;
                        candidates++;
                        ScaleFloorVisual(station, renderer, multiplier);
                    }
                    catch (Exception e) { FloorMessage(renderer, $"skipped: {e.GetType().Name}: {e.Message}"); }
                }
                if (candidates == 0)
                {
                    DestroyStationCollision(station);
                    if (multiplier > 1f)
                        FloorMessage(station.Station, "unchanged: no active owned Stationtile Garage50 A/B/C CRPRenderer with LitStationTile; no global fallback.");
                }
            }
        }
        catch (Exception e) { Warn($"Garage floor update skipped: {e.GetType().Name}: {e.Message}"); }
    }

    private static string AssetName(string name) => (name ?? "").Replace(" ", "").Replace("_", "").Replace("-", "");

    private static bool GarageMesh(Mesh mesh)
    {
        if (mesh == null) return false;
        string name = AssetName(mesh.name);
        return name.Equals("StationtileGarage50A", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("StationtileGarage50B", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("StationtileGarage50C", StringComparison.OrdinalIgnoreCase);
    }

    private static bool GarageMaterial(Material material) => material != null &&
        string.Equals(material.name, "LitStationTile", StringComparison.OrdinalIgnoreCase);

    private static void FloorMessage(Component owner, string text)
    {
        if (owner == null) return;
        if (CfgDiag == null || !CfgDiag.Value)
        {
            if (text.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("skipped", StringComparison.OrdinalIgnoreCase) >= 0)
                Warn("Garage floor '" + Hierarchy(owner.transform) + "' " + text);
            return;
        }
        if (_diagnosticLines >= 128) return;
        string key = owner.GetInstanceID() + ":" + text;
        if (FloorMessages.Add(key)) Diagnostic($"Garage floor '{Hierarchy(owner.transform)}' {text}");
    }

    private static string Short(string value)
    {
        value = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        return value.Length <= 80 ? value : value.Substring(0, 80);
    }

    private static string Hierarchy(Transform transform)
    {
        string path = "";
        for (int depth = 0; transform != null && depth < 8; depth++, transform = transform.parent)
            path = Short(transform.name) + (path.Length == 0 ? "" : "/" + path);
        return path;
    }

    private static string MaterialInfo(Material material)
    {
        if (material == null) return "no material";
        string details = $"material='{Short(material.name)}'";
        foreach (string property in new[] { "_BaseColor", "_Color" })
        {
            int id = Shader.PropertyToID(property);
            if (!material.HasProperty(id)) continue;
            Color color = material.GetColor(id);
            details += $" {property}=({color.r:F2},{color.g:F2},{color.b:F2},{color.a:F2})";
        }
        return details;
    }

    private static Unity.Entities.ComponentType CrpType(string name) => Unity.Entities.ComponentType.ReadWrite(
        Il2CppSystem.Type.GetType(name + ", Assembly-CSharp"));

    private static CRPRendererData ReadDraw(Unity.Entities.EntityManager em, Unity.Entities.Entity entity, bool shared) =>
        shared ? em.GetSharedComponent<CRPRendererData>(entity) : em.GetComponentData<CRPRendererData>(entity);

    private static bool DrawMatches(CRPRendererData data, Mesh mesh, CRPRenderer renderer)
    {
        if (data._header._type != CRPRendererData.Type.MeshReference || mesh == null || renderer._material == null) return false;
        var draw = data.GetDrawDataMeshReference();
        return draw._meshReference._instanceID == mesh.GetInstanceID() && draw._submeshID == renderer._submeshID &&
            data._material._instanceID == renderer._material.GetInstanceID();
    }

    private static float4x4 EcsMatrix(Matrix4x4 matrix) => new float4x4(
        new float4(matrix.m00, matrix.m10, matrix.m20, matrix.m30),
        new float4(matrix.m01, matrix.m11, matrix.m21, matrix.m31),
        new float4(matrix.m02, matrix.m12, matrix.m22, matrix.m32),
        new float4(matrix.m03, matrix.m13, matrix.m23, matrix.m33));

    private static Matrix4x4 Matrix(float4x4 matrix) => new Matrix4x4(
        new Vector4(matrix.c0.x, matrix.c0.y, matrix.c0.z, matrix.c0.w),
        new Vector4(matrix.c1.x, matrix.c1.y, matrix.c1.z, matrix.c1.w),
        new Vector4(matrix.c2.x, matrix.c2.y, matrix.c2.z, matrix.c2.w),
        new Vector4(matrix.c3.x, matrix.c3.y, matrix.c3.z, matrix.c3.w));

    private static bool SameMatrix(Matrix4x4 a, Matrix4x4 b, float tolerance)
    {
        for (int row = 0; row < 4; row++)
            for (int column = 0; column < 4; column++)
                if (!Finite((double)a[row, column]) || !Finite((double)b[row, column]) ||
                    Math.Abs(a[row, column] - b[row, column]) > tolerance) return false;
        return true;
    }

    private static bool Invertible(Matrix4x4 matrix)
    {
        if (!SameMatrix(matrix, matrix, 0f)) return false;
        double determinant = (double)matrix.m00 * ((double)matrix.m11 * matrix.m22 - (double)matrix.m12 * matrix.m21) -
            (double)matrix.m01 * ((double)matrix.m10 * matrix.m22 - (double)matrix.m12 * matrix.m20) +
            (double)matrix.m02 * ((double)matrix.m10 * matrix.m21 - (double)matrix.m11 * matrix.m20);
        return Finite(determinant) && determinant != 0d && SameMatrix(matrix.inverse, matrix.inverse, 0f);
    }

    private static Mesh BuildTiledMesh(Mesh source, int tiles)
    {
        Mesh target = null;
        try
        {
            if (source == null || !source.isReadable || source.vertexCount <= 0 || source.subMeshCount != 1) return null;
            Bounds sourceBounds = source.bounds;
            if (!Finite(sourceBounds.center) || !Finite(sourceBounds.size) || sourceBounds.size.x <= 0f || sourceBounds.size.z <= 0f) return null;
            Vector3[] sourceVertices = source.vertices;
            Vector3[] sourceNormals = source.normals;
            Vector4[] sourceTangents = source.tangents;
            Color32[] sourceColors = source.colors32;
            var sourceUv = new Il2CppSystem.Collections.Generic.List<Vector4>[8];
            for (int channel = 0; channel < sourceUv.Length; channel++)
            {
                var values = new Il2CppSystem.Collections.Generic.List<Vector4>();
                source.GetUVs(channel, values);
                sourceUv[channel] = values.Count == sourceVertices.Length ? values : null;
            }
            int totalVertices = source.vertexCount * tiles * tiles;
            if (totalVertices <= 0 || totalVertices > 65535) return null;
            var vertices = new Il2CppSystem.Collections.Generic.List<Vector3>(totalVertices);
            var normals = sourceNormals != null && sourceNormals.Length == source.vertexCount ? new Il2CppSystem.Collections.Generic.List<Vector3>(totalVertices) : null;
            var tangents = sourceTangents != null && sourceTangents.Length == source.vertexCount ? new Il2CppSystem.Collections.Generic.List<Vector4>(totalVertices) : null;
            var colors = sourceColors != null && sourceColors.Length == source.vertexCount ? new Il2CppSystem.Collections.Generic.List<Color>(totalVertices) : null;
            var uv = new Il2CppSystem.Collections.Generic.List<Vector4>[8];
            for (int channel = 0; channel < uv.Length; channel++)
                if (sourceUv[channel] != null) uv[channel] = new Il2CppSystem.Collections.Generic.List<Vector4>(totalVertices);
            float offsetX = sourceBounds.size.x;
            float offsetZ = sourceBounds.size.z;
            for (int z = 0; z < tiles; z++)
            {
                for (int x = 0; x < tiles; x++)
                {
                    float dx = (x - (tiles - 1) * 0.5f) * offsetX;
                    float dz = (z - (tiles - 1) * 0.5f) * offsetZ;
                    for (int i = 0; i < sourceVertices.Length; i++)
                    {
                        vertices.Add(sourceVertices[i] + new Vector3(dx, 0f, dz));
                        normals?.Add(sourceNormals[i]);
                        tangents?.Add(sourceTangents[i]);
                        colors?.Add(sourceColors[i]);
                        for (int channel = 0; channel < uv.Length; channel++) uv[channel]?.Add(sourceUv[channel][i]);
                    }
                }
            }
            target = new Mesh { name = source.name + " MorePlayersTiledX" + tiles, hideFlags = HideFlags.DontSave };
            target.SetVertices(vertices);
            if (normals != null) target.SetNormals(normals);
            if (tangents != null) target.SetTangents(tangents);
            if (colors != null) target.SetColors(colors);
            for (int channel = 0; channel < uv.Length; channel++) if (uv[channel] != null) target.SetUVs(channel, uv[channel]);
            int[] sourceTriangles = source.GetTriangles(0);
            var triangles = new Il2CppSystem.Collections.Generic.List<int>(sourceTriangles.Length * tiles * tiles);
            for (int z = 0; z < tiles; z++)
            {
                for (int x = 0; x < tiles; x++)
                {
                    int vertexOffset = (z * tiles + x) * source.vertexCount;
                    foreach (int triangle in sourceTriangles) triangles.Add(vertexOffset + triangle);
                }
            }
            target.SetTriangles(triangles, 0);
            target.RecalculateBounds();
            if (target.vertexCount != totalVertices || !float.IsFinite(target.bounds.center.x) ||
                !float.IsFinite(target.bounds.center.y) || !float.IsFinite(target.bounds.center.z) ||
                !float.IsFinite(target.bounds.size.x) || !float.IsFinite(target.bounds.size.y) ||
                !float.IsFinite(target.bounds.size.z)) throw new InvalidOperationException("expanded mesh readback failed");
            return target;
        }
        catch
        {
            if (target != null) UnityEngine.Object.Destroy(target);
            return null;
        }
    }

    private static void WriteDraw(Unity.Entities.EntityManager em, FloorTransform floor, CRPRendererData data)
    {
        if (floor.SharedDraw)
        {
            em.SetSharedComponent(floor.Entity, data);
            return;
        }
        em.SetComponentData(floor.Entity, data);
    }

    private static void RestoreOriginalDraw(Unity.Entities.EntityManager em, FloorTransform floor)
    {
        if (floor.OriginalSharedDraw)
        {
            em.SetSharedComponent(floor.Entity, floor.OriginalDraw);
            floor.SharedDraw = true;
            return;
        }
        em.SetComponentData(floor.Entity, floor.OriginalDraw);
        floor.SharedDraw = false;
    }

    private static void RestoreFloorVisual(FloorTransform floor, string reason)
    {
        if (floor == null) return;
        bool restored = true;
        try
        {
            if (floor.Dirty && floor.World != null && floor.World.IsCreated)
            {
                var em = floor.World.EntityManager;
                if (em.Exists(floor.Entity) && em.HasComponent<CRPRendererData>(floor.Entity) &&
                    em.HasComponent<CRPLocalBounds>(floor.Entity) && em.HasComponent<CRPWorldBounds>(floor.Entity))
                {
                    em.CompleteAllTrackedJobs();
                    RestoreOriginalDraw(em, floor);
                    em.SetComponentData(floor.Entity, floor.OriginalLocal);
                    em.SetComponentData(floor.Entity, floor.OriginalWorld);
                }
            }
        }
        catch (Exception e)
        {
            restored = false;
            Warn("Garage floor restore failed: " + e.GetType().Name + ": " + e.Message);
        }
        if (restored && floor.AppliedMesh != null)
        {
            try { UnityEngine.Object.Destroy(floor.AppliedMesh); } catch { }
            floor.AppliedMesh = null;
        }
        if (restored)
        {
            floor.Applied = 1f;
            floor.Dirty = false;
            if (!string.IsNullOrEmpty(reason)) FloorMessage(floor.Renderer, "restored original floor mesh and bounds: " + reason + ".");
        }
    }

    private static void ScaleFloorVisual(StationState station, CRPRenderer renderer, float multiplier)
    {
        FloorTransforms.TryGetValue(renderer.GetInstanceID(), out var floor);
        if (floor != null && (floor.Blocked || floor.Renderer != renderer || floor.Station != station.Station)) return;
        Mesh source = renderer._mesh;
        if (source == null || renderer._layer != CRPLayer.StaticObject || source.subMeshCount != 1 || renderer._submeshID != 0)
        {
            FloorMessage(renderer, "unchanged: not a readable single-submesh StaticObject garage floor.");
            return;
        }
        var world = Unity.Entities.World.DefaultGameObjectInjectionWorld;
        if (world == null || !world.IsCreated) return;
        var em = world.EntityManager;
        var entity = renderer._entity;
        var drawType = CrpType("CRPRendererData");
        if (!em.Exists(entity) || !em.HasComponent(entity, drawType) ||
            !em.HasComponent(entity, CrpType("CRPLocalBounds")) || !em.HasComponent(entity, CrpType("CRPWorldBounds")) ||
            !em.HasComponent<Unity.Transforms.LocalToWorld>(entity))
        {
            FloorMessage(renderer, "unchanged: CRP draw/bounds or LocalToWorld unavailable; retrying later.");
            return;
        }
        if (entity.Equals(station.Station._entity) || em.HasComponent<Unity.Transforms.Child>(entity))
        {
            FloorMessage(renderer, "unchanged: floor entity is the station or has a Child buffer; refusing to edit potentially unrelated structures.");
            return;
        }
        em.CompleteAllTrackedJobs();
        var localToWorld = em.GetComponentData<Unity.Transforms.LocalToWorld>(entity);
        var originalLocal = em.GetComponentData<CRPLocalBounds>(entity);
        var originalWorld = em.GetComponentData<CRPWorldBounds>(entity);
        bool shared = drawType.IsSharedComponent;
        var currentDraw = ReadDraw(em, entity, shared);
        if (floor == null)
        {
            if (!DrawMatches(currentDraw, source, renderer) || !originalLocal.Equals(new CRPLocalBounds(source.bounds)))
            {
                FloorMessage(renderer, "unchanged: ECS mesh/material/submesh or local bounds differ from the owned renderer.");
                return;
            }
            floor = new FloorTransform
            {
                Renderer = renderer, Station = station.Station, MeshId = source.GetInstanceID(), World = world, Entity = entity,
                SharedDraw = shared, OriginalSharedDraw = shared, OriginalDraw = currentDraw, OriginalLocal = originalLocal, OriginalWorld = originalWorld,
                OriginalLocalToWorld = Matrix(localToWorld.Value), OriginalMesh = source, Applied = 1f
            };
            FloorTransforms.Add(renderer.GetInstanceID(), floor);
        }
        else if (floor.World != world || !floor.Entity.Equals(entity) || floor.MeshId != source.GetInstanceID() ||
            !SameMatrix(Matrix(localToWorld.Value), floor.OriginalLocalToWorld, 0.01f))
        {
            RestoreFloorVisual(floor, "mesh, entity, world, or native transform provenance changed");
            FloorTransforms.Remove(renderer.GetInstanceID());
            return;
        }
        if (multiplier <= 1f)
        {
            RestoreFloorVisual(floor, "zone multiplier returned to x1");
            DestroyStationCollision(station);
            FloorTransforms.Remove(renderer.GetInstanceID());
            return;
        }
        int tiles = Math.Max(1, Math.Min(4, (int)Math.Round(multiplier)));
        if (floor.AppliedMesh == null || floor.Applied != multiplier)
        {
            if (floor.AppliedMesh != null) UnityEngine.Object.Destroy(floor.AppliedMesh);
            floor.AppliedMesh = BuildTiledMesh(floor.OriginalMesh, tiles);
            if (floor.AppliedMesh == null)
            {
                floor.Blocked = true;
                FloorMessage(renderer, "unchanged: source mesh is not CPU-readable or cannot be safely tiled; no renderer transform was changed.");
                return;
            }
            floor.AppliedDraw = new CRPRendererData(floor.AppliedMesh, renderer._submeshID, renderer._material);
            floor.AppliedLocal = new CRPLocalBounds(floor.AppliedMesh.bounds);
            floor.AppliedWorld = new CRPWorldBounds(localToWorld.Value, floor.AppliedLocal);
        }
        try
        {
            floor.Dirty = true;
            WriteDraw(em, floor, floor.AppliedDraw);
            em.SetComponentData(entity, floor.AppliedLocal);
            em.SetComponentData(entity, floor.AppliedWorld);
            var readDraw = ReadDraw(em, entity, floor.SharedDraw);
            if (!DrawMatches(readDraw, floor.AppliedMesh, renderer) ||
                !em.GetComponentData<CRPLocalBounds>(entity).Equals(floor.AppliedLocal) ||
                !em.GetComponentData<CRPWorldBounds>(entity).Equals(floor.AppliedWorld))
                throw new InvalidOperationException("expanded floor draw/bounds readback failed");
            floor.Applied = multiplier;
            EnsureStationCollision(station, floor, multiplier);
            FloorMessage(renderer, $"garage floor tiled x{tiles} with unchanged tile scale and unchanged LocalToWorld/parent transform; expanded mesh={floor.AppliedMesh.GetInstanceID()}, bounds={floor.AppliedMesh.bounds}. Neighboring renderers and station textures are not transformed.");
        }
        catch (Exception e)
        {
            RestoreFloorVisual(floor, "expanded floor write failed");
            floor.Blocked = true;
            FloorMessage(renderer, $"expanded floor update failed: {e.GetType().Name}: {e.Message}; original draw and bounds restored.");
        }
    }

    private static bool TryPhysicsWorldIndex(Unity.Entities.EntityManager em, Entity floorEntity, Entity stationEntity,
        out Unity.Physics.PhysicsWorldIndex index)
    {
        index = new Unity.Physics.PhysicsWorldIndex(0u);
        var cursor = floorEntity;
        var visited = new HashSet<Entity> { floorEntity };
        for (int depth = 0; em.Exists(cursor); depth++)
        {
            if (em.HasComponent<Unity.Physics.PhysicsWorldIndex>(cursor))
            {
                index = em.GetSharedComponent<Unity.Physics.PhysicsWorldIndex>(cursor);
                return true;
            }
            if (depth >= 32 || !em.HasComponent<Unity.Transforms.Parent>(cursor)) break;
            cursor = em.GetComponentData<Unity.Transforms.Parent>(cursor).Value;
            if (!visited.Add(cursor)) break;
        }
        if (em.Exists(stationEntity) && em.HasComponent<Unity.Physics.PhysicsWorldIndex>(stationEntity))
        {
            index = em.GetSharedComponent<Unity.Physics.PhysicsWorldIndex>(stationEntity);
            return true;
        }
        try
        {
            var query = em.CreateEntityQuery(new[]
            {
                Unity.Entities.ComponentType.ReadOnly<Unity.Physics.PhysicsWorldIndex>()
            });
            try
            {
                var entities = query.ToEntityArray(Unity.Collections.Allocator.Temp);
                try
                {
                    if (entities.Length > 0)
                    {
                        index = em.GetSharedComponent<Unity.Physics.PhysicsWorldIndex>(entities[0]);
                        return true;
                    }
                }
                finally { entities.Dispose(); }
            }
            finally { query.Dispose(); }
        }
        catch { }
        return true;
    }

    private static void DestroyStationCollision(StationState station)
    {
        if (station == null) return;
        if (station.CollisionWorld == null || !station.CollisionWorld.IsCreated || station.CollisionEntity == default)
        {
            station.CollisionEntity = default;
            station.CollisionWorld = null;
            station.CollisionApplied = 0f;
            return;
        }
        Unity.Entities.BlobAssetReference<Unity.Physics.Collider> blob = null;
        bool worldAlive = station.CollisionWorld != null && station.CollisionWorld.IsCreated;
        try
        {
            var em = station.CollisionWorld.EntityManager;
            em.CompleteAllTrackedJobs();
            if (em.Exists(station.CollisionEntity) && em.HasComponent<Unity.Physics.PhysicsCollider>(station.CollisionEntity))
                blob = em.GetComponentData<Unity.Physics.PhysicsCollider>(station.CollisionEntity).Value;
            if (em.Exists(station.CollisionEntity)) em.DestroyEntity(station.CollisionEntity);
            if (!worldAlive && blob.IsCreated) blob.Dispose();
        }
        catch (Exception e) { Warn($"Garage collision cleanup skipped: {e.GetType().Name}: {e.Message}"); }
        station.CollisionEntity = default;
        station.CollisionWorld = null;
        station.CollisionApplied = 0f;
    }

    private static void EnsureStationCollision(StationState station, FloorTransform floor, float multiplier)
    {
        if (multiplier <= 1f)
        {
            DestroyStationCollision(station);
            return;
        }
        if (station.CollisionWorld != null && station.CollisionWorld.IsCreated && station.CollisionEntity != default &&
            station.CollisionWorld.EntityManager.Exists(station.CollisionEntity) && station.CollisionApplied == multiplier) return;
        DestroyStationCollision(station);
        if (floor == null || floor.World == null || !floor.World.IsCreated) return;
        var em = floor.World.EntityManager;
        Unity.Entities.BlobAssetReference<Unity.Physics.Collider> created = null;
        Entity entity = default;
        string api = "station and floor LocalToWorld";
        try
        {
            em.CompleteAllTrackedJobs();
            if (!em.Exists(floor.Entity) || !em.HasComponent<CRPWorldBounds>(floor.Entity) ||
                !em.Exists(station.Station._entity) || !em.HasComponent<Unity.Transforms.LocalToWorld>(station.Station._entity))
                throw new InvalidOperationException("floor or station LocalToWorld is unavailable");
            var stationMatrix = Matrix(em.GetComponentData<Unity.Transforms.LocalToWorld>(station.Station._entity).Value);
            if (!Invertible(stationMatrix)) throw new InvalidOperationException("station LocalToWorld is singular");
            float worldTop = em.GetComponentData<CRPWorldBounds>(floor.Entity)._worldBounds.Max.y;
            float topStationY = stationMatrix.inverse.MultiplyPoint3x4(new Vector3(0f, worldTop, 0f)).y;
            float3 center = (station.Min + station.Max) * 0.5f;
            float3 size = station.Max - station.Min;
            size.x *= multiplier;
            size.z *= multiplier;
            size.y = 1f;
            center.y = topStationY - size.y * 0.5f;
            if (!Finite(center) || !Finite(size) || size.x <= 0f || size.z <= 0f)
                throw new InvalidOperationException("garage collision footprint is nonfinite or empty");
            var filter = new Unity.Physics.CollisionFilter
            {
                BelongsTo = 0xFFFFFFFF, CollidesWith = 0xFFFFFFFF, GroupIndex = 0
            };
            api = "BoxCollider.Create";
            created = Unity.Physics.BoxCollider.Create(new Unity.Physics.BoxGeometry
            {
                Center = center, Size = size, Orientation = quaternion.identity, BevelRadius = 0f
            }, filter, new Unity.Physics.Material());
            if (!created.IsCreated) throw new InvalidOperationException("box factory returned no blob");
            api = "EntityManager.CreateEntity(PhysicsCollider, LocalToWorld)";
            entity = em.CreateEntity(
                Unity.Entities.ComponentType.ReadWrite<Unity.Physics.PhysicsCollider>(),
                Unity.Entities.ComponentType.ReadWrite<Unity.Transforms.LocalToWorld>());
            em.AddComponentData(entity, new Unity.Physics.PhysicsCollider { Value = created });
            em.AddComponentData(entity, new Unity.Transforms.LocalToWorld { Value = EcsMatrix(stationMatrix) });
            api = "EntityManager.AddSharedComponent(PhysicsWorldIndex)";
            if (!TryPhysicsWorldIndex(em, floor.Entity, station.Station._entity, out var worldIndex))
                throw new InvalidOperationException("no PhysicsWorldIndex found on the floor, its parents, or station");
            em.AddSharedComponent<Unity.Physics.PhysicsWorldIndex>(entity, worldIndex);
            api = "collision entity component readback";
            if (!em.Exists(entity) || !em.HasComponent<Unity.Physics.PhysicsCollider>(entity) ||
                !em.HasComponent<Unity.Transforms.LocalToWorld>(entity) ||
                !em.HasComponent<Unity.Physics.PhysicsWorldIndex>(entity) ||
                !em.GetComponentData<Unity.Physics.PhysicsCollider>(entity).Value.IsCreated)
                throw new InvalidOperationException("collision entity component readback failed");
            station.CollisionEntity = entity;
            station.CollisionWorld = floor.World;
            station.CollisionApplied = multiplier;
            FloorMessage(floor.Renderer, $"solid static garage box registered as physics body {entity}, local center={center}, dims={size}, x{multiplier}; PhysicsCollider + LocalToWorld + PhysicsWorldIndex are present.");
        }
        catch (Exception e)
        {
            FloorMessage(floor.Renderer, $"solid garage collision registration failed at {api}: {e}");
            try
            {
                em.CompleteAllTrackedJobs();
                if (entity != default && em.Exists(entity)) em.DestroyEntity(entity);
                if (entity == default && created.IsCreated) created.Dispose();
            }
            catch { }
        }
    }

    private static void Diagnostic(string text)
    {
        if (CfgDiag == null || !CfgDiag.Value || _diagnosticLines >= 128) return;
        _diagnosticLines++;
        Log.LogInfo(text);
    }

    private static void Diagnose(StationState station)
    {
        if (_diagnosticLines >= 128 || station.Diagnosed) return;
        station.Diagnosed = true;
        int count = 0;
        foreach (var renderer in station.Station.GetComponentsInChildren<CRPRenderer>(true))
        {
            if (_diagnosticLines >= 128 || count >= 8) break;
            if (renderer == null || renderer.GetComponentInParent<PlanetStation>(true) != station.Station) continue;
            if (!GarageMesh(renderer._mesh)) continue;
            count++;
            FloorTransforms.TryGetValue(renderer.GetInstanceID(), out var floor);
            string applied = floor == null || floor.AppliedMesh == null ? "none" :
                $"x{floor.Applied}, mesh={floor.AppliedMesh.GetInstanceID()}, bounds={floor.AppliedMesh.bounds}";
            Diagnostic($"Garage CRPRenderer '{Hierarchy(renderer.transform)}': source='{Short(renderer._mesh.name)}' bounds={renderer._mesh.bounds} readable={renderer._mesh.isReadable} submesh={renderer._submeshID} layer={renderer._layer} entity={renderer._entity} {MaterialInfo(renderer._material)}; applied={applied}.");
        }
        if (count != 0) return;
        foreach (var renderer in station.Station.GetComponentsInChildren<EPC_Renderer>(true))
        {
            if (_diagnosticLines >= 128 || count >= 4) break;
            if (renderer == null || renderer.GetComponentInParent<PlanetStation>(true) != station.Station || !GarageMesh(renderer._mesh)) continue;
            count++;
            Diagnostic($"Garage EPC_Renderer fallback '{Hierarchy(renderer.transform)}': mesh='{Short(renderer._mesh.name)}' bounds={renderer._mesh.bounds} submesh={renderer._submeshID} layer={renderer._layer} {MaterialInfo(renderer._material)}; unchanged.");
        }
    }

    internal static void LogYardState()
    {
        try
        {
            if (!TryGetSingleton(out var singleton)) return;
            string signature = $"{singleton._boundsSize.x:F1},{singleton._boundsSize.y:F1},{singleton._boundsSize.z:F1}";
            if (signature == _lastYardSig) return;
            _lastYardSig = signature;
            Log.LogInfo($"Garage live bounds: {singleton._boundsSize}; target horizontal multiplier x{Multiplier}.");
        }
        catch { }
    }

    internal static void LogMatrixProbe()
    {
        try
        {
            if (CfgDiag == null || !CfgDiag.Value || !TryGetSingleton(out var singleton)) return;
            string signature = $"{singleton._boundsSize.x:F1}|{singleton._Matrix.c3.x:F1},{singleton._Matrix.c3.y:F1},{singleton._Matrix.c3.z:F1}";
            if (signature == _lastProbeSig) return;
            _lastProbeSig = signature;
            Log.LogInfo($"Garage matrix: c0={singleton._Matrix.c0} c1={singleton._Matrix.c1} c2={singleton._Matrix.c2} c3={singleton._Matrix.c3}, bounds={singleton._boundsSize}");
        }
        catch { }
    }

    private static bool TryGetSingleton(out GarageGrabberSingleton singleton)
    {
        singleton = default;
        try
        {
            var world = Unity.Entities.World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated) return false;
            var em = world.EntityManager;
            Unity.Entities.EntityManager copy = em;
            var type = Unity.Entities.ComponentType.ReadOnly(
                Il2CppSystem.Type.GetType("GarageGrabberSingleton, Assembly-CSharp"));
            var query = copy.CreateEntityQuery(new Unity.Entities.ComponentType[] { type });
            var entity = query.GetSingletonEntity();
            singleton = copy.GetComponentData<GarageGrabberSingleton>(entity);
            return true;
        }
        catch { return false; }
    }

    internal static void DiagPlatforms()
    {
        try
        {
            if (CfgDiag == null || !CfgDiag.Value) return;
            EnsureScaled();
            foreach (var station in StationOriginals.Values)
                if (Live(station.Station)) Diagnose(station);
        }
        catch (Exception e) { Warn($"Garage diagnostics skipped: {e.GetType().Name}: {e.Message}"); }
    }
}
