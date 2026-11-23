# Smart-X IoT Mesh — Final Report

**PROG7312 / AAPD7112 — Portfolio of Evidence, Final Part** · ST10442063

> **DRAFT — read this first.** This is a working draft assembled from the project's code,
> commit history and test results, to be rewritten in your own words before it is exported to
> PDF and submitted. The IIE's policies on intellectual integrity (IIE023) and on AI usage
> (IIE015) apply to the submitted report. Every reference below is to a real, well-known
> source, but check each one against the original and your library's Harvard guide before
> submitting, and add the journal articles you used for Part 1's research. Sections in
> *[square brackets]* are prompts for your own reflection.

---

## Contents

1. [Project completion report](#1-project-completion-report)
   1. [What was built](#11-what-was-built)
   2. [Challenges in the final part, and how they were overcome](#12-challenges-in-the-final-part-and-how-they-were-overcome)
   3. [Challenges across the whole project](#13-challenges-across-the-whole-project)
   4. [Key learnings](#14-key-learnings)
2. [Technology recommendations](#2-technology-recommendations)
3. [Reference list](#3-reference-list)

---

## 1. Project completion report

### 1.1 What was built

Smart-X is a three-tier IoT application on .NET 10: an ASP.NET Core gateway API, a Blazor
WebAssembly operator console, and a separate device emulator that stands in for a fleet of
ESP32 microcontrollers and reaches the gateway only over HTTP. Each part of the PoE added one
architectural pillar.

| Part | Pillar | Core data structures |
|---|---|---|
| 1 | Sensor Data Ingestion and Telemetry | `TelemetryPacket<T>` generics, unit-aware operator overloading, a jagged `double[][]` batch store and a rectangular `double[,]` heat matrix, recursive deployment validation, a custom ring buffer |
| 2 | Real-Time Command Stream and History | `Queue<T>` and `PriorityQueue` triage lanes, `Stack<T>` undo/redo, `Dictionary` keyed by a 48-bit MAC, `SortedDictionary` timelines, `HashSet` duplicate suppression, a sequential pattern miner |
| Final | Network Topology and Mesh Routing | A hand-written augmented **AVL tree** per sensor; the mesh as an adjacency-list **graph** weighted by ETX; **BFS** reachability; **DFS** subnets and Tarjan's **articulation points**; an indexed binary **min-heap**; **Prim's** and **Kruskal's** minimum spanning trees with **union-find** |

The finished solution has 382 automated tests (352 unit, 30 end-to-end), builds with zero
warnings, and is delivered with Docker Compose files and a GitHub Actions pipeline.

### 1.2 Challenges in the final part, and how they were overcome

#### Choosing a tree, and making range queries genuinely cheap

The brief allows a BST, AVL or red-black tree. A plain BST was ruled out first: seeded and
live telemetry arrives almost in timestamp order, which is the worst case for an unbalanced
tree — it degenerates into a linked list and every query becomes O(n). .NET's
`SortedDictionary` is a red-black tree, but it exposes neither a range query nor any way to
aggregate a subtree, so using it would have meant iterating — exactly what the brief says to
avoid. I chose to hand-write an AVL tree, because its stricter balance (height at most about
1.44 log₂ n, against 2 log₂ n for red-black) suits a workload that is read far more often than
any single sensor writes (Adelson-Velsky and Landis, 1962; Cormen et al., 2022).

The harder problem was that a range *query* that returns k readings is still O(log n + k).
The dashboard mostly wants the *shape* of a window — count, minimum, maximum, mean — and a
chart of 120 slices. Augmenting every node with its subtree's size, sum, minimum and maximum
turned both into O(log n) operations, independent of how many readings the window holds, and
the subtree sizes also gave rank-based paging. The difficulty was keeping six fields correct
through rotations. I overcame it with an `IsValid` method that checks every invariant, called
by the tests after inserts and trims so a broken rotation fails at the operation that broke it,
and by checking every query against a plain LINQ scan over hundreds of random windows.

*[Reflect: what you would have done had you not tested invariants this way.]*

#### Turning a radio mesh into graph weights

The brief describes vertices and edges but leaves the edge weight open. Using distance alone
would make the minimum spanning tree a purely geometric object with no link to power. I
modelled link quality the way mesh protocols do: received signal strength from the
log-distance path-loss model (Rappaport, 2002), packet delivery ratio from signal strength,
and the **expected transmission count (ETX)** as the cost (De Couto et al., 2003). Because each
transmission costs roughly the same energy, ETX is proportional to the energy spent moving a
packet across the link, which is what makes the MST over these weights a power-efficient
backbone. Choosing an adjacency list over a matrix followed from the sparsity of a mesh: each
node hears only the handful of neighbours within about 70 metres.

#### Designing a demonstration that cannot quietly break

Self-healing is only visible if the mesh has a relay whose loss forces a costlier detour, and
a single point of failure is only visible if one exists. Because the links are derived from
positions, I laid out the site plan deliberately — a cheap bridge to Facility B with a weak
fallback, and a valve at the far end of Facility C in range of a single relay — and then
pinned both scenarios down with end-to-end tests that boot the fully seeded gateway. If a later
change to the cost model or the layout breaks the demonstration, a test fails.

#### When the tests passed but the system was wrong

Two problems only appeared when the whole stack ran together, and both taught me something.

First, the self-healing report showed a *negative* detour cost of −18.9 ETX with nothing
failed. Investigating showed that comparing each node's whole path cost was the wrong measure:
an MST minimises the total, not each path, so removing one node can rewire the tree far away
and swing path costs in both directions. It also showed that the never-reporting valve was
counted in the "all nodes up" baseline. The fix came from asking what the MST actually
optimises. Under per-round aggregation — each node forwards one combined frame to its parent
— a node's energy per round is its own uplink's ETX, and the tree's total is the sum of those
(Heinzelman, Chandrakasan and Balakrishnan, 2000). Costing reroutes by uplink ETX, and building
the baseline only from nodes that had joined, made the report both correct and readable.

Second, with the emulator running, the console showed no critical relays where the design had
one. Noisy neighbour reports had let a pair of nodes at the very edge of range gain a link on
one lucky reading and keep it permanently. Real mesh stacks guard against exactly this with
link-admission thresholds. I added the same hysteresis — a new link must be heard 3 dB above
the floor — and a test for it.

*[Reflect: how running the system, not just the tests, changed your view of "done".]*

#### Integrating the C# backend with the Blazor frontend

Sharing only a contracts project between the API, the console and the emulator kept the
tiers honest, but it meant every new view needed new DTOs and mapping code. Drawing the mesh
was the main front-end challenge: rather than add a JavaScript graph library, I drew the site
plan as inline SVG from C#, as the earlier parts' charts were, scaling metre coordinates onto
a fixed drawing. Two Razor pitfalls cost time: a variable named `page` collides with the
`@page` directive, and a pattern variable in a top-level `@if` leaks into the whole render
method. Keeping selection state in a scoped `TopologyState` service meant the selected node
and history window survive navigation between pillars, which is the rubric's "preserves
application states" criterion.

### 1.3 Challenges across the whole project

| Part | Challenge | How it was overcome |
|---|---|---|
| 1 | Handling float, int and bool telemetry through one pipeline without boxing | A generic `TelemetryPacket<T>` constrained to value types, with a non-generic interface for storage |
| 1 | Recursive validation of deep deployment profiles without stack overflow | Explicit depth and cycle guards; raised ASP.NET's JSON depth limits so the validator, not model binding, reports over-deep profiles |
| 1 | Uploading attachments securely without buffering whole files | Streamed AES-256-GCM in 64 KB chunks, with the key stored apart from the ciphertext |
| 2 | Showing that critical packets really bypass the FIFO queue | A per-packet processing budget so a backlog forms; recording how many packets each alert overtook |
| 2 | `PriorityQueue` is not stable, so equal-severity alerts could reorder | Made the arrival sequence part of the priority |
| 2 | Two singletons with their own locks needing each other's data | Never holding both locks; raising events outside locks — a rule the final part's topology service follows too |
| 2 | Making simulated data "hit the API like a device" | Replaced the in-process simulator with an emulator that uses only the HTTP contracts |
| Final | See section 1.2 | |

### 1.4 Key learnings

- **Choose a structure by its workload, then measure it.** Each structure in the project was
  chosen for a specific access pattern, and each is backed by a test that measures the work it
  does — comparisons for Part 1's binary search, lookup nanoseconds for Part 2's dictionary,
  nodes visited for the final AVL tree. Reporting the cost in the UI made the efficiency
  visible rather than asserted.
- **Augmenting a structure can change its complexity class.** Storing four extra numbers per
  AVL node turned window statistics from O(k) into O(log n). This was the most transferable
  technique I learned.
- **Know what an algorithm optimises before measuring it.** The MST minimises total cost, not
  each path; misreading that produced a misleading healing report. The console now shows the
  tree route beside BFS's fewest-hops route so the trade-off is explicit.
- **Prove algorithms against brute force and against each other.** Tarjan's algorithm was
  checked against removing every vertex in turn, and Prim against Kruskal and against an
  exhaustive search of small graphs — two independent implementations agreeing is strong
  evidence (Prim, 1957; Kruskal, 1956; Tarjan, 1972).
- **Recursion where depth is bounded, iteration where it is not.** The AVL tree recurses
  because its height is logarithmic; graph searches use explicit stacks because a chain of
  relays can be as long as the mesh.
- **Run the whole system.** Three of the final part's fixes were found only by running the
  API, emulator and console together.
- *[Add: new skills — e.g. Blazor component design, SignalR, xUnit with WebApplicationFactory,
  Docker Compose, GitHub Actions — and how each one was used in this project.]*

---

## 2. Technology recommendations

Each recommendation is chosen to fix a limit that the current design states openly in the
README, and each fits the existing .NET 10 stack.

### 2.1 MQTT for device traffic — e.g. EMQX or Eclipse Mosquitto with MQTTnet

**Gap.** Devices publish over HTTP and poll for commands once a second. Polling wastes radio
time and battery and adds up to a second of command latency.
**Recommendation.** An MQTT broker, with the gateway subscribing through the MQTTnet library.
Telemetry, neighbour tables and commands become topics (`smartx/{mac}/telemetry`,
`smartx/{mac}/cmd`), and MQTT's last-will messages give instant disconnect detection instead
of waiting two heartbeat windows.
**Fit with .NET.** MQTTnet is a mature .NET library; the current uplink and downlink contracts
map directly onto message payloads, so `UplinkService` and `CommandService` keep their logic.

### 2.2 A time-series database for history — e.g. TimescaleDB or Azure Data Explorer

**Gap.** History is in memory, kept for 48 hours, and lost on restart.
**Recommendation.** TimescaleDB (PostgreSQL with time-partitioned hypertables and continuous
aggregates) for long-term storage. The AVL trees remain as the hot in-memory index for recent
data — the access pattern they serve well — while continuous aggregates provide the
long-range equivalents of `Summarise`.
**Fit with .NET.** Npgsql and Entity Framework Core support PostgreSQL directly.

### 2.3 Managed IoT ingestion — Azure IoT Hub or Event Hubs

**Gap.** One gateway instance; per-device identity is only a MAC address.
**Recommendation.** Azure IoT Hub for per-device credentials, device twins (which formalise
Part 2's desired/reported state) and cloud-to-device commands; Event Hubs to absorb bursts
before processing.
**Fit with .NET.** First-party Azure SDKs; the gateway becomes one consumer among several.

### 2.4 Horizontal scale — Redis and a SignalR backplane

**Gap.** State lives in in-process singletons, so the API cannot run as several instances.
**Recommendation.** Redis for shared device state and the SignalR Redis backplane (or Azure
SignalR Service) so every instance can push to every console.
**Fit with .NET.** `Microsoft.AspNetCore.SignalR.StackExchangeRedis` is a one-line registration.

### 2.5 CI/CD — GitHub Actions (implemented) extended to delivery

**Done.** The repository's workflow builds in Release with warnings as errors, runs all 382
tests and builds the Docker images on every push.
**Next.** Push images to GitHub Container Registry on `main`, and deploy to Azure Container
Apps with the compose services as separate apps.

### 2.6 Observability — OpenTelemetry with Prometheus and Grafana

**Gap.** Queue depths, lookup times and tree costs are shown in the console but not recorded.
**Recommendation.** OpenTelemetry metrics and traces from the API, scraped by Prometheus and
graphed in Grafana, with alerts on queue depth and stranded-node count.
**Fit with .NET.** ASP.NET Core emits OpenTelemetry natively via `System.Diagnostics.Metrics`.

### 2.7 Shortest-path routing beside the MST — RPL-style routing

**Gap.** The MST minimises total energy but can give some nodes long paths (Valve 06 is ten
tree hops from the gateway, four by fewest hops).
**Recommendation.** Add a Dijkstra shortest-path tree by ETX — the approach of RPL, the
routing protocol used by Thread (Winter et al., 2012) — as a third `SpanningTreeResult`
builder, and let operators choose energy-optimal or latency-optimal routing per zone. Add
neighbour-entry ageing so links that stop being heard expire.
**Fit.** The graph, heap and result types are already in place.

### 2.8 Home Assistant integration

**Recommendation.** Publish Smart-X devices to Home Assistant through MQTT discovery so
facility staff can use its dashboards, automations and mobile app alongside the operator
console.
**Fit.** Follows from recommendation 2.1 at little extra cost.

### 2.9 An LLM daily alert digest

**Recommendation.** A scheduled job that sends the day's alert history, healing events and
learned patterns to a large-language-model API and emails operators a short plain-language
summary. The prompt should contain only aggregated, non-sensitive data.
**Fit.** A .NET background service calling the provider's REST API; the event log and pattern
miner already hold the inputs.

### 2.10 Security hardening

**Gap.** The API has no authentication.
**Recommendation.** Mutual TLS or per-device tokens on the device endpoints; Microsoft Entra
ID (OpenID Connect) for the console; ASP.NET Core rate limiting on the uplink.
**Fit.** All built into ASP.NET Core.

---

## 3. Reference list

*Harvard style. Verify each entry, and add the journal articles from your Part 1 research.*

Adelson-Velsky, G.M. and Landis, E.M. (1962) 'An algorithm for the organization of
information', *Soviet Mathematics Doklady*, 3, pp. 1259–1263.

Cormen, T.H., Leiserson, C.E., Rivest, R.L. and Stein, C. (2022) *Introduction to
algorithms*. 4th edn. Cambridge, MA: MIT Press.

De Couto, D.S.J., Aguayo, D., Bicket, J. and Morris, R. (2003) 'A high-throughput path metric
for multi-hop wireless routing', in *Proceedings of the 9th Annual International Conference
on Mobile Computing and Networking (MobiCom '03)*. San Diego, CA: ACM, pp. 134–146.

Heinzelman, W.R., Chandrakasan, A. and Balakrishnan, H. (2000) 'Energy-efficient
communication protocol for wireless microsensor networks', in *Proceedings of the 33rd Annual
Hawaii International Conference on System Sciences*. Maui, HI: IEEE.

Kruskal, J.B. (1956) 'On the shortest spanning subtree of a graph and the traveling salesman
problem', *Proceedings of the American Mathematical Society*, 7(1), pp. 48–50.

Prim, R.C. (1957) 'Shortest connection networks and some generalizations', *Bell System
Technical Journal*, 36(6), pp. 1389–1401.

Rappaport, T.S. (2002) *Wireless communications: principles and practice*. 2nd edn. Upper
Saddle River, NJ: Prentice Hall.

Tarjan, R. (1972) 'Depth-first search and linear graph algorithms', *SIAM Journal on
Computing*, 1(2), pp. 146–160.

Tarjan, R.E. (1975) 'Efficiency of a good but not linear set union algorithm', *Journal of
the ACM*, 22(2), pp. 215–225.

Winter, T., Thubert, P., Brandt, A., Hui, J., Kelsey, R., Levis, P., Pister, K., Struik, R.,
Vasseur, J.P. and Alexander, R. (2012) *RPL: IPv6 routing protocol for low-power and lossy
networks*. RFC 6550. Internet Engineering Task Force.
