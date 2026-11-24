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

// A column written from the object: when its row is created, then whenever what feeds it changes (see Glue).
// NativeArray<T>, T[], or TransformAccessArray (appended to and removed from in row order).
[AttributeUsage(AttributeTargets.Field)]
public sealed class InputAttribute : Attribute
{
    public int Stride { get; set; } = 1;
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

// Links a member to the column it feeds; every column is written in full when its row is created. After that:
// - tracked: a settable partial property, which the generator implements, so it sees every change. Its column, and those of the
//   computed members reading it, are written on change: at once while the graph is idle, at the next flush otherwise.
//   MarkDirty never re-reads it, so a value that is fixed for the row's life costs one write.
// - untracked: any other member (a field, a computed property, a list changed in place) is re-read after MarkDirty(). A computed
//   property reading tracked properties is also rewritten when they change, glued or marked [Track].
// A List<T> or T[] fills a strided row, with handles when T is an Ambi type.
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class GlueAttribute(string column) : Attribute
{
    public string Column { get; } = column;
}

// Tracks a settable partial property that feeds no column itself, for the computed glue reading it: the generator implements
// it, and a change rewrites what reads it. Glue tracks a settable partial property without it; any other is left to you.
[AttributeUsage(AttributeTargets.Property)]
public sealed class TrackAttribute : Attribute { }

// Reads a get-only partial property back from its row of a column, completing the job graph first.
// After names a JobHandle member to complete instead: a promise that the column is safe to read once that handle completes.
[AttributeUsage(AttributeTargets.Property)]
public sealed class ReadbackAttribute(string column) : Attribute
{
    public string Column { get; } = column;

    public string After { get; set; }
}
