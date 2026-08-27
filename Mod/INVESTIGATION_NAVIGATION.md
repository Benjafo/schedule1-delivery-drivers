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

| Run | Leg | Start origin                     | Dest dock                  |   Dist | Result     |   Elapsed | Notes                                                      |
| --: | --: | -------------------------------- | -------------------------- | -----: | ---------- | --------: | ---------------------------------------------------------- |
|   1 |   1 | Bungalow open area (−154,−3,121) | Bungalow Dock 1            |  17.0m | Complete   |     5.63s | normal                                                     |
|   1 |   2 | Bungalow (−163,−3,109)           | Storage Unit Dock 1        | 156.3m | Complete   |    59.60s | normal                                                     |
|   1 |   3 | Storage Unit (−7,1,97)           | **Docks Warehouse Dock 2** | 180.6m | **Failed** | **0.78s** | endPos=(−19,1,99); vehicle drove 13m then path-calc failed |
|   2 |   1 | Docks open area (−70,−2,−49)     | **Docks Warehouse Dock 2** |  21.8m | **Failed** | **0.44s** | endPos == startPos; zero movement                          |
|   3 |   1 | Hyland (163,11,−69)              | Hyland Dock 2              |   8.9m | Complete   |   130.42s | user manually pushed car past trees in driveway            |
|   3 |   2 | Hyland (156,11,−68)              | Barn Dock 2                |  53.3m | —          |         — | user gave up (stuck on Hyland driveway trees)              |
|   4 |   1 | Storage Unit (−20,1,94)          | Storage Unit Dock 1        |  15.1m | Complete   |    41.29s | slow but normal                                            |
|   4 |   2 | Storage Unit (−7,1,97)           | Bungalow Dock 1            | 159.9m | Complete   |    93.95s | normal                                                     |
|   4 |   3 | Bungalow (−162,−3,109)           | **Docks Warehouse Dock 2** | 191.5m | **Failed** | **0.46s** | endPos≈startPos; no movement                               |
|   5 |   1 | Barn (162,1,−12)                 | Barn Dock 2                |  17.6m | Complete   |    16.84s | normal                                                     |
|   5 |   2 | Barn (178,1,−19)                 | Hyland Dock 2              |  55.4m | —          |         — | car drove into trees at dead end                           |

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
was _never_ reached and failed from three different start origins, including
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

Runs 3 and 5 show a different kind of trouble: `Navigate()` _succeeded_ in
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

- **Failure mode 1 — immediate path-calc failure:** _Specific destination_.
  Confined (in this dataset) to Docks Warehouse Loading Dock 2. Reproduces
  100% of the time we tried to navigate there. Failure returns within
  ~0.5s with little or no vehicle movement. Strong signal that the
  destination point is not connected to the navigable `VehicleGraph` — its
  `Parking.EntryPoint` may sit too far from any road-graph node, or its
  graph segment is isolated from the rest of the map.

- **Failure mode 2 — execution-time stuck:** _Property-driveway-specific_.
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

| Dock           | Property        | EntryPoint               |               Graph snap |       Dist | Verdict   |
| -------------- | --------------- | ------------------------ | -----------------------: | ---------: | --------- |
| Loading Dock 1 | Bungalow        | (−166.06, −4.00, 109.35) | (−160.48, −3.95, 108.91) |  **5.60m** | OK        |
| Loading Dock 2 | Barn            | (177.62, −0.09, −20.47)  |   (172.65, 0.01, −13.75) |      8.36m | OFF-GRAPH |
| Loading Dock 1 | Storage Unit    | (−6.75, 0.10, 100.50)    |   (−14.35, 0.10, 104.25) |      8.47m | OFF-GRAPH |
| Loading Dock 2 | Docks Warehouse | (−84.37, −2.50, −65.56)  |  (−85.85, −2.22, −56.25) |      9.43m | OFF-GRAPH |
| Loading Dock 1 | Barn            | (177.62, −0.09, −1.38)   |    (169.65, 0.17, −6.50) |      9.48m | OFF-GRAPH |
| Loading Dock 3 | Hyland Manor    | (172.75, 10.01, −72.50)  |   (171.55, 9.35, −81.89) |      9.49m | OFF-GRAPH |
| Loading Dock 1 | Docks Warehouse | (−82.37, −2.50, −69.02)  |  (−74.81, −2.50, −76.15) |     10.39m | OFF-GRAPH |
| Loading Dock 1 | Hyland Manor    | (154.25, 10.01, −72.50)  |   (166.75, 9.44, −81.89) |     15.64m | OFF-GRAPH |
| Loading Dock 2 | Hyland Manor    | (154.25, 10.01, −68.00)  |   (166.75, 9.44, −81.89) | **18.70m** | OFF-GRAPH |

**8 of 9 docks are OFF-GRAPH at the 6m threshold.** Only Bungalow Dock 1
clears the threshold cleanly, at 5.60m.

### Cross-reference: audit distance vs. observed run behavior

| Dock                       | Audit dist | Observed runs targeting it | Observed result                             |
| -------------------------- | ---------: | -------------------------- | ------------------------------------------- |
| Bungalow Dock 1            |      5.60m | Run 1.1, Run 4.2 (dest)    | ✓ Both succeeded                            |
| Barn Dock 2                |      8.36m | Run 5.1                    | ✓ Succeeded                                 |
| Storage Unit Dock 1        |      8.47m | Run 1.2 (dest), Run 4.1    | ✓ Both succeeded                            |
| **Docks Warehouse Dock 2** |  **9.43m** | Run 1.3, Run 2.1, Run 4.3  | ✗ All 3 failed (path-calc)                  |
| Docks Warehouse Dock 1     |     10.39m | (not directly targeted)    | —                                           |
| Hyland Manor Dock 1        |     15.64m | (not directly targeted)    | —                                           |
| **Hyland Manor Dock 2**    | **18.70m** | Run 3.1                    | ✓ Succeeded (with manual push past terrain) |

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

---

## Phase 3: Mode-1 Fix — Outbound Teleport (Checkpoint)

### The fix

Mode 1 (graph-connectivity path-calc failures) is addressed by two
complementary mechanisms working together:

- **Phase 2 — inbound ring-probe.** When a direct `Navigate` to a dock's
  `Parking.EntryPoint` returns `Failed`, probe a ring of candidate points
  around the entry (radii 6m / 12m / 24m, every 30° = 36 candidates,
  nearest-first). Use `NavigationUtility.CalculatePath` as a side-effect-free
  reachability test (it does not engage the AI driver), drive to the first
  reachable candidate, and cache it per-dock keyed by `dock.GUID`. Repeat
  visits reuse the cached point without re-probing.

- **Phase 3 — outbound teleport.** When the vehicle leaves a dock that was
  previously probed, `Park()` has snapped it onto the dock's parking spot —
  which sits on the same unreachable graph segment that defeated the inbound
  Navigate. Before issuing the outbound `Navigate`, the vehicle is teleported
  from that dead-end snap position back to the cached navigable approach
  point (and its rigidbody velocity is zeroed). This closes mode 1 in the
  outbound direction: Park drops the vehicle on an unreachable point; the
  teleport restores it to a known-navigable one.

### Verification

One full F6 round-trip starting from Docks Warehouse — the hardest known
mode-1 route (Docks Warehouse Dock 2 failed from every origin in the
original triage) — completed all 4 stops end-to-end with **zero manual
intervention** (410.6s total). The run exercised every code path:

- Inbound probe + cache on legs 1 and 2 (leg 1 found candidate 15/36; leg 2
  found candidate 1/36 immediately, because the outbound teleport had already
  placed the vehicle on a navigable start node).
- Outbound teleport on legs 2 and 3 (off the Docks Warehouse and Storage Unit
  dead-ends respectively).
- Leg-4 cache hit reusing leg-1's approach point for the return to Docks
  Warehouse Dock 2 — no re-probe needed.

`Vehicle.Park()` cleanly absorbed approach-to-spot gaps up to 20.5m at every
stop; dock occupancy and all cargo transfers succeeded.

### Status

Mode 1 is **fix implemented and verified on the hardest known case** — NOT
closed. This is one successful run on one previously-failing route, not a
reliability proof. Full closure requires multi-run characterization across
several routes targeting Docks Warehouse Dock 2 and the other off-graph
docks, matching the rigor of the original triage. That re-measurement is
folded into the upcoming mode-2 triage rather than performed separately.

### Caveat

Failure mode 2 (vehicle physically stuck on terrain/trees at the Hyland
Manor and Barn driveways during path execution) is still present and
untouched. The verification run ground slowly through terrain rather than
getting permanently stuck — leg 1 took 115s to cover 16m near Docks
Warehouse — but mode 2 remains the open hard blocker for unattended
operation and needs its own dedicated investigation.

---

## Phase 4: Mode-2 Triage

Investigation only — no fix. Added a stuck-state sampler that logs, every 2s
while `AutoDriving` is true: `Speed_Kmh`, distance moved since last sample,
`GetIsStuck()`, `IsReversing`, `IsOnVehicleGraph()`, position, and a JUMP
flag when the inter-sample move exceeds 5m (implying an engine teleport).
DriveFlags are logged once per leg at NAV begin.

### Data: one run, Hyland Manor leg 1 (hands-off, no manual nudging)

Route stop 1 = Hyland Manor Dock 2, the vehicle never reached the dock. The
log shows ~70s of continuous samples with **no `NAV end` callback ever
firing** — the defining mode-2 signature: Navigate produced a path,
execution hung, no Complete/Failed/Stopped.

| Observable            | Value during the hang                                                                                                                                                              |
| --------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `NAV end` callback    | **Never fired** (hung mid-execution)                                                                                                                                               |
| DriveFlags            | `ObstacleMode=Default StuckDetection=True UseRoads=True SpeedLimitMult=1.00` (all stock)                                                                                           |
| `GetIsStuck()`        | **False for the entire 70s**, despite the vehicle being pinned                                                                                                                     |
| `IsReversing`         | **False the entire time** — reverse routine never engaged                                                                                                                          |
| `IsOnVehicleGraph()`  | Flickered True/False — vehicle sat right at a graph edge                                                                                                                           |
| Speed                 | Oscillated −1.1 to +3.6 km/h (PID rocking against an obstacle)                                                                                                                     |
| Distance moved/sample | 0.02–0.10m for 70s — physically pinned                                                                                                                                             |
| Wedge position        | ~`(167.0, 10.5, -79.4)`                                                                                                                                                            |
| One early JUMP        | 12.6m in the first 2s at 6.6 km/h sampled speed — inconsistent with that speed, implies a one-time engine teleport (nav-start graph snap) at the leg start; no further jumps after |

**The wedge position `(167.0, 10.5, -79.4)` is essentially the Phase-1-audited
graph-snap point for Hyland Dock 2, `(166.75, 9.44, -81.89)` — ~18.7m from
the dock's EntryPoint.** The vehicle drove/snapped to the nearest navigable
graph node for this dock, which sits in the driveway-tree wedge, and pinned
there.

### Part B: which subsystem is failing

Root cause is a **conflict between obstacle-avoidance and stuck-detection in
the engine, which suppresses recovery.** Traced in the decompiled source:

- `UpdateSpeedReduction` (`VehicleAgent.cs:454-455`) computes a speed
  multiplier `num11 = obstacleMultiplier * destinationMultiplier` and feeds
  it to `speedReductionTracker.SubmitValue`. When the vehicle noses into an
  obstacle, the obstacle multiplier collapses toward 0 — the AI deliberately
  throttles to near-zero speed.
- `GetIsStuck()` (`VehicleAgent.cs:1184`) and `UpdateStuckDetection`
  (`:475`) both early-return **false** at the guard
  `if (speedReductionTracker.GetLowestValue() < 0.1f) return false;`
  (`:490`, `:1190`). Because obstacle avoidance has driven the commanded
  speed below 0.1, the stuck-detector concludes "intentionally slow, not
  stuck" and never proceeds to the distance check (`StuckDistanceThreshold =
1m` over `StuckTimeThreshold = 10s`, which the vehicle easily satisfies —
  it moved <0.3m in 70s).
- The recovery teleport (`Teleporter.MoveToGraph` / `MoveToRoadNetwork`,
  `:508-515`) lives _inside_ `UpdateStuckDetection`, so it is gated behind
  the same suppressed precondition and never fires.
- The reverse routine engages only from the steering sweep test
  (`:344-370`), which requires an active hard-steer command
  (`|targetSteerAngle| * maxSteerAngle > 5°`). A head-on pin with little
  steering never triggers it — consistent with `IsReversing=False`
  throughout.

So mode 2 is a **combination, dominated by a config/logic problem**:

1. (Primary) Engine stuck-detection is enabled but self-suppressed: obstacle
   avoidance's own speed reduction trips the `GetLowestValue() < 0.1` guard,
   so neither `GetIsStuck()` nor the recovery teleport ever fires.
2. (Secondary) The reverse routine only triggers on hard-steer sweep
   failure, not on a straight-in pin, so it never helps this geometry.
3. (Underlying) The navigable graph node for Hyland Dock 2 is itself ~18.7m
   from the dock, in a driveway-tree wedge — a graph-data problem that puts
   the vehicle in the bad spot in the first place.

### Part C: mode-1 re-measurement

**Not obtained.** This single run got stuck at stop 1 (Hyland Manor) before
reaching any off-graph mode-1 dock (Docks Warehouse Dock 2 was not on this
route's reachable legs). The probe+teleport machinery did not get exercised.
Mode 1 therefore remains "verified once" (Phase 3) — the multi-run
re-measurement is still outstanding and should be retried on routes that
actually complete past their first leg, which currently requires mode 2 to
be addressed first.

### Realistic assessment: is mode 2 mod-side fixable?

**Partially — with a meaningful residual that may be a game limitation.**

- _Hopeful:_ The mod can implement its **own** stuck watchdog. We already
  sample position every 2s and can trivially detect "moved <Xm over Ys" —
  the exact condition the engine's detector refuses to act on. On detection
  we can invoke recovery ourselves: call the engine's
  `Teleporter.MoveToGraph` / `MoveToRoadNetwork`, re-`Navigate`, or force a
  reverse. This routes around the suppressed precondition entirely and is
  fully mod-side.
- _Caveat:_ Recovery may not stick. The navigable graph node for Hyland Dock
  2 is in the wedge, so a naive teleport-to-graph could drop the vehicle
  right back into the same spot. Making recovery durable likely needs the
  same ring-probe idea mode 1 used — search for an approach point that is
  both reachable _and_ not wedged — which is more involved.
- _Residual:_ Some docks may have no good navigable approach near them at
  all (graph data routing through impassable terrain). For those, the
  honest outcome is mitigation, not guarantee: a watchdog + timeout that
  aborts a hung leg cleanly (instead of hanging forever) plus a reliability
  bar, rather than 100% completion. This matters for M7 overnight routes —
  the requirement may need to be "detect-and-abort-and-retry" rather than
  "never get stuck."

Net: mode 2 is a tractable milestone for the common case (self-built
watchdog + recovery), but it is **not** a pure config toggle, and a fraction
of dock geometries may only be mitigable rather than fully solvable. Plan for
a reliability target with clean abort/retry, not a guarantee.

### Triage complete — awaiting direction on mode-2 fix approach.
