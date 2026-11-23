# Smart-X — Update Ledger

**PROG7312 / AAPD7112 — Portfolio of Evidence, Final Part** · ST10442063

The brief asks for "a file listing the updates you have made based on feedback from Parts 1
and 2". **No written lecturer feedback was received for Parts 1 or 2**, so this ledger records
the updates made from my own review of the earlier parts: defects found by testing and by
running the full stack, inefficiencies found by re-reading the code against the data-structure
requirements, and changes the next part's requirements forced on earlier code. If lecturer
feedback arrives before submission, it should be added as rows in section 1 with the comment
quoted in the *Source* column.

Each row names what was wrong, what changed, and the evidence that it is fixed — a test, or a
commit in this repository. Rows from Parts 1 and 2 predate this repository (they were made in
the Part 2 repository), so their evidence is the test that now guards them.

**Key to *Source*:** *Review* = found by reading the code; *Test* = found by a failing or new
test; *Run* = found by running the whole stack; *Brief* = required by the next part's brief.

---

## 1. Updates made to Part 1 code (carried out in Part 2)

| ID | Area | Source | Issue | Change | Evidence |
|---|---|---|---|---|---|
| P1-01 | `SortedSensorIndex.FindById` | Review | Resolving a sensor by id was a linear scan, and `GatewayStore.Accept` does it on every packet — O(n) per packet, growing with the fleet. | Backed by a `Dictionary<Guid, SensorRecord>` alongside the sorted list: O(1). | `SortedSensorIndexTests` |
| P1-02 | `GatewayStore.TryRaiseDisconnect` | Review | Each health sweep scanned every anomaly ever raised to see whether an outage was already reported, a cost that grew with uptime. | A `HashSet<(SensorId, OutageStart)>`; `Add` is both the check and the record. | Code: `GatewayStore.TryRaiseDisconnect` (the same pattern in Part 2's `OutageTracker` is covered by `OutageTrackerTests`) |
| P1-03 | `GatewayStore` events | Review | The live registry needed to hear about registrations; raising the event inside the store's lock risked a deadlock against a listener's own lock. | `SensorRegistered` is raised after the lock is released. The final PoE's topology service relies on the same guarantee. | `RegisteredNode_JoinsTheMeshNearItsFacility` |
| P1-04 | `MeshSimulatorService` | Brief | Part 1 simulated telemetry inside the API process, but Part 2 requires simulated data to "hit the API like it was a device". | Removed in favour of the separate `SmartX.DeviceEmulator` over HTTP. | `src/SmartX.DeviceEmulator` |
| P1-05 | Telemetry workbench | Review | The workbench wrote straight to storage, a second ingestion path that could disagree with the live stream about a node. | Routed through the shared `TelemetryPipeline`. | Code: `TelemetryController`, `TelemetryPipeline` |
| P1-06 | Navigation | Brief | Flat navigation did not show the three pillars. | Reorganised by pillar with sub-navigation; every Part 1 route kept. | `MainLayout.razor` |

## 2. Updates made to Parts 1 and 2 code in the final PoE

| ID | Area | Source | Issue | Change | Evidence |
|---|---|---|---|---|---|
| F-01 | History storage (Part 1 `HistoricalBatchStore`, Part 2 `SensorTimeline`) | Brief | Neither store could answer a time-window query efficiently: the jagged store is in arrival order (O(n) scan), and the timeline's `Range` walks from the start of its tree and keeps only one-minute buckets. | Every reading now also goes into a per-sensor augmented **AVL tree** inside `GatewayStore.Accept`, so the seed, direct and queued paths all feed it. | `f6942ff`, `a644848`; `TelemetryAvlTreeTests`, `SensorHistoryIndexTests` |
| F-02 | Seed depth (`Gateway:SeedReadingsPerSensor`) | Review | 600 readings at one a minute is ten hours of history; a fixed clock window such as 14:00–16:00 was empty for much of the day. | Raised to 1,440 (24 hours). README counts updated. | `a644848`; `HistoryApiTests` |
| F-03 | Retention | Review | A new store needs a bound, or a long-running gateway grows without limit. | `Gateway:HistoryRetentionHours` (48), measured from each sensor's newest reading; readings already outside the window are not inserted at all. | `a644848`; `Append_TrimsBehindTheNewestReading`, `Append_ReadingOlderThanTheWindow_IsDiscarded` |
| F-04 | Device manifest (Part 2) | Brief | The emulator had no notion of position, so it could not model radio range or report neighbours. | `ManifestDeviceResponse` carries each device's site-plan position, and the manifest the gateway's. | `7308adb`, `514fc20` |
| F-05 | Device emulator (Part 2) | Brief | Link weights should come from devices, like all simulated data. | Every sixth tick each online node posts its neighbour table with modelled RSSI plus Gaussian shadowing. | `514fc20`; `NeighbourReport_RefinesTheLinkSignal` |
| F-06 | Test fixture (Part 2 `GatewayFactory`) | Test | The fixture always started unseeded, so the topology of the real seeded site could not be tested end to end. | Optional `seed` parameter. | `17138a8`; `TopologyApiTests` |
| F-07 | Navigation and home page | Brief | Pillar 3 was a disabled placeholder; the footer still said "Part 2". | Pillar, menu entry and sub-navigation enabled; footer updated; sensor pages cross-link to the map and history. | `bcbf24f`, `ef413d0` |

## 3. Defects found and fixed while building the final PoE

| ID | Area | Source | Issue | Change | Evidence |
|---|---|---|---|---|---|
| D-01 | AVL summary test | Test | `Summarise_MatchesLinqAggregates` failed: two means differed by 3×10⁻¹⁴ but rounded differently at six decimal places, because the tree adds the same values in a different order. | Compared with an absolute tolerance, with a comment explaining why. | `261bc4f` |
| D-02 | Self-healing report | Test, Run | Two seeded-site scenario tests failed, and running the site showed a detour cost of −18.9 ETX with nothing failed: (a) reroutes were costed by whole-path ETX, which swings with every rewiring further up the tree; (b) the never-reporting Valve 14 was counted in the "all up" baseline, so the mesh always looked rerouted. | (a) A reroute is costed by the node's own **uplink** ETX — its energy per round, the quantity the MST minimises; (b) the baseline includes only nodes that have ever joined. The repeater scenario now reads one reroute at +1.89 ETX, and both tests pass. | `c609890`, `17138a8`; `FailingTheRepeater_ReroutesFacilityBAtACost`, `FailingMeter12_StrandsValve06` |
| D-03 | Neighbour discovery | Run | With the emulator running, the console showed **0** critical relays where the design has one. Noisy reports let an edge-of-range pair (Valve 06 ↔ EC Probe 11, ~−91 dBm on average) gain a link on one lucky reading and keep it, giving Valve 06 a second route. | Devices list only neighbours whose *expected* signal clears the floor, and the gateway admits a *new* link only 3 dB above the floor (admission hysteresis). | `269141c`; `NeighbourReport_NewLinkNeedsMarginAboveTheFloor` |
| D-04 | Mesh map layout | Run | In the browser the map was cramped in the left column and Facility A's labels overlapped. | Map card moved to full width; non-tree links fainter; labels larger. | `0164927` |
| D-05 | Razor naming | Test (build) | A loop variable named `page` collided with the `@page` directive, and a pattern variable leaked across the render method. | Renamed (`view`, `picked`). | `15e4aa0`, `bcbf24f` |
| D-06 | Topology deep links | Run | In a full end-to-end walkthrough, changing `?mac=` while the topology page was open (Back/Forward, or a link to another node) kept the old node selected, so *Simulate failure* acted on the wrong node. Blazor reuses the page when only the query string changes, and the page read `?mac=` only on first load. | Both topology pages now pick up a changed `?mac=` in `OnParametersSetAsync`. | `1d7389b` |

## 4. Process improvements

| ID | Area | Change | Evidence |
|---|---|---|---|
| X-01 | Continuous integration | GitHub Actions builds in Release with warnings as errors, runs all tests, and builds the Docker images on every push. | `41fe194` |
| X-02 | Commit history | Work committed in small, single-purpose commits with descriptive messages, so each data structure and fix can be traced. | `git log` |
| X-03 | Documentation | README rewritten for the complete application, with a requirement-to-file map and in-depth structure explanations. | `899cc5c` |
| X-04 | Test coverage | From 282 tests at the end of Part 2 to 382: AVL invariants and queries against LINQ, graph algorithms against brute force and each other, and end-to-end topology scenarios. | `dotnet test` |
