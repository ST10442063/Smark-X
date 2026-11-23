# Final PoE demo — video script

About 12 minutes. Each section lists what to show and what to say; the rubric line it serves
is in brackets.

## Before you press record

1. `dotnet test` — confirm **382 passed**. Keep the terminal for section 9.
2. Start the stack with `.\run-dev.ps1` (the emulator window must have hotkeys). Wait until
   the API log says *"Seeded 17 sensors, 23040 readings"*.
3. Open <http://localhost:5090/topology> and let it load once.
4. Open Swagger at <http://localhost:5080/swagger> in a second tab.

### Seeded nodes you will use

| Node | MAC |
|---|---|
| Mesh Repeater 08 | `A4:CF:12:00:07:03` |
| Pump Feed Meter 12 | `A4:CF:12:00:0B:97` |
| Irrigation Valve 06 | `A4:CF:12:00:05:B9` |
| Soil Moisture Probe 01 | `A4:CF:12:00:00:00` |

### Rubric map

| Rubric criterion | Sections |
|---|---|
| Tree structures and historical queries (30) | 6 |
| Graphs, traversals and MST (30) | 2, 3, 4, 5 |
| README and structure explanations (10) | 9 |
| Main menu integration and architecture health (5) | 1, 8 |
| Feedback ledger (5) | 9 |

---

## 1. All three pillars (0:00–1:00) [menu integration]

**Show:** the home page; click through Ingestion & Telemetry, Command Stream, Network Topology.
**Say:** "All three pillars are now live under one menu. The topology pillar is the final
part: the mesh as a graph, routing by minimum spanning tree, and history from an AVL tree."

## 2. The mesh as a graph (1:00–2:30) [graphs]

**Show:** the topology page tiles and map; hover a node and a link.
**Say:** "Every ESP32 is a vertex on the site plan; every wireless link is an edge in an
adjacency list keyed by MAC. A link's weight is its ETX — the expected transmissions per
packet, from a path-loss radio model — so weights are proportional to energy. The emulator
refines them with neighbour tables every 30 seconds. The teal lines are the minimum spanning
tree; the amber ring is a critical relay."

## 3. Route tracing by MAC and BFS (2:30–4:00) [traversal, routing]

**Show:** type `A4:CF:12:00:05:B9` → Trace. Scroll the hop list; point at the fewest-hops
comparison; show the Reachability rings card.
**Say:** "The tree route is ten hops; breadth-first search finds four. The MST minimises the
*total* energy of a collection round, where each node forwards once to its parent; BFS
minimises hops. The console shows both so the trade-off is visible. BFS also gives every
node's hop count — these rings."

## 4. Self-healing (4:00–5:30) [MST, where to hop]

**Show:** select Mesh Repeater 08 → Simulate failure. Point at the dashed red links, the new
tree link across the gap, *Stranded 0*, Meter 04's new amber ring, and the Self-healing card.
Then Restore.
**Say:** "Six nodes depended on the repeater; all six found another way. Meter 04 now hops via
Canopy Humidity 02 at plus 1.89 ETX a round, and has become the new single point of failure."

## 5. A critical relay and a stranded node (5:30–6:30) [DFS, down nodes]

**Show:** select Pump Feed Meter 12: the warning and its DFS subnet. Simulate failure; show
*Stranded 1* and Valve 06's trace "blocked by Pump Feed Meter 12". Restore.
**Say:** "Tarjan's depth-first search finds articulation points. Meter 12 is one: lose it and
Valve 06 is up but cut off — stranded, which is different from down."
**Optional:** press **D** in the emulator and return to the page about two minutes later to
show a real outage healing by itself.

## 6. History by time window — the AVL tree (6:30–8:30) [trees]

**Show:** Telemetry history → Soil Moisture Probe 01 → Custom → 14:00–16:00. Point at the
summary badge, the chart, the hour-grouped log, then Last page and the pager badge.
**Say:** "Every reading is in a per-sensor AVL tree keyed by timestamp. This window is found by
walking down the tree. The count, min, max and mean come from aggregates stored in each node,
so the summary visited 18 nodes, not the 120 readings. The last page is reached by rank, at
the same cost as the first — a list scan would read all 1,400-odd readings."

## 7. Code walkthrough (8:30–10:30)

**Show, briefly:** `TelemetryAvlTree.cs` (`Refresh`, `SummariseAtOrAfter`, `Rebalance`);
`MeshGraph.cs` (the two dictionaries); `MeshTraversal.BreadthFirst` and `ArticulationPoints`;
`IndexedMinHeap.TryDecrease`; `PrimMst.Build`; `KruskalMst.Build`.
**Say:** one sentence each on *why* — AVL for read-heavy balance, adjacency list for a sparse
mesh, the indexed heap for decrease-key, Kruskal as an independent cross-check.

## 8. API and state across navigation (10:30–11:15) [architecture health]

**Show:** in Swagger, `GET /api/topology?algorithm=Kruskal` — same tree cost as Prim; then
`GET /api/history/{mac}/series`. In the console, select a node, go to Command Stream and back:
the selection is still there.

## 9. Tests, README, ledger, GitHub (11:15–12:00) [documentation, ledger]

**Show:** `dotnet test` output; the README's *Final-PoE data structures in depth* section;
`docs/FEEDBACK-LEDGER.md`; the GitHub commit history and the Actions run.
**Say:** "382 tests, including Prim and Kruskal checked against brute force and against each
other, and the healing scenarios end to end."

## Closing

"That completes Smart-X: ingestion, a real-time command stream, and a self-healing mesh with
power-efficient routing — all on .NET 10 with a Blazor console."
