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
    /// A single stop in a delivery route — pure serializable data.
    /// References a dock by GUID string (no runtime object references).
    /// </summary>
    public class RouteStop
    {
        public string DockGUID { get; set; }
        public StopAction Action { get; set; }

        public RouteStop(string dockGuid, StopAction action)
        {
            DockGUID = dockGuid;
            Action = action;
        }
    }

    /// <summary>
    /// Runtime-resolved references for a single route stop.
    /// Populated when a RouteAssignment is created, not serialized.
    /// </summary>
    public struct ResolvedStop
    {
        public LoadingDock Dock;
        public ParkingLot Parking;
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

        /// <summary>
        /// Parallel list of resolved runtime references, indexed same as Route.Stops.
        /// Populated by ResolveRouteStops() after assignment creation.
        /// </summary>
        public List<ResolvedStop> ResolvedStops { get; } = new List<ResolvedStop>();
        public ResolvedStop CurrentResolvedStop => ResolvedStops[CurrentStopIndex];

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
