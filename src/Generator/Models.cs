using System;
using System.Collections.Immutable;

namespace AmbiECS.Generator;

// Everything the emitter needs, as plain values: equal models from one edit to the next stop the pipeline before emitting.

enum ColumnKind
{
    Input,
    State,
    Scratch,
}

enum Storage
{
    NativeArray,
    ManagedArray,
    TransformAccessArray,
}

sealed record TypeModel(
    string? Namespace,
    string Name,
    bool IsBehaviour,
    bool HasCleanupAttribute,
    int Capacity,
    bool Fixed,
    bool RetainColumnsWhenEmpty,
    bool HasSyncHook,
    EquatableArray<ColumnModel> Columns,
    EquatableArray<GlueModel> Glues,
    EquatableArray<PropertyModel> Properties,
    EquatableArray<ReadbackModel> Readbacks,
    EquatableArray<MessageModel> Messages,
    EquatableArray<string> Errors
)
{
    public string HintName => (Namespace is null ? "" : Namespace + ".") + Name + ".Ambi.g.cs";

    // whether any column is written through pending bits, and so whether the object carries them
    public bool HasPendingBits
    {
        get
        {
            foreach (GlueModel glue in Glues.Items)
            {
                if (glue.Bit != 0)
                    return true;
            }

            return false;
        }
    }
}

// Element: the column's element type. Initial: what a State row starts as.
sealed record ColumnModel(
    string Name,
    ColumnKind Kind,
    Storage Storage,
    string Element,
    int Stride,
    int PerRow,
    int Length,
    string Initial
);

// Sequence: a List<T> or T[] filling a strided row (IsList: the former). Targets: its element is an Ambi type, written as handles.
// Element: the element type (the member's own type when not a sequence). Tracked: the member is a tracked property, never
// re-read on MarkDirty. Bit: its pending bit, for tracked glue and for computed glue reading tracked properties; 0 for none.
sealed record GlueModel(
    string Member,
    string Column,
    bool Sequence,
    bool IsList,
    bool Targets,
    string Element,
    bool Tracked,
    ulong Bit
);

// A tracked property: a settable partial property the generator implements. Writes: the glue it rewrites on change, its own
// first, then the computed glue reading it. Mask: their pending bits.
sealed record PropertyModel(string Name, PartialModel Declaration, ulong Mask, EquatableArray<int> Writes);

// A get-only partial property reading its row of Column back. After: the JobHandle member to complete first, instead of the graph.
sealed record ReadbackModel(string Member, string Column, string? After, PartialModel Declaration);

// A partial property's defining declaration, repeated by the implementing one.
sealed record PartialModel(string Modifiers, string Type, bool HasGetter, string GetModifiers, string SetKeyword, string SetModifiers)
{
    public bool Settable => SetKeyword.Length > 0;
}

// Override: the message is virtual in a base class of the user's, so it is extended rather than declared.
sealed record MessageModel(string Name, string Accessibility, bool Override, bool CallBase);

// ImmutableArray compares by reference; this compares by content, which incremental caching needs.
readonly struct EquatableArray<T>(ImmutableArray<T> items) : IEquatable<EquatableArray<T>>
    where T : IEquatable<T>
{
    readonly ImmutableArray<T> _items = items;

    public ImmutableArray<T> Items => _items.IsDefault ? ImmutableArray<T>.Empty : _items;

    public bool Equals(EquatableArray<T> other)
    {
        ImmutableArray<T> left = Items;
        ImmutableArray<T> right = other.Items;

        if (left.Length != right.Length)
            return false;

        for (int i = 0; i < left.Length; i++)
        {
            if (!left[i].Equals(right[i]))
                return false;
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        int hash = 17;

        foreach (T item in Items)
            hash = hash * 31 + item.GetHashCode();

        return hash;
    }
}
