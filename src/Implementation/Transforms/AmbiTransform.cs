using Unity.Collections;
using Unity.Mathematics;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Jobs;

// instead of doing TransformAccessArray on each instance of an arbitrary Ambi component
// we instead have a reference counted shared transform access class called AmbiTransform
// essentially - whenever a component needs to know its transform data
// it asks AmbiTransforms for an instance of AmbiTransform by passing its Entity ID (component's EntityID)
// if another component has already asked for an AmbiTransform, it simply gets an existing AmbiTransform reference
//
// during Burst Parallel Jobs, components requiring its position/rotation/matrix data do an indirection call using their AmbiHandle for AmbiTransform
//
// additionally, if a component want its transform to track another transform
// instead of doing so on the main thread - they instead pass a transform they want their AmbiTransform to track, and it does so in the job graph
[NoAutoStaticsCleanup]
public sealed class AmbiTransform : AmbiBase<AmbiTransform>
{
    internal static TransformAccessArray Transforms;
    internal static NativeArray<float3> ReadPositions;
    internal static NativeArray<quaternion> ReadRotations;
    internal static NativeArray<float4x4> ReadLocalToWorlds;

    internal static NativeArray<AmbiHandle> Leaders;

    internal static NativeArray<float3> Positions;
    internal static NativeArray<quaternion> Rotations;
    internal static NativeArray<float4x4> LocalToWorlds;
    internal static NativeArray<bool> Follows;

    internal static TransformAccessArray DrivenTransforms;
    internal static NativeArray<AmbiHandle> DrivenRows;
    internal static int DrivenCount;
    static AmbiTransform[] _driven;

    internal readonly Transform Target;
    internal readonly EntityId Key;
    internal int Holders;

    AmbiTransform _leader;
    int _drivenIndex = AmbiSlot.NONE;

    internal AmbiTransform(Transform target, EntityId key)
    {
        Target = target;
        Key = key;
    }

    protected override int AllocatedCapacity => HC.MAX_AMBI_TRANSFORMS;

    public AmbiTransform Leader
    {
        get => _leader;
        set
        {
            if (_leader == value)
                return;

            _leader = value;
            MarkDirty();
        }
    }

    // csharpier-ignore
    protected override void AllocateNative()
    {
        Transforms        = new(AllocatedCapacity);
        ReadPositions     = new(AllocatedCapacity, Allocator.Persistent);
        ReadRotations     = new(AllocatedCapacity, Allocator.Persistent);
        ReadLocalToWorlds = new(AllocatedCapacity, Allocator.Persistent);

        Leaders           = new(AllocatedCapacity, Allocator.Persistent);

        Positions         = new(AllocatedCapacity, Allocator.Persistent);
        Rotations         = new(AllocatedCapacity, Allocator.Persistent);
        LocalToWorlds     = new(AllocatedCapacity, Allocator.Persistent);
        Follows           = new(AllocatedCapacity, Allocator.Persistent);

        DrivenTransforms  = new(AllocatedCapacity);
        DrivenRows        = new(AllocatedCapacity, Allocator.Persistent);
        _driven           = new AmbiTransform[AllocatedCapacity];
    }

    protected override void DeallocateNative()
    {
        Transforms.TryDispose();
        ReadPositions.TryDispose();
        ReadRotations.TryDispose();
        ReadLocalToWorlds.TryDispose();

        Leaders.TryDispose();

        Positions.TryDispose();
        Rotations.TryDispose();
        LocalToWorlds.TryDispose();
        Follows.TryDispose();

        DrivenTransforms.TryDispose();
        DrivenRows.TryDispose();
        _driven = null;
    }

    protected override void LoadObjectToArrays() => Transforms.Add(Target);

    protected override void SyncObjectToArrays()
    {
        Leaders[SoAIndex] = _leader?.Handle ?? AmbiHandle.Null;

        if (_leader != null && _drivenIndex < 0)
            Drive();
    }

    protected override void RemoveAtSwapBack(int removedIndex, int lastIndex)
    {
        Transforms.RemoveAtSwapBack(removedIndex);
        Leaders[removedIndex] = Leaders[lastIndex];

        if (_drivenIndex >= 0)
            Undrive();
    }

    void Drive()
    {
        _drivenIndex = DrivenCount;
        _driven[DrivenCount] = this;
        DrivenRows[DrivenCount++] = Handle;
        DrivenTransforms.Add(Target);
    }

    void Undrive()
    {
        int index = _drivenIndex;
        AmbiTransform last = _driven[--DrivenCount];

        _driven[index] = last;
        last._drivenIndex = index;
        DrivenRows[index] = DrivenRows[DrivenCount];
        _driven[DrivenCount] = null;
        DrivenTransforms.RemoveAtSwapBack(index);

        _drivenIndex = AmbiSlot.NONE;
    }
}
