using Unity.Collections;
using Unity.Mathematics;
using UnityEngine.Jobs;

public sealed partial class AmbiTransform
{
    internal const int HARDCODED_MAXIMUM_AMBI_TRANSFORMS = 16384;

    protected override int PreallocatedCapacity => HARDCODED_MAXIMUM_AMBI_TRANSFORMS;
    protected override bool RetainColumnsWhenEmpty => true;

    // what the gather reads, every frame
    internal static TransformAccessArray Transforms;
    internal static NativeArray<float3> ReadPositions;
    internal static NativeArray<quaternion> ReadRotations;
    internal static NativeArray<float4x4> ReadLocalToWorlds;

    internal static NativeArray<AmbiHandle> Leaders;

    // the pose everything reads: the row's own, or its leader's while it follows one
    internal static NativeArray<float3> Positions;
    internal static NativeArray<quaternion> Rotations;
    internal static NativeArray<float4x4> LocalToWorlds;
    internal static NativeArray<bool> Follows;

    internal static TransformAccessArray DrivenTransforms;
    internal static NativeArray<AmbiHandle> DrivenRows;
    static AmbiTransform[] _driven;

    // csharpier-ignore
    protected override void AllocateColumns()
    {
        Transforms        = new(PreallocatedCapacity);
        ReadPositions     = new(PreallocatedCapacity, Allocator.Persistent);
        ReadRotations     = new(PreallocatedCapacity, Allocator.Persistent);
        ReadLocalToWorlds = new(PreallocatedCapacity, Allocator.Persistent);

        Leaders           = new(PreallocatedCapacity, Allocator.Persistent);

        Positions         = new(PreallocatedCapacity, Allocator.Persistent);
        Rotations         = new(PreallocatedCapacity, Allocator.Persistent);
        LocalToWorlds     = new(PreallocatedCapacity, Allocator.Persistent);
        Follows           = new(PreallocatedCapacity, Allocator.Persistent);

        DrivenTransforms  = new(PreallocatedCapacity);
        DrivenRows        = new(PreallocatedCapacity, Allocator.Persistent);
        _driven           = new AmbiTransform[PreallocatedCapacity];
    }

    protected override void DeallocateColumns()
    {
        Transforms.Dispose();
        ReadPositions.Dispose();
        ReadRotations.Dispose();
        ReadLocalToWorlds.Dispose();

        Leaders.Dispose();

        Positions.Dispose();
        Rotations.Dispose();
        LocalToWorlds.Dispose();
        Follows.Dispose();

        DrivenTransforms.Dispose();
        DrivenRows.Dispose();
        _driven = null;
    }

    protected override void LoadObjectToRow() => Transforms.Add(Target);

    protected override void SyncObjectToRow()
    {
        Leaders[SoAIndex] = _leader?.Handle ?? AmbiHandle.Null;

        if (_leader != null && _drivenIndex < 0)
            Drive();
    }

    protected override void SwapBackRow(int removedIndex, int lastIndex)
    {
        Transforms.RemoveAtSwapBack(removedIndex);
        Leaders[removedIndex] = Leaders[lastIndex];

        if (_drivenIndex >= 0)
            Undrive();
    }
}
