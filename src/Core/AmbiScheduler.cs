using System;
using Unity.Jobs;
using Unity.Profiling;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.LowLevel;

public interface IAmbiSystem
{
    void BeforeFlush();

    JobHandle Schedule();
}

// Synchronization and scheduling engine
// Main Thread OOP Update Loop deals with OOP classes for authoring/control.
// Every time a registered OOP property changes (or the instance is manually marked as dirty) - we flag it.
// At the end of the main thread update loop - we do all of the OOP->DOD work (removals, additions, synchronization)
//
// For scheduling, you implement IAmbiSystem and subscribe it to the AmbiScheduler
//
// Sometimes (for culling for example) - you will want to check what instances are about to go into the scheduled system, and see if some could be culled
// For that, you implement BeforeFlush, and iterate over IAmbiRegistrant<T>.Registered (unlike DOD, it's not deferred) and deregister.
//
// Given certain Unity's editor callbacks not having a guaranteed point in the execution model (OnDrawGizmos for example):
// One issue of deferred sync approach is that any kind of introspection into the ECS DOD state from an arbitrary thread at a random time will require us to pre-emptively complete the entire graph
//
// EarlyUpdate Force Finish Last Scheduled Graph -> Main Thread Update Loop -> Every IAmbiSystem.BeforeFlush() -> AmbiScheduler.Flush() -> Every IAmbiSystem.Schedule();
//
// TODO: figure out a better inter-system JobHandle chaining pattern.
[NoAutoStaticsCleanup]
public static partial class AmbiScheduler
{
    // player-loop system names, as the profiler shows them
    struct AmbiComplete { }

    struct AmbiFrame { }

    static readonly ProfilerMarker BeforeFlushMarker = new("Ambi.BeforeFlush");
    static readonly ProfilerMarker FlushMarker = new("Ambi.Flush");
    static readonly ProfilerMarker ScheduleMarker = new("Ambi.Schedule");

    static JobHandle _inFlight;
    static bool _busy;
    static bool _deferring;

    static readonly IAmbiSystem[] _systems = new IAmbiSystem[8];
    static int _systemCount;

    // one triple per Ambi type, enlisted the first time the type is touched
    static readonly Action[] _departures = new Action[32];
    static readonly Action[] _arrivals = new Action[32];
    static readonly Action[] _disposals = new Action[32];
    static int _registryCount;

    internal static bool Defers => _deferring;

    internal static bool IsIdle => !_busy;

    // Systems schedule in the order they subscribed.
    public static void Subscribe(IAmbiSystem system)
    {
        if (Array.IndexOf(_systems, system, 0, _systemCount) >= 0)
            return;

        if (_systemCount == _systems.Length)
            throw new InvalidOperationException($"More than {_systems.Length} Ambi systems subscribed.");

        _systems[_systemCount++] = system;
    }

    public static void Unsubscribe(IAmbiSystem system)
    {
        int index = Array.IndexOf(_systems, system, 0, _systemCount);

        if (index < 0)
            return;

        Array.Copy(_systems, index + 1, _systems, index, --_systemCount - index);
        _systems[_systemCount] = null;
    }

    // Force finish the current graph (OnDrawGizmos off-mainthread callbacks for example)
    public static void Complete()
    {
        if (!_busy)
            return;

        _inFlight.Complete();
        _inFlight = default;
        _busy = false;
    }

    internal static void Enlist(Action departures, Action arrivals, Action disposal)
    {
        _departures[_registryCount] = departures;
        _arrivals[_registryCount] = arrivals;
        _disposals[_registryCount++] = disposal;
    }

    // A system that throws is logged and skipped, like a MonoBehaviour: one failure never stops the flush or the other systems.
    static void Frame()
    {
        if (!_deferring)
            return; // edit mode, or quitting

        // normally a no-op: AmbiComplete already completed last frame's graph. Kept for player loops that drop it.
        Complete();

        using (BeforeFlushMarker.Auto())
        {
            for (int i = 0; i < _systemCount; i++)
            {
                IAmbiSystem system = _systems[i];

                try
                {
                    system.BeforeFlush();
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, system as UnityEngine.Object);
                }
            }
        }

        using (FlushMarker.Auto())
            Flush();

        JobHandle graph = default;

        using (ScheduleMarker.Auto())
        {
            for (int i = 0; i < _systemCount; i++)
            {
                IAmbiSystem system = _systems[i];

                try
                {
                    graph = JobHandle.CombineDependencies(graph, system.Schedule());
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, system as UnityEngine.Object);
                }
            }
        }

        _inFlight = graph;
        _busy = true;

        // scheduled jobs sit in this thread's batch until flushed: start the graph now, not at the next engine flush
        JobHandle.ScheduleBatchedJobs();
    }

    // Every type's departures land before any type's arrivals: the rows never outnumber what Register counted, and a Sync reading a
    // Handle never finds the identity slots held by objects destroyed in the same frame. A Load or Sync issues no requests, so one
    // pass lands everything.
    static void Flush()
    {
        for (int i = 0; i < _registryCount; i++)
            _departures[i]();

        for (int i = 0; i < _registryCount; i++)
            _arrivals[i]();
    }

    // A session runs from entering Play mode, to exiting it, and a code reload in Play mode ends one session and starts the next.
    [OnEnteringPlayMode]
    static void BeginPlaying()
    {
        _deferring = true;

        PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();

        bool completes = Insert(ref root, typeof(UnityEngine.PlayerLoop.EarlyUpdate), null, typeof(AmbiComplete), Complete);

        bool frames = Insert(
            ref root,
            typeof(UnityEngine.PlayerLoop.Update),
            typeof(UnityEngine.PlayerLoop.Update.ScriptRunDelayedTasks),
            typeof(AmbiFrame),
            Frame
        );

        if (completes || frames)
            PlayerLoop.SetPlayerLoop(root);
    }

    // Lands what waits, so that teardown after it applies at once and cleans up after itself, and drops the systems: each session subscribes its own.
    [OnExitingPlayMode]
    static void EndPlaying()
    {
        Complete();
        Flush();
        _deferring = false;

        Array.Clear(_systems, 0, _systemCount);
        _systemCount = 0;
    }

    [OnCodeUnloading]
    static void Unload()
    {
        Complete();

        for (int i = 0; i < _registryCount; i++)
            _disposals[i]();
    }

    // Puts a system into phase right after `after`: first when `after` is null, last when the phase lacks it. False if already there.
    static bool Insert(ref PlayerLoopSystem root, Type phase, Type after, Type type, PlayerLoopSystem.UpdateFunction update)
    {
        for (int i = 0; i < root.subSystemList.Length; i++)
        {
            if (root.subSystemList[i].type != phase)
                continue;

            PlayerLoopSystem[] systems = root.subSystemList[i].subSystemList;
            int at = after == null ? 0 : systems.Length;

            for (int j = 0; j < systems.Length; j++)
            {
                if (systems[j].type == type)
                    return false;

                if (systems[j].type == after)
                    at = j + 1;
            }

            var inserted = new PlayerLoopSystem[systems.Length + 1];
            Array.Copy(systems, 0, inserted, 0, at);
            inserted[at] = new PlayerLoopSystem { type = type, updateDelegate = update };
            Array.Copy(systems, at, inserted, at + 1, systems.Length - at);

            root.subSystemList[i].subSystemList = inserted;
            return true;
        }

        return false;
    }
}
