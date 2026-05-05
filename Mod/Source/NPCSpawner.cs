using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FishNet;
using MelonLoader;
using ScheduleOne.DevUtilities;
using ScheduleOne.Employees;
using ScheduleOne.Persistence;
using ScheduleOne.PlayerScripts;
using Newtonsoft.Json;
using UnityEngine;

namespace DeliveryDriversMod
{
    public class NPCSpawner : MonoBehaviour
    {
        public static NPCSpawner Instance { get; private set; }

        private List<SpawnedNPCRecord> spawnedNPCs = new List<SpawnedNPCRecord>();
        private bool _registered;

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        void Start()
        {
            Register();
        }

        /// <summary>
        /// Subscribe to save/load events. Safe to call multiple times.
        /// </summary>
        public void Register()
        {
            if (_registered) return;

            var saveManager = Singleton<SaveManager>.Instance;
            var loadManager = Singleton<LoadManager>.Instance;

            if (saveManager == null || loadManager == null)
            {
                MelonLogger.Warning("[DeliveryDriversMod] SaveManager or LoadManager not ready, deferring registration");
                return;
            }

            saveManager.onSaveComplete.AddListener(WriteSaveFile);
            loadManager.onLoadComplete.AddListener(OnGameLoaded);
            _registered = true;

            MelonLogger.Msg("[DeliveryDriversMod] NPCSpawner registered with save/load events");
        }

        /// <summary>
        /// Unsubscribe event listeners before re-registration on scene reload.
        /// </summary>
        public void Unregister()
        {
            if (!_registered) return;

            var saveManager = Singleton<SaveManager>.Instance;
            var loadManager = Singleton<LoadManager>.Instance;

            if (saveManager != null)
                saveManager.onSaveComplete.RemoveListener(WriteSaveFile);
            if (loadManager != null)
                loadManager.onLoadComplete.RemoveListener(OnGameLoaded);

            _registered = false;
        }

        #region Spawn

        public void SpawnTestNPC()
        {
            if (!InstanceFinder.IsServer)
            {
                MelonLogger.Warning("[DeliveryDriversMod] Cannot spawn NPC: not server");
                return;
            }

            if (Player.Local == null)
            {
                MelonLogger.Warning("[DeliveryDriversMod] Cannot spawn NPC: Player.Local is null");
                return;
            }

            Employee prefab = NetworkSingleton<EmployeeManager>.Instance.BotanistPrefab;
            if (prefab == null)
            {
                MelonLogger.Error("[DeliveryDriversMod] BotanistPrefab is null — cannot spawn");
                return;
            }

            Vector3 pos = Player.Local.transform.position + Player.Local.transform.forward * 2f;
            Quaternion rot = Player.Local.transform.rotation;
            string guid = Guid.NewGuid().ToString();

            SpawnNPCInternal(prefab, pos, rot, guid);
            MelonLogger.Msg("[DeliveryDriversMod] Spawned test NPC at " + pos + ", GUID: " + guid);
        }

        private void SpawnNPCInternal(Employee prefab, Vector3 pos, Quaternion rot, string guid)
        {
            GameObject npcObj = UnityEngine.Object.Instantiate(prefab.gameObject, pos, rot);
            InstanceFinder.ServerManager.Spawn(npcObj);

            spawnedNPCs.Add(new SpawnedNPCRecord { guid = guid, npcObject = npcObj });
        }

        #endregion

        #region Save

        private void WriteSaveFile()
        {
            try
            {
                var data = new DeliveryDriverModSaveData();
                data.spawnedNPCs = spawnedNPCs
                    .Where(r => r.npcObject != null)
                    .Select(r => new NPCPositionData
                    {
                        guid = r.guid,
                        x = r.npcObject.transform.position.x,
                        y = r.npcObject.transform.position.y,
                        z = r.npcObject.transform.position.z,
                        rotY = r.npcObject.transform.eulerAngles.y
                    }).ToArray();

                string json = JsonConvert.SerializeObject(data, Formatting.Indented);
                string filePath = GetSaveFilePath();
                if (filePath == null) return;

                Directory.CreateDirectory(Path.GetDirectoryName(filePath));
                File.WriteAllText(filePath, json);
                MelonLogger.Msg("[DeliveryDriversMod] Saved " + data.spawnedNPCs.Length + " NPC(s) to " + filePath);
            }
            catch (Exception ex)
            {
                MelonLogger.Error("[DeliveryDriversMod] Save failed: " + ex);
            }
        }

        #endregion

        #region Load

        private void OnGameLoaded()
        {
            try
            {
                string filePath = GetSaveFilePath();
                if (filePath == null || !File.Exists(filePath)) return;

                string json = File.ReadAllText(filePath);
                var data = JsonConvert.DeserializeObject<DeliveryDriverModSaveData>(json);
                if (data == null || data.spawnedNPCs == null || data.spawnedNPCs.Length == 0) return;

                if (!InstanceFinder.IsServer)
                {
                    MelonLogger.Warning("[DeliveryDriversMod] Cannot restore NPCs: not server");
                    return;
                }

                Employee prefab = NetworkSingleton<EmployeeManager>.Instance.BotanistPrefab;
                if (prefab == null)
                {
                    MelonLogger.Error("[DeliveryDriversMod] BotanistPrefab is null — cannot restore NPCs");
                    return;
                }

                foreach (var npcData in data.spawnedNPCs)
                {
                    Vector3 pos = new Vector3(npcData.x, npcData.y, npcData.z);
                    Quaternion rot = Quaternion.Euler(0f, npcData.rotY, 0f);
                    SpawnNPCInternal(prefab, pos, rot, npcData.guid);
                    MelonLogger.Msg("[DeliveryDriversMod] Restored NPC " + npcData.guid + " at " + pos);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error("[DeliveryDriversMod] Load failed: " + ex);
            }
        }

        #endregion

        #region Helpers

        private string GetSaveFilePath()
        {
            var loadManager = Singleton<LoadManager>.Instance;
            if (loadManager == null || string.IsNullOrEmpty(loadManager.LoadedGameFolderPath))
            {
                MelonLogger.Warning("[DeliveryDriversMod] No active save slot");
                return null;
            }

            string slotId = new DirectoryInfo(loadManager.LoadedGameFolderPath).Name;
            string dir = Path.Combine(Application.persistentDataPath, "DeliveryDriverMod");
            return Path.Combine(dir, slotId + ".json");
        }

        #endregion

        private class SpawnedNPCRecord
        {
            public string guid;
            public GameObject npcObject;
        }
    }
}
