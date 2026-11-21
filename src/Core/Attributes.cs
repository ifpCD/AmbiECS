using System;

namespace AmbiECS;

public enum AmbiRegistration
{
    WhileEnabled, // components register in OnEnable and leave in OnDisable
    Manual, // you call Register() and Deregister(), as for pooling or culling
}

// Makes a partial class an Ambi type: the generator implements IAmbiRegistrant<T> for it from its columns and glue, plus the
// Unity messages a component needs. No base class is required.
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class AmbiAttribute : Attribute
{
    public int Capacity { get; set; } = 64;

    // throw past Capacity instead of growing
    public bool Fixed { get; set; }

    public bool RetainColumnsWhenEmpty { get; set; } = true;

    public AmbiRegistration Registration { get; set; }
}

// A column written from the object: on every sync after the object is marked dirty, or when its row is created only (LoadOnly).
// NativeArray<T>, T[], or TransformAccessArray (appended to and removed from in row order).
[AttributeUsage(AttributeTargets.Field)]
public sealed class InputAttribute : Attribute
{
    public int Stride { get; set; } = 1;

    public bool LoadOnly { get; set; }
}

// A column owned by jobs: set to Default (a constant) or DefaultFrom (a static member, by nameof) when a row is created, and
// carried along with its row. NativeArray<T> or T[].
[AttributeUsage(AttributeTargets.Field)]
public sealed class StateAttribute : Attribute
{
    public object Default { get; set; }

    public string DefaultFrom { get; set; }

    public int Stride { get; set; } = 1;
}

// A buffer owned by jobs and not tied to rows: PerRow elements per row of capacity, following growth, or a fixed Length.
// Never moved. NativeArray<T> or T[].
[AttributeUsage(AttributeTargets.Field)]
public sealed class ScratchAttribute : Attribute
{
    public int PerRow { get; set; } = 1;

    public int Length { get; set; }
}

// Links a member to the column it feeds. A settable partial property gets a setter that marks the object dirty; a get-only
// partial property reads its row back. A List<T> or T[] fills a strided row, with handles when T is an Ambi type.
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class GlueAttribute(string column) : Attribute
{
    public string Column { get; } = column;
}
