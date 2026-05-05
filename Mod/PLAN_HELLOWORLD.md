# Hello-World Milestone Plan

## Goal

Prove three things work from a MelonLoader mod:
1. Mod loads, attaches hotkey, logs to console
2. Hotkey spawns a single NPC at player position (cloned Employee prefab)
3. Spawned NPC persists across save → quit → reload

## Architecture

```
Mod/Source/
├── DeliveryDriversMod.cs    ← MelonMod entry point (hotkey, lifecycle)
├── NPCSpawner.cs            ← Spawn logic + MonoBehaviour that implements ISaveable
└── SpawnedNPCData.cs        ← Serializable save data class
```

---

## 1. Mod Entry Point (`DeliveryDriversMod.cs`)

**Class:** `DeliveryDriversMod : MelonMod`

**Lifecycle hooks:**

| MelonLoader Hook | What We Do |
|---|---|
| `OnInitializeMelon()` | Log "mod loaded" |
| `OnSceneWasLoaded(buildIndex, sceneName)` | When sceneName == "Main", create our persistent GameObject with `NPCSpawner` component |
| `OnUpdate()` | Check `Input.GetKeyDown(KeyCode.F9)` → call `NPCSpawner.Instance.SpawnTestNPC()` |

**Key detail:** We create a `GameObject("DeliveryDriverMod_Manager")` with `Object.DontDestroyOnLoad` and attach our `NPCSpawner` MonoBehaviour. This gives us a persistent Unity object for the ISaveable implementation.

**Scene guard:** Only create the manager once. Track via a static bool or null-check on instance.

---

## 2. NPC Spawning (`NPCSpawner.cs`)

**Class:** `NPCSpawner : MonoBehaviour, ISaveable`

### How to get the NPC prefab

```csharp
Employee prefab = NetworkSingleton<EmployeeManager>.Instance.BotanistPrefab;
```

We use BotanistPrefab because it's a fully-configured NPC prefab with NetworkObject, NavMeshAgent, appearance, etc. We're NOT creating a Botanist employee — we just clone the prefab as a visual body.

### Spawn sequence

```csharp
void SpawnTestNPC()
{
    if (!InstanceFinder.IsServer) return;  // singleplayer host == server

    if (Player.Local == null)
    {
        MelonLogger.Warning("Cannot spawn NPC: Player.Local is null");
        return;
    }

    Employee prefab = NetworkSingleton<EmployeeManager>.Instance.BotanistPrefab;
    Vector3 pos = Player.Local.transform.position + Player.Local.transform.forward * 2f;
    Quaternion rot = Player.Local.transform.rotation;

    GameObject npcObj = UnityEngine.Object.Instantiate(prefab.gameObject, pos, rot);

    // Strip Employee-specific behavior (prevent it from trying to find a property)
    // We'll destroy the Employee component after spawn and just keep the NPC base

    // Network spawn
    InstanceFinder.ServerManager.Spawn(npcObj);

    // Generate a GUID for save tracking
    string guid = System.Guid.NewGuid().ToString();

    // Track in our spawned list
    spawnedNPCs.Add(new SpawnedNPCRecord { guid = guid, npcObject = npcObj });

    MelonLogger.Msg($"Spawned test NPC at {pos}, GUID: {guid}");
}
```

### Open question: Employee component cleanup

After instantiating from BotanistPrefab, the clone has a `Botanist` component which extends `Employee` which extends `NPC`. The `Employee.Initialize()` expects to be called with a property assignment. Options:

**Option A (simpler):** Don't call Initialize at all. The NPC will exist visually but won't have schedule/behavior active. For hello-world this is fine — we just need it to exist and persist.

**Option B:** Call a minimal NPC-level init (set name, GUID) without the property assignment. Risk: Employee.Start() or Awake() might do things that fail without a property.

**Decision: Go with Option A.** If the NPC throws errors on spawn, we'll investigate. The hello-world goal is spawn + persist, not behavior.

### NPC tracking

```csharp
private List<SpawnedNPCRecord> spawnedNPCs = new List<SpawnedNPCRecord>();

private class SpawnedNPCRecord
{
    public string guid;
    public GameObject npcObject;
}
```

---

## 3. Save System (`NPCSpawner` implements `ISaveable`)

### Strategy: External save file with ISaveable for timing

We register as ISaveable to hook into the game's save timing, but we write our data to a **separate location** outside the game's managed save folder. This bypasses `SaveManager.ClearBaseLevelOutdatedSaves()` which deletes unapproved files from the save root. Migrating to proper ISaveable path routing is a polish step after hello-world works.

**Save file location:** `Application.persistentDataPath/DeliveryDriverMod/<saveSlotIdentifier>.json`

The `saveSlotIdentifier` is derived from `LoadManager.Instance.LoadedGameFolderPath` (the folder name of the current save slot, e.g. "SaveGame_0"). This scopes our data per save slot.

### ISaveable implementation

```csharp
public string SaveFolderName => "DeliveryDriverMod";
public string SaveFileName => "DeliveryDriverMod";
public bool ShouldSaveUnderFolder => false;
public Loader Loader => _loader;
public List<string> LocalExtraFiles { get; set; } = new List<string>();
public List<string> LocalExtraFolders { get; set; } = new List<string>();
public bool HasChanged { get; set; } = true;
```

### Registration timing

```csharp
void Start()
{
    Singleton<SaveManager>.Instance.RegisterSaveable(this);
    Singleton<SaveManager>.Instance.onSaveStart.AddListener(WriteSaveFile);
}
```

The `NPCSpawner` GameObject is created in `OnSceneWasLoaded("Main")`. At that point, `SaveManager` is already alive (it's a PersistentSingleton created early). We register immediately.

**Why this is safe:** SaveManager.RegisterSaveable() just adds to a list. It doesn't trigger saves. The list persists until scene change (`Clean()` is called on `onPreSceneChange`). Since our GameObject persists across scenes (DontDestroyOnLoad), we re-register when the Main scene loads again.

### GetSaveString()

Returns empty string — we don't use the ISaveable file-write pipeline.

```csharp
public string GetSaveString() => string.Empty;
```

### WriteSaveFile() — our actual save logic

Called via `SaveManager.onSaveStart` event.

```csharp
void WriteSaveFile()
{
    var data = new DeliveryDriverModSaveData();
    data.spawnedNPCs = spawnedNPCs
        .Where(r => r.npcObject != null)
        .Select(r => new NPCPositionData {
            guid = r.guid,
            x = r.npcObject.transform.position.x,
            y = r.npcObject.transform.position.y,
            z = r.npcObject.transform.position.z,
            rotY = r.npcObject.transform.eulerAngles.y
        }).ToArray();

    string json = JsonUtility.ToJson(data, true);
    string dir = Path.Combine(Application.persistentDataPath, "DeliveryDriverMod");
    Directory.CreateDirectory(dir);
    string slotId = new DirectoryInfo(
        Singleton<LoadManager>.Instance.LoadedGameFolderPath).Name;
    File.WriteAllText(Path.Combine(dir, slotId + ".json"), json);
}
```

### Save data written

File: `<persistentDataPath>/DeliveryDriverMod/SaveGame_0.json`

```json
{
    "spawnedNPCs": [
        { "guid": "abc-123", "x": 10.5, "y": 0.0, "z": -5.2, "rotY": 90.0 }
    ]
}
```

---

## 4. Load Flow

### When to load

Subscribe to `LoadManager.Instance.onLoadComplete` UnityEvent. This fires after ALL game systems have loaded their data (NPCManager, VehicleManager, etc.), so singletons are ready.

```csharp
void Start()
{
    Singleton<SaveManager>.Instance.RegisterSaveable(this);
    Singleton<SaveManager>.Instance.onSaveStart.AddListener(WriteSaveFile);
    Singleton<LoadManager>.Instance.onLoadComplete.AddListener(OnGameLoaded);
}
```

### Load implementation

```csharp
void OnGameLoaded()
{
    string dir = Path.Combine(Application.persistentDataPath, "DeliveryDriverMod");
    string slotId = new DirectoryInfo(
        Singleton<LoadManager>.Instance.LoadedGameFolderPath).Name;
    string filePath = Path.Combine(dir, slotId + ".json");

    if (!File.Exists(filePath)) return;  // no previous save data

    string json = File.ReadAllText(filePath);
    var data = JsonUtility.FromJson<DeliveryDriverModSaveData>(json);

    foreach (var npcData in data.spawnedNPCs)
    {
        RespawnNPC(npcData);
    }
}

void RespawnNPC(NPCPositionData data)
{
    if (!InstanceFinder.IsServer) return;

    Employee prefab = NetworkSingleton<EmployeeManager>.Instance.BotanistPrefab;
    Vector3 pos = new Vector3(data.x, data.y, data.z);
    Quaternion rot = Quaternion.Euler(0, data.rotY, 0);

    GameObject npcObj = UnityEngine.Object.Instantiate(prefab.gameObject, pos, rot);
    InstanceFinder.ServerManager.Spawn(npcObj);

    spawnedNPCs.Add(new SpawnedNPCRecord { guid = data.guid, npcObject = npcObj });
    MelonLogger.Msg($"Restored NPC {data.guid} at {pos}");
}
```

### Loader class (minimal)

```csharp
private class DeliveryDriverModLoader : Loader
{
    public override void Load(string mainPath) { }  // no-op, we handle loading ourselves
}
```

The game's LoadManager calls `Loader.Load()` during its load sequence. We don't use this — instead we listen to `onLoadComplete` and handle it ourselves. This keeps our load logic self-contained.

---

## 5. Save Data Class (`SpawnedNPCData.cs`)

```csharp
[Serializable]
public class DeliveryDriverModSaveData
{
    public NPCPositionData[] spawnedNPCs = new NPCPositionData[0];
}

[Serializable]
public class NPCPositionData
{
    public string guid;
    public float x, y, z;
    public float rotY;
}
```

Uses `[Serializable]` + `JsonUtility` (Unity's built-in serializer, same as the game uses).

---

## 6. Lifecycle Summary

```
Game Boot
  └─ MelonLoader loads our DLL
     └─ OnInitializeMelon() → log "loaded"

Scene "Main" loads
  └─ OnSceneWasLoaded("Main")
     └─ Create NPCSpawner GameObject (DontDestroyOnLoad)
        └─ NPCSpawner.Start()
           ├─ RegisterSaveable(this)
           └─ onLoadComplete += OnGameLoaded

Game load completes
  └─ OnGameLoaded()
     └─ Read <persistentDataPath>/DeliveryDriverMod/<slotId>.json
     └─ For each saved NPC: Instantiate prefab → ServerManager.Spawn → track

Player presses F9
  └─ SpawnTestNPC()
     └─ Instantiate BotanistPrefab clone → ServerManager.Spawn → track

Player sleeps / manual save
  └─ SaveManager.onSaveStart fires
     └─ WriteSaveFile() → writes <persistentDataPath>/DeliveryDriverMod/<slotId>.json

Player quits → reloads
  └─ Same flow: scene loads → register → onLoadComplete → read file → respawn
```

---

## 7. Dependencies / References

Our mod DLL needs references to:
- `MelonLoader.dll` (mod API)
- `Assembly-CSharp.dll` (game code — NPC, Employee, EmployeeManager, SaveManager, LoadManager, Player, etc.)
- `FishNet.Runtime.dll` (InstanceFinder, ServerManager, NetworkObject)
- `UnityEngine.CoreModule.dll` (GameObject, MonoBehaviour, Vector3, JsonUtility, etc.)
- `UnityEngine.InputLegacyModule.dll` (Input.GetKeyDown)

---

## 8. Open Questions (to resolve during implementation)

1. **Employee component on cloned NPC:** Will the Botanist/Employee component's Awake()/Start() throw errors without proper initialization? Note: Awake() runs synchronously inside `Instantiate()`, so we cannot Destroy the component "before Awake runs." If Botanist.Awake() throws without a Property, the spawn has already failed. **Fallback:** Switch to a non-Employee NPC prefab. Check NPCManager (RECON.md §2) for plain NPC prefabs — dealers, customers, suppliers, or civilians should work without Property dependencies.

2. **Network spawn without parent NetworkObject:** EmployeeManager uses `base.NetworkObject.Spawn(obj)` — will `InstanceFinder.ServerManager.Spawn(obj)` work identically? FishNet docs suggest yes for server-authoritative spawning.

3. **SaveManager cleanup of our file:** ~~Resolved~~ — we now write to `Application.persistentDataPath/DeliveryDriverMod/` outside the game's save folder, bypassing the cleanup issue entirely. See sections 3 and 4.

4. **Re-registration on scene reload:** When player quits to menu and reloads, `SaveManager.Clean()` clears the Saveables list. Our DontDestroyOnLoad object persists but needs to re-register. We handle this by listening for scene load again in `OnSceneWasLoaded`.

---

## 9. Success Criteria

- [ ] Mod loads without errors, "DeliveryDriversMod loaded" appears in MelonLoader console
- [ ] Pressing F9 in-game spawns a visible NPC 2m in front of the player
- [ ] NPC is physically present (has collider, visible model)
- [ ] Saving the game writes `<persistentDataPath>/DeliveryDriverMod/<slotId>.json`
- [ ] After quit → reload, the NPC reappears at its saved position
- [ ] No errors in console during spawn, save, or load (warnings acceptable)

---

## 10. What This Does NOT Cover

- NPC behavior/AI (it just stands there)
- NPC appearance customization
- Multiple NPC management UI
- Driving, vehicles, deliveries
- Multiplayer correctness (singleplayer only)
- Proper Employee initialization or property assignment
