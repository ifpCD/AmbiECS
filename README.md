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
- It produces no garbage. Everything is sized up front, so registering, updating and removing objects allocate nothing.
- Main thread introspection into the DOD state is available through graph force completion, specifically for visualization and non-time guaranteed callbacks like `OnDisable`.

## Classes

- `AmbiScheduler` runs the frame: it finishes last frame's jobs, applies changes and schedules systems.
- `AmbiMonoBehaviour<T>` and `AmbiBase<T>` are the base classes for components and for plain C# objects that own a row.
- `AmbiRegistry<T>` keeps each type's arrays tightly packed as objects come and go.
- `AmbiHandle` and `AmbiLookup` provide stable references between objects that jobs can resolve.
- `AmbiTransform` gives shared, parallel access to transforms.
- `IAmbiSystem` is the interface to implement in order to schedule jobs each frame, and use the pre-flushing scheduler callback.

## Roadmap

Currently, all OOP to DOD synchronization glue has to be handwritten. As Unity rolls out C# 14, all glue will be source generated.  
Inter-dependency chaining is not fully figured out yet.  
Potentially a Job Graph Source Generator via Visual Scripting.