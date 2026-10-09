using System;
using System.IO;
using System.Text.RegularExpressions;
using CargoCore;

class Program
{
    static int checks;

    static void Main(string[] args)
    {
        string configuration = File.ReadAllText(args[0]);
        var trailer = TrailerDefinition.Parse(configuration);
        var route = DeliveryRoute.Parse(File.ReadAllText(args.Length > 1 ? args[1] : "route.cfg"));

        TrailerConfiguration(configuration);
        LoadingBounds(trailer);
        RelativeCoordinates();
        AssignmentSerialization();
        AttachmentLifecycle();
        RouteConfiguration(route);
        PickupAndArrival(route);
        DeliveryReadiness(route);
        DeliveryValidation(route);
        CompletionAndCancellation(route);

        Console.WriteLine($"PASS: {checks} checks");
    }

    static void Check(bool condition, string message)
    {
        checks++;
        if (!condition)
            throw new Exception(message);
    }

    static void TrailerConfiguration(string configuration)
    {
        var trailer = TrailerDefinition.Parse(configuration);
        Check(trailer.Strategy == "entity", "Default attachment is rigid");
        string customTimeout = Regex.Replace(configuration, @"(?m)^rampTimeoutSeconds=\d+", "rampTimeoutSeconds=12");
        Check(TrailerDefinition.Parse(customTimeout).RampTimeoutSeconds == 12, "Custom ramp timeout");
        Check(trailer.RampInstantClose, "Default ramp closes instantly");
        Check(!TrailerDefinition.Parse(configuration.Replace("rampInstantClose=true", "rampInstantClose=false")).RampInstantClose,
            "Animated closing can be enabled");

        RejectConfiguration(() => TrailerDefinition.Parse(
            Regex.Replace(configuration, @"(?m)^rampSpeed=\d+", "rampSpeed=0")), "Zero ramp speed");
        RejectConfiguration(() => TrailerDefinition.Parse(
            Regex.Replace(configuration, @"(?m)^rampTimeoutSeconds=\d+", "rampTimeoutSeconds=0")), "Zero ramp timeout");
        RejectConfiguration(() => TrailerDefinition.Parse("model=tr2\nmodel=tr2"), "Duplicate trailer setting");
    }

    static void RejectConfiguration(Action parse, string message)
    {
        bool rejected = false;
        try
        {
            parse();
        }
        catch (FormatException)
        {
            rejected = true;
        }
        Check(rejected, message);
    }

    static void LoadingBounds(TrailerDefinition trailer)
    {
        Check(Rules.Validate(trailer, 0, -2, 1, 0, 0) == null, "Valid lower deck");
        Check(Rules.Validate(trailer, -0.15f, 4.87f, 1.29f, 0.3f, 0) == null, "Blista fits the forward deck");
        Check(Rules.Validate(trailer, -0.15f, trailer.MaxY + 0.01f, 1.29f, 0.3f, 0) != null, "Beyond forward deck limit");
        Check(Rules.Validate(trailer, -0.15f, 4.87f, 1.29f, 0.3f, 1) != null, "Moving Blista cannot load");
        Check(Rules.Validate(trailer, 2, -2, 1, 0, 0) != null, "Outside width");
        Check(Rules.Validate(trailer, 0, -8, 1, 0, 0) != null, "Behind trailer");
        Check(Rules.Validate(trailer, 0, -2, -1, 0, 0) != null, "Below deck");
        Check(Rules.Validate(trailer, 0, -2, 4, 0, 0) != null, "Above lower deck");
        Check(Rules.Validate(trailer, 0, -2, 1, 30, 0) != null, "Misalignment");
        Check(Rules.Validate(trailer, 0, -2, 1, 360, 0) == null, "Heading wrap");
        Check(Rules.Validate(trailer, 0, -2, 1, 180, 0) != null, "Reverse orientation rejected");
        Check(Rules.Validate(trailer, 0, -2, 1, 0, 1) != null, "Moving trailer");
        Check(Rules.Validate(trailer, float.NaN, -2, 1, 0, 0) != null, "Nonfinite offset");
        Check(Rules.Validate(trailer, 0, -2, 1, 0, float.PositiveInfinity) != null, "Nonfinite speed");
        Check(Rules.Validate(trailer, trailer.MinX, trailer.MinY, trailer.MinZ, trailer.MaxAngle, trailer.MaxSpeed) == null, "Inclusive boundaries");
    }

    static void RelativeCoordinates()
    {
        var localPosition = Rules.Relative(2, 0, 1, 90);
        Check(Math.Abs(localPosition[0]) < 0.001 && Math.Abs(localPosition[1] + 2) < 0.001 && localPosition[2] == 1, "Rotated trailer coordinates");
        var aligned = Rules.RelativeRotation(0, 0, 93.3f, 0, 0, 93f);
        Check(Math.Abs(aligned[2] - 0.3f) < 0.001, "Aligned car keeps local yaw");
        var wrapped = Rules.RelativeRotation(0, 0, -179, 0, 0, 179);
        Check(wrapped[2] == 2, "Yaw wraps at 180 degrees");
        var tilted = Rules.RelativeRotation(2, 1, 45, 1, 1, 45);
        Check(tilted[0] == 1 && tilted[1] == 0 && tilted[2] == 0, "Pitch is relative to the trailer");
    }

    static void AssignmentSerialization()
    {
        var original = new Assignment
        {
            Cargo = 10,
            Trailer = 20,
            Revision = 4,
            Attached = true,
            X = 0.25f,
            Y = -3,
            Z = 1,
            RZ = 12
        };
        var decoded = Assignment.Decode(original.Encode());
        Check(decoded.Cargo == original.Cargo && decoded.Trailer == original.Trailer
            && decoded.Revision == original.Revision && decoded.Attached
            && decoded.X == original.X && decoded.Y == original.Y && decoded.RZ == original.RZ,
            "Assignment round trip");
    }

    static void AttachmentLifecycle()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var operation = new CargoOperation
        {
            State = new Assignment { Cargo = 10, Trailer = 20, Revision = 1, Attached = true },
            Deadline = now.AddSeconds(10)
        };
        Check(operation.ReservesSlot && operation.Phase == OperationPhase.Attaching, "Pending attach reserves slot");
        Check(!operation.Confirm(0), "Stale receipt rejected");
        Check(operation.AttachTimedOut(now.AddSeconds(11)), "Unconfirmed attachment times out");
        Check(operation.Confirm(1) && operation.Phase == OperationPhase.Secured, "Attachment receipt secures cargo");
        Check(!operation.Confirm(1) && !operation.AttachTimedOut(now.AddSeconds(11)), "Duplicate receipt leaves cargo secured");
        operation.Release(2, now);
        Check(operation.ReservesSlot && !operation.State.Attached && operation.Phase == OperationPhase.Releasing, "Release waits for its receipt");
        Check(!operation.Confirm(1), "Old receipt cannot undo release");
        Check(operation.Confirm(2) && !operation.ReservesSlot && operation.Phase == OperationPhase.Released, "Release receipt frees the slot");
    }

    static void RouteConfiguration(DeliveryRoute route)
    {
        RejectConfiguration(() => DeliveryRoute.Parse(null), "Missing route configuration");
        float distance = DeliveryRoute.Distance(
            route.PickupX, route.PickupY, route.PickupZ,
            route.DropoffX, route.DropoffY, route.DropoffZ);
        Check(distance > route.PickupRadius + route.ArrivalRadius, "Pickup and drop-off are separate");
    }

    static void PickupAndArrival(DeliveryRoute route)
    {
        var job = new TransportContract { Id = 1, Owner = 1, Cargo = 10, Trailer = 20 };
        Check(job.Deliver(route, ReadyEvidence(route)) != null, "Delivery requires pickup");
        Check(!job.ConfirmPickup(false, true, true), "Pickup requires assigned cargo");
        Check(!job.ConfirmPickup(true, false, true), "Pickup requires secured cargo");
        Check(!job.ConfirmPickup(true, true, false), "Pickup requires the pickup zone");
        Check(job.ConfirmPickup(true, true, true) && job.Phase == ContractPhase.InTransit, "Pickup starts transport");
        Check(!job.ConfirmPickup(true, true, true), "Duplicate pickup is ignored");
        Check(!job.ObserveArrival(true, false, true), "Arrival requires secured cargo");
        Check(!job.ObserveArrival(true, true, false), "Arrival requires the destination");
        Check(!job.ObserveArrival(false, true, true), "Arrival requires the assigned rig");
        Check(job.Deliver(route, ReadyEvidence(route)) != null, "Delivery requires arrival");
        Check(job.ObserveArrival(true, true, true) && job.Phase == ContractPhase.AwaitingUnload, "Arrival allows unloading");
    }

    static void DeliveryReadiness(DeliveryRoute route)
    {
        var job = JobAwaitingUnload();
        Check(job.DeliveryProblem(route, ReadyEvidence(route)) == null && job.Phase == ContractPhase.AwaitingUnload,
            "Readiness leaves the job open");
        var evidence = ReadyEvidence(route);
        evidence.Released = false;
        Check(job.DeliveryProblem(route, evidence) != null && job.Phase == ContractPhase.AwaitingUnload,
            "Secured cargo is not ready");
    }

    static void DeliveryValidation(DeliveryRoute route)
    {
        var job = JobAwaitingUnload();
        RejectDelivery(job, route, evidence => evidence.Released = false, "Cargo must be released");
        RejectDelivery(job, route, evidence => evidence.Authorized = false, "Unauthorized delivery");
        RejectDelivery(job, route, evidence => evidence.SameBucket = false, "Wrong routing bucket");
        RejectDelivery(job, route, evidence => evidence.EntitiesExist = false, "Missing cargo");
        RejectDelivery(job, route, evidence => evidence.CargoAlive = false, "Destroyed cargo");
        RejectDelivery(job, route, evidence => evidence.CargoDistance = route.DeliveryRadius + 0.01f, "Car outside the bay");
        RejectDelivery(job, route, evidence => evidence.PlayerDistance = route.InteractionRadius + 0.01f, "Player too far away");
        RejectDelivery(job, route, evidence => evidence.TrailerDistance = route.MinTrailerDistance - 0.01f, "Car too close to trailer");
        RejectDelivery(job, route, evidence => evidence.CargoSpeed = route.MaxSpeed + 0.01f, "Car is moving");
        RejectDelivery(job, route, evidence => evidence.TruckSpeed = route.MaxSpeed + 0.01f, "Truck is moving");
        RejectDelivery(job, route, evidence => evidence.TrailerSpeed = route.MaxSpeed + 0.01f, "Trailer is moving");
        RejectDelivery(job, route, evidence => evidence.CargoDistance = float.NaN, "Unavailable car position");
        Check(job.Phase == ContractPhase.AwaitingUnload, "Rejected delivery leaves the job open");
    }

    static void CompletionAndCancellation(DeliveryRoute route)
    {
        var job = JobAwaitingUnload();
        var evidence = ReadyEvidence(route);
        evidence.CargoDistance = route.DeliveryRadius;
        evidence.PlayerDistance = route.InteractionRadius;
        evidence.TrailerDistance = route.MinTrailerDistance;
        evidence.CargoSpeed = route.MaxSpeed;
        Check(job.Deliver(route, evidence) == null && job.Phase == ContractPhase.Delivered, "Delivery accepts boundary values");
        Check(job.Deliver(route, evidence) != null && !job.Cancel(), "Completed job cannot change");
        var cancelled = new TransportContract();
        Check(cancelled.Cancel() && cancelled.Phase == ContractPhase.Cancelled, "Cancellation ends the job");
        Check(!cancelled.ConfirmPickup(true, true, true) && cancelled.Deliver(route, evidence) != null, "Cancelled job cannot resume");
    }

    static void RejectDelivery(TransportContract job, DeliveryRoute route, Action<DeliveryEvidence> change, string message)
    {
        var evidence = ReadyEvidence(route);
        change(evidence);
        Check(job.Deliver(route, evidence) != null, message);
    }

    static TransportContract JobAwaitingUnload()
    {
        var job = new TransportContract { Id = 1, Owner = 1, Cargo = 10, Trailer = 20 };
        job.ConfirmPickup(true, true, true);
        job.ObserveArrival(true, true, true);
        return job;
    }

    static DeliveryEvidence ReadyEvidence(DeliveryRoute route)
    {
        return new DeliveryEvidence
        {
            Authorized = true,
            SameBucket = true,
            EntitiesExist = true,
            CargoAlive = true,
            Released = true,
            CargoDistance = 1,
            PlayerDistance = 1,
            CargoSpeed = 0,
            TruckSpeed = 0,
            TrailerSpeed = 0,
            TrailerDistance = route.MinTrailerDistance + 1
        };
    }

}
