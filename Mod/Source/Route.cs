using System;
using System.Collections.Generic;
using ScheduleOne.Delivery;
using ScheduleOne.Map;

namespace DeliveryDriversMod
{
    public enum StopAction
    {
        Pickup,
        Dropoff
    }

    /// <summary>
    /// A single stop in a delivery route.
    /// References a dock by GUID string (serializable, no direct object reference).
    /// </summary>
    public class RouteStop
    {
        public string DockGUID { get; set; }
        public StopAction Action { get; set; }

        // Resolved at runtime, NOT serialized
        [NonSerialized] public LoadingDock ResolvedDock;
        [NonSerialized] public ParkingLot ResolvedParking;

        public RouteStop(string dockGuid, StopAction action)
        {
            DockGUID = dockGuid;
            Action = action;
        }
    }

    /// <summary>
    /// A reusable route definition: ordered list of stops.
    /// Serializable to JSON for M6 persistence.
    /// </summary>
    public class Route
    {
        public string Name { get; set; }
        public List<RouteStop> Stops { get; set; } = new List<RouteStop>();

        public Route(string name)
        {
            Name = name;
        }
    }

    /// <summary>
    /// Tracks a driver's progress through an assigned route.
    /// Separates "route definition" from "execution state."
    /// One driver holds at most one RouteAssignment at a time.
    /// </summary>
    public class RouteAssignment
    {
        public Route Route { get; }
        public int CurrentStopIndex { get; set; }
        public bool IsComplete => CurrentStopIndex >= Route.Stops.Count;

        public RouteStop CurrentStop => IsComplete ? null : Route.Stops[CurrentStopIndex];

        public RouteAssignment(Route route)
        {
            Route = route;
            CurrentStopIndex = 0;
        }

        public void AdvanceToNextStop()
        {
            CurrentStopIndex++;
        }
    }
}
