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

// A travel field for that size of body: how far every cell is from a goal, going round what is in the way.
var toGoal = new[] { TravelSeed.FromPoint(Solid.Sphere(goal.x, goal.y, goal.z, 0.8f), goal.x, goal.y, goal.z) };
WallGridField field = grid.ScheduleField(body, toGoal);
field.TrySample(point, out float3 way, out float travel);                 // the way down the field at a point, and how far is left
JobHandle sampled = field.ScheduleSample(points, ways, travels);          // the same for many points at once, in a job

// The way for one body of that size from a point to a goal: a few straight legs.
var route = new WallGridRoute();                        // keep it: it holds the search's arrays, and serves any grid
route.Schedule(grid, body, from, to);                   // searched in a job
if (route.Status == RouteStatus.Found) {                // reading the answer waits for the job
    NativeArray<float3>.ReadOnly legs = route.Points;   // go to each in turn; the last is the goal
}
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
- **A field is for followers with no plan of their own.** Build one per goal, and sample it for every follower each frame; `ScheduleSample` does that in a job, and can run after a job of yours that fills the points. A refused sample gives a travel of -1 there.
- **A field starts from seeds:** the cells inside a solid, at their distance from a point (a goal), or at their distance in front of a plane, which makes the field lead out through an opening. A cell is started only if the straight way from it to the point, or square to the plane, is open, so a seed can be generous: it never starts a cell behind a wall. A seed that starts no cell gives a field that is built and reaches nothing: read `Reached`, and try a larger seed.
- **A follower has arrived when its travel falls below about a cell.** The lowest cell can be most of a cell from the goal, and the direction keeps turning there.
- **A field is of the regions as they were when it was built.** Once they are forgotten or flooded again it stops answering: `IsBuilt` is false and every sample is refused. Build a new one then. It is built in a job after the flood, with scratch of 12 bytes a cell that is freed when the build is done. Builds do not wait for one another, so the fields of a grid can all be built side by side.
- **A field says nothing of what lies between cells.** Whatever follows it needs its own way of not hitting things.
- **A field outlives its grid.** When memory is short, dispose the grid and keep its fields: a field that was good goes on answering as it was built. **It then cannot know that the way has changed.** If a door shuts or a cost changes there, it does not refuse: dispose it yourself, and build a new one from a new grid.
- **You own a field.** Dispose it; it holds 2 bytes a cell. A job of yours that reads its `Distance` must be handed to the field's `AddReader`, so that `Dispose` waits for it.
- **A route is for one body with somewhere to go.** Its points are straight legs that keep the body's full radius from every shape: proven from the wall distance where that can say, and from the shapes where it can't. Where no straight leg is clear the route steps between open cells, which keeps the size class's `Guarantee`.
- **A route keeps to what its way paid.** The way is searched by the cells' costs and then pulled straight. A straight leg is taken only if it is no worse than the part of the way it replaces: it passes through no cell that costs more than the costliest cell of that part, and it costs no more as flown, which is its length in each cell times 1 + cost / 16. So pulling straight never makes a route cost more, and a route goes round an area filled with a higher cost where its way did. The straight leg from start to goal is taken with nothing searched where it is sure to be the cheapest: where no cell under it has a cost, or where it costs no more than the same line would at the least cost among the grid's open cells. So in a grid where every open cell has the one cost, routes cost what they would with no costs at all; one corner left at a lower cost, and a straight leg waits for a search.
- **A route has few legs; it is not always the shortest.** From each point, the next is the farthest cell of the searched way that a straight leg reaches. Now and then a nearer turning point would have made the whole route a little shorter.
- **Where a whole grid costs more, fill the cost over all of it.** A search is told the least cost among the cells the flood reached, and saves work only where that is above 0: then a clear straight leg is taken with nothing searched, and a search takes up about as many cells as with no costs. So fill a solid that covers the whole grid, with any cells it has beyond the room's walls and doorways. One reachable cell left at the normal cost, and every route with a costed cell under its straight line waits for a search, and a slow one.
- **A route's way may cost a tenth more than the cheapest,** and is found with about a tenth of the work for it. `WallGridRoute.Leeway` is that share: 0 gives the cheapest way, and more gives quicker searches. In a grid where much of the room costs more than the rest a small leeway saves little. One as large as that cost is, 2 for a cost of 32, can make such a search far quicker, and what it gives up depends on the room: in the rooms measured the ways came out from hardly dearer to 15 in 100 dearer on the whole, and up to twice as dear at the worst, and in some the search still took up thousands of cells.
- **Read a route's `Status` first.** `Unreachable`: there is no way for a body of that size. `Refused`: the size class has no regions (flood first), or a point is not a number. `BufferTooShort`: it has more points than the route was made to hold, and `Count` says how many.
- **`Ends` says what a route does not promise.** `FirstLegUnproven`: the start is nearer a shape than the radius, and the first point is the centre of the nearest open cell it can see. `StopsShortOfGoal`: the goal is, and the route ends at an open cell's centre beside it.
- **A route is of the grid as it was when it was searched.** After `FillCost`, `ClearCost`, `SetShapes` or another flood, `IsCurrent` is false. The points are kept: stop following them, and search again. `IsCurrent` is about the grid alone, so read `Status` as well.
- **Closed cells keep a leg half a cell off, not a radius.** To keep a body off something shut by cost alone, such as a door, fill `CellCost.Closed` wider than it by the body's radius.
- **Keep one `WallGridRoute` for each search that runs at once, not one for each agent.** It holds 16 bytes for each cell of the largest grid it has searched, and serves grids of any size. Copy the points out when a search is done, and a few route objects serve any number of agents. A search scheduled while its last is still running runs after it and replaces its answer.
- **Reading a route's `Status`, `Points` and the rest waits for the search.** To not wait, look at `Handle.IsCompleted` first.
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

The jobs that flood regions, build a travel field and sample one lift none.

## Tests

Run them in the Unity editor, with the package in a project's `testables`:

```
Unity -batchmode -projectPath <project> -runTests -testPlatform EditMode -testResults <file>.xml
```

Burst compiles the jobs first, and leak detection is on. Tested with Unity 6000.3, Burst 1.8.29, Collections 2.6.5 and Mathematics 1.3.3 (the versions `package.json` asks for); nothing older has been tried. The source is C# 9, the version Unity compiles.
