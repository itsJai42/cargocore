using System;
using System.Collections.Generic;
using System.Globalization;

namespace CargoCore
{
    public sealed class DeliveryRoute
    {
        public float PickupX, PickupY, PickupZ, DropoffX, DropoffY, DropoffZ;
        public float StartRadius, PickupRadius, ArrivalRadius, DeliveryRadius, InteractionRadius, MinTrailerDistance, MaxSpeed;
        public static DeliveryRoute Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new FormatException("CargoCore route configuration is missing or empty");
            var values = new Dictionary<string, string>();
            foreach (string line in text.Split('\n'))
            {
                string s = line.Trim();
                if (s.Length == 0 || s.StartsWith("#"))
                    continue;
                var p = s.Split(new[] { '=' }, 2);
                if (p.Length != 2 || values.ContainsKey(p[0].Trim()))
                    throw new FormatException("Invalid route setting: " + s);
                values.Add(p[0].Trim(), p[1].Trim());
            }
            Func<string, float> f = k =>
            {
                float n = float.Parse(values[k], CultureInfo.InvariantCulture);
                if (!Rules.Finite(n))
                    throw new FormatException("Nonfinite route setting");
                return n;
            };
            var r = new DeliveryRoute
            {
                PickupX = f("pickupX"),
                PickupY = f("pickupY"),
                PickupZ = f("pickupZ"),
                DropoffX = f("dropoffX"),
                DropoffY = f("dropoffY"),
                DropoffZ = f("dropoffZ"),
                StartRadius = f("startRadius"),
                PickupRadius = f("pickupRadius"),
                ArrivalRadius = f("arrivalRadius"),
                DeliveryRadius = f("deliveryRadius"),
                InteractionRadius = f("interactionRadius"),
                MinTrailerDistance = f("minTrailerDistance"),
                MaxSpeed = f("maxSpeed")
            };
            foreach (float n in new[] { r.StartRadius, r.PickupRadius, r.ArrivalRadius, r.DeliveryRadius, r.InteractionRadius, r.MinTrailerDistance, r.MaxSpeed })
                if (n <= 0)
                    throw new FormatException("Route limits must be positive");
            if (r.StartRadius > r.PickupRadius || r.DeliveryRadius > r.ArrivalRadius)
                throw new FormatException("Invalid route radii");
            if (Distance(r.PickupX, r.PickupY, r.PickupZ, r.DropoffX, r.DropoffY, r.DropoffZ) < r.PickupRadius + r.ArrivalRadius)
                throw new FormatException("Pickup and drop-off zones must not overlap");
            return r;
        }

        public static float Distance(float x, float y, float z, float tx, float ty, float tz)
        {
            return (float)Math.Sqrt((x - tx) * (x - tx) + (y - ty) * (y - ty) + (z - tz) * (z - tz));
        }
    }
    public enum ContractPhase
    {
        AwaitingLoad, InTransit, AwaitingUnload, Delivered, Cancelled
    }
    public sealed class DeliveryEvidence
    {
        public bool Authorized, SameBucket, EntitiesExist, CargoAlive, Released;
        public float CargoDistance, PlayerDistance, CargoSpeed, TrailerSpeed, TruckSpeed, TrailerDistance;
    }
    public sealed class TransportContract
    {
        public int Id, Owner, Cargo, Trailer;
        public ContractPhase Phase { get; private set; } = ContractPhase.AwaitingLoad;
        public bool Terminal
        {
            get
            {
                return Phase == ContractPhase.Delivered || Phase == ContractPhase.Cancelled;
            }
        }

        public bool ConfirmPickup(bool correctAssignment, bool secured, bool atPickup)
        {
            if (Phase != ContractPhase.AwaitingLoad || !correctAssignment || !secured || !atPickup)
                return false;
            Phase = ContractPhase.InTransit;
            return true;
        }

        public bool ObserveArrival(bool correctAssignment, bool secured, bool atDestination)
        {
            if (Phase != ContractPhase.InTransit || !correctAssignment || !secured || !atDestination)
                return false;
            Phase = ContractPhase.AwaitingUnload;
            return true;
        }

        public string Deliver(DeliveryRoute route, DeliveryEvidence e)
        {
            string error = DeliveryProblem(route, e);
            if (error != null)
                return error;
            Phase = ContractPhase.Delivered;
            return null;
        }

        public string DeliveryProblem(DeliveryRoute route, DeliveryEvidence e)
        {
            if (Phase == ContractPhase.Delivered)
                return "This delivery is already complete";
            if (Phase != ContractPhase.AwaitingUnload)
                return "Haul the secured car to the destination before unloading";
            if (!e.Authorized || !e.SameBucket)
                return "You are not authorized for this delivery";
            if (!e.EntitiesExist || !e.CargoAlive)
                return "The assigned vehicle or transport rig is unavailable";
            foreach (float n in new[] { e.CargoDistance, e.PlayerDistance, e.CargoSpeed, e.TrailerSpeed, e.TruckSpeed, e.TrailerDistance })
                if (!Rules.Finite(n) || n < 0)
                    return "Delivery position is unavailable";
            if (!e.Released)
                return "Release the assigned car and wait for confirmation";
            if (e.CargoDistance > route.DeliveryRadius)
                return "Park the assigned car inside the drop-off marker";
            if (e.PlayerDistance > route.InteractionRadius)
                return "Approach the assigned car to complete delivery";
            if (Math.Max(e.CargoSpeed, Math.Max(e.TrailerSpeed, e.TruckSpeed)) > route.MaxSpeed)
                return "Stop the car and transport rig before completing delivery";
            if (e.TrailerDistance < route.MinTrailerDistance)
                return "Drive the car fully off the trailer; park the rig farther from the drop-off bay";
            return null;
        }

        public bool Cancel()
        {
            if (Terminal)
                return false;
            Phase = ContractPhase.Cancelled;
            return true;
        }
    }
}
