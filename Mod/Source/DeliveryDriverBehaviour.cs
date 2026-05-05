using System;
using System.Linq;
using FishNet;
using MelonLoader;
using ScheduleOne.DevUtilities;
using ScheduleOne.Map;
using ScheduleOne.NPCs;
using ScheduleOne.PlayerScripts;
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
            ExitingVehicle,
            Done
        }

        private NPC _npc;
        private LandVehicle _vehicle;
        private ParkingLot _destination;

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

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;
        }

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

            SetState(DriverState.WalkingToVehicle);
        }

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
                        MelonLogger.Error("[DeliveryDriversMod] Navigation FAILED — aborting drive test");
                        SetState(DriverState.Done);
                        break;
                    case VehicleAgent.ENavigationResult.Stopped:
                        MelonLogger.Warning("[DeliveryDriversMod] Navigation STOPPED — aborting drive test");
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
                    SetState(DriverState.ExitingVehicle);
                    return;
                }

                EParkingAlignment alignment = _destination.ParkingSpots[spotIndex].Alignment;
                var parkData = new ParkData(_destination.GUID, spotIndex, alignment);

                _vehicle.Park(null, parkData, true);
                MelonLogger.Msg("[DeliveryDriversMod] Vehicle parked at spot " + spotIndex);

                SetState(DriverState.ExitingVehicle);
            }
            catch (Exception ex)
            {
                MelonLogger.Error("[DeliveryDriversMod] Park failed: " + ex);
                SetState(DriverState.ExitingVehicle);
            }
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
            MelonLogger.Msg("[DeliveryDriversMod] Drive test complete: NPC exited at " + exitPos);

            // Clear references
            _npc = null;
            _vehicle = null;
            _destination = null;
            _state = DriverState.Idle;
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

        #endregion
    }
}
