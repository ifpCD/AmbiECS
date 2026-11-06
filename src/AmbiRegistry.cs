using System;
using Unity.Collections;
using Unity.Scripting.LifecycleManagement;

internal interface IAmbiRegistrant
{
    ref AmbiSlot Slot { get; }
    int PreallocatedCapacity { get; }
    bool RetainsColumnsWhenEmpty { get; }

    void Allocate();
    void Deallocate();
    void SwapBack(int removedIndex, int lastIndex);
    void Load();
    void Sync();
}

// The dense SoA registry of one Ambi type, shared by AmbiMonoBehaviour<T> and AmbiBase<T>.
//
// Two views of the same objects, each for one side:
// Registered: the objects registered right now, for main-thread code. Requests change it at once, so it never holds an object
// that has left or been destroyed, and Register counts against capacity there, throwing at its caller, never inside a flush.
// Rows: the objects the graph reads, packed in [0, ActiveCount), removed by swap-back.
// Each request queues its object once, and rows change only when AmbiScheduler flushes the queues. 
// Load fills a new row and Sync rewrites a row from its object; both run at the flush, write that row and nothing else, and issue no requests.
// Columns are conditionally freed when a drain leaves the type empty.
//
// Identities: an AmbiHandle names an object from the first read of its Handle until it is destroyed, whatever its registration in between.
// HandleToSoA[identity] is its row or -1, and HandleVersions retires the handle on destruction.
// Versions start at 1 (0 is Null) and outlive the table, so a recycled slot never reissues a handle someone still holds.
// AmbiHandle is used to inter-link managed IAmbiRegistrant instances in the narive DOD paradigm via indirection using AmbiLookup
//
// Nothing is allocated per request: Registered, the rows, the identities and the queue are all sized from the type's capacity.
[NoAutoStaticsCleanup]
internal static class AmbiRegistry<T>
    where T : class, IAmbiRegistrant
{
    static T[] _registered;
    internal static int RegisteredCount;
    static int _visits; // stamps the walks over Registered

    internal static T[] Rows;
    internal static int RowCount;
    static int _rowCapacity;
    static T _allocationOwner;

    internal static NativeArray<int> HandleToSoA;
    internal static NativeArray<int> HandleVersions;
    static int[] _versions; // authoritative, mirrored into HandleVersions
    static int[] _freeIdentities;
    static int _freeIdentityCount;
    static int _nextIdentity;
    static int _liveIdentities;

    static T[] _queue;
    static int _queueCount;

    internal static AmbiLookup Lookup => new() { HandleToSoA = HandleToSoA, HandleVersions = HandleVersions };

    static AmbiRegistry() => AmbiScheduler.Enlist(Depart, Arrive, Dispose);

    internal static void Register(T instance)
    {
        ref AmbiSlot slot = ref instance.Slot;

        if (slot.Registered >= 0 || (slot.Flags & AmbiSlot.DESTROYED) != 0)
            return;

        int capacity = instance.PreallocatedCapacity;

        if (RegisteredCount >= capacity)
            throw new InvalidOperationException($"{typeof(T).Name} capacity of {capacity} exceeded.");

        // sized once, and again only for a capacity configured larger since (sources)
        if (_registered == null || _registered.Length < capacity)
            Array.Resize(ref _registered, capacity);

        slot.Registered = RegisteredCount;
        _registered[RegisteredCount++] = instance;

        if (slot.SoAIndex >= 0)
            slot.Flags |= AmbiSlot.RELOAD;

        Settle(instance, ref slot);
    }

    internal static void Deregister(T instance)
    {
        ref AmbiSlot slot = ref instance.Slot;

        if (slot.Registered < 0)
            return;

        Unregister(ref slot);
        Settle(instance, ref slot);
    }

    internal static void MarkDirty(T instance)
    {
        ref AmbiSlot slot = ref instance.Slot;

        // a row is synced in full when it is created
        if (slot.Registered < 0)
            return;

        slot.Flags |= AmbiSlot.DIRTY;
        Settle(instance, ref slot);
    }

    internal static void Destroy(T instance)
    {
        ref AmbiSlot slot = ref instance.Slot;

        if ((slot.Flags & AmbiSlot.DESTROYED) != 0)
            return;

        Unregister(ref slot);
        slot.Flags |= AmbiSlot.DESTROYED;

        // only a row or a handle needs undoing
        if (slot.SoAIndex >= 0 || slot.Identity >= 0)
            Settle(instance, ref slot);
    }

    internal static AmbiHandle HandleOf(T instance)
    {
        ref AmbiSlot slot = ref instance.Slot;

        if ((slot.Flags & AmbiSlot.DESTROYED) != 0)
            return AmbiHandle.Null;

        if (slot.Identity == AmbiSlot.NONE)
            AcquireIdentity(instance, ref slot);

        return new AmbiHandle { Index = slot.Identity, Version = _versions[slot.Identity] };
    }

    // Code unload, once the graph is done: every object leaves, those still waiting to arrive included, and the identity table goes
    // with the rows whatever handles are still out, since no code is left to resolve them.
    static void Dispose()
    {
        while (RegisteredCount != 0)
            Unregister(ref _registered[RegisteredCount - 1].Slot);

        Drain();

        while (RowCount != 0)
        {
            T instance = Rows[RowCount - 1];
            Apply(instance, ref instance.Slot);
        }

        DisposeRows();
        HandleToSoA.DisposeIfAllocated();
        HandleVersions.DisposeIfAllocated();
    }

    // Out of Registered at once, by swap-back.
    static void Unregister(ref AmbiSlot slot)
    {
        int index = slot.Registered;

        if (index < 0)
            return;

        T last = _registered[--RegisteredCount];
        _registered[index] = last;
        last.Slot.Registered = index;
        _registered[RegisteredCount] = null;
        slot.Registered = AmbiSlot.NONE;
    }

    static void Settle(T instance, ref AmbiSlot slot)
    {
        if (!AmbiScheduler.Defers)
        {
            Apply(instance, ref slot);
            ReleaseRowsIfEmpty();
            return;
        }

        if ((slot.Flags & AmbiSlot.QUEUED) != 0)
            return;

        if (_queueCount == 0)
            SizeQueue(instance.PreallocatedCapacity);
        else if (_queueCount == _queue.Length)
        {
            // more objects changed than even a scene swap changes: land the queue now, waiting for the graph if it runs, rather
            // than lose a request
            AmbiScheduler.Complete();
            Drain();
        }

        slot.Flags |= AmbiSlot.QUEUED;
        _queue[_queueCount++] = instance;
    }

    // Twice the capacity: every row can leave and as many objects arrive in one frame, as in a scene swap. Sized while empty, so
    // that a capacity configured per session (sources) is followed.
    static void SizeQueue(int capacity)
    {
        if (_queue == null || _queue.Length < 2 * capacity)
            _queue = new T[2 * capacity];
    }

    // This type alone, outside a flush: teardown, and a full queue.
    static void Drain()
    {
        Depart();
        Arrive();
    }

    // Rows that go and identities that retire, before anything arrives: the rows never outnumber the capacity Register counted
    // against, and the identity slots are free again for the handles the arrivals' Syncs read.
    static void Depart()
    {
        for (int i = 0; i < _queueCount; i++)
        {
            T instance = _queue[i];
            ref AmbiSlot slot = ref instance.Slot;

            if (slot.SoAIndex >= 0 && Leaves(ref slot))
                RemoveRow(instance, ref slot);

            if ((slot.Flags & AmbiSlot.DESTROYED) != 0)
                RetireIdentity(ref slot);
        }
    }

    // Everything else, from the back, so that if a Load or Sync throws, whatever was not applied stays queued for the next drain.
    static void Arrive()
    {
        while (_queueCount > 0)
        {
            T instance = _queue[--_queueCount];
            _queue[_queueCount] = null;

            ref AmbiSlot slot = ref instance.Slot;
            slot.Flags &= ~AmbiSlot.QUEUED;
            Apply(instance, ref slot);
        }

        ReleaseRowsIfEmpty();
    }

    static void Apply(T instance, ref AmbiSlot slot)
    {
        if (slot.SoAIndex >= 0 && Leaves(ref slot))
            RemoveRow(instance, ref slot);

        if (slot.Registered >= 0)
        {
            if (slot.SoAIndex < 0)
                AddRow(instance, ref slot);
            else if ((slot.Flags & AmbiSlot.DIRTY) != 0)
                instance.Sync();
        }

        slot.Flags &= ~(AmbiSlot.DIRTY | AmbiSlot.RELOAD);

        if (slot.Identity >= 0)
            HandleToSoA[slot.Identity] = slot.SoAIndex;

        if ((slot.Flags & AmbiSlot.DESTROYED) != 0)
            RetireIdentity(ref slot);
    }

    // Its row goes when it is no longer registered, or registered again after leaving, so that the row starts over.
    static bool Leaves(ref AmbiSlot slot) => slot.Registered < 0 || (slot.Flags & AmbiSlot.RELOAD) != 0;

    static void AddRow(T instance, ref AmbiSlot slot)
    {
        Initialize(instance);

        if (RowCount == _rowCapacity)
            throw new InvalidOperationException($"{typeof(T).Name} capacity of {_rowCapacity} exceeded.");

        slot.SoAIndex = RowCount;
        Rows[RowCount++] = instance;

        instance.Load();
        instance.Sync();
    }

    static void RemoveRow(T instance, ref AmbiSlot slot)
    {
        int removedIndex = slot.SoAIndex;
        int lastIndex = --RowCount;

        if (removedIndex != lastIndex)
        {
            T moved = Rows[lastIndex];
            ref AmbiSlot movedSlot = ref moved.Slot;
            Rows[removedIndex] = moved;
            movedSlot.SoAIndex = removedIndex;

            if (movedSlot.Identity >= 0)
                HandleToSoA[movedSlot.Identity] = removedIndex;
        }

        instance.SwapBack(removedIndex, lastIndex);

        Rows[lastIndex] = null;
        slot.SoAIndex = AmbiSlot.NONE;
    }

    // Once per drain rather than per removal: a type emptied and refilled in one flush, such as a lone generator toggled or a kind
    // reattached, keeps its columns.
    static void ReleaseRowsIfEmpty()
    {
        if (RowCount == 0 && Rows != null && !_allocationOwner.RetainsColumnsWhenEmpty)
            DisposeRows();
    }

    static void AcquireIdentity(T instance, ref AmbiSlot slot)
    {
        EnsureIdentities(instance.PreallocatedCapacity);

        if (_freeIdentityCount > 0)
            slot.Identity = _freeIdentities[--_freeIdentityCount];
        else if (_nextIdentity < _freeIdentities.Length)
            slot.Identity = _nextIdentity++;
        else
            throw new InvalidOperationException($"{typeof(T).Name}: more than {_freeIdentities.Length} referenced objects alive at once.");

        _liveIdentities++;

        // An unused slot already maps to -1, so only an object with a row has an entry to write. While the graph is idle, as it is
        // whenever a Sync reads a Handle, it is written at once, so a reference synced in a flush resolves in the graph that follows.
        if (slot.SoAIndex < 0)
            return;

        if (AmbiScheduler.IsIdle)
            HandleToSoA[slot.Identity] = slot.SoAIndex;
        else
            Settle(instance, ref slot);
    }

    static void RetireIdentity(ref AmbiSlot slot)
    {
        int identity = slot.Identity;

        if (identity < 0)
            return;

        slot.Identity = AmbiSlot.NONE;

        int version = _versions[identity] + 1;
        _versions[identity] = version == 0 ? 1 : version; // 0 is Null

        HandleVersions[identity] = _versions[identity];
        HandleToSoA[identity] = AmbiSlot.NONE;
        _freeIdentities[_freeIdentityCount++] = identity;
        _liveIdentities--;

        DisposeIdentitiesIfUnused();
    }

    static void Initialize(T owner)
    {
        if (Rows != null)
            return;

        int capacity = owner.PreallocatedCapacity;
        EnsureIdentities(capacity);

        _rowCapacity = capacity;
        Rows = new T[capacity];
        _allocationOwner = owner;
        owner.Allocate();
    }

    static void DisposeRows()
    {
        if (Rows == null)
            return;

        _allocationOwner.Deallocate();
        _allocationOwner = null;
        Rows = null;
        _rowCapacity = 0;

        DisposeIdentitiesIfUnused();
    }

    // The identity table lives while any row or identity does.
    static void EnsureIdentities(int capacity)
    {
        if (HandleToSoA.IsCreated)
            return;

        if (capacity <= 0)
            throw new InvalidOperationException($"{typeof(T).Name} requires a positive maximum capacity.");

        if (_versions == null || _versions.Length < capacity)
        {
            int existing = _versions?.Length ?? 0;
            Array.Resize(ref _versions, capacity);

            for (int i = existing; i < capacity; i++)
                _versions[i] = 1;
        }

        HandleToSoA = new NativeArray<int>(capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        HandleVersions = new NativeArray<int>(capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

        for (int i = 0; i < capacity; i++)
            HandleToSoA[i] = AmbiSlot.NONE;

        NativeArray<int>.Copy(_versions, HandleVersions, capacity);

        if (_freeIdentities == null || _freeIdentities.Length != capacity)
            _freeIdentities = new int[capacity];

        _freeIdentityCount = 0;
        _nextIdentity = 0;
    }

    static void DisposeIdentitiesIfUnused()
    {
        if (_liveIdentities != 0 || Rows != null)
            return;

        HandleToSoA.DisposeIfAllocated();
        HandleVersions.DisposeIfAllocated();
    }

    // Mutation-tolerant, swap-back-aware enumerator.
    // Uses reverse traversal and per-object visit stamps to prevent
    // duplicate visits when registrations change during iteration.
    // 
    // Essentially: The objects registered right now, for main-thread code.
    // Any request is safe while walking them: each object registered
    // throughout is yielded exactly once, one that leaves first is never yielded, and one that arrives meanwhile may be.
    //
    // Intended primarily for culling passes inside IAmbiSystem.BeforeFlush
    // but can be used elsewhere as long as AmbiScheduler.Complete() is invoked prior.
    internal readonly struct Registrations
    {
        public Enumerator GetEnumerator() => new(++_visits);

        public struct Enumerator
        {
            readonly int _visit;
            int _index;
            T _current;

            internal Enumerator(int visit)
            {
                _visit = visit;
                _index = RegisteredCount;
                _current = null;
            }

            public readonly T Current => _current;

            // Backwards: an object that leaves is replaced by the last one, which is already behind us. Only when the one leaving is
            // still ahead does the last one move ahead again, and its stamp skips it.
            public bool MoveNext()
            {
                for (_index = Math.Min(_index, RegisteredCount) - 1; _index >= 0; _index--)
                {
                    T instance = _registered[_index];
                    ref AmbiSlot slot = ref instance.Slot;

                    if (slot.Visit == _visit)
                        continue;

                    slot.Visit = _visit;
                    _current = instance;
                    return true;
                }

                return false;
            }
        }
    }
}
