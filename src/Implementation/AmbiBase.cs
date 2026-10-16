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

    public static int ActiveCount => AmbiRegistry<T>.ActiveCount;
    public static bool CompletelyInactive => AmbiRegistry<T>.ActiveCount == 0;

    public AmbiHandle Handle => AmbiRegistry<T>.HandleOf((T)this);
    public bool IsRegistered => _slot.Registered >= 0;
    internal int SoAIndex => _slot.SoAIndex;

    protected abstract int AllocatedCapacity { get; }
    protected virtual bool RetainNativeWhenEmpty => false;

    protected abstract void AllocateNative();

    protected abstract void DeallocateNative();

    protected abstract void RemoveAtSwapBack(int removedIndex, int lastIndex);

    protected virtual void LoadObjectToArrays() { }

    protected virtual void SyncObjectToArrays() { }

    protected static void DisposeRegistry() => AmbiRegistry<T>.Dispose();

    internal void Register() => AmbiRegistry<T>.Register((T)this);

    internal void Deregister() => AmbiRegistry<T>.Deregister((T)this);

    internal void MarkDirty() => AmbiRegistry<T>.MarkDirty((T)this);

    // Equivalent of MonoBehaviour's OnDestroy: whoever drops it for good calls this to retire its handle.
    // Maybe invoke in finalizer?
    internal void Release() => AmbiRegistry<T>.Destroy((T)this);

    ref AmbiSlot IAmbiRegistrant.Slot => ref _slot;

    int IAmbiRegistrant.Capacity => AllocatedCapacity;

    bool IAmbiRegistrant.RetainsNative => RetainNativeWhenEmpty;

    void IAmbiRegistrant.Allocate() => AllocateNative();

    void IAmbiRegistrant.Deallocate() => DeallocateNative();

    void IAmbiRegistrant.SwapBack(int removedIndex, int lastIndex) => RemoveAtSwapBack(removedIndex, lastIndex);

    void IAmbiRegistrant.Load() => LoadObjectToArrays();

    void IAmbiRegistrant.Sync() => SyncObjectToArrays();
}
