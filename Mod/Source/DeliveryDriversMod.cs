using System.Collections.Generic;
using FishNet;
using MelonLoader;
using ScheduleOne.DevUtilities;
using ScheduleOne.PlayerScripts;
using ScheduleOne.Vehicles;
using UnityEngine;

[assembly: MelonInfo(typeof(DeliveryDriversMod.DeliveryDriversMod), "DeliveryDriversMod", "0.1.0", "Benjafo")]
[assembly: MelonGame("TVGS", "Schedule I")]

namespace DeliveryDriversMod
{
    public class DeliveryDriversMod : MelonMod
    {
        private bool _managerCreated;
        private int _vehiclePrefabIndex;

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
                Object.DontDestroyOnLoad(go);
                go.AddComponent<NPCSpawner>();
                go.AddComponent<DeliveryDriverBehaviour>();
                _managerCreated = true;
                LoggerInstance.Msg("Manager created (NPCSpawner + DeliveryDriverBehaviour)");
            }
            else if (NPCSpawner.Instance != null)
            {
                // Re-register after returning to Main scene (SaveManager.Clean()
                // clears the Saveables list on scene change).
                NPCSpawner.Instance.Unregister();
                NPCSpawner.Instance.Register();
            }
        }

        public override void OnUpdate()
        {
            if (NPCSpawner.Instance == null) return;

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
                    MelonLogger.Msg("[DeliveryDriversMod] Drive test already in progress");
                }
            }
        }

        private void SpawnTestVehicle()
        {
            if (!InstanceFinder.IsServer)
            {
                MelonLogger.Warning("[DeliveryDriversMod] Cannot spawn vehicle: not server");
                return;
            }

            if (Player.Local == null)
            {
                MelonLogger.Warning("[DeliveryDriversMod] Cannot spawn vehicle: Player.Local is null");
                return;
            }

            var vehicleManager = NetworkSingleton<VehicleManager>.Instance;
            if (vehicleManager == null)
            {
                MelonLogger.Error("[DeliveryDriversMod] VehicleManager not available");
                return;
            }

            List<LandVehicle> prefabs = vehicleManager.VehiclePrefabs;
            if (prefabs == null || prefabs.Count == 0)
            {
                MelonLogger.Error("[DeliveryDriversMod] No vehicle prefabs registered");
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
                MelonLogger.Error("[DeliveryDriversMod] SpawnAndReturnVehicle returned null for code '" + vehicleCode + "'");
                return;
            }

            int countAfter = vehicleManager.PlayerOwnedVehicles.Count;
            MelonLogger.Msg("[DeliveryDriversMod] Spawned vehicle '" + vehicleCode + "' at " + pos +
                " (PlayerOwnedVehicles: " + countBefore + " → " + countAfter + ")");

            // Log all available vehicle codes on first spawn for reference
            if (countBefore == 0)
            {
                var codes = new List<string>();
                foreach (var p in prefabs) codes.Add(p.VehicleCode);
                MelonLogger.Msg("[DeliveryDriversMod] Available vehicle codes: " + string.Join(", ", codes));
            }
        }
    }
}
