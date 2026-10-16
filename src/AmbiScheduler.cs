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

[NoAutoStaticsCleanup]
public static class AmbiScheduler
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

    static readonly Action[] _departures = new Action[32];
    static readonly Action[] _arrivals = new Action[32];
    static int _registryCount;

    internal static bool Defers => _deferring;

    internal static bool IsIdle => !_busy;

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

    public static void Complete()
    {
        if (!_busy)
            return;

        _inFlight.Complete();
        _inFlight = default;
        _busy = false;
    }

    internal static void Enlist(Action departures, Action arrivals)
    {
        _departures[_registryCount] = departures;
        _arrivals[_registryCount++] = arrivals;
    }

    static void Frame()
    {
        if (!_deferring)
            return; // edit mode, or quitting

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

        JobHandle.ScheduleBatchedJobs();
    }

    static void Flush()
    {
        for (int i = 0; i < _registryCount; i++)
            _departures[i]();

        for (int i = 0; i < _registryCount; i++)
            _arrivals[i]();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void BeginPlaying()
    {
        Array.Clear(_systems, 0, _systemCount);
        _systemCount = 0;
        _deferring = true;

        Application.quitting -= EndPlaying;
        Application.quitting += EndPlaying;

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

    static void EndPlaying()
    {
        Complete();
        Flush();
        _deferring = false;
    }

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
