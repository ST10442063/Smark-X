# Smart-X Part 1 — Video Walkthrough Script

Target length: **12–15 minutes**. Each section says what to **do** and what to **say**,
and which rubric row it earns. Everything below was checked against the running app.

---

## Before you press record

1. **Commit and push** so GitHub shows the .NET 10, encryption and Blazor work.
2. **Start fresh** so the seeded data matches this script (state is in memory):
   ```bash
   dotnet run --project src/SmartX.Api
   ```
   ```bash
   dotnet run --project src/SmartX.Client
   ```
   Wait until the API log says *"Seeded 14 sensors, 7800 readings across 90 batches"*.
3. Open these, ready to switch to:
   - Browser tab 1: <http://localhost:5090> (the console) — zoom to 125 % so text is readable
   - Browser tab 2: <http://localhost:5080/swagger>
   - Browser tab 3: your GitHub repo
   - VS Code / Visual Studio with the solution open
   - File Explorer at `src\SmartX.Api\App_Data\attachments`
   - File Explorer at the `demo` folder (files to upload and paste)
4. Don't rebuild while the client is running — stop it first, or the page sticks on
   "Starting the Smart-X console…".

### Seeded data you will point at

| Node | MAC | Why it matters |
|---|---|---|
| Irrigation Valve 14 | `A4:CF:12:00:0D:E1` | Registered but **silent** — grey strip, Disconnect alert |
| Lighting Rail Meter 13 | `A4:CF:12:00:0C:BC` | **Spike** — about 2 600 W |
| Soil Moisture Probe 01 | `A4:CF:12:00:00:00` | **Drop** — about 7 % |
| Pump Feed Meter 04 / Lighting Rail Meter 05 | `A4:CF:12:00:03:6F` / `A4:CF:12:00:04:94` | Used for `Meter3 = Meter1 + Meter2` |

### Rubric map

| Rubric row | Section |
|---|---|
| Gateway startup layout and API integration | 1, 2, 10 |
| Sensor telemetry ingestion and UI | 4, 5, 7 |
| Advanced OOP: generics and overloading | 7, 9, 11 |
| Data handling: arrays and recursion | 7, 8, 11 |
| Media / log file upload (incl. encryption) | 6, 11 |
| Dynamic dashboard engagement | 3 |
| UI design | throughout, 10 |
| Collections optimisation (custom structure) | 4, 7, 11 |
| Documentation, README, GitHub | 12 |

---

## 1. Introduction and architecture — 1 min

**Do:** Show the solution in the IDE. Open `Directory.Build.props`.

**Say:**
- "This is Smart-X, an IoT data ingestion gateway for ESP32 sensor meshes."
- "It's the Web-First stack from the brief: an ASP.NET Core Web API on **.NET 10** — see
  `net10.0` here — with a **Blazor WebAssembly** frontend."
- Walk the projects:
  - `SmartX.Core` — domain model and every data structure the brief asks for.
  - `SmartX.Contracts` — the request/response DTOs. It has **no project references**, so
    the frontend physically cannot reach the gateway internals; it can only use HTTP.
  - `SmartX.Api` — the gateway.
  - `SmartX.Client` — the Blazor console, C# compiled to WebAssembly running in the browser.
  - `SmartX.Core.Tests` — 155 unit tests.

---

## 2. Startup interface — 1 min

**Do:** Show the two terminals running, then tab 1, the **Overview** page.

**Say:**
- "The API seeds 14 mock sensors and 7 800 readings on startup, then keeps publishing every
  five seconds like a live mesh, so the system is always under load."
- "The landing page offers the three architectural pillars. **Sensor Data Ingestion and
  Telemetry** is active. **Real-Time Command Stream** and **Network Topology** are shown but
  disabled — their buttons can't be clicked — because they're Parts 2 and 3."
- Point at **Gateway vitals**: "This is live data from the API: health score, registered
  nodes, readings held, open alerts, and one silent node."

---

## 3. Engagement feature — Anomaly Radar — 2½ min

**Do:** Click **Anomaly Radar** in the nav.

**Say (the strategy, from your research):**
- "My engagement strategy is real-time visual telemetry feedback with an acknowledgement
  loop — the Anomaly Radar. It answers the operator's real question: *which node do I look
  at first?*"

**Do / say, top to bottom:**
1. **Score ring** — "One health score out of 100. It's a ring, not a progress bar, because
   it moves in both directions. The headline tells me what to do first."
2. **Heartbeat strips** — "Every node is a strip of its last 40 readings. Bar height is the
   value; colour is how far that reading is from the node's own rolling average — amber past
   two standard deviations, red past three."
   - Point at **Irrigation Valve 14**: "A silent node goes flat and grey — a dropout looks
     completely different from a spike, so you can't confuse them."
   - Point at a red bar on **Lighting Rail Meter 13**: "That's a power spike."
3. **Triage queue** — "Worst first, each explained in plain language, not just a number."
4. **Watch it update** — "It refreshes every three seconds without reloading the page —
   watch *Readings held* at the bottom climb." Wait a few seconds.
5. **Acknowledge** all three alerts, one at a time. "Clearing an alert raises the score, so
   the score only improves when someone has actually dealt with a problem." After the third,
   the **Inbox Zero** badge turns green with a toast.
   - "Acknowledging the silent valve doesn't make it re-alert every five seconds — it alerts
     once per outage — but the strip stays grey because the node is still down."
6. **Badges** — "Four operator badges reinforce the same loop."
7. Scroll to **Ingestion pipeline** — "Ring-buffer occupancy, overwritten packets as a
   back-pressure signal, aggregate power draw computed with my overloaded `+` operator, and
   a zone-by-hour heat grid from a two-dimensional array. An empty cell is a dot, not zero,
   because *no reading* isn't the same as *a reading of zero*."
8. Click a node name on a strip — "Every node links straight to its profile."

---

## 4. Sensor registry and search — 1 min

**Do:** Click **Sensors**.

**Say:** "The registry is kept sorted by MAC address, which gives two search strategies."

1. Type `Pump` → **Search**. "Free text has to use a linear scan — it examined **14 of 14**
   nodes."
2. Type `a4-cf-12-00-0c-bc` → **Search**. "A MAC address uses **binary search** over the
   sorted list — O(log n) instead of O(n). Notice I typed lower-case with hyphens; it's
   normalised."
3. Press the browser **Back** button. "The search lives in the URL, so Back returns to the
   previous results."
4. Click **Clear**.

---

## 5. Registration and validation — 2 min

**Do:** Click **Register a node**.

1. Click **Register node** with the form empty.
   **Say:** "Validation runs in the browser first. These are the *same* DataAnnotations the
   API enforces, because the form is bound to the shared request DTO — client and server
   rules can't disagree."
2. Enter bad values: MAC `ZZ:12`, Display name `ab`, Firmware `v1`, Heartbeat `0`.
   Click **Register node** — each field shows its own message.
3. Fix them one by one — "each message clears as soon as I fix that field."
4. Fill in a valid node:
   - MAC: `a4cf.129b.0e77`  *(dotted form)*
   - Display name: `Hydro Bay 3 — Pump Meter`
   - Category: **Power Consumption** — "the unit pre-fills to **Watt**"
   - Facility `Facility A`, Zone `Zone 1`, Sub-zone `Sub-Zone B`, Node ID `NODE-15`
   - Firmware `1.4.2`, Heartbeat `30`
5. Click **Register node**.
   **Say:** "It posts to the API, shows a toast, and opens the new profile. The MAC was
   normalised to `A4:CF:12:9B:0E:77`, and the deployment location is shown as a path."
6. Go back to **Sensors → Register a node** and register the same MAC in another format:
   `A4-CF-12-9B-0E-77` (fill the other required fields with anything).
   **Say:** "The API returns **409 Conflict**, and the message lands on the MAC field
   itself, not a generic error box."

---

## 6. File attachments with encryption — 2 min

**Do:** Open the profile of the node you just registered (Sensors → Hydro Bay 3).

1. Under **Configuration and log files**, choose `demo\node-07-boot.log` → **Attach file**.
   **Say:** "This uploads as multipart form data. The row shows **🔒 AES-256-GCM** and the
   SHA-256 of the original file."
2. Attach `demo\deployment-photo.png` — "photos work too; it gets an image icon."
3. Attach `demo\not-really-a-photo.png`.
   **Say:** "This is an executable renamed to `.png`. The API checks the file's magic bytes,
   not just the extension, and refuses it."
4. Mention: "Files over 10 MB are refused in the browser before a byte is sent, and
   extensions outside the allow-list are rejected."
5. Switch to **File Explorer** at `src\SmartX.Api\App_Data\attachments`. Open the newest
   `.enc` file in Notepad.
   **Say:** "On disk it's unreadable — only the `SXE1` header is plain. The log contained a
   Wi-Fi password and MQTT credentials, and neither is stored in the clear."
6. Back in the console, click **Open** on the log.
   **Say:** "The gateway decrypts it on the way out, and I get the original back."
7. **Say (the design):** "It encrypts while streaming, in 64 KB AES-GCM chunks, so a 10 MB
   upload never sits in memory. GCM also authenticates, so a tampered file won't decrypt,
   and chunks can't be reordered or cut off. The key is kept in a separate folder from the
   files, or supplied through configuration in production."

---

## 7. Telemetry workbench — generics, arrays, collections — 2 min

**Do:** On the profile, click **Publish telemetry for this node**.

1. Point at the hint **TelemetryPacket&lt;Int32&gt;**.
   **Say:** "A power meter publishes integers, so the reading is wrapped as
   `TelemetryPacket<int>`."
2. Click **Publish reading** (value `420`) — the toast confirms the packet type and ring
   buffer occupancy.
3. Change **Target node** to *Soil Moisture Probe 01* → hint becomes
   **TelemetryPacket&lt;Single&gt;**. Change to *Irrigation Valve 06* →
   **TelemetryPacket&lt;Boolean&gt;**.
   **Say:** "One generic class handles float, int and bool without boxing — each value stays
   in a strongly typed field."
4. Change back to **Soil Moisture Probe 01**. Under **Batch flush**, keep the default six
   readings → **Publish batch**.
   **Say:** "A batch is stored as one row of a **jagged array**, sized to exactly its
   length." Point at **Jagged batch shape** and the line saying how many slots a rectangular
   array would have wasted.
5. Type `abc` as the single value → **Publish reading** → "rejected: can't be read as a
   number."
6. Under **Generate under load**, set count `600`, tick **Inject an anomaly** →
   **Generate readings**.
   **Say:** "600 readings into a 512-slot ring buffer — my custom `IngestionBuffer<T>`. It
   overwrites the oldest and counts what it overwrote, as a back-pressure signal." Point at
   *overwritten* in the toast.
7. Point at the **red dot** on the sparkline — "the injected anomaly."
8. Point at **Overloaded operators** — "`latest + previous` and `latest − previous` are the
   overloaded `+` and `−` operating on real readings, with units carried through."
9. Click **Anomaly Radar** — the new alert for Soil Moisture Probe 01 is in the triage queue.

---

## 8. Deployment validation — recursion — 1½ min

**Do:** Click **Deployment**.

1. Click **Validate profile** on the pre-filled example.
   **Say:** "A deployment profile nests Facility → Zone → Sub-Zone → Device. The API walks it
   with a **recursive** validator. It's valid; the table shows each device's full path, its
   depth, and the heartbeat it **inherited** from the nearest ancestor that set one."
2. Paste the contents of `demo\broken-profile.json` → **Validate profile**.
   **Say:** "Three findings: `TIER_ORDER` — a Facility inside a Zone; `DEVICE_NO_MAC`; and
   `DUPLICATE_ID`."
3. Paste `demo\too-deep-profile.json` (40 nested sub-zones) → **Validate profile**.
   **Say:** "Recursion needs a base case *and* a guard. At 32 tiers it reports `MAX_DEPTH`
   instead of overflowing the stack and crashing the API. There's also a cycle guard."
4. Type something invalid like `{ not json` → **Validate profile** → the JSON error is caught
   in the browser.
5. Click **Reset to example**.

---

## 9. Swagger — the API directly — 1 min

**Do:** Switch to tab 2 (Swagger).

1. Show the endpoint groups: Sensors, Telemetry, Attachments, Deployment, Dashboard.
2. **GET /api/telemetry/aggregate** → *Try it out* →
   `firstMac` = `A4:CF:12:00:03:6F`, `secondMac` = `A4:CF:12:00:04:94` → *Execute*.
   **Say:** "This is literally `Meter3 = Meter1 + Meter2` from the brief, using my overloaded
   operator" — read out the `expression` line, e.g. `876 W + 684 W = 1560 W`. The numbers
   are live readings, so yours will differ.
3. Change `secondMac` to `A4:CF:12:00:00:00` (a moisture probe) → *Execute* →
   **409**: *"Cannot apply '+' to readings in Watt and Percent."*
   **Say:** "Adding watts to percent is refused rather than silently computed."
4. **GET /api/sensors/by-mac/{mac}** with `a4-cf-12-00-0c-bc` → point at the response header
   `X-Search-Comparisons: 4`. "Binary search found it in four comparisons out of fifteen nodes."

---

## 10. Resilience — 30 s

**Do:** In the API terminal press **Ctrl+C**. In the console, click **Sensors**.

**Say:** "If the gateway goes down, the console doesn't crash — it shows this banner and
checks again every three seconds."

**Do:** Start the API again (`dotnet run --project src/SmartX.Api`). Don't touch the browser.

**Say:** "When the gateway is back, the banner clears and the page reloads its data by
itself." A toast says *"The Smart-X gateway is back online."*

---

## 11. Code walkthrough — 2 min

Open each file and scroll to the line. Keep it brisk — one or two sentences each.

| Concept | File | Line | What to say |
|---|---|---|---|
| Generics, no boxing | `src/SmartX.Core/Telemetry/TelemetryPacket.cs` | 30–31 | `T : struct, IComparable<T>, IEquatable<T>` keeps the value in a typed field; comparisons never go through `object`. |
| Boxing only on purpose | same file | 65 | The only boxing path is an explicit interface member you must cast to reach. |
| Cast-free conversion | `src/SmartX.Core/Telemetry/TelemetryValueConverter.cs` | 29, 36 | A `Func<T,double>` resolved once per type in a static constructor, instead of `Convert.ToDouble(object)`. |
| Operator overloading | `src/SmartX.Core/Telemetry/SensorReading.cs` | 61, 104 | `+`, `−`, `*`, `/` and comparisons, all unit-aware; mismatched units throw `UnitMismatchException`. |
| Jagged array | `src/SmartX.Core/Storage/HistoricalBatchStore.cs` | 33, 68 | `double[][]` — each batch row sized to its own length. |
| Arrays → `List<T>` | same file | 141 | `FlattenToList()` moves the raw arrays into a pre-sized `List<SensorReading>`. |
| 2-D array | `src/SmartX.Core/Storage/TelemetryHeatMatrix.cs` | 26 | `double[,]` for zone × hour, where the shape never changes. |
| Recursion | `src/SmartX.Core/Validation/RecursiveDeploymentValidator.cs` | 90, 102, 113, 194 | `Descend` calls itself per child; depth guard, cycle guard, device leaf as base case. |
| Custom collection | `src/SmartX.Core/Collections/IngestionBuffer.cs` | 34, 78 | Ring buffer implementing `ICollection<T>`: O(1) add, allocates once, overwrites oldest. |
| Binary search index | `src/SmartX.Core/Collections/SortedSensorIndex.cs` | 29, 62 | Sorted by MAC; `List.BinarySearch` for lookup and for the insertion point. |
| Encryption | `src/SmartX.Core/Security/AttachmentEncryption.cs` | 94, 288 | Streaming AES-256-GCM, one sealed chunk at a time. |
| Upload pipeline | `src/SmartX.Api/Services/AttachmentService.cs` | 182 | request → SHA-256 → AES-GCM → file in one pass. |
| Async API client | `src/SmartX.Client/Services/SmartXApiClient.cs` | 167, 255 | Fully async; never throws — returns `ApiResult<T>` so failures render as UI, not crashes. |
| Live polling | `src/SmartX.Client/Pages/Dashboard.razor` | 315 | `PeriodicTimer` loop that never overlaps and stops when you leave the page. |

---

## 12. Tests, README, Docker and GitHub — 1 min

1. In a terminal:
   ```bash
   dotnet test -c Release
   ```
   **Say:** "155 tests passing — including the encryption tamper tests: a flipped bit, the
   wrong key, a truncated file and swapped chunks are all rejected."
   *(**Run this once before recording.** Windows Smart App Control on this machine blocks
   freshly built test DLLs with "An Application Control policy has blocked this file".
   If you see that, record this step on a machine without Smart App Control, or show the
   last passing run instead.)*
2. Open `README.md` (on GitHub it renders nicely): setup commands, architecture diagram,
   the table mapping every requirement to its file, the API reference, and the demo steps.
3. Show `docker-compose.yml` and the two Dockerfiles: "The API runs in an ASP.NET image with
   separate volumes for encrypted files and the key; the Blazor client is published to
   static files and served by nginx." If Docker Desktop is running, show
   `docker compose up --build`.
4. GitHub tab: scroll the commit history. "Built incrementally with descriptive commits."

---

## Closing — 15 s

"That's Part 1: a .NET 10 ingestion API and a Blazor WebAssembly console, covering generics,
operator overloading, jagged and 2-D arrays, recursion, a custom collection, encrypted
multipart uploads, and a live engagement dashboard. Parts 2 and 3 build on these pillars."
