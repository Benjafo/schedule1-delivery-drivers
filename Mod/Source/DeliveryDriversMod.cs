using System;
using System.Collections.Generic;
using System.Linq;
using FishNet;
using MelonLoader;
using ScheduleOne;
using ScheduleOne.Delivery;
using ScheduleOne.DevUtilities;
using ScheduleOne.ItemFramework;
using ScheduleOne.ObjectScripts;
using ScheduleOne.PlayerScripts;
using ScheduleOne.Property;
using ScheduleOne.Storage;
using ScheduleOne.Vehicles;
using ScheduleOne.Vehicles.AI;
using UnityEngine;

[assembly: MelonInfo(typeof(DeliveryDriversMod.DeliveryDriversMod), "DeliveryDriversMod", "0.1.0", "Benjafo")]
[assembly: MelonGame("TVGS", "Schedule I")]

namespace DeliveryDriversMod
{
    public class DeliveryDriversMod : MelonMod
    {
        private bool _managerCreated;
        private int _vehiclePrefabIndex;
        private int _rackTeleportIndex;

        public override void OnInitializeMelon()
        {
            LoggerInstance.Msg("DeliveryDriversMod loaded");
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            if (sceneName != "Main") return;

            if (!_managerCreated)
            {
                var go = new GameObject("DeliveryDriverMod_Manager");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.AddComponent<NPCSpawner>();
                go.AddComponent<DeliveryDriverBehaviour>();
                _managerCreated = true;
                LoggerInstance.Msg("Manager created (NPCSpawner + DeliveryDriverBehaviour)");
            }
            else
            {
                // Re-register after returning to Main scene (SaveManager.Clean()
                // clears the Saveables list on scene change).
                if (NPCSpawner.Instance != null)
                {
                    NPCSpawner.Instance.Unregister();
                    NPCSpawner.Instance.Register();
                }

                // Reset driver state — old NPC/vehicle/storage references are stale
                DeliveryDriverBehaviour.Instance?.ResetState();
            }
        }

        public override void OnUpdate()
        {
            if (NPCSpawner.Instance == null) return;

            if (Input.GetKeyDown(KeyCode.F4))
            {
                AuditDockGraphProximity();
            }

            if (Input.GetKeyDown(KeyCode.F5))
            {
                TeleportToNextEmptyProperty();
            }

            if (Input.GetKeyDown(KeyCode.F6))
            {
                var driver = DeliveryDriverBehaviour.Instance;
                if (driver != null && !driver.IsRunning)
                {
                    driver.TriggerRouteTest();
                }
                else if (driver != null && driver.IsRunning)
                {
                    MelonLogger.Msg("Test already in progress");
                }
            }

            if (Input.GetKeyDown(KeyCode.F7))
            {
                GrantPropertyOwnership();
            }

            if (Input.GetKeyDown(KeyCode.F8))
            {
                SpawnTestVehicle();
            }

            if (Input.GetKeyDown(KeyCode.F9))
            {
                NPCSpawner.Instance.SpawnTestNPC();
            }

            if (Input.GetKeyDown(KeyCode.F10))
            {
                var driver = DeliveryDriverBehaviour.Instance;
                if (driver != null && !driver.IsRunning)
                {
                    driver.TriggerDriveTest();
                }
                else if (driver != null && driver.IsRunning)
                {
                    MelonLogger.Msg("Drive test already in progress");
                }
            }

            if (Input.GetKeyDown(KeyCode.F11))
            {
                var driver = DeliveryDriverBehaviour.Instance;
                if (driver != null && !driver.IsRunning)
                {
                    driver.TriggerCargoTest();
                }
                else if (driver != null && driver.IsRunning)
                {
                    MelonLogger.Msg("Test already in progress");
                }
            }

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
        }

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

            if (candidates.Count == 0)
            {
                MelonLogger.Msg("F7: Already own all " + ownedWithDocks +
                    " properties with loading docks — no grant needed");
                return;
            }

            int granted = 0;
            foreach (var prop in candidates)
            {
                MelonLogger.Msg("F7: Granting ownership of '" + prop.PropertyName +
                    "' (code: " + prop.PropertyCode + ", docks: " + prop.LoadingDockCount + ")");
                prop.SetOwned();
                granted++;
            }

            MelonLogger.Msg("F7: Granted " + granted + " properties. Total owned with docks: " +
                (ownedWithDocks + granted));
        }

        private void SpawnTestVehicle()
        {
            if (!InstanceFinder.IsServer)
            {
                MelonLogger.Warning("Cannot spawn vehicle: not server");
                return;
            }

            if (Player.Local == null)
            {
                MelonLogger.Warning("Cannot spawn vehicle: Player.Local is null");
                return;
            }

            var vehicleManager = NetworkSingleton<VehicleManager>.Instance;
            if (vehicleManager == null)
            {
                MelonLogger.Error("VehicleManager not available");
                return;
            }

            List<LandVehicle> prefabs = vehicleManager.VehiclePrefabs;
            if (prefabs == null || prefabs.Count == 0)
            {
                MelonLogger.Error("No vehicle prefabs registered");
                return;
            }

            // Cycle through available prefabs on repeated presses
            _vehiclePrefabIndex = _vehiclePrefabIndex % prefabs.Count;
            string vehicleCode = prefabs[_vehiclePrefabIndex].VehicleCode;
            _vehiclePrefabIndex = (_vehiclePrefabIndex + 1) % prefabs.Count;

            Vector3 pos = Player.Local.transform.position + Player.Local.transform.forward * 5f;
            Quaternion rot = Player.Local.transform.rotation;

            int countBefore = vehicleManager.PlayerOwnedVehicles.Count;

            LandVehicle spawned = vehicleManager.SpawnAndReturnVehicle(vehicleCode, pos, rot, true);
            if (spawned == null)
            {
                MelonLogger.Error("SpawnAndReturnVehicle returned null for code '" + vehicleCode + "'");
                return;
            }

            int countAfter = vehicleManager.PlayerOwnedVehicles.Count;
            MelonLogger.Msg("Spawned vehicle '" + vehicleCode + "' at " + pos +
                " (PlayerOwnedVehicles: " + countBefore + " → " + countAfter + ")");

            // Log all available vehicle codes on first spawn for reference
            if (countBefore == 0)
            {
                var codes = new List<string>();
                foreach (var p in prefabs) codes.Add(p.VehicleCode);
                MelonLogger.Msg("Available vehicle codes: " + string.Join(", ", codes));
            }
        }

        private static void AuditDockGraphProximity()
        {
            MelonLogger.Msg("F4: AUDIT — measuring dock EntryPoint distance to General Vehicle Graph");
            int count = 0;
            float threshold = 6f; // RECON §1: vehicles >6f from graph get teleported back
            foreach (var prop in Property.OwnedProperties)
                count += AuditPropertyDocks(prop, "owned", threshold);
            foreach (var prop in Property.UnownedProperties)
                count += AuditPropertyDocks(prop, "unowned", threshold);
            MelonLogger.Msg("F4: AUDIT done — " + count + " dock(s) measured");
        }

        private static int AuditPropertyDocks(Property prop, string ownership, float threshold)
        {
            if (prop == null || prop.LoadingDocks == null) return 0;
            int n = 0;
            foreach (var dock in prop.LoadingDocks)
            {
                if (dock == null) continue;
                if (dock.Parking == null || dock.Parking.EntryPoint == null)
                {
                    MelonLogger.Msg("AUDIT  " + ownership + "  " + prop.PropertyName +
                        "  " + dock.Name + "  NO ENTRYPOINT");
                    n++;
                    continue;
                }
                Vector3 entry = dock.Parking.EntryPoint.position;
                Vector3 onGraph;
                try { onGraph = NavigationUtility.SampleVehicleGraph(entry); }
                catch (Exception ex)
                {
                    MelonLogger.Msg("AUDIT  " + ownership + "  " + prop.PropertyName +
                        "  " + dock.Name + "  SAMPLE FAILED: " + ex.Message);
                    n++;
                    continue;
                }
                float dist = Vector3.Distance(entry, onGraph);
                string verdict = dist <= threshold ? "OK" : "OFF-GRAPH";
                MelonLogger.Msg("AUDIT  " + ownership + "  " + prop.PropertyName +
                    "  " + dock.Name +
                    "  entry=" + entry.ToString("F2") +
                    "  graph=" + onGraph.ToString("F2") +
                    "  dist=" + dist.ToString("F2") + "m  " + verdict);
                n++;
            }
            return n;
        }

        private void TeleportToNextEmptyProperty()
        {
            if (Player.Local == null)
            {
                MelonLogger.Warning("F5: Player.Local is null");
                return;
            }

            var withDocks = Property.OwnedProperties
                .Where(p => p != null && p.LoadingDocks != null && p.LoadingDocks.Length > 0)
                .Where(p => p.LoadingDocks.Any(d => d != null && d.Parking?.EntryPoint != null))
                .ToList();

            if (withDocks.Count == 0)
            {
                MelonLogger.Warning("F5: No owned properties with usable docks");
                return;
            }

            var empties = withDocks.Where(p => !PropertyHasStorage(p)).ToList();
            List<Property> cycle;
            string mode;
            if (empties.Count > 0)
            {
                EnsureStorageRackInInventory();
                cycle = empties;
                mode = "missing storage";
            }
            else
            {
                MelonLogger.Msg("F5: All owned properties with docks already have in-bounds storage — cycling for positioning.");
                cycle = withDocks;
                mode = "all owned";
            }

            _rackTeleportIndex %= cycle.Count;
            var target = cycle[_rackTeleportIndex];
            var firstDock = target.LoadingDocks.FirstOrDefault(d => d != null && d.Parking?.EntryPoint != null);
            Vector3 pos = firstDock != null
                ? firstDock.transform.position + Vector3.up * 1f
                : target.transform.position + Vector3.up * 1f;

            Player.Local.transform.position = pos;
            MelonLogger.Msg("F5: Teleported to '" + target.PropertyName + "' (" +
                (_rackTeleportIndex + 1) + "/" + cycle.Count + " " + mode + ") at " + pos);
            _rackTeleportIndex++;
        }

        private static bool PropertyHasStorage(Property prop)
        {
            foreach (var s in WorldStorageEntity.All)
            {
                if (s == null || !s.gameObject.activeInHierarchy) continue;
                if (prop.DoBoundsContainPoint(s.transform.position)) return true;
            }
            foreach (var p in UnityEngine.Object.FindObjectsOfType<PlaceableStorageEntity>())
            {
                if (p == null || !p.gameObject.activeInHierarchy || p.StorageEntity == null) continue;
                if (prop.DoBoundsContainPoint(p.transform.position)) return true;
            }
            return false;
        }

        private void EnsureStorageRackInInventory()
        {
            var inv = PlayerSingleton<PlayerInventory>.Instance;
            if (inv == null)
            {
                MelonLogger.Warning("F5: PlayerInventory not available");
                return;
            }

            // Try common candidate IDs first, then scan the registry for any item whose
            // ID contains "storage", "rack", and "large".
            string[] candidates = { "storagerack_large", "largestoragerack", "storagerack-large", "large_storage_rack" };
            ItemDefinition def = null;
            foreach (var id in candidates)
            {
                if (Registry.ItemExists(id)) { def = Registry.GetItem(id); break; }
            }
            if (def == null)
            {
                var all = Singleton<Registry>.Instance.GetAllItems();
                def = all.FirstOrDefault(d => d != null && !string.IsNullOrEmpty(d.ID) &&
                    d.ID.IndexOf("storage", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    d.ID.IndexOf("rack", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    d.ID.IndexOf("large", StringComparison.OrdinalIgnoreCase) >= 0);
            }
            if (def == null)
            {
                MelonLogger.Warning("F5: Could not find a large storage rack item in the Registry");
                return;
            }

            uint have = inv.GetAmountOfItem(def.ID);
            if (have > 0)
            {
                MelonLogger.Msg("F5: Inventory already has " + have + "x " + def.ID);
                return;
            }

            var instance = def.GetDefaultInstance(1);
            if (!inv.CanItemFitInInventory(instance, 1))
            {
                MelonLogger.Warning("F5: Inventory full, cannot add " + def.ID);
                return;
            }
            inv.AddItemToInventory(instance);
            MelonLogger.Msg("F5: Added 1x " + def.ID + " to inventory");
        }
    }
}
