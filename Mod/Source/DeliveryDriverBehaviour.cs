using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FishNet;
using MelonLoader;
using ScheduleOne;
using ScheduleOne.Delivery;
using ScheduleOne.DevUtilities;
using ScheduleOne.ItemFramework;
using ScheduleOne.Map;
using ScheduleOne.Math;
using ScheduleOne.NPCs;
using ScheduleOne.ObjectScripts;
using ScheduleOne.PlayerScripts;
using ScheduleOne.Property;
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
            OccupyingDock,
            ReleasingDock,
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

        // Dock test fields (null when running F10 or F11 tests)
        private LoadingDock _sourceDock;
        private LoadingDock _destDock;

        // Route fields (null when running F10/F11/F12 tests)
        private RouteAssignment _routeAssignment;
        private float _routeStartTime;

        // GUID of the dock the vehicle was just released from; consumed once in EnterDriving
        // to teleport the vehicle to that dock's cached approach point, sidestepping the
        // graph dead-end Park() leaves us in on unpark.
        private Guid? _previousDockGUID;

        // State
        private DriverState _state = DriverState.Idle;
        private float _stateTimer;
        private Vector3 _walkTarget;
        private bool _navigationCallbackFired;
        private VehicleAgent.ENavigationResult _navigationResult;
        private float _navStartTime;
        private Vector3 _navStartPos;
        private Vector3 _navDestPos;

        // Mode-2 stuck-state sampler (Phase 4 triage)
        private const float STUCK_SAMPLE_INTERVAL = 2f;
        private const float STUCK_TELEPORT_JUMP_M = 5f; // a jump larger than this between samples implies engine recovery teleport
        private float _lastStuckSampleTime;
        private Vector3 _lastSamplePos;
        private bool _everStuckThisLeg;
        private bool _everReversedThisLeg;

        // Approach-point probe (M5.5 mode-1 fix)
        private enum ProbePhase { NotStarted, InFlight, Complete, Failed }
        private const int MAX_PROBE_CANDIDATES = 36;
        private const float PARK_GAP_WARN_M = 8f;
        private static readonly Dictionary<Guid, Vector3> _dockApproachCache = new Dictionary<Guid, Vector3>();
        private static FieldInfo _generalSeekerField;
        private static FieldInfo _roadSeekerField;
        private Vector3? _navOverridePoint;
        private ProbePhase _probePhase;
        private List<Vector3> _probeCandidates;
        private int _probeIndex;
        private bool _probeCalcCallbackFired;
        private NavigationUtility.ENavigationCalculationResult _probeCalcResult;

        // Mode-2 stuck watchdog + bounded recovery (M5.5 Phase 5)
        private const float WATCHDOG_WINDOW_S = 20f;
        private const float MIN_PROGRESS_M = 1.0f;
        private const float MIN_ODOMETER_M = 1.5f;
        private const int MAX_RECOVERY_ATTEMPTS = 3;
        private readonly List<float> _wTime = new List<float>();
        private readonly List<Vector3> _wPos = new List<Vector3>();
        private readonly List<float> _wDist = new List<float>();
        private int _recoveryAttempts;
        private bool _isRecovering;
        private bool _recoveryProbeActive;   // probe in flight is for recovery, not initial mode-1
        private int _recoveryProbeStartIndex; // candidate index the next recovery probe begins from

        // Departure smoothing: fast-pin detection + forward-nudge recovery (M5.5 Phase 6)
        private const float FASTPIN_ARM_S = 10f;
        private const float FASTPIN_WINDOW_S = 8f;
        private const float FASTPIN_ODOMETER_M = 0.35f; // hard-pin only; healthy anchor ~1.1m/8s
        private const float NUDGE_BASE_M = 18f;  // first nudge: hop this far along the remaining path
        private const float NUDGE_STEP_M = 18f;  // each retry hops this much further
        // Learned per-property road-side exit points; future departures teleport here
        // before Navigate instead of fighting the property's broken driveway graph.
        private static readonly Dictionary<string, Vector3> _propertyExitCache = new Dictionary<string, Vector3>();
        private bool _nudgeCalcActive;
        private PathSmoothingUtility.SmoothedPath _probeCalcPath;

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
                MelonLogger.Msg("Resetting stale driver state (" + _state + ")");
            }
            _npc = null;
            _vehicle = null;
            _destination = null;
            _sourceStorage = null;
            _destStorage = null;
            _sourceParkingLot = null;
            _destParkingLot = null;
            _sourceDock = null;
            _destDock = null;
            _routeAssignment = null;
            _previousDockGUID = null;
            _isRecovering = false;
            _recoveryProbeActive = false;
            _recoveryAttempts = 0;
            _recoveryProbeStartIndex = 0;
            _nudgeCalcActive = false;
            ResetWatchdogWindow();
            _state = DriverState.Idle;
        }

        #region Test Triggers

        public void TriggerDriveTest()
        {
            if (IsRunning)
            {
                MelonLogger.Warning("Drive test already in progress");
                return;
            }

            if (!InstanceFinder.IsServer)
            {
                MelonLogger.Warning("Cannot start drive test: not server");
                return;
            }

            if (Player.Local == null)
            {
                MelonLogger.Warning("Cannot start drive test: Player.Local is null");
                return;
            }

            // Find vehicle
            var vehicle = FindNearestPlayerVehicle();
            if (vehicle == null)
            {
                MelonLogger.Warning("No player-owned vehicle within " + VEHICLE_SEARCH_RADIUS + "m");
                return;
            }

            // Find NPC
            var npcObj = NPCSpawner.Instance?.GetLastSpawnedNPC();
            if (npcObj == null)
            {
                MelonLogger.Warning("No spawned NPC available. Spawn one with F9 first");
                return;
            }
            var npc = npcObj.GetComponent<NPC>();
            if (npc == null)
            {
                MelonLogger.Error("Spawned object has no NPC component");
                return;
            }

            // Find destination
            var destination = FindDestinationParkingLot(vehicle.transform.position);
            if (destination == null)
            {
                MelonLogger.Error("No suitable ParkingLot found");
                return;
            }

            var playerPos = Player.Local.transform.position;
            var vehDist = Vector3.Distance(vehicle.transform.position, playerPos);
            var destDist = Vector3.Distance(destination.EntryPoint.position, vehicle.transform.position);

            MelonLogger.Msg("F10: Starting drive test");
            MelonLogger.Msg("  Vehicle: " + vehicle.name + " at " + vehicle.transform.position + " (distance: " + vehDist.ToString("F1") + "m)");
            MelonLogger.Msg("  NPC: " + npcObj.name);
            MelonLogger.Msg("  Destination: ParkingLot at " + destination.EntryPoint.position + " (distance: " + destDist.ToString("F1") + "m)");

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
                MelonLogger.Warning("Test already in progress");
                return;
            }

            if (!InstanceFinder.IsServer)
            {
                MelonLogger.Warning("Cannot start cargo test: not server");
                return;
            }

            if (Player.Local == null)
            {
                MelonLogger.Warning("Cannot start cargo test: Player.Local is null");
                return;
            }

            // Find vehicle
            var vehicle = FindNearestPlayerVehicle();
            if (vehicle == null)
            {
                MelonLogger.Warning("No player-owned vehicle within " + VEHICLE_SEARCH_RADIUS + "m");
                return;
            }

            if (vehicle.Storage == null)
            {
                MelonLogger.Error("Vehicle has no Storage component");
                return;
            }

            // Find NPC
            var npcObj = NPCSpawner.Instance?.GetLastSpawnedNPC();
            if (npcObj == null)
            {
                MelonLogger.Warning("No spawned NPC available. Spawn one with F9 first");
                return;
            }
            var npc = npcObj.GetComponent<NPC>();
            if (npc == null)
            {
                MelonLogger.Error("Spawned object has no NPC component");
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

            MelonLogger.Msg("F11: Starting cargo transfer test");
            MelonLogger.Msg("  Vehicle: " + vehicle.name + " at " + vehicle.transform.position + " (distance: " + vehDist.ToString("F1") + "m)");
            MelonLogger.Msg("  NPC: " + npcObj.name);
            MelonLogger.Msg("  Source: " + srcStorage.name + " at " + srcStorage.transform.position + ", ParkingLot " + srcDist.ToString("F1") + "m away");
            MelonLogger.Msg("  Destination: " + dstStorage.name + " at " + dstStorage.transform.position + ", ParkingLot " + dstDist.ToString("F1") + "m from source lot");

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

        public void TriggerDockTest()
        {
            if (IsRunning)
            {
                MelonLogger.Warning("Test already in progress");
                return;
            }

            if (!InstanceFinder.IsServer)
            {
                MelonLogger.Warning("Cannot start dock test: not server");
                return;
            }

            if (Player.Local == null)
            {
                MelonLogger.Warning("Cannot start dock test: Player.Local is null");
                return;
            }

            // Find vehicle
            var vehicle = FindNearestPlayerVehicle();
            if (vehicle == null)
            {
                MelonLogger.Warning("No player-owned vehicle within " + VEHICLE_SEARCH_RADIUS + "m");
                return;
            }

            if (vehicle.Storage == null)
            {
                MelonLogger.Error("Vehicle has no Storage component");
                return;
            }

            // Find NPC
            var npcObj = NPCSpawner.Instance?.GetLastSpawnedNPC();
            if (npcObj == null)
            {
                MelonLogger.Warning("No spawned NPC available. Spawn one with F9 first");
                return;
            }
            var npc = npcObj.GetComponent<NPC>();
            if (npc == null)
            {
                MelonLogger.Error("Spawned object has no NPC component");
                return;
            }

            // Find two LoadingDock instances
            if (!FindDockLocations(vehicle.transform.position, out LoadingDock srcDock, out ParkingLot srcLot,
                    out LoadingDock dstDock, out ParkingLot dstLot))
            {
                return; // Error already logged
            }

            // Pre-populate vehicle with test items
            if (!PopulateSourceStorage(vehicle.Storage))
            {
                return; // Error already logged
            }

            // Log setup
            var playerPos = Player.Local.transform.position;
            var vehDist = Vector3.Distance(vehicle.transform.position, playerPos);
            var srcDist = Vector3.Distance(srcLot.EntryPoint.position, vehicle.transform.position);
            var dstDist = Vector3.Distance(dstLot.EntryPoint.position, srcLot.EntryPoint.position);

            MelonLogger.Msg("F12: Starting dock test");
            MelonLogger.Msg("  Vehicle: " + vehicle.name + " at " + vehicle.transform.position +
                " (distance: " + vehDist.ToString("F1") + "m)");
            MelonLogger.Msg("  NPC: " + npcObj.name);
            MelonLogger.Msg("  Source: " + srcDock.Name + " (" + srcDock.ParentProperty.PropertyName +
                "), parking entry at " + srcLot.EntryPoint.position +
                " (distance: " + srcDist.ToString("F1") + "m)");
            MelonLogger.Msg("  Dest: " + dstDock.Name + " (" + dstDock.ParentProperty.PropertyName +
                "), parking entry at " + dstLot.EntryPoint.position +
                " (distance: " + dstDist.ToString("F1") + "m from source)");

            // Set references
            _npc = npc;
            _vehicle = vehicle;
            _sourceDock = srcDock;
            _destDock = dstDock;
            _sourceParkingLot = srcLot;
            _destParkingLot = dstLot;

            // First leg: drive to source dock
            _destination = _sourceParkingLot;
            _currentLeg = DeliveryLeg.Pickup;

            SetState(DriverState.WalkingToVehicle);
        }

        public void TriggerRouteTest()
        {
            if (IsRunning)
            {
                MelonLogger.Warning("Test already in progress");
                return;
            }

            if (!InstanceFinder.IsServer)
            {
                MelonLogger.Warning("Cannot start route test: not server");
                return;
            }

            if (Player.Local == null)
            {
                MelonLogger.Warning("Cannot start route test: Player.Local is null");
                return;
            }

            // Find vehicle
            var vehicle = FindNearestPlayerVehicle();
            if (vehicle == null)
            {
                MelonLogger.Warning("No player-owned vehicle within " + VEHICLE_SEARCH_RADIUS + "m");
                return;
            }

            if (vehicle.Storage == null)
            {
                MelonLogger.Error("Vehicle has no Storage component");
                return;
            }

            // Find NPC
            var npcObj = NPCSpawner.Instance?.GetLastSpawnedNPC();
            if (npcObj == null)
            {
                MelonLogger.Warning("No spawned NPC available. Spawn one with F9 first");
                return;
            }
            var npc = npcObj.GetComponent<NPC>();
            if (npc == null)
            {
                MelonLogger.Error("Spawned object has no NPC component");
                return;
            }

            // Find all available docks
            var docks = FindAllAvailableDocks();
            MelonLogger.Msg("F6: Found " + docks.Count + " available docks across " +
                Property.OwnedProperties.Count + " owned properties");

            if (docks.Count < 3)
            {
                MelonLogger.Error("Need at least 3 LoadingDocks for route test. Found " + docks.Count +
                    ". Press F7 to grant ownership of properties with docks.");
                return;
            }

            // Log available docks
            for (int i = 0; i < docks.Count; i++)
            {
                MelonLogger.Msg("  Dock " + (i + 1) + ": " + docks[i].Name +
                    " (" + docks[i].ParentProperty.PropertyName + ")");
            }

            // Pick docks for 4-stop route:
            // Stop 1 (Pickup) & Stop 4 (Dropoff): dockA (closest to vehicle)
            // Stop 2 (Dropoff): dockB (different property preferred)
            // Stop 3 (Pickup): dockC (different from A and B)
            var vehiclePos = vehicle.transform.position;
            docks.Sort((a, b) =>
                Vector3.Distance(a.Parking.EntryPoint.position, vehiclePos)
                    .CompareTo(Vector3.Distance(b.Parking.EntryPoint.position, vehiclePos)));

            var dockA = docks[0];
            LoadingDock dockB = null;
            LoadingDock dockC = null;

            // dockB: prefer different property from A
            foreach (var d in docks)
            {
                if (d == dockA) continue;
                if (d.ParentProperty != dockA.ParentProperty)
                {
                    dockB = d;
                    break;
                }
            }
            // Fallback: any dock != A
            if (dockB == null)
                dockB = docks.First(d => d != dockA);

            // dockC: prefer a third distinct property so the route exercises
            // three different storage locations (same-property docks share storage).
            foreach (var d in docks)
            {
                if (d == dockA || d == dockB) continue;
                if (d.ParentProperty == dockA.ParentProperty) continue;
                if (d.ParentProperty == dockB.ParentProperty) continue;
                dockC = d;
                break;
            }
            // Fallback: any dock != A, B (only 2 distinct properties available)
            if (dockC == null)
            {
                foreach (var d in docks)
                {
                    if (d == dockA || d == dockB) continue;
                    dockC = d;
                    break;
                }
            }
            if (dockC == null)
            {
                MelonLogger.Error("Cannot find 3 distinct docks for route test");
                return;
            }

            // Build route
            var route = new Route("Test Round-Trip");
            route.Stops.Add(new RouteStop(dockA.GUID.ToString(), StopAction.Pickup));
            route.Stops.Add(new RouteStop(dockB.GUID.ToString(), StopAction.Dropoff));
            route.Stops.Add(new RouteStop(dockC.GUID.ToString(), StopAction.Pickup));
            route.Stops.Add(new RouteStop(dockA.GUID.ToString(), StopAction.Dropoff));

            // Resolve all stops
            var assignment = new RouteAssignment(route);
            if (!ResolveRouteStops(assignment)) return;

            // Pre-flight: verify WorldStorageEntity reachable at every stop
            bool preflightOk = true;
            var preflightErrors = new List<string>();
            for (int i = 0; i < assignment.ResolvedStops.Count; i++)
            {
                var resolved = assignment.ResolvedStops[i];
                var stopAction = route.Stops[i].Action;
                if (resolved.Storage == null)
                {
                    preflightOk = false;
                    preflightErrors.Add("  Stop " + (i + 1) + " (" + stopAction + " at " +
                        resolved.Dock.Name + ", " + resolved.Dock.ParentProperty.PropertyName +
                        "): no WorldStorageEntity within range");
                }
            }
            if (!preflightOk)
            {
                MelonLogger.Error("F6: Pre-flight check FAILED. Cannot start route test.");
                foreach (var err in preflightErrors)
                    MelonLogger.Error(err);
                MelonLogger.Error("Route test aborted.");
                return;
            }

            // Pre-populate pickup sources via the cached per-stop storage references
            // (stop 0 = dockA pickup, stop 2 = dockC pickup)
            MelonLogger.Msg("F6: Pre-populating pickup docks...");
            PopulateDockStorage(assignment.ResolvedStops[0].Storage, dockA, "cash", 5);
            PopulateDockStorage(assignment.ResolvedStops[2].Storage, dockC, "baggie", 5);

            // Log route summary
            var playerPos = Player.Local.transform.position;
            var vehDist = Vector3.Distance(vehicle.transform.position, playerPos);

            MelonLogger.Msg("F6: Starting route test '" + route.Name + "'");
            MelonLogger.Msg("  Vehicle: " + vehicle.name + " (distance: " + vehDist.ToString("F1") + "m)");
            MelonLogger.Msg("  NPC: " + npcObj.name);
            MelonLogger.Msg("  Route: " + route.Stops.Count + " stops");
            MelonLogger.Msg("    Stop 1: Pickup at " + dockA.Name + " (" + dockA.ParentProperty.PropertyName + ")");
            MelonLogger.Msg("    Stop 2: Dropoff at " + dockB.Name + " (" + dockB.ParentProperty.PropertyName + ")");
            MelonLogger.Msg("    Stop 3: Pickup at " + dockC.Name + " (" + dockC.ParentProperty.PropertyName + ")");
            MelonLogger.Msg("    Stop 4: Dropoff at " + dockA.Name + " (" + dockA.ParentProperty.PropertyName + ") [back to origin]");

            // Set references and start
            _npc = npc;
            _vehicle = vehicle;
            _routeAssignment = assignment;
            _routeStartTime = Time.time;
            _destination = assignment.CurrentResolvedStop.Parking;

            SetState(DriverState.WalkingToVehicle);
        }

        #endregion

        #region State Machine

        private void SetState(DriverState newState)
        {
            var oldState = _state;
            _state = newState;
            _stateTimer = 0f;

            MelonLogger.Msg("State: " + oldState + " → " + newState);

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
                case DriverState.OccupyingDock:
                    EnterOccupyingDock();
                    break;
                case DriverState.ReleasingDock:
                    EnterReleasingDock();
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
                MelonLogger.Msg("NPC walking to vehicle at " + _walkTarget);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("SetDestination failed (" + ex.Message + "), will warp on timeout");
            }
        }

        private void UpdateWalkingToVehicle()
        {
            if (_npc == null || _vehicle == null)
            {
                MelonLogger.Error("NPC or vehicle destroyed during walk");
                SetState(DriverState.Done);
                return;
            }

            float dist = Vector3.Distance(_npc.transform.position, _walkTarget);

            if (dist < WALK_ARRIVE_DIST)
            {
                MelonLogger.Msg("NPC arrived at vehicle (walked in " + _stateTimer.ToString("F1") + "s)");
                SetState(DriverState.EnteringVehicle);
                return;
            }

            if (_stateTimer > WALK_TIMEOUT)
            {
                MelonLogger.Warning("Walk timeout (" + WALK_TIMEOUT + "s), warping NPC to vehicle");
                try
                {
                    _npc.Movement.Warp(_walkTarget);
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning("Warp failed (" + ex.Message + "), teleporting directly");
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
                MelonLogger.Msg("NPC entering vehicle");
            }
            catch (Exception ex)
            {
                MelonLogger.Error("EnterVehicle failed: " + ex);
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
            _probePhase = ProbePhase.NotStarted;
            _probeCandidates = null;
            _probeIndex = 0;
            _probeCalcCallbackFired = false;
            _navOverridePoint = null;

            // Cache hit: skip the raw-entry attempt and head straight to the known-reachable point.
            var currentDock = _routeAssignment?.CurrentResolvedStop.Dock;
            if (currentDock != null && _dockApproachCache.TryGetValue(currentDock.GUID, out Vector3 cached))
            {
                _navOverridePoint = cached;
                MelonLogger.Msg("PROBE cache hit: dock=" + currentDock.Name +
                    " using cached approach=" + cached.ToString("F2"));
            }

            try
            {
                // Unpark if needed
                if (_vehicle.isParked)
                {
                    MelonLogger.Msg("Vehicle is parked, unparking first...");
                    bool useExitPoint = _vehicle.CurrentParkingLot != null && _vehicle.CurrentParkingLot.UseExitPoint;
                    _vehicle.ExitPark_Networked(null, useExitPoint);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("ExitPark failed (" + ex.Message + "), continuing anyway");
            }

            // Departure pre-emption: a prior outbound leg from this property pinned and
            // the nudge recovery learned a road-side exit point. Start there — skips the
            // property's broken driveway graph entirely (one stationary reposition at
            // departure instead of a visible mid-drive pin + teleport).
            bool exitTeleported = false;
            var departProp = FindPropertyAt(_vehicle.transform.position);
            if (departProp != null && _destination != null &&
                !departProp.DoBoundsContainPoint(_destination.EntryPoint.position) &&
                _propertyExitCache.TryGetValue(departProp.PropertyName, out Vector3 exitPoint))
            {
                Vector3 beforeExit = _vehicle.transform.position;
                _vehicle.transform.position = exitPoint + Vector3.up * 0.5f;
                if (_vehicle.Rb != null)
                {
                    _vehicle.Rb.velocity = Vector3.zero;
                    _vehicle.Rb.angularVelocity = Vector3.zero;
                }
                exitTeleported = true;
                MelonLogger.Msg("EXIT teleport: " + beforeExit.ToString("F2") +
                    " -> " + _vehicle.transform.position.ToString("F2") +
                    " (cached exit for " + departProp.PropertyName + ")");
            }

            // Outbound teleport: Park() snapped us to the dock's spot, which sits on a
            // graph dead-end. Return the vehicle to the cached approach point that the
            // inbound probe found navigable, then Navigate proceeds normally from there.
            if (!exitTeleported && _previousDockGUID.HasValue &&
                _dockApproachCache.TryGetValue(_previousDockGUID.Value, out Vector3 outboundStart))
            {
                Vector3 before = _vehicle.transform.position;
                _vehicle.transform.position = outboundStart + Vector3.up * 0.5f;
                if (_vehicle.Rb != null)
                {
                    _vehicle.Rb.velocity = Vector3.zero;
                    _vehicle.Rb.angularVelocity = Vector3.zero;
                }
                MelonLogger.Msg("OUTBOUND teleport: " + before.ToString("F2") +
                    " -> " + _vehicle.transform.position.ToString("F2") +
                    " (cached approach for previous dock)");
            }
            _previousDockGUID = null;
        }

        private void UpdateDriving()
        {
            // Mode-2 stuck sampler + watchdog: while the AI is actively driving,
            // sample stuck/reverse/graph state and evaluate the pin window.
            if (_vehicle != null && _vehicle.Agent != null && _vehicle.Agent.AutoDriving &&
                Time.time - _lastStuckSampleTime >= STUCK_SAMPLE_INTERVAL)
            {
                bool pinned = SampleStuckState();
                _lastStuckSampleTime = Time.time;
                if (pinned && !_isRecovering)
                {
                    BeginRecovery();
                    return;
                }
            }

            // If Navigate's callback already fired, handle the result
            if (_navigationCallbackFired)
            {
                float elapsed = Time.time - _navStartTime;
                switch (_navigationResult)
                {
                    case VehicleAgent.ENavigationResult.Complete:
                        MelonLogger.Msg("NAV end: result=Complete elapsed=" + elapsed.ToString("F2") +
                            "s everStuck=" + _everStuckThisLeg + " everReversed=" + _everReversedThisLeg);
                        if (_navOverridePoint.HasValue)
                        {
                            float gap = Vector3.Distance(_vehicle.transform.position, _destination.EntryPoint.position);
                            if (gap > PARK_GAP_WARN_M)
                            {
                                MelonLogger.Warning("PARK gap: vehicle is " + gap.ToString("F2") +
                                    "m from raw dock entry — Park() will teleport-snap to spot");
                            }
                        }
                        SetState(DriverState.Parking);
                        break;
                    case VehicleAgent.ENavigationResult.Failed:
                        MelonLogger.Error("NAV end: result=Failed elapsed=" + elapsed.ToString("F2") +
                            "s endPos=" + _vehicle.transform.position.ToString("F2") +
                            " [" + DescribeNavPoint(_vehicle.transform.position) + "]");
                        // Route mode + probe not yet attempted: ring-probe for an alternate approach point
                        if (_routeAssignment != null && _probePhase == ProbePhase.NotStarted)
                        {
                            StartProbe();
                            _navigationCallbackFired = false; // allow Navigate to fire again after probe
                            return;
                        }
                        SetState(DriverState.Done);
                        break;
                    case VehicleAgent.ENavigationResult.Stopped:
                        MelonLogger.Warning("NAV end: result=Stopped elapsed=" + elapsed.ToString("F2") + "s");
                        SetState(DriverState.Done);
                        break;
                }
                return;
            }

            // Nudge path calculation in flight: pick the hop point once resolved
            if (_nudgeCalcActive)
            {
                TickNudge();
                return;
            }

            // Probe in flight: tick it until reachability resolved
            if (_probePhase == ProbePhase.InFlight)
            {
                TickProbe();
                return;
            }
            if (_probePhase == ProbePhase.Failed)
            {
                SetState(DriverState.Done);
                return;
            }

            // Ready to issue Navigate (initial attempt, or post-probe with override point)
            if (!_vehicle.Agent.AutoDriving && _stateTimer > UNPARK_DELAY)
            {
                StartNavigation();
            }
        }

        private void StartNavigation()
        {
            Vector3 target = _navOverridePoint ?? _destination.EntryPoint.position;
            _navStartPos = _vehicle.transform.position;
            _navDestPos = target;
            _navStartTime = Time.time;
            string startProp = DescribeNavPoint(_navStartPos);
            string destProp = DescribeNavPoint(_navDestPos);
            float dist = Vector3.Distance(_navStartPos, _navDestPos);
            string legLabel = _routeAssignment != null
                ? ("route stop " + (_routeAssignment.CurrentStopIndex + 1) +
                    "/" + _routeAssignment.Route.Stops.Count + " " + _routeAssignment.CurrentStop.Action)
                : "non-route";
            MelonLogger.Msg("NAV begin (" + legLabel + "): start=" + _navStartPos.ToString("F2") +
                " [" + startProp + "] -> dest=" + _navDestPos.ToString("F2") +
                " [" + destProp + "] dist=" + dist.ToString("F1") + "m" +
                (_navOverridePoint.HasValue ? " (probe-resolved)" : ""));

            // Log DriveFlags config once per leg + reset the stuck sampler
            var flags = _vehicle.Agent.Flags;
            if (flags != null)
            {
                MelonLogger.Msg("  NAV flags: ObstacleMode=" + flags.ObstacleMode +
                    " StuckDetection=" + flags.StuckDetection +
                    " UseRoads=" + flags.UseRoads +
                    " SpeedLimitMult=" + flags.SpeedLimitMultiplier.ToString("F2"));
            }
            _lastStuckSampleTime = Time.time;
            _lastSamplePos = _navStartPos;
            _everStuckThisLeg = false;
            _everReversedThisLeg = false;

            // Fresh leg: reset the mode-2 watchdog + recovery state
            _recoveryAttempts = 0;
            _isRecovering = false;
            _recoveryProbeActive = false;
            _recoveryProbeStartIndex = 0;
            ResetWatchdogWindow();

            try
            {
                _vehicle.Agent.Navigate(
                    target,
                    null,
                    new VehicleAgent.NavigationCallback(OnNavigationComplete)
                );
            }
            catch (Exception ex)
            {
                MelonLogger.Error("Navigate() threw: " + ex);
                SetState(DriverState.Done);
            }
        }

        private void ResetWatchdogWindow()
        {
            _wTime.Clear();
            _wPos.Clear();
            _wDist.Clear();
        }

        // --- Mode-2 stuck sampler + watchdog (Phase 4 triage / Phase 5 fix) -

        // Returns true when the rolling window declares a pin (recovery should begin).
        private bool SampleStuckState()
        {
            var agent = _vehicle.Agent;
            Vector3 pos = _vehicle.transform.position;
            float moved = Vector3.Distance(pos, _lastSamplePos);
            bool stuck = false, reversing = false, onGraph = false;
            try
            {
                stuck = agent.GetIsStuck();
                reversing = agent.IsReversing;
                onGraph = agent.IsOnVehicleGraph();
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("STUCK sample failed: " + ex.Message);
                _lastSamplePos = pos;
                return false;
            }

            if (stuck) _everStuckThisLeg = true;
            if (reversing) _everReversedThisLeg = true;

            // Flag only movement inconsistent with reported speed — normal driving at
            // 22km/h covers ~12m per sample and is not a jump.
            string note = "";
            float speedExpectedM = Mathf.Abs(_vehicle.Speed_Kmh) / 3.6f * STUCK_SAMPLE_INTERVAL;
            if (moved > Mathf.Max(STUCK_TELEPORT_JUMP_M, speedExpectedM * 2f))
                note = " <-- JUMP " + moved.ToString("F1") + "m (likely engine recovery teleport)";

            MelonLogger.Msg("STUCK sample: speed=" + _vehicle.Speed_Kmh.ToString("F1") +
                "km/h moved=" + moved.ToString("F2") + "m stuck=" + stuck +
                " reversing=" + reversing + " onGraph=" + onGraph +
                " pos=" + pos.ToString("F1") + " [" + DescribeNavPoint(pos) + "]" + note);

            _lastSamplePos = pos;

            // --- Watchdog: push into the rolling window and evaluate ---
            float now = Time.time;
            float distToDest = Vector3.Distance(pos, _navDestPos);
            _wTime.Add(now);
            _wPos.Add(pos);
            _wDist.Add(distToDest);
            while (_wTime.Count > 1 && now - _wTime[0] > WATCHDOG_WINDOW_S)
            {
                _wTime.RemoveAt(0);
                _wPos.RemoveAt(0);
                _wDist.RemoveAt(0);
            }

            // Fast-pin: a hard pin (near-zero odometer over a short window) is
            // unambiguous well before the full window arms. Threshold sits ~3x below
            // the known-healthy crawl anchor so the lax full-window logic still
            // governs the gray zone; a false fire only costs a small forward nudge.
            if (now - _navStartTime >= FASTPIN_ARM_S)
            {
                float fastOdo = 0f, fastSpan = 0f;
                for (int i = _wPos.Count - 1; i > 0; i--)
                {
                    if (now - _wTime[i - 1] > FASTPIN_WINDOW_S) break;
                    fastOdo += Vector3.Distance(_wPos[i - 1], _wPos[i]);
                    fastSpan = now - _wTime[i - 1];
                }
                if (fastSpan >= FASTPIN_WINDOW_S - STUCK_SAMPLE_INTERVAL && fastOdo < FASTPIN_ODOMETER_M)
                {
                    MelonLogger.Msg("WATCHDOG: FAST-PIN odometer=" + fastOdo.ToString("F2") +
                        "m over " + fastSpan.ToString("F0") + "s — declaring pin early");
                    return true;
                }
            }

            // Arm only after a full window has elapsed since NAV begin (lets
            // unpark/accel settle and the buffer fill). Never abort a leg that
            // hasn't had a fair chance to make progress.
            bool armed = (now - _navStartTime) >= WATCHDOG_WINDOW_S && _wTime.Count >= 2;
            if (!armed)
            {
                MelonLogger.Msg("WATCHDOG: armed=False (warming up, " +
                    (now - _navStartTime).ToString("F0") + "s/" + WATCHDOG_WINDOW_S.ToString("F0") + "s)");
                return false;
            }

            float progress = _wDist[0] - distToDest; // positive = net closer to dest
            float odometer = 0f;
            for (int i = 1; i < _wPos.Count; i++)
                odometer += Vector3.Distance(_wPos[i - 1], _wPos[i]);
            bool pin = progress < MIN_PROGRESS_M && odometer < MIN_ODOMETER_M;
            MelonLogger.Msg("WATCHDOG: progress=" + progress.ToString("F2") + "m odometer=" +
                odometer.ToString("F2") + "m window=" + (now - _wTime[0]).ToString("F0") +
                "s armed=True pin=" + pin);
            return pin;
        }

        // --- Mode-2 bounded recovery (Phase 5) ------------------------------

        private void BeginRecovery()
        {
            var dock = _routeAssignment?.CurrentResolvedStop.Dock;
            string dockName = dock != null ? dock.Name : "?";
            _recoveryAttempts++;
            MelonLogger.Warning("MODE2 recovery: pin detected at " +
                _vehicle.transform.position.ToString("F2") + " near " + dockName +
                " — attempt " + _recoveryAttempts + "/" + MAX_RECOVERY_ATTEMPTS);

            if (_recoveryAttempts > MAX_RECOVERY_ATTEMPTS)
            {
                AbortLeg(dockName);
                return;
            }

            _isRecovering = true;
            try
            {
                if (_vehicle.Agent != null) _vehicle.Agent.StopNavigating();
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("StopNavigating threw: " + ex.Message);
            }
            _navigationCallbackFired = false; // consume the deliberate Stopped callback

            // Early attempts: forward-nudge — hop a short distance along the remaining
            // path (past the obstruction) and keep driving. Preserves the journey; the
            // player sees a small correction, not a vanish. Final attempt falls back to
            // the destination ring-probe (teleport to approach + park).
            if (_recoveryAttempts < MAX_RECOVERY_ATTEMPTS)
            {
                _nudgeCalcActive = true;
                _probeCalcCallbackFired = false;
                MelonLogger.Msg("MODE2 nudge: calculating path from pin to target " +
                    _navDestPos.ToString("F2"));
                IssueProbe(_navDestPos);
                return;
            }
            BeginDestinationRingProbe(dockName);
        }

        private void BeginDestinationRingProbe(string dockName)
        {
            // Ring-probe the destination dock, starting past the last-used recovery
            // point so each retry tries a genuinely different escape.
            Vector3 entry = _destination.EntryPoint.position;
            _probeCandidates = GenerateRingCandidates(entry);
            _probeIndex = Mathf.Clamp(_recoveryProbeStartIndex, 0, _probeCandidates.Count - 1);
            _probeCalcCallbackFired = false;
            _probePhase = ProbePhase.InFlight;
            _recoveryProbeActive = true;
            MelonLogger.Msg("MODE2 probe: dock=" + dockName + " entry=" + entry.ToString("F2") +
                " startIndex=" + _probeIndex + " candidates=" + _probeCandidates.Count);
            IssueProbe(_probeCandidates[_probeIndex]);
        }

        private void TickNudge()
        {
            if (!_probeCalcCallbackFired) return;
            _nudgeCalcActive = false;

            var dock = _routeAssignment?.CurrentResolvedStop.Dock;
            string dockName = dock != null ? dock.Name : "?";
            var path = _probeCalcPath;
            if (_probeCalcResult != NavigationUtility.ENavigationCalculationResult.Success ||
                path == null || path.vectorPath == null || path.vectorPath.Count < 2)
            {
                MelonLogger.Warning("MODE2 nudge: no path from pin to target — " +
                    "falling back to destination probe");
                BeginDestinationRingProbe(dockName);
                return;
            }

            float totalLen = 0f;
            for (int i = 1; i < path.vectorPath.Count; i++)
                totalLen += Vector3.Distance(path.vectorPath[i - 1], path.vectorPath[i]);

            float want = NUDGE_BASE_M + (_recoveryAttempts - 1) * NUDGE_STEP_M;
            float hop = Mathf.Min(want, totalLen - 2f);
            if (hop < 4f)
            {
                // Remaining path is basically the destination itself — nudging is
                // pointless, go straight to the approach-point fallback.
                MelonLogger.Msg("MODE2 nudge: remaining path only " + totalLen.ToString("F1") +
                    "m — falling back to destination probe");
                BeginDestinationRingProbe(dockName);
                return;
            }

            Vector3 pinPos = _vehicle.transform.position;
            Vector3 nudgePoint = PointAlongPath(path.vectorPath, hop);
            _vehicle.transform.position = nudgePoint + Vector3.up * 0.5f;
            if (_vehicle.Rb != null)
            {
                _vehicle.Rb.velocity = Vector3.zero;
                _vehicle.Rb.angularVelocity = Vector3.zero;
            }

            // Outbound leg leaving a property: the nudge point that cleared the wedge
            // is a good road-side exit — cache it so future departures pre-empt the pin.
            var pinProp = FindPropertyAt(pinPos);
            if (pinProp != null && !pinProp.DoBoundsContainPoint(_navDestPos))
            {
                _propertyExitCache[pinProp.PropertyName] = nudgePoint;
                MelonLogger.Msg("EXIT learned: property=" + pinProp.PropertyName +
                    " point=" + nudgePoint.ToString("F2"));
            }

            MelonLogger.Msg("MODE2 nudge: hopped " + hop.ToString("F1") + "m along path (of " +
                totalLen.ToString("F1") + "m remaining) to " + nudgePoint.ToString("F2") +
                " — resuming drive");

            // Resume the same leg with a fresh watchdog window; recovery attempt
            // count is preserved so repeated pins still escalate to the fallback.
            _navStartPos = _vehicle.transform.position;
            _navStartTime = Time.time;
            _lastStuckSampleTime = Time.time;
            _lastSamplePos = _navStartPos;
            ResetWatchdogWindow();
            _isRecovering = false;
            try
            {
                _vehicle.Agent.Navigate(
                    _navDestPos,
                    null,
                    new VehicleAgent.NavigationCallback(OnNavigationComplete)
                );
            }
            catch (Exception ex)
            {
                MelonLogger.Error("Navigate() threw after nudge: " + ex);
                SetState(DriverState.Done);
            }
        }

        private static Vector3 PointAlongPath(List<Vector3> points, float distance)
        {
            float walked = 0f;
            for (int i = 1; i < points.Count; i++)
            {
                float seg = Vector3.Distance(points[i - 1], points[i]);
                if (walked + seg >= distance && seg > 0f)
                    return Vector3.Lerp(points[i - 1], points[i], (distance - walked) / seg);
                walked += seg;
            }
            return points[points.Count - 1];
        }

        private void CompleteRecovery(Vector3 point)
        {
            var dock = _routeAssignment?.CurrentResolvedStop.Dock;
            if (dock != null) _dockApproachCache[dock.GUID] = point;
            // Teleport out of the wedge to the reachable approach point, then let
            // Park() snap into the dock spot (same as the mode-1 + Park flow).
            _vehicle.transform.position = point + Vector3.up * 0.5f;
            if (_vehicle.Rb != null)
            {
                _vehicle.Rb.velocity = Vector3.zero;
                _vehicle.Rb.angularVelocity = Vector3.zero;
            }
            _recoveryProbeStartIndex = _probeIndex + 1; // next retry skips this point
            _probePhase = ProbePhase.NotStarted;
            _recoveryProbeActive = false;
            _isRecovering = false;
            MelonLogger.Msg("MODE2 recovery success: dock=" + (dock != null ? dock.Name : "?") + " candidate " +
                (_probeIndex + 1) + "/" + _probeCandidates.Count + " at " + point.ToString("F2") +
                " — teleported out of wedge, parking");
            SetState(DriverState.Parking);
        }

        private void AbortLeg(string dockName)
        {
            MelonLogger.Error("MODE2 ABORT: driver could not reach dock " + dockName + " after " +
                MAX_RECOVERY_ATTEMPTS + " recovery attempts — aborting route");
            _isRecovering = false;
            _recoveryProbeActive = false;
            _probePhase = ProbePhase.NotStarted;
            try
            {
                if (_vehicle != null && _vehicle.Agent != null) _vehicle.Agent.StopNavigating();
            }
            catch (Exception) { }
            _navigationCallbackFired = false;
            SetState(DriverState.ExitingVehicle);
        }

        // --- Ring probe (mode-1 fix) ----------------------------------------

        private void StartProbe()
        {
            var dock = _routeAssignment.CurrentResolvedStop.Dock;
            Vector3 entry = _destination.EntryPoint.position;
            _probeCandidates = GenerateRingCandidates(entry);
            _probeIndex = 0;
            _probeCalcCallbackFired = false;
            _probePhase = ProbePhase.InFlight;
            MelonLogger.Msg("PROBE begin: dock=" + dock.Name + " entry=" + entry.ToString("F2") +
                " candidates=" + _probeCandidates.Count);
            IssueProbe(_probeCandidates[_probeIndex]);
        }

        private void TickProbe()
        {
            if (!_probeCalcCallbackFired) return;

            var dock = _routeAssignment?.CurrentResolvedStop.Dock;
            string dockName = dock != null ? dock.Name : "?";
            string tag = _recoveryProbeActive ? "MODE2 probe" : "PROBE";
            Vector3 current = _probeCandidates[_probeIndex];
            if (_probeCalcResult == NavigationUtility.ENavigationCalculationResult.Success)
            {
                if (_recoveryProbeActive)
                {
                    CompleteRecovery(current);
                }
                else
                {
                    _dockApproachCache[dock.GUID] = current;
                    _navOverridePoint = current;
                    _probePhase = ProbePhase.Complete;
                    MelonLogger.Msg("PROBE success: dock=" + dock.Name +
                        " candidate " + (_probeIndex + 1) + "/" + _probeCandidates.Count +
                        " at " + current.ToString("F2") + " (cached)");
                }
                return;
            }

            MelonLogger.Msg(tag + " try " + (_probeIndex + 1) + "/" + _probeCandidates.Count +
                " at " + current.ToString("F2") + " -> Failed");
            _probeIndex++;
            if (_probeIndex >= _probeCandidates.Count)
            {
                if (_recoveryProbeActive)
                {
                    MelonLogger.Error("MODE2 probe: no reachable point near dock " + dockName +
                        " from current position (tried " + _probeCandidates.Count + ")");
                    AbortLeg(dockName);
                }
                else
                {
                    MelonLogger.Error("PROBE FAILED: no reachable approach point near dock " + dock.Name +
                        " after " + _probeCandidates.Count + " candidates — aborting route");
                    _probePhase = ProbePhase.Failed;
                }
                return;
            }
            _probeCalcCallbackFired = false;
            IssueProbe(_probeCandidates[_probeIndex]);
        }

        private void IssueProbe(Vector3 candidate)
        {
            if (!ReflectSeekers(out object generalSeeker, out object roadSeeker))
            {
                MelonLogger.Error("PROBE: could not reflect VehicleAgent seekers");
                _probePhase = ProbePhase.Failed;
                return;
            }
            Vector3 start = _vehicle.transform.position;
            try
            {
                var calcMethod = typeof(NavigationUtility).GetMethod("CalculatePath",
                    BindingFlags.Public | BindingFlags.Static);
                if (calcMethod == null)
                {
                    MelonLogger.Error("PROBE: NavigationUtility.CalculatePath not found via reflection");
                    _probePhase = ProbePhase.Failed;
                    return;
                }
                var callback = new NavigationUtility.NavigationCalculationCallback(OnProbeCalcCallback);
                calcMethod.Invoke(null, new object[]
                {
                    start, candidate, new NavigationSettings(), _vehicle.Agent.Flags,
                    generalSeeker, roadSeeker, callback
                });
            }
            catch (Exception ex)
            {
                MelonLogger.Error("PROBE: CalculatePath invoke failed: " + ex);
                _probePhase = ProbePhase.Failed;
            }
        }

        private void OnProbeCalcCallback(NavigationUtility.ENavigationCalculationResult result,
            PathSmoothingUtility.SmoothedPath path)
        {
            _probeCalcResult = result;
            _probeCalcPath = path;
            _probeCalcCallbackFired = true;
        }

        private bool ReflectSeekers(out object general, out object road)
        {
            general = null; road = null;
            if (_vehicle == null || _vehicle.Agent == null) return false;
            if (_generalSeekerField == null || _roadSeekerField == null)
            {
                var t = typeof(VehicleAgent);
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                _generalSeekerField = t.GetField("generalSeeker", flags);
                _roadSeekerField = t.GetField("roadSeeker", flags);
            }
            if (_generalSeekerField == null || _roadSeekerField == null) return false;
            general = _generalSeekerField.GetValue(_vehicle.Agent);
            road = _roadSeekerField.GetValue(_vehicle.Agent);
            return general != null && road != null;
        }

        private static List<Vector3> GenerateRingCandidates(Vector3 entry)
        {
            // 1 raw entry + 12 + 12 + 11 = 36 candidates, ordered nearest-first.
            var list = new List<Vector3>(MAX_PROBE_CANDIDATES);
            list.Add(entry);
            float[] radii = { 6f, 12f, 24f };
            int[] anglesPerRadius = { 12, 12, 11 };
            for (int r = 0; r < radii.Length; r++)
            {
                int n = anglesPerRadius[r];
                for (int a = 0; a < n; a++)
                {
                    float deg = (360f / n) * a;
                    float rad = deg * Mathf.Deg2Rad;
                    list.Add(new Vector3(
                        entry.x + Mathf.Cos(rad) * radii[r],
                        entry.y,
                        entry.z + Mathf.Sin(rad) * radii[r]));
                }
            }
            return list;
        }

        private static Property FindPropertyAt(Vector3 pos)
        {
            foreach (var prop in Property.OwnedProperties)
            {
                if (prop != null && prop.DoBoundsContainPoint(pos))
                    return prop;
            }
            foreach (var prop in Property.UnownedProperties)
            {
                if (prop != null && prop.DoBoundsContainPoint(pos))
                    return prop;
            }
            return null;
        }

        private static string DescribeNavPoint(Vector3 pos)
        {
            foreach (var prop in Property.OwnedProperties)
            {
                if (prop != null && prop.DoBoundsContainPoint(pos))
                    return prop.PropertyName;
            }
            foreach (var prop in Property.UnownedProperties)
            {
                if (prop != null && prop.DoBoundsContainPoint(pos))
                    return prop.PropertyName + " (unowned)";
            }
            return "open";
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
                    MelonLogger.Warning("No free parking spots, skipping park");
                    // Decide next state even without parking
                    TransitionAfterParking();
                    return;
                }

                EParkingAlignment alignment = _destination.ParkingSpots[spotIndex].Alignment;
                var parkData = new ParkData(_destination.GUID, spotIndex, alignment);

                _vehicle.Park(null, parkData, true);
                MelonLogger.Msg("Vehicle parked at spot " + spotIndex);

                TransitionAfterParking();
            }
            catch (Exception ex)
            {
                MelonLogger.Error("Park failed: " + ex);
                TransitionAfterParking();
            }
        }

        private void TransitionAfterParking()
        {
            if (_routeAssignment != null)
            {
                // Route mode: always occupy dock first
                SetState(DriverState.OccupyingDock);
            }
            else if (_sourceDock != null)
            {
                // Dock test (F12) — occupy dock before cargo
                SetState(DriverState.OccupyingDock);
            }
            else if (_sourceStorage == null)
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
            if (_routeAssignment != null)
            {
                // Route mode: transfer items from nearby storage into vehicle
                var stop = _routeAssignment.CurrentStop;
                var dock = _routeAssignment.CurrentResolvedStop.Dock;
                int stopNum = _routeAssignment.CurrentStopIndex + 1;
                int stopTotal = _routeAssignment.Route.Stops.Count;

                MelonLogger.Msg("ROUTE PICKUP: Stop " + stopNum + "/" + stopTotal +
                    " at " + dock.Name + " (" + dock.ParentProperty.PropertyName + ")");
                MelonLogger.Msg("  Vehicle slots occupied: " + CountOccupiedSlots(_vehicle.Storage) +
                    "/" + _vehicle.Storage.ItemSlots.Count);

                // Check capacity
                if (CountOccupiedSlots(_vehicle.Storage) >= _vehicle.Storage.ItemSlots.Count)
                {
                    MelonLogger.Warning("ROUTE PICKUP: Vehicle full, skipping pickup at " + dock.Name);
                    SetState(DriverState.ReleasingDock);
                    return;
                }

                // Use storage resolved at route-resolution time (cached in ResolvedStop)
                var storage = _routeAssignment.CurrentResolvedStop.Storage;
                if (storage != null)
                {
                    int transferred = TransferItems(storage, _vehicle.Storage, "ROUTE_PICKUP");
                    MelonLogger.Msg("  Picked up " + transferred + " item(s) from " + storage.name);
                }
                else
                {
                    MelonLogger.Warning("ROUTE PICKUP: No nearby storage at " + dock.Name + " — nothing to pick up");
                }

                MelonLogger.Msg("  Vehicle slots after: " + CountOccupiedSlots(_vehicle.Storage) +
                    "/" + _vehicle.Storage.ItemSlots.Count);
                SetState(DriverState.ReleasingDock);
            }
            else if (_sourceDock != null)
            {
                // Dock mode: items are pre-populated in vehicle.
                MelonLogger.Msg("DOCK LOAD: Vehicle has " + CountOccupiedSlots(_vehicle.Storage) + " occupied slots");
                MelonLogger.Msg("DOCK LOAD: Dock OutputSlots count = " + _sourceDock.OutputSlots.Count);

                // No transfer needed — items are already in the vehicle
                SetState(DriverState.ReleasingDock);
            }
            else
            {
                // M3 cargo test mode: transfer from external storage
                MelonLogger.Msg("Loading cargo from source storage...");
                int count = TransferItems(_sourceStorage, _vehicle.Storage, "LOAD");
                if (count == 0)
                    MelonLogger.Warning("Source was empty — nothing to deliver");

                _destination = _destParkingLot;
                _currentLeg = DeliveryLeg.Delivery;
                SetState(DriverState.Driving);
            }
        }

        #endregion

        #region UnloadingCargo

        private void EnterUnloadingCargo()
        {
            if (_routeAssignment != null)
            {
                // Route mode: transfer items from vehicle to nearby storage
                var stop = _routeAssignment.CurrentStop;
                var dock = _routeAssignment.CurrentResolvedStop.Dock;
                int stopNum = _routeAssignment.CurrentStopIndex + 1;
                int stopTotal = _routeAssignment.Route.Stops.Count;

                MelonLogger.Msg("ROUTE DROPOFF: Stop " + stopNum + "/" + stopTotal +
                    " at " + dock.Name + " (" + dock.ParentProperty.PropertyName + ")");
                MelonLogger.Msg("  Vehicle slots occupied: " + CountOccupiedSlots(_vehicle.Storage) +
                    "/" + _vehicle.Storage.ItemSlots.Count);

                var storage = _routeAssignment.CurrentResolvedStop.Storage;
                if (storage != null)
                {
                    int transferred = TransferItems(_vehicle.Storage, storage, "ROUTE_DROPOFF");
                    MelonLogger.Msg("  Dropped off " + transferred + " item(s) to " + storage.name);
                }
                else
                {
                    MelonLogger.Warning("ROUTE DROPOFF: No nearby storage at " + dock.Name +
                        " — items remain in vehicle");
                }

                MelonLogger.Msg("  Vehicle slots after: " + CountOccupiedSlots(_vehicle.Storage) +
                    "/" + _vehicle.Storage.ItemSlots.Count);
                SetState(DriverState.ReleasingDock);
            }
            else if (_destDock != null)
            {
                // Dock mode: log dock state, transfer to nearby WorldStorageEntity if available
                MelonLogger.Msg("DOCK UNLOAD: Vehicle has " + CountOccupiedSlots(_vehicle.Storage) + " occupied slots");
                MelonLogger.Msg("DOCK UNLOAD: Dock OutputSlots count = " + _destDock.OutputSlots.Count);

                var nearbyStorage = FindNearestStorage(_destDock, STORAGE_LOT_SEARCH_RADIUS);
                if (nearbyStorage != null)
                {
                    MelonLogger.Msg("DOCK UNLOAD: Found nearby storage " + nearbyStorage.name + ", transferring...");
                    int count = TransferItems(_vehicle.Storage, nearbyStorage, "DOCK_UNLOAD");
                    MelonLogger.Msg("DOCK UNLOAD: Transferred " + count + " items to " + nearbyStorage.name);
                }
                else
                {
                    MelonLogger.Msg("DOCK UNLOAD: No nearby WorldStorageEntity — items remain in vehicle");
                }

                SetState(DriverState.ReleasingDock);
            }
            else
            {
                // M3 cargo test mode
                MelonLogger.Msg("Unloading cargo to destination storage...");
                int count = TransferItems(_vehicle.Storage, _destStorage, "UNLOAD");
                if (count == 0)
                    MelonLogger.Warning("Vehicle was empty — nothing to unload");

                SetState(DriverState.ExitingVehicle);
            }
        }

        #endregion

        #region OccupyingDock

        private void EnterOccupyingDock()
        {
            LoadingDock dock = GetCurrentDock();

            MelonLogger.Msg("Setting dock occupancy: " + dock.Name +
                " (property: " + dock.ParentProperty.PropertyName + ")");

            dock.SetStaticOccupant(_vehicle);

            MelonLogger.Msg("  StaticOccupant set: " + (dock.StaticOccupant != null));
            MelonLogger.Msg("  IsInUse: " + dock.IsInUse);
            MelonLogger.Msg("  Vehicle storage slots: " + _vehicle.Storage.ItemSlots.Count);

            // Transition to cargo transfer
            if (_routeAssignment != null)
            {
                if (_routeAssignment.CurrentStop.Action == StopAction.Pickup)
                    SetState(DriverState.LoadingCargo);
                else
                    SetState(DriverState.UnloadingCargo);
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

        #region ReleasingDock

        private void EnterReleasingDock()
        {
            LoadingDock dock = GetCurrentDock();

            MelonLogger.Msg("Releasing dock: " + dock.Name);

            _previousDockGUID = dock.GUID;
            dock.SetStaticOccupant(null);
            dock.VehicleDetector.Clear();

            MelonLogger.Msg("  StaticOccupant cleared: " + (dock.StaticOccupant == null));
            MelonLogger.Msg("  IsInUse: " + dock.IsInUse);

            if (_routeAssignment != null)
            {
                _routeAssignment.AdvanceToNextStop();

                if (_routeAssignment.IsComplete)
                {
                    float elapsed = Time.time - _routeStartTime;
                    MelonLogger.Msg("Route '" + _routeAssignment.Route.Name + "' complete — " +
                        _routeAssignment.Route.Stops.Count + " stops in " + elapsed.ToString("F1") + "s");
                    SetState(DriverState.ExitingVehicle);
                }
                else
                {
                    var nextStop = _routeAssignment.CurrentStop;
                    var nextResolved = _routeAssignment.CurrentResolvedStop;
                    _destination = nextResolved.Parking;
                    MelonLogger.Msg("Advancing to stop " + (_routeAssignment.CurrentStopIndex + 1) +
                        "/" + _routeAssignment.Route.Stops.Count +
                        " (" + nextStop.Action + " at " + nextResolved.Dock.Name + ")");
                    SetState(DriverState.Driving);
                }
            }
            else if (_currentLeg == DeliveryLeg.Pickup)
            {
                // Switch to delivery leg
                _destination = _destParkingLot;
                _currentLeg = DeliveryLeg.Delivery;
                SetState(DriverState.Driving);
            }
            else
            {
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
                    MelonLogger.Msg("NPC exiting vehicle");
                }
                else
                {
                    MelonLogger.Warning("NPC not in vehicle, skipping exit");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error("ExitVehicle failed: " + ex);
            }

            SetState(DriverState.Done);
        }

        #endregion

        #region Done

        private void EnterDone()
        {
            var exitPos = _npc != null ? _npc.transform.position.ToString() : "unknown";

            if (_routeAssignment != null)
            {
                MelonLogger.Msg("Route test complete: NPC exited at " + exitPos);
            }
            else if (_sourceDock != null)
            {
                MelonLogger.Msg("Dock test complete: NPC exited at " + exitPos);
            }
            else if (_sourceStorage != null)
            {
                MelonLogger.Msg("Cargo test complete: NPC exited at " + exitPos);
            }
            else
            {
                MelonLogger.Msg("Drive test complete: NPC exited at " + exitPos);
            }

            // Clear all references
            _npc = null;
            _vehicle = null;
            _destination = null;
            _sourceStorage = null;
            _destStorage = null;
            _sourceParkingLot = null;
            _destParkingLot = null;
            _sourceDock = null;
            _destDock = null;
            _routeAssignment = null;
            _previousDockGUID = null;
            _isRecovering = false;
            _recoveryProbeActive = false;
            _recoveryAttempts = 0;
            _recoveryProbeStartIndex = 0;
            _nudgeCalcActive = false;
            ResetWatchdogWindow();
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
            MelonLogger.Msg("" + label + ": source has " + occupiedSlots + " occupied slot(s)");

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

            MelonLogger.Msg("" + label + ": transferred " + totalTransferred + " item(s)");
            return totalTransferred;
        }

        private bool PopulateSourceStorage(StorageEntity source)
        {
            try
            {
                ItemDefinition def = Registry.GetItem(TEST_ITEM_ID);
                if (def == null)
                {
                    MelonLogger.Error("Registry.GetItem('" + TEST_ITEM_ID + "') returned null");
                    return false;
                }

                for (int i = 0; i < TEST_ITEM_COUNT; i++)
                {
                    ItemInstance instance = def.GetDefaultInstance(1);
                    source.InsertItem(instance, true);
                }

                MelonLogger.Msg("Populated source with " + TEST_ITEM_COUNT + " " + TEST_ITEM_ID);
                return true;
            }
            catch (Exception ex)
            {
                MelonLogger.Error("Failed to populate source storage: " + ex);
                return false;
            }
        }

        private int CountOccupiedSlots(StorageEntity storage)
        {
            int count = 0;
            foreach (var slot in storage.ItemSlots)
                if (slot.ItemInstance != null) count++;
            return count;
        }

        private string DescribeStorageLookup(string label, LoadingDock dock, StorageEntity storage)
        {
            var dockPos = dock.transform.position;
            if (storage == null)
                return label + ": dock=" + dock.Name + " (" + dock.ParentProperty.PropertyName +
                    ") dockPos=" + dockPos.ToString("F2") + " -> storage=NULL";
            var sPos = storage.transform.position;
            float dist = Vector3.Distance(dockPos, sPos);
            return label + ": dock=" + dock.Name + " (" + dock.ParentProperty.PropertyName +
                ") dockPos=" + dockPos.ToString("F2") +
                " -> storage='" + storage.name + "'#" + storage.GetInstanceID() +
                " sPos=" + sPos.ToString("F2") +
                " dist=" + dist.ToString("F2") + "m occupied=" +
                CountOccupiedSlots(storage) + "/" + storage.ItemSlots.Count;
        }

        private StorageEntity FindNearestStorage(LoadingDock dock, float maxDistance)
        {
            // Scope candidates to storages whose world position lies inside the dock's
            // parent property bounds. The game has no formal dock<->storage binding,
            // so property containment is the strongest association we can derive.
            var pos = dock.transform.position;
            var prop = dock.ParentProperty;
            StorageEntity best = null;
            float bestDist = maxDistance;

            foreach (var s in WorldStorageEntity.All)
            {
                if (s == null || !s.gameObject.activeInHierarchy) continue;
                if (!prop.DoBoundsContainPoint(s.transform.position)) continue;
                float dist = Vector3.Distance(s.transform.position, pos);
                if (dist < bestDist) { best = s; bestDist = dist; }
            }

            foreach (var p in FindObjectsOfType<PlaceableStorageEntity>())
            {
                if (p == null || !p.gameObject.activeInHierarchy || p.StorageEntity == null) continue;
                if (!prop.DoBoundsContainPoint(p.transform.position)) continue;
                float dist = Vector3.Distance(p.transform.position, pos);
                if (dist < bestDist) { best = p.StorageEntity; bestDist = dist; }
            }

            return best;
        }

        #endregion

        #region Selection Helpers

        private LandVehicle FindNearestPlayerVehicle()
        {
            var vehicleManager = NetworkSingleton<VehicleManager>.Instance;
            if (vehicleManager == null)
            {
                MelonLogger.Warning("VehicleManager not available");
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
                MelonLogger.Warning("No ParkingLots found in world");
                return null;
            }

            MelonLogger.Msg("Found " + lots.Length + " ParkingLot(s) in world");

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
                MelonLogger.Warning("No lot >30m away, using closest available at " +
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
            MelonLogger.Msg("Found " + allStorage.Count + " WorldStorageEntity(s) in world");

            if (allStorage.Count == 0)
            {
                MelonLogger.Error("No WorldStorageEntities found — cannot run cargo test. " +
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

            MelonLogger.Msg("" + candidates.Count + " storage(s) have a ParkingLot within " + STORAGE_LOT_SEARCH_RADIUS + "m");

            if (candidates.Count < 2)
            {
                MelonLogger.Error("Need at least 2 storage entities near ParkingLots, found " + candidates.Count);
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
                MelonLogger.Error("All storage candidates share the same ParkingLot — cannot run cargo test");
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

        private bool FindDockLocations(Vector3 vehiclePos,
            out LoadingDock srcDock, out ParkingLot srcLot,
            out LoadingDock dstDock, out ParkingLot dstLot)
        {
            srcDock = null; srcLot = null; dstDock = null; dstLot = null;

            // Enumerate docks from owned properties only
            var docks = new List<LoadingDock>();
            foreach (var prop in Property.OwnedProperties)
            {
                if (prop?.LoadingDocks == null) continue;
                foreach (var dock in prop.LoadingDocks)
                {
                    if (dock?.Parking?.EntryPoint != null)
                        docks.Add(dock);
                }
            }

            MelonLogger.Msg("Found " + docks.Count + " LoadingDock(s) across " +
                Property.OwnedProperties.Count + " owned properties");

            if (docks.Count < 2)
            {
                MelonLogger.Error("Need at least 2 LoadingDocks on owned properties. Found " + docks.Count +
                    ". Press F7 to grant ownership of properties with docks.");
                return false;
            }

            // Log all found docks for debugging
            foreach (var d in docks)
            {
                MelonLogger.Msg("  Dock: " + d.Name + " at " + d.ParentProperty.PropertyName +
                    ", parking entry: " + d.Parking.EntryPoint.position +
                    ", inUse: " + d.IsInUse);
            }

            // Pick source: closest property to vehicle, then first free dock at that property
            var sourceProperty = Property.OwnedProperties
                .Where(p => p?.LoadingDocks != null && p.LoadingDocks.Any(d => d != null && d.Parking?.EntryPoint != null))
                .OrderBy(p => p.LoadingDocks
                    .Where(d => d?.Parking?.EntryPoint != null)
                    .Min(d => Vector3.Distance(d.Parking.EntryPoint.position, vehiclePos)))
                .First();

            var source = sourceProperty.LoadingDocks
                .Where(d => d?.Parking?.EntryPoint != null)
                .FirstOrDefault(d => !d.IsInUse)
                ?? sourceProperty.LoadingDocks.First(d => d?.Parking?.EntryPoint != null);

            // Pick dest: first free dock at a different property
            var destProperty = Property.OwnedProperties
                .Where(p => p != null && p != sourceProperty && p.LoadingDocks != null
                    && p.LoadingDocks.Any(d => d != null && d.Parking?.EntryPoint != null))
                .FirstOrDefault();

            // Fallback: different dock on same property
            LoadingDock dest = null;
            if (destProperty != null)
            {
                dest = destProperty.LoadingDocks
                    .Where(d => d?.Parking?.EntryPoint != null)
                    .FirstOrDefault(d => !d.IsInUse)
                    ?? destProperty.LoadingDocks.First(d => d?.Parking?.EntryPoint != null);
            }
            else
            {
                dest = sourceProperty.LoadingDocks
                    .Where(d => d != null && d != source && d.Parking?.EntryPoint != null)
                    .FirstOrDefault(d => !d.IsInUse)
                    ?? sourceProperty.LoadingDocks
                        .FirstOrDefault(d => d != null && d != source && d.Parking?.EntryPoint != null);
            }

            if (dest == null)
            {
                MelonLogger.Error("Could not find a second dock different from source");
                return false;
            }

            // Warn if either dock is currently in use
            if (source.IsInUse)
                MelonLogger.Warning("Source dock is currently in use — may conflict with vanilla delivery");
            if (dest.IsInUse)
                MelonLogger.Warning("Dest dock is currently in use — may conflict with vanilla delivery");

            srcDock = source;
            srcLot = source.Parking;
            dstDock = dest;
            dstLot = dest.Parking;
            return true;
        }

        #endregion

        #region Route Helpers

        private bool ResolveRouteStops(RouteAssignment assignment)
        {
            assignment.ResolvedStops.Clear();
            for (int i = 0; i < assignment.Route.Stops.Count; i++)
            {
                var stop = assignment.Route.Stops[i];
                var dock = GUIDManager.GetObject<LoadingDock>(new Guid(stop.DockGUID));
                if (dock == null)
                {
                    MelonLogger.Error("Route stop " + (i + 1) + " references unknown dock GUID: " + stop.DockGUID);
                    return false;
                }
                if (dock.Parking == null)
                {
                    MelonLogger.Error("Dock '" + dock.Name + "' has no Parking assigned");
                    return false;
                }
                var storage = FindNearestStorage(dock, STORAGE_LOT_SEARCH_RADIUS);
                assignment.ResolvedStops.Add(new ResolvedStop {
                    Dock = dock, Parking = dock.Parking, Storage = storage
                });
            }
            return true;
        }

        private LoadingDock GetCurrentDock()
        {
            if (_routeAssignment != null)
                return _routeAssignment.CurrentResolvedStop.Dock;
            return (_currentLeg == DeliveryLeg.Pickup) ? _sourceDock : _destDock;
        }

        private List<LoadingDock> FindAllAvailableDocks()
        {
            var docks = new List<LoadingDock>();
            foreach (var prop in Property.OwnedProperties)
            {
                if (prop?.LoadingDocks == null) continue;
                foreach (var dock in prop.LoadingDocks)
                {
                    if (dock?.Parking?.EntryPoint != null)
                        docks.Add(dock);
                }
            }
            return docks;
        }

        private bool PopulateDockStorage(StorageEntity storage, LoadingDock dock, string itemId, int count)
        {
            if (storage == null)
            {
                MelonLogger.Warning("No WorldStorageEntity near dock '" + dock.Name +
                    "' — cannot pre-populate for pickup");
                return false;
            }

            try
            {
                ItemDefinition def = Registry.GetItem(itemId);
                if (def == null)
                {
                    MelonLogger.Error("Registry.GetItem('" + itemId + "') returned null");
                    return false;
                }

                for (int i = 0; i < count; i++)
                {
                    ItemInstance instance = def.GetDefaultInstance(1);
                    storage.InsertItem(instance, true);
                }

                MelonLogger.Msg("  Pre-populated " + count + "x " + itemId + " in " +
                    storage.name + " (near " + dock.Name + ")");
                return true;
            }
            catch (Exception ex)
            {
                MelonLogger.Error("Failed to populate dock storage: " + ex);
                return false;
            }
        }

        #endregion
    }
}

