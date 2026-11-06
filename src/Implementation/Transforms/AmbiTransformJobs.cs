using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine.Jobs;

[BurstCompile]
internal struct ReadTransformsJob : IJobParallelForTransform
{
    // csharpier-ignore-start
    [WriteOnly] public NativeArray<float3> Positions;
    [WriteOnly] public NativeArray<quaternion> Rotations;
    [WriteOnly] public NativeArray<float4x4> LocalToWorlds;
    // csharpier-ignore-end

    public void Execute(int index, TransformAccess transform)
    {
        if (!transform.isValid)
            return;

        Positions[index] = transform.position;
        Rotations[index] = transform.rotation;
        LocalToWorlds[index] = transform.localToWorldMatrix;
    }
}

[BurstCompile]
internal struct ResolveTransformsJob : IJobParallelFor
{
    // csharpier-ignore-start
    [ReadOnly] public AmbiLookup Rows;
    [ReadOnly] public NativeArray<AmbiHandle> Leaders;
    [ReadOnly] public NativeArray<float3> ReadPositions;
    [ReadOnly] public NativeArray<quaternion> ReadRotations;
    [ReadOnly] public NativeArray<float4x4> ReadLocalToWorlds;

    [WriteOnly] public NativeArray<float3> Positions;
    [WriteOnly] public NativeArray<quaternion> Rotations;
    [WriteOnly] public NativeArray<float4x4> LocalToWorlds;
    [WriteOnly] public NativeArray<bool> Follows;
    // csharpier-ignore-end

    public void Execute(int row)
    {
        float4x4 own = ReadLocalToWorlds[row];

        if (!Rows.TryResolve(Leaders[row], out int leader))
        {
            Positions[row] = ReadPositions[row];
            Rotations[row] = ReadRotations[row];
            LocalToWorlds[row] = own;
            Follows[row] = false;
            return;
        }

        float3 position = ReadPositions[leader];
        quaternion rotation = ReadRotations[leader];
        float3 scale = new(math.length(own.c0.xyz), math.length(own.c1.xyz), math.length(own.c2.xyz));

        Positions[row] = position;
        Rotations[row] = rotation;
        LocalToWorlds[row] = float4x4.TRS(position, rotation, scale);
        Follows[row] = true;
    }
}

[BurstCompile]
internal struct WriteDrivenTransformsJob : IJobParallelForTransform
{
    // csharpier-ignore-start
    [ReadOnly] public NativeArray<AmbiHandle> DrivenRows;
    [ReadOnly] public AmbiLookup Rows;
    [ReadOnly] public NativeArray<bool> Follows;
    [ReadOnly] public NativeArray<float3> Positions;
    [ReadOnly] public NativeArray<quaternion> Rotations;
    // csharpier-ignore-end

    public void Execute(int index, TransformAccess transform)
    {
        if (!transform.isValid || !Rows.TryResolve(DrivenRows[index], out int row) || !Follows[row])
            return;

        transform.SetPositionAndRotation(Positions[row], Rotations[row]);
    }
}

// indirection resolving jobs

[BurstCompile]
internal struct GatherPositionsJob : IJobParallelFor
{
    // csharpier-ignore-start
    [ReadOnly] public NativeArray<AmbiHandle> Handles;
    [ReadOnly] public AmbiLookup Rows;
    [ReadOnly] public NativeArray<float3> RowPositions;

    [WriteOnly] public NativeArray<float3> Positions;
    // csharpier-ignore-end

    public void Execute(int index)
    {
        if (Rows.TryResolve(Handles[index], out int row))
            Positions[index] = RowPositions[row];
    }
}

[BurstCompile]
internal struct GatherLocalToWorldsJob : IJobParallelFor
{
    // csharpier-ignore-start
    [ReadOnly] public NativeArray<AmbiHandle> Handles;
    [ReadOnly] public AmbiLookup Rows;
    [ReadOnly] public NativeArray<float4x4> RowLocalToWorlds;

    [WriteOnly] public NativeArray<float4x4> LocalToWorlds;
    // csharpier-ignore-end

    public void Execute(int index)
    {
        if (Rows.TryResolve(Handles[index], out int row))
            LocalToWorlds[index] = RowLocalToWorlds[row];
    }
}
