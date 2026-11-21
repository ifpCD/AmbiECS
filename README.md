# AmbiECS

AmbiECS is a hybrid ECS for Unity 7 C# 14.

Unity makes you choose between two approaches. MonoBehaviours are easy to build with but don't scale. Entities scale, but you have to rebuild your project around them.  
An alternative is to use a hybrid ECS pattern. Components stay as ordinary MonoBehaviours, and on runtime, each component can register itself into a row in densely-packed backing arrays dedicated for its type, which Burst jobs can process off the main thread.  
Components are simply redesigned one type at a time. There's no Entities package, no baking step and no second world to keep in sync.

## How it works

Game code works with components as usual. Registering and unregistering take effect immediately for main-thread code, and any change is flagged for the job side.  
Once per frame, at the end of `Update`, the scheduler copies everything that changed into the arrays in a single pass. Each object is copied once, however many times it changed.  
The jobs run alongside the rest of the frame, through `LateUpdate` and rendering, and are force completed by the start of the next frame. The main thread doesn't have to wait for them.

## What sets it apart

- There is one sync point per frame. Jobs never see half-applied changes, and adding or removing objects never stalls the main thread.
- References survive into jobs. Components point at each other using generational handles that jobs resolve via indirection, and a destroyed target simply stops resolving.
- Transform has an abstraction called AmbiTransform, which is a main thread, shared, and referenced-counted pointer to TransformAccess targeted to reduce interop, and allow for transform tracking off main thread.
- It produces no garbage in steady state. Everything is sized from each type's capacity, so registering, updating and removing objects allocate nothing until a type outgrows it, and growing never waits on the job graph.
- Main thread introspection into the DOD state is available through graph force completion, specifically for visualization and non-time guaranteed callbacks like `OnDisable`.

## Classes

- `[Ambi]` makes a `partial` class, component or plain object, an Ambi type. A source generator implements `IAmbiRegistrant<T>` for it from its column and glue attributes.
- `Ambi<T>` keeps each type's rows tightly packed as objects come and go, and hands out their handles.
- `AmbiScheduler` runs the frame: it finishes last frame's jobs, applies changes and schedules systems.
- `AmbiHandle` and `AmbiLookup` provide stable references between objects that jobs can resolve.
- `AmbiTransform` gives shared, parallel access to transforms.
- `IAmbiSystem` is the interface to implement in order to schedule jobs each frame, and use the pre-flushing scheduler callback.

## Roadmap

Inter-dependency chaining is not fully figured out yet.  
Potentially a Job Graph Source Generator via Visual Scripting.