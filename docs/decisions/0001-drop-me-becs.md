# 0001: Plain Unity Jobs + Burst instead of ME.BECS

- **Status:** accepted
- **Date:** 2026-11-08

## Context

We wanted data-oriented, multithreaded simulation that runs in the Web build. We trialled
[ME.BECS](https://github.com/chromealex/ME.BECS) (2026.10.22 rc1, commit `84e4a4f`), a Burst-based ECS.

What happened on Unity 6000.6.8f1:

1. **Unity 6.6 incompatibility.** ME.BECS targets 6000.0. Unity 6.6 turns `Object.GetInstanceID()`,
   `EntityId ↔ int` casts, `RenderParams.instanceID`, `InstanceIDToObject`, `EndNameEditAction` and more
   into compile errors. 24 call sites had to be patched; it also needed undeclared Addressables and
   Newtonsoft packages, and shipped without its QuickStart images.
2. **Code-generation dependency.** Component, system and job registration comes from an IL-analysing
   editor code generator that writes ~5 MB of "bridge" assemblies into `Assets/`. Its runtime pass only
   discovers systems placed in a SystemsGraph asset (or explicitly included types). A world built in code
   isn't fully registered.
3. **Editor crash.** After fixing type IDs, scheduling the first query job crashed the editor natively
   (`ScheduleManagedJob`): the job's runtime registration was never generated.

## Decision

Don't use ME.BECS. Use Unity's own **C# Job System + Burst** directly, with a thin in-house layer on top
only where the game needs it.

## Consequences

- No third-party ECS to patch for each Unity upgrade; no generated assemblies in the repo.
- Verified: a plain Burst `IJobParallelFor` runs multithreaded in Chrome and Safari Web builds
  (`Spikes/WebThreadsTest`, 5 job workers).
- We write our own small abstractions (struct components, systems that schedule jobs, explicit
  `AsParallel`-style opt-in) as gameplay needs them, not up front.
- A patched copy of ME.BECS plus findings is kept **outside this repo** (local `~/Documents/GitHub/MEBECS`)
  as a reference for ideas.
