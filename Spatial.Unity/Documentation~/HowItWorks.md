# Spatial: how it works

Seven ideas and a summary. Each idea comes in the same order: the problem, the simple way, how the tree way differs, building, using, the numbers that matter, and what it gives up. The README has the calls and [Reference.md](Reference.md) has every rule. This page says why the calls are shaped as they are.

Two words first. A **grid** cuts a box of space into cubic cells, half a metre a side in most examples. A **tree** is the same grid kept in a smaller form, for a grid so large that a number for every cell no longer fits. Where the numbers below name an "outdoor area", they are for one 450 x 49 x 450 m, which is 79 million cells.

- [Wall distance and room](#wall-distance-and-room)
- [The tree](#the-tree)
- [Regions](#regions)
- [Routes](#routes)
- [Travel fields](#travel-fields)
- [Cost tables](#cost-tables)
- [Heightfields and the cut at a box](#heightfields-and-the-cut-at-a-box)
- [Dense and tree, side by side](#dense-and-tree-side-by-side)

## Wall distance and room

**The problem.** Many times a frame, something asks: how much room is there at this point? Can a body of this radius be here? Asking the shapes each time (boxes, spheres, triangles) is exact, and slow when there are thousands.

**The simple way.** Ask once, in advance. Each cell stores how far its centre is from the nearest shape, in one byte: a count of a small unit (a 128th of a metre), up to a cap (128 counts, so 1 m). `TryRoom` then reads the eight cells round a point and says how far away the nearest shape is *at least*. It never says more than the truth, and never more than the cap. Marking the grid is done once, in jobs, one layer of cells to a job.

**The tree way.** Most of a large area is open air, and a number for every cell of open air is the same number over and over. The tree keeps the numbers only where they differ (see [the tree](#the-tree)). It answers the same questions: `TryRoom`, `TryDistance`, and `HasRoom` ("is there at least this much room here?"), which in open air says yes without reading a single count. A grid of more than 4 million cells keeps a tree by itself; nothing is chosen.

**Building.** Marking is a Burst job. For a tree the build first asks whole cubes of space: no shape near means open, wholly inside a solid means solid, and only the rest is marked cell by cell. The built tree can be stored as a block of bytes and loaded later, so a part is built once, ahead of time, and not at every load.

**Numbers.**

| | Dense | Tree |
|---|---|---|
| Memory, the outdoor area | 79 MB | 4.2 MB |
| Room at a point in open air (a made-up 300 x 300 x 30 m part, Burst, desktop) | 71 ns | 45 ns |
| Room within 2 m of the ground (the same part) | 42 ns | 62 ns |

**What it gives up.** The tree keeps four bits a cell, not eight, so near surfaces it reads a little less room than the dense grid: 3 cm on average, 12 cm for 99 points in 100, and at most 20 cm. It never reads more. In round figures, 1 point in 20 near a surface is one where a body that fits is told it does not.

## The tree

**The problem.** A grid of 79 million cells needs 79 MB of wall distance, and the same again for each body size, field and search. There is a budget for the whole part, and it is not that.

**The simple way.** One byte for every cell, in a flat array. Reading is fast and the array is simple, and it is the same size for empty air as for a forest.

**The tree way.** Space is split into cubes of 4 x 4 x 4 children, and each child is one of three things:

- **An open cube:** every cell in it is at the cap. It costs a bit in its node and nothing more. It can be up to 16 cells along a side (8 m), and no more.
- **A solid cube:** every cell reads 0. It costs the same, and can be of any size.
- **A brick:** a mix. 4 x 4 x 4 cells, four bits a cell, 32 bytes.

Near the ground and the walls there are bricks; in the air above them there are a few big open cubes. A cube of 4 x 4 x 4 children is a node of 24 bytes.

**Building.** From the shapes, top down: ask a cube, and only cut it further where the answer is "mixed". A part with its tree built is stored as a block, and `SetTree` loads it. The block carries a fingerprint of the shapes it came from, and is refused beside other shapes.

**Using it.** The same calls as the dense grid. On a grid that keeps a tree the `Wall` array is empty: ask `TryRoom`, `TryDistance` and `HasRoom`. `IsTree` says which storage a grid has.

**Numbers.** The outdoor area: At a byte a cell it was about 8,400 nodes and 125,000 bricks (8.2 MB); at four bits it is 8,394 nodes and 124,089 bricks (4.2 MB), since bricks that read 0 become solid cubes. A brick is kept as `count * 15 / cap`, rounded down, and read back as the least count that gives; so a cell reads at most the dense count, and less by under a fifteenth of the cap.

**What it gives up.** Exactness near surfaces (above). A few bytes of nodes that must be read to answer. And simplicity: the tree is why regions, routes and fields each have a second way of doing their work.

## Regions

**The problem.** Which parts of the space can a body of a given size get to? Is this point inside the space a flying body can reach? Where is the nearest such point? A route search is no use here: it needs a goal.

**The simple way.** A cell is *open* for a body of radius r if its centre is at least r from every shape and no cost has closed it. Start from seeds (a doorway, a spot you know is open) and spread through open cells that share a face. Each cell gets a region number, and two open cells are in one region if the spread joined them. One byte a cell for each body size.

**The tree way.** The spread goes place by place instead of cell by cell. A *place* is an open cube taken whole, or the open cells of a brick when they are joined and plain, or else each cell. The region number is kept for each brick and open cube, and for each cell only in bricks where regions meet.

**Building.** A job: add the body size (`AddSizeClass`), then flood (`ScheduleRegions`) from seeds. Mark first, and flood again after the shapes change, or a cost closes or opens a cell.

**Using it.** `IsOpen` says whether a point's cell is open. `TryNearestOpen` finds the nearest open cell within a reach. Routes and fields start from regions: an end in a region the other end can't reach is refused at once, with nothing searched.

**Numbers.**

| A 300 x 49 x 300 m piece of the outdoor area | Dense | Tree |
|---|---|---|
| Kept | 35 MB | 141 KB |
| Working memory while it floods | 141 MB | 1 MB |
| Time (.NET, one thread) | 155 ms | 79 ms |

The whole area, in a tree: 0.35 MB for one body size, flooded in 0.2 s. Every cell gets the region number the dense flood gives it over the same counts, in all 35 million cells of that piece.

**What it gives up.** `TryNearestOpen` with `like:` (nearest place a body at another point could reach) is not answered over a tree. A seed is a solid, and may reach no farther than 1,024 m from the frame's origin, so a very long grid is seeded where bodies come into it.

## Routes

**The problem.** The way for one body, from here to there, as a few straight legs that keep its whole radius clear of every shape.

**The simple way.** Search cell by cell, cheapest first, with a little leeway so the search heads for the goal; then pull the way straight, replacing a stretch of cells by one leg wherever the straight leg is clear and no dearer. Its working arrays take 16 bytes for each cell of the grid, and the search refuses a grid of more than 67 million cells.

**The tree way.** Search place by place. In open air a place is an open cube: the way comes into it at one cell and cuts straight across to the cell it leaves by, which is clear because every cell of the cube is open. Near shapes a place is the open cells of a brick, when they are joined and plain, or else each cell the way comes into. While searching, the way through a brick is counted in steps across faces; the way written out uses the dense search's steps. So a search takes up a few hundred places where the dense one takes up many thousands of cells, and its arrays are sized to the search, not to the grid.

**Pulling straight.** The way is tried leg by leg against the shapes' exact distance. Over a tree, one cell in four of the way is tried as a turning point: that halved the cost of the slowest route and left its length as it was.

**Leeway.** A search with leeway reckons each metre still to go as dearer than a metre of the cheapest way, so it heads for the goal. A route object starts with a tenth. Over a tree it is at least 1 (`WallGridRoute.TreeLeeway`). It matters a great deal: on a made-up 300 x 300 x 30 m part, a search at leeway 0 took up 695 places on average, at 0.25 it took 31, and at 1 it took 20.

**Using it.** Keep a `WallGridRoute`, `Schedule` a search, and read `Status` first. A tree search can fill its table before it finds a way and says so (`SearchFull`); it never says "unreachable" for that.

**Numbers.** On the outdoor area, over a tree: a found search takes up about 90 places on average and under 3,500 at the most, in arrays under a megabyte. The dense search cannot run on that grid at all. Most routes take well under a millisecond, and the slowest of 800 long pairs took 7 ms (desktop, .NET, one thread). On the made-up part the slowest took 2.1 ms under Burst on the desktop and 6.1 ms on a phone.

**What it gives up.** The way is not the cheapest there is: as a rule it is as long as the dense search's, and among shapes set close together the worst seen is a quarter longer. The first way found into a place is the one kept. With costs the tree's way can be dearer than the dense way: on the whole within 7 in 100, and about twice as dear where an end lies inside a dear brick, since a place is entered at one cell.

## Travel fields

**The problem.** Many bodies stream to one goal without each planning a route. A travel field knows, everywhere, how far it is to the goal going round obstacles, so each body flies "downhill".

**The simple way.** Every half-metre cell stores its distance to the goal. A body samples the cells round it each frame and steps to the lowest. Over a 450 x 49 x 450 m outdoor area that is 79 million cells and about 160 MB for one field, so it cannot work there.

**The tree way.** The tree has already split space into open cubes (empty blocks up to 8 m) and bricks near surfaces. The field stores one distance for each place: each open cube, each brick whose open cells form one block, and each cell only in the few mixed bricks. Each place has a point the lines are drawn to, worked out from its cells when asked. About 0.9 MB for a field.

**Building.** Outward from the goal, place by place, like a route search run backwards. It takes seconds, in a job, when the goal changes (the whole outdoor area: 2.4 to 3.6 s on one desktop thread). On a made-up 300 x 300 x 30 m part under Burst it took 1.4 s, where the dense field took 3.4 s; on a phone, 4.4 s.

**Using it.** A body asks once which place it is in and where to fly next: a point in the neighbouring place with the lowest value plus distance. It flies straight, and asks again on entering a new place. When the straight line to the next place's own point is clear, it looks on up to three places. In the outdoor area a body enters a new place about every fifth step. Ask with `TryNext` (the point to fly to) and `TryTravel` (how far is left); arrival is when travel falls below about a cell.

**Many fields at once.** A body that reads nine fields a step should not look up its place nine times. `Around` finds the places round a point once for every field over that tree, and `TravelNear` reads one field from them. On the made-up part under Burst, finding the places round a point took 0.41 µs and each field then 0.04 µs, where one field asked alone takes 0.51 µs; `PlaceAt` finds the body's own place in 0.04 µs, so the places are looked up again only on entering a new one.

**Numbers.**

| The outdoor area, one field | Dense | Tree |
|---|---|---|
| Kept | about 160 MB | 0.84 to 0.96 MB |
| Followers that arrive | all | all (12,000 of 12,000 on a 300 m piece; 3,600 of 3,600 starting up to 150 m away on the whole area) |
| Steps into a closed cell | none | none (0 in 15,600 followers) |
| Way flown | 1 | 1.03 times |

**What it gives up.** The travel read from a tree field is the dense field's within about 1.02 to 1.13 times, for 99 in 100. One field asked alone costs 5 to 6 times a dense sample (0.5 µs against 0.09 µs under Burst), which is why many fields share their work. Over a tree `Distance` is empty: read `KeepsCells` first, and ask `TryTravel` and `TryNext`. A field is refused with more than 256 seeds.

## Cost tables

**The problem.** The same ground is not the same price to everyone. One body keeps out of danger and another doesn't mind it; one wants to stay in fog. A cost array says what each cell *is*. Each kind of body needs its own view of what that costs.

**The simple way.** A byte for each cell: 0 normal, 255 closed, anything between makes a step into that cell dearer by 1 + cost / 16. One price list for everybody.

**The table way.** The byte becomes a *kind of ground*, and each kind of body has a table of 256 prices for it. Routes and fields take a table; with none, each byte is its own price. Regions are made from the array for every kind at once, so a table can close a byte for its own kind but never open 255, which is closed for all. The package's convention is danger in the high four bits and fog in the low four, each 0 to 15, and `CostTable.Of(danger, fog)` writes a byte. `CostTable.Make(danger, fog, table)` makes a table from two weights, moved up so the cheapest ground costs 0. A weight below 0 is a preference: a body with a fog weight of -4 keeps to fog.

**The tree way.** A byte for each brick and open cube. Costs then reach 8 m in open air and 2 m near surfaces. A step pays for the brick or open cube it enters, and a cost of 255 closes the whole brick or open cube.

**Building.** Write what the cells are with `FillCost`, give each kind of body its table (`WallGridRoute.SetCostTable`, and the table argument of `ScheduleField`). A fill that closes or opens nothing keeps the regions, and fields keep the costs they were built with, so danger that moves can be written every frame without a new flood.

**Numbers.** On a 300 m piece of the outdoor area with 40 danger zones of 6 to 14 m: routes spent 390 m in danger where routes that ignored it spent 2,679 m, and cost 0.55 as much as flown over the costs (19,879 against 36,180). With no costs and no table the same calls were 0 to 3.5 in 100 slower than before tables existed (Burst).

**What it gives up.** Searches that go round danger look at more places. On that 300 m piece the mean went from 0.4 to 1.9 ms and the worst from 7 to 28 ms (.NET), with no search full. On the made-up part it went from 0.32 to 1.72 ms and the worst from 3.8 to 33 ms (.NET), with 2 searches in 300 full; on a phone under Burst, from 0.45 to 2.6 ms and the worst from 6.1 to 40 ms. Costs written over a tree are by place: a solid that holds the centre of any one cell of a brick prices the whole brick. `RouteResult.Cost` over a tree is the search's own sum and can read lower than the way as flown.

## Heightfields and the cut at a box

**The problem.** Ground is thousands of triangles, and the space *under* ground is solid, which triangles don't say. A large scene is far more than one grid needs.

**The simple way.** Hand the collider's triangles over. A terrain of 513 x 513 heights is over half a million triangles, and a triangle has no inside, so room can read as available deep under the ground.

**The heightfield way.** Ground is kept as heights over a grid of squares, solid from its surface down to a floor. A point under the surface is at distance 0. Heights are kept in blocks of 16 x 16 squares, each block at every 8th, 4th or 2nd height where that keeps the surface within a bound of the ground (0.25 m for the terrains the collector takes), and at every height where it does not. The surface kept is never below the real ground and never more than the bound above it.

**The cut at a box.** The collector takes a box of the frame and leaves out what lies wholly outside it: whole colliders, and a mesh's triangles one by one. `ColliderShapes.Within` gives the box for a grid: the grid's own, grown by as far as marking looks from a cell and a cell more (1.5 m with half-metre cells and the default unit and cap). A cell at the grid's edge then still holds its true distance to what lies just outside. Terrains go in as heightfields in these calls.

**Building and using.** `HeightFields.Pack` makes one from a grid of heights; `ColliderShapes` calls it for terrains. As for any shapes, collect in the editor and store the block.

**Numbers.** Seven terrains of 513 x 513 heights took 0.22 MB, against 3.7 MB as given. A terrain packs in about 10 ms. In a test scene on a phone, marking ground as a heightfield took about a third of the time the same floor took as triangles. Room is found as for any other shape, and the distance to a heightfield is exact to the surface it keeps.

**What it gives up.** A body can't come quite as near the ground as it truly could: the surface lies above the ground by 5 cm on average and 25 cm at the most. An end of a route that rests on the ground may look out from under the surface. A terrain with holes goes in as a box around it and is counted; trees painted on a terrain whose prefabs have colliders are not taken, and are counted too. Shapes cut at a box know nothing beyond it: `TryDistance` is the true distance only for a point inside the grid and a limit no more than the box was grown by.

## Dense and tree, side by side

For the 450 x 49 x 450 m outdoor area, 79 million cells, by the figures above.

| What | Dense (a byte or two a cell) | Tree |
|---|---|---|
| Wall distance | 79 MB | 4.2 MB |
| Regions, one body size | 79 MB | 0.35 MB |
| A travel field, one goal | about 160 MB | about 0.9 MB |
| A route's search arrays | 16 bytes a cell; refuses over 67 million cells | under 1 MB |
| Costs | a byte a cell | a byte a brick or open cube |

In one line: the dense way pays for every cell of air, and the tree way pays for the places where the air stops, at the cost of answers that are a little less exact near surfaces and routes that are not always the shortest.
