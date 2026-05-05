using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(DeliveryDriversMod.DeliveryDriversMod), "DeliveryDriversMod", "0.1.0", "Benjafo")]
[assembly: MelonGame("TVGS", "Schedule I")]

namespace DeliveryDriversMod
{
    public class DeliveryDriversMod : MelonMod
    {
        private bool _managerCreated;

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
                _managerCreated = true;
                LoggerInstance.Msg("NPCSpawner manager created");
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

            if (Input.GetKeyDown(KeyCode.F9))
            {
                NPCSpawner.Instance.SpawnTestNPC();
            }
        }
    }
}
