# Vehicle Driving Milestone Plan

## Goal

Prove an NPC can drive a player-owned vehicle from point A to point B using the game's `VehicleAgent.Navigate()` system.

**One-way trip:** NPC walks to vehicle → enters → drives to ParkingLot → parks → exits.

**Trigger:** Player presses F10 in-game.

---

## Prerequisites (checked at F10 press)

- At least one NPC spawned via F9 (hello-world milestone)
- At least one player-owned vehicle exists in the world
- Game is loaded (Main scene)

---

## 1. Selection Logic (on F10 press)

### Vehicle: closest player-owned within 50m

```csharp
var playerPos = Player.Local.transform.position;
var vehicles = NetworkSingleton<VehicleManager>.Instance.PlayerOwnedVehicles;
LandVehicle closest = vehicles
    .Where(v => v != null && Vector3.Distance(v.transform.position, playerPos) < 50f)
    .OrderBy(v => Vector3.Distance(v.transform.position, playerPos))
    .FirstOrDefault();
```

If none found → log warning, abort.

### NPC: most recently spawned

Add a public accessor to `NPCSpawner`:

```csharp
public GameObject GetLastSpawnedNPC()
{
    var last = spawnedNPCs.LastOrDefault(r => r.npcObject != null);
    return last?.npcObject;
}
```

Get the NPC component: `npcObj.GetComponent<NPC>()`.

If null → log warning, abort.

### Destination: any ParkingLot with free spots, far enough away

```csharp
var lots = Object.FindObjectsOfType<ParkingLot>();
ParkingLot dest = lots
    .Where(l => l.EntryPoint != null && l.GetRandomFreeSpotIndex() != -1)
    .Where(l => Vector3.Distance(l.EntryPoint.position, vehicle.transform.position) > 30f)
    .OrderBy(l => Vector3.Distance(l.EntryPoint.position, vehicle.transform.position))
    .FirstOrDefault();
```

Strategy: pick the **closest lot that is >30m away** (ensures a real drive but not cross-map). If no suitable lot found, fall back to the closest lot with free spots regardless of distance.

If still none → log error, abort. (This would mean the world has no ParkingLots, which shouldn't happen.)

---

## 2. State Machine

```
Idle → WalkingToVehicle → EnteringVehicle → Driving → Parking → ExitingVehicle → Done
                                                ↘ Failed (on nav failure)
```

Each transition is logged via `MelonLogger.Msg`.

### State: WalkingToVehicle

**Entry action:**

```csharp
Vector3 walkTarget = vehicle.driverEntryPoint.position;  // exitPoints[0]
npc.Movement.SetDestination(walkTarget);
```

**Per-frame check (Update):**

```csharp
float dist = Vector3.Distance(npc.transform.position, walkTarget);
if (dist < 2.5f) → transition to EnteringVehicle
```

**Timeout fallback:** If >15 seconds elapse without arriving, warp the NPC:

```csharp
npc.Movement.Warp(walkTarget);
```

Then transition to EnteringVehicle. This handles cases where SetDestination doesn't work on our uninitialized NPC or the NavMesh path is blocked.

**Risk:** The NPC was spawned from BotanistPrefab without `Initialize()`. The `NPCMovement` component and `NavMeshAgent` are present from the prefab clone, but may not be fully configured. If `SetDestination` throws or does nothing, the timeout catches it.

### State: EnteringVehicle

**Entry action:**

```csharp
npc.EnterVehicle(null, vehicle);
```

Calling convention: `null` for `NetworkConnection` → broadcasts to all observers (correct for server/host-side code).

**What this does internally** (NPC.cs L1786):

- Sets `CurrentVehicle = vehicle`
- Hides NPC model (`SetVisible(false)`)
- Disables NavMeshAgent
- Parents NPC to vehicle transform
- Positions NPC at driver seat
- Calls `vehicle.AddNPCOccupant(npc)` → which calls `StartVehicle()` (first occupant starts the engine)

**Transition:** Wait 0.5s (one-frame delay for network sync), then → Driving.

### State: Driving

**Entry action:**

First, if the vehicle is parked, unpark it:

```csharp
if (vehicle.isParked)
{
    vehicle.ExitPark_Networked(null, vehicle.CurrentParkingLot.UseExitPoint);
    // Wait ~0.5s for unpark to complete
}
```

Then navigate:

```csharp
vehicle.Agent.Navigate(
    destination.EntryPoint.position,
    null,  // default NavigationSettings
    OnNavigationComplete
);
```

**Callback handler:**

```csharp
private void OnNavigationComplete(VehicleAgent.ENavigationResult result)
{
    switch (result)
    {
        case ENavigationResult.Complete:
            MelonLogger.Msg("Navigation complete, parking...");
            SetState(DriverState.Parking);
            break;
        case ENavigationResult.Failed:
            MelonLogger.Error("Navigation FAILED");
            SetState(DriverState.Done);
            break;
        case ENavigationResult.Stopped:
            MelonLogger.Warning("Navigation STOPPED");
            SetState(DriverState.Done);
            break;
    }
}
```

**No retry on failure** — per scope, just log and stop.

**Key detail:** `Navigate()` is **host-only** (`!InstanceFinder.IsHost` returns immediately). We guard with `InstanceFinder.IsServer` at the top of the F10 handler.

**Key detail:** `Navigate()` calculates a path on the **vehicle road graph** (not NavMesh). The vehicle must be within 6 units of the road graph. Default `NavigationSettings` has `ensureProximityToGraph = true` and `teleportToGraphIfCalculationFails = true`, so this is handled automatically.

**Key detail:** Arrival detection triggers at **3 units** from the final path point. The `Park()` call handles final alignment precision.

### State: Parking

**Entry action:**

```csharp
int spotIndex = destination.GetRandomFreeSpotIndex();
if (spotIndex == -1)
{
    MelonLogger.Warning("No free parking spots, skipping park");
    SetState(DriverState.ExitingVehicle);
    return;
}

EParkingAlignment alignment = destination.ParkingSpots[spotIndex].Alignment;
vehicle.Park(null, new ParkData(destination.GUID, spotIndex, alignment), true);
```

- `network: true` → broadcasts park to all clients (matches how `NPCSignal_DriveToCarPark` does it)
- `Park()` internally calls `AlignTo()` which teleport-snaps the vehicle to the exact spot position
- `Park()` also disables physics (`UpdatePhysicallySimulated(false)`)

**Gotcha:** If `spotIndex == -1` and we called `Park()` anyway, the vehicle would become invisible (`SetVisible(false)`). We explicitly guard against this.

**Transition:** Immediate → ExitingVehicle (park is synchronous).

### State: ExitingVehicle

**Entry action:**

```csharp
npc.ExitVehicle();
```

**What this does internally** (NPC.cs L1886):

- Removes NPC from vehicle occupants
- Resets VehicleAgent flags
- Reparents NPC to `NPCManager.Instance.NPCContainer`
- Positions NPC at vehicle exit point
- Re-enables NavMeshAgent
- Makes NPC visible again

**Transition:** Immediate → Done.

### State: Done

Log summary:

```
Drive test complete: NPC exited vehicle at <position>
```

Reset state so F10 can be pressed again for another test run.

---

## 3. Code Structure

### New file: `Mod/Source/DeliveryDriverBehaviour.cs`

```csharp
namespace DeliveryDriversMod
{
    public class DeliveryDriverBehaviour : MonoBehaviour
    {
        // State enum
        public enum DriverState
        {
            Idle,
            WalkingToVehicle,
            EnteringVehicle,
            Driving,
            Parking,
            ExitingVehicle,
            Done
        }

        // References (set on StartDriveTest)
        private NPC npc;
        private LandVehicle vehicle;
        private ParkingLot destination;

        // State
        private DriverState state = DriverState.Idle;
        private float stateTimer;

        // Public API
        public bool IsRunning => state != DriverState.Idle && state != DriverState.Done;
        public void StartDriveTest(NPC npc, LandVehicle vehicle, ParkingLot destination);

        // State machine
        private void SetState(DriverState newState);
        private void Update();  // polls current state

        // Callbacks
        private void OnNavigationComplete(VehicleAgent.ENavigationResult result);
    }
}
```

**Lifecycle:** Attached to the existing `DeliveryDriverMod_Manager` GameObject (same one that holds `NPCSpawner`). Created once, reused for each test run.

### Modified: `Mod/Source/DeliveryDriversMod.cs`

Changes:

1. Add `DeliveryDriverBehaviour` component to the manager GameObject
2. Add F10 hotkey handler in `OnUpdate()`
3. F10 handler: validate prerequisites, find vehicle/NPC/destination, call `StartDriveTest()`

```csharp
// In OnSceneWasLoaded, when creating manager:
go.AddComponent<DeliveryDriverBehaviour>();

// In OnUpdate:
if (Input.GetKeyDown(KeyCode.F10))
{
    var driver = DeliveryDriverBehaviour.Instance;
    if (driver != null && !driver.IsRunning)
    {
        driver.TriggerDriveTest();  // selection + start
    }
}
```

### Modified: `Mod/Source/NPCSpawner.cs`

One addition: public accessor for the last spawned NPC.

```csharp
public GameObject GetLastSpawnedNPC()
{
    var last = spawnedNPCs.LastOrDefault(r => r.npcObject != null);
    return last?.npcObject;
}
```

### No changes to: `SpawnedNPCData.cs`

---

## 4. New References Needed

The new file needs these additional assembly references (beyond what hello-world already uses):

| Assembly              | Types Used                                                                                                                                                                     |
| --------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `Assembly-CSharp.dll` | `LandVehicle`, `VehicleAgent`, `VehicleManager`, `ParkingLot`, `ParkingSpot`, `ParkData`, `EParkingAlignment`, `ENavigationResult`, `NavigationSettings`, `NPC`, `NPCMovement` |

These should all already be available from the existing `Assembly-CSharp.dll` reference. No new DLL references needed.

---

## 5. Namespace Imports (DeliveryDriverBehaviour.cs)

```csharp
using MelonLoader;
using UnityEngine;
using System.Linq;
using FishNet;
using ScheduleOne.DevUtilities;          // Singleton<>, NetworkSingleton<>
using ScheduleOne.NPCs;                  // NPC, NPCMovement
using ScheduleOne.PlayerScripts;         // Player
using ScheduleOne.Vehicles;              // LandVehicle, VehicleManager, VehicleAgent
using ScheduleOne.Vehicles.AI;           // ENavigationResult, NavigationSettings, DriveFlags
using ScheduleOne.Map;                   // ParkingLot, ParkingSpot, ParkData, EParkingAlignment
```

Note: `ParkData` and `EParkingAlignment` might be in `ScheduleOne.Vehicles` rather than `ScheduleOne.Map`. Verify during implementation.

---

## 6. Risks and Mitigations

| Risk                                                           | Likelihood | Mitigation                                                                                                                                            |
| -------------------------------------------------------------- | ---------- | ----------------------------------------------------------------------------------------------------------------------------------------------------- |
| `NPCMovement.SetDestination` doesn't work on uninitialized NPC | Medium     | 15-second timeout → warp fallback                                                                                                                     |
| Vehicle is >6m from road graph                                 | Low        | Default `NavigationSettings` auto-teleports to graph                                                                                                  |
| `ParkingLot.EntryPoint` is unreachable on vehicle graph        | Low        | Expected to work (vanilla NPC vehicles use ParkingLots). If Navigate fails, callback handles it gracefully                                            |
| No ParkingLots with free spots                                 | Very Low   | Game world has many; we pick the best available                                                                                                       |
| `EnterVehicle` fails on uninitialized NPC                      | Low        | NPC.EnterVehicle is a base class method that just does transform parenting + visibility — shouldn't depend on Initialize. If it throws, catch and log |
| Navigation callback fires on a destroyed object                | Low        | Guard callback with null-checks on vehicle/npc references                                                                                             |

---

## 7. Logging Plan

Every state transition logs with the `[DeliveryDriversMod]` prefix:

```
F10: Starting drive test
  Vehicle: <vehicleName> at <pos> (distance: <d>m)
  NPC: <guid>
  Destination: ParkingLot at <pos> (distance: <d>m)
State: Idle → WalkingToVehicle
State: WalkingToVehicle → EnteringVehicle (walked <d>m in <t>s)
State: EnteringVehicle → Driving
State: Driving → Parking (navigation complete)
State: Parking → ExitingVehicle (parked at spot <idx>)
State: ExitingVehicle → Done
Drive test complete: NPC exited at <pos>
```

Or on failure:

```
Navigation FAILED — aborting drive test
State: Driving → Done (failed)
```

---

## 8. What This Does NOT Cover

Per milestone scope:

- No cargo / item transfer
- No LoadingDock interaction
- No multiple destinations or routes
- No driver-vehicle assignment persistence
- No UI for selecting destinations
- No return trip (one-way only)
- No multiple simultaneous drivers
- No error recovery beyond logging

---

## 9. Success Criteria

- [ ] F10 with nearby vehicle + spawned NPC starts the test
- [ ] NPC walks (or warps) to vehicle and enters driver seat
- [ ] Vehicle drives autonomously to a ParkingLot
- [ ] Vehicle parks at a spot in the lot
- [ ] NPC exits the vehicle
- [ ] Logs appear at each phase transition
- [ ] No unhandled exceptions during the flow
- [ ] F10 can be pressed again after completion for another test
