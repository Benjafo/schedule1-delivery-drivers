# M5.5 Navigation Hardening — Mode-2 Fix Plan

Mod-side stuck watchdog with bounded recovery and clean abort. Routes around
the engine's self-suppressed stuck detection (Phase 4 root cause). No Harmony
patches; no edits to the engine's own detection.

**Core requirement: eliminate unbounded hangs, NOT guarantee 100% completion.**
A leg that cannot be recovered must end in a clean logged abort, never an
infinite hang.

---

## 1. Watchdog detection (with false-positive guard)

The hard part is distinguishing a **pin** (Phase 4: 0.02–0.10m per 2s for 70s,
net-zero) from **healthy slow crawl** (Phase 3: 16m in 115s, but net-advancing
and completed). Raw speed and per-sample movement both oscillate and cannot
discriminate — confirmed in Phase 4.

**Two metrics over a rolling window, both must be low to declare a pin:**

1. **Net progress toward destination.** Track `distanceToDest` at the start of
   the window vs. now. `progress = distToDest_windowStart − distToDest_now`.
   A pin makes near-zero progress; a crawling leg steadily reduces it.
2. **Odometer (total distance traveled) over the window.** Sum of per-sample
   moves. Guards against the curvy-path false-positive: a leg driving a curved
   route may not reduce straight-line distance-to-dest yet still has a large
   odometer. A pin has near-zero odometer too (Phase 4: ~0.5m over 20s vs
   Phase 3 healthy ~2.8m over 20s).

**Pin declared when, over a full rolling window `W`:**
`progress < MIN_PROGRESS_M` **AND** `odometer < MIN_ODOMETER_M`.

**Threshold philosophy — bias toward lax (toward NOT declaring a pin).** A
false abort kills a route that would have completed — the exact failure this
milestone eliminates. A too-lax threshold only costs one extra ~`W`-second
recovery window before the real pin is caught — cheap. The margin between the
one known-healthy example (Phase 3: ~2.8m odometer/20s) and a naive 3m
threshold is only 0.2m, and we have NO data on the slowest *completable* leg —
just that single anchor. So start the thresholds close to the pin's measured
~0.5m, far below the healthy ~2.8m, and tune up against real data:

- `W = 20s` rolling window
- `MIN_PROGRESS_M = 1.0m`  (pin ≈ 0m; healthy ≈ 2.8m)
- `MIN_ODOMETER_M = 1.5m`  (pin ≈ 0.5m; healthy ≈ 2.8m)
- Arm-delay: watchdog does not evaluate until `W` seconds after NAV begin
  (lets unpark/acceleration settle and the window fill).

All four are `const`s, trivially tunable once the first runs report real
healthy-slow values.

**Log every evaluation, not just pins.** On each watchdog evaluation (every 2s
once armed) emit a `WATCHDOG` line with the window's `progress` and `odometer`
values regardless of outcome. The first test runs will reveal the actual
distribution of healthy-slow legs so thresholds are set against data, not two
anchor points. Example:
`WATCHDOG: progress=4.12m odometer=5.30m window=20s armed=True pin=False`.

Reuses the existing Phase-4 stuck sampler (already runs every 2s during
`AutoDriving`). The sampler keeps a small ring buffer of `(time, pos,
distToDest)` spanning `W`; the watchdog reads from it. Keep all existing
`STUCK sample` logging — it's how we verify the watchdog fires correctly.

Separation check against known data (with the conservative thresholds):
- Phase 4 pin: progress ≈ 0m, odometer ≈ 0.5m over 20s → both below
  (1.0 / 1.5) → **detected.**
- Phase 3 slow-but-healthy: ~2.8m progress/odometer over 20s → both well
  above → **not detected**, with comfortable margin.

---

## 2. Recovery sequence (reuses the mode-1 ring-probe)

On pin detection, if under the attempt cap:

1. `_recoveryAttempts++`.
2. `_vehicle.Agent.StopNavigating()` — halts `AutoDriving`. This fires the
   stored callback with `Stopped`; guard so it does NOT abort the leg (see §4).
3. **Ring-probe the destination dock's EntryPoint** using the EXISTING
   `StartProbe` / `TickProbe` / `IssueProbe` machinery (36 candidates, 6/12/24m
   ring, `CalculatePath` reachability test from the current position). No new
   probe code.
4. On probe **success**: teleport the vehicle to the probed point
   (`transform.position = probed + up*0.5f`, zero `Rb.velocity`/`angularVelocity`
   — identical to the Phase-3 outbound teleport), then re-`Navigate` to the
   destination. This physically lifts the vehicle out of the wedge to a
   known-reachable approach. Cache the point in `_dockApproachCache` (same
   cache mode 1 uses) so future visits to this dock pre-empt the wedge.
5. Reset the watchdog window and resume normal driving.

Why teleport-to-probed-point rather than `Teleporter.MoveToGraph`: Phase 4
established that Hyland Dock 2's nearest graph node IS the wedge, so
`MoveToGraph` would re-deposit the vehicle in the same spot. The ring-probe
finds a point that is reachable AND (being offset 6–24m) generally out of the
wedge. `VehicleAgent.Teleporter` (`VehicleAgent.cs:1428`) is therefore not
used.

**Recovery-point exclusion (pulled into v1 — low complexity).**
`GenerateRingCandidates` already returns candidates ordered nearest-first
(raw entry, then the 6m ring, then 12m, then 24m). The probe walks from
`_probeIndex = 0` and takes the first reachable one. For recovery, if attempt
N teleported to candidate index `k` and the vehicle re-pinned there, attempt
N+1 starts probing from `k + 1` instead of 0 — skipping the known-bad point
and naturally stepping to a wider ring. This is a single start-offset field
(`_recoveryProbeStartIndex`), no new probing logic. The attempt cap still
bounds termination; this just makes each retry try a genuinely different
escape point instead of re-selecting the same wedge.

---

## 3. Attempt cap

- `MAX_RECOVERY_ATTEMPTS = 3` per leg (`_recoveryAttempts`, reset at NAV begin).
- Each successful recovery that still re-pins counts as another attempt.
- On the 4th pin (cap exceeded) OR a probe that exhausts all 36 candidates →
  clean abort (§4).

---

## 4. Clean abort path (THE CORE REQUIREMENT)

When recovery is exhausted:

1. Log: `MODE2 ABORT: driver could not reach dock <name> after N recovery
   attempts — aborting route`.
2. `_vehicle.Agent.StopNavigating()` to ensure `AutoDriving` is off.
3. `SetState(DriverState.ExitingVehicle)` — reuses the existing
   `EnterExitingVehicle` → `Done` → cleanup path (`DeliveryDriverBehaviour.cs:1372`).
   The route assignment is abandoned, NOT advanced. Driver returns to Idle.

The route reports failure and the driver frees up — survivable for M7 scheduled
routes (they log the miss and move on). No human, no infinite hang.

**Callback guarding:** `StopNavigating()` fires `OnNavigationComplete` with
`Stopped`, which today routes to `SetState(Done)`. Add an `_isRecovering`
(and `_aborting`) flag so the `Stopped` branch in `UpdateDriving` is ignored
while we are deliberately stopping for recovery/abort, rather than
double-handling it.

---

## 5. State / field additions

New fields on `DeliveryDriverBehaviour` (alongside existing nav/probe fields):
- `int _recoveryAttempts`
- `bool _isRecovering`
- `int _recoveryProbeStartIndex` (where the next recovery probe begins walking
  the candidate list — implements recovery-point exclusion)
- rolling window buffer: small `List<(float t, Vector3 pos, float distToDest)>`
  or three parallel arrays, capped to `W`-worth of samples.
- consts: `WATCHDOG_WINDOW_S = 20f`, `MIN_PROGRESS_M = 1.0f`,
  `MIN_ODOMETER_M = 1.5f`, `MAX_RECOVERY_ATTEMPTS = 3`.

Integration in `UpdateDriving` (order):
1. existing stuck sampler (every 2s) — also pushes into the window buffer.
2. if `AutoDriving` and watchdog armed and window full → evaluate pin; if
   pinned and not already recovering → begin recovery (or abort if capped).
3. if recovering and probe `InFlight` → `TickProbe`; on Complete → teleport +
   re-Navigate; on Failed → abort.
4. existing callback handling / probe / StartNavigation logic.

Reset `_recoveryAttempts`, `_isRecovering`, and the window buffer in
`StartNavigation` (per leg) and in `ResetState` / `EnterDone`.

---

## 6. Mode-1 re-measurement (folds in, no separate task)

While testing, run routes that get past their first leg and target Docks
Warehouse Dock 2 multiple times (stand near Bungalow / Storage Unit so it lands
on stop 2/3/4). Confirm the probe+teleport (Phase 2/3) still resolves it every
time. Converts mode 1 from "verified once" to "verified across N runs," or
surfaces any remaining mode-1 gap.

---

## 7. Test plan (after implementation)

Rebuild (close the game first — stale-DLL trap), deploy, then:
- **Hyland Manor start** — the Phase-4 pin case. Expect: watchdog fires,
  `MODE2` recovery log, either recovery-completes or a clean `MODE2 ABORT`.
  Critically: NO 70s+ hang.
- **Barn start** — the other known mode-2 driveway.
- **Docks Warehouse / Bungalow / Storage Unit starts** — exercise mode-1 dock
  (Docks Warehouse Dock 2) across multiple runs for the re-measurement, and
  confirm healthy slow legs are NOT false-aborted.

Report:
- Hang eliminated? (no leg runs >~`W` + recovery time without resolving)
- Clean aborts where expected, with the abort log line?
- Healthy slow legs (Phase-3-style) NOT false-positive aborted?
- Mode 1 holding across N runs?

---

## Out of scope
- M6 persistence.
- Patching the engine's own stuck detection (we route around it).
- Any route-engine / storage / state-machine change beyond the watchdog,
  recovery, and clean-abort path.

---

# Phase 6 addendum: departure smoothing (post-first-test)

The first in-game run (2026-08-27) validated the watchdog end-to-end: two real
pins at Hyland Manor's driveway wedge, two recoveries, 4-stop route completed,
no hang. It also exposed two player-visible problems: the recovery teleported
straight to the destination dock (probe candidate 1 = raw entry, graph-reachable
even though physically blocked), making the vehicle "vanish" mid-drive; and the
Hyland exit pin is deterministic — the nav graph's only route out passes the
wedge, so the safety net fired on every departure. Fixes, keeping the
watchdog/abort architecture unchanged:

1. **Forward-nudge recovery (attempts 1–2).** On pin, calculate the path from
   the pin to the current nav target and teleport 18m (then 36m on retry) along
   it, then re-`Navigate` to the same target with a fresh watchdog window and
   preserved attempt count. The player sees a small hop past the obstruction and
   the drive continues. Attempt 3 keeps the old destination ring-probe
   (teleport-to-approach + park) as the fallback; the abort path is unchanged.
2. **Fast-pin detection.** A hard pin (odometer < 0.35m over 8s, armed 10s
   after NAV begin) is declared early instead of waiting the full 20s window.
   Threshold sits ~3x below the known-healthy crawl anchor; a false fire now
   only costs a small forward nudge, not a route skip.
3. **Learned per-property exit points.** When a nudge rescues an outbound leg
   (pin inside a property, destination outside), the nudge point is cached per
   property. Future departures from that property teleport there while still
   stationary, before `Navigate` — pre-empting the known-bad driveway graph.
   Session-scoped, like the dock-approach cache (persist in M6).
4. **Log fix:** the `JUMP` annotation now compares movement against reported
   speed instead of a flat 5m, so normal 22km/h driving isn't flagged as a
   teleport.

No player-facing notifications for recovered pins (recovered = non-event;
surfacing it would read as "something is broken"). Only a clean abort is worth
surfacing, and that belongs to M7's route reporting.

Deferred (layer 3, plan later if wanted): mod-driven "valet" driving through
property geometry — replace the engine AI only in the known-bad zone, hand off
at the road. The engine AI is proven good on roads and only fails in
driveways, but this needs a mini vehicle controller + clear-corridor finding.
