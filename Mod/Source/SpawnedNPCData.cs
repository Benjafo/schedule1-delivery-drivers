using System;

namespace DeliveryDriversMod
{
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
}
