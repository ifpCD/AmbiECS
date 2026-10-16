internal struct AmbiSlot
{
    internal const int NONE = -1;

    internal const int DIRTY = 1; // its row must be synced from the object
    internal const int RELOAD = 2; // registered again before its old row was removed: the row starts over
    internal const int QUEUED = 4; // waiting for the next flush
    internal const int DESTROYED = 8; // gone for good: its identity retires when it lands

    internal int Registered; // place among the registered objects, NONE while unregistered: the one truth behind IsRegistered
    internal int SoAIndex; // row, NONE while it has none
    internal int Identity; // identity slot, NONE until its Handle is first read
    internal int Visit; // the last walk over the registered objects that yielded it
    internal int Flags;

    internal static AmbiSlot Unassigned =>
        new()
        {
            Registered = NONE,
            SoAIndex = NONE,
            Identity = NONE,
        };
}
