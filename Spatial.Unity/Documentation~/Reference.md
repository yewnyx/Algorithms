# Spatial for Unity: reference

Every rule and limit, grouped by the type it belongs to. The [README](../README.md) has the short tour.

- [BoxSet](#boxset)
- [ColliderShapes](#collidershapes)
- [WallGrid: shapes, room and costs](#wallgrid-shapes-room-and-costs)
- [WallGrid: body sizes and regions](#wallgrid-body-sizes-and-regions)
- [WallGridField](#wallgridfield)
- [WallGridRoute](#wallgridroute)
- [Jobs, threads and safety checks](#jobs-threads-and-safety-checks)
- [Tests](#tests)

## BoxSet

### Moving many items

`Move` suits the odd item. When many move every frame, pass their poses in arrays instead, so the calling thread does no work per item:

```csharp
// Instead of Move and Schedule(). movedIds[i] is at positions[i], rotations[i]; a job of yours may still be filling them.
JobHandle posesReady = new MyPosesJob { Positions = positions, Rotations = rotations }.Schedule(count, 64);
JobHandle upToDate = set.Schedule(movedIds, positions, rotations, posesReady);
```

- The set's job reads the arrays after `posesReady`, so your job, the set and the queries run in order with nothing waited for on the calling thread.
- Ids not in the set are skipped.
- Keep the arrays until the returned handle completes.
- With 1,000 items moving every frame in a Windows player on one desktop, the calling thread spent about 10 us in `Move` calls and under 1 us passing arrays.

### Rules

- **Changes wait for `Schedule`.** `Add`, `Remove` and `Move` check the id and note the change. A job applies the changes in order, then reads any poses passed in arrays.
- **Results are your ids**, not positions in the set's arrays.
- **Resting items cost nothing.** Only items added or moved since the last `Schedule` are worked out again.
- **No change, no job.** With no change, `Schedule` starts no job.
- **The tree is rebuilt whole** while anything changes.
- **Scan or tree.** `tree: false` tests every box. `tree: true` walks the tree.
- **Same items, different order.** The scan gives them in set order and the tree in tree order. `Nearest` gives the same order either way.
- **Job order.** `Schedule` runs after the queries scheduled so far, which see the set as it was.
- **Queries run side by side.** Queries scheduled between two `Schedule`s only read the set.
- **`Schedule` waits** only for the previous `Schedule`'s update, normally long done.
- **A full set refuses.** The capacity is fixed when the set is made. `Add` returns false if the set is full or the id is in use.
- **`Remove` and `Move`** return false for an id that is not there.
- **A query's count is -1 if it was refused.** Then read nothing from its results.
- **Main thread only.** The jobs it schedules run anywhere; the set's own methods do not.
- **After `Dispose`**, members that read or change the set throw `ObjectDisposedException`. `Complete`, `Capacity`, `Count`, `Extent`, `Rebuilds` and `IsDisposed` stay safe.

### Seeing it

- `BoxSetGizmo` draws a set in the scene view (`gizmo.Set = set`): each item's box, blue if it rested and red if the last `Schedule` worked it out again, and the tree's nodes, fainter the deeper they are.
- It waits for the set's jobs, so use it for looking, not in a build that must be quick.
- The sample "Moving boxes" (Package Manager, Samples) has 300 boxes, a tenth drifting, and a query that follows an object you drag. Boxes it finds turn green.
- A frame of the sample is three steps in jobs: move the drifting boxes, update the set, query.

## ColliderShapes

- **A built player cannot read a mesh** unless it was imported with Read/Write on. Write the block in the editor and store it; `SetShapes` takes it back. `SetShapes` also takes solids and triangles directly.
- **In the editor it can read every mesh.**
- **Shapes are the physics engine's.** Under a turned child of an unevenly scaled parent the engine does not hold a sheared box, sphere or capsule: it takes the transform's rotation and lossy scale, and so does `ColliderShapes`.
- **A mesh goes through the matrix,** as the engine's does.
- **Nothing is left out in silence.** A collider whose shape cannot be held goes in as a box around it: a convex mesh, a mesh that cannot be read, any other kind. `CollectReport` counts them.
- **`Missing` counts colliders that gave nothing:** a mesh collider with no mesh, or an inactive collider of another kind. Shapes collected with any missing are not to be trusted.
- **It never asks the physics engine,** so it sees only the colliders it is given.
- **A part of a larger level is collected within a box.** The calls that take one leave out the colliders that lie wholly outside it, and, of a mesh that reaches in, the triangles that do not. `CollectReport` counts both (`Outside`, `TrianglesOutside`).
- **A shape may not be stored more than `Shapes.Reach` (1,024 m) from the frame's origin,** and one that is refuses the whole block. Cut at the box, far scenery is not in the way.
- **`ColliderShapes.Within` gives the box for a grid:** the grid's own, grown by as far as marking looks from a cell and a cell more. A cell at the grid's edge then holds its true distance to what lies just outside.
- **Shapes collected that way know nothing beyond the box.** Marking, regions, fields and routes never ask beyond it.
- **`TryDistance` can ask beyond it.** With such shapes its answer is the true one only for a point inside the grid and a limit no more than the box was grown by: the cap times the unit, a unit and a cell (1.5 m with half-metre cells and the default unit and cap).
- **A terrain goes in as a heightfield** in the calls that take a box: solid from its surface down as far as the frame reaches, so the space under the ground never reads as room.
- **It is taken if any of that solid reaches into the box,** so a grid that lies wholly under a terrain reads as solid too.
- **Its surface is never below the terrain's,** and at most `ColliderShapes.TerrainBound` (0.25 m) above it.
- **Gentle ground keeps every 8th height.** A terrain of 513 by 513 heights took 22 to 50 KB in the ones measured.
- **It sits where the engine puts a terrain:** at its transform's position, along the world's axes, whatever the transform's turn and scale.
- **A terrain with holes goes in as a box around it** and is counted (`Holed`).
- **Trees painted on a terrain whose prefabs have colliders are not taken,** and are counted (`Trees`): with any of those, the shapes leave something out.
- **The calls that take no box take a terrain as a box around it.**
- **The same colliders give the same block,** on one kind of machine.
- **`Shapes.Version`** changes when the layout, the tree's order or the packing of heights does; a block of another version is refused.

## WallGrid: shapes, room and costs

- **One frame, no transform.** Points and shapes are in the grid's own frame. Where that frame sits in the world is the caller's.
- **The grid never claims room it has not worked out.** `TryRoom` gives 0 before the first `ScheduleMark`, and again from `SetShapes` until the next `ScheduleMark` is done. `IsMarked` is false for as long.
- **A layer whose marking is refused,** because the shapes are damaged, is left at 0.
- **`TryRoom` and `TryDistance` say how far the nearest surface is,** not whether a point is in open space. A triangle has no inside.
- **Costs take a byte a cell only once one is written.** Until `FillCost` writes a cost that isn't 0, and again after `ClearCost`, the grid holds none and `Cost` is empty.
- **Read `Cost` again after `FillCost` or `ClearCost`.** They can put another array in its place. One kept from before is then freed, or is the empty one, which reads every cell as normal, closed ones included.
- **Costs:** 0 is normal, `CellCost.Closed` (255) closes a cell, and anything else scales a step into the cell by 1 + cost / 16. A cost of 32 makes a step cost three times as much.
- **`FillCost` writes the cells whose centre is inside the solid.** A solid thinner than a cell can hold no centre and write none, so make it at least a cell thick.
- **`HasRoom(point, radius)` is the cheaper question in open air** on a grid that keeps a tree: it answers without reading a count.

### A large grid keeps a tree

- **Above `WallGrid.DenseMost` cells (4 million) a grid keeps its wall distance as a tree:** counts only near surfaces, open air as cubes of one bit. `IsTree` says which.
- **On a tree `TryRoom` and `HasRoom` prove less room near surfaces than counts would, never more:** the tree keeps each count to a fifteenth of the cap.
- **How much less:** at a cell's centre under a fifteenth of the cap (6.7 cm at the defaults); at a point between cells, which is proven from eight, more. Measured at points within 0.9 m of a surface on a large outdoor part: 3 cm on the whole, under 12 cm for 99 in 100, 20 cm at the worst.
- **`TryDistance` answers the same either way.**
- **Building the tree takes seconds** for a part of some hundreds of metres. Build it where the part is baked and store what `TryWriteTree` gives.
- **`SetTree` takes a stored tree back** in place of a marking. Give the grid its shapes first: a tree built from other shapes is refused.
- **Costs, size classes, regions, fields and routes need a count for every cell.** On a tree `AddSizeClass` gives -1 and `FillCost` false.

## WallGrid: body sizes and regions

- **Regions follow the wall distance and the closed cells.**
- **A size class has no regions until `ScheduleRegions` has run,** and none again after `SetShapes`, `FillCost` or `ClearCost`: flood again after those. `HasRegions` says.
- **Mark, then flood.** A flood is refused unless the wall distance was marked from the shapes held, so regions are never newer than the marking they came from.
- **`IsOpen` is about the point's cell.** A body at that cell's centre fits; the point itself can be most of a cell from there, so nearer a wall than the radius.
- **To ask whether a body fits at a point,** compare `TryRoom` or `TryDistance` with its radius.
- **`TryNearestOpen` is by straight line,** whatever lies between: the cell can be on the other side of a thin wall or under a floor, with either form.
- **`TryNearestOpen` with `like:`** looks only at cells that can be reached from that other point: the nearest place a body there could get to. It returns false if `like` is not itself open.
- **A seed with no open cell inside reaches nothing.** `EmptySeeds` counts them; make the seed larger and flood again.
- **`AddSizeClass` returns -1** for a radius of half a cell or less, or one that needs more than the grid's cap can say.

## WallGridField

- **A field is for followers with no plan of their own.** Build one per goal, and sample it for every follower each frame.
- **`ScheduleSample`** samples many points in a job, and can run after a job of yours that fills the points.
- **A refused sample gives a travel of -1** there.
- **A field starts from seeds:** the cells inside a solid, at their distance from a point (a goal), or at their distance in front of a plane, which makes the field lead out through an opening.
- **A seed can be generous.** A cell is started only if the straight way from it to the point, or square to the plane, is open, so a seed never starts a cell behind a wall.
- **A seed that starts no cell** gives a field that is built and reaches nothing: read `Reached`, and try a larger seed.
- **A follower has arrived when its travel falls below about a cell.** The lowest cell can be most of a cell from the goal, and the direction keeps turning there.
- **A field is of the regions as they were when it was built.** Once they are forgotten or flooded again it stops answering: `IsBuilt` is false and every sample is refused. Build a new one then.
- **It is built in a job after the flood,** with scratch of 12 bytes a cell that is freed when the build is done.
- **Builds do not wait for one another,** so the fields of a grid can all be built side by side.
- **A field says nothing of what lies between cells.** Whatever follows it needs its own way of not hitting things.
- **A field outlives its grid.** When memory is short, dispose the grid and keep its fields: a field that was good goes on answering as it was built.
- **It then cannot know that the way has changed.** If a door shuts or a cost changes there, it does not refuse: dispose it yourself, and build a new one from a new grid.
- **You own a field.** Dispose it; it holds 2 bytes a cell.
- **A job of yours that reads its `Distance`** must be handed to the field's `AddReader`, so that `Dispose` waits for it.

## WallGridRoute

### What a route is

- **A route is for one body with somewhere to go.**
- **Its points are straight legs** that keep the body's full radius from every shape: proven from the wall distance where that can say, and from the shapes where it can't.
- **Where no straight leg is clear** the route steps between open cells, which keeps the size class's `Guarantee`.
- **A route has few legs; it is not always the shortest.** From each point, the next is the farthest cell of the searched way that a straight leg reaches. Now and then a nearer turning point would have made the whole route a little shorter.
- **Closed cells keep a leg half a cell off, not a radius.** To keep a body off something shut by cost alone, such as a door, fill `CellCost.Closed` wider than it by the body's radius.

### Reading the answer

Read `Status` first.

| `Status` | Meaning |
|---|---|
| `Found` | The points lead from the start to the goal, or as near it as `Ends` says. |
| `Unreachable` | There is no way for a body of that size. |
| `Refused` | The size class has no regions (flood first), or a point is not a number. |
| `BufferTooShort` | The route has more points than it was made to hold. `Count` says how many. |

`Ends` says what a found route does not promise.

| `Ends` | Meaning |
|---|---|
| `FirstLegUnproven` | The start is nearer a shape than the radius. The first point is the centre of the nearest open cell it can see. |
| `StopsShortOfGoal` | The goal is. The route ends at an open cell's centre beside it. |

- **Reading `Status`, `Points` and the rest waits for the search.** To not wait, look at `Handle.IsCompleted` first.
- **A route is of the grid as it was when it was searched.** After `FillCost`, `ClearCost`, `SetShapes` or another flood, `IsCurrent` is false. The points are kept: stop following them, and search again.
- **`IsCurrent` is about the grid alone,** so read `Status` as well.

### Costs and the route

The way is searched by the cells' costs, then pulled straight. A straight leg replaces part of the way only if it is no worse than that part: it passes through no cell that costs more than the costliest cell of that part, and it costs no more as flown (its length in each cell times 1 + cost / 16). So pulling straight never makes a route cost more.

| When | The route |
|---|---|
| An area is filled with a higher cost | goes round it, where its way did |
| No cell under the straight line from start to goal has a cost | takes that line with nothing searched |
| The line costs no more than the same line would at the least cost among the grid's open cells | takes that line with nothing searched |
| Every open cell has the one cost | costs what it would with no costs at all |
| One corner is left at a lower cost | waits for a search, even for a straight leg |

- **Do: where a whole grid costs more, fill the cost over all of it.** Fill a solid that covers the whole grid, with any cells it has beyond the room's walls and doorways.
- **Why:** a search is told the least cost among the cells the flood reached, and saves work only where that is above 0. Then a clear straight leg is taken with nothing searched, and a search takes up about as many cells as with no costs.
- **Don't: leave one reachable cell at the normal cost.** Every route with a costed cell under its straight line then waits for a search, and a slow one.

### Leeway

- **A route's way may cost a tenth more than the cheapest,** and is found with about a tenth of the work for it.
- **`WallGridRoute.Leeway` is that share:** 0 gives the cheapest way, and more gives quicker searches.
- **Where much of the room costs more than the rest,** a small leeway saves little. One as large as that cost is, 2 for a cost of 32, can make such a search far quicker.
- **What it gives up depends on the room.** In the rooms measured the ways came out from hardly dearer to 15 in 100 dearer on the whole, and up to twice as dear at the worst, and in some the search still took up thousands of cells.

### Using route objects

- **Keep one `WallGridRoute` for each search that runs at once, not one for each agent.**
- **It holds 16 bytes for each cell of the largest grid it has searched,** and serves grids of any size.
- **Copy the points out when a search is done,** and a few route objects serve any number of agents.
- **A search scheduled while its last is still running** runs after it and replaces its answer.

## Jobs, threads and safety checks

- **Main thread only.** `TryRoom`, `IsMarked` and `Wall` wait for the marking jobs first, and what reads a size class's regions waits for its flood. They do not wait for a job of yours.
- **A job of yours that reads `Wall` or `Cost`** must be handed to `AddReader`. The next marking then runs after it, and `SetShapes`, cost changes and `Dispose` wait for it. Otherwise those write or free the arrays under your job.
- **After `Dispose`** members that read or change a grid or a set throw `ObjectDisposedException`.

Unity's checks are turned off in these places, each for the reason shown. The job that updates a `BoxSet` lifts none, and nor do the jobs that flood regions, build a travel field and sample one.

| Job | Array | Why it is safe |
|---|---|---|
| `InSphereJob`, `NearestJob` | the results | Query q writes only `results[q * stride .. (q + 1) * stride)`, so no two queries write the same place |
| `NearestJob` | the squared distances | The same slices as the results |
| `MarkJob` | the wall distance | Layer z writes only `wall[z * layer .. (z + 1) * layer)`, so no two layers write the same place |

## Tests

Run them in the Unity editor, with the package in a project's `testables`:

```
Unity -batchmode -projectPath <project> -runTests -testPlatform EditMode -testResults <file>.xml
```

- Burst compiles the jobs first, and leak detection is on.
- Tested with Unity 6000.3, Burst 1.8.29, Collections 2.6.5 and Mathematics 1.3.3 (the versions `package.json` asks for); nothing older has been tried.
- The source is C# 9, the version Unity compiles.
