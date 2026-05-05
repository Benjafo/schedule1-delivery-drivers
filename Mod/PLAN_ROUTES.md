# M5: Round-Trip Routes — Implementation Plan

## Summary

Replace the hardcoded 2-stop source→destination flow with a generalized N-stop route. A route is an ordered list of stops; each stop references a dock by GUID and specifies an action (Pickup or Dropoff). The driver state machine processes stops sequentially, returning to idle when the route completes.

---

## 1. Route & Stop Data Types

### Design

```csharp
// New file: Mod/Source/Route.cs

namespace DeliveryDriversMod
{
    public enum StopAction
    {
        Pickup,
        Dropoff
    }

    /// <summary>
    /// A single stop in a delivery route.
    /// References a dock by GUID string (serializable, no direct object reference).
    /// </summary>
    public class RouteStop
    {
        public string DockGUID { get; set; }
        public StopAction Action { get; set; }

        // Resolved at runtime, NOT serialized
        [NonSerialized] public LoadingDock ResolvedDock;
        [NonSerialized] public ParkingLot ResolvedParking;

        public RouteStop(string dockGuid, StopAction action)
        {
            DockGUID = dockGuid;
            Action = action;
        }
    }

    /// <summary>
    /// A reusable route definition: ordered list of stops.
    /// Serializable to JSON for M6 persistence.
    /// </summary>
    public class Route
    {
        public string Name { get; set; }
        public List<RouteStop> Stops { get; set; } = new List<RouteStop>();

        public Route(string name)
        {
            Name = name;
        }
    }

    /// <summary>
    /// Tracks a driver's progress through an assigned route.
    /// Separates "route definition" from "execution state."
    /// One driver holds at most one RouteAssignment at a time.
    /// </summary>
    public class RouteAssignment
    {
        public Route Route { get; }
        public int CurrentStopIndex { get; set; }
        public bool IsComplete => CurrentStopIndex >= Route.Stops.Count;

        public RouteStop CurrentStop => IsComplete ? null : Route.Stops[CurrentStopIndex];

        public RouteAssignment(Route route)
        {
            Route = route;
            CurrentStopIndex = 0;
        }

        public void AdvanceToNextStop()
        {
            CurrentStopIndex++;
        }
    }
}
```

### JSON Serializability (M6 note)

The `Route` and `RouteStop` classes use only primitives (`string`, `enum`). For M6, serialize as:

```json
{
  "Name": "Storage Loop",
  "Stops": [
    { "DockGUID": "abc-123-...", "Action": "Pickup" },
    { "DockGUID": "def-456-...", "Action": "Dropoff" }
  ]
}
```

The `[NonSerialized]` fields (`ResolvedDock`, `ResolvedParking`) are resolved at route start using `GUIDManager.GetObject<LoadingDock>(new Guid(stop.DockGUID))`. This matches the game's existing pattern for `AdvancedTransitRoute`.

---

## 2. Dock Identifier Strategy

**Finding: LoadingDock implements `IGUIDRegisterable`.**

- Each dock has a `BakedGUID` (string) baked into the prefab/scene
- On Awake, `this.GUID = new Guid(this.BakedGUID)` is set and registered with `GUIDManager`
- Lookup: `GUIDManager.GetObject<LoadingDock>(new Guid(guidString))`
- This GUID is stable across save/reload — the same approach used by the game for `AdvancedTransitRoute`, `Contract`, `VehicleManager`, etc.

**Resolution method** (called once when a route is assigned to a driver):

```csharp
private bool ResolveRouteStops(RouteAssignment assignment)
{
    foreach (var stop in assignment.Route.Stops)
    {
        var dock = GUIDManager.GetObject<LoadingDock>(new Guid(stop.DockGUID));
        if (dock == null)
        {
            MelonLogger.Error("Route stop references unknown dock GUID: " + stop.DockGUID);
            return false;
        }
        stop.ResolvedDock = dock;
        stop.ResolvedParking = dock.Parking;
        if (stop.ResolvedParking == null)
        {
            MelonLogger.Error("Dock '" + dock.Name + "' has no Parking assigned");
            return false;
        }
    }
    return true;
}
```

---

## 3. State Machine Design

### Current M4 Structure (to be replaced)

```
WalkToVehicle → Enter → Drive(source) → Park → OccupyDock → Load → ReleaseDock
→ Drive(dest) → Park → OccupyDock → Unload → ReleaseDock → Exit → Done
```

Hardcoded 2-leg flow using `DeliveryLeg` enum and `_sourceDock`/`_destDock` pair.

### New M5 Structure

**Key insight:** Keep the same per-stop state sequence (Drive → Park → OccupyDock → Load/Unload → ReleaseDock) but wrap it in a loop controlled by `RouteAssignment.CurrentStopIndex`.

**States remain the same.** No new enum values needed. The change is in transitions:

```
[Route start]
  WalkToVehicle → EnteringVehicle → [enter stop loop]

[Per-stop loop] (repeats for each stop in route)
  Driving → Parking → OccupyingDock → LoadingCargo|UnloadingCargo → ReleasingDock
  → [advance stop index]
  → if more stops: Driving (next destination)
  → if no more stops: ExitingVehicle → Done
```

**State transition diagram:**

```
Idle
 ↓ (F6 trigger)
WalkingToVehicle
 ↓
EnteringVehicle
 ↓
┌──────────────────────────────────────────────────┐
│ STOP LOOP (currentStopIndex 0..N-1)              │
│                                                  │
│  Driving → Parking → OccupyingDock               │
│    ↓                                             │
│  [if Pickup] → LoadingCargo → ReleasingDock      │
│  [if Dropoff] → UnloadingCargo → ReleasingDock   │
│    ↓                                             │
│  AdvanceToNextStop()                             │
│    ↓                                             │
│  [if more stops] → set _destination, → Driving   │
│  [if complete] → ExitingVehicle                  │
└──────────────────────────────────────────────────┘
 ↓
Done (clear all state)
```

### Refactored Decision Points

**`TransitionAfterParking()`** — simplified:

```csharp
private void TransitionAfterParking()
{
    if (_routeAssignment != null)
    {
        // Route mode: always occupy dock first
        SetState(DriverState.OccupyingDock);
    }
    else if (_sourceDock != null)
    {
        // Legacy F12 dock test (kept for backward compat during transition)
        SetState(DriverState.OccupyingDock);
    }
    else if (_sourceStorage == null)
    {
        SetState(DriverState.ExitingVehicle);
    }
    else if (_currentLeg == DeliveryLeg.Pickup)
    {
        SetState(DriverState.LoadingCargo);
    }
    else
    {
        SetState(DriverState.UnloadingCargo);
    }
}
```

**`EnterOccupyingDock()`** — use current stop's dock:

```csharp
private void EnterOccupyingDock()
{
    LoadingDock dock;
    if (_routeAssignment != null)
        dock = _routeAssignment.CurrentStop.ResolvedDock;
    else
        dock = (_currentLeg == DeliveryLeg.Pickup) ? _sourceDock : _destDock;

    // ... existing occupancy logic unchanged ...
}
```

**`EnterLoadingCargo()` / `EnterUnloadingCargo()`** — route mode transfers to/from vehicle:

```csharp
// Route-mode LoadingCargo:
// Transfer ALL items from dock's associated storage into vehicle
// (dock.OutputSlots reflects vehicle storage when parked — but for pickup
//  we need items FROM the dock. Pre-populated test items are in dock storage
//  or a nearby WorldStorageEntity.)

// Route-mode UnloadingCargo:
// Transfer ALL items from vehicle to a nearby WorldStorageEntity at this dock
```

**`EnterReleasingDock()`** — the key loop point:

```csharp
private void EnterReleasingDock()
{
    // Release current dock (same as now)
    LoadingDock dock = GetCurrentDock();
    dock.SetStaticOccupant(null);
    dock.VehicleDetector.Clear();

    if (_routeAssignment != null)
    {
        _routeAssignment.AdvanceToNextStop();

        if (_routeAssignment.IsComplete)
        {
            MelonLogger.Msg("Route complete (" + _routeAssignment.Route.Stops.Count + " stops)");
            SetState(DriverState.ExitingVehicle);
        }
        else
        {
            var nextStop = _routeAssignment.CurrentStop;
            _destination = nextStop.ResolvedParking;
            MelonLogger.Msg("Advancing to stop " + (_routeAssignment.CurrentStopIndex + 1) +
                "/" + _routeAssignment.Route.Stops.Count +
                " (" + nextStop.Action + " at " + nextStop.ResolvedDock.Name + ")");
            SetState(DriverState.Driving);
        }
    }
    else
    {
        // Legacy F12 path (unchanged)
        if (_currentLeg == DeliveryLeg.Pickup)
        {
            _destination = _destParkingLot;
            _currentLeg = DeliveryLeg.Delivery;
            SetState(DriverState.Driving);
        }
        else
        {
            SetState(DriverState.ExitingVehicle);
        }
    }
}
```

---

## 4. Cargo Tracking Through the Route

### Transfer Logic (Route Mode)

**Pickup stop:** Transfer items from a nearby `WorldStorageEntity` (or dock InputSlots source) into `_vehicle.Storage`.

**Dropoff stop:** Transfer items from `_vehicle.Storage` into a nearby `WorldStorageEntity`.

Both use the existing `TransferItems(source, destination, label)` method.

### Capacity Overflow Handling

If vehicle is full at a pickup stop: log warning, skip the transfer (leave items at dock), continue to next stop. Do NOT abort the route.

```csharp
if (CountOccupiedSlots(_vehicle.Storage) >= _vehicle.Storage.ItemSlots.Count)
{
    MelonLogger.Warning("ROUTE PICKUP: Vehicle full, skipping pickup at " + dock.Name);
    SetState(DriverState.ReleasingDock);
    return;
}
```

### Logging Plan

Each stop logs:
1. **Entry:** `"Stop N/M: ACTION at DockName (PropertyName)"`
2. **Pre-transfer:** Vehicle slot count, source/dest slot count
3. **Post-transfer:** Items transferred count, vehicle slot count after
4. **Exit:** `"Stop N/M complete"`

Route-level logs:
- Start: `"F6: Starting route 'Name' with M stops"`
- Complete: `"Route 'Name' complete — N stops executed in Xs"`
- Abort: `"Route 'Name' aborted at stop N/M: reason"`

---

## 5. Test Route Definition

### The 4-Stop Test Route

| Stop | Dock | Action | Items |
|------|------|--------|-------|
| 1 | Storage Unit Dock 1 (closest property) | Pickup | 5x "cash" (pre-populated) |
| 2 | Hyland Manor Dock 1 (second property) | Dropoff | Drops all vehicle cargo |
| 3 | Hyland Manor Dock 2 (or same property, different dock) | Pickup | 5x "baggie" (pre-populated, distinct from stop 1) |
| 4 | Storage Unit Dock 1 (same as stop 1 — back to origin) | Dropoff | Drops all vehicle cargo |

**Key verification points:**
- After stop 1: vehicle has 5x cash, storage unit dock is empty
- After stop 2: vehicle is empty, items at Hyland Manor
- After stop 3: vehicle has 5x baggie, second source is empty
- After stop 4: vehicle is empty, items back at storage unit dock. Same dock served as both pickup source (stop 1) and dropoff destination (stop 4).

### F7 Change: Grant All Dock Properties

The current F7 handler grants only enough properties to reach 2. For M5's 4-stop route (needing 3+ unique docks), update to grant ALL unowned properties that have loading docks:

```csharp
// Remove the "needed = Max(0, 2 - ownedWithDocks)" cap.
// Grant every unowned property that has at least one valid dock.
foreach (var prop in candidates)
{
    MelonLogger.Msg("F7: Granting ownership of '" + prop.PropertyName + "' ...");
    prop.SetOwned();
    granted++;
}
```

### F6 Test Handler

```csharp
public void TriggerRouteTest()
{
    // Standard guards: IsRunning, IsServer, Player.Local, vehicle, NPC

    // Find 4 docks across owned properties
    // Need: at least 2 properties with docks, one property needs 2+ docks
    // OR: 3 separate docks across 2+ properties (stop 4 reuses stop 1's dock)
    var docks = FindAllAvailableDocks(); // returns List<LoadingDock>
    if (docks.Count < 3) { error; return; } // 3 unique docks needed (stop 4 reuses stop 1)

    var dockA = docks[0]; // Stop 1 pickup, Stop 4 dropoff
    var dockB = docks[1]; // Stop 2 dropoff
    var dockC = docks[2]; // Stop 3 pickup (ideally on same property as dockB)

    // Build route
    var route = new Route("Test Round-Trip");
    route.Stops.Add(new RouteStop(dockA.GUID.ToString(), StopAction.Pickup));
    route.Stops.Add(new RouteStop(dockB.GUID.ToString(), StopAction.Dropoff));
    route.Stops.Add(new RouteStop(dockC.GUID.ToString(), StopAction.Pickup));
    route.Stops.Add(new RouteStop(dockA.GUID.ToString(), StopAction.Dropoff)); // back to origin

    // Resolve all stops (GUID → runtime references)
    var assignment = new RouteAssignment(route);
    if (!ResolveRouteStops(assignment)) return;

    // Pre-populate pickup sources with distinct items
    PopulateDockStorage(dockA, "cash", 5);   // Stop 1 source
    PopulateDockStorage(dockC, "baggie", 5); // Stop 3 source

    // Assign and start
    _routeAssignment = assignment;
    _destination = assignment.CurrentStop.ResolvedParking;
    SetState(DriverState.WalkingToVehicle);
}
```

### Pre-populating Dock Storage

For the test, place items in a `WorldStorageEntity` near each pickup dock. The existing `FindNearestWorldStorage()` method locates it during the stop. If no WorldStorageEntity exists near a dock, create test items directly in the vehicle at that stop (fallback behavior, logged as warning).

Alternative simpler approach: populate items directly in the vehicle before the route starts and treat stop 1 as a "fake" pickup that just logs what's already there. **No — this defeats the purpose of testing multi-pickup.** Instead, use the game's actual storage near docks.

**Fallback if no WorldStorageEntity near a pickup dock:** Use `dock.InputSlots` if populated, or skip with warning.

---

## 6. Refactor Scope

### Files Modified

| File | Changes |
|------|---------|
| `DeliveryDriverBehaviour.cs` | Add `_routeAssignment` field. Add `TriggerRouteTest()`. Modify `EnterOccupyingDock`, `EnterLoadingCargo`, `EnterUnloadingCargo`, `EnterReleasingDock`, `TransitionAfterParking`, `EnterDone`, `ResetState` to check `_routeAssignment`. Add `ResolveRouteStops()`, `FindAllAvailableDocks()`, `PopulateDockStorage()`. |
| `DeliveryDriversMod.cs` | Add F6 key handler calling `TriggerRouteTest()`. Update F7 `GrantPropertyOwnership()` to grant ALL properties with docks (not just enough to reach 2). |

### Files Created

| File | Purpose |
|------|---------|
| `Route.cs` | `StopAction` enum, `RouteStop` class, `Route` class, `RouteAssignment` class. |

### Files Unchanged

| File | Reason |
|------|--------|
| `NPCSpawner.cs` | No changes needed for routes. |
| `SpawnedNPCData.cs` | No changes needed. |

### Backward Compatibility

The F10, F11, F12 tests remain functional. The route mode is triggered only by `_routeAssignment != null`. When `_routeAssignment` is null, all existing code paths execute identically.

---

## 7. Driver-Route Relationship

```
┌─────────────────────┐     holds 0..1      ┌──────────────────┐
│ DeliveryDriverBehav. │ ──────────────────→ │  RouteAssignment  │
│                     │                      │  - Route          │
│  _routeAssignment   │                      │  - CurrentStopIdx │
└─────────────────────┘                      └──────────────────┘
                                                      │
                                              references 1
                                                      │
                                                      ▼
                                              ┌──────────────┐
                                              │    Route      │
                                              │  - Name       │
                                              │  - Stops[]    │
                                              └──────────────┘
```

- A driver holds **at most one** `RouteAssignment` (enforced by the `IsRunning` guard — can't start a new route while one is active).
- `Route` is a pure data object (reusable definition). `RouteAssignment` is execution state (tracks progress).
- In M5, routes are created inline by the test handler. In M7+, routes will be stored persistently and assigned to drivers via a scheduling system.
- The `Route` object is not modified during execution. Multiple drivers could theoretically share the same `Route` definition (different `RouteAssignment` instances) — not needed for M5 but the model supports it.

---

## 8. Implementation Sequence

1. **Create `Route.cs`** with data types. Compiles independently, no behavior.
2. **Add `_routeAssignment` field** and `ResolveRouteStops()` to `DeliveryDriverBehaviour.cs`.
3. **Modify transition logic** in `TransitionAfterParking`, `EnterOccupyingDock`, `EnterLoadingCargo`, `EnterUnloadingCargo`, `EnterReleasingDock` to handle route mode.
4. **Add `TriggerRouteTest()`** and helpers (`FindAllAvailableDocks`, `PopulateDockStorage`).
5. **Wire F6 key** in `DeliveryDriversMod.cs`.
6. **Update `EnterDone()` and `ResetState()`** to clear `_routeAssignment`.
7. **Test.** Run F7 → F8 → F9 → F6 in-game. Verify all 4 stops complete.

---

## 9. Open Questions (Resolved)

| Question | Answer |
|----------|--------|
| Does LoadingDock have a GUID? | **Yes.** `BakedGUID` field, implements `IGUIDRegisterable`, registered with `GUIDManager` on Awake. Lookup via `GUIDManager.GetObject<LoadingDock>(guid)`. |
| How to find items at a pickup dock? | Use `FindNearestWorldStorage(dock.transform.position)` — same pattern as M4's unload. Pre-populate that storage in the test setup. |
| What if a dock has no nearby WorldStorageEntity? | For pickup: log warning, skip (vehicle stays empty for that stop). For dropoff: log warning, items remain in vehicle. Route continues. |
| Keep F10/F11/F12 tests? | Yes. They exercise simpler code paths and serve as regression tests. Route mode is additive. |
| New `DriverState` enum values? | No. Existing states suffice — the loop is expressed through transition logic, not new states. |

---

## 10. Risk Assessment

| Risk | Mitigation |
|------|-----------|
| State machine becomes spaghetti with route-mode branches | Each decision point checks `_routeAssignment != null` first and takes the route path; else falls through to legacy logic. Clear separation. |
| Dock GUID resolution fails at runtime | `ResolveRouteStops()` validates ALL stops before starting. Fails fast with clear error. |
| Same dock used twice in route (stop 1 and 4) causes state issues | Dock occupancy is set/cleared per-stop. After stop 1 releases, the dock is clean for stop 4 to re-occupy. Tested explicitly. |
| Vehicle full at pickup | Log and skip. Don't abort. Tested if all slots are occupied before transfer. |
| No WorldStorageEntity near a test dock | Test setup uses F7 to grant properties that have storage. If still missing, log and skip that stop's transfer. |
