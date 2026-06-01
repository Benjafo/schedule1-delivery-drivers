# Navigation Failure Investigation

Triage of `VehicleAgent.Navigate` failures observed during F6 round-trip routes
after the storage-lookup fix was deployed. **Reconnaissance only — no fixes
proposed in this document.**

## Method

Instrumented every `Navigate()` call in `DeliveryDriverBehaviour.UpdateDriving`
to log a structured `NAV begin` line (start pos, dest pos, distance, leg label,
and property-bounds containment for each endpoint) and a `NAV end` line
(result, elapsed time; plus end position and property containment on Failed).

Ran F6 five times, each from a different property cluster, to vary
property-pair coverage. Vehicle spawn (F8) and NPC spawn (F9) happened
near the player after F5-teleporting to each cluster.

## Per-leg results

| Run | Leg | Start origin | Dest dock | Dist | Result | Elapsed | Notes |
|----:|----:|---|---|----:|---|----:|---|
| 1 | 1 | Bungalow open area (−154,−3,121) | Bungalow Dock 1 | 17.0m | Complete | 5.63s | normal |
| 1 | 2 | Bungalow (−163,−3,109) | Storage Unit Dock 1 | 156.3m | Complete | 59.60s | normal |
| 1 | 3 | Storage Unit (−7,1,97) | **Docks Warehouse Dock 2** | 180.6m | **Failed** | **0.78s** | endPos=(−19,1,99); vehicle drove 13m then path-calc failed |
| 2 | 1 | Docks open area (−70,−2,−49) | **Docks Warehouse Dock 2** | 21.8m | **Failed** | **0.44s** | endPos == startPos; zero movement |
| 3 | 1 | Hyland (163,11,−69) | Hyland Dock 2 | 8.9m | Complete | 130.42s | user manually pushed car past trees in driveway |
| 3 | 2 | Hyland (156,11,−68) | Barn Dock 2 | 53.3m | — | — | user gave up (stuck on Hyland driveway trees) |
| 4 | 1 | Storage Unit (−20,1,94) | Storage Unit Dock 1 | 15.1m | Complete | 41.29s | slow but normal |
| 4 | 2 | Storage Unit (−7,1,97) | Bungalow Dock 1 | 159.9m | Complete | 93.95s | normal |
| 4 | 3 | Bungalow (−162,−3,109) | **Docks Warehouse Dock 2** | 191.5m | **Failed** | **0.46s** | endPos≈startPos; no movement |
| 5 | 1 | Barn (162,1,−12) | Barn Dock 2 | 17.6m | Complete | 16.84s | normal |
| 5 | 2 | Barn (178,1,−19) | Hyland Dock 2 | 55.4m | — | — | car drove into trees at dead end |

All endpoints logged as `[open]` — none of the failing or completed navigations
had a start or destination inside any property's `BoxCollider` bounds via
`Property.DoBoundsContainPoint`.

## Headline finding

**All three immediate path-calc failures target the same destination:
Docks Warehouse Loading Dock 2 at (−84.37, −2.50, −65.56).**

- Run 1 leg 3: from Storage Unit, dest = Docks Warehouse Dock 2 → Failed 0.78s
- Run 2 leg 1: from Docks Warehouse open area (22m away), dest = Docks Warehouse Dock 2 → Failed 0.44s
- Run 4 leg 3: from Bungalow, dest = Docks Warehouse Dock 2 → Failed 0.46s

Every other destination (Bungalow Dock 1, Storage Unit Dock 1, Hyland Dock 2,
Barn Dock 2) was reachable at least once in this dataset. Docks Warehouse Dock 2
was *never* reached and failed from three different start origins, including
a start point only 22m away inside the same property cluster.

This is a **specific-pair / destination-specific** failure, not a systemic
cross-property failure or a stochastic flake.

## Gated-property hypothesis

**Not supported by the data.** All eleven leg start/dest endpoints logged as
`[open]` — outside any owned or unowned property's `BoxCollider` bounds.
The path-calc failures correlate with destination dock identity (Docks
Warehouse Dock 2 specifically), not with property-bounds containment of
either endpoint.

The hypothesis was reasonable given the Hyland Manor gate incident, but
that turned out to be a different failure mode (see "Second failure mode"
below).

## Second failure mode (distinct from immediate path-calc failure)

Runs 3 and 5 show a different kind of trouble: `Navigate()` *succeeded* in
producing a path, but the vehicle then got stuck on terrain during execution.

- Run 3 leg 1: 130 seconds to traverse 8.9m at Hyland Manor entrance —
  user had to physically push the car past the driveway trees.
- Run 5 leg 2: car exited the Barn dock, turned the wrong way ("a dead end"),
  and drove into trees. No callback captured before user aborted.

These are **execution-time obstacles**, not path-calc failures. The road graph
appears to plot routes that traverse terrain the vehicle can't actually
clear. Distinct subsystem from the Docks-Warehouse-Dock-2 issue and likely
needs its own treatment.

## Classification

- **Failure mode 1 — immediate path-calc failure:** *Specific destination*.
  Confined (in this dataset) to Docks Warehouse Loading Dock 2. Reproduces
  100% of the time we tried to navigate there. Failure returns within
  ~0.5s with little or no vehicle movement. Strong signal that the
  destination point is not connected to the navigable `VehicleGraph` — its
  `Parking.EntryPoint` may sit too far from any road-graph node, or its
  graph segment is isolated from the rest of the map.

- **Failure mode 2 — execution-time stuck:** *Property-driveway-specific*.
  Hyland Manor driveway and one direction from Barn dock are routes the
  path planner will produce but the vehicle can't actually drive without
  collision. RECON §1 mentions `VehicleAgent.GetIsStuck()` and stuck
  detection as a `DriveFlag`, suggesting the engine has machinery for
  this case that we may not be configuring.

## Magnitude

- 11 nav legs attempted across 5 routes
- 6 succeeded (55%)
- 3 immediate path-calc failures (27%) — all to the same dock
- 2 stuck-on-terrain (18%) — two different driveways

Of the 6 cross-property transitions (start origin's nearest property ≠ dest dock's property), 4 of them either failed at path-calc (3) or stuck on terrain (1). The cross-property failure rate is high enough that **the current F6 route test cannot reliably complete a 4-stop round trip** without manual intervention, regardless of which property cluster the route starts from. The fix is not a one-liner — it requires investigating the road graph's connectivity around Docks Warehouse Dock 2 and the stuck-detection behavior on driveways.

## Recommendation surface (decision input only — no fix proposed)

The two failure modes warrant separate consideration:

- Failure mode 1 is **likely a data issue, not a code issue** — the dock's
  EntryPoint or the road graph around it. Could be a targeted fix
  (move the dock's EntryPoint to a road-adjacent point, or use the
  existing `VehicleTeleporter.MoveToGraph` machinery to snap the
  destination), or it could affect more docks than just Docks Warehouse
  Dock 2 once we look closer.

- Failure mode 2 is **execution-time** and probably needs road-graph and
  obstacle-handling investigation: stuck detection, reverse routine
  (`StartReverse()` in RECON §1), `ObstacleMode` flags, and how the
  road graph handles property driveways with vegetation.

A natural M6 plan candidate: "Navigation Hardening" covering both —
verify dock EntryPoint road-graph proximity for every owned property's
docks, and configure VehicleAgent stuck-detection + recovery so terrain
hangs don't terminate routes.

## Triage complete — awaiting direction on fix approach.

---

## Phase 1: Graph Proximity Audit

### Method

Added an F4 hotkey that iterates every `LoadingDock` across all owned and
unowned properties. For each dock with a non-null `Parking.EntryPoint`,
it samples the nearest point on the "General Vehicle Graph" via
`NavigationUtility.SampleVehicleGraph(entry)` (which delegates to
`AstarPath.active.GetNearest`) and reports the distance from the raw
EntryPoint to that graph-snap point.

Threshold used for the `OK`/`OFF-GRAPH` verdict: 6m, as referenced in
RECON §1 ("if the vehicle is >6f from the road graph,
`VehicleTeleporter.MoveToGraph()` teleports it back").

### Results (9 owned docks; unowned not yet granted in this session)

Sorted by distance ascending:

| Dock | Property | EntryPoint | Graph snap | Dist | Verdict |
|---|---|---|---:|---:|---|
| Loading Dock 1 | Bungalow | (−166.06, −4.00, 109.35) | (−160.48, −3.95, 108.91) | **5.60m** | OK |
| Loading Dock 2 | Barn | (177.62, −0.09, −20.47) | (172.65, 0.01, −13.75) | 8.36m | OFF-GRAPH |
| Loading Dock 1 | Storage Unit | (−6.75, 0.10, 100.50) | (−14.35, 0.10, 104.25) | 8.47m | OFF-GRAPH |
| Loading Dock 2 | Docks Warehouse | (−84.37, −2.50, −65.56) | (−85.85, −2.22, −56.25) | 9.43m | OFF-GRAPH |
| Loading Dock 1 | Barn | (177.62, −0.09, −1.38) | (169.65, 0.17, −6.50) | 9.48m | OFF-GRAPH |
| Loading Dock 3 | Hyland Manor | (172.75, 10.01, −72.50) | (171.55, 9.35, −81.89) | 9.49m | OFF-GRAPH |
| Loading Dock 1 | Docks Warehouse | (−82.37, −2.50, −69.02) | (−74.81, −2.50, −76.15) | 10.39m | OFF-GRAPH |
| Loading Dock 1 | Hyland Manor | (154.25, 10.01, −72.50) | (166.75, 9.44, −81.89) | 15.64m | OFF-GRAPH |
| Loading Dock 2 | Hyland Manor | (154.25, 10.01, −68.00) | (166.75, 9.44, −81.89) | **18.70m** | OFF-GRAPH |

**8 of 9 docks are OFF-GRAPH at the 6m threshold.** Only Bungalow Dock 1
clears the threshold cleanly, at 5.60m.

### Cross-reference: audit distance vs. observed run behavior

| Dock | Audit dist | Observed runs targeting it | Observed result |
|---|---:|---|---|
| Bungalow Dock 1 | 5.60m | Run 1.1, Run 4.2 (dest) | ✓ Both succeeded |
| Barn Dock 2 | 8.36m | Run 5.1 | ✓ Succeeded |
| Storage Unit Dock 1 | 8.47m | Run 1.2 (dest), Run 4.1 | ✓ Both succeeded |
| **Docks Warehouse Dock 2** | **9.43m** | Run 1.3, Run 2.1, Run 4.3 | ✗ All 3 failed (path-calc) |
| Docks Warehouse Dock 1 | 10.39m | (not directly targeted) | — |
| Hyland Manor Dock 1 | 15.64m | (not directly targeted) | — |
| **Hyland Manor Dock 2** | **18.70m** | Run 3.1 | ✓ Succeeded (with manual push past terrain) |

### Verdict

**The graph-proximity hypothesis does NOT explain mode 1.**

- Storage Unit Dock 1 (8.47m off-graph) is reached successfully in two
  runs from two different start origins.
- Hyland Manor Dock 2 (18.70m off-graph — twice as far as Docks Warehouse
  Dock 2) is reached successfully in Run 3, even though the vehicle then
  got stuck on terrain (mode 2).
- Docks Warehouse Dock 2 (9.43m off-graph) is reached **zero times** from
  three different start origins, including one only 22m away.

Distance from graph is not the discriminator. The data points instead to
**graph connectivity** — the snap point for Docks Warehouse Dock 2,
(−85.85, −2.22, −56.25), may sit on an isolated graph segment with no
reachable edges from the surrounding road network, regardless of which
start position we begin from.

Note also that `Navigate(location, null, callback)` uses default
`NavigationSettings` where `ensureProximityToGraph = true` and
`teleportToGraphIfCalculationFails = true` (see
`ScheduleOne/Vehicles/AI/NavigationSettings.cs:12-15`). The defaults
already attempt to snap to the graph and recover from path failures —
they do not save us here, which is further evidence that the issue is
connectivity rather than proximity.

### Implications for Phase 2

Two fix options become less promising:

- "Snap destination to nearest graph node" — Navigate already does this
  by default. The snap point itself is just unreachable.
- "Use `VehicleTeleporter.MoveToGraph` for off-graph destinations" —
  teleporting to the snap point doesn't help if no path exists from the
  start to that snap point.

More promising candidates given the connectivity hypothesis:

- **Probe a ring of candidate destinations** around the dock's EntryPoint
  (e.g. sample every 30° at radius 6m, 12m, 24m) and call Navigate against
  each until one returns Complete. The first reachable snap point near the
  dock becomes the de-facto destination; we drive the last few meters by
  manual control (or just `Vehicle.Park()` does it for us, since parking
  snaps regardless).
- **Per-dock approach-point override**: hardcode a known-reachable Vector3
  for each problematic dock (read from logs once, store in a small dict)
  and Navigate to that point instead of the raw EntryPoint. Brittle but
  zero-engine-risk.
- **Try a `CalculatePath` dry run before Navigate** to detect unreachable
  destinations early and pick an alternate, but `CalculatePath` is
  coroutine-based; this would add async complexity to the state machine.

### Phase 1 conclusion

Mode 1 is **not** caused by EntryPoint distance from the graph. It is
caused by the specific Docks Warehouse Dock 2 snap point being on an
unreachable graph segment (or having no incoming edges from the road
network that other docks share). Phase 2 needs a connectivity-aware
fix, not a distance-aware one.

**Awaiting direction on Phase 2 approach.**

