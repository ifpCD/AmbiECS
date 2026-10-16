using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Jobs;

[NoAutoStaticsCleanup]
public static class AmbiTransforms
{
    static readonly Dictionary<EntityId, AmbiTransform> _byTransform = new(HC.MAX_AMBI_TRANSFORMS);
    static readonly Scheduling _scheduling = new();

    static JobHandle _resolved;
    static bool _resolvedThisFrame;

    public static AmbiTransform Acquire(Transform transform)
    {
        if (transform == null)
            return null;

        EntityId key = transform.GetEntityId();

        if (!_byTransform.TryGetValue(key, out AmbiTransform row))
        {
            if (AmbiTransform.RegisteredCount == HC.MAX_AMBI_TRANSFORMS)
                EvictUnheld();

            row = new AmbiTransform(transform, key);
            row.Register();
            _byTransform.Add(key, row);
        }

        row.Holders++;
        return row;
    }

    public static void Release(AmbiTransform row)
    {
        if (row != null)
            row.Holders--;
    }

    public static JobHandle Resolved
    {
        get
        {
            if (_resolvedThisFrame)
                return _resolved;

            _resolvedThisFrame = true;

            if (AmbiTransform.CompletelyInactive)
                return _resolved = default;

            // csharpier-ignore
            JobHandle read = new ReadTransformsJob
            {
                Positions     = AmbiTransform.ReadPositions,
                Rotations     = AmbiTransform.ReadRotations,
                LocalToWorlds = AmbiTransform.ReadLocalToWorlds,
            }.ScheduleReadOnly(AmbiTransform.Transforms, 32);

            // csharpier-ignore
            return _resolved = new ResolveTransformsJob
            {
                Rows              = AmbiTransform.Lookup,
                Leaders           = AmbiTransform.Leaders,
                ReadPositions     = AmbiTransform.ReadPositions,
                ReadRotations     = AmbiTransform.ReadRotations,
                ReadLocalToWorlds = AmbiTransform.ReadLocalToWorlds,

                Positions         = AmbiTransform.Positions,
                Rotations         = AmbiTransform.Rotations,
                LocalToWorlds     = AmbiTransform.LocalToWorlds,
                Follows           = AmbiTransform.Follows,
            }.Schedule(AmbiTransform.ActiveCount, 64, read);
        }
    }

    // csharpier-ignore
    public static JobHandle GatherPositions(NativeArray<AmbiHandle> handles, int count, NativeArray<float3> positions, JobHandle dependency = default)
    {
        if (count == 0 || AmbiTransform.CompletelyInactive)
            return dependency;

        return new GatherPositionsJob
        {
            Handles       = handles,
            Rows          = AmbiTransform.Lookup,
            RowPositions  = AmbiTransform.Positions,

            Positions     = positions,
        }.Schedule(count, 64, JobHandle.CombineDependencies(Resolved, dependency));
    }

    // csharpier-ignore
    public static JobHandle GatherLocalToWorlds(NativeArray<AmbiHandle> handles, int count, NativeArray<float4x4> localToWorlds, JobHandle dependency = default)
    {
        if (count == 0 || AmbiTransform.CompletelyInactive)
            return dependency;

        return new GatherLocalToWorldsJob
        {
            Handles          = handles,
            Rows             = AmbiTransform.Lookup,
            RowLocalToWorlds = AmbiTransform.LocalToWorlds,

            LocalToWorlds    = localToWorlds,
        }.Schedule(count, 64, JobHandle.CombineDependencies(Resolved, dependency));
    }

    // creates garbage
    // this realistically only happens when a gameobject is deleted
    // todo: reuse?
    // optimization candidate: main thread spike alert
    static void EvictUnheld()
    {
        foreach (AmbiTransform row in AmbiTransform.Registered)
        {
            if (row.Holders > 0)
                continue;

            _byTransform.Remove(row.Key);
            row.Release();
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
    static void BeginPlaying()
    {
        AmbiScheduler.Subscribe(_scheduling);

        Application.quitting -= EndPlaying;
        Application.quitting += EndPlaying;
    }

    static void EndPlaying()
    {
        foreach (AmbiTransform row in AmbiTransform.Registered)
            row.Release();

        _byTransform.Clear();
    }

    sealed class Scheduling : IAmbiSystem
    {
        public void BeforeFlush() => _resolvedThisFrame = false;

        // csharpier-ignore
        public JobHandle Schedule()
        {
            JobHandle resolved = Resolved;

            if (AmbiTransform.DrivenCount == 0)
                return resolved;

            return new WriteDrivenTransformsJob
            {
                DrivenRows = AmbiTransform.DrivenRows,
                Rows       = AmbiTransform.Lookup,
                Follows    = AmbiTransform.Follows,
                Positions  = AmbiTransform.Positions,
                Rotations  = AmbiTransform.Rotations,
            }.Schedule(AmbiTransform.DrivenTransforms, resolved);
        }
    }
}
