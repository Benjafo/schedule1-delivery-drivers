# Schedule 1 - Delivery Driver Mod: Codebase Reconnaissance

> Generated from decompiled source at `./Schedule1-Decompiled/Assembly-CSharp/ScheduleOne/`
> All file paths are relative to `./Schedule1-Decompiled/Assembly-CSharp/`

---

## 1. Vehicle System

### Namespaces & Key Classes

| Class | File | Lines | Purpose |
|-------|------|-------|---------|
| `LandVehicle` | `ScheduleOne/Vehicles/LandVehicle.cs` | 2994 | Base vehicle entity. Physics, seats, storage, parking, save/load |
| `VehicleAgent` | `ScheduleOne/Vehicles/AI/VehicleAgent.cs` | 1550 | AI driving controller (pathfinding, steering PID, obstacle avoidance) |
| `VehicleManager` | `ScheduleOne/Vehicles/VehicleManager.cs` | 344 | Singleton registry. Spawns, tracks, saves all vehicles |
| `DriveFlags` | `ScheduleOne/Vehicles/AI/DriveFlags.cs` | 66 | Configuration for AI driving behavior |
| `NavigationUtility` | `ScheduleOne/Vehicles/AI/NavigationUtility.cs` | 10650 | Path calculation on vehicle road graph |
| `NavigationSettings` | `ScheduleOne/Vehicles/AI/NavigationSettings.cs` | 370 | Per-navigation-call settings |
| `PathUtility` | `ScheduleOne/Vehicles/AI/PathUtility.cs` | 6815 | Path smoothing, sampling |
| `Sensor` | `ScheduleOne/Vehicles/AI/Sensor.cs` | 3823 | Obstacle detection raycasts |
| `SteerPID` | `ScheduleOne/Vehicles/AI/SteerPID.cs` | 1021 | PID controller for steering |
| `VehicleTeleporter` | `ScheduleOne/Vehicles/AI/VehicleTeleporter.cs` | 1383 | Stuck-recovery teleportation |
| `DeliveryVehicle` | `ScheduleOne/Delivery/DeliveryVehicle.cs` | 69 | Wraps LandVehicle for cargo delivery |
| `Shitbox` | `ScheduleOne/Vehicles/Shitbox.cs` | 1467 | Specific vehicle subclass |

### Class Hierarchy

```
NetworkBehaviour (FishNet)
 └── LandVehicle : IGUIDRegisterable, ISaveable, IWeatherEntity
      ├── Shitbox (example subclass)
      └── (other vehicle-specific subclasses)
```

### How Vehicles Work

**LandVehicle** is a physics-driven vehicle using Unity's `WheelCollider` system. Key fields (from `LandVehicle.cs`):
- `Rigidbody Rb` (L2797) - Main physics body
- `WheelCollider[] driveWheels / steerWheels / handbrakeWheels` (L2774-2782)
- `AnimationCurve motorTorque` (L2864), `AnimationCurve brakeForce` (L2885)
- `float TopSpeed = 60f` (L2872), `float maxSteeringAngle = 25f` (L2851)
- `VehicleSeat[] Seats` (L2803) - Seat positions (index 0 = driver)
- `NPC[] OccupantNPCs` (L215) - NPCs currently in the vehicle
- `StorageEntity Storage` (L2925) - Cargo inventory
- `StorageDoorAnimation Trunk` (L2815) - Trunk animation
- `VehicleAgent Agent` (L2809) - AI driving component
- `NavMeshObstacle NavMeshObstacle` (L2818) - Vehicles are NavMesh obstacles, NOT NavMesh agents

**Spawning** (VehicleManager.cs L111-130):
```csharp
public LandVehicle SpawnAndReturnVehicle(string vehicleCode, Vector3 position,
    Quaternion rotation, bool playerOwned)
```
Instantiates prefab by code, sets position, calls `NetworkObject.Spawn()`, adds to `PlayerOwnedVehicles` list.

### Player vs. AI Driving

**Player driving:** Uses `EnterVehicle()` (L1225) which sets `LocalPlayerIsDriver`, takes network ownership, and reads input each frame in `Update()` / `FixedUpdate()`. Player controls go through `UpdateThrottle()` (L771) and `UpdateSteerAngle()` (L907).

**AI driving:** The `VehicleAgent` component attached to each vehicle handles autonomous navigation. When AI is active, it sets `vehicle.overrideControls = true` (L672) and writes to `vehicle.throttleOverride` / `vehicle.steerOverride` each frame. This is our primary integration point.

### VehicleAgent - The AI Driver (Critical)

**Navigate API** (VehicleAgent.cs L622):
```csharp
public void Navigate(Vector3 location, NavigationSettings settings = null,
    VehicleAgent.NavigationCallback callback = null)
```
This is the central method. It:
1. Calculates a path on the **vehicle road graph** (not NavMesh) via `NavigationUtility.CalculatePath()`
2. Sets `AutoDriving = true`
3. Enables obstacle sensors
4. Follows the path using PID steering/throttle controllers
5. Fires callback with `ENavigationResult.Complete`, `Failed`, or `Stopped`

**DriveFlags** configure AI behavior per-vehicle:
- `OverrideSpeed` / `OverriddenSpeed` - Custom speed limit
- `IgnoreTrafficLights` - Run reds
- `UseRoads` - Stay on road graph
- `StuckDetection` - Auto-detect stuck vehicles
- `ObstacleMode` - Default, IgnoreAll, IgnoreOnlySquishy
- `AutoBrakeAtDestination` - Stop at target

**Other key VehicleAgent methods:**
- `StopNavigating()` (L717) - Halt AI driving, fires callback with `Stopped`
- `RecalculateNavigation()` (L731) - Recompute path to same target
- `StartReverse()` (L948) - Manual backup routine
- `GetIsStuck()` (L1184) - Check stuck status
- `PursuitModeEnabled` (L1415) - Chase a target transform

**Pathfinding:** Vehicles do NOT use Unity NavMeshAgent. They use a **custom road graph** (`VehicleGraph`). The `NavigationUtility` calculates paths between road nodes, and `PathUtility` smooths them. If the vehicle is >6f from the road graph, `VehicleTeleporter.MoveToGraph()` teleports it back.

### Vehicle Storage

Vehicles have a `StorageEntity Storage` property (L2925). The existing delivery system uses this to load cargo:
```csharp
vehicle.Storage.InsertItem(itemInstance, true);  // Add item
vehicle.Storage.ItemSlots                         // Access all slots
vehicle.Storage.GetAllItems()                     // Get contents
```

### Extension Points

**Virtual methods on LandVehicle (can override in subclass):**
- `Awake()` (L419), `Start()` (L524), `Update()` (L602), `FixedUpdate()` (L632)
- `UpdateThrottle()` (L771), `ApplyThrottle()` (L785)
- `UpdateSteerAngle()` (L907), `ApplySteerAngle()` (L985)
- `SetOwner()` (L718), `OnOwnerChanged()` (L725)
- `Load()` (L1570) - save data loading
- `RecoverVehicle()` (L1374)

**Control override fields (no patching needed):**
- `overrideControls` (L2909), `throttleOverride` (L2913), `steerOverride` (L2917), `handbrakeOverride` (L2921)

### Driving Behavior Notes

- **Drive timing:** ~22s for 34.5m suggests average ~5.6 km/h on the road graph (well below the 60f TopSpeed) — the AI may be conservative on short trips. Worth noting in case future milestones expect faster transit times.
- **Last-mile alignment pattern:** "Drive to ParkingLot.EntryPoint → `Vehicle.Park()` snaps the rest" is the canonical approach and will apply identically to LoadingDocks (since `LoadingDock.Parking` is a `ParkingLot` reference).

---

## 2. NPC AI System

### Namespaces & Key Classes

| Class | File | Lines | Purpose |
|-------|------|-------|---------|
| `NPC` | `ScheduleOne/NPCs/NPC.cs` | 3655 | Base NPC entity |
| `NPCMovement` | `ScheduleOne/NPCs/NPCMovement.cs` | 1685 | NavMeshAgent-based foot pathfinding |
| `NPCBehaviour` | `ScheduleOne/NPCs/Behaviour/NPCBehaviour.cs` | ~800 | Behavior collection manager per NPC |
| `Behaviour` | `ScheduleOne/NPCs/Behaviour/Behaviour.cs` | 387 | Abstract base behavior class |
| `NPCScheduleManager` | `ScheduleOne/NPCs/NPCScheduleManager.cs` | ~600 | Daily schedule orchestration |
| `NPCAction` | `ScheduleOne/NPCs/Schedules/NPCAction.cs` | 406 | Abstract scheduled action |
| `NPCEvent` | `ScheduleOne/NPCs/Schedules/NPCEvent.cs` | 151 | Duration-based scheduled event |
| `NPCSignal` | `ScheduleOne/NPCs/Schedules/NPCSignal.cs` | ~50 | One-shot scheduled signal |
| `NPCManager` | `ScheduleOne/NPCs/NPCManager.cs` | 244 | Global NPC registry |
| `NPCInventory` | `ScheduleOne/NPCs/NPCInventory.cs` | ~1200 | NPC item slots (IItemSlotOwner) |
| `Employee` | `ScheduleOne/Employees/Employee.cs` | ~1100 | Employee subclass of NPC |
| `EmployeeManager` | `ScheduleOne/Employees/EmployeeManager.cs` | ~380 | Employee spawning & management |

### NPC Class Hierarchy

```
NetworkBehaviour (FishNet)
 └── NPC : IGUIDRegisterable, ISaveable, ICombatTargetable, IDamageable,
            ISightable, INetworkedEquippableUser, IEquippableUser, IWeatherEntity
      └── Employee (with subtypes: Botanist, Handler, Chemist, Cleaner)
```

### Key NPC Fields

From `NPC.cs`:
- `NPCMovement Movement` (L391) - Foot pathfinding
- `NPCBehaviour Behaviour` (L415) - Behavior manager
- `NPCInventory Inventory` (L419) - Item storage
- `NPCScheduleManager` - Schedule orchestration (accessed via Behaviour.ScheduleBehaviour)
- `LandVehicle CurrentVehicle` (L120) - Vehicle ref when inside one
- `bool IsInVehicle` (L124) - Quick check
- `Action<LandVehicle> onEnterVehicle` (L3536) - Event
- `Action<LandVehicle> onExitVehicle` (L3539) - Event

### Movement System

NPCs use **Unity NavMeshAgent** for foot pathfinding (confirmed in `NPCMovement.cs`). Key methods:
- `SetDestination(Vector3 position, callback)` - Navigate on foot
- `Warp(Vector3)` - Teleport instantly
- `Stop()` - Halt movement
- `FaceDirection(Vector3, float time)` - Rotate toward direction
- `SetAgentEnabled(bool)` - Enable/disable NavMeshAgent

Speed is controlled by `WalkSpeed`, `RunSpeed`, `MovementSpeedScale` (blend), and `MoveSpeedMultiplier`.

### NPC Vehicle Integration

NPCs enter/exit vehicles via networked RPCs (NPC.cs L749-768):
```csharp
[ObserversRpc(RunLocally = true)][TargetRpc]
public virtual void EnterVehicle(NetworkConnection connection, LandVehicle veh)

[ObserversRpc(RunLocally = true)]
public virtual void ExitVehicle()
```

On enter: NPC disables NavMeshAgent, parents to vehicle seat, becomes invisible. On exit: re-enables NavMeshAgent, teleports to exit point, becomes visible.

### Behavior System (State Machine)

**Architecture:** One `NPCBehaviour` manager per NPC holds references to all behavior components. Only ONE behavior can be `Active` at a time. Behaviors have priorities; higher-priority behaviors interrupt lower ones.

**Behaviour base class** (Behaviour.cs) states:
- `Enabled` - Can be activated
- `Active` - Currently running
- `Started` - Has been initialized

**Lifecycle:** `Enable()` → `Activate()` → `BehaviourUpdate()` (each frame) → `Deactivate()` / `Pause()` / `Resume()`

**Virtual hooks we can override:**
- `BehaviourUpdate()` (L229) - Per-frame when active
- `OnActiveTick()` (L237) - System tick when active
- `OnActiveUncappedMinutePass()` (L242) - Time-based when active
- `Activate()` / `Deactivate()` / `Pause()` / `Resume()` (L135-186)

### Existing Vehicle Behaviors (Templates for Our Driver)

**VehiclePatrolBehaviour** (`NPCs/Behaviour/VehiclePatrolBehaviour.cs`):
- Assigns `LandVehicle Vehicle` and `VehiclePatrolRoute Route`
- Uses `Vehicle.Agent.Navigate(location, null, callback)` to drive between waypoints
- Checks `isDriving` via `Vehicle.OccupantNPCs[0] == Npc`
- On `Pause()`: calls `Npc.ExitVehicle()` + `Agent.StopNavigating()`

**NPCSignal_DriveToCarPark** (`NPCs/Schedules/NPCSignal_DriveToCarPark.cs`, 340 lines):
- Drives an NPC to a specific `ParkingLot` and parks
- Flow: walk to vehicle → enter → `Vehicle.Agent.Navigate()` → `Vehicle.Park()` → exit vehicle
- This is the closest existing pattern to what our delivery driver needs

### Schedule System

**NPCScheduleManager** manages a list of `NPCAction` objects sorted by `StartTime` (int, minutes since midnight 0-1439). Each tick it checks which action should be active based on current game time and priority.

**NPCAction lifecycle:** `Started()` → `ActiveUpdate()` (each frame) → `End()` or `Interrupt()`

**NPCEvent** extends NPCAction with a `Duration` (minutes). Auto-ends when time expires.

**NPCSignal** is a one-shot action (no duration).

### Employee System (Our Best Template)

**Employee** extends NPC with:
- `Property AssignedProperty` (L37) - Work location
- `EEmployeeType Type` - Botanist, Handler, Chemist, Cleaner
- `float DailyWage = 100f` (L1125) - Daily pay
- `bool PaidForToday` (L47) - Wage tracking
- `int TicksSinceLastWork` (L109)
- `float CurrentWorkSpeed` (L98)
- `bool Fired` (L64)

**Employee spawning** (`EmployeeManager.cs` L50-83):
```csharp
Employee CreateEmployee_Server(Property property, EEmployeeType type,
    string firstName, string lastName, string id, bool male,
    int appearanceIndex, Vector3 position, Quaternion rotation, string guid)
```
Instantiates prefab from `GetEmployeePrefab(type)`, calls `Initialize()`, spawns on network, warps to position.

**Wage system:** Employee checks `EmployeeHome.GetCashSum() >= DailyWage` and calls `RemoveDailyWage()` which removes cash from the home's cash register. If no cash, employee eventually quits.

---

## 3. Loading Zones / Storage

### Namespaces & Key Classes

| Class | File | Lines | Purpose |
|-------|------|-------|---------|
| `LoadingDock` | `ScheduleOne/Delivery/LoadingDock.cs` | 245 | Dock where vehicles load/unload cargo |
| `DeliveryVehicle` | `ScheduleOne/Delivery/DeliveryVehicle.cs` | 69 | Wraps LandVehicle for delivery |
| `DeliveryManager` | `ScheduleOne/Delivery/DeliveryManager.cs` | 660 | Central delivery coordinator |
| `DeliveryInstance` | `ScheduleOne/Delivery/DeliveryInstance.cs` | 155 | Single delivery record |
| `StorageEntity` | `ScheduleOne/Storage/StorageEntity.cs` | 1199 | Base container (slots-based) |
| `WorldStorageEntity` | `ScheduleOne/Storage/WorldStorageEntity.cs` | 211 | Persistent world storage |
| `StorageGrid` | `ScheduleOne/Storage/StorageGrid.cs` | 224 | 2D spatial storage (warehouse floor) |
| `StorageManager` | `ScheduleOne/Storage/StorageManager.cs` | 146 | Global storage registry |
| `VehicleDetector` | `ScheduleOne/DevUtilities/VehicleDetector.cs` | ~50 | Proximity sensor for vehicles at docks |

### The Existing Delivery System (Critical Finding)

The game **already has a delivery system** in `ScheduleOne/Delivery/`. This is extremely relevant - we may be able to extend it rather than build from scratch.

**Current delivery flow:**

```
1. DeliveryManager.SendDelivery(DeliveryInstance)
   └─ Sets status to InTransit, starts countdown timer

2. DeliveryManager.OnTimePass() decrements TimeUntilArrival
   └─ When == 0, calls DeliveryInstance.SetStatus(Arrived)

3. DeliveryInstance.SetStatus(Arrived):
   └─ Gets DeliveryVehicle from shop's available vehicles
   └─ Calls DeliveryVehicle.Activate(this)
       ├─ Vehicle.Park() at the LoadingDock
       ├─ Sets Storage.AccessSettings = Full
       └─ Opens trunk animation

4. DeliveryInstance.AddItemsToDeliveryVehicle():
   └─ For each item: Vehicle.Storage.InsertItem(instance, true)

5. Player manually unloads items from vehicle

6. DeliveryManager.OnTimePass() monitors:
   └─ When Vehicle.Storage.ItemCount == 0 for 3+ minutes
       └─ SetStatus(Completed) → vehicle.Deactivate() → hides vehicle
```

**Key limitation:** Current deliveries are "magic" - vehicles appear at the dock already loaded. No NPC actually drives them. This is exactly the gap our mod fills.

### LoadingDock Details (LoadingDock.cs)

- `LandVehicle DynamicOccupant` (L22) - Vehicle currently at dock (player-driven)
- `LandVehicle StaticOccupant` (L27) - Permanently parked delivery vehicle
- `bool IsInUse` (L31) - Either occupant exists
- `Property ParentProperty` (L224) - Owning property
- `VehicleDetector VehicleDetector` (L227) - Proximity trigger
- `ParkingLot Parking` (L230) - Associated parking spots
- `List<ItemSlot> InputSlots` (L64) - Items arriving (from vehicle)
- `List<ItemSlot> OutputSlots` (L69) - Items leaving (vehicle's storage slots)
- `Transform[] AccessPoints` (L87) - NPC/player positions for unloading
- `bool IsAcceptingItems` (L98) - Toggle for receiving deliveries

**RefreshOccupant()** (L126-148) runs every 1 second, checks `VehicleDetector.closestVehicle` and if speed < 2 km/h, sets as DynamicOccupant.

### StorageEntity API

The universal container class. Key methods:
- `InsertItem(ItemInstance item, bool network = true)` (L173) - Add item respecting slot limits
- `CanItemFit(ItemInstance item, int quantity)` (L146) - Check capacity
- `HowManyCanFit(ItemInstance item)` (L152) - Available space for item type
- `GetAllItems()` (L215) - Get all stored items
- `ClearContents()` (L240) - Empty everything
- `GetContentsDictionary()` (L132) - Items as dictionary
- Events: `onContentsChanged`, `onOpened`, `onClosed`
- Access control: `EAccessSettings { Full, SinglePlayerOnly, Closed }`

---

## 4. Inventory / Item Transfer

### Namespaces & Key Classes

| Class | File | Purpose |
|-------|------|---------|
| `ItemSlot` | `ScheduleOne/ItemFramework/ItemSlot.cs` (505 lines) | Single inventory slot (one item stack) |
| `ItemInstance` | `ScheduleOne/ItemFramework/ItemInstance.cs` (90 lines) | Base item instance |
| `ItemDefinition` | `ScheduleOne/ItemFramework/ItemDefinition.cs` | Item template/definition (ScriptableObject) |
| `IItemSlotOwner` | `ScheduleOne/ItemFramework/IItemSlotOwner.cs` (120 lines) | Interface for any container |
| `NPCInventory` | `ScheduleOne/NPCs/NPCInventory.cs` (~1200 lines) | NPC inventory (implements IItemSlotOwner) |
| `StorageEntity` | `ScheduleOne/Storage/StorageEntity.cs` | Container (implements IItemSlotOwner) |
| `Registry` | (various) | Item lookup by ID |

### Item Transfer API

**IItemSlotOwner** is the universal interface. Both `StorageEntity`, `NPCInventory`, and `PlayerInventory` implement it.

Key IItemSlotOwner methods:
- `List<ItemSlot> ItemSlots { get; set; }` - All slots
- `SetStoredInstance(conn, slotIndex, ItemInstance)` - Network RPC to set item
- `SetItemSlotQuantity(slotIndex, quantity)` - Network RPC to change quantity
- `GetQuantitySum()` - Total items
- `GetQuantityOfItem(string id)` - Count of specific item
- `GetFirstSlotContaining(string id)` - Find item

**ItemSlot** key methods:
- `SetStoredItem(ItemInstance, bool internal)` (L146) - Replace slot contents
- `InsertItem()` / `AddItem()` (L184) - Add with stacking
- `ClearStoredInstance()` (L206) - Remove item
- `ChangeQuantity(int change)` (L270) - Modify stack size
- `static TryInsertItemIntoSet(ItemInstance, List<ItemSlot>)` (L461) - Batch insert across slots
- Events: `onItemDataChanged`, `onItemInstanceChanged`

**How to transfer items between containers:**
```csharp
// Get item from source
ItemInstance item = sourceStorage.ItemSlots[i].ItemInstance.GetCopy();
int qty = sourceStorage.ItemSlots[i].Quantity;

// Remove from source
sourceStorage.ItemSlots[i].ClearStoredInstance();

// Insert into destination
destStorage.InsertItem(item.GetCopy(qty), true);
```

**Item instantiation from definition:**
```csharp
ItemDefinition def = Registry.GetItem("item_id");
ItemInstance instance = def.GetDefaultInstance(quantity);
```

### Cargo Transfer Notes

> Confirmed working pattern from M3 testing.

The validated transfer sequence is: **Get → Copy → Clear source → Insert dest.** Specifically: read `ItemSlot.ItemInstance`, call `GetCopy(quantity)`, call `ClearStoredInstance()` on the source slot, then `StorageEntity.InsertItem(copy, true)` on the destination. Testing used `Registry.GetItem("ogkush")` with `GetDefaultInstance(5)` to seed source storage. No timing delays were needed between clear and insert — the operations are synchronous in singleplayer/host context. The NPC remained seated in the vehicle throughout both the pickup and delivery transfers.

---

## 5. Time / Day Cycle

### Namespaces & Key Classes

| Class | File | Lines | Purpose |
|-------|------|-------|---------|
| `TimeManager` | `ScheduleOne/GameTime/TimeManager.cs` | 1202 | Central time controller |
| `GameDateTime` | `ScheduleOne/GameTime/GameDateTime.cs` | 97 | Date/time struct |
| `TimedCallback` | `ScheduleOne/GameTime/TimedCallback.cs` | 101 | Scheduled callback utility |
| `EDay` | `ScheduleOne/GameTime/EDay.cs` | 24 | Day-of-week enum (Monday-Sunday) |

### Time Representation

`TimeManager` is a `NetworkSingleton`. Time is stored as an **integer in 24-hour format** (0-2359). Examples: 800 = 8:00 AM, 1430 = 2:30 PM.

Key properties:
- `CurrentTime` (int) - Current time (0-2359)
- `ElapsedDays` (int) - Total days since game start
- `CurrentDay` (EDay) - Day of week enum
- `DayIndex` (int) - `ElapsedDays % 7`
- `IsNight` (bool) - Before 6:00 AM or after 6:00 PM
- `IsEndOfDay` (bool) - Equals 4:00 AM (400)
- `NormalizedTimeOfDay` (float) - 0.0 to 1.0

**Real-time mapping:** `CycleDuration` (default 24 min real time) = 24 hours game time. So 1 game minute ≈ 1 real second.

### Events for Scheduling (Our Integration Points)

```csharp
TimeManager tm = NetworkSingleton<TimeManager>.Instance;

tm.onMinutePass    // Action - fires every game minute (staggered)
tm.onHourPass      // Action - fires when hour changes
tm.onDayPass       // Action - fires at midnight
tm.onWeekPass      // Action - fires when day == Monday
tm.onSleepStart    // Action - fires when sleep begins
tm.onSleepEnd      // Action - fires when sleep ends
tm.onTick          // Action - fires every 0.5 real seconds
tm.onTimeSkip      // Action<int> - fires on skip, passes minutes skipped
```

**How to schedule driver activity:**
```csharp
// Subscribe to minute pass
TimeManager.Instance.onMinutePass += CheckDriverSchedule;

void CheckDriverSchedule()
{
    int time = NetworkSingleton<TimeManager>.Instance.CurrentTime;
    if (time == 800) StartMorningRoute();
    if (time == 1700) ReturnToBase();
}
```

**TimedCallback** utility (for countdown timers):
```csharp
new TimedCallback(OnArrival, durationMinutes: 30, tickAtEndOfDay: true, tickOnTimeSkip: true);
```

### Sleep / Day Transition

When all players sleep:
1. `onSleepStart` fires
2. Time skips to 700 (7:00 AM)
3. `ElapsedDays++`
4. `onDayPass` fires, then `onWeekPass` if Monday
5. `onSleepEnd` fires
6. `SaveManager.Save()` called

**Important:** Between 400-600 (end-of-day period), `onMinutePass` does NOT fire. Use `onUncappedMinutePass` if you need ticks during this window.

---

## 6. Save System

### Namespaces & Key Classes

| Class | File | Purpose |
|-------|------|---------|
| `ISaveable` | `ScheduleOne/Persistence/ISaveable.cs` | Main save interface (352 lines of default implementations) |
| `IBaseSaveable` | `ScheduleOne/Persistence/IBaseSaveable.cs` | Adds LoadOrder to ISaveable |
| `IGenericSaveable` | `ScheduleOne/Persistence/IGenericSaveable.cs` | GUID-tracked object saves |
| `SaveManager` | `ScheduleOne/Persistence/SaveManager.cs` | Central save coordinator |
| `GenericSaveablesManager` | `ScheduleOne/Persistence/GenericSaveablesManager.cs` | Manages GUID-tracked saves |
| `SaveData` | `ScheduleOne/Persistence/Datas/SaveData.cs` | Base serializable data class |
| `Loader` | `ScheduleOne/Persistence/Loaders/` | Deserialization base class |

### Save Data Format

JSON files stored at `Application.persistentDataPath/Saves/[SteamID]/[SaveName]/[Folder]/[File].json`

`SaveData` base class:
```csharp
[Serializable]
public class SaveData
{
    public string DataType;      // Auto-populated with class name
    public int DataVersion;      // For migration
    public string GameVersion;   // Game version string
    public virtual string GetJson(bool prettyPrint = true);
}
```

### How to Register Custom Mod Data

**Option A: ISaveable** (for singleton-like data, e.g., all driver assignments):
```csharp
public class DriverModSaveData : SaveData
{
    public DriverAssignment[] Assignments;
    // ... custom fields
}

public class DriverModManager : MonoBehaviour, ISaveable
{
    public string SaveFolderName => "DeliveryDrivers";
    public string SaveFileName => "DriverData";

    public void InitializeSaveable()
    {
        Singleton<SaveManager>.Instance.RegisterSaveable(this);
    }

    public string GetSaveString()
    {
        return new DriverModSaveData { Assignments = ... }.GetJson(true);
    }
}
```

**Option B: IGenericSaveable** (for per-object GUID-tracked data like individual drivers):
```csharp
public class DeliveryDriver : MonoBehaviour, IGenericSaveable
{
    public Guid GUID { get; }
    public void Load(GenericSaveData data) { ... }
    public GenericSaveData GetSaveData() { ... }
}
```

**Existing save structure for reference:**
- `OwnedVehicles/` - Vehicle state (VehicleManager)
- `WorldStorageEntities/` - Storage contents (StorageManager)
- `Properties/` - Property state (PropertyManager)
- `Time/Time.json` - Game time
- `Money/Money.json` - Player balance

---

## 7. Economy

### Namespaces & Key Classes

| Class | File | Purpose |
|-------|------|---------|
| `MoneyManager` | `ScheduleOne/Money/MoneyManager.cs` | Player balance tracking, transactions |
| `Transaction` | `ScheduleOne/Money/Transaction.cs` | Transaction record struct |
| `ATM` | `ScheduleOne/Money/ATM.cs` | ATM interaction + online banking |
| `Dealer` | `ScheduleOne/Economy/Dealer.cs` | Drug dealer NPC |
| `Customer` | `ScheduleOne/Economy/Customer.cs` | Customer NPC |
| `Supplier` | `ScheduleOne/Economy/Supplier.cs` | Supply chain NPC |

### Player Balance API

`MoneyManager` is a `NetworkSingleton<MoneyManager>` implementing `ISaveable`.

**Cash on hand:**
```csharp
// Read balance
float cash = MoneyManager.Instance.cashBalance;

// Modify cash (visual feedback optional)
MoneyManager.Instance.ChangeCashBalance(100f);   // Add $100
MoneyManager.Instance.ChangeCashBalance(-50f);   // Remove $50
```

**Online/bank balance:**
```csharp
// Read
float bank = MoneyManager.Instance.onlineBalance;

// Create transaction (shows in history)
MoneyManager.Instance.CreateOnlineTransaction(
    "Driver Wages", -100f, 1f, "Daily wage for delivery driver"
);
```

**Saved data** (`MoneyData`):
- `float OnlineBalance`
- `float Networth`
- `float LifetimeEarnings`
- `float WeeklyDepositSum`

### Employee Payment Model

Employees are paid via `DailyWage` (default $100/day). Payment is checked from an `EmployeeHome` cash register - the player must leave cash there. If insufficient funds, the employee eventually leaves.

For our mod, we could either:
1. Use the same EmployeeHome cash system
2. Deduct directly from `MoneyManager.ChangeCashBalance(-wage)` or `CreateOnlineTransaction()`

---

## Confidence Assessment

| System | Confidence | Notes |
|--------|-----------|-------|
| Vehicle AI / VehicleAgent | **High** | Well-understood. `Navigate()` + callback pattern is clear. DriveFlags is well-documented. |
| NPC Behavior System | **High** | Behaviour base class is clean. VehiclePatrolBehaviour is a direct template. |
| Loading Docks / Delivery | **High** | Existing delivery system is well-structured. LoadingDock has clear occupant management. |
| Storage / Item Transfer | **High** | IItemSlotOwner interface is universal. InsertItem/ClearStoredInstance are straightforward. |
| Time / Scheduling | **High** | Events are simple Action delegates. Time format is trivial integer comparison. |
| Save System | **Medium** | ISaveable interface is clear but there are 150+ data classes. Need to verify MelonLoader can hook into SaveManager's save cycle. |
| Economy | **High** | MoneyManager API is simple. ChangeCashBalance and CreateOnlineTransaction are all we need. |
| NPC Spawning (for drivers) | **Medium** | EmployeeManager.CreateEmployee_Server pattern is clear, but creating a new EEmployeeType may require Harmony patching of the prefab lookup. Alternatively we could spawn NPCs directly and manage them outside the Employee system. |
| FishNet Networking | **Medium-Low** | All game systems use FishNet RPCs heavily. Our mod will need to work correctly in multiplayer or at minimum not break singleplayer. Need more investigation on how MelonLoader mods interact with FishNet's NetworkBehaviour lifecycle. |

---

## Hooks Plan (Harmony Patch Targets)

### Phase 1 - Minimum Viable Driver

#### 1. Driver Spawning & Assignment
**No patch needed.** We can instantiate our own NPC or Employee subclass using the existing `EmployeeManager.CreateEmployee_Server()` pattern, or spawn a basic NPC prefab directly.

#### 2. Vehicle Navigation
**No patch needed.** `VehicleAgent.Navigate(Vector3, NavigationSettings, callback)` is a public method. We call it directly on the vehicle's Agent component.

#### 3. NPC Vehicle Entry/Exit
**No patch needed.** `NPC.EnterVehicle(connection, vehicle)` and `NPC.ExitVehicle()` are public virtual ObserversRpc methods.

#### 4. Item Loading at Source
**No patch needed.** `StorageEntity.InsertItem()` and `ItemSlot.ClearStoredInstance()` are public methods. We transfer items between source storage and vehicle storage programmatically.

#### 5. Item Unloading at Destination
**No patch needed.** Same API as loading but in reverse. Transfer from vehicle storage to destination storage.

#### 6. Time-Based Scheduling
**No patch needed.** Subscribe to `TimeManager.Instance.onMinutePass` or `onHourPass`.

#### 7. Wage Deduction
**No patch needed.** Call `MoneyManager.Instance.ChangeCashBalance(-wage)`.

#### Patches That ARE Needed:

| Target | Method | Patch Type | Purpose |
|--------|--------|-----------|---------|
| `SaveManager` | `Save()` | **Postfix** | Trigger save of our custom driver data after game saves |
| `LoadManager` | Load sequence | **Postfix** | Load our driver assignments after game loads |
| `TimeManager` | `StartSleep()` or `onSleepEnd` | **Postfix** | Handle day transitions (reset PaidForToday, check if driver was mid-route) |
| `Property` | `OnDestroy()` or ownership change | **Postfix** | Clean up driver assignments if property is sold/lost |
| `DeliveryManager` | `OnTimePass()` | **Prefix/Postfix** | Optionally intercept deliveries to use our drivers instead of magic-spawned vehicles |

### Phase 2 - Enhanced Integration

| Target | Method | Patch Type | Purpose |
|--------|--------|-----------|---------|
| `EmployeeManager` | `GetEmployeePrefab()` | **Postfix** | Return our custom driver prefab for a new employee type |
| `EEmployeeType` | (enum) | **No patch possible** | Would need to use integer casting or a parallel tracking system |
| `ManagementInterface` | UI methods | **Postfix** | Add driver management to existing UI |
| `VehicleManager` | `SpawnAndReturnVehicle()` | **Postfix** | Track vehicles assigned to drivers |

---

## Risks

### High Risk

1. **FishNet Networking Compatibility.** Every major game class extends `NetworkBehaviour` and uses `[ServerRpc]`/`[ObserversRpc]` attributes. Our mod-spawned objects won't have proper network registration. In singleplayer this may be fine (host == client), but in multiplayer we'd need to ensure our NPC/vehicle manipulation goes through the correct RPC channels. **Mitigation:** Start singleplayer-only; wrap all mutations in `if (InstanceFinder.IsServer)` checks.
**Status (validated milestone 1):** `InstanceFinder.ServerManager.Spawn(gameObject)` confirmed as the correct FishNet spawn entry point from MelonLoader in singleplayer/host context. Mod-spawned NetworkBehaviours register and persist correctly.
**Status (validated milestone 4):** Further validated. All M4 operations (NPC spawn, vehicle spawn, drive, dock occupancy, cargo transfer, vanilla coexistence) stable in singleplayer. Multiplayer remains untested per scope.

2. **Vehicle Road Graph Availability.** `VehicleAgent.Navigate()` requires the vehicle to be within 6f of the vehicle road graph. If loading docks are off the road graph, navigation will fail or teleport the vehicle. **Mitigation:** Check `GetDistanceFromVehicleGraph()` before navigating; use `NavigationSettings.ensureProximityToGraph = true`.
**Status (validated milestone 2):** Downgraded. `ParkingLot.EntryPoint` is graph-connected — no `VehicleTeleporter` triggered during a 34.5m drive between parking lots. Loading docks may still need separate verification in M4, but the parking-lot case is confirmed.
**Status (validated milestone 4):** Fully resolved for ParkingLot and LoadingDock cases. EntryPoints are reliably reachable on the vehicle road graph.

3. **NPC Spawning Without Prefab.** The `EmployeeManager` uses specific prefabs per `EEmployeeType`. We can't add a new enum value cleanly. **Mitigation:** Either clone an existing employee prefab at runtime and modify it, or spawn a generic NPC prefab and manage the driver logic externally (not as a true Employee subclass).
**Status (validated milestone 1):** Cloning Employee prefabs (BotanistPrefab) and spawning via `InstanceFinder.ServerManager.Spawn()` without calling `Initialize()` works — Awake/Start handle null Property gracefully. No crash, NPC renders and persists.

### Medium Risk

4. **Save System Hook Timing.** If our Harmony postfix on `SaveManager.Save()` runs at the wrong time in the save pipeline, we might write incomplete data or miss the save entirely. **Mitigation:** Use `ISaveable` interface properly and call `RegisterSaveable()` if possible from MelonLoader, or use a file watcher approach.
**Status (validated milestone 1):** `Singleton<SaveManager>.Instance.RegisterSaveable(this)` works from a mod-created MonoBehaviour. `SaveManager.onSaveStart` and `LoadManager.onLoadComplete` UnityEvents fire reliably — no Harmony patches needed for save/load timing.

5. **Game Update Fragility.** The decompiled code has no obfuscation (class/method names are clear), which is good. However, any game update could rename methods, change signatures, or restructure class hierarchies. Vehicle AI (VehicleAgent) is the most complex and likely to change. **Mitigation:** Keep Harmony patches minimal (we need very few); prefer public API calls over patches.

6. **Loading Dock Occupancy Conflicts.** `LoadingDock.IsInUse` checks for both DynamicOccupant and StaticOccupant. If a delivery is already pending and our driver arrives, there could be conflicts. **Mitigation:** Check `DeliveryManager.IsLoadingBayFree()` before dispatching driver.
**Status (validated milestone 4):** Resolved. `SetStaticOccupant` + null-clear pattern coexists cleanly with vanilla DynamicOccupant flow. `IsInUse` aggregation is correct.

7. **AI Pathfinding Failures.** `VehicleAgent.Navigate()` can fail (graph not reachable, stuck detection). The `ENavigationResult.Failed` callback needs robust handling. **Mitigation:** Implement retry logic with exponential backoff; use `VehicleTeleporter` as last resort.
**Status (validated milestone 2):** Partially downgraded. Short cross-property routes complete cleanly with `ENavigationResult.Complete`. Longer routes and edge cases (blocked paths, distant destinations) are still untested.
**Status (validated milestone 4):** Further downgraded. Long-distance routes (~233m) complete cleanly. Failure callback path still untested.
**Status (validated milestone 5):** REVISED. Short and medium routes work, but a new failure mode was observed: vehicle became physically stuck against a tree on the Storage Unit → Hyland Manor leg. Neither `GetIsStuck()` detection nor `VehicleTeleporter` recovery triggered in observed time. Required manual intervention. Severity: medium for current dev testing, high for production use cases like overnight scheduled routes (M7).

### Low Risk

8. **Employee Wage Cash-Register Model.** The existing wage system requires physical cash in an EmployeeHome. If we use a different payment model (direct bank deduction), this is simpler but diverges from vanilla behavior. Players may find it inconsistent. **Mitigation:** Offer both options in mod config.

9. **Vehicle Storage Slot Limits.** `StorageEntity.MAX_SLOTS = 20`. If cargo exceeds slot capacity, items will be lost. **Mitigation:** Check `HowManyCanFit()` before loading; split large shipments.
**Status (validated milestone 3):** Confirmed manageable. Test transfers stayed within the 20-slot limit and completed cleanly. Real implementation should still call `HowManyCanFit()` before bulk inserts to handle edge cases.

10. **NavMesh/Vehicle Graph Mismatch.** NPCs walk on NavMesh; vehicles drive on the vehicle road graph. The transition point (NPC walks to parked vehicle) needs both systems to connect at the vehicle's position. If a vehicle is parked somewhere the NavMesh can't reach, the NPC can't get to it. **Mitigation:** Park vehicles at known accessible locations (existing ParkingLots have both graph connections).

---

## Validated Assumptions

> Confirmed empirically via milestone testing (working in-game). Future sessions can treat these as ground truth.

### Validated in M1 (Hello World)

- **Cloning Employee prefabs without Initialize() is safe.** Cloning BotanistPrefab and spawning without calling `Initialize()` does not crash — `Awake()`/`Start()` handle null `Property` gracefully.
- **`InstanceFinder.ServerManager.Spawn(gameObject)` is the correct FishNet spawn path.** This is the right entry point for spawning mod-created NetworkBehaviours from a MelonLoader mod in singleplayer/host context.
- **`RegisterSaveable()` works from mod-created MonoBehaviours.** Calling `Singleton<SaveManager>.Instance.RegisterSaveable(this)` during `OnSceneWasLoaded("Main")` successfully registers custom saveables into the game's save pipeline.
- **`SaveManager.onSaveStart` and `LoadManager.onLoadComplete` are sufficient for save/load timing.** These UnityEvents fire reliably; we do not need to implement the `Loader.Load()` pipeline or use Harmony patches for persistence.
- **Writing save data outside the game's managed save folder is stable.** Storing mod data under `Application.persistentDataPath/<modname>/<slotId>.json` bypasses the game's save-folder cleanup and survives reloads without corruption.
- **Re-registration on scene reload works via `OnSceneWasLoaded`.** After quit-to-menu, `DontDestroyOnLoad` GameObjects survive but `SaveManager.Saveables` gets cleared — re-hooking `OnSceneWasLoaded("Main")` and re-registering handles this correctly.

### Validated in M2 (Driving)

- **`ParkingLot.EntryPoint` positions are reachable on the vehicle road graph.** No teleport-back-to-graph (`VehicleTeleporter`) was triggered during a 34.5m drive between parking lots.
- **`VehicleAgent.Navigate(destination, settings, callback)` completes successfully for short cross-property distances.** The `ENavigationResult.Complete` callback fired; the failure path was not exercised.
- **`NPC.EnterVehicle()` and `NPC.ExitVehicle()` work from a mod context without patches.** No Harmony patches or RPC wrapping needed in singleplayer/host.
- **`NPCMovement.SetDestination(position, callback)` works for mod-driven foot pathfinding.** NPC walked 4.6m to vehicle in 7.7s without issues.
- **`Vehicle.Park(parkData)` final teleport-snap into the parking spot is intended behavior.** Consistent with vanilla NPC behavior — the visual snap is correct, not a bug.
- **The full driver state machine flow works end-to-end with no patches.** `Idle → WalkingToVehicle → EnteringVehicle → Driving → Parking → ExitingVehicle → Done` completed successfully.

### Validated in M3 (Cargo Transfer)

- **`StorageEntity.InsertItem(itemInstance, networkUpdate)` works from a mod context.** Successfully transfers items into a vehicle's Storage and into a destination StorageEntity — no RPC wrapping or Harmony patches needed.
- **`ItemSlot.ClearStoredInstance()` reliably empties source slots.** No orphan state or duplicate items observed after clearing — source slots are clean for reuse.
- **`ItemInstance.GetCopy(quantity)` produces transferable item instances.** Copied instances survive insertion into different containers without state loss or corruption.
- **`Registry.GetItem(itemId)` and `ItemDefinition.GetDefaultInstance(quantity)` are valid for programmatic item creation.** Used to create test items from a mod — instances are fully functional and insertable.
- **NPC stays in vehicle during cargo transfer without issues.** No need to dismount the NPC at source or destination for plain StorageEntity transfers.
- **The driver state machine handles two driving legs (pickup + delivery) cleanly.** `VehicleAgent.Navigate` is callable a second time on the same vehicle without re-initialization or stuck states.
- **Item count logging before/after each transfer matches expectations.** No items lost or duplicated across the full pickup → delivery flow.

### Validated in M4 (Loading Docks)

- **`LoadingDock.SetStaticOccupant(vehicle)` and `SetStaticOccupant(null)` work correctly from a mod context.** No Harmony patch on `RefreshOccupant` needed — direct calls mark docks occupied/released cleanly.
- **`IsInUse` correctly aggregates StaticOccupant + DynamicOccupant.** When our mod releases static occupancy but a vanilla DynamicOccupant is present, `IsInUse` remains true (correct behavior).
- **Driver state machine handles dock contention gracefully.** When destination parking has no free spots (e.g. occupied by a vanilla delivery vehicle), `Vehicle.Park()` is skipped and flow continues without crashing — vehicle stops at the parking entry.
- **Vanilla delivery system and mod driver system coexist without state corruption.** Vanilla deliveries continue working at all docks before, during, and after mod driver activity.
- **Cargo transfer at docks uses the same direct-Storage transfer pattern as M3.** `OutputSlots` are populated for DynamicOccupants only (not StaticOccupants), so we do not interact with OutputSlots.
- **Long-distance driving (~233m, Storage Unit → Hyland Manor) completes via `VehicleAgent.Navigate`.** Realistic ~5 minute transit time with no pathfinding failures or stuck detection triggers.
- **Vehicle-NPC collisions during AI driving are non-fatal and unattributed.** Struck NPCs receive ~36 HP damage and ragdoll, but `DriverPlayer == null` for NPC drivers means no relationship penalty, no wanted-level, no police response, no witness reaction. See ./Mod/INVESTIGATION_VEHICLE_DAMAGE.md.

### Validated in M5 (Round-Trip Routes)

- **`GUIDManager.GetObject<LoadingDock>(new Guid(guidString))` reliably resolves dock references from persisted GUIDs.** Confirms LoadingDock implements IGUIDRegisterable correctly and that GUID-based identification is the right strategy for M6 persistence.
- **The same dock can serve multiple roles in a single route (pickup at stop 1, dropoff at stop 4) without state contamination.** `SetStaticOccupant` set/clear cycles cleanly across reuse.
- **Cargo state persists correctly across multiple stops.** Items picked up at stop 1 (5x cash) were retained through stop 2's failed dropoff and stop 3's skipped pickup, then successfully delivered at stop 4 — no item duplication or loss.
- **Capacity overflow handling works as designed.** When vehicle is full at a pickup stop, the transfer is logged and skipped, and the route continues to the next stop without aborting.
- **Missing-storage graceful degradation works.** When a dock has no nearby WorldStorageEntity, the transfer is skipped with a warning and the route continues. (See Phase 2 Enhancements — this is currently a silent test-validity problem that needs production hardening.)
- **Route data structure (`Route`, `RouteStop`, `RouteAssignment` in Route.cs) is sufficient to express N-stop routes with same-dock reuse.** Ready for M6 JSON serialization after a small refactor (see Phase 2 Enhancements).
- **State machine refactor to handle N stops via `RouteAssignment.CurrentStopIndex` works correctly.** Stop advancement, route completion detection, and final exit transitions all execute cleanly.
- **Long-distance multi-leg routes complete successfully.** Total test route took ~463 seconds (~7.7 minutes real-time) covering Storage Unit ↔ Hyland Manor twice.

---

## Spike Findings

### Spike 1: Employee Subtype vs. Parallel System

#### How tightly is EEmployeeType wired in?

`EEmployeeType` has 4 values: `Botanist`, `Handler`, `Chemist`, `Cleaner` (`Employees/EEmployeeType.cs`). The enum is referenced in **9 distinct locations** across the codebase:

| Location | File | Lines | What branches on it |
|----------|------|-------|---------------------|
| **Prefab factory** | `Employees/EmployeeManager.cs` | 190-204 | `GetEmployeePrefab()` - switch returns typed prefab |
| **Employee home visuals** | `Employees/EmployeeHome.cs` | 87-101 | `UpdateMaterial()` - switch sets material per type |
| **Bed visuals** | `ObjectScripts/Bed.cs` | 115-129 | `UpdateMaterial()` - switch sets blanket texture |
| **Hiring dialogue** | `Dialogue/DialogueController_Fixer.cs` | 23-47 | If/else maps dialogue choice → enum value |
| **Hiring cost check** | `Dialogue/DialogueController_Fixer.cs` | 86 | Uses prefab's `SigningFee` for cost validation |
| **Hiring cost display** | `Dialogue/DialogueController_Fixer.cs` | 116 | Uses prefab's `DailyWage` for UI text |
| **Save loader** | `Persistence/Loaders/EmployeeLoader.cs` | 38-54 | Maps data type name → enum for reconstruction |
| **Legacy loader** | `Persistence/Loaders/LegacyEmployeeLoader.cs` | 49-65 | Same mapping as above |
| **Quest system** | `Quests/Quest_Employees.cs` | 12+ | LINQ match `x.EmployeeType == type` |

Additionally, station configurations use **`typeof()` reflection** (not the enum) to enforce type requirements:
- `SpawnStationConfiguration.cs` L30: `TypeRequirement = typeof(Botanist)`
- `PotConfiguration.cs` L65: `TypeRequirement = typeof(Botanist)`
- `PackagingStationConfiguration.cs` L30: `TypeRequirement = typeof(Packager)`
- `MixingStationConfiguration.cs` L30: `TypeRequirement = typeof(Chemist)`
- `CauldronConfiguration.cs` L30: `TypeRequirement = typeof(Chemist)`
- `BrickPressConfiguration.cs` L30: `TypeRequirement = typeof(Packager)`
- `DryingRackConfiguration.cs` L31: `TypeRequirement = typeof(Botanist)`

#### Do subclasses diverge?

All four subclasses (`Botanist.cs`, `Chemist.cs`, `Cleaner.cs`, `Packager.cs`) follow an **identical pattern**: implement `IConfigurable`, hold a type-specific Configuration object, and expose `SetConfigurer()` as a ServerRpc. The base `Employee` class does **no internal branching** on `this.Type` — all divergence happens through polymorphic overrides (`ResetConfiguration()`, `IsAnyWorkInProgress()`, `ShouldIdle()`). The `Type` field (Employee.cs L1115) is purely a data label.

Note: `EEmployeeType.Handler` maps to the `Packager` class and `PackagerPrefab` — naming inconsistency in the game itself.

#### Recommendation: **Parallel DeliveryDriver system**

- **Adding a 5th enum value requires patching 9+ files**, including switch statements in `GetEmployeePrefab()`, `EmployeeHome.UpdateMaterial()`, `Bed.UpdateMaterial()`, the Fixer dialogue controller, both save loaders, and the quest system. This is a fragile surface area for a mod to maintain across game updates.
- **The `typeof()` station configurations are a dead end** — our drivers don't work at stations, so the IConfigurable pattern adds complexity with zero benefit.
- **A parallel system lets us own our lifecycle entirely**: custom spawning, custom save data, custom scheduling, no dependence on the Fixer NPC's dialogue tree or the EmployeeHome cash-register wage model. We can still reuse the `NPC` base class and `VehiclePatrolBehaviour` pattern without touching the Employee hierarchy.

---

### Spike 2: Loading Dock Vehicle Graph Reachability

#### How does the existing delivery system get vehicles to docks?

**It doesn't drive them — it teleports.** The entire existing delivery vehicle flow is instant placement:

**DeliveryVehicle.Activate()** (`Delivery/DeliveryVehicle.cs` L32-47):
```csharp
ParkingLot parking = instance.LoadingDock.Parking;
instance.LoadingDock.SetStaticOccupant(this.Vehicle);
this.Vehicle.Park(null, new ParkData {
    lotGUID = parking.GUID,
    spotIndex = 0,
    alignment = parking.ParkingSpots[0].Alignment
}, false);
this.Vehicle.SetVisible(true);
```

**LandVehicle.Park()** (`Vehicles/LandVehicle.cs` L1434-1461) calls `AlignTo()` which directly sets `transform.position` and `Rb.position` — **pure teleportation**, no navigation. Physics are disabled via `UpdatePhysicallySimulated(false)`.

**Deactivation** (`DeliveryVehicle.cs` L50-63) is equally instant: `ExitPark(false)` → `SetVisible(false)` → `SetTransform(0, -100, 0)` — teleport off-world.

#### Are LoadingDock and ParkingLot co-located?

Each `LoadingDock` has a `ParkingLot Parking` reference (`LoadingDock.cs` L230). This is a field set in the Inspector — the ParkingLot can be at any position. The dock uses a `VehicleDetector` (`DevUtilities/VehicleDetector.cs`) with trigger colliders to detect nearby vehicles (checks `closestVehicle` when speed < 2 km/h in `RefreshOccupant()` every 1 second at L126-148). The `VehicleDetector` has an activation distance of 20 units (`ACTIVATION_DISTANCE_SQ = 400f` at L156).

**ParkingLot** (`Map/ParkingLot.cs`, 107 lines) has:
- `List<ParkingSpot> ParkingSpots` — spots with `AlignmentPoint` transforms
- `Transform EntryPoint` — where vehicles enter
- `Transform ExitPoint` — where vehicles exit (optional)
- `GetRandomFreeSpotIndex()` (L57-65) — finds open spots
- `EParkingAlignment` — `FrontToKerb` or `RearToKerb`

#### What this means for our driver

Our driver **can drive to the dock's ParkingLot** using `VehicleAgent.Navigate()` to reach the `ParkingLot.EntryPoint` position. The ParkingLot's EntryPoint should be on or near the vehicle road graph (since vanilla NPC vehicles like police cars use ParkingLots too). Once near the entry, we call `Vehicle.Park()` to snap into the spot — same teleport-to-spot the game uses, but only the final alignment, not the whole journey.

**Proposed flow:**
1. `VehicleAgent.Navigate(dock.Parking.EntryPoint.position, settings, callback)` — drive there
2. On `ENavigationResult.Complete` callback → `Vehicle.Park(parkData)` — snap to spot
3. `dock.SetStaticOccupant(vehicle)` or let `RefreshOccupant()` detect it as DynamicOccupant
4. Transfer items
5. `Vehicle.ExitPark()` → `VehicleAgent.Navigate(nextDestination)` — drive away

The last-mile alignment (Park snap) is fine since the game itself always teleports for that step. The key question is whether `ParkingLot.EntryPoint` is reachable on the vehicle graph — this is almost certainly true because vanilla NPC vehicles (police patrols via `NPCSignal_DriveToCarPark`) navigate to parking lots already.

---

### Spike 3: DeliveryInstance Interception Point

#### The complete delivery lifecycle

```
Player orders via DeliveryShop.SubmitOrder()
  (UI/Phone/Delivery/DeliveryShop.cs L106-138)
    ↓
Creates DeliveryInstance(status=InTransit, TimeUntilArrival=N)
    ↓
DeliveryManager.SendDelivery(instance)  [ServerRpc, L207]
    ↓
RpcLogic___SendDelivery (L371) → ReceiveDelivery(null, delivery)
    ↓
RpcLogic___ReceiveDelivery (L495-508):
  - Adds to Deliveries list
  - Calls delivery.SetStatus(InTransit)  ← no vehicle spawn yet
  - Fires onDeliveryCreated event         ← HOOKABLE
    ↓
Every minute: OnTimePass(minutes) (L151-198):
  - Decrements TimeUntilArrival
  - When == 0 AND loading bay free:
    1. AddItemsToDeliveryVehicle()        ← items loaded into hidden vehicle
    2. SetDeliveryState("id", Arrived)    ← TRIGGERS MAGIC SPAWN
  - When == 0 AND bay occupied:
    SetDeliveryState("id", Waiting)
    ↓
RpcLogic___SetDeliveryState (L588-608):
  - delivery.SetStatus(Arrived)           ← THE MAGIC SPAWN
  - Fires onDeliveryCompleted if Completed
    ↓
DeliveryInstance.SetStatus(Arrived) (L76-96):
  - ActiveVehicle = GetShopInterface(StoreName).DeliveryVehicle
  - ActiveVehicle.Activate(this)          ← TELEPORT + VISIBLE
```

**Key detail:** Each shop has exactly ONE `DeliveryVehicle` (`UI/Shop/ShopInterface.cs` L806), referenced as a pre-placed scene object. There's no vehicle pool.

#### Events available for hooking

| Event | Location | When it fires | Useful? |
|-------|----------|---------------|---------|
| `DeliveryManager.onDeliveryCreated` | L34 | After InTransit set, BEFORE arrival | Yes — flag deliveries for driver handling |
| `DeliveryManager.onDeliveryCompleted` | L39 | After Completed, vehicle already deactivated | Only for cleanup |
| `DeliveryInstance.onDeliveryCompleted` | L152 | Same as above (UnityEvent) | Only for cleanup |

**No event fires between the Arrived state transition and `Activate()`.** The spawn happens inside `SetStatus()` synchronously.

#### Recommendation: **Option B — Coexist with parallel "driver delivery" flow**

Option A (suppress magic-spawn) is technically feasible by patching `DeliveryInstance.SetStatus()` or `DeliveryManager.RpcLogic___SetDeliveryState()`, but has significant drawbacks:
- We'd need to intercept a networked RPC handler, which is fragile
- We'd need to track which deliveries are "ours" vs. vanilla
- The `AddItemsToDeliveryVehicle()` call happens BEFORE `SetDeliveryState(Arrived)` in `OnTimePass()` (L165-167), populating the hidden shop vehicle's storage — we'd then need to extract those items
- Single DeliveryVehicle per shop means we'd conflict with non-driver deliveries from the same shop

**Option B is cleaner.** Our driver delivery flow runs entirely parallel:

1. **Trigger:** Player assigns a route via our custom UI (source property → destination property)
2. **Loading:** Driver NPC walks to source storage, we programmatically transfer items from source `StorageEntity` into the vehicle's `StorageEntity` using `InsertItem()`
3. **Driving:** `VehicleAgent.Navigate()` to destination `LoadingDock.Parking.EntryPoint`
4. **Docking:** `Vehicle.Park()` at dock's parking lot, `dock.SetStaticOccupant(vehicle)` or let `VehicleDetector` pick it up via `RefreshOccupant()`
5. **Unloading:** Transfer items from vehicle storage to destination storage, or let the player manually unload (dock's OutputSlots will auto-populate from vehicle storage via `LoadingDock.SetOccupant()` L151-172)
6. **Departure:** `Vehicle.ExitPark()` → navigate to next stop or return to base

This uses only public APIs: `StorageEntity.InsertItem()`, `VehicleAgent.Navigate()`, `Vehicle.Park()`, `LoadingDock.SetOccupant()`. The vanilla delivery system continues working independently for shop orders. We check `DeliveryManager.IsLoadingBayFree()` (L201) before dispatching to avoid dock conflicts.

The one Harmony patch worth considering: a **Postfix on `LoadingDock.RefreshOccupant()`** to prevent our driver's vehicle from being cleared as DynamicOccupant while the driver is loading/unloading (since `RefreshOccupant` clears vehicles that leave the trigger zone or exceed 2 km/h). Alternatively, use `SetStaticOccupant()` which isn't cleared by the refresh cycle.

---

## Phase 2 Enhancements (Deferred)

> Ideas surfaced during Phase 1 development. NOT in the current milestone roadmap — for future consideration.

- **Driver "carefulness" stat:** NPCs can hit pedestrians while driving. Per ./Mod/INVESTIGATION_VEHICLE_DAMAGE.md: damage IS applied to struck NPCs (~36 HP at 30 km/h relative speed) but no attribution, relationship penalty, crime, or police response occurs because `DriverPlayer` is null for AI-driven vehicles. The cosmetic concern is NPC ragdolls; the real concern is if a Customer or Dealer is killed by accumulated damage. Phase 2 could add per-driver carefulness affecting routing aggressiveness, speed, and pedestrian avoidance.

- **Vehicle pathing edge cases.** AI-driven vehicles can become physically stuck against scenery (trees, terrain) on longer routes. Observed during M5 testing on the Storage Unit → Hyland Manor leg — vehicle hit a tree, both stuck detection and VehicleTeleporter recovery failed to trigger. Required manual intervention (player physically pushed the vehicle). Affects route reliability for long unattended deliveries. Investigate StuckDetection threshold tuning, VehicleTeleporter trigger conditions, and possibly DriveFlags.ObstacleMode configuration during polish phase. Will become critical when M7 (scheduling) enables overnight automated routes.

- **Silent failure on missing dock storage.** When a pickup or dropoff dock has no nearby WorldStorageEntity, the route silently continues with empty/skipped transfers. Currently logged as a warning, not an error. Observed during M5 test: Loading Dock 2 at Hyland Manor had no nearby storage, causing stop 2's dropoff to be a no-op. The state machine handled it gracefully but the test could not verify cargo transfer at that stop. Future hardening: either require valid storage at route definition time (player-facing validation in M9 UI) or pre-flight check at route execution start (deferred to a small pre-M6 cleanup task).

---

## Updated Architecture Recommendation

**Build a fully parallel delivery driver system** that reuses game infrastructure via public APIs but does not subclass Employee or intercept the vanilla delivery pipeline. Our `DeliveryDriver` should be a standalone `MonoBehaviour` that holds references to an NPC (spawned from a cloned base NPC prefab) and an assigned `LandVehicle`. It manages its own state machine: Idle → WalkToVehicle → DriveToSource → LoadCargo → DriveToDest → UnloadCargo → Return. Time-based scheduling subscribes to `TimeManager.onMinutePass`. Persistence uses a custom `ISaveable` registered with `SaveManager`. Economy uses `MoneyManager.ChangeCashBalance()`. The entire system needs zero Harmony patches for core functionality — only optional patches for save/load timing and preventing dock occupancy conflicts. This is the lowest-risk, highest-maintainability path: we touch no game enums, no RPC handlers, and no UI code in phase 1.
