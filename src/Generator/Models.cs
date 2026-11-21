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
    EquatableArray<ColumnModel> Columns,
    EquatableArray<GlueModel> Glues,
    EquatableArray<MessageModel> Messages,
    EquatableArray<string> Errors
)
{
    public string HintName => (Namespace is null ? "" : Namespace + ".") + Name + ".Ambi.g.cs";
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
    bool LoadOnly,
    string Initial
);

// Sequence: a List<T> or T[] filling a strided row. Targets: its element is an Ambi type, written as handles.
// Element: the element type (the member's own type when not a sequence). Source: the expression read from the member.
sealed record GlueModel(string Member, string Column, bool Sequence, bool Targets, string Element, string Source, PartialModel? Partial);

// A partial property's defining declaration, repeated by the implementing one. No setter: it reads its row back.
sealed record PartialModel(string Modifiers, string Type, bool HasGetter, string GetModifiers, string SetKeyword, string SetModifiers)
{
    public bool ReadBack => SetKeyword.Length == 0;
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
