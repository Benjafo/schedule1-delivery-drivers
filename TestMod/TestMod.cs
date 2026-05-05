using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(MyMod.TestMod), "Test Mod", "1.0.0", "Benjafo")]
[assembly: MelonGame("TVGS", "Schedule I")]

namespace MyMod {
    public class TestMod : MelonMod {
        public override void OnApplicationStart() {
            MelonLogger.Msg("My mod has loaded!");
        }

        public override void OnUpdate()
        {
            if (Input.GetKeyDown(KeyCode.F1))
            {
                MelonLogger.Msg("F1 key was pressed!");
            }
        }
    }
}