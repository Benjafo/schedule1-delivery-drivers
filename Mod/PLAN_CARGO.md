# Cargo Transfer Milestone Plan (M3)

## Goal

Extend the M2 driver state machine to: drive to a source location, load cargo from a `StorageEntity`, drive to a destination location, and unload cargo into a different `StorageEntity`.

**Trigger:** Player presses F11 in-game.
**F10 simple drive test continues to work unchanged.**

---

## 1. Source/Destination Strategy

### Approach: Discover existing `WorldStorageEntity` containers at runtime

`WorldStorageEntity` maintains a static list `WorldStorageEntity.All` tracking every world storage container (crates, lockers, property storage, etc.). At F11 press, we enumerate this list, pair each with its nearest ParkingLot, and select two suitable containers.

**Selection logic:**

```csharp
// 1. Get all WorldStorageEntities
var allStorage = WorldStorageEntity.All;  // static List<WorldStorageEntity>

// 2. For each, find nearest ParkingLot with free spots
var candidates = allStorage
    .Where(s => s != null && s.gameObject.activeInHierarchy)
    .Select(s => new {
        storage = s,
        nearestLot = FindNearestParkingLot(s.transform.position),
        lotDistance = /* distance to nearest lot */
    })
    .Where(c => c.nearestLot != null && c.lotDistance < 50f)  // Must have a ParkingLot within 50m
    .OrderBy(c => c.lotDistance)
    .ToList();

// 3. Pick source = first candidate
// 4. Pick destination = first candidate that's >30m from source (by ParkingLot separation)
```

**Why this approach:**

- `StorageEntity` is a `NetworkBehaviour` — cannot be reliably instantiated from scratch without a prefab
- `WorldStorageEntity.All` is a static list populated automatically by `WorldStorageEntity.Awake()`
- No UI needed — pure automated discovery
- Works in any save game that has placed storage containers in the world

**Abort conditions:**

- `WorldStorageEntity.All` is empty → log error listing count, abort
- Fewer than 2 candidates with ParkingLots within 50m → log error with diagnostics, abort

**Diagnostic logging at F11 press:**

```
Found N WorldStorageEntities in world
  Source: <name> at <pos>, nearest ParkingLot at <dist>m
  Destination: <name> at <pos>, nearest ParkingLot at <dist>m
```

### Helper: `FindNearestParkingLot(Vector3 position)`

Reuses the pattern from M2's `FindDestinationParkingLot()` but parameterized by position:

```csharp
private ParkingLot FindNearestParkingLotTo(Vector3 position, float maxDistance = 50f)
{
    return FindObjectsOfType<ParkingLot>()
        .Where(l => l.EntryPoint != null && l.GetRandomFreeSpotIndex() != -1)
        .Where(l => Vector3.Distance(l.EntryPoint.position, position) < maxDistance)
        .OrderBy(l => Vector3.Distance(l.EntryPoint.position, position))
        .FirstOrDefault();
}
```

---

## 2. Item Population

**When:** Immediately at F11 press, after selecting source storage, before starting the state machine.

**What:** Insert test items into the source `WorldStorageEntity` using the public `InsertItem()` API.

**Item choice:** `"cash"` — the simplest, most universally available item. Confirmed in multiple code paths: `Registry.GetItem("cash").GetDefaultInstance(1)` is used in `PlayerInventory.cs` and `ItemUIManager.cs`.

```csharp
// Populate source with test items
const string TEST_ITEM_ID = "cash";
const int TEST_ITEM_COUNT = 5;

ItemDefinition def = Registry.GetItem(TEST_ITEM_ID);
if (def == null)
{
    MelonLogger.Error("Registry.GetItem('" + TEST_ITEM_ID + "') returned null");
    return;
}

for (int i = 0; i < TEST_ITEM_COUNT; i++)
{
    ItemInstance instance = def.GetDefaultInstance(1);
    _sourceStorage.InsertItem(instance, true);
}

MelonLogger.Msg("Populated source with " + TEST_ITEM_COUNT + " " + TEST_ITEM_ID);
```

**Why individual inserts:** Cash may not stack (it's currency, not a typical stackable item). Inserting 5 individual instances of quantity 1 is safer than 1 instance of quantity 5. If it does stack, InsertItem handles that automatically.

**Namespace for Registry:** Need to verify exact namespace during implementation. Likely `ScheduleOne.ItemFramework` or a sub-namespace. Will grep for `class Registry` if not obvious.

---

## 3. State Machine Changes

### New States

```csharp
public enum DriverState
{
    Idle,
    WalkingToVehicle,
    EnteringVehicle,
    Driving,          // reused for both legs
    Parking,          // reused for both legs
    LoadingCargo,     // NEW: at source, transfer items from storage → vehicle
    UnloadingCargo,   // NEW: at destination, transfer items from vehicle → storage
    ExitingVehicle,
    Done
}
```

### New Fields

```csharp
// Cargo test fields (null when running simple F10 drive test)
private enum DeliveryLeg { Pickup, Delivery }
private DeliveryLeg _currentLeg;
private StorageEntity _sourceStorage;
private StorageEntity _destStorage;
private ParkingLot _sourceParkingLot;
private ParkingLot _destParkingLot;
```

### "Two Legs" Tracking

The existing `_destination` field (type `ParkingLot`) is reused. It points to whichever ParkingLot the vehicle should drive to next:

- **At test start:** `_destination = _sourceParkingLot`, `_currentLeg = Pickup`
- **After LoadingCargo:** `_destination = _destParkingLot`, `_currentLeg = Delivery`

The existing `EnterDriving()` and `EnterParking()` code already use `_destination` — no changes needed there.

### State Flow

```
F11 (cargo test):
Idle → WalkingToVehicle → EnteringVehicle → Driving(→source) → Parking(source)
  → LoadingCargo → Driving(→dest) → Parking(dest) → UnloadingCargo
  → ExitingVehicle → Done

F10 (simple drive test, unchanged):
Idle → WalkingToVehicle → EnteringVehicle → Driving → Parking
  → ExitingVehicle → Done
```

### Modified Transition: `EnterParking()`

After parking, decide what to do next based on whether this is a cargo test:

```csharp
// After parking the vehicle (existing code)...

if (_sourceStorage == null)
{
    // Simple drive test (F10) — go straight to exit
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
```

`_sourceStorage == null` is the discriminator between simple drive mode (F10) and cargo mode (F11). No new enum or mode flag needed.

### Design decision: NPC stays in vehicle during load/unload

**NPC does NOT exit the vehicle at the source.** Cargo transfer happens programmatically while the NPC remains in the driver seat. Rationale:

- Avoids needing to re-run WalkingToVehicle + EnteringVehicle for the second leg
- The transfer is "magical" for this milestone anyway (no animation)
- Simpler state machine — `LoadingCargo` transitions directly to `Driving` for the next leg

---

## 4. Transfer Logic

### Shared Transfer Method

```csharp
/// <summary>
/// Transfers all items from source to destination, logging counts.
/// Returns number of items transferred.
/// </summary>
private int TransferItems(StorageEntity source, StorageEntity destination, string label)
{
    int totalTransferred = 0;
    int slotsBefore = 0;

    // Count source items before
    foreach (var slot in source.ItemSlots)
    {
        if (slot.ItemInstance != null)
            slotsBefore++;
    }
    MelonLogger.Msg("" + label + ": source has " + slotsBefore + " occupied slots");

    // Transfer each occupied slot
    foreach (var slot in source.ItemSlots)
    {
        if (slot.ItemInstance == null) continue;

        ItemInstance copy = slot.ItemInstance.GetCopy();
        int qty = slot.Quantity;

        slot.ClearStoredInstance();
        destination.InsertItem(copy, true);
        totalTransferred += qty;
    }

    MelonLogger.Msg("" + label + ": transferred " + totalTransferred + " items");
    return totalTransferred;
}
```

### LoadingCargo State

```csharp
private void EnterLoadingCargo()
{
    MelonLogger.Msg("Loading cargo from source storage...");
    int count = TransferItems(_sourceStorage, _vehicle.Storage, "LOAD");

    if (count == 0)
    {
        MelonLogger.Warning("Source was empty — nothing to deliver");
        // Still continue the full flow to test driving
    }

    // Switch to delivery leg
    _destination = _destParkingLot;
    _currentLeg = DeliveryLeg.Delivery;
    SetState(DriverState.Driving);
}
```

### UnloadingCargo State

```csharp
private void EnterUnloadingCargo()
{
    MelonLogger.Msg("Unloading cargo to destination storage...");
    int count = TransferItems(_vehicle.Storage, _destStorage, "UNLOAD");

    if (count == 0)
    {
        MelonLogger.Warning("Vehicle was empty — nothing to unload");
    }

    SetState(DriverState.ExitingVehicle);
}
```

### API Pattern (from RECON.md §4)

The transfer follows the documented pattern:

```csharp
ItemInstance item = sourceStorage.ItemSlots[i].ItemInstance.GetCopy();
int qty = sourceStorage.ItemSlots[i].Quantity;
sourceStorage.ItemSlots[i].ClearStoredInstance();
destStorage.InsertItem(item, true);  // network=true for multiplayer sync
```

---

## 5. Edge Cases (Log, Don't Handle)

| Edge Case                                  | Behavior                                                                                                                |
| ------------------------------------------ | ----------------------------------------------------------------------------------------------------------------------- |
| Source storage is empty                    | Log warning "Source was empty — nothing to deliver". Continue flow (still test the driving).                            |
| Vehicle storage is full when loading       | `InsertItem()` may silently fail if no slots available. Log vehicle slot count before/after load. No overflow handling. |
| Destination storage is full when unloading | Same as above — `InsertItem()` may fail. Log before/after counts.                                                       |
| No WorldStorageEntities in world           | Log error with count, abort before starting state machine.                                                              |
| No ParkingLots near storage entities       | Log error with diagnostics (how many storages found, none had nearby lots), abort.                                      |
| Vehicle destroyed mid-flow                 | Existing null guard in `UpdateWalkingToVehicle` catches this. Similar guards in other states.                           |
| NPC destroyed mid-flow                     | Same existing null guard.                                                                                               |
| Navigation fails on either leg             | Existing callback handler → `SetState(Done)`.                                                                           |

---

## 6. New Entry Point: `TriggerCargoTest()`

```csharp
public void TriggerCargoTest()
{
    // Same guards as TriggerDriveTest: IsRunning, IsServer, Player.Local null

    // 1. Find vehicle (reuse FindNearestPlayerVehicle)
    // 2. Find NPC (reuse GetLastSpawnedNPC)
    // 3. Find source + destination WorldStorageEntities with nearby ParkingLots
    // 4. Populate source with test items
    // 5. Set cargo fields: _sourceStorage, _destStorage, _sourceParkingLot, _destParkingLot
    // 6. _destination = _sourceParkingLot (first leg target)
    // 7. _currentLeg = DeliveryLeg.Pickup
    // 8. SetState(DriverState.WalkingToVehicle)
}
```

### F11 Handler in `DeliveryDriversMod.cs`

```csharp
if (Input.GetKeyDown(KeyCode.F11))
{
    var driver = DeliveryDriverBehaviour.Instance;
    if (driver != null && !driver.IsRunning)
    {
        driver.TriggerCargoTest();
    }
}
```

---

## 7. Cleanup in `EnterDone()`

Extend existing cleanup to clear cargo fields:

```csharp
private void EnterDone()
{
    // ... existing logging ...
    _npc = null;
    _vehicle = null;
    _destination = null;
    _sourceStorage = null;     // NEW
    _destStorage = null;       // NEW
    _sourceParkingLot = null;  // NEW
    _destParkingLot = null;    // NEW
    _state = DriverState.Idle;
}
```

---

## 8. New Namespace Imports

```csharp
using ScheduleOne.Storage;         // WorldStorageEntity, StorageEntity
using ScheduleOne.ItemFramework;   // Registry, ItemDefinition, ItemInstance, ItemSlot
```

Verify `Registry` namespace during implementation — may be in a different namespace.

---

## 9. Files Modified

| File                                    | Changes                                                                                                                                                                                                                        |
| --------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `Mod/Source/DeliveryDriverBehaviour.cs` | Add LoadingCargo + UnloadingCargo states, DeliveryLeg enum, cargo fields, TriggerCargoTest(), TransferItems(), FindNearestParkingLotTo(), item population logic. Modify EnterParking() transition. Extend EnterDone() cleanup. |
| `Mod/Source/DeliveryDriversMod.cs`      | Add F11 hotkey handler (3 lines).                                                                                                                                                                                              |

No new files. No changes to `NPCSpawner.cs` or `SpawnedNPCData.cs`.

---

## 10. Logging Plan

```
F11: Starting cargo transfer test
  Found N WorldStorageEntities in world
  Source: <name> at <pos>, ParkingLot <dist>m away
  Destination: <name> at <pos>, ParkingLot <dist>m away
  Vehicle: <name> at <pos>
  NPC: <name>
  Populated source with 5 cash
State: Idle → WalkingToVehicle
State: WalkingToVehicle → EnteringVehicle
State: EnteringVehicle → Driving
  Starting navigation to source ParkingLot at <pos>
State: Driving → Parking (source)
State: Parking → LoadingCargo
LOAD: source has N occupied slots
LOAD: transferred N items
State: LoadingCargo → Driving
  Starting navigation to destination ParkingLot at <pos>
State: Driving → Parking (destination)
State: Parking → UnloadingCargo
UNLOAD: source has N occupied slots
UNLOAD: transferred N items
State: UnloadingCargo → ExitingVehicle
State: ExitingVehicle → Done
Cargo test complete: delivered N items
```

---

## 11. Risks

| Risk                                            | Likelihood | Mitigation                                                                                                          |
| ----------------------------------------------- | ---------- | ------------------------------------------------------------------------------------------------------------------- |
| `WorldStorageEntity.All` is empty in a new save | Medium     | Log diagnostic, abort cleanly. Player needs at least one property with storage.                                     |
| No ParkingLot within 50m of any storage entity  | Low        | The game places ParkingLots near properties which also have storage. If this fails, increase search radius to 100m. |
| `Registry.GetItem("cash")` returns null         | Low        | "cash" is used in core game code. Guard with null check + clear error.                                              |
| Vehicle storage is null                         | Low        | Guard: `if (_vehicle.Storage == null)` → abort. Vehicle prefabs include StorageEntity.                              |
| `InsertItem()` silently fails (full storage)    | Medium     | Log slot counts before/after. Don't handle overflow — just log discrepancy.                                         |
| Registry namespace is different than expected   | Medium     | Grep for `class Registry` during implementation.                                                                    |
| Second navigation (source → dest) fails         | Medium     | Same callback handler as M2 — logs and aborts cleanly.                                                              |

---

## 12. Success Criteria

- [ ] F11 with nearby vehicle + spawned NPC starts the cargo transfer test
- [ ] Source and destination WorldStorageEntities are discovered automatically and logged
- [ ] Source storage is populated with 5 cash items before the state machine starts
- [ ] NPC walks (or warps) to vehicle and enters driver seat
- [ ] Vehicle drives to source ParkingLot and parks
- [ ] Items transfer from source StorageEntity into vehicle's Storage (LOAD log shows count > 0)
- [ ] NPC stays in vehicle (no exit/re-enter between legs)
- [ ] Vehicle drives to destination ParkingLot and parks
- [ ] Items transfer from vehicle's Storage to destination StorageEntity (UNLOAD log shows count > 0)
- [ ] NPC exits vehicle
- [ ] Logs appear at each phase transition with item counts before/after each transfer
- [ ] No unhandled exceptions during the full flow
- [ ] F10 simple drive test still works unchanged
- [ ] F11 can be pressed again after completion for another test run

---

## 13. What This Does NOT Cover

Per scope rules:

- No LoadingDock integration (M4)
- No save/load of cargo or route state (M6)
- No schedule-based triggering (M7)
- No wages (M8)
- No UI for selecting storage/items (M9)
- No multiple cargo types per trip (single item type: cash)
- No capacity overflow handling
- No round-trip / return to origin
- No error recovery beyond logging
- No NPC animation for loading/unloading
