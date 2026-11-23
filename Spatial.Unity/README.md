# Spatial for Unity

> Experimental: a work in progress that may change, or be removed if it doesn't work out.

Queries over many boxes from Unity jobs: which items are within a radius of a point, and which are nearest. For callers that query many boxes every frame from jobs, where most boxes rest and some move.

- `Core/`: the Spatial library, with no Unity reference. The same files build as the `Spatial` library for .NET in this repository.
- `Runtime/`: a `BoxSet` that owns the native arrays, and Burst jobs that update it and answer queries.

## Use

```csharp
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Yewnyx.Spatial;

// Up to 1,000 items somewhere in a 200 m cube.
var set = new BoxSet(1000, new Box(-100, -100, -100, 100, 100, 100));

// An item is a box in its own frame (a centre and half-extents) at a pose. The id is yours.
set.Add(id: 7, centre: float3.zero, halfExtents: new float3(0.5f), position, rotation);

// Each frame: say which items moved, then schedule. Only those are worked out again.
set.Move(7, newPosition, newRotation);
JobHandle upToDate = set.Schedule();

// Queries run as jobs after it. One sphere each: x, y, z and the radius in w.
var spheres = new NativeArray<float4>(1, Allocator.TempJob);
var results = new NativeArray<int>(16, Allocator.TempJob);      // 16 ids per query
var counts = new NativeArray<int>(1, Allocator.TempJob);
var totals = new NativeArray<int>(1, Allocator.TempJob);
spheres[0] = new float4(point, 4f);
JobHandle done = set.ScheduleInSphere(spheres, results, counts, totals, stride: 16, tree: true);

// After done: results[0 .. counts[0]) are the ids found.
done.Complete();
```

`ScheduleNearest` has the same shape and also writes squared distances, nearest first.

## Many moving items

`Move` suits the odd item. When many move every frame, pass their poses in arrays instead, so the calling thread does no work per item:

```csharp
// Instead of Move and Schedule() above. movedIds[i] is at positions[i], rotations[i]; a job of yours may still be filling them.
JobHandle posesReady = new MyPosesJob { Positions = positions, Rotations = rotations }.Schedule(count, 64);
JobHandle upToDate = set.Schedule(movedIds, positions, rotations, posesReady);
```

The set's job reads the arrays after `posesReady`, so your job, the set and the queries run in order with nothing waited for on the calling thread. Ids not in the set are skipped. Keep the arrays until the returned handle completes.

With 1,000 items moving every frame in a Windows player on one desktop, the calling thread spent about 10 us in `Move` calls and under 1 us passing arrays.

## Seeing it

- `BoxSetGizmo` draws a set in the scene view (`gizmo.Set = set`): each item's box, blue if it rested and red if the last `Schedule` worked it out again, and the tree's nodes, fainter the deeper they are. It waits for the set's jobs, so use it for looking, not in a build that must be quick.
- The sample "Moving boxes" (Package Manager, Samples): 300 boxes, a tenth drifting, and a query that follows an object you drag. Boxes it finds turn green. A frame is three steps in jobs: move the drifting boxes, update the set, query.

## What to know

- **Changes wait for `Schedule`.** `Add`, `Remove` and `Move` check the id and note the change. A job applies the changes in order, then reads any poses passed in arrays.
- **Results are your ids**, not positions in the set's arrays.
- **Resting items cost nothing.** Only items added or moved since the last `Schedule` are worked out again. With no change, `Schedule` starts no job. While anything changes, the tree is rebuilt whole.
- **Scan or tree.** `tree: false` tests every box. `tree: true` walks the tree. Both give the same items: the scan in set order, the tree in tree order. `Nearest` gives the same order either way.
- **Job order.** `Schedule` runs after the queries scheduled so far, which see the set as it was. Queries scheduled between two `Schedule`s only read the set, so they run side by side. `Schedule` itself waits only for the previous `Schedule`'s update, normally long done.
- **A full set refuses.** The capacity is fixed when the set is made. `Add` returns false if the set is full or the id is in use. `Remove` and `Move` return false for an id that is not there.
- **A query's count is -1 if it was refused.** Then read nothing from its results.
- **Main thread only.** The jobs it schedules run anywhere; the set's own methods do not.
- **After `Dispose`**, members that read or change the set throw `ObjectDisposedException`. `Complete`, `Capacity`, `Count`, `Extent`, `Rebuilds` and `IsDisposed` stay safe.

## Safety restrictions lifted in the jobs

Unity's checks are turned off in these places, each for the reason shown. The job that updates the set lifts none.

| Job | Array | Why it is safe |
|---|---|---|
| `InSphereJob`, `NearestJob` | the results | Query q writes only `results[q * stride .. (q + 1) * stride)`, so no two queries write the same place |
| `NearestJob` | the squared distances | The same slices as the results |

## Tests

Run them in the Unity editor, with the package in a project's `testables`:

```
Unity -batchmode -projectPath <project> -runTests -testPlatform EditMode -testResults <file>.xml
```

Burst compiles the jobs first, and leak detection is on. Tested with Unity 6000.3, Burst 1.8.29, Collections 2.6.5 and Mathematics 1.3.3 (the versions `package.json` asks for); nothing older has been tried. The source is C# 9, the version Unity compiles.
