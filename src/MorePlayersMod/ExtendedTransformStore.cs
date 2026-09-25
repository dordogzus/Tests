using BepInEx.Configuration;
using BepInEx.Logging;
using System;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;

namespace MorePlayersMod;

internal static class ExtendedTransformStore
{
    private enum Phase { Unknown, Build, Flight, Returning, Checking }

    private struct Sample
    {
        internal Entity Entity;
        internal ulong Prefab;
        internal GarageTransform Transform;
    }

    private const int MaxEntities = 16384;
    private const int VerifyTicks = 30;
    private const int MatchTicks = 240;
    private const int StableMatchTicks = 3;
    private static ManualLogSource Log;
    private static ConfigEntry<bool> CfgEnabled;
    private static ConfigEntry<bool> CfgCodecProbe;
    private static Dictionary<string, Sample> _snapshot = new Dictionary<string, Sample>();
    private static readonly Dictionary<string, Sample> Frozen = new Dictionary<string, Sample>();
    private static readonly Dictionary<string, Sample> Baseline = new Dictionary<string, Sample>();
    private static readonly Dictionary<string, int> MatchStable = new Dictionary<string, int>();
    private static readonly Dictionary<string, Sample> Written = new Dictionary<string, Sample>();
    private static World _world;
    private static ulong _worldSequence;
    private static Entity _coreEntity;
    private static int _coreId;
    private static GarageGrabberSingleton _basis;
    private static Phase _phase;
    private static int _ticks;
    private static int _lines;
    private static bool _blocked;
    private static bool _garageBuildMode;
    private static bool _restoreRequested;
    private static bool _codecAttempted;
    private static ulong _pollCount;
    private static string _diagnosticState;
    private const int MaxPropagationLines = 12;
    private const double PositionTolerance = 0.001;
    private const double BasisTolerance = 0.0001;
    private static int _propagationLines;
    private static string _propagationKey;
    private static Sample _propagationTarget;

    internal static void Bind(ConfigFile config, ManualLogSource log)
    {
        Log = log;
        CfgEnabled = config.Bind("Garage", "PreserveExtendedTransforms", true,
            "Capture the authoritative build snapshot at garage stop, then restore GUID-matched placements through the game's complete transform propagation path after returning to build mode.");
        CfgCodecProbe = config.Bind("Garage", "ExtendedTransformCodecProbe", false,
            "Opt-in single build-snapshot Encode/Decode diagnostic per session, on local copies with a registered prefab and ready Core maps. Does not write decoded data. Native codec semantics are not established by interop signatures.");
    }

    internal static bool GarageBuildMode => _garageBuildMode;

    internal static void ResetSession()
    {
        Clear();
        _blocked = false;
        _garageBuildMode = false;
        _restoreRequested = false;
        _codecAttempted = false;
        _propagationLines = 0;
        _lines = 0;
        _pollCount = 0;
        _diagnosticState = null;
    }

    internal static void CaptureBeforeGarageStops()
    {
        _garageBuildMode = false;
        Zone.SuspendForTransition();
        if (CfgEnabled == null || !CfgEnabled.Value || _blocked || _phase != Phase.Build) return;
        try
        {
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated) return;
            var em = world.EntityManager;
            em.CompleteAllTrackedJobs();
            var current = ReadSnapshot(em);
            _snapshot = current;
            _phase = Phase.Flight;
            _basis = TryGetBasis(em, out var basis) ? basis : _basis;
            Notice($"Garage stop snapshot captured before native teardown: {current.Count} GUID+prefab placements; transition writes are suspended.");
        }
        catch (Exception e)
        {
            _snapshot.Clear();
            Warn($"Garage stop snapshot failed: {e.GetType().Name}: {e.Message}");
        }
    }

    internal static void BeginRestore()
    {
        _garageBuildMode = true;
        Zone.ResumeForBuild();
        if (CfgEnabled == null || !CfgEnabled.Value || _blocked) return;
        _restoreRequested = true;
        Notice("Garage return lifecycle observed; waiting for GUID+prefab reconstruction before applying exact transforms.");
    }

    private static void Clear()
    {
        _snapshot.Clear();
        Frozen.Clear();
        Baseline.Clear();
        MatchStable.Clear();
        Written.Clear();
        ClearPropagation();
        _world = null;
        _worldSequence = 0;
        _coreEntity = default;
        _coreId = 0;
        _basis = default;
        _phase = Phase.Unknown;
        _ticks = 0;
        _restoreRequested = false;
    }

    internal static void Tick()
    {
        _pollCount++;
        if (!_garageBuildMode)
        {
            return;
        }
        World observedWorld = null;
        bool? observedFlight = null;
        Entity? observedGarage = null;
        string observedPredicate = null;
        try
        {
            if (CfgEnabled == null || !CfgEnabled.Value || _blocked) return;
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated) return;
            observedWorld = world;
            if (_phase != Phase.Unknown && (_worldSequence != world.SequenceNumber ||
                _world == null || _world.Pointer != world.Pointer))
                Invalidate("world identity changed");
            var core = Core.Get();
            int coreId = core == null ? 0 : core.GetInstanceID();
            if (_phase != Phase.Unknown && core != null && _coreId != coreId)
                Invalidate("Core identity changed");
            var em = world.EntityManager;
            em.CompleteAllTrackedJobs();
            bool hasShip = TrySingleton(em, "SpaceshipSingleton", out var shipEntity);
            bool hasGarage = TrySingleton(em, "GarageGrabberSingleton", out var garageEntity);
            bool hasCore = TrySingleton(em, "Core+Singleton", out var coreEntity);
            if (hasGarage) observedGarage = garageEntity;
            observedPredicate = "garage-lifecycle";
            if (hasShip)
            {
                var ship = em.GetComponentData<SpaceshipSingleton>(shipEntity);
                observedFlight = ship.IsCreated();
            }
            bool coreReady = core != null && hasCore && SameEntity(core._coreEntity, coreEntity);
            if (_phase != Phase.Unknown && coreReady && !SameEntity(_coreEntity, coreEntity))
                Invalidate("Core singleton identity changed");
            if (!hasGarage || !coreReady) return;
            bool flight = !_garageBuildMode;
            var basis = em.GetComponentData<GarageGrabberSingleton>(garageEntity);
            if (!FiniteBasis(basis)) return;
            if (_phase == Phase.Unknown)
            {
                _world = world;
                _worldSequence = world.SequenceNumber;
                _coreId = coreId;
                _coreEntity = coreEntity;
                _basis = basis;
                _phase = flight ? Phase.Flight : Phase.Build;
                Notice($"Garage polling: IsCreated={flight}; initial observation never restores.");
            }
            if (flight)
            {
                if (_phase == Phase.Build)
                {
                    Frozen.Clear();
                    foreach (var pair in _snapshot) Frozen.Add(pair.Key, pair.Value);
                    _snapshot.Clear();
                    Notice($"Garage polling: build->flight; froze {Frozen.Count} unique GUID+prefab transforms from the authoritative stop snapshot.");
                }
                else if (_phase != Phase.Flight)
                {
                    Frozen.Clear();
                    Baseline.Clear();
                    MatchStable.Clear();
                    Written.Clear();
                    _snapshot.Clear();
                    ClearPropagation();
                    Notice("Garage polling: flight interrupted recovery/verification; discarded, not re-armed.", true);
                }
                _phase = Phase.Flight;
                return;
            }
            if (_phase != Phase.Build && !SameBasis(_basis, basis))
            {
                Block("return basis differs; frozen transforms discarded");
                return;
            }
            var current = ReadSnapshot(em);
            if (_phase == Phase.Flight)
            {
                bool lifecycleRestore = _restoreRequested;
                Baseline.Clear();
                MatchStable.Clear();
                _phase = Phase.Returning;
                _ticks = 0;
                _restoreRequested = false;
                Notice($"Garage polling: explicit flight->build ({(lifecycleRestore ? "lifecycle" : "fallback")}); waiting up to {MatchTicks} manager ticks for GUID+prefab reconstruction of {Frozen.Count} placements.");
            }
            else if (_phase == Phase.Returning)
            {
                var missing = new List<string>();
                foreach (var pair in Frozen)
                {
                    if (!current.TryGetValue(pair.Key, out var target) || target.Prefab != pair.Value.Prefab)
                    {
                        missing.Add(pair.Key);
                        Baseline.Remove(pair.Key);
                        MatchStable.Remove(pair.Key);
                        continue;
                    }
                    if (!Baseline.TryGetValue(pair.Key, out var previous) || !SameSample(previous, target))
                    {
                        Baseline[pair.Key] = target;
                        MatchStable[pair.Key] = 1;
                    }
                    else MatchStable[pair.Key] = MatchStable.TryGetValue(pair.Key, out var stable) ? stable + 1 : 1;
                }
                bool allStable = Baseline.Count == Frozen.Count && MatchStable.Count == Frozen.Count;
                if (allStable)
                {
                    foreach (int stable in MatchStable.Values)
                    {
                        if (stable < StableMatchTicks)
                        {
                            allStable = false;
                            break;
                        }
                    }
                }
                if (allStable && ++_ticks >= StableMatchTicks)
                {
                    RestoreOnce(em, current);
                    foreach (string key in new List<string>(Baseline.Keys))
                        if (!Frozen.ContainsKey(key)) { Baseline.Remove(key); MatchStable.Remove(key); }
                    _phase = NextRestorePhase();
                    _ticks = 0;
                }
                else if (_ticks >= MatchTicks)
                {
                    Notice($"Garage return timeout: {Baseline.Count}/{Frozen.Count} placements matched after {MatchTicks} ticks; missing GUIDs={string.Join(",", missing)}; unmatched entries remain pending.", true);
                    RestoreOnce(em, current);
                    foreach (string key in new List<string>(Baseline.Keys))
                        if (!Frozen.ContainsKey(key)) { Baseline.Remove(key); MatchStable.Remove(key); }
                    _phase = NextRestorePhase();
                    _ticks = 0;
                }
            }
            else if (_phase == Phase.Checking)
            {
                int reapplied = 0;
                bool invalid = false;
                foreach (var pair in Written)
                {
                    if (!current.TryGetValue(pair.Key, out var target) || target.Prefab != pair.Value.Prefab)
                    {
                        invalid = true;
                        break;
                    }
                    if (!SameTransform(pair.Value.Transform, target.Transform))
                    {
                        bool reverted = Baseline.TryGetValue(pair.Key, out var before) && SameTransform(before.Transform, target.Transform);
                        if (!reverted)
                        {
                            invalid = true;
                            break;
                        }
                        try
                        {
                            GarageGrabber.SetGarageTransform(em, pair.Value.Transform, target.Entity, _basis, default(GarageTransform));
                            reapplied++;
                        }
                        catch
                        {
                            invalid = true;
                            break;
                        }
                    }
                }
                if (invalid)
                {
                    Block("written placement disappeared or changed after native transform propagation");
                    return;
                }
                if (reapplied > 0) Notice($"Garage restore re-applied {reapplied} placement transforms after a native baseline overwrite.");
                if (++_ticks >= VerifyTicks)
                {
                    ObservePropagation(em, "final-thirty-frame");
                    ClearPropagation();
                    Notice($"Garage restore check: {Written.Count} native-transform writes remained exact for {VerifyTicks} later manager frames.");
                    Written.Clear();
                    Baseline.Clear();
                    _phase = Phase.Build;
                }
            }
            _snapshot = current;
            _basis = basis;
            ProbeCodec(em, core, coreEntity, current);
        }
        catch (Exception e)
        {
            Block($"{e.GetType().Name} during polling/access; restart the session before retrying");
        }
        finally
        {
            Diagnose(observedWorld, observedFlight, observedGarage, observedPredicate);
        }
    }

    private static void Diagnose(World world, bool? flight, Entity? garage, string predicate)
    {
        string enabled = _garageBuildMode.ToString();
        string system = "garage-lifecycle";
        string garageValue = garage.HasValue ? $"{garage.Value.Index}:{garage.Value.Version}" : "unavailable";
        string current = $"ship.IsCreated()={flight?.ToString() ?? "unavailable"}; garageSingleton={garageValue}; GarageGrabber={system}; lifecycleBuild={enabled}; predicate={predicate ?? "unavailable"}";
        if (current == _diagnosticState) return;
        _diagnosticState = current;
        Notice($"Garage diagnostic: pollCount={_pollCount}; {current}.");
    }

    private static Dictionary<string, Sample> ReadSnapshot(EntityManager em)
    {
        var result = new Dictionary<string, Sample>();
        var seen = new HashSet<string>();
        var query = em.CreateEntityQuery(new EntityQueryDesc[]
        {
            new EntityQueryDesc
            {
                All = new ComponentType[] { GameType("SCGuid") },
                Options = EntityQueryOptions.IncludeDisabledEntities | EntityQueryOptions.IncludePrefab
            }
        });
        try
        {
            if (query.CalculateEntityCount() > MaxEntities)
                throw new InvalidOperationException("SCGuid query exceeds safety limit");
            var arr = query.ToEntityArray(Unity.Collections.Allocator.Temp);
            try
            {
                for (int i = 0; i < arr.Length; i++)
                {
                    var entity = arr[i];
                    string key = Key(em.GetComponentData<SCGuid>(entity));
                    if (key == null) continue;
                    if (!seen.Add(key))
                    {
                        result.Remove(key);
                        continue;
                    }
                    if (em.HasComponent<Prefab>(entity) || em.HasComponent<Disabled>(entity) ||
                        !em.HasComponent<GarageTransform>(entity) || !em.HasComponent<SCPrefab>(entity)) continue;
                    var prefab = em.GetComponentData<SCPrefab>(entity)._prefab;
                    var transform = em.GetComponentData<GarageTransform>(entity);
                    if (prefab == 0 || !FiniteTransform(transform)) continue;
                    result.Add(key, new Sample { Entity = entity, Prefab = prefab, Transform = transform });
                }
            }
            finally { arr.Dispose(); }
        }
        finally { query.Dispose(); }
        return result;
    }

    private static Phase NextRestorePhase()
    {
        if (Frozen.Count > 0) return Phase.Returning;
        return Written.Count == 0 ? Phase.Build : Phase.Checking;
    }

    private static void RestoreOnce(EntityManager em, Dictionary<string, Sample> current)
    {
        ClearPropagation();
        int unchanged = 0;
        int failed = 0;
        foreach (var pair in Baseline)
        {
            var target = pair.Value;
            var exact = Frozen[pair.Key];
            bool wasExact = SameTransform(target.Transform, exact.Transform);
            target.Transform = exact.Transform;
            if (_propagationKey == null && _propagationLines <= MaxPropagationLines - 3)
            {
                _propagationKey = pair.Key;
                _propagationTarget = target;
                ObservePropagation(em, "before-native-transform-restore");
            }
            try
            {
                GarageGrabber.SetGarageTransform(em, exact.Transform, target.Entity, _basis, default(GarageTransform));
                if (_propagationKey == pair.Key) ObservePropagation(em, "immediate-after-native-transform-restore");
                if (!em.Exists(target.Entity) || !em.HasComponent<GarageTransform>(target.Entity) ||
                    !SameTransform(em.GetComponentData<GarageTransform>(target.Entity), exact.Transform))
                    throw new InvalidOperationException("native GarageTransform readback failed");
                if (wasExact) unchanged++;
                Written.Add(pair.Key, target);
                current[pair.Key] = target;
                Frozen.Remove(pair.Key);
            }
            catch (Exception e)
            {
                failed++;
                Notice($"Garage native transform restore failed for GUID={pair.Key}: {e.GetType().Name}: {e.Message}.", true);
            }
        }
        Notice($"Garage restore: {Written.Count} native complete-transform writes, {unchanged} GarageTransform values already exact, {failed} failed, {Frozen.Count} pending unmatched or failed.");
        if (Written.Count == 0 && Frozen.Count == 0) Baseline.Clear();
    }

    private static void ClearPropagation()
    {
        _propagationKey = null;
        _propagationTarget = default;
    }

    private static void ObservePropagation(EntityManager em, string stage)
    {
        if (_propagationKey == null || _propagationLines >= MaxPropagationLines) return;
        string observation = "";
        try
        {
            var entity = _propagationTarget.Entity;
            observation = $"stage={stage}; GUID={_propagationKey}; entity={entity.Index}:{entity.Version}; prefab={_propagationTarget.Prefab:X16}";
            if (!em.Exists(entity) || !em.HasComponent<SCGuid>(entity) ||
                Key(em.GetComponentData<SCGuid>(entity)) != _propagationKey ||
                !em.HasComponent<SCPrefab>(entity) ||
                em.GetComponentData<SCPrefab>(entity)._prefab != _propagationTarget.Prefab)
                throw new InvalidOperationException("selected identity unavailable or changed");
            if (!em.HasComponent<GarageTransform>(entity))
                throw new InvalidOperationException("selected GarageTransform missing");
            var actual = em.GetComponentData<GarageTransform>(entity);
            var desired = _propagationTarget.Transform;
            observation += $"; GarageTransform.pos={actual._transform.pos}, rot={actual._transform.rot.value}; restore.pos={desired._transform.pos}, rot={desired._transform.rot.value}; GT-to-restore {PositionError(actual._transform.pos, desired._transform.pos)}; rotationChordError={RotationError(actual._transform.rot.value, desired._transform.rot.value):G9} (tol={BasisTolerance})";
            double3? universePosition = null;
            Unity.Transforms.LocalTransform? local = null;
            float4x4? ltw = null;
            if (em.HasComponent<UniversePosition>(entity))
                universePosition = em.GetComponentData<UniversePosition>(entity)._position;
            observation += $"; UniversePosition._position={universePosition?.ToString() ?? "missing"}";
            if (em.HasComponent<Unity.Transforms.LocalTransform>(entity))
                local = em.GetComponentData<Unity.Transforms.LocalTransform>(entity);
            observation += local.HasValue
                ? $"; LocalTransform.Position={local.Value.Position}, Rotation={local.Value.Rotation.value}, Scale={local.Value.Scale:G9}"
                : "; LocalTransform=missing";
            if (em.HasComponent<Unity.Transforms.LocalToWorld>(entity))
                ltw = em.GetComponentData<Unity.Transforms.LocalToWorld>(entity).Value;
            observation += ltw.HasValue ? $"; LocalToWorld.Value={MatrixText(ltw.Value)}" : "; LocalToWorld=missing";
            if (!TrySingleton(em, "GarageGrabberSingleton", out var garageEntity) ||
                !TrySingleton(em, "UniverseCoreSingleton", out var universeEntity))
                throw new InvalidOperationException("current garage/universe singleton unavailable or nonunique");
            var basis = em.GetComponentData<GarageGrabberSingleton>(garageEntity);
            var universe = em.GetComponentData<UniverseCoreSingleton>(universeEntity);
            observation += $"; currentGarage={garageEntity.Index}:{garageEntity.Version}; currentUniverse={universeEntity.Index}:{universeEntity.Version}; origin={universe._centerEntityUniversePosition}; garageMatrix=[{basis._Matrix.c0}; {basis._Matrix.c1}; {basis._Matrix.c2}; {basis._Matrix.c3}]; garageRotation={basis._Rotation.value}";
            if (!FiniteBasis(basis) || !FiniteTransform(actual) || !FiniteTransform(desired))
                throw new InvalidOperationException("invalid current basis/transform");
            var expectedPosition = actual.GetUniversePosition(basis);
            var expectedMatrix = actual.GetLTWMatrix(basis, universe);
            var restorePosition = desired.GetUniversePosition(basis);
            var restoreMatrix = desired.GetLTWMatrix(basis, universe);
            observation += $"; expectedFromCurrentGT.UP={expectedPosition}, LTW={MatrixText(expectedMatrix)}; expectedFromRestoreGT.UP={restorePosition}, LTW={MatrixText(restoreMatrix)}";
            if (universePosition.HasValue)
                observation += $"; UP-to-currentGT {PositionError(universePosition.Value, expectedPosition)}; UP-to-restoreGT {PositionError(universePosition.Value, restorePosition)}";
            if (ltw.HasValue)
                observation += $"; LTW-to-currentGT {MatrixError(ltw.Value, expectedMatrix)}; LTW-to-restoreGT {MatrixError(ltw.Value, restoreMatrix)}";
            if (local.HasValue)
            {
                bool hasParent = em.HasComponent<Unity.Transforms.Parent>(entity);
                bool hasPost = em.HasComponent<Unity.Transforms.PostTransformMatrix>(entity);
                observation += $"; LocalTransform.Parent={hasParent}, PostTransformMatrix={hasPost}";
                if (!hasParent && !hasPost)
                    observation += $"; unparented-local-to-currentGT {MatrixError(local.Value.ToMatrix(), expectedMatrix)}; unparented-local-to-restoreGT {MatrixError(local.Value.ToMatrix(), restoreMatrix)}";
                else observation += "; local/world comparison skipped: different transform space/composition";
            }
            PropagationNotice(observation + "; LTW comparisons use this observation only; component evidence, visuals/collisions unverified.");
        }
        catch (Exception e)
        {
            PropagationNotice(observation + $"; diagnostic failed: {e.GetType().Name}: {e.Message}; restoration unaffected; visuals/collisions unverified.");
        }
    }

    private static string PositionError(double3 actual, double3 expected)
    {
        double x = actual.x - expected.x, y = actual.y - expected.y, z = actual.z - expected.z;
        double error = Math.Sqrt(x * x + y * y + z * z);
        return $"positionError={error:G9}, near={double.IsFinite(error) && error <= PositionTolerance} (tol={PositionTolerance})";
    }

    private static double RotationError(float4 a, float4 b)
    {
        if (!UnitRotation(a) || !UnitRotation(b)) return double.NaN;
        double minus = 0, plus = 0;
        for (int i = 0; i < 4; i++)
        {
            double d = (double)a[i] - b[i], s = (double)a[i] + b[i];
            minus += d * d;
            plus += s * s;
        }
        return Math.Sqrt(Math.Min(minus, plus));
    }

    private static string MatrixText(float4x4 matrix) =>
        $"columns=[{matrix.c0}; {matrix.c1}; {matrix.c2}; {matrix.c3}]";

    private static string MatrixError(float4x4 actual, float4x4 expected)
    {
        double basisError = Math.Max(ColumnError(actual.c0, expected.c0),
            Math.Max(ColumnError(actual.c1, expected.c1), ColumnError(actual.c2, expected.c2)));
        double homogeneousError = Math.Abs((double)actual.c3.w - expected.c3.w);
        return $"{PositionError(actual.c3.xyz, expected.c3.xyz)}, basisMaxError={basisError:G9}, basisNear={double.IsFinite(basisError) && basisError <= BasisTolerance}, homogeneousError={homogeneousError:G9}, homogeneousNear={double.IsFinite(homogeneousError) && homogeneousError <= BasisTolerance} (tol={BasisTolerance})";
    }

    private static double ColumnError(float4 actual, float4 expected) =>
        Math.Max(Math.Max(Math.Abs((double)actual.x - expected.x), Math.Abs((double)actual.y - expected.y)),
            Math.Max(Math.Abs((double)actual.z - expected.z), Math.Abs((double)actual.w - expected.w)));

    private static void PropagationNotice(string text)
    {
        if (_propagationLines >= MaxPropagationLines) return;
        _propagationLines++;
        try { Log?.LogInfo("Garage propagation diagnostic: " + text); }
        catch { }
    }

    private static void ProbeCodec(EntityManager em, Core core, Entity coreEntity, Dictionary<string, Sample> current)
    {
        if (_codecAttempted || CfgCodecProbe == null || !CfgCodecProbe.Value || _phase != Phase.Build || current.Count == 0)
            return;
        var singleton = em.GetComponentData<Core.Singleton>(coreEntity);
        if (singleton._scPrefabsMap == IntPtr.Zero || singleton._scPrefabDataMap == IntPtr.Zero ||
            singleton._scPrefabSizeOf == IntPtr.Zero || core._componentsMap == null) return;
        foreach (var sample in current.Values)
        {
            var prefab = new SCPrefab { _prefab = sample.Prefab };
            if (!core._componentsMap.ContainsKey(prefab) || core._componentsMap[prefab] == null) continue;
            _codecAttempted = true;
            try
            {
                var copy = sample.Transform;
                uint encoded = copy.Encode(singleton, prefab);
                var decoded = GarageTransform.Decode(singleton, prefab, encoded);
                Notice($"Garage codec local-copy sample: prefab={sample.Prefab:X16}, packed={encoded:X8}, inputPos={sample.Transform._transform.pos}, decodedPos={decoded._transform.pos}, inputRot={sample.Transform._transform.rot.value}, decodedRot={decoded._transform.rot.value}, finite={FiniteTransform(decoded)}, exact={SameTransform(sample.Transform, decoded)}; decoded data never applied, not save/network evidence.");
            }
            catch (Exception e)
            {
                Notice($"Garage codec sample failed: {e.GetType().Name}; not retried this session.", true);
            }
            return;
        }
    }

    private static bool TryGetBasis(EntityManager em, out GarageGrabberSingleton basis)
    {
        basis = default;
        if (!TrySingleton(em, "GarageGrabberSingleton", out var entity)) return false;
        basis = em.GetComponentData<GarageGrabberSingleton>(entity);
        return FiniteBasis(basis);
    }

    private static void Warn(string text)
    {
        Notice(text, true);
    }

    private static ComponentType GameType(string name) =>
        ComponentType.ReadOnly(Il2CppSystem.Type.GetType(name + ", Assembly-CSharp"));

    private static bool TrySingleton(EntityManager em, string name, out Entity entity)
    {
        entity = default;
        var query = em.CreateEntityQuery(new ComponentType[] { GameType(name) });
        try
        {
            if (query.CalculateEntityCount() != 1) return false;
            entity = query.GetSingletonEntity();
            return em.Exists(entity);
        }
        finally { query.Dispose(); }
    }

    private static string Key(SCGuid id)
    {
        if ((id._a | id._b | id._c | id._d) == 0) return null;
        return id._a.ToString("X8") + id._b.ToString("X8") + id._c.ToString("X8") + id._d.ToString("X8");
    }

    private static bool SameEntity(Entity a, Entity b) => a.Index == b.Index && a.Version == b.Version;
    private static bool SameSample(Sample a, Sample b) =>
        SameEntity(a.Entity, b.Entity) && a.Prefab == b.Prefab && SameTransform(a.Transform, b.Transform);
    private static bool SameTransform(GarageTransform a, GarageTransform b) =>
        Same(a._transform.pos, b._transform.pos) && Same(a._transform.rot.value, b._transform.rot.value);
    private static bool FiniteTransform(GarageTransform value) =>
        Finite(value._transform.pos) && UnitRotation(value._transform.rot.value);

    private static bool FiniteBasis(GarageGrabberSingleton basis)
    {
        if (!Finite(basis._Matrix) || !Finite(basis._MatrixInv) ||
            !UnitRotation(basis._Rotation.value) || !UnitRotation(basis._RotationInv.value)) return false;
        var product = math.mul(basis._Matrix, basis._MatrixInv);
        return Near(product.c0, new double4(1, 0, 0, 0)) && Near(product.c1, new double4(0, 1, 0, 0)) &&
            Near(product.c2, new double4(0, 0, 1, 0)) && Near(product.c3, new double4(0, 0, 0, 1));
    }

    private static bool SameBasis(GarageGrabberSingleton a, GarageGrabberSingleton b) =>
        Near(a._Matrix, b._Matrix) && Near(a._MatrixInv, b._MatrixInv) &&
        Near(a._Rotation.value, b._Rotation.value) && Near(a._RotationInv.value, b._RotationInv.value);
    private static bool UnitRotation(float4 value)
    {
        if (!Finite(value)) return false;
        double norm = (double)value.x * value.x + (double)value.y * value.y +
            (double)value.z * value.z + (double)value.w * value.w;
        return Math.Abs(norm - 1.0) <= 0.001;
    }
    private static bool Finite(float3 value) =>
        float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
    private static bool Finite(float4 value) =>
        float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z) && float.IsFinite(value.w);
    private static bool Finite(double4 value) =>
        double.IsFinite(value.x) && double.IsFinite(value.y) && double.IsFinite(value.z) && double.IsFinite(value.w);
    private static bool Finite(double4x4 value) =>
        Finite(value.c0) && Finite(value.c1) && Finite(value.c2) && Finite(value.c3);
    private static bool Same(float3 a, float3 b) => a.x == b.x && a.y == b.y && a.z == b.z;
    private static bool Same(float4 a, float4 b) => a.x == b.x && a.y == b.y && a.z == b.z && a.w == b.w;
    private static bool Same(double4 a, double4 b) => a.x == b.x && a.y == b.y && a.z == b.z && a.w == b.w;
    private static bool Same(double4x4 a, double4x4 b) =>
        Same(a.c0, b.c0) && Same(a.c1, b.c1) && Same(a.c2, b.c2) && Same(a.c3, b.c3);
    private static bool Near(double4 a, double4 b) => Finite(a) &&
        Math.Abs(a.x - b.x) <= 0.0001 && Math.Abs(a.y - b.y) <= 0.0001 &&
        Math.Abs(a.z - b.z) <= 0.0001 && Math.Abs(a.w - b.w) <= 0.0001;
    private static bool Near(float4 a, float4 b) => Finite(a) &&
        Math.Abs(a.x - b.x) <= 0.0001f && Math.Abs(a.y - b.y) <= 0.0001f &&
        Math.Abs(a.z - b.z) <= 0.0001f && Math.Abs(a.w - b.w) <= 0.0001f;
    private static bool Near(double4x4 a, double4x4 b) =>
        Near(a.c0, b.c0) && Near(a.c1, b.c1) && Near(a.c2, b.c2) && Near(a.c3, b.c3);

    private static void Invalidate(string reason)
    {
        if (_phase != Phase.Unknown) Notice("Garage polling invalidated: " + reason + ".", true);
        Clear();
    }

    private static void Block(string reason)
    {
        _blocked = true;
        Notice("Garage preservation suspended until ResetSession: " + reason + "; no retry writes.", true);
        Clear();
    }

    private static void Notice(string text, bool warning = false)
    {
        if (_lines >= 48) return;
        _lines++;
        try
        {
            if (warning) Log?.LogWarning(text);
            else Log?.LogInfo(text);
        }
        catch { }
    }
}
