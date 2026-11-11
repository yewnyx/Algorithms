Small .NET libraries for graph algorithms, diffing and saving object graphs. They are written to avoid allocating: results go into buffers you provide, and the `ref` overloads size the buffers for you.

- **Graphs** (`Algorithms`): strongly connected components with `Graph.Tarjan` (also from chosen roots), `Graph.ConnectedComponents`, and all-pairs shortest paths with `FloydWarshall`.
- **Diffing** (`Algorithms`): `MyersDiff`, `HistogramDiff`, `HeckelDiff` (moved blocks), diffs of items in groups (lines, then the characters in them) and `Patch`.
- **Packables** (`Packables`, `Packables.Newtonsoft`): versioned saving and loading of object graphs, with JSON text.
- **Collections**: `TwoWayDictionary`, a dictionary with one-to-one pairs that looks up in both directions.
- **Visualizer**: a desktop and browser app that shows the graph algorithms on random graphs and opens Packables documents. [Try it in the browser](https://yewnyx.github.io/Algorithms/).

## Graphs

A `Graph` holds only edges. Vertices are plain zero-indexed numbers, so there are no constraints on backing data structures and no unnecessary allocations.

`Graph.Tarjan` writes two layers into spans you provide: `dependencyOrder`, which holds every vertex once, and a list of `Segment`s that slice it into the strongly connected components. The components come out in dependency order: for every edge A → B, B's component is written no later than A's. One pass gives both the components and an order to process them in, with no allocation per component. The search is iterative, with no recursion, each vertex keeping its own cursor into edges held in CSR form, and the output depends only on the vertex count and the set of edges, not on the order they were added.

```csharp
using Yewnyx.Algorithms.Graphs;

// A cycle 0 → 1 → 2 → 0, then a tail 2 → 3 → 4.
var graph = new Graph().AddEdges((0, 1), (1, 2), (2, 0), (2, 3), (3, 4));

int[]? order = null;
Segment[]? segments = null;
int count = graph.Tarjan(ref order, ref segments);   // 3 components
// order:    [4, 3, 2, 1, 0]
// segments: (0, 1) (1, 1) (2, 3)                     (offset, size)

for (int i = 0; i < count; i++) {
    ReadOnlySpan<int> component = order.AsSpan(segments[i]);   // a view, no copy
    // i = 0: [4]   i = 1: [3]   i = 2: [2, 1, 0]
}

// Only what vertex 3 reaches: 2 components, order [4, 3].
count = graph.Tarjan(new[] { 3 }, ref order, ref segments);
```

The `ref` overloads make or grow the arrays; the span overloads write into buffers you own. A `Segment` is an (offset, size) slice of an array: `array.AsSpan(segment)` is a view, and `array[segment]` copies.

- `Graph.ConnectedComponents` groups vertices joined by edges in either direction, using union-find, with groups ordered by their lowest vertex.
- `FloydWarshall.Solve` computes all-pairs shortest paths in place over a caller-owned n × n weight matrix, with an optional next-hop matrix for rebuilding paths. It suits small graphs: O(n³) time.
- `Graph.ContractVersion` is bumped whenever behaviour callers rely on changes, such as output order or buffer rules, so a rebuilt library can't change behaviour silently. Check it once at startup.

## Diffing

`MyersDiff` finds a shortest edit script between two sequences, and `HistogramDiff` finds one that lines up the rarest items first, as git's histogram diff does, which reads better for text. Both work over spans, are given their buffers, and don't allocate. A result is a list of `Hunk`s, each saying which range of the original is replaced by which range of the revised.

```csharp
using Yewnyx.Algorithms.Diffing;

string[] original = ["header", "alpha", "beta", "gamma", "footer"];
string[] revised  = ["header", "alpha", "gamma", "delta", "footer"];

// Simple: the arrays are made or replaced as needed.
Hunk[]? hunks = null;
int[]? workspace = null;
int count = MyersDiff.Diff<string>(original, revised, ref hunks, ref workspace);
// hunks[0]: original [2, +1) -> revised [2, +0)   delete "beta"
// hunks[1]: original [4, +0) -> revised [3, +1)   insert "delta" before "footer"

// No allocation: size the buffers with MaxHunks and WorkspaceLength, and keep them.
Span<Hunk> hunkBuffer = stackalloc Hunk[MyersDiff.MaxHunks(original.Length, revised.Length)];
Span<int> work = stackalloc int[MyersDiff.WorkspaceLength(original.Length, revised.Length)];
int spanCount = MyersDiff.Diff<string>(original, revised, hunkBuffer, work); // the same hunks, in hunkBuffer
```

`HistogramDiff` has the same shape and its own `MaxHunks` and `WorkspaceLength`, which are larger. An empty side's offset is where the edit happens. Original and revised may be slices of one buffer, even overlapping ones, because both are only read; hunk offsets are relative to each slice's own start:

```csharp
int[] data = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13];
Hunk[]? sliceHunks = null;
int[]? sliceWorkspace = null;
int sliceCount = MyersDiff.Diff<int>(data.AsSpan(0, 10), data.AsSpan(3, 10), ref sliceHunks, ref sliceWorkspace);
// original [0, +3) -> revised [0, +0)   delete 1, 2, 3
// original [10, +0) -> revised [7, +3)  insert 11, 12, 13
```

A `Patch<T>` keeps a diff for later, without the revised sequence: the hunks and copies of the items they delete and insert. It applies forwards and backwards, and refuses an original that differs in an item the patch deletes:

```csharp
var patch = new Patch<string>();
patch.Set(original, revised, hunks.AsSpan(0, count));
// patch.Deleted: [beta]   patch.Inserted: [delta]

var result = new string[patch.RevisedLength];
patch.TryApply(original, result);   // true: header, alpha, gamma, delta, footer
var undone = new string[patch.OriginalLength];
patch.TryRevert(result, undone);    // true: header, alpha, beta, gamma, footer
```

`FindMoves` pairs deleted items with equal inserted ones:

```csharp
string[] a = ["a", "b", "c", "d"];
string[] b = ["b", "c", "d", "a"];
int moveCount = MyersDiff.Diff<string>(a, b, ref hunks, ref workspace);
patch.Set(a, b, hunks.AsSpan(0, moveCount));
foreach (var (was, now) in patch.FindMoves()) { /* was 0, now 3: "a" moved to the end */ }
```

`FindMoves` pairs single items after the fact. `HeckelDiff` finds whole moved blocks as it diffs. It writes hunks that `Patch<T>` takes unchanged, and the blocks that moved: each pairs a run deleted in one hunk with an equal run inserted in another. A reordered list of distinct items costs O(n log n), where Myers' diff is quadratic. Use it where a move is worth showing: reordered lists, undo, and diff views that draw a moved block instead of a delete and an insert.

```csharp
string[] listBefore = ["Milk", "Eggs", "Bread", "Apples", "Coffee", "Rice"];
string[] listAfter  = ["Coffee", "Milk", "Eggs", "Apples", "Rice", "Bread"];

Hunk[]? listHunks = null;
BlockMove[]? listMoves = null;
int[]? listWorkspace = null;
int listCount = HeckelDiff.Diff<string>(listBefore, listAfter, ref listHunks, ref listMoves, ref listWorkspace, out int listMoveCount);
// 4 hunks: original [0, +0) -> revised [0, +1)   insert Coffee
//          original [2, +1) -> revised [3, +0)   delete Bread
//          original [4, +1) -> revised [4, +0)   delete Coffee
//          original [6, +0) -> revised [5, +1)   insert Bread
// 2 moves: (Original 2, Revised 5, Length 1)     Bread moved to the end
//          (Original 4, Revised 0, Length 1)     Coffee moved to the front
```

`MyersDiff` and `HistogramDiff` give the same four hunks and no moves. Like them, `HeckelDiff` also has span overloads, with `MaxHunks`, `MaxMoves` and `WorkspaceLength`. A block made only of repeated items has no anchor, so it stays a delete and an insert (`FindMoves` can still pair its items one by one), and the script is as short as Myers' only between the blocks that stay.

`GroupClasses` and `GroupedDiff` diff items split into groups, such as the characters of a text split into lines. Use them for text views that show which characters or words changed inside changed lines, and for lists of lists, such as rows of cells. There are three steps, so you pick the diff in the middle: give equal groups one id, diff the ids with `MyersDiff` or `HistogramDiff`, and refine the changed groups into item hunks:

```csharp
using Yewnyx.Algorithms.Graphs;

string textBefore = "one\ntwo\nthree\n";
string textAfter  = "one\ntwo!\nthree\n";
Segment[] linesBefore = [new(0, 4), new(4, 4), new(8, 6)];   // the lines, with their line breaks
Segment[] linesAfter  = [new(0, 4), new(4, 5), new(9, 6)];

// 1. Equal lines share an id, numbered from 0 in the order they are first seen.
int[] idsBefore = new int[linesBefore.Length];
int[] idsAfter = new int[linesAfter.Length];
int[] idWorkspace = new int[GroupClasses.WorkspaceLength(linesBefore.Length, linesAfter.Length)];
GroupClasses.Assign<char>(textBefore, linesBefore, textAfter, linesAfter, idsBefore, idsAfter, idWorkspace);
// idsBefore: [0, 1, 2]   idsAfter: [0, 3, 2]

// 2. Diff the ids, which diffs the lines.
Hunk[]? lineHunks = null;
int[]? lineWorkspace = null;
int lineCount = MyersDiff.Diff<int>(idsBefore, idsAfter, ref lineHunks, ref lineWorkspace);
// original [1, +1) -> revised [1, +1)   the second line changed

// 3. Refine the changed lines into hunks over the characters.
var charHunks = new Hunk[GroupedDiff.MaxHunks(textBefore.Length, textAfter.Length)];
var charWorkspace = new int[GroupedDiff.WorkspaceLength(textBefore.Length, textAfter.Length)];
int charCount = GroupedDiff.Refine<char>(textBefore, linesBefore, textAfter, linesAfter, lineHunks.AsSpan(0, lineCount), charHunks, charWorkspace);
// original [7, +0) -> revised [7, +1)   "!" inserted after "two"
```

The result is one complete script over the items, which `Patch<T>` takes unchanged. Where a run of lines is replaced by as many lines, they pair in order and each pair is diffed with Myers; a run replaced by a different number of lines is one hunk, with nothing refined inside it. The groups must tile the items in order, which for text means lines with their line breaks. You split the text, because the library never parses it, so the items could as well be words in lines or cells in rows. Two long, unrelated lines are diffed quadratically.

Items must implement `IEquatable<T>`. `HistogramDiff`, `HeckelDiff`, `GroupClasses` and `FindMoves` also hash their items, so `GetHashCode` should agree with `Equals` and vary in its low bits (a struct needs to override it). The inputs may overlap each other, but the workspace and output buffers must not share memory with either one, and `Diff` throws an `ArgumentException` if they do; a `Patch` result must not share memory with its input, and throws if it does.

## Packables

Packables saves a graph of objects to a versioned document and loads it back. Shared and looping references keep their identity, a type's version travels with its data, and a damaged document is reported as problems instead of throwing. Objects write and read themselves, and the library never creates one: you pass a create function or bind an instance you made. `Packables` is the core; `Packables.Newtonsoft` reads and writes the JSON text.

```csharp
using Yewnyx.Packables;

public class Crew : IPackable {
    public string? Name;
    public Crew? Friend;

    public void PackInto(Packer packer) {
        packer.WriteString("name", Name);
        packer.WriteRef("friend", Friend);
    }

    public void UnpackFrom(Unpacker unpacker) {
        unpacker.TryReadString("name", out Name);
        unpacker.TryReadRef("friend", static () => new Crew(), out Friend);
    }
}

var registry = new PackableRegistry.Builder().Add<Crew>("Crew", 0).Build();

// Pack: any object reachable from the root, loops included.
var document = new PackedDocument();
Packer.TryPack(ada, registry, document, out var unregistered);
string json = PackedJson.Write(document, "application/crew+json");

// Read and unpack into a root you provide.
var read = new PackedDocument();
PackedJson.TryRead(json, read, "application/crew+json", out var error);
var problems = new List<UnpackProblem>();
var outcome = Unpacker.Unpack(read, new Crew(), registry, problems); // Clean, WithProblems or Failed
```

A read of an absent key returns false and records nothing. A key of the wrong kind or out of range, or a ref to a missing object or of the wrong type, returns false and is recorded in `problems`. `UnpackOptions.FailFast` ends the load at the first problem, and `Permissive` records unknown types instead of refusing the document.

The JSON for two `Crew` that point at each other, `ada` first (fields sorted by key, objects numbered breadth-first as `"o:N"`):

```json
{
  "Content-Type": "application/crew+json",
  "version": 3,
  "root": { "$ref": "o:0", "$type": "Crew#0" },
  "objects": {
    "o:0": { "$type": "Crew#0", "$fields": { "friend": { "$ref": "o:1", "$type": "Crew#0" }, "name": "Ada" } },
    "o:1": { "$type": "Crew#0", "$fields": { "friend": { "$ref": "o:0", "$type": "Crew#0" }, "name": "Bo" } }
  }
}
```

Plain values are plain JSON, and keys starting with `$` belong to the format: `$ref` with `$type` for a ref, `$map` and `$array` for containers, `$fields` for an object's fields, `{ "$double": "NaN" }` for NaN and the infinities. Ids are opaque strings, so two documents can be joined without renumbering.

To read a format 1 document, register the old assembly-qualified type name as an alias (version 0 is the format 1 layout) and convert it first:

```csharp
var registry = new PackableRegistry.Builder()
    .Add<Crew>("Crew", 0, "Old.Crew, OldAssembly, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null")
    .Build();

var document = new PackedDocument();
if (Format1Converter.TryConvert(oldJson, document, "application/packable+json", registry, out var error)) {
    var outcome = Unpacker.Unpack(document, new Crew(), registry);
}
```
