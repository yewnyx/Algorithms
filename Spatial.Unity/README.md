# Spatial for Unity

> Experimental: a work in progress that may change, or be removed if it doesn't work out.

Queries over many boxes from Unity jobs: which items are within a radius of a point, and which are nearest. For callers that query many boxes every frame from jobs, where most boxes rest and some move.

- `Core/`: the Spatial library, with no Unity reference. The same files build as the `Spatial` library for .NET in this repository.
- `Runtime/`: a `BoxSet` that owns the native arrays, and Burst jobs that update it and answer queries. Also a `WallGrid` for geometry that does not move, and `ColliderShapes` to fill it from colliders.

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

## Room around geometry that does not move

For callers that ask how much room there is at a point, many times a frame, of colliders that stay put.

```csharp
// Colliders into shapes, in the frame of a root transform. In the editor this can read every mesh.
ColliderShapes.TryWrite(colliders, root.worldToLocalMatrix, out byte[] block, out CollectReport report);

// A grid of half-metre cells in that frame. It copies the shapes, so the block can go.
var grid = new WallGrid(new GridShape(0, 0, 0, 0.5f, 80, 80, 60));
grid.SetShapes(block);
JobHandle marked = grid.ScheduleMark();   // each cell's distance to the nearest shape, one z layer per index

grid.TryRoom(point, out float room);                    // proven from eight cells, never more than the true distance
grid.TryDistance(point, limit: 2f, out float exact);    // exact, from the shapes
grid.FillCost(Solid.Box(new Box(0, 0, 0, 2, 2, 4)), 32);   // cells whose centre is inside cost three times as much

// Where a body of radius 0.6 can be, flooded from seeds such as the openings into a room.
int body = grid.AddSizeClass(0.6f);
grid.ScheduleRegions(body, seeds);
grid.IsOpen(body, point);                                    // can a body of that size be here?
grid.TryNearestOpen(body, point, reach: 2f, out float3 open);        // the nearest place it can be
grid.TryNearestOpen(body, point, reach: 2f, like: from, out open);   // ... that can be reached from another point
```

- **One frame, no transform.** Points and shapes are in the grid's own frame. Where that frame sits in the world is the caller's.
- **A built player cannot read a mesh** unless it was imported with Read/Write on. Write the block in the editor and store it; `SetShapes` takes it back. `SetShapes` also takes solids and triangles directly.
- **Shapes are the physics engine's.** Under a turned child of an unevenly scaled parent the engine does not hold a sheared box, sphere or capsule: it takes the transform's rotation and lossy scale, and so does `ColliderShapes`. A mesh goes through the matrix, as the engine's does.
- **Nothing is left out in silence.** A collider whose shape cannot be held goes in as a box around it: a convex mesh, a mesh that cannot be read, any other kind. `CollectReport` counts them. It also counts colliders that gave nothing (`Missing`): a mesh collider with no mesh, or an inactive collider of another kind. Shapes collected with any missing are not to be trusted.
- **It never asks the physics engine,** so it sees only the colliders it is given.
- **The same colliders give the same block,** on one kind of machine. `Shapes.Version` changes when the layout does; a block of another version is refused.
- **The grid never claims room it has not worked out.** `TryRoom` gives 0 before the first `ScheduleMark`, and again from `SetShapes` until the next `ScheduleMark` is done; `IsMarked` is false for as long. A layer whose marking is refused, because the shapes are damaged, is left at 0.
- **`TryRoom` and `TryDistance` say how far the nearest surface is,** not whether a point is in open space. A triangle has no inside.
- **Costs take a byte a cell only once one is written.** Until `FillCost` writes a cost that isn't 0, and again after `ClearCost`, the grid holds none and `Cost` is empty.
- **Read `Cost` again after `FillCost` or `ClearCost`.** They can put another array in its place. One kept from before is then freed, or is the empty one, which reads every cell as normal, closed ones included.
- **Regions follow the wall distance and the closed cells.** A size class has none until `ScheduleRegions` has run, and none again after `SetShapes`, `FillCost` or `ClearCost`: flood again after those. `HasRegions` says.
- **Mark, then flood.** A flood is refused unless the wall distance was marked from the shapes held, so regions are never newer than the marking they came from.
- **`IsOpen` is about the point's cell.** A body at that cell's centre fits; the point itself can be most of a cell from there, so nearer a wall than the radius. To ask whether a body fits at a point, compare `TryRoom` or `TryDistance` with its radius.
- **`TryNearestOpen` is by straight line,** whatever lies between: the cell can be on the other side of a thin wall or under a floor, with either form.
- **A seed with no open cell inside reaches nothing.** `EmptySeeds` counts them; make the seed larger and flood again.
- **`AddSizeClass` returns -1** for a radius of half a cell or less, or one that needs more than the grid's cap can say.
- **Main thread only.** `TryRoom`, `IsMarked` and `Wall` wait for the marking jobs first, and what reads a size class's regions waits for its flood. They do not wait for a job of yours.
- **A job of yours that reads `Wall` or `Cost`** must be handed to `AddReader`. The next marking then runs after it, and `SetShapes`, cost changes and `Dispose` wait for it. Otherwise those write or free the arrays under your job.

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

Unity's checks are turned off in these places, each for the reason shown. The job that updates a `BoxSet` lifts none.

| Job | Array | Why it is safe |
|---|---|---|
| `InSphereJob`, `NearestJob` | the results | Query q writes only `results[q * stride .. (q + 1) * stride)`, so no two queries write the same place |
| `NearestJob` | the squared distances | The same slices as the results |
| `MarkJob` | the wall distance | Layer z writes only `wall[z * layer .. (z + 1) * layer)`, so no two layers write the same place |

The job that floods regions lifts none.

## Tests

Run them in the Unity editor, with the package in a project's `testables`:

```
Unity -batchmode -projectPath <project> -runTests -testPlatform EditMode -testResults <file>.xml
```

Burst compiles the jobs first, and leak detection is on. Tested with Unity 6000.3, Burst 1.8.29, Collections 2.6.5 and Mathematics 1.3.3 (the versions `package.json` asks for); nothing older has been tried. The source is C# 9, the version Unity compiles.
