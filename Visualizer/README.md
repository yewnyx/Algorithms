# Visualizer

Avalonia desktop app that shows `Graph.Tarjan`, `Graph.ConnectedComponents` and `FloydWarshall` on random graphs.

```
dotnet run -c Release --project Visualizer
```

- **Graphs.** Islands are generated as rings plus chords, so they are strongly connected by construction, and Tarjan's result is checked against them. Wide puts many islands in each of a few waves. Deep puts a few islands in each of many waves.
- **Before / after panes.** The before pane shows vertices in input order. The after pane shows Tarjan islands arranged into parallel waves, left to right. Cycle edges are orange. Drag to pan, scroll to zoom, double-click to reset.
- **Execution grid.** One cell per vertex in execution order, which is the reverse of Tarjan emission, so dependencies come first.
- **Scan.** Serial walks the grid linearly. Parallel runs islands on worker threads with a lock-free scheduler: each island keeps an atomic count of unfinished dependencies and joins a concurrent ready queue when that count reaches zero. When a scan finishes, every cross-island edge is checked to confirm it finished before its dependent started.
- **Hover.** Hovering a vertex in any view highlights it in the other two.
- **Time slicing tab.** Replays, in simulated time, a scheduler that runs many small tasks under a per-frame budget. Each task is a connected component of a generated dataflow graph. Pinned tasks run every frame; the rest share a round-robin rotation that stops once the budget is spent, so frame time stays near the budget and extra work turns into a longer rotation. The timeline shows each frame's cost, and the grid shows how long ago each task last ran.
- **Components and roots tab.** The same graph twice: `ConnectedComponents` ignores edge direction, `Tarjan` follows it. Tarjan rooted at the component vertices writes each component as one contiguous run, aligned with the component segments in the strips below. Click vertices to make them roots and see what each one reaches, the order it is written in, and what is skipped. The shuffle button rebuilds the graph from the same edges in random order with duplicates and confirms every output is unchanged.
- **Shortest paths tab.** A floor plan of rooms joined by doors, some missing or one-way, solved for every pair of rooms with `FloydWarshall.Solve`. Click a room to make it the goal: every room is coloured by its distance and shows an arrow through the door to take, read from the next-room table, and hovering a room draws its route from `FloydWarshall.Path`. "Watch it learn" replays the algorithm with its own copy of the textbook loop, one room at a time becoming an allowed stopover, and compares its distances with the library when it finishes. Clicking a doorway makes that door fast with `FloydWarshall.Relax` and checks the result against a full re-solve. The distance table the library stores can be shown alongside, in Tarjan's order, where it is block triangular.
