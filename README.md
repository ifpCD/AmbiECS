# AmbiECS

AmbiECS is a hybrid ECS for Unity 7 (C# 14) that bridges MonoBehaviours with Burst-compiled job graphs.  

Annotated classes and properties are tracked by a source generator that packs its data into the declared persistent SoA storage, tracking property changes, and managing generational handles.  
The scheduler manages data synchronization and provides a subsystem pattern to deal with flushing, scheduling, and retrieving results.

## Terminology
- `Column` is a densely packed array annotated either with `[Input]`, `[State]`, or `[Scratch]`.
- `Row` is an index within each `Column` for a given registered class instance, that may change during each flush.

## Annotations
### Classes
- `Ambi` makes a `partial` class, a component or a plain object, an Ambi type through source generation.
### Buffers
- `Input` is a `Column` for OOP side authoring values and read-only access in the Job Graph.
- `Scratch` is a effectively a persistently allocated scratchpad, sized either by the instance capacity, or a fixed length.
- `State` is a `Column` without an OOP value, that persists across the entirety of an instance's registration through swap backs.
### Properties
- `Glue` is how you point an OOP property to an `Input Column`. If it's a property getter that's reading a `Track` property, it automatically synchronizes on change.
- `Track` is used if a piece of data is a derivative to the value you want to use in the Job Graph without doing it in DOD.
- `Readback` is a computed getter that lets you introspect into the Job Graph results on the main thread, at the cost of potentially waiting for job completion.

## Classes

- `Ambi<T>` keeps each type's rows tightly packed as objects come and go, and hands out their handles.
- `AmbiScheduler` runs the frame: finishes last frame's jobs, applies changes and schedules systems.
- `AmbiHandle` and `AmbiLookup` provide stable references between objects that jobs can resolve.
- `AmbiTransform` gives shared, parallel access to transforms.
- `IAmbiSystem` is the interface to implement in order to schedule jobs each frame, and use the pre-flushing scheduler callback.

## Roadmap

Inter-dependency chaining is not fully figured out yet.  

## Implementation

```cs
[Ambi(Capacity = 10)]
internal partial class Foo : MonoBehaviour
{
    const int BAR_STRIDE = 15;

    public static JobHandle PromisedCompletionHandle;

    [Input]
    public static NativeArray<float> TargetVolumes;

    [Input]
    public static float[] ConstantFactors;

    [Input(Stride = BAR_STRIDE)]
    public static NativeArray<AmbiHandle> BarHandles;

    // Does not have an OOP input, but instance swap backs preserve the information between frames.
    [State(Default = 1f)]
    public static NativeArray<float> CurrentVolumes;

    // Manually managed via optional callbacks
    [Scratch(Length = 2048)]
    public static NativeArray<int> ArbitraryScratch;

    [Track]
    [field: SerializeField]
    public partial float Volume { get; set; } = 1f;

    // If Volume changes, this recomputes and updates TargetVolumes
    [Glue(nameof(TargetVolumes))]
    float TargetVolume => Mathf.Clamp01(Volume);

    [field: SerializeField]
    [Glue(nameof(ConstantFactors))]
    public partial float ConstantFactor { get; set; } = 3.8f;

    // Automatically resolves Bust friendly Bar AmbiHandles
    [Glue(nameof(BarHandles))]
    public List<Bar> Bars = [];

    // DOD value that can be read back with the promise that it's ready after a specified JobHandle
    [Readback(nameof(CurrentVolumes), After = nameof(PromisedCompletionHandle))]
    public partial float CurrentVolume { get; }

    static partial void OnColumnsAllocated(int capacity) { }

    static partial void OnColumnsResized(int rowCount, int capacity) { }

    static partial void OnColumnsDeallocating() { }

    partial void OnRowLoaded(int row) { }

    partial void OnRowSynced(int row) { }

    partial void OnRowRemoved(int row, int lastRow) { }
}

[Ambi(Capacity = 150)]
internal partial class Bar : MonoBehaviour
{
    [Input]
    public static NativeArray<int> Contributions;

    [Glue(nameof(Contributions))]
    public int Contribution { get; set; } = 12;
}
```

## Source Generation Example (Partially omitted)

```cs
partial class Foo : AmbiECS.IAmbiRegistrant<Foo>
{
    static void AmbiECS.IAmbiRegistrant<Foo>.AllocateColumns(int capacity)
    {
        TargetVolumes = new(capacity, Unity.Collections.Allocator.Persistent);
        ConstantFactors = new float[capacity];
        BarHandles = new(capacity * 15, Unity.Collections.Allocator.Persistent);
        CurrentVolumes = new(capacity, Unity.Collections.Allocator.Persistent);
        ArbitraryScratch = new(2048, Unity.Collections.Allocator.Persistent);
        OnColumnsAllocated(capacity);
    }

    static void AmbiECS.IAmbiRegistrant<Foo>.ResizeColumns(int rowCount, int capacity)
    {
        AmbiECS.AmbiColumn.Resize(ref TargetVolumes, capacity);
        System.Array.Resize(ref ConstantFactors, capacity);
        AmbiECS.AmbiColumn.Resize(ref BarHandles, capacity * 15);
        AmbiECS.AmbiColumn.Resize(ref CurrentVolumes, capacity);
        OnColumnsResized(rowCount, capacity);
    }

    static void AmbiECS.IAmbiRegistrant<Foo>.DeallocateColumns()
    {
        OnColumnsDeallocating();
        TargetVolumes.Dispose();
        ConstantFactors = null;
        BarHandles.Dispose();
        CurrentVolumes.Dispose();
        ArbitraryScratch.Dispose();
    }

    void AmbiECS.IAmbiRegistrant<Foo>.LoadRow(int row)
    {
        CurrentVolumes[row] = (float)(1);
        TargetVolumes[row] = TargetVolume;
        ConstantFactors[row] = ConstantFactor;
        AmbiECS.AmbiColumn.WriteHandles(BarHandles, row, 15, CollectionsMarshal.AsSpan(Bars));
        _ambiPending = 0;
        OnRowLoaded(row);
    }

    void AmbiECS.IAmbiRegistrant<Foo>.SyncRow(int row, bool dirty)
    {
        ulong pending = _ambiPending;
        _ambiPending = 0;

        if (dirty)
        {
            AmbiECS.AmbiColumn.WriteHandles(BarHandles, row, 15, CollectionsMarshal.AsSpan(Bars));
        }

        if (dirty || (pending & 0x1UL) != 0)
            TargetVolumes[row] = TargetVolume;
        if ((pending & 0x2UL) != 0)
            ConstantFactors[row] = ConstantFactor;
        OnRowSynced(row);
    }

    void AmbiECS.IAmbiRegistrant<Foo>.RemoveRow(int row, int lastRow)
    {
        if (row != lastRow)
        {
            TargetVolumes[row] = TargetVolumes[lastRow];
            ConstantFactors[row] = ConstantFactors[lastRow];
            AmbiECS.AmbiColumn.MoveBlock(BarHandles, lastRow, row, 15);
            CurrentVolumes[row] = CurrentVolumes[lastRow];
        }
        OnRowRemoved(row, lastRow);
    }

    public partial float Volume
    {
        get;
        set
        {
            if (EqualityComparer<float>.Default.Equals(field, value))
                return;

            field = value;

            _ambiPending |= 0x1UL;
            AmbiECS.Ambi<Foo>.MarkPending(this);
        }
    }

    public partial float ConstantFactor
    {
        get;
        set
        {
            if (EqualityComparer<float>.Default.Equals(field, value))
                return;

            field = value;

            _ambiPending |= 0x2UL;
            AmbiECS.Ambi<Foo>.MarkPending(this);
        }
    }

    public partial float CurrentVolume
    {
        get
        {
            PromisedCompletionHandle.Complete();
            int row = _ambiSlot.Row;
            return row < 0 ? default : CurrentVolumes[row];
        }
    }

    static partial void OnColumnsAllocated(int capacity);

    static partial void OnColumnsResized(int rowCount, int capacity);

    static partial void OnColumnsDeallocating();

    partial void OnRowLoaded(int row);

    partial void OnRowSynced(int row);

    partial void OnRowRemoved(int row, int lastRow);
}
```