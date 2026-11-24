# Spatial: reference

Every rule and limit of the `Yewnyx.Spatial` library, grouped by the type it belongs to. The [README](../README.md#spatial) has the short tour. The Unity package's own rules are in [its reference](../Spatial.Unity/Documentation~/Reference.md).

- [Every call](#every-call)
- [Shapes and blocks](#shapes-and-blocks)
- [Wall distance](#wall-distance)
- [Size classes and regions](#size-classes-and-regions)
- [Travel fields](#travel-fields)
- [Routes](#routes)

## Every call

- **Queries take and return points,** never cell numbers. `GridShape.TryCentre` gives the centre of the cell that holds a point.
- **To walk an array you own,** `IndexOf` and `CentreOf` give a cell's place and centre.
- **A call returns false if it is refused:** a point outside the grid, spans that don't belong together, a shape that may not be stored. Then read nothing from its outputs.
- **A refused `MarkLayer`, `FromSeeds` or `Build`** leaves its array saying nothing: no room, no open cell, no cell reached.

## Shapes and blocks

- **A solid's fields can be read back,** to draw it for instance. What each holds depends on its `Kind`.
- **A distance is within `Shapes.Slack` of the true one** (a quarter of a millimetre), for shapes within `Shapes.Reach` (1,024 m) of the origin, and for a box or a heightfield whose axes are as exact as single precision gives.
- **A triangle has no inside,** so the middle of a closed shell of triangles reads as room. A solid's inside reads 0.
- **`HeightFields.Pack` makes a heightfield from a grid of heights:** ground that is solid from its surface down.
- **It keeps every 8th, 4th or 2nd height** where that stays within a bound of the ground, and every height where it does not.
- **A heightfield's surface is never below the ground it was made from,** and never more than its `Gap` above it.
- **So room is proven from the ground itself,** and what is given up is reach: a body cannot come quite as near the ground as it truly could.
- **An end of a route that rests on the ground** may look out from under the surface.
- **The same shapes give the same block on every runtime.**
- **A block of another `Shapes.Version` is refused:** make it again.
- **A damaged block reads nothing out of bounds,** but it can give wrong answers.
- **A solid of an unknown kind,** or one that holds a number that isn't finite, never reads as farther than it is.

## Wall distance

- **A cell's count times the `unit`** is never more than the true distance from its centre.
- **A cell at the `cap` is that far or more.**
- **`RoomAt` answers from the grid alone,** in constant time.
- **`HasRoom` asks "is there at least this much room here?"** and gives what `RoomAt` gives, compared.

### Wall distance as a tree

- **`WallTrees` keeps a grid's counts as a tree,** for a grid that is mostly open air: counts only near surfaces.
- **A cube that is all open or all inside a solid is one bit.** An open cube is never more than 16 cells a side.
- **A brick keeps four bits a cell:** each count put down to a fifteenth of the cap. With a cap of 15 or less nothing is lost.
- **`WallTrees.RoomAt` gives what the grid gives with its counts put down the same way,** to the bit, for shapes that are whole: a little less room near surfaces, never more.
- **In open air `WallTrees.HasRoom` answers without reading a count.**
- **`Measure` says how much room `Build` needs;** a build that runs out of room is refused.
- **A side of more than 2^30 cells is refused.**
- **A tree's block is read only beside the shapes it was built from:** it keeps their fingerprint.
- **A block that passes `TryRead` sends no query outside its nodes or bricks.** A tree that is not whole claims no room.

## Size classes and regions

- **"Open" is said of cells.** `IsOpen` at a point says its cell is open, so a body at the cell's centre fits. The point itself can be most of a cell from there.
- **To ask whether a body fits at a point,** compare `RoomAt` or `Shapes.Distance` with its radius.
- **A cell is open for a size class** when its centre is at least the radius from every shape and its cost isn't closed.
- **Between two open cells that share a face,** every point is at least `SizeClass.Guarantee` from every shape.
- **A region is the open cells joined by steps between cells that share a face,** never across a diagonal: so a sheet with no thickness is never crossed.
- **A radius of half a cell or less has no guarantee** and is refused.
- **A seed reaches from the open cells whose centres are inside it.** One with none reaches nothing and is counted, not moved.
- **`NearestOpen` is by straight line,** whatever lies between: the cell can be on the other side of a thin wall.
- **`NearestOpen` can also be asked for the nearest open cell in the same region as another point.** That cell can still be across a wall when both sides are joined some other way.

### Regions over a tree

- **`SizeClass.TryMake` takes a tree as well as a grid.**
- **`TreeRegions.FromSeeds` gives the same cells the same region numbers** as `Regions.FromSeeds` over the grid's counts put down as the tree keeps them.
- **It keeps a byte a brick and a byte an open cube.** A brick whose open cells are not all in one region is kept in full, 68 bytes more.
- **`TreeRegions.IsOpen` and `TreeRegions.NearestOpen` answer as `Regions`' do.** `NearestOpen` over a tree has no form for the same region.

### Costs over a tree

- **`TreeCosts` keeps a byte for each leaf, a brick or an open cube,** where a grid keeps one for each cell: `LayerLength` bytes, about half a megabyte for a part of 450 x 50 x 450 m.
- **The bytes mean what `CellCost`'s do, and a table prices them the same way.**
- **`TreeCosts.Fill` gives a leaf the value where the solid holds the centre of any of its cells.** A leaf holds one cost for all its cells: 2 m for a brick, up to 8 m for an open cube, so a body keeps away from a little more than was filled.
- **`TreeRegions.FromSeeds` takes the costs:** a leaf that holds 255 is closed, all its cells. `TreeRegions.CostsIn` marks the values the reached leaves hold, for `CellCost.Least`.

## Travel fields

- **A field spreads from its seeds** through the cells of a region array, by steps between cells that share a face.
- **A step pays for the cell it enters:** a cost of c makes a cell 1 + c / 16 times as far to cross.
- **A seed's cells start at their distance from a point** (a goal), or at their distance in front of a plane, which makes the field lead out through an opening.
- **A cell is started only if the straight way from it to the point, or square to the plane, lies through cells the field can enter.** So a seed never starts a cell behind a wall, and one across an opening too narrow to pass starts nothing in front of it.
- **Distances are found by fast marching,** from a cell's neighbours on all three axes at once.
- **Through open space they are a little over the straight line:** about 5 parts in 100 far from the start.
- **A distance of more than 65,534 counts doesn't fit.** Such a cell is left unreached and counted, never held at the largest count.
- **`Travel.Sample` blends the distance** between the eight cells round a point.
- **Its direction is down that slope where the slope truly leads down.** Otherwise it is towards the lowest cell that shares a face with the nearest reached one, or, if none of those six is lower, the lowest of the 26 round it.
- **At a goal the direction never settles:** a follower has arrived when the distance falls below about a cell.
- **It promises nothing about what lies between cells:** whatever follows it still needs its own way of not hitting things.

### Travel fields over a tree

- **`TreeFields.Build` keeps a travel for each place, not each cell:** an open cube, a brick whose open cells are joined and plain, and each open cell of any other brick. 2 bytes a place, under a megabyte for a part of 450 x 50 x 450 m.
- **`Measure` says how long the field's bytes and the build's scratch are,** exactly, for the tree, the regions and the seeds.
- **Seeds start as a dense field's do,** and the field keeps the cells they started.
- **A place's travel is the straight line where it can be:** taken from the place before last when every cell the line between their points touches is open.
- **Every place a seed did not start has a neighbour lower than itself,** so a follower that goes downhill from place to place always arrives.
- **`TravelAt` is the cheap question:** how far a point is along the field, from its place and the places beside it. Ask it of every field at every step.
- **Asking many fields at one point: `Around` once, then `TravelNear` for each field.** The places round a point are the same for every field over one tree and regions; `TravelNear` gives what `TravelAt` does.
- **Keep `Around`'s places while `PlaceAt` gives the same place,** and ask again on a new one. `PlaceAt` is the cheapest call: the place alone. Room for 32 places is enough at about 96 points in 100; where a point has more, `Around` says how many, and is asked again each step with room for them.
- **`Next` gives a point to fly to,** not a direction. Fly straight to it, and ask again on entering another place or on reaching it; both calls say which place a point is in.
- **The point given is in sight:** every cell the straight line to it touches, at a face, an edge or a corner, is open.
- **A point belongs to the cell `GridShape.TryCentre` finds it in,** as over a dense map: a point on the face between two cells is in the higher one.
- **A point whose cell is not open is refused.** A dense field may still answer there, from the eight cells round it.
- **`Next` is refused at the seed's own point,** or as near it as open cells let a point be: there is nowhere lower. A follower has arrived when `TravelAt` is under about a cell.
- **`TreeFields.Build` takes the costs and a table:** a line counts its length in each leaf it passes through 1 + cost / 16 times. Ask the field with the same costs and table, given to `TreeField`.
- **With costs, the point to fly to is never taken further on through a leaf dearer than the places hopped through.**

## Routes

A route is the points to go to in turn; the start isn't among them.

`RouteResult.Status` says whether there is one.

| `Status` | Meaning |
|---|---|
| `Found` | There is a route. |
| `Unreachable` | There is no way for a body of that size. |
| `BufferTooShort` | There is a route with more points than the buffer holds. Its `Count` says how many. |
| `Refused` | Something given cannot be used. |
| `SearchFull` | Over a tree only: the search filled its arrays before it ended. |

### Legs

- **Every leg is one of two kinds.**
- **A straight leg is proven clear along its whole length for the class's full radius:** from the grid where the grid can say, and from the shapes where it can't.
- **Where no straight leg is clear,** the route steps between the centres of two open cells that touch, which keeps `SizeClass.Guarantee`.
- **With a `ShapeSet` of no shapes** a straight leg is proven from the grid alone, which needs more room: for a radius of 0.51 m on half-metre cells, up to 0.64 m from a wall.
- **No leg passes through a closed cell, or nearer one than half a cell.** That is less than a body's radius: to keep a body off something shut by cost alone, close the cells out to the radius beyond it.
- **Pulling straight takes, from each point, the farthest cell of the way that a straight leg reaches.** That gives few legs, not always the shortest route: now and then a nearer turning point would have made the next leg shorter.

### Ends

An end need not be in an open cell. It then takes the nearest open cell whose centre it can see, no farther than 1.73 times the radius and a cell.

| `RouteEnds` | When |
|---|---|
| `FirstLegUnproven` | The start is nearer a shape than the radius. The route's first point is that cell's centre. |
| `StopsShortOfGoal` | The goal is. The route ends at that cell's centre. |

- **Ends in two regions are `Unreachable` at once,** with nothing searched.
- **A start in a cell closed since the regions were made, by cost or by the table, is routed out of it** where it is beside an open cell, with `FirstLegUnproven`. Where it is not, the route is `Unreachable`, and so is a goal in such a cell. Over a tree it is the start's leaf that is closed, and its cell that must be beside an open one: both storages agree.

### Costs

The way is searched cell by cell, cheapest first by the cells' costs, and then pulled straight. A straight leg takes the place of part of the way only if it is clear and no worse than that part in two things: it passes through no cell that costs more than the costliest cell of that part, and it costs no more as flown, which is its length in each cell times 1 + cost / 16 for that cell.

| So | |
|---|---|
| A route never costs more as flown | than its way left as cells |
| It goes round an area | that its way went round |
| It crosses an area | only where that costs no more than what its way did |

- **The straight leg from start to goal is taken with nothing searched** where it is sure to cost no more than any way: no way between the centres of the two ends' cells costs less than the straight line between them at the least cost of any cell.
- **`Regions.LeastCost` gives that least,** and `Routes.Find` takes it as `leastCost`. With 0, which is always right, the leg is taken at once only where no cell under it has a cost.
- **The search reckons what is left to go at the same least cost,** so where every cell costs something it takes up about as many cells as with no costs.
- **Both save work only where the least is above 0.** Do: where a whole grid is meant to cost more, give every cell with a region that cost.
- **A table gives one kind of agent its own costs over the same array:** 256 costs, one for each byte. `Routes.Find`, `Travel.Build` and `Regions.LeastCost` take one.
- **A table can close a byte for its kind, but cannot open 255:** regions are made from the array, for every kind at once (`CellCost.IsTable`). A goal that only the table shuts off is found unreachable by searching its whole region.
- **Give `Regions.LeastCost` the same table as the search.**

### Leeway

- **`Routes.Find` takes a `leeway`:** how much more than the cheapest the way may cost, as a share of it. With 0, the default, the way is the cheapest.
- **With 0.1 the way costs up to a tenth more,** and the search takes up about a tenth as many cells where the grid has no costs or one cost all through.
- **Where much of a room costs more than the rest it saves little** unless it is as large as that cost is: what is left to go is reckoned at the least cost of any cell.
- **The legs are held to the way that was found either way.**

### The search's arrays

- **A search's own arrays take 16 bytes a cell,** and can be used again without clearing, for grids of any size they are long enough for.
- **The links must be all zero the first time;** every search leaves them so.

### Routes over a tree

- **`TreeRoutes.Find` gives legs of the same two kinds,** proven the same way, and ends as `Routes.Find` does.
- **It searches places, not cells:** an open cube whole, crossed by one straight cut, and the joined open cells of a brick.
- **Its arrays are sized to the search, not the grid:** `LinksLength` and `SumsLength` for a number of places, 28 bytes a slot.
- **A search that fills them ends `SearchFull`.** Give it larger ones.
- **`TreeRoutes.Find` takes the costs, a table and `leastCost`:** a step pays for the leaf it enters, and no way enters a leaf the table closes.
- **A leg replaces part of the way only where it passes through no dearer leaf than that part, and costs no more as flown.** No leg passes through a closed leaf.
- **The straight leg from start to goal is taken with nothing searched** only where every leaf under it costs `leastCost`.
- **`leeway` is 0.25 unless given.**
- **The way is not the cheapest there is.** Pulled straight, routes come out as long as the grid's on the whole, and up to a quarter longer among shapes set close together.
