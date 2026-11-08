# Visualizer

Avalonia desktop app that shows `Graph.Tarjan` on large random graphs.

```
dotnet run -c Release --project Visualizer
```

- **Graphs.** Islands are generated as rings plus chords, so they are strongly connected by construction, and Tarjan's result is checked against them. Wide puts many islands in each of a few waves. Deep puts a few islands in each of many waves.
- **Before / after panes.** The before pane shows vertices in input order. The after pane shows Tarjan islands arranged into parallel waves, left to right. Cycle edges are orange. Drag to pan, scroll to zoom, double-click to reset.
- **Execution grid.** One cell per vertex in execution order, which is the reverse of Tarjan emission, so dependencies come first.
- **Scan.** Serial walks the grid linearly. Parallel runs islands on worker threads with a lock-free scheduler: each island keeps an atomic count of unfinished dependencies and joins a concurrent ready queue when that count reaches zero. When a scan finishes, every cross-island edge is checked to confirm it finished before its dependent started.
- **Hover.** Hovering a vertex in any view highlights it in the other two.
- **Time slicing tab.** Replays, in simulated time, a scheduler that runs many small tasks under a per-frame budget. Each task is a connected component of a generated dataflow graph. Pinned tasks run every frame; the rest share a round-robin rotation that stops once the budget is spent, so frame time stays near the budget and extra work turns into a longer rotation. The timeline shows each frame's cost, and the grid shows how long ago each task last ran.
