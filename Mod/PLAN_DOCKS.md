# Loading Dock Integration Milestone Plan (M4)

## Goal

Replace the plain `StorageEntity` containers from M3 with real `LoadingDock` instances. Driver navigates to a dock's parking lot, parks, becomes the dock's occupant, transfers cargo through the vehicle's storage (the dock's output interface), departs, drives to a destination dock, parks, transfers cargo out, exits.

**Trigger:** Player presses F12 in-game.
**F10 simple drive test and F11 cargo test continue to work unchanged.**

---

## 1. Dock Discovery

### Approach: Enumerate `LoadingDock` instances via `Property.OwnedProperties`

Every `Property` has a `LoadingDock[] LoadingDocks` field (Property.cs L1025). `Property.OwnedProperties` is a static `List<Property>` (L942) populated at runtime. We enumerate all owned properties' docks to find two suitable ones.

```csharp
var docks = Property.OwnedProperties
    .Where(p => p != null && p.LoadingDocks != null)
    .SelectMany(p => p.LoadingDocks)
    .Where(d => d != null && d.Parking != null && d.Parking.EntryPoint != null)
    .ToList();
```

If `OwnedProperties` yields fewer than 2 docks, log an error and abort. The player should use **F7** (see §1b below) to grant ownership of properties first.

**Selection logic:**

1. Pick source dock: closest to player/vehicle position (by `dock.Parking.EntryPoint`)
2. Pick destination dock: different property, prefer >30m separation from source dock's parking entry. Fallback: any dock at a different property.

**Abort conditions:**
- Fewer than 2 `LoadingDock` instances found → log error, abort
- Same property for both → acceptable (if property has 2+ docks), but prefer different properties

**Diagnostic logging:**
```
Found N LoadingDock(s) across M properties
  Source: <dock.Name> at property <property.PropertyName>, parking entry at <pos>
  Dest:   <dock.Name> at property <property.PropertyName>, parking entry at <pos>
```

**New namespace import:** `using ScheduleOne.Property;` (for `Property` class) and `using ScheduleOne.Delivery;` (for `LoadingDock` class).

---

## 1b. Property Ownership Cheat (F7)

### Problem

A new save won't have 2 owned properties with loading docks. Rather than silently falling back to unowned properties, we bind **F7** to grant ownership of unowned properties that have docks.

### Approach

`Property.SetOwned()` is the public API. It calls through `SetOwned_Server()` → `ReceiveOwned_Networked()` → `RecieveOwned()`, which:
- Sets `IsOwned = true`
- Moves the property from `UnownedProperties` to `OwnedProperties`
- Sets variable database flag
- Hides the For Sale sign
- Fires `onPropertyAcquired` event

### Implementation

In `DeliveryDriversMod.OnUpdate()`:

```csharp
if (Input.GetKeyDown(KeyCode.F7))
{
    GrantPropertyOwnership();
}
```

```csharp
private void GrantPropertyOwnership()
{
    if (!InstanceFinder.IsServer)
    {
        MelonLogger.Warning("Cannot grant ownership: not server");
        return;
    }

    // Find unowned properties that have loading docks
    var candidates = Property.UnownedProperties
        .Where(p => p != null && p.LoadingDocks != null && p.LoadingDocks.Length > 0)
        .Where(p => p.LoadingDocks.Any(d => d != null && d.Parking != null))
        .ToList();

    // Count how many owned properties already have docks
    int ownedWithDocks = Property.OwnedProperties
        .Count(p => p != null && p.LoadingDocks != null && p.LoadingDocks.Length > 0
            && p.LoadingDocks.Any(d => d != null && d.Parking != null));

    int needed = Math.Max(0, 2 - ownedWithDocks);

    if (needed == 0)
    {
        MelonLogger.Msg("F7: Already own " + ownedWithDocks +
            " properties with loading docks — no grant needed");
        return;
    }

    if (candidates.Count < needed)
    {
        MelonLogger.Warning("F7: Need " + needed +
            " more properties with docks, but only " + candidates.Count + " unowned candidates exist");
    }

    int granted = 0;
    foreach (var prop in candidates.Take(needed))
    {
        MelonLogger.Msg("F7: Granting ownership of '" + prop.PropertyName +
            "' (code: " + prop.PropertyCode + ", docks: " + prop.LoadingDockCount + ")");
        prop.SetOwned();
        granted++;
    }

    MelonLogger.Msg("F7: Granted " + granted + " properties. Total owned with docks: " +
        (ownedWithDocks + granted));
}
```

### Notes

- `Property.PropertyName` (public getter, L61) returns the display name
- `Property.PropertyCode` (needs verification — may be private field `propertyCode` L960). If private, use `prop.name` (GameObject name) instead
- `Property.LoadingDockCount` (public getter, L86) returns `LoadingDocks.Length`
- Calling `SetOwned()` on an already-owned property is safe (`RecieveOwned` returns early if `IsOwned`)
- F7 is idempotent — pressing it when 2+ docked properties are already owned does nothing

### Test workflow

```
1. Load a fresh save (no properties owned)
2. Press F7 → grants 2 properties with docks
3. Press F9 → spawn NPC
4. Press F8 → spawn vehicle
5. Press F12 → run dock test
```

---

## 2. Test Item Population

Same approach as M3: populate the **vehicle's storage** with test items before the state machine starts. We pre-load the vehicle because:

- The dock's `OutputSlots` are just references to the vehicle's `Storage.ItemSlots` (see Critical Question #2 below)
- There is no separate "dock storage" to populate — the dock is an interface to the vehicle
- We transfer items **from vehicle → destination dock's vehicle storage** at the destination, which means we need items in the vehicle from the start

Wait — this changes the flow from M3. In M3, items were in a source `WorldStorageEntity` and the driver loaded them into the vehicle. For dock integration, the flow is:

**Source dock:** Vehicle parks at source dock. Items should already be at the source dock (in the dock's "input"). But `LoadingDock.InputSlots` is just a list that gets cleared/repopulated dynamically. There's no persistent dock storage.

**Revised approach:** Pre-populate the vehicle's storage with test items. The driver:
1. Drives to source dock (simulating having picked up goods)
2. Parks and registers as occupant (making items visible through dock UI for verification)
3. Drives to destination dock
4. Parks, registers as occupant, transfers items from vehicle → a `WorldStorageEntity` near the destination (simulating unloading to a property's storage)

**Simpler alternative:** Since the real goal is testing dock occupancy mechanics (park, occupy, release), we can:
1. Pre-populate vehicle storage with items
2. Drive to dock A, park, occupy, log that dock shows vehicle's items via OutputSlots, release, depart
3. Drive to dock B, park, occupy, transfer items from vehicle to a nearby WorldStorageEntity (if one exists) or just log the items are accessible, release, depart

**Chosen approach:** Keep it closest to the future real flow. Pre-populate vehicle storage. At source dock: just park/occupy/verify/release (simulating a pickup that already happened). At destination dock: park/occupy, transfer items from vehicle.Storage into a nearby `WorldStorageEntity` if available, release. This tests dock occupancy at both ends while reusing M3's transfer code.

Actually, the simplest thing that tests all the dock mechanics:

1. Pre-populate source dock area: find a `WorldStorageEntity` near source dock, populate it with items (reuse M3 pattern)
2. Park at source dock, occupy it, transfer items from nearby storage → vehicle
3. Drive to dest dock
4. Park at dest dock, occupy it, transfer items from vehicle → nearby storage at dest
5. Release and exit

This requires `WorldStorageEntity` instances near docks. Most properties have storage containers. If none exist near a dock, we skip the transfer and just test park/occupy/release.

**Final decision:** Pre-populate vehicle storage with test items (same as M3). At source dock: park, set occupant, log OutputSlots match vehicle items, release. At dest dock: park, set occupant, log OutputSlots, release. Transfer is vehicle→vehicle conceptually; the dock integration is about occupancy, not storage. Items stay in the vehicle the whole time. This is the minimum test of dock mechanics.

If we want to also test transfer: at the destination dock, transfer items from vehicle storage to a `WorldStorageEntity` near the destination dock (if one exists within 50m). This validates cargo offloading.

---

## 3. Critical Questions — Resolved

### Q1: Dock Occupancy Timing

**Finding:** `SetStaticOccupant(vehicle)` (LoadingDock.cs L175-178) simply sets the `StaticOccupant` property. `RefreshOccupant()` (L126-148) runs every 1 second and:
- Sets/clears `DynamicOccupant` based on `VehicleDetector.closestVehicle` speed < 2 km/h
- Clears `StaticOccupant` only if `StaticOccupant.IsVisible == false` (L137-140)

**Decision:** Use `SetStaticOccupant(vehicle)`. Our vehicle is visible (we drove it there), so `RefreshOccupant` will NOT clear our static occupancy. This is exactly what `DeliveryVehicle.Activate()` does (DeliveryVehicle.cs L37). No Harmony patch needed.

**Note:** Our vehicle will also be detected as `DynamicOccupant` by `VehicleDetector` (since it's parked and speed < 2 km/h). This is fine — `IsInUse` checks either occupant, and having both set doesn't cause issues. The dynamic occupant will populate `OutputSlots` with our vehicle's storage slots (LoadingDock.cs L170), which is desirable for UI consistency.

### Q2: OutputSlots vs Direct Storage Transfer

**Finding:** `LoadingDock.SetOccupant()` (L151-172, private method called by `RefreshOccupant`):
```csharp
private void SetOccupant(LandVehicle occupant) {
    this.DynamicOccupant = occupant;
    this.InputSlots.Clear();
    this.OutputSlots.Clear();
    if (this.DynamicOccupant != null) {
        this.OutputSlots.AddRange(this.DynamicOccupant.Storage.ItemSlots);
    }
}
```

`OutputSlots` are just **references to the vehicle's `Storage.ItemSlots`**. There is no separate dock storage. The dock is a UI window into the vehicle's trunk.

**Decision:** Transfer items directly via `vehicle.Storage` (same as M3). The dock's `OutputSlots` are just aliases. Our existing `TransferItems(source, destination)` code works unchanged — we pass `_vehicle.Storage` as source or destination. No dock-specific transfer API needed.

### Q3: Dock Release / Vehicle Departure

**Finding from `DeliveryVehicle.Deactivate()` (L50-63):**
```csharp
this.Vehicle.ExitPark(false);
this.Vehicle.SetVisible(false);
this.ActiveDelivery.LoadingDock.SetStaticOccupant(null);
this.ActiveDelivery.LoadingDock.VehicleDetector.Clear();
```

Release sequence:
1. `Vehicle.ExitPark(moveToExitPoint)` — clears parking lot reference, clears spot occupant
2. `dock.SetStaticOccupant(null)` — clears our static occupancy
3. `dock.VehicleDetector.Clear()` — clears vehicle list so `RefreshOccupant` doesn't re-detect

**Our release sequence (driver doesn't disappear, just drives away):**
1. `dock.SetStaticOccupant(null)` — release static occupancy
2. `dock.VehicleDetector.Clear()` — prevent re-detection as DynamicOccupant during departure
3. Then the existing `EnterDriving()` handles `ExitPark_Networked()` before navigating

**Decision:** Call `SetStaticOccupant(null)` + `VehicleDetector.Clear()` before transitioning to Driving. The existing `EnterDriving()` already handles `ExitPark_Networked()`. No new methods needed.

### Q4: Vanilla Delivery Coexistence

**Finding:** `DeliveryManager.IsLoadingBayFree(Property, int loadingDockIndex)` (L201-203) checks `!destination.LoadingDocks[loadingDockIndex].IsInUse`. Our `SetStaticOccupant(vehicle)` will cause `IsInUse == true`, which will delay vanilla deliveries to that specific dock.

**Risk assessment:**
- We occupy each dock briefly (seconds, not minutes)
- Vanilla deliveries check `IsLoadingBayFree` once per game minute in `OnTimePass`
- If our driver is at a dock during that check, the vanilla delivery enters `Waiting` state and retries next minute
- This is acceptable behavior — it's realistic (dock is busy) and temporary

**Mitigation:** Pick docks that aren't actively receiving a vanilla delivery. We could check `dock.IsInUse` before selecting, but for this test milestone it's unnecessary. Just log a warning if the selected dock is already in use.

**Verification plan:** After the dock test runs, order a cheap item from a shop that delivers to a different property's dock. Confirm the delivery arrives normally (vehicle appears, items are in trunk). This is a manual verification step, not automated.

---

## 4. State Machine Changes

### New States

```csharp
public enum DriverState
{
    Idle,
    WalkingToVehicle,
    EnteringVehicle,
    Driving,
    Parking,
    LoadingCargo,
    UnloadingCargo,
    OccupyingDock,      // NEW: set dock occupancy after parking
    ReleasingDock,       // NEW: clear dock occupancy before driving away
    ExitingVehicle,
    Done
}
```

### New Fields

```csharp
// Dock test fields (null when running F10 or F11 tests)
private LoadingDock _sourceDock;
private LoadingDock _destDock;
```

The existing `_sourceParkingLot` / `_destParkingLot` fields are reused — they're set to `dock.Parking` references.

### State Flow (F12 Dock Test)

```
F12 (dock test):
Idle → WalkingToVehicle → EnteringVehicle
  → Driving(→ source dock parking) → Parking(source)
  → OccupyingDock(source) → LoadingCargo
  → ReleasingDock(source) → Driving(→ dest dock parking)
  → Parking(dest)
  → OccupyingDock(dest) → UnloadingCargo
  → ReleasingDock(dest) → ExitingVehicle → Done
```

### OccupyingDock State

```csharp
private void EnterOccupyingDock()
{
    LoadingDock dock = (_currentLeg == DeliveryLeg.Pickup) ? _sourceDock : _destDock;

    MelonLogger.Msg("Setting dock occupancy: " + dock.Name +
        " (property: " + dock.ParentProperty.PropertyName + ")");

    dock.SetStaticOccupant(_vehicle);

    MelonLogger.Msg("  StaticOccupant set: " + (dock.StaticOccupant != null));
    MelonLogger.Msg("  IsInUse: " + dock.IsInUse);
    MelonLogger.Msg("  Vehicle storage slots: " + _vehicle.Storage.ItemSlots.Count);

    // Transition to cargo transfer
    if (_currentLeg == DeliveryLeg.Pickup)
        SetState(DriverState.LoadingCargo);
    else
        SetState(DriverState.UnloadingCargo);
}
```

### ReleasingDock State

```csharp
private void EnterReleasingDock()
{
    LoadingDock dock = (_currentLeg == DeliveryLeg.Pickup) ? _sourceDock : _destDock;

    MelonLogger.Msg("Releasing dock: " + dock.Name);

    dock.SetStaticOccupant(null);
    dock.VehicleDetector.Clear();

    MelonLogger.Msg("  StaticOccupant cleared: " + (dock.StaticOccupant == null));
    MelonLogger.Msg("  IsInUse: " + dock.IsInUse);

    if (_currentLeg == DeliveryLeg.Pickup)
    {
        // Switch to delivery leg
        _destination = _destParkingLot;
        _currentLeg = DeliveryLeg.Delivery;
        SetState(DriverState.Driving);
    }
    else
    {
        SetState(DriverState.ExitingVehicle);
    }
}
```

### Modified: LoadingCargo (dock-aware)

When `_sourceDock != null`, we're in dock mode. "Loading" means verifying items are in the vehicle (pre-populated) and logging the dock's OutputSlots state:

```csharp
private void EnterLoadingCargo()
{
    if (_sourceDock != null)
    {
        // Dock mode: items are pre-populated in vehicle.
        // Log that dock OutputSlots reflect vehicle storage.
        MelonLogger.Msg("DOCK LOAD: Vehicle has " + CountOccupiedSlots(_vehicle.Storage) + " occupied slots");
        MelonLogger.Msg("DOCK LOAD: Dock OutputSlots count = " + _sourceDock.OutputSlots.Count);

        // No transfer needed — items are already in the vehicle
        SetState(DriverState.ReleasingDock);
    }
    else
    {
        // M3 cargo test mode: transfer from external storage
        MelonLogger.Msg("Loading cargo from source storage...");
        int count = TransferItems(_sourceStorage, _vehicle.Storage, "LOAD");
        if (count == 0)
            MelonLogger.Warning("Source was empty — nothing to deliver");

        _destination = _destParkingLot;
        _currentLeg = DeliveryLeg.Delivery;
        SetState(DriverState.Driving);
    }
}
```

### Modified: UnloadingCargo (dock-aware)

When `_destDock != null`, log dock state and then optionally transfer to a nearby `WorldStorageEntity`:

```csharp
private void EnterUnloadingCargo()
{
    if (_destDock != null)
    {
        // Dock mode: log dock state
        MelonLogger.Msg("DOCK UNLOAD: Vehicle has " + CountOccupiedSlots(_vehicle.Storage) + " occupied slots");
        MelonLogger.Msg("DOCK UNLOAD: Dock OutputSlots count = " + _destDock.OutputSlots.Count);

        // Transfer items from vehicle to a nearby WorldStorageEntity if available
        var nearbyStorage = FindNearestWorldStorage(_destDock.transform.position, 50f);
        if (nearbyStorage != null)
        {
            MelonLogger.Msg("DOCK UNLOAD: Found nearby storage " + nearbyStorage.name + ", transferring...");
            int count = TransferItems(_vehicle.Storage, nearbyStorage, "DOCK_UNLOAD");
            MelonLogger.Msg("DOCK UNLOAD: Transferred " + count + " items to " + nearbyStorage.name);
        }
        else
        {
            MelonLogger.Msg("DOCK UNLOAD: No nearby WorldStorageEntity — items remain in vehicle");
        }

        SetState(DriverState.ReleasingDock);
    }
    else
    {
        // M3 cargo test mode
        MelonLogger.Msg("Unloading cargo to destination storage...");
        int count = TransferItems(_vehicle.Storage, _destStorage, "UNLOAD");
        if (count == 0)
            MelonLogger.Warning("Vehicle was empty — nothing to unload");

        SetState(DriverState.ExitingVehicle);
    }
}
```

### Modified: TransitionAfterParking

Add dock-aware branch:

```csharp
private void TransitionAfterParking()
{
    if (_sourceDock != null)
    {
        // Dock test (F12) — occupy dock before cargo
        SetState(DriverState.OccupyingDock);
    }
    else if (_sourceStorage == null)
    {
        // Simple drive test (F10)
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

### Helper: FindNearestWorldStorage

```csharp
private WorldStorageEntity FindNearestWorldStorage(Vector3 position, float maxDistance)
{
    return WorldStorageEntity.All
        .Where(s => s != null && s.gameObject.activeInHierarchy)
        .Where(s => Vector3.Distance(s.transform.position, position) < maxDistance)
        .OrderBy(s => Vector3.Distance(s.transform.position, position))
        .FirstOrDefault();
}
```

### Helper: CountOccupiedSlots

```csharp
private int CountOccupiedSlots(StorageEntity storage)
{
    int count = 0;
    foreach (var slot in storage.ItemSlots)
        if (slot.ItemInstance != null) count++;
    return count;
}
```

---

## 5. Entry Point: `TriggerDockTest()`

```csharp
public void TriggerDockTest()
{
    // Same guards as other triggers: IsRunning, IsServer, Player.Local

    // Find vehicle (reuse FindNearestPlayerVehicle)
    // Check vehicle.Storage exists

    // Find NPC (reuse GetLastSpawnedNPC)

    // Find two LoadingDock instances
    if (!FindDockLocations(vehicle.transform.position, out LoadingDock srcDock, out ParkingLot srcLot,
            out LoadingDock dstDock, out ParkingLot dstLot))
        return;

    // Pre-populate vehicle with test items
    if (!PopulateSourceStorage(vehicle.Storage))
        return;

    // Log setup
    // Set references: _npc, _vehicle, _sourceDock, _destDock
    // Set _sourceParkingLot = srcLot, _destParkingLot = dstLot
    // _destination = srcLot, _currentLeg = Pickup

    SetState(DriverState.WalkingToVehicle);
}
```

### FindDockLocations

```csharp
private bool FindDockLocations(Vector3 vehiclePos,
    out LoadingDock srcDock, out ParkingLot srcLot,
    out LoadingDock dstDock, out ParkingLot dstLot)
{
    srcDock = null; srcLot = null; dstDock = null; dstLot = null;

    // Enumerate docks from owned properties only
    var docks = new List<LoadingDock>();
    foreach (var prop in Property.OwnedProperties)
    {
        if (prop?.LoadingDocks == null) continue;
        foreach (var dock in prop.LoadingDocks)
        {
            if (dock?.Parking?.EntryPoint != null)
                docks.Add(dock);
        }
    }

    MelonLogger.Msg("Found " + docks.Count + " LoadingDock(s) across " +
        Property.OwnedProperties.Count + " owned properties");

    if (docks.Count < 2)
    {
        MelonLogger.Error("Need at least 2 LoadingDocks on owned properties. Found " + docks.Count +
            ". Press F7 to grant ownership of properties with docks.");
        return false;
    }

    // Log all found docks for debugging
    foreach (var d in docks)
    {
        MelonLogger.Msg("  Dock: " + d.Name + " at " + d.ParentProperty.PropertyName +
            ", parking entry: " + d.Parking.EntryPoint.position +
            ", inUse: " + d.IsInUse);
    }

    // Pick source: closest parking entry to vehicle
    var source = docks
        .OrderBy(d => Vector3.Distance(d.Parking.EntryPoint.position, vehiclePos))
        .First();

    // Pick dest: different dock, prefer >30m separation, prefer different property
    LoadingDock dest = docks
        .Where(d => d != source)
        .Where(d => Vector3.Distance(d.Parking.EntryPoint.position,
                        source.Parking.EntryPoint.position) > MIN_DESTINATION_DIST)
        .OrderBy(d => Vector3.Distance(d.Parking.EntryPoint.position, vehiclePos))
        .FirstOrDefault();

    // Fallback: any different dock
    if (dest == null)
        dest = docks.FirstOrDefault(d => d != source);

    if (dest == null)
    {
        MelonLogger.Error("Could not find a second dock different from source");
        return false;
    }

    // Warn if either dock is currently in use
    if (source.IsInUse)
        MelonLogger.Warning("Source dock is currently in use — may conflict with vanilla delivery");
    if (dest.IsInUse)
        MelonLogger.Warning("Dest dock is currently in use — may conflict with vanilla delivery");

    srcDock = source;
    srcLot = source.Parking;
    dstDock = dest;
    dstLot = dest.Parking;
    return true;
}
```

---

## 6. Cleanup in `EnterDone()`

Extend existing cleanup:

```csharp
_sourceDock = null;     // NEW
_destDock = null;       // NEW
```

Also add to `ResetState()`.

---

## 7. F12 Handler in `DeliveryDriversMod.cs`

```csharp
if (Input.GetKeyDown(KeyCode.F12))
{
    var driver = DeliveryDriverBehaviour.Instance;
    if (driver != null && !driver.IsRunning)
    {
        driver.TriggerDockTest();
    }
    else if (driver != null && driver.IsRunning)
    {
        MelonLogger.Msg("Test already in progress");
    }
}
```

---

## 8. Files Modified

| File | Changes |
|------|---------|
| `Mod/Source/DeliveryDriverBehaviour.cs` | Add `OccupyingDock` + `ReleasingDock` states, dock fields, `TriggerDockTest()`, `FindDockLocations()`, `FindNearestWorldStorage()`, `CountOccupiedSlots()`. Modify `TransitionAfterParking()`, `EnterLoadingCargo()`, `EnterUnloadingCargo()`, `EnterDone()`, `ResetState()`. Add `using ScheduleOne.Property` and `using ScheduleOne.Delivery`. |
| `Mod/Source/DeliveryDriversMod.cs` | Add F7 property ownership handler (`GrantPropertyOwnership()`), add F12 dock test handler. Add `using ScheduleOne.Property` and `using ScheduleOne.Delivery`. |

No new files. No Harmony patches.

---

## 9. Logging Plan

```
F7: Granting ownership of 'Motel' (code: motel, docks: 1)
F7: Granting ownership of 'Sweatshop' (code: sweatshop, docks: 1)
F7: Granted 2 properties. Total owned with docks: 2

F12: Starting dock test
  Found N LoadingDock(s) across M owned properties
    Dock: Loading Dock 1 at PropertyName, parking entry: <pos>, inUse: false
    Dock: Loading Dock 2 at PropertyName, parking entry: <pos>, inUse: false
  Source: Loading Dock 1 (PropertyA), parking entry at <pos>
  Dest:   Loading Dock 2 (PropertyB), parking entry at <pos>
  Vehicle: <name> at <pos>
  NPC: <name>
  Populated vehicle with 5 cash
State: Idle → WalkingToVehicle
State: WalkingToVehicle → EnteringVehicle
State: EnteringVehicle → Driving
  Starting navigation to <source parking entry pos>
State: Driving → Parking (source dock)
  Vehicle parked at spot 0
State: Parking → OccupyingDock
  Setting dock occupancy: Loading Dock 1 (property: PropertyA)
    StaticOccupant set: true
    IsInUse: true
    Vehicle storage slots: 16
State: OccupyingDock → LoadingCargo
  DOCK LOAD: Vehicle has 5 occupied slots
  DOCK LOAD: Dock OutputSlots count = 16
State: LoadingCargo → ReleasingDock
  Releasing dock: Loading Dock 1
    StaticOccupant cleared: true
    IsInUse: false
State: ReleasingDock → Driving
  Starting navigation to <dest parking entry pos>
State: Driving → Parking (dest dock)
  Vehicle parked at spot 0
State: Parking → OccupyingDock
  Setting dock occupancy: Loading Dock 2 (property: PropertyB)
    StaticOccupant set: true
    IsInUse: true
    Vehicle storage slots: 16
State: OccupyingDock → UnloadingCargo
  DOCK UNLOAD: Vehicle has 5 occupied slots
  DOCK UNLOAD: Dock OutputSlots count = 16
  DOCK UNLOAD: Found nearby storage <name>, transferring...
  DOCK_UNLOAD: source has 5 occupied slot(s)
  DOCK_UNLOAD: transferred 5 item(s)
State: UnloadingCargo → ReleasingDock
  Releasing dock: Loading Dock 2
    StaticOccupant cleared: true
    IsInUse: false
State: ReleasingDock → ExitingVehicle
  NPC exiting vehicle
State: ExitingVehicle → Done
  Dock test complete: NPC exited at <pos>
```

---

## 10. Vanilla Coexistence Verification Plan

**Manual test after dock test completes:**

1. Open phone → go to a shop (e.g., Gas Station)
2. Order a cheap item for delivery to a property whose dock our test did NOT use (or wait for our test to fully release both docks)
3. Wait for the delivery timer to expire
4. Verify: delivery vehicle appears at the property's dock, items are in the vehicle's trunk
5. Unload items to confirm the full vanilla flow works

**What could go wrong:**
- If our test is still running when a vanilla delivery tries to arrive at the same dock, `IsLoadingBayFree` returns false, and the delivery enters `Waiting` state. This is correct behavior (dock busy), not a bug. It'll retry next minute.
- If we somehow leave `StaticOccupant` set (crash/abort during test), the dock stays "in use" permanently. Our `EnterDone()` cleanup mitigates this — we always clear dock references. But a crash would leave stale state. Future milestone (M6 persistence) would handle this.

---

## 11. Risks

| Risk | Likelihood | Mitigation |
|------|-----------|------------|
| Fewer than 2 owned properties with docks | Medium | F7 grants ownership. Abort with clear message if F7 wasn't pressed first. |
| `dock.Parking.EntryPoint` not reachable on vehicle graph | Low | Validated in M2 that ParkingLot entry points are on the road graph. Dock parking lots are the same type. |
| `SetStaticOccupant` cleared by `RefreshOccupant` | Very Low | Only cleared when `!IsVisible` (L137-140). Our vehicle is visible. Confirmed by reading source. |
| `VehicleDetector.Clear()` causes issues with DynamicOccupant | Low | `Clear()` just empties the vehicle list. Next `RefreshOccupant` tick will re-detect if vehicle is still in range — but by then we should be driving away. |
| No `WorldStorageEntity` near destination dock | Medium | Log and skip transfer. Items stay in vehicle. Not a test failure. |
| `Property.PropertyName` or `PropertyCode` accessor issue | Very Low | Both confirmed as public getters (L61, L71). No issue expected. |
| Dock `OutputSlots` not populated because we use `SetStaticOccupant` (not dynamic) | Medium | `OutputSlots` are populated by `SetOccupant()` (private, for dynamic only). Static occupant doesn't trigger `OutputSlots` population. This is fine — we access `_vehicle.Storage` directly. Log OutputSlots count may show 0, which is expected for static occupancy. Document this. |

---

## 12. Success Criteria

- [ ] F7 grants ownership of unowned properties with loading docks (idempotent)
- [ ] F12 with nearby vehicle + spawned NPC starts the dock test
- [ ] F12 aborts with clear message if fewer than 2 owned properties have docks
- [ ] Two `LoadingDock` instances are discovered and logged with property names
- [ ] Vehicle is pre-populated with 5 cash items
- [ ] NPC walks to vehicle and enters
- [ ] Vehicle drives to source dock's parking lot entry point
- [ ] Vehicle parks at source dock's parking spot
- [ ] `SetStaticOccupant(vehicle)` sets the dock's occupant (logged)
- [ ] `dock.IsInUse` is true while occupied (logged)
- [ ] Dock is released cleanly (`SetStaticOccupant(null)` + `VehicleDetector.Clear()`)
- [ ] Vehicle drives to destination dock's parking lot
- [ ] Vehicle parks at destination dock's parking spot
- [ ] Destination dock occupied and released cleanly
- [ ] If `WorldStorageEntity` exists near dest dock, items transfer from vehicle to it
- [ ] NPC exits vehicle
- [ ] Logs at each phase including dock occupancy state
- [ ] F10 and F11 tests still work unchanged
- [ ] No unhandled exceptions
- [ ] Vanilla shop delivery to a different dock still works (manual verification)

---

## 13. What This Does NOT Cover

Per scope rules:
- No save/load of dock-related driver state (M6)
- No schedule-based triggering (M7)
- No wages (M8)
- No UI for selecting docks (M9)
- No automatic dock selection by property ownership
- No round-trip / multi-stop routes (M5)
- No handling dock unavailability (just log warning if dock is in use)
- No replacing or intercepting the vanilla delivery system
- No Harmony patches
