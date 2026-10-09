using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CitizenFX.Core;
using CargoCore;
using static CitizenFX.Core.Native.API;

namespace jai_trucking.Server
{
    public sealed class ServerMain : BaseScript
    {
        sealed class Rig
        {
            public int Player, Truck, Trailer, Cargo, RampRevision;
            public bool RampOpen, RampPending;
            public DateTime RampDeadline;
        }
        sealed class DeliveryJob
        {
            public Rig Rig;
            public TransportContract Contract;
            public int Revision;
        }
        readonly Dictionary<int, DeliveryJob> jobs = new Dictionary<int, DeliveryJob>();
        readonly Dictionary<int, CargoOperation> assignments = new Dictionary<int, CargoOperation>();
        readonly Dictionary<int, DateTime> requests = new Dictionary<int, DateTime>();
        readonly List<Rig> rigs = new List<Rig>();
        readonly TrailerDefinition definition;
        readonly DeliveryRoute route;
        readonly string routeConfiguration;
        int revision;
        public ServerMain()
        {
            definition = TrailerDefinition.Parse(LoadResourceFile(GetCurrentResourceName(), "trailer.cfg"));
            routeConfiguration = LoadResourceFile(GetCurrentResourceName(), "route.cfg");
            route = DeliveryRoute.Parse(routeConfiguration);
            EventHandlers["cargocore:request"] += new Action<Player, string, int, int>(Request);
            EventHandlers["cargocore:ack"] += new Action<Player, int, int, bool>(Ack);
            EventHandlers["cargocore:sync"] += new Action<Player>(Sync);
            EventHandlers["cargocore:rampAck"] += new Action<Player, int, int>(RampAck);
            EventHandlers["cargocore:attachmentLost"] += new Action<Player, int, int>(AttachmentLost);
            EventHandlers["cargocore:deliver"] += new Action<Player>(Deliver);
            EventHandlers["cargocore:startJob"] += new Action<Player>(StartJobRequest);
            EventHandlers["playerDropped"] += new Action<Player, string>(Dropped);
            EventHandlers["entityRemoved"] += new Action<int>(Removed);
            EventHandlers["onResourceStop"] += new Action<string>(Stop);
            RegisterCommand("cargo_spawn", new Action<int, List<object>, string>((id, args, raw) => Spawn(id)), false);
            RegisterCommand("cargo_job", new Action<int, List<object>, string>((id, args, raw) => StartDelivery(id)), false);
            RegisterCommand("cargo_status", new Action<int, List<object>, string>((id, args, raw) =>
            {
                DeliveryJob job;
                if (id > 0 && jobs.TryGetValue(id, out job))
                {
                    ObserveJobArrival(job);
                    Tell(id, "Delivery phase: " + job.Contract.Phase + ". " + DeliveryStatus(job));
                }
            }), false);
            RegisterCommand("cargo_cancel", new Action<int, List<object>, string>((id, args, raw) =>
            {
                if (id > 0 && Allowed(id, "cargocore.dev") && Rate(id) && jobs.ContainsKey(id))
                    CleanupPlayer(id);
            }), false);
            RegisterCommand("cargo_cleanup", new Action<int, List<object>, string>((id, args, raw) =>
            {
                if (id > 0 && Allowed(id, "cargocore.dev"))
                    CleanupPlayer(id);
            }), false);
            Exports.Add("GetCargoAssignment", new Func<int, string>(net =>
            {
                CargoOperation operation;
                return assignments.TryGetValue(net, out operation) ? operation.State.Encode() : null;
            }));
            Tick += Reconcile;
            Debug.WriteLine("[CargoCore] ready; OneSync required; configuration=" + definition.Model);
        }

        bool Allowed(int id, string ace)
        {
            return IsPlayerAceAllowed(id.ToString(), ace);
        }

        bool Rate(int id)
        {
            DateTime last;
            if (requests.TryGetValue(id, out last) && (DateTime.UtcNow - last).TotalMilliseconds < 750)
                return false;
            requests[id] = DateTime.UtcNow;
            return true;
        }
        void Tell(int id, string text)
        {
            if (id > 0)
                Players[id].TriggerEvent("cargocore:message", text);
        }

        void Publish(Assignment a)
        {
            TriggerClientEvent("cargocore:state", a.Encode());
        }

        int Entity(int net)
        {
            return net > 0 ? NetworkGetEntityFromNetworkId(net) : 0;
        }

        bool Exists(int e)
        {
            return e != 0 && DoesEntityExist(e);
        }

        bool Near(int id, int entity)
        {
            int ped = GetPlayerPed(id.ToString());
            return Exists(ped) && GetPlayerRoutingBucket(id.ToString()) == GetEntityRoutingBucket(entity) && (GetEntityCoords(ped) - GetEntityCoords(entity)).Length() <= 12f;
        }

        void Sync([FromSource] Player player)
        {
            int id = int.Parse(player.Handle);
            if (!Rate(id))
                return;
            player.TriggerEvent("cargocore:route", routeConfiguration);
            foreach (var op in assignments.Values)
                player.TriggerEvent("cargocore:state", op.State.Encode());
            foreach (var rig in rigs)
                player.TriggerEvent("cargocore:rig", NetworkGetNetworkIdFromEntity(rig.Truck), NetworkGetNetworkIdFromEntity(rig.Trailer), NetworkGetNetworkIdFromEntity(rig.Cargo));
            DeliveryJob job;
            if (jobs.TryGetValue(id, out job))
                SendJob(job);
        }

        void Request([FromSource] Player player, string action, int cargoNet, int trailerNet)
        {
            int id = int.Parse(player.Handle);
            if (!Rate(id))
            {
                Tell(id, "Wait before another cargo request");
                return;
            }
            if (!Allowed(id, "cargocore.interact"))
            {
                Tell(id, "Cargo interaction is not authorized");
                return;
            }
            int trailer = Entity(trailerNet), cargo = Entity(cargoNet);
            if (!Exists(trailer) || GetEntityType(trailer) != 2 || GetEntityModel(trailer) != GetHashKey(definition.Model) || !Near(id, trailer))
            {
                Tell(id, "Unsupported trailer or too far away");
                return;
            }
            if (GetEntitySpeed(trailer) > definition.MaxSpeed)
            {
                Tell(id, "Trailer must be stationary");
                return;
            }
            var rig = rigs.FirstOrDefault(r => r.Trailer == trailer);
            if (action == "ramp")
            {
                if (rig == null || rig.Player != id)
                {
                    Tell(id, "Only the rig owner can operate its ramp");
                    return;
                }
                if (rig.RampPending)
                {
                    Tell(id, "Ramp operation is pending; wait for its result");
                    return;
                }
                rig.RampOpen = !rig.RampOpen;
                rig.RampRevision = ++revision;
                rig.RampPending = true;
                rig.RampDeadline = DateTime.UtcNow.AddSeconds(definition.RampTimeoutSeconds + 2);
                Debug.WriteLine($"[CargoCore] ramp requested player={id} trailer={trailerNet} owner={NetworkGetEntityOwner(trailer)} open={rig.RampOpen}");
                Tell(id, "Moving trailer ramp...");
                SendRamp(rig);
                return;
            }
            if (!Exists(cargo) || GetEntityType(cargo) != 2 || !Near(id, cargo) || GetEntityRoutingBucket(cargo) != GetEntityRoutingBucket(trailer))
            {
                Tell(id, "Cargo unavailable or too far away");
                return;
            }
            CargoOperation existing;
            if (action == "release")
            {
                if (!assignments.TryGetValue(cargoNet, out existing) || !existing.State.Attached || existing.State.Trailer != trailerNet)
                {
                    Tell(id, "Vehicle is not secured here");
                    return;
                }
                if (existing.Requester != id && !Allowed(id, "cargocore.manage"))
                {
                    Tell(id, "Only the loader or cargo manager may release this vehicle");
                    return;
                }
                DeliveryJob arrivingJob;
                if (jobs.TryGetValue(id, out arrivingJob))
                    ObserveJobArrival(arrivingJob);
                existing.Requester = id;
                existing.Release(++revision, DateTime.UtcNow);
                Publish(existing.State);
                return;
            }
            if (action != "secure")
                return;
            DeliveryJob activeJob;
            if (jobs.TryGetValue(id, out activeJob) && activeJob.Contract.Phase == ContractPhase.AwaitingLoad
                && DistanceToPickup(GetEntityCoords(cargo)) > route.PickupRadius)
            {
                Tell(id, "Load and secure the assigned car at the pickup point first");
                return;
            }
            if (assignments.Count >= 128 && !assignments.ContainsKey(cargoNet))
            {
                Tell(id, "Cargo capacity reached; contact an administrator");
                return;
            }
            for (int seat = -1; seat < 4; seat++)
            {
                int occupant = GetPedInVehicleSeat(cargo, seat);
                if (occupant != 0 && occupant != GetPlayerPed(id.ToString()))
                {
                    Tell(id, "Other occupants must leave the cargo vehicle");
                    return;
                }
            }
            if (assignments.TryGetValue(cargoNet, out existing) && existing.ReservesSlot)
            {
                Tell(id, "Cargo already assigned or operation pending");
                return;
            }
            if (assignments.Values.Any(o => o.State.Trailer == trailerNet && o.ReservesSlot))
            {
                Tell(id, "Cargo slot is occupied or releasing");
                return;
            }
            if (rig == null || rig.Player != id || rig.Cargo != cargo || NetworkGetEntityOwner(cargo) != id)
            {
                Tell(id, "Use your registered test vehicle and trailer; enter the cargo driver seat first");
                return;
            }
            if (!definition.CargoModels.Any(m => GetEntityModel(cargo) == GetHashKey(m.Trim())))
            {
                Tell(id, "Cargo model is not permitted");
                return;
            }
            var t = GetEntityCoords(trailer);
            var c = GetEntityCoords(cargo);
            var tr = GetEntityRotation(trailer);
            var cr = GetEntityRotation(cargo);
            if (Math.Abs(tr.X) > 8 || Math.Abs(tr.Y) > 8 || Math.Abs(cr.X) > 10 || Math.Abs(cr.Y) > 10)
            {
                Tell(id, "Load on level ground");
                return;
            }
            var offset = Rules.Relative(c.X - t.X, c.Y - t.Y, c.Z - t.Z, tr.Z);
            string error = Rules.Validate(definition, offset[0], offset[1], offset[2], cr.Z - tr.Z, Math.Max(GetEntitySpeed(trailer), GetEntitySpeed(cargo)));
            if (error != null)
            {
                Tell(id, error);
                return;
            }
            var relativeRotation = Rules.RelativeRotation(cr.X, cr.Y, cr.Z, tr.X, tr.Y, tr.Z);
            var state = new Assignment
            {
                Cargo = cargoNet,
                Trailer = trailerNet,
                Revision = ++revision,
                Attached = true,
                X = offset[0],
                Y = offset[1],
                Z = offset[2],
                RX = relativeRotation[0],
                RY = relativeRotation[1],
                RZ = relativeRotation[2]
            };
            assignments[cargoNet] = new CargoOperation { State = state, Requester = id, CargoHandle = cargo, TrailerHandle = trailer, Deadline = DateTime.UtcNow.AddSeconds(10) };
            Debug.WriteLine($"[CargoCore] secure requested player={id} cargo={cargoNet} trailer={trailerNet} revision={revision}");
            Publish(state);
        }

        void Ack([FromSource] Player player, int net, int rev, bool success)
        {
            CargoOperation op;
            int cargo = Entity(net);
            if (!assignments.TryGetValue(net, out op) || op.State.Revision != rev || op.Confirmed || !Exists(cargo) || cargo != op.CargoHandle || NetworkGetEntityOwner(cargo) != int.Parse(player.Handle))
                return;
            if (!success)
                return;
            if (!op.Confirm(rev))
                return;
            Tell(op.Requester, op.State.Attached ? "Vehicle secured" : "Vehicle released");
            Debug.WriteLine($"[CargoCore] owner receipt cargo={net} revision={rev} attached={op.State.Attached}");
            DeliveryJob job;
            if (jobs.TryGetValue(op.Requester, out job) && job.Contract.ConfirmPickup(
                job.Rig.Cargo == cargo && job.Contract.Trailer == op.State.Trailer,
                op.Phase == OperationPhase.Secured, DistanceToPickup(GetEntityCoords(cargo)) <= route.PickupRadius))
            {
                PublishJob(job);
                Tell(job.Contract.Owner, "Delivery loaded. Follow the GPS to the drop-off; arrive with the car secured.");
            }
        }

        void AttachmentLost([FromSource] Player player, int net, int rev)
        {
            CargoOperation op;
            int cargo = Entity(net);
            if (!assignments.TryGetValue(net, out op) || !op.State.Attached || op.State.Revision != rev || !Exists(cargo)
                || cargo != op.CargoHandle || NetworkGetEntityOwner(cargo) != int.Parse(player.Handle))
                return;
            op.Release(++revision, DateTime.UtcNow);
            Publish(op.State);
            Tell(op.Requester, "Cargo attachment was lost. Stop safely and reload the vehicle.");
            Debug.WriteLine("[CargoCore] attachment loss reported by owner cargo=" + net + " priorRevision=" + rev);
        }

        async Task Reconcile()
        {
            await Delay(1000);
            foreach (var pair in assignments.ToArray())
            {
                var op = pair.Value;
                int cargo = Entity(pair.Key), trailer = Entity(op.State.Trailer);
                if (!Exists(cargo) || cargo != op.CargoHandle)
                {
                    assignments.Remove(pair.Key);
                    TriggerClientEvent("cargocore:forget", pair.Key);
                    continue;
                }
                if (op.State.Attached && (!Exists(trailer) || trailer != op.TrailerHandle || GetEntityHealth(cargo) <= 0 || GetEntityHealth(trailer) <= 0 || op.AttachTimedOut(DateTime.UtcNow)))
                {
                    op.Release(++revision, DateTime.UtcNow);
                    Tell(op.Requester, "Attachment cancelled: entity unavailable, destroyed, or owner timed out");
                }
                Publish(op.State);
            }
            foreach (var rig in rigs.ToArray())
            {
                if (!Exists(rig.Truck) || !Exists(rig.Trailer) || !Exists(rig.Cargo))
                {
                    DeleteRig(rig);
                    continue;
                }
                if (!rig.RampPending)
                    continue;
                if (DateTime.UtcNow > rig.RampDeadline)
                {
                    rig.RampPending = false;
                    Tell(rig.Player, "Ramp could not be confirmed. Approach the trailer and retry /cargo_ramp; check F8 and server logs.");
                    Debug.WriteLine("[CargoCore] ramp timeout trailer=" + NetworkGetNetworkIdFromEntity(rig.Trailer));
                }
                else
                    SendRamp(rig);
            }
            foreach (var job in jobs.Values.ToArray())
            {
                var rig = job.Rig;
                if (job.Contract.Terminal)
                    continue;
                if (!Exists(rig.Cargo) || !Exists(rig.Truck) || !Exists(rig.Trailer) || GetEntityHealth(rig.Cargo) <= 0
                    || GetEntityHealth(rig.Truck) <= 0 || GetEntityHealth(rig.Trailer) <= 0)
                {
                    Tell(rig.Player, "Delivery cancelled: assigned car or rig unavailable");
                    DeleteRig(rig);
                    continue;
                }
                ObserveJobArrival(job);
                Players[rig.Player].TriggerEvent("cargocore:deliveryStatus", job.Contract.Id, DeliveryStatus(job));
            }
        }

        void ObserveJobArrival(DeliveryJob job)
        {
            var rig = job.Rig;
            if (job.Contract.Phase != ContractPhase.InTransit || !Exists(rig.Cargo) || !Exists(rig.Trailer) || !Exists(rig.Truck))
                return;
            CargoOperation op;
            bool secured = assignments.TryGetValue(job.Contract.Cargo, out op) && op.CargoHandle == rig.Cargo
                && op.TrailerHandle == rig.Trailer && op.Phase == OperationPhase.Secured;
            int driver = GetPlayerPed(rig.Player.ToString());
            int bucket = GetEntityRoutingBucket(rig.Cargo);
            bool nearby = Exists(driver) && GetEntityHealth(rig.Cargo) > 0 && GetEntityHealth(rig.Truck) > 0 && GetEntityHealth(rig.Trailer) > 0
                && DistanceToDropoff(GetEntityCoords(rig.Cargo)) <= route.ArrivalRadius
                && DistanceToDropoff(GetEntityCoords(rig.Trailer)) <= route.ArrivalRadius
                && DistanceToDropoff(GetEntityCoords(driver)) <= route.ArrivalRadius + 20
                && GetEntitySpeed(rig.Truck) <= route.MaxSpeed && GetEntitySpeed(rig.Trailer) <= route.MaxSpeed
                && GetPlayerRoutingBucket(rig.Player.ToString()) == bucket
                && GetEntityRoutingBucket(rig.Truck) == bucket && GetEntityRoutingBucket(rig.Trailer) == bucket;
            if (job.Contract.ObserveArrival(true, secured, nearby))
            {
                PublishJob(job);
                Tell(rig.Player, "Destination reached. Release the car, drive it into the drop-off bay, and press H.");
            }
        }

        float DistanceToPickup(Vector3 p)
        {
            return DeliveryRoute.Distance(p.X, p.Y, p.Z, route.PickupX, route.PickupY, route.PickupZ);
        }

        float DistanceToDropoff(Vector3 p)
        {
            return DeliveryRoute.Distance(p.X, p.Y, p.Z, route.DropoffX, route.DropoffY, route.DropoffZ);
        }

        void SendJob(DeliveryJob job)
        {
            Players[job.Contract.Owner].TriggerEvent("cargocore:jobState", job.Contract.Id, job.Contract.Cargo, (int)job.Contract.Phase, job.Revision);
        }

        void PublishJob(DeliveryJob job)
        {
            job.Revision = ++revision;
            SendJob(job);
        }

        void StartDelivery(int id)
        {
            if (id <= 0 || !Allowed(id, "cargocore.dev") || !Allowed(id, "cargocore.interact"))
            {
                if (id > 0)
                    Tell(id, "Delivery testing requires cargocore.dev and cargocore.interact");
                return;
            }
            if (!Rate(id))
                return;
            if (jobs.ContainsKey(id) || rigs.Any(r => r.Player == id))
            {
                Tell(id, "Use /cargo_cleanup before starting a new delivery");
                return;
            }
            int ped = GetPlayerPed(id.ToString());
            if (!Exists(ped) || DistanceToPickup(GetEntityCoords(ped)) > route.StartRadius)
            {
                Tell(id, "Go to the CargoCore pickup blip to start a delivery");
                return;
            }
            Spawn(id, false);
            var rig = rigs.FirstOrDefault(r => r.Player == id);
            if (rig == null)
                return;
            var job = new DeliveryJob
            {
                Rig = rig,
                Contract = new TransportContract
                {
                    Id = ++revision,
                    Owner = id,
                    Cargo = NetworkGetNetworkIdFromEntity(rig.Cargo),
                    Trailer = NetworkGetNetworkIdFromEntity(rig.Trailer)
                }
            };
            jobs[id] = job;
            PublishJob(job);
            Tell(id, "Delivery started. Load and secure your assigned Blista here at pickup.");
            Debug.WriteLine("[CargoCore] delivery started owner=" + id + " contract=" + job.Contract.Id + " cargo=" + job.Contract.Cargo);
        }

        void StartJobRequest([FromSource] Player player)
        {
            StartDelivery(int.Parse(player.Handle));
        }

        void Deliver([FromSource] Player player)
        {
            int id = int.Parse(player.Handle);
            if (!Rate(id))
                return;
            DeliveryJob job;
            if (!jobs.TryGetValue(id, out job))
            {
                Tell(id, "You have no active delivery");
                return;
            }
            var rig = job.Rig;
            int ped = GetPlayerPed(player.Handle);
            bool exist = Exists(ped) && Exists(rig.Cargo) && Exists(rig.Trailer) && Exists(rig.Truck);
            if (!exist)
            {
                Tell(id, "Assigned car or rig unavailable");
                return;
            }
            CargoOperation op;
            bool released = assignments.TryGetValue(job.Contract.Cargo, out op) && op.CargoHandle == rig.Cargo
                && op.TrailerHandle == rig.Trailer && op.Phase == OperationPhase.Released;
            var e = DeliveryFacts(job, ped, released);
            string error = job.Contract.Deliver(route, e);
            if (error != null)
            {
                Tell(id, error);
                return;
            }
            PublishJob(job);
            Tell(id, "Delivery complete. Assigned vehicle received.");
            Debug.WriteLine("[CargoCore] delivery completed owner=" + id + " contract=" + job.Contract.Id + " cargo=" + job.Contract.Cargo);
        }

        DeliveryEvidence DeliveryFacts(DeliveryJob job, int ped, bool released)
        {
            var rig = job.Rig;
            int id = job.Contract.Owner;
            return new DeliveryEvidence
            {
                Authorized = Allowed(id, "cargocore.interact") && rig.Player == id,
                SameBucket = GetPlayerRoutingBucket(id.ToString()) == GetEntityRoutingBucket(rig.Cargo)
                    && GetEntityRoutingBucket(rig.Trailer) == GetEntityRoutingBucket(rig.Cargo)
                    && GetEntityRoutingBucket(rig.Truck) == GetEntityRoutingBucket(rig.Cargo),
                EntitiesExist = true,
                CargoAlive = GetEntityHealth(rig.Cargo) > 0 && GetEntityHealth(rig.Trailer) > 0 && GetEntityHealth(rig.Truck) > 0,
                Released = released,
                CargoDistance = DistanceToDropoff(GetEntityCoords(rig.Cargo)),
                PlayerDistance = (GetEntityCoords(ped) - GetEntityCoords(rig.Cargo)).Length(),
                CargoSpeed = GetEntitySpeed(rig.Cargo),
                TrailerSpeed = GetEntitySpeed(rig.Trailer),
                TruckSpeed = GetEntitySpeed(rig.Truck),
                TrailerDistance = (GetEntityCoords(rig.Cargo) - GetEntityCoords(rig.Trailer)).Length()
            };
        }

        string DeliveryStatus(DeliveryJob job)
        {
            var rig = job.Rig;
            int ped = GetPlayerPed(rig.Player.ToString());
            if (!Exists(ped) || !Exists(rig.Cargo) || !Exists(rig.Truck) || !Exists(rig.Trailer))
                return "Assigned car or rig unavailable";
            if (job.Contract.Phase == ContractPhase.InTransit)
                return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "Arrive secured: car {0:F1}m / trailer {1:F1}m from destination (within {2}m); rig speed {3:F2} (max {4})",
                    DistanceToDropoff(GetEntityCoords(rig.Cargo)), DistanceToDropoff(GetEntityCoords(rig.Trailer)), route.ArrivalRadius,
                    Math.Max(GetEntitySpeed(rig.Truck), GetEntitySpeed(rig.Trailer)), route.MaxSpeed);
            CargoOperation op;
            bool released = assignments.TryGetValue(job.Contract.Cargo, out op) && op.CargoHandle == rig.Cargo
                && op.TrailerHandle == rig.Trailer && op.Phase == OperationPhase.Released;
            return job.Contract.DeliveryProblem(route, DeliveryFacts(job, ped, released)) ?? "Car stopped and ready: [H] complete delivery";
        }

        void SendRamp(Rig rig)
        {
            int owner = NetworkGetEntityOwner(rig.Trailer);
            if (owner <= 0)
                return;
            Players[owner].TriggerEvent("cargocore:ramp", NetworkGetNetworkIdFromEntity(rig.Trailer), rig.RampRevision, rig.RampOpen);
        }

        void RampAck([FromSource] Player player, int net, int rev)
        {
            int entity = Entity(net);
            var rig = rigs.FirstOrDefault(r => r.Trailer == entity && r.RampPending && r.RampRevision == rev);
            if (rig == null || !Exists(entity) || NetworkGetEntityOwner(entity) != int.Parse(player.Handle))
                return;
            rig.RampPending = false;
            Tell(rig.Player, rig.RampOpen ? "Trailer ramp opened" : "Trailer ramp closed");
            Debug.WriteLine("[CargoCore] ramp confirmed trailer=" + net + " revision=" + rev);
        }

        void Spawn(int id, bool checkRate = true)
        {
            if (id <= 0 || !Allowed(id, "cargocore.dev") || (checkRate && !Rate(id)))
            {
                if (id > 0)
                    Tell(id, "Requires cargocore.dev permission; wait between requests");
                return;
            }
            int ped = GetPlayerPed(id.ToString());
            if (!Exists(ped))
                return;
            if (rigs.Any(r => r.Player == id))
            {
                Tell(id, "Clean up your existing rig first with /cargo_cleanup");
                return;
            }
            var pos = GetEntityCoords(ped);
            float h = GetEntityHeading(ped);
            Func<float, float, Vector3> at = (x, y) =>
            {
                var a = h * Math.PI / 180;
                return pos + new Vector3(
                    (float)(x * Math.Cos(a) - y * Math.Sin(a)),
                    (float)(x * Math.Sin(a) + y * Math.Cos(a)),
                    0.5f);
            };
            var rig = new Rig { Player = id };
            rigs.Add(rig);
            try
            {
                var a = at(5, 12);
                var b = at(5, 1);
                var c = at(5, -13);
                rig.Truck = CreateVehicleServerSetter(unchecked((uint)GetHashKey("phantom")), "automobile", a.X, a.Y, a.Z, h);
                rig.Trailer = CreateVehicleServerSetter(unchecked((uint)GetHashKey(definition.Model)), "trailer", b.X, b.Y, b.Z, h);
                rig.Cargo = CreateVehicleServerSetter(unchecked((uint)GetHashKey("blista")), "automobile", c.X, c.Y, c.Z, h);
                foreach (int e in new[] { rig.Truck, rig.Trailer, rig.Cargo })
                {
                    if (!Exists(e))
                        throw new InvalidOperationException("Server vehicle creation failed; check OneSync");
                    SetEntityRoutingBucket(e, GetPlayerRoutingBucket(id.ToString()));
                    SetEntityOrphanMode(e, 2);
                }
                TriggerClientEvent("cargocore:rig", NetworkGetNetworkIdFromEntity(rig.Truck), NetworkGetNetworkIdFromEntity(rig.Trailer), NetworkGetNetworkIdFromEntity(rig.Cargo));
                Tell(id, "Rig spawned. Reverse Phantom into trailer; open ramp with G near trailer; drive Blista onto lower deck; E secures/releases.");
            }
            catch (Exception ex)
            {
                DeleteRig(rig);
                Debug.WriteLine("[CargoCore] spawn failed: " + ex);
                Tell(id, "Rig creation failed; see server log");
            }
        }

        void DeleteRig(Rig rig)
        {
            rigs.Remove(rig);
            DeliveryJob job;
            if (jobs.TryGetValue(rig.Player, out job) && job.Rig == rig)
            {
                if (job.Contract.Cancel())
                {
                    PublishJob(job);
                    Tell(rig.Player, "Delivery cancelled; test rig removed");
                }
                jobs.Remove(rig.Player);
                Players[rig.Player].TriggerEvent("cargocore:jobClear");
            }
            foreach (var pair in assignments.Where(p => Entity(p.Key) == rig.Cargo || Entity(p.Value.State.Trailer) == rig.Trailer).ToArray())
            {
                pair.Value.State.Attached = false;
                pair.Value.State.Revision = ++revision;
                Publish(pair.Value.State);
                assignments.Remove(pair.Key);
                TriggerClientEvent("cargocore:forget", pair.Key);
            }
            foreach (int e in new[] { rig.Cargo, rig.Trailer, rig.Truck })
                if (Exists(e))
                    DeleteEntity(e);
        }

        void Removed(int entity)
        {
            foreach (var pair in assignments.Where(p => p.Value.CargoHandle == entity).ToArray())
            {
                assignments.Remove(pair.Key);
                TriggerClientEvent("cargocore:forget", pair.Key);
            }
            foreach (var op in assignments.Values.Where(o => o.TrailerHandle == entity))
            {
                op.Release(++revision, DateTime.UtcNow);
                Publish(op.State);
            }
            foreach (var rig in rigs.Where(r => r.Cargo == entity || r.Trailer == entity || r.Truck == entity).ToArray())
                DeleteRig(rig);
        }

        void Dropped([FromSource] Player player, string reason)
        {
            CleanupPlayer(int.Parse(player.Handle));
        }

        void CleanupPlayer(int id)
        {
            foreach (var op in assignments.Values.Where(o => o.Requester == id))
            {
                op.Release(++revision, DateTime.UtcNow);
                Publish(op.State);
            }
            foreach (var rig in rigs.Where(r => r.Player == id).ToArray())
                DeleteRig(rig);
            requests.Remove(id);
        }

        void Stop(string resource)
        {
            if (resource != GetCurrentResourceName())
                return;
            foreach (var op in assignments.Values)
            {
                op.State.Attached = false;
                op.State.Revision = ++revision;
                Publish(op.State);
            }
            foreach (var rig in rigs.ToArray())
                DeleteRig(rig);
        }
        [Command("hello_server")]
        public void HelloServer()
        {
            Debug.WriteLine("Sure, hello.");
        }
    }
}
