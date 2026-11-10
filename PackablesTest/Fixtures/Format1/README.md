# Format 1 fixtures

Documents written by the format 1 Packer, or derived from its output by hand. They stay as input for
the format 1 to 3 converter after format 1 is no longer written. The bytes are exact: `.gitattributes`
keeps their `\n` line endings.

| File | Graph | Read back as |
| --- | --- | --- |
| scalars | One `Scalars` object | `i` -7, `l` 2^40, `d` 0.1, `flag` true, `negativeZero` -0.0. `nan`, `positiveInfinity` and `negativeInfinity` are stored as the strings "NaN", "Infinity" and "-Infinity". `text` has a non-ASCII letter and quotes, `empty` is "", `none` is null. |
| nested | One `Vectors` object | `points`: b [1, 2, 3] then a [4.5, 5, 6, 7]. `counts`: d 4, b 2, c 3, the order a `Dictionary` gives after a removal (neither insertion nor sorted order). |
| shared | A `Pair` whose two refs point to one `Node` (7) | Both refs give the same instance. |
| loop | `Node` 1, then 2, then back to 1 | The loop closes on the same instance. |
| chain12 | `Node`s 1 to 12, each pointing to the next | Values 1 to 12. Deeper than the old 8-level Packer could write. |
| map-of-refs | A `Table` from strings to `Node`s: id-2 to Node 2, id-1 and id-3 to one Node 1 | Keys in that order, with id-3 the same instance as id-1. |
| dangling | A chain of 9 with `o:9` removed from `objects` | 1 to 8, then a `Node` with value 0: format 1 readers make an empty object for a missing id. |
| unresolved-type | `shared`, with `Node`'s type named in an assembly that doesn't exist | The root loads, and both refs come back null. |

The types are in `PackablesTest/Format1FixtureTests.cs`. While the format 1 Packer exists, run the
tests with `UPDATE_FIXTURES=1` to regenerate.
