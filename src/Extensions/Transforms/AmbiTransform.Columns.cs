using System;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine.Jobs;

namespace AmbiECS.Extensions;

public sealed partial class AmbiTransform
{
    [Input]
    internal static TransformAccessArray Transforms;

    [Input]
    internal static NativeArray<AmbiHandle> Leaders;

    // in row order: the transforms the gather reads every frame, and each row's leader
    // rewritten from the transforms every frame, so never carried along with their rows: the raw reads, and the pose everything
    // reads (the row's own, or its leader's while it follows one)
    // csharpier-ignore-start
    [Scratch] internal static NativeArray<float3>     ReadPositions;
    [Scratch] internal static NativeArray<quaternion> ReadRotations;
    [Scratch] internal static NativeArray<float4x4>   ReadLocalToWorlds;
    [Scratch] internal static NativeArray<float3>     Positions;
    [Scratch] internal static NativeArray<quaternion> Rotations;
    [Scratch] internal static NativeArray<float4x4>   LocalToWorlds;
    [Scratch] internal static NativeArray<bool>       Follows;
    // csharpier-ignore-end

    // the rows that follow a leader, in an order of their own: kept by hand through the column hooks
    internal static TransformAccessArray DrivenTransforms;
    internal static NativeArray<AmbiHandle> DrivenRows;
    static AmbiTransform[] _driven;

    static partial void OnColumnsAllocated(int capacity)
    {
        DrivenTransforms = new(capacity);
        DrivenRows = new(capacity, Allocator.Persistent);
        _driven = new AmbiTransform[capacity];
    }

    static partial void OnColumnsResized(int rowCount, int capacity)
    {
        DrivenTransforms.capacity = capacity;
        DrivenRows.Resize(capacity, DrivenCount);
        Array.Resize(ref _driven, capacity);
    }

    static partial void OnColumnsDeallocating()
    {
        DrivenTransforms.Dispose();
        DrivenRows.Dispose();
        _driven = null;
    }
}
