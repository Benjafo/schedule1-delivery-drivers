using System;
using System.Collections.Generic;
using System.Linq;
using FishNet;
using MelonLoader;
using ScheduleOne;
using ScheduleOne.DevUtilities;
using ScheduleOne.ItemFramework;
using ScheduleOne.Map;
using ScheduleOne.NPCs;
using ScheduleOne.PlayerScripts;
using ScheduleOne.Storage;
using ScheduleOne.Vehicles;
using ScheduleOne.Vehicles.AI;
using UnityEngine;

namespace DeliveryDriversMod
{
    public class DeliveryDriverBehaviour : MonoBehaviour
    {
        public static DeliveryDriverBehaviour Instance { get; private set; }

        public enum DriverState
        {
            Idle,
            WalkingToVehicle,
            EnteringVehicle,
            Driving,
            Parking,
            LoadingCargo,
            UnloadingCargo,
            ExitingVehicle,
            Done
        }

        private enum DeliveryLeg { Pickup, Delivery }

        // Core references
        private NPC _npc;
        private LandVehicle _vehicle;
        private ParkingLot _destination;

        // Cargo test fields (null when running simple F10 drive test)
        private StorageEntity _sourceStorage;
        private StorageEntity _destStorage;
        private ParkingLot _sourceParkingLot;
        private ParkingLot _destParkingLot;
        private DeliveryLeg _currentLeg;

        // State
        private DriverState _state = DriverState.Idle;
        private float _stateTimer;
        private Vector3 _walkTarget;
        private bool _navigationCallbackFired;
        private VehicleAgent.ENavigationResult _navigationResult;

        public bool IsRunning => _state != DriverState.Idle && _state != DriverState.Done;

        private const float WALK_TIMEOUT = 15f;
        private const float WALK_ARRIVE_DIST = 2.5f;
        private const float ENTER_DELAY = 0.5f;
        private const float UNPARK_DELAY = 0.5f;
        private const float VEHICLE_SEARCH_RADIUS = 50f;
        private const float MIN_DESTINATION_DIST = 30f;
        private const float STORAGE_LOT_SEARCH_RADIUS = 50f;
        private const string TEST_ITEM_ID = "cash";
        private const int TEST_ITEM_COUNT = 5;

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;
        }

        /// <summary>
        /// Reset state machine after scene reload (quit-without-save, etc.).
        /// References to NPC/vehicle/storage are stale after reload.
        /// </summary>
        public void ResetState()
        {
            if (IsRunning)
            {
                MelonLogger.Msg("[DeliveryDriversMod] Resetting stale driver state (" + _state + ")");
            }
            _npc = null;
            _vehicle = null;
            _destination = null;
            _sourceStorage = null;
            _destStorage = null;
            _sourceParkingLot = null;
            _destParkingLot = null;
            _state = DriverState.Idle;
        }

        #region Test Triggers

        public void TriggerDriveTest()
        {
            if (IsRunning)
            {
                MelonLogger.Warning("[DeliveryDriversMod] Drive test already in progress");
                return;
            }

            if (!InstanceFinder.IsServer)
            {
                MelonLogger.Warning("[DeliveryDriversMod] Cannot start drive test: not server");
                return;
            }

            if (Player.Local == null)
            {
                MelonLogger.Warning("[DeliveryDriversMod] Cannot start drive test: Player.Local is null");
                return;
            }

            // Find vehicle
            var vehicle = FindNearestPlayerVehicle();
            if (vehicle == null)
            {
                MelonLogger.Warning("[DeliveryDriversMod] No player-owned vehicle within " + VEHICLE_SEARCH_RADIUS + "m");
                return;
            }

            // Find NPC
            var npcObj = NPCSpawner.Instance?.GetLastSpawnedNPC();
            if (npcObj == null)
            {
                MelonLogger.Warning("[DeliveryDriversMod] No spawned NPC available. Spawn one with F9 first");
                return;
            }
            var npc = npcObj.GetComponent<NPC>();
            if (npc == null)
            {
                MelonLogger.Error("[DeliveryDriversMod] Spawned object has no NPC component");
                return;
            }

            // Find destination
            var destination = FindDestinationParkingLot(vehicle.transform.position);
            if (destination == null)
            {
                MelonLogger.Error("[DeliveryDriversMod] No suitable ParkingLot found");
                return;
            }

            var playerPos = Player.Local.transform.position;
            var vehDist = Vector3.Distance(vehicle.transform.position, playerPos);
            var destDist = Vector3.Distance(destination.EntryPoint.position, vehicle.transform.position);

            MelonLogger.Msg("[DeliveryDriversMod] F10: Starting drive test");
            MelonLogger.Msg("[DeliveryDriversMod]   Vehicle: " + vehicle.name + " at " + vehicle.transform.position + " (distance: " + vehDist.ToString("F1") + "m)");
            MelonLogger.Msg("[DeliveryDriversMod]   NPC: " + npcObj.name);
            MelonLogger.Msg("[DeliveryDriversMod]   Destination: ParkingLot at " + destination.EntryPoint.position + " (distance: " + destDist.ToString("F1") + "m)");

            _npc = npc;
            _vehicle = vehicle;
            _destination = destination;
            // Cargo fields stay null — simple drive mode

            SetState(DriverState.WalkingToVehicle);
        }

        public void TriggerCargoTest()
        {
            if (IsRunning)
            {
                MelonLogger.Warning("[DeliveryDriversMod] Test already in progress");
                return;
            }

            if (!InstanceFinder.IsServer)
            {
                MelonLogger.Warning("[DeliveryDriversMod] Cannot start cargo test: not server");
                return;
            }

            if (Player.Local == null)
            {
                MelonLogger.Warning("[DeliveryDriversMod] Cannot start cargo test: Player.Local is null");
                return;
            }

            // Find vehicle
            var vehicle = FindNearestPlayerVehicle();
            if (vehicle == null)
            {
                MelonLogger.Warning("[DeliveryDriversMod] No player-owned vehicle within " + VEHICLE_SEARCH_RADIUS + "m");
                return;
            }

            if (vehicle.Storage == null)
            {
                MelonLogger.Error("[DeliveryDriversMod] Vehicle has no Storage component");
                return;
            }

            // Find NPC
            var npcObj = NPCSpawner.Instance?.GetLastSpawnedNPC();
            if (npcObj == null)
            {
                MelonLogger.Warning("[DeliveryDriversMod] No spawned NPC available. Spawn one with F9 first");
                return;
            }
            var npc = npcObj.GetComponent<NPC>();
            if (npc == null)
            {
                MelonLogger.Error("[DeliveryDriversMod] Spawned object has no NPC component");
                return;
            }

            // Find source + destination storage with nearby parking lots
            if (!FindCargoLocations(vehicle.transform.position, out StorageEntity srcStorage, out ParkingLot srcLot,
                    out StorageEntity dstStorage, out ParkingLot dstLot))
            {
                return; // Error already logged
            }

            // Populate source with test items
            if (!PopulateSourceStorage(srcStorage))
            {
                return; // Error already logged
            }

            // Log setup
            var playerPos = Player.Local.transform.position;
            var vehDist = Vector3.Distance(vehicle.transform.position, playerPos);
            var srcDist = Vector3.Distance(srcLot.EntryPoint.position, vehicle.transform.position);
            var dstDist = Vector3.Distance(dstLot.EntryPoint.position, srcLot.EntryPoint.position);

            MelonLogger.Msg("[DeliveryDriversMod] F11: Starting cargo transfer test");
            MelonLogger.Msg("[DeliveryDriversMod]   Vehicle: " + vehicle.name + " at " + vehicle.transform.position + " (distance: " + vehDist.ToString("F1") + "m)");
            MelonLogger.Msg("[DeliveryDriversMod]   NPC: " + npcObj.name);
            MelonLogger.Msg("[DeliveryDriversMod]   Source: " + srcStorage.name + " at " + srcStorage.transform.position + ", ParkingLot " + srcDist.ToString("F1") + "m away");
            MelonLogger.Msg("[DeliveryDriversMod]   Destination: " + dstStorage.name + " at " + dstStorage.transform.position + ", ParkingLot " + dstDist.ToString("F1") + "m from source lot");

            // Set references
            _npc = npc;
            _vehicle = vehicle;
            _sourceStorage = srcStorage;
            _destStorage = dstStorage;
            _sourceParkingLot = srcLot;
            _destParkingLot = dstLot;

            // First leg: drive to source
            _destination = _sourceParkingLot;
            _currentLeg = DeliveryLeg.Pickup;

            SetState(DriverState.WalkingToVehicle);
        }

        #endregion

        #region State Machine

        private void SetState(DriverState newState)
        {
            var oldState = _state;
            _state = newState;
            _stateTimer = 0f;

            MelonLogger.Msg("[DeliveryDriversMod] State: " + oldState + " → " + newState);

            switch (newState)
            {
                case DriverState.WalkingToVehicle:
                    EnterWalkingToVehicle();
                    break;
                case DriverState.EnteringVehicle:
                    EnterEnteringVehicle();
                    break;
                case DriverState.Driving:
                    EnterDriving();
                    break;
                case DriverState.Parking:
                    EnterParking();
                    break;
                case DriverState.LoadingCargo:
                    EnterLoadingCargo();
                    break;
                case DriverState.UnloadingCargo:
                    EnterUnloadingCargo();
                    break;
                case DriverState.ExitingVehicle:
                    EnterExitingVehicle();
                    break;
                case DriverState.Done:
                    EnterDone();
                    break;
            }
        }

        void Update()
        {
            if (!IsRunning) return;

            _stateTimer += Time.deltaTime;

            switch (_state)
            {
                case DriverState.WalkingToVehicle:
                    UpdateWalkingToVehicle();
                    break;
                case DriverState.EnteringVehicle:
                    UpdateEnteringVehicle();
                    break;
                case DriverState.Driving:
                    UpdateDriving();
                    break;
            }
        }

        #endregion

        #region WalkingToVehicle

        private void EnterWalkingToVehicle()
        {
            _walkTarget = _vehicle.driverEntryPoint.position;

            try
            {
                _npc.Movement.SetDestination(_walkTarget);
                MelonLogger.Msg("[DeliveryDriversMod] NPC walking to vehicle at " + _walkTarget);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[DeliveryDriversMod] SetDestination failed (" + ex.Message + "), will warp on timeout");
            }
        }

        private void UpdateWalkingToVehicle()
        {
            if (_npc == null || _vehicle == null)
            {
                MelonLogger.Error("[DeliveryDriversMod] NPC or vehicle destroyed during walk");
                SetState(DriverState.Done);
                return;
            }

            float dist = Vector3.Distance(_npc.transform.position, _walkTarget);

            if (dist < WALK_ARRIVE_DIST)
            {
                MelonLogger.Msg("[DeliveryDriversMod] NPC arrived at vehicle (walked in " + _stateTimer.ToString("F1") + "s)");
                SetState(DriverState.EnteringVehicle);
                return;
            }

            if (_stateTimer > WALK_TIMEOUT)
            {
                MelonLogger.Warning("[DeliveryDriversMod] Walk timeout (" + WALK_TIMEOUT + "s), warping NPC to vehicle");
                try
                {
                    _npc.Movement.Warp(_walkTarget);
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning("[DeliveryDriversMod] Warp failed (" + ex.Message + "), teleporting directly");
                    _npc.transform.position = _walkTarget;
                }
                SetState(DriverState.EnteringVehicle);
            }
        }

        #endregion

        #region EnteringVehicle

        private void EnterEnteringVehicle()
        {
            try
            {
                _npc.EnterVehicle(null, _vehicle);
                MelonLogger.Msg("[DeliveryDriversMod] NPC entering vehicle");
            }
            catch (Exception ex)
            {
                MelonLogger.Error("[DeliveryDriversMod] EnterVehicle failed: " + ex);
                SetState(DriverState.Done);
            }
        }

        private void UpdateEnteringVehicle()
        {
            // Wait a short delay for the enter to process, then start driving
            if (_stateTimer > ENTER_DELAY)
            {
                SetState(DriverState.Driving);
            }
        }

        #endregion

        #region Driving

        private void EnterDriving()
        {
            _navigationCallbackFired = false;

            try
            {
                // Unpark if needed
                if (_vehicle.isParked)
                {
                    MelonLogger.Msg("[DeliveryDriversMod] Vehicle is parked, unparking first...");
                    bool useExitPoint = _vehicle.CurrentParkingLot != null && _vehicle.CurrentParkingLot.UseExitPoint;
                    _vehicle.ExitPark_Networked(null, useExitPoint);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[DeliveryDriversMod] ExitPark failed (" + ex.Message + "), continuing anyway");
            }
        }

        private void UpdateDriving()
        {
            // If callback already fired, handle the result
            if (_navigationCallbackFired)
            {
                switch (_navigationResult)
                {
                    case VehicleAgent.ENavigationResult.Complete:
                        MelonLogger.Msg("[DeliveryDriversMod] Navigation complete, parking...");
                        SetState(DriverState.Parking);
                        break;
                    case VehicleAgent.ENavigationResult.Failed:
                        MelonLogger.Error("[DeliveryDriversMod] Navigation FAILED — aborting");
                        SetState(DriverState.Done);
                        break;
                    case VehicleAgent.ENavigationResult.Stopped:
                        MelonLogger.Warning("[DeliveryDriversMod] Navigation STOPPED — aborting");
                        SetState(DriverState.Done);
                        break;
                }
                return;
            }

            // Start navigation after a brief delay (allow unpark to settle)
            if (!_vehicle.Agent.AutoDriving && _stateTimer > UNPARK_DELAY)
            {
                MelonLogger.Msg("[DeliveryDriversMod] Starting navigation to " + _destination.EntryPoint.position);
                try
                {
                    _vehicle.Agent.Navigate(
                        _destination.EntryPoint.position,
                        null,
                        new VehicleAgent.NavigationCallback(OnNavigationComplete)
                    );
                }
                catch (Exception ex)
                {
                    MelonLogger.Error("[DeliveryDriversMod] Navigate() threw: " + ex);
                    SetState(DriverState.Done);
                }
            }
        }

        private void OnNavigationComplete(VehicleAgent.ENavigationResult result)
        {
            // Store result and handle in Update to avoid issues with callback context
            _navigationResult = result;
            _navigationCallbackFired = true;
        }

        #endregion

        #region Parking

        private void EnterParking()
        {
            try
            {
                int spotIndex = _destination.GetRandomFreeSpotIndex();
                if (spotIndex == -1)
                {
                    MelonLogger.Warning("[DeliveryDriversMod] No free parking spots, skipping park");
                    // Decide next state even without parking
                    TransitionAfterParking();
                    return;
                }

                EParkingAlignment alignment = _destination.ParkingSpots[spotIndex].Alignment;
                var parkData = new ParkData(_destination.GUID, spotIndex, alignment);

                _vehicle.Park(null, parkData, true);
                MelonLogger.Msg("[DeliveryDriversMod] Vehicle parked at spot " + spotIndex);

                TransitionAfterParking();
            }
            catch (Exception ex)
            {
                MelonLogger.Error("[DeliveryDriversMod] Park failed: " + ex);
                TransitionAfterParking();
            }
        }

        private void TransitionAfterParking()
        {
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
        }

        #endregion

        #region LoadingCargo

        private void EnterLoadingCargo()
        {
            MelonLogger.Msg("[DeliveryDriversMod] Loading cargo from source storage...");

            int count = TransferItems(_sourceStorage, _vehicle.Storage, "LOAD");

            if (count == 0)
            {
                MelonLogger.Warning("[DeliveryDriversMod] Source was empty — nothing to deliver");
            }

            // Switch to delivery leg
            _destination = _destParkingLot;
            _currentLeg = DeliveryLeg.Delivery;
            SetState(DriverState.Driving);
        }

        #endregion

        #region UnloadingCargo

        private void EnterUnloadingCargo()
        {
            MelonLogger.Msg("[DeliveryDriversMod] Unloading cargo to destination storage...");

            int count = TransferItems(_vehicle.Storage, _destStorage, "UNLOAD");

            if (count == 0)
            {
                MelonLogger.Warning("[DeliveryDriversMod] Vehicle was empty — nothing to unload");
            }

            SetState(DriverState.ExitingVehicle);
        }

        #endregion

        #region ExitingVehicle

        private void EnterExitingVehicle()
        {
            try
            {
                if (_npc != null && _npc.IsInVehicle)
                {
                    _npc.ExitVehicle();
                    MelonLogger.Msg("[DeliveryDriversMod] NPC exiting vehicle");
                }
                else
                {
                    MelonLogger.Warning("[DeliveryDriversMod] NPC not in vehicle, skipping exit");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error("[DeliveryDriversMod] ExitVehicle failed: " + ex);
            }

            SetState(DriverState.Done);
        }

        #endregion

        #region Done

        private void EnterDone()
        {
            var exitPos = _npc != null ? _npc.transform.position.ToString() : "unknown";

            if (_sourceStorage != null)
            {
                MelonLogger.Msg("[DeliveryDriversMod] Cargo test complete: NPC exited at " + exitPos);
            }
            else
            {
                MelonLogger.Msg("[DeliveryDriversMod] Drive test complete: NPC exited at " + exitPos);
            }

            // Clear all references
            _npc = null;
            _vehicle = null;
            _destination = null;
            _sourceStorage = null;
            _destStorage = null;
            _sourceParkingLot = null;
            _destParkingLot = null;
            _state = DriverState.Idle;
        }

        #endregion

        #region Cargo Transfer

        private int TransferItems(StorageEntity source, StorageEntity destination, string label)
        {
            int totalTransferred = 0;
            int occupiedSlots = 0;

            // Count source items before transfer
            foreach (var slot in source.ItemSlots)
            {
                if (slot.ItemInstance != null)
                    occupiedSlots++;
            }
            MelonLogger.Msg("[DeliveryDriversMod] " + label + ": source has " + occupiedSlots + " occupied slot(s)");

            // Transfer each occupied slot
            for (int i = 0; i < source.ItemSlots.Count; i++)
            {
                var slot = source.ItemSlots[i];
                if (slot.ItemInstance == null) continue;

                ItemInstance copy = slot.ItemInstance.GetCopy();
                int qty = slot.Quantity;

                slot.ClearStoredInstance();
                destination.InsertItem(copy, true);
                totalTransferred += qty;
            }

            MelonLogger.Msg("[DeliveryDriversMod] " + label + ": transferred " + totalTransferred + " item(s)");
            return totalTransferred;
        }

        private bool PopulateSourceStorage(StorageEntity source)
        {
            try
            {
                ItemDefinition def = Registry.GetItem(TEST_ITEM_ID);
                if (def == null)
                {
                    MelonLogger.Error("[DeliveryDriversMod] Registry.GetItem('" + TEST_ITEM_ID + "') returned null");
                    return false;
                }

                for (int i = 0; i < TEST_ITEM_COUNT; i++)
                {
                    ItemInstance instance = def.GetDefaultInstance(1);
                    source.InsertItem(instance, true);
                }

                MelonLogger.Msg("[DeliveryDriversMod] Populated source with " + TEST_ITEM_COUNT + " " + TEST_ITEM_ID);
                return true;
            }
            catch (Exception ex)
            {
                MelonLogger.Error("[DeliveryDriversMod] Failed to populate source storage: " + ex);
                return false;
            }
        }

        #endregion

        #region Selection Helpers

        private LandVehicle FindNearestPlayerVehicle()
        {
            var vehicleManager = NetworkSingleton<VehicleManager>.Instance;
            if (vehicleManager == null)
            {
                MelonLogger.Warning("[DeliveryDriversMod] VehicleManager not available");
                return null;
            }

            var playerPos = Player.Local.transform.position;
            return vehicleManager.PlayerOwnedVehicles
                .Where(v => v != null && Vector3.Distance(v.transform.position, playerPos) < VEHICLE_SEARCH_RADIUS)
                .OrderBy(v => Vector3.Distance(v.transform.position, playerPos))
                .FirstOrDefault();
        }

        private ParkingLot FindDestinationParkingLot(Vector3 vehiclePos)
        {
            var lots = FindObjectsOfType<ParkingLot>();
            if (lots == null || lots.Length == 0)
            {
                MelonLogger.Warning("[DeliveryDriversMod] No ParkingLots found in world");
                return null;
            }

            MelonLogger.Msg("[DeliveryDriversMod] Found " + lots.Length + " ParkingLot(s) in world");

            // Prefer a lot >30m away with free spots
            var candidate = lots
                .Where(l => l.EntryPoint != null && l.GetRandomFreeSpotIndex() != -1)
                .Where(l => Vector3.Distance(l.EntryPoint.position, vehiclePos) > MIN_DESTINATION_DIST)
                .OrderBy(l => Vector3.Distance(l.EntryPoint.position, vehiclePos))
                .FirstOrDefault();

            if (candidate != null) return candidate;

            // Fallback: any lot with free spots
            candidate = lots
                .Where(l => l.EntryPoint != null && l.GetRandomFreeSpotIndex() != -1)
                .OrderByDescending(l => Vector3.Distance(l.EntryPoint.position, vehiclePos))
                .FirstOrDefault();

            if (candidate != null)
            {
                MelonLogger.Warning("[DeliveryDriversMod] No lot >30m away, using closest available at " +
                    Vector3.Distance(candidate.EntryPoint.position, vehiclePos).ToString("F1") + "m");
            }

            return candidate;
        }

        private ParkingLot FindNearestParkingLotTo(Vector3 position, float maxDistance)
        {
            return FindObjectsOfType<ParkingLot>()
                .Where(l => l != null && l.EntryPoint != null && l.GetRandomFreeSpotIndex() != -1)
                .Where(l => Vector3.Distance(l.EntryPoint.position, position) < maxDistance)
                .OrderBy(l => Vector3.Distance(l.EntryPoint.position, position))
                .FirstOrDefault();
        }

        private bool FindCargoLocations(Vector3 vehiclePos,
            out StorageEntity srcStorage, out ParkingLot srcLot,
            out StorageEntity dstStorage, out ParkingLot dstLot)
        {
            srcStorage = null;
            srcLot = null;
            dstStorage = null;
            dstLot = null;

            var allStorage = WorldStorageEntity.All;
            MelonLogger.Msg("[DeliveryDriversMod] Found " + allStorage.Count + " WorldStorageEntity(s) in world");

            if (allStorage.Count == 0)
            {
                MelonLogger.Error("[DeliveryDriversMod] No WorldStorageEntities found — cannot run cargo test. " +
                    "Player needs at least one property with storage.");
                return false;
            }

            // Build candidates: storage entities paired with their nearest ParkingLot
            var candidates = new List<CargoCandidate>();
            foreach (var storage in allStorage)
            {
                if (storage == null || !storage.gameObject.activeInHierarchy) continue;

                var lot = FindNearestParkingLotTo(storage.transform.position, STORAGE_LOT_SEARCH_RADIUS);
                if (lot == null) continue;

                float lotDist = Vector3.Distance(lot.EntryPoint.position, storage.transform.position);
                candidates.Add(new CargoCandidate
                {
                    storage = storage,
                    parkingLot = lot,
                    lotDistance = lotDist
                });
            }

            candidates.Sort((a, b) => a.lotDistance.CompareTo(b.lotDistance));

            MelonLogger.Msg("[DeliveryDriversMod] " + candidates.Count + " storage(s) have a ParkingLot within " + STORAGE_LOT_SEARCH_RADIUS + "m");

            if (candidates.Count < 2)
            {
                MelonLogger.Error("[DeliveryDriversMod] Need at least 2 storage entities near ParkingLots, found " + candidates.Count);
                return false;
            }

            // Pick source: closest candidate to vehicle
            var source = candidates
                .OrderBy(c => Vector3.Distance(c.parkingLot.EntryPoint.position, vehiclePos))
                .First();

            // Pick destination: different ParkingLot, prefer >30m from source lot
            CargoCandidate dest = null;
            foreach (var c in candidates)
            {
                if (c.parkingLot == source.parkingLot) continue;
                float separation = Vector3.Distance(c.parkingLot.EntryPoint.position, source.parkingLot.EntryPoint.position);
                if (separation > MIN_DESTINATION_DIST)
                {
                    dest = c;
                    break;
                }
            }

            // Fallback: any candidate with a different parking lot
            if (dest == null)
            {
                dest = candidates.FirstOrDefault(c => c.parkingLot != source.parkingLot);
            }

            if (dest == null)
            {
                MelonLogger.Error("[DeliveryDriversMod] All storage candidates share the same ParkingLot — cannot run cargo test");
                return false;
            }

            srcStorage = source.storage;
            srcLot = source.parkingLot;
            dstStorage = dest.storage;
            dstLot = dest.parkingLot;
            return true;
        }

        private class CargoCandidate
        {
            public StorageEntity storage;
            public ParkingLot parkingLot;
            public float lotDistance;
        }

        #endregion
    }
}
