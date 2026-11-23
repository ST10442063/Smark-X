# Smart-X Part 2 — Video Walkthrough Script

Target length: **12–15 minutes**. Each section says what to **do** and what to **say**, and
which rubric row it earns.

---

## Before you press record

1. **Commit and push**, so GitHub shows the Part 2 history.
2. **Start fresh**, so the seeded data matches this script (state is in memory). Either run
   `.\run-dev.ps1`, or use three terminals:
   ```bash
   dotnet run --project src/SmartX.Api
   ```
   ```bash
   dotnet run --project src/SmartX.DeviceEmulator
   ```
   ```bash
   dotnet run --project src/SmartX.Client
   ```
   Wait until the API log says *"Seeded 17 sensors, 9600 readings"* and *"Loaded 12
   demonstration operator sessions"*, and the emulator prints its first ticks.
3. Arrange the screen so the **emulator window is visible beside the browser**. Its hotkeys,
   and its `downlink: … acknowledged` lines, are part of the story.
4. Have these open, ready to switch to:
   - Browser tab 1: <http://localhost:5090/stream>, zoomed to about 110 %
   - Browser tab 2: <http://localhost:5080/swagger>
   - Browser tab 3: the GitHub repository
   - The IDE with the solution open
5. **Leave Water Pump A off.** The engine never suggests something already in effect.

### Seeded nodes you will point at

| Node | MAC | Why it matters |
|---|---|---|
| Soil Moisture Probe 01 | `A4:CF:12:00:00:00` | **M** crashes it; the learned habit is to switch Pump A on |
| Water Pump A | `A4:CF:12:01:00:01` | Same zone as the probe; switching it on makes the probe recover |
| Lighting Rail Meter 13 | `A4:CF:12:00:0C:BC` | **S** spikes it |
| Grow Light Relay | `A4:CF:12:01:00:03` | Switching it off ends the spike |
| Irrigation Valve 14 | `A4:CF:12:00:0D:E1` | Never reports: one disconnect alert, commands time out |
| Mesh Repeater 08 | `A4:CF:12:00:07:03` | **D** takes it offline |

### Rubric map

| Rubric row | Section |
|---|---|
| Main menu navigation and active pillar state (30) | 2, 9 |
| Advanced collections: stacks and queues (15) | 3, 4, 6 |
| Lookup structures: dictionaries and key-value maps (15) | 5, 7 |
| Unique collections: sets (10) | 8 |
| Predictive action and suggestion engine (30) | 6, 10 |
| GitHub, README | 11 |

---

## 1. What changed since Part 1 — 1 min

**Do:** Show the solution in the IDE.

**Say:**
- "Part 2 turns the ingestion gateway into a real-time processing engine."
- Walk the new parts:
  - `SmartX.Core`: new folders `Streaming`, `Commands`, `Devices`, `Timeline`, `Alerts` and
    `Insights`, one per data structure family.
  - `SmartX.DeviceEmulator`: a separate process standing in for the ESP32 fleet. "It
    references only the contracts project, like firmware would, and every reading reaches the
    gateway as an HTTP request."
  - `SmartX.Api.Tests`: "end-to-end tests that boot the real gateway."

---

## 2. Navigation — 1½ min

**Do:** Start on the Overview page, then click through the top menu.

**Say:**
- "The menu is organised by the three pillars. Ingestion & Telemetry is Part 1, Command Stream
  is Part 2, and Network Topology is visible but disabled until the final PoE."
- Click **Ingestion & Telemetry**. "Each pillar has its own pages underneath: all the Part 1
  pages still work."
- Point at the **Live** chip. "That's the SignalR connection to the gateway, opened once for
  the whole tab. It's showing packets per second, live, on every page."

---

## 3. The queues — 2 min

**Do:** Open **Command Stream**. Point at the two lanes.

**Say:**
- "Every packet the emulator sends goes into a FIFO `Queue<T>`, the standard lane. When a
  packet arrives, the gateway looks the device up in a dictionary and triages it against
  safety bands. A critical reading goes into a `PriorityQueue` instead, which the dispatch
  worker always drains first."
- "The worker spends 20 ms per packet, modelling a Raspberry Pi-class gateway, so we can
  actually see a backlog."

**Do:** Press **F** in the emulator (or **Flood 500**). The standard lane fills. While it
drains, press **S**.

**Say:**
- Read the banner: "The power spike was processed after a few milliseconds, **ahead of** four
  hundred-odd routine packets that arrived before it. The Bypasses tile counts it."
- "Within the priority lane, equal-severity alerts stay in arrival order: the priority is
  severity *and* sequence, because a plain heap isn't stable."

---

## 4. Manual overrides and undo — 2 min

**Do:** In the device grid, switch **Circulation Fan Relay 07**.

**Say:**
- "The switch shows `ON…` with a dashed border. The command is in the device's downlink
  mailbox, which is a queue too, because a device must apply its commands in order."
- Point at the emulator window: "The emulator collected it and acknowledged it. Only the
  acknowledgement makes the switch solid. A pump that's been told to start isn't the same as
  one that has."

**Do:** Press **Shut down all valves & pumps**, then **Undo**.

**Say:**
- "That was one bulk command, pushed onto a `Stack<T>` with every target's previous state.
  Undo pops it and sends compensating commands. Only the valves it actually changed go back;
  one that was already off stays off."
- Point at Irrigation Valve 14 in the command history: "That one is offline, so its command
  never got collected and was marked **TimedOut**."

---

## 5. Dictionaries — 1 min

**Do:** Point at the emulator's tick lines (`lookup 113 ns over 17 devices`).

**Say:**
- "Each packet's device is found in a `Dictionary` keyed by a 48-bit MAC. It's one hash and
  one comparison, the same cost however big the fleet gets."
- "The key isn't a string. It's the MAC as a number, and within one vendor's address block
  its hash can't collide. There's a test proving that over 16,000 addresses."

---

## 6. The predictive engine — 3 min

**Do:** Press **M** in the emulator.

**Say:**
- "Soil Moisture Probe 01 just dropped to 7%. That's below the 20% floor, so it went through
  the priority lane."
- Point at **Suggested actions**: "*Switch ON Water Pump A*. Before I've searched for
  anything, the engine has offered the action, with its evidence: of the six times this has
  happened, counting this one, I switched Pump A on four times, usually within two minutes.
  The pump's row is flagged in the grid."
- "That's sequential pattern mining. Every alert, search and command becomes an event; when
  one event follows another within ten minutes it's counted, giving a confidence and a lift
  for each rule. A rule seen twice counts for less than one seen ten times."

**Do:** Press **Run**.

**Say:**
- "It issued the command, and that answer is learned straight away: the habit is now five
  of six. It's on the undo stack marked *suggested*, the emulator
  acknowledged it, and" (point at the probe's sparkline) "the moisture is climbing because
  its zone is irrigated now. Once it's back above 25%, the alert clears."
- Point at **Automated insights**: "These come from the data itself. *Irrigation Valve 14 is
  offline*, and *Check Mesh Repeater 08*, because when that valve dropped before, I went and
  looked at the repeater."

---

## 7. Timelines — 1 min

**Do:** Click Soil Moisture Probe 01 in the grid; switch the timeline between 1 h and 6 h.

**Say:**
- "Each device's timeline is a `SortedDictionary` of one-minute buckets. A late packet, from
  a node that buffered while it was offline, is inserted in its place, so the graph is always
  in time order without sorting."
- Point at the red mark and the dashed line: "The crash, against the 20% limit."

---

## 8. Sets — 1 min

**Do:** Point at the **Duplicates absorbed** tile, then press **D** in the emulator.

**Say:**
- "Irrigation Valve 14 is found silent on every five-second sweep, but it was reported once.
  The disconnected nodes are a `HashSet`, and each sweep is two set differences: newly down,
  and recovered."
- "I've just taken the mesh repeater offline. In about two minutes it'll be reported, once,
  however long I leave it."
- "Repeat critical readings work the same way: the alert set's `Add` returns false for a
  condition that's already open, so there's one alert, not forty."

---

## 9. State survives navigation — 1 min

**Do:** Filter by **Actuator** and type `pump` in the search. Go to **Anomaly Radar**, then
back to **Command Stream**.

**Say:**
- "The filters are still applied, and the feed kept updating while I was away. The live
  model lives in a service for the whole tab, not in the page. The filters are also in the
  URL, so this view can be bookmarked."
- Press **S** while on the Anomaly Radar: "A critical alert toasts on any page, and the
  Command Stream tab shows a badge."

---

## 10. History and patterns — 1 min

**Do:** Open **History & patterns**.

**Say:**
- "The full event log, filtered on the gateway. It's a reversed `SortedDictionary`, so the
  newest hundred events cost a hundred steps, not the whole log."
- "And everything the engine has learned, with confidence, support and lift. My Run a minute
  ago pushed the Pump A habit higher. Faded rows are still being learned."

---

## 11. Tests, README and GitHub — 1 min

**Do:** Run `dotnet test`; show the README's *Testing the queues, dictionaries and sets*
section; show the commit history on GitHub.

**Say:**
- "282 tests: 273 unit tests over the data structures, and nine end-to-end tests that boot
  the gateway and drive it over HTTP, including a critical packet jumping a 300-packet flood."
- "The README walks through simulating a high-priority alert three ways, and how to see every
  structure work."
