using Unity.Collections;
using Unity.Scripting.LifecycleManagement;

// A plain C# object with a row in the dense SoA registry of T while registered, and a Handle for its whole life, for rows that are
// not components of their own, such as a generator's kind rows. Same contract as AmbiMonoBehaviour<T>.
[NoAutoStaticsCleanup]
public abstract class AmbiBase<T> : IAmbiRegistrant
    where T : AmbiBase<T>
{
    AmbiSlot _slot = AmbiSlot.Unassigned;

    public static NativeArray<int> HandleToSoA => AmbiRegistry<T>.HandleToSoA;
    public static NativeArray<int> HandleVersions => AmbiRegistry<T>.HandleVersions;
    public static AmbiLookup Lookup => AmbiRegistry<T>.Lookup;

    internal static AmbiRegistry<T>.Registrations Registered => default;
    public static int RegisteredCount => AmbiRegistry<T>.RegisteredCount;

    public static int RowCount => AmbiRegistry<T>.RowCount;
    public static bool IsEmpty => AmbiRegistry<T>.RowCount == 0;

    public AmbiHandle Handle => AmbiRegistry<T>.HandleOf((T)this);
    public bool IsRegistered => _slot.Registered >= 0;
    internal int SoAIndex => _slot.SoAIndex;

    protected abstract int PreallocatedCapacity { get; }
    protected virtual bool RetainColumnsWhenEmpty => false;

    protected abstract void AllocateColumns();

    protected abstract void DeallocateColumns();

    protected abstract void SwapBackRow(int removedIndex, int lastIndex);

    protected virtual void LoadObjectToRow() { }

    protected virtual void SyncObjectToRow() { }

    internal void Register() => AmbiRegistry<T>.Register((T)this);

    internal void Deregister() => AmbiRegistry<T>.Deregister((T)this);

    internal void MarkDirty() => AmbiRegistry<T>.MarkDirty((T)this);

    // Equivalent of MonoBehaviour's OnDestroy: whoever drops it for good calls this to retire its handle.
    internal void Release() => AmbiRegistry<T>.Destroy((T)this);

    ref AmbiSlot IAmbiRegistrant.Slot => ref _slot;

    int IAmbiRegistrant.PreallocatedCapacity => PreallocatedCapacity;

    bool IAmbiRegistrant.RetainsColumnsWhenEmpty => RetainColumnsWhenEmpty;

    void IAmbiRegistrant.Allocate() => AllocateColumns();

    void IAmbiRegistrant.Deallocate() => DeallocateColumns();

    void IAmbiRegistrant.SwapBack(int removedIndex, int lastIndex) => SwapBackRow(removedIndex, lastIndex);

    void IAmbiRegistrant.Load() => LoadObjectToRow();

    void IAmbiRegistrant.Sync() => SyncObjectToRow();
}
