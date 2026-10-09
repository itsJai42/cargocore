using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CitizenFX.Core;
using CargoCore;
using static CitizenFX.Core.Native.API;

namespace jai_trucking.Client
{
    public sealed class ClientMain : BaseScript
    {
        readonly Dictionary<int, Assignment> desired = new Dictionary<int, Assignment>();
        readonly HashSet<int> applied = new HashSet<int>();
        readonly Dictionary<int, int> reportedAttachmentFailures = new Dictionary<int, int>();
        readonly HashSet<int> trailers = new HashSet<int>();
        readonly HashSet<int> rampOperations = new HashSet<int>();
        readonly TrailerDefinition definition;
        int nextRequest;
        int interactionScanAfter, interactionTrailer, interactionCargo;
        Assignment interactionSecured;
        bool interactionCanSecure;
        string interactionText;
        public ClientMain()
        {
            definition = TrailerDefinition.Parse(LoadResourceFile(GetCurrentResourceName(), "trailer.cfg"));
            EventHandlers["cargocore:state"] += new Action<string>(State);
            EventHandlers["cargocore:forget"] += new Action<int>(Forget);
            EventHandlers["cargocore:message"] += new Action<string>(Notify);
            EventHandlers["cargocore:rig"] += new Action<int, int, int>((truck, trailer, cargo) => trailers.Add(trailer));
            EventHandlers["cargocore:ramp"] += new Action<int, int, bool>((net, revision, open) =>
            {
                _ = Ramp(net, revision, open);
            });
            RegisterCommand("cargo_ramp", new Action<int, List<object>, string>((source, args, raw) => RequestRamp()), false);
            RegisterKeyMapping("cargo_ramp", "CargoCore: toggle nearby trailer ramp", "keyboard", "G");
            RegisterCommand("cargo_debug", new Action<int, List<object>, string>((source, args, raw) => DebugLoading()), false);
            RegisterCommand("cargo_coords", new Action<int, List<object>, string>((source, args, raw) => ShowCoordinates(args)), false);
            EventHandlers["onClientResourceStop"] += new Action<string>(Stop);
            Tick += Interact;
            Tick += Reconcile;
            TriggerServerEvent("cargocore:sync");
            Debug.WriteLine("[CargoCore] client ready");
        }

        void State(string wire)
        {
            var a = Assignment.Decode(wire);
            Assignment old;
            if (desired.TryGetValue(a.Cargo, out old) && old.Revision >= a.Revision)
                return;
            desired[a.Cargo] = a;
            trailers.Add(a.Trailer);
        }

        int Entity(int net)
        {
            return NetworkDoesEntityExistWithNetworkId(net) ? NetworkGetEntityFromNetworkId(net) : 0;
        }

        bool Exists(int e)
        {
            return e != 0 && DoesEntityExist(e);
        }

        void Notify(string text)
        {
            BeginTextCommandThefeedPost("STRING");
            AddTextComponentSubstringPlayerName(text);
            EndTextCommandThefeedPostTicker(false, false);
        }

        void ShowCoordinates(List<object> args)
        {
            int ped = PlayerPedId();
            var p = GetEntityCoords(ped, true);
            float heading = GetEntityHeading(ped);
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            string position = string.Format(culture, "X {0:F2}, Y {1:F2}, Z {2:F2}, heading {3:F2}", p.X, p.Y, p.Z, heading);
            Notify(position);
            Debug.WriteLine("[CargoCore] coordinates: " + position);
            string prefix = args.Count > 0 ? args[0].ToString().ToLowerInvariant() : "pickup";
            if (prefix != "pickup" && prefix != "dropoff")
            {
                Notify("Use /cargo_coords pickup or /cargo_coords dropoff for route settings");
                return;
            }
            Debug.WriteLine(string.Format(culture, "{0}X={1:F2}\n{0}Y={2:F2}\n{0}Z={3:F2}", prefix, p.X, p.Y, p.Z));
        }

        void Help(string text)
        {
            BeginTextCommandDisplayHelp("STRING");
            AddTextComponentSubstringPlayerName(text);
            EndTextCommandDisplayHelp(0, false, false, -1);
        }

        void RequestRamp()
        {
            var position = GetEntityCoords(PlayerPedId(), true);
            var trailer = World.GetAllVehicles().Where(v => v.Model.Hash == GetHashKey(definition.Model) && (v.Position - position).Length() <= 12f)
                .OrderBy(v => (v.Position - position).Length()).FirstOrDefault();
            if (trailer == null)
            {
                Notify("No supported trailer nearby; approach your test trailer");
                return;
            }
            if (GetGameTimer() < nextRequest)
                return;
            nextRequest = GetGameTimer() + 1000;
            Debug.WriteLine("[CargoCore] ramp key/command detected trailer=" + trailer.NetworkId);
            TriggerServerEvent("cargocore:request", "ramp", 0, trailer.NetworkId);
        }

        async Task Ramp(int net, int revision, bool open)
        {
            Debug.WriteLine("[CargoCore] ramp event received trailer=" + net + " revision=" + revision + " open=" + open);
            if (!rampOperations.Add(net))
                return;
            try
            {
                int deadline = GetGameTimer() + definition.RampTimeoutSeconds * 1000;
                bool issued = false;
                string reason = "door did not reach the requested angle";
                float angle = -1f;
                while (GetGameTimer() < deadline)
                {
                    int e = Entity(net);
                    if (!Exists(e) || GetEntityModel(e) != GetHashKey(definition.Model))
                    {
                        reason = "trailer not in scope";
                        break;
                    }
                    if (!NetworkHasControlOfEntity(e))
                    {
                        reason = "network control unavailable";
                        NetworkRequestControlOfEntity(e);
                        await Delay(50);
                        continue;
                    }
                    if (!issued)
                    {
                        if (GetEntitySpeed(e) > definition.MaxSpeed)
                        {
                            reason = "trailer moving: " + GetEntitySpeed(e);
                            break;
                        }
                        if (open)
                            SetVehicleDoorOpen(e, definition.RampDoor, false, false);
                        else
                            SetVehicleDoorShut(e, definition.RampDoor, definition.RampInstantClose);
                        issued = true;
                        reason = "door did not reach the requested angle";
                    }
                    if (open || !definition.RampInstantClose)
                        SetVehicleDoorControl(e, definition.RampDoor, definition.RampSpeed, open ? 1f : 0f);
                    angle = GetVehicleDoorAngleRatio(e, definition.RampDoor);
                    if ((open && angle > 0.5f) || (!open && angle < 0.05f))
                    {
                        TriggerServerEvent("cargocore:rampAck", net, revision);
                        return;
                    }
                    await Delay(0);
                }
                Debug.WriteLine("[CargoCore] ramp application not confirmed trailer=" + net + " revision=" + revision + " issued=" + issued + " angle=" + angle + " reason=" + reason);
            }
            finally { rampOperations.Remove(net); }
        }

        void DebugLoading()
        {
            int ped = PlayerPedId();
            var position = GetEntityCoords(ped, true);
            var trailer = World.GetAllVehicles().Where(v => v.Model.Hash == GetHashKey(definition.Model) && (v.Position - position).Length() <= 12f)
                .OrderBy(v => (v.Position - position).Length()).FirstOrDefault();
            int cargo = GetVehiclePedIsIn(ped, false);
            if (cargo == 0)
                cargo = GetVehiclePedIsIn(ped, true);
            if (trailer == null || !Exists(cargo) || cargo == trailer.Handle)
            {
                Notify("Enter the Blista near the trailer, then use /cargo_debug");
                return;
            }
            var c = GetEntityCoords(cargo, true);
            var p = GetOffsetFromEntityGivenWorldCoords(trailer.Handle, c.X, c.Y, c.Z);
            float heading = Rules.Angle(GetEntityHeading(cargo) - GetEntityHeading(trailer.Handle));
            string result = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "[CargoCore] loading position x={0:F2} y={1:F2} z={2:F2} heading={3:F1}; bounds x=[{4},{5}] y=[{6},{7}] z=[{8},{9}]; touching={10}; attached={11}",
                p.X, p.Y, p.Z, heading, definition.MinX, definition.MaxX, definition.MinY, definition.MaxY, definition.MinZ, definition.MaxZ,
                IsEntityTouchingEntity(cargo, trailer.Handle), IsEntityAttachedToEntity(cargo, trailer.Handle));
            Debug.WriteLine(result);
            Notify(string.Format(System.Globalization.CultureInfo.InvariantCulture, "Loading position: X {0:F2}, Y {1:F2}, Z {2:F2}. Details in F8.", p.X, p.Y, p.Z));
        }

        async Task Reconcile()
        {
            await Delay(desired.Values.Any(a => a.Attached) ? 100 : 750);
            foreach (var a in desired.Values.ToArray())
            {
                int cargo = Entity(a.Cargo), trailer = Entity(a.Trailer);
                if (!Exists(cargo) || !NetworkHasControlOfEntity(cargo))
                    continue;
                if (!a.Attached)
                {
                    if (IsEntityAttached(cargo) && (applied.Contains(a.Cargo) || (Exists(trailer) && IsEntityAttachedToEntity(cargo, trailer))))
                        ReleaseAtCurrentPose(cargo, trailer);
                    applied.Remove(a.Cargo);
                    if (!IsEntityAttached(cargo))
                        TriggerServerEvent("cargocore:ack", a.Cargo, a.Revision, true);
                    continue;
                }
                if (!Exists(trailer) || GetEntityModel(trailer) != GetHashKey(definition.Model))
                    continue;
                if (!IsEntityAttachedToEntity(cargo, trailer))
                {
                    if (IsEntityAttached(cargo))
                        continue;
                    var min = Vector3.Zero;
                    var max = Vector3.Zero;
                    GetModelDimensions(unchecked((uint)GetEntityModel(cargo)), ref min, ref max);
                    if (max.X - min.X > definition.MaxWidth || max.Y - min.Y > definition.MaxLength || max.Z - min.Z > definition.MaxHeight)
                        continue;
                    
                    var expected = GetOffsetFromEntityInWorldCoords(trailer, a.X, a.Y, a.Z);
                    float distance = (GetEntityCoords(cargo, true) - expected).Length();
                    if (distance > 0.75f)
                    {
                        int reported;
                        if (!reportedAttachmentFailures.TryGetValue(a.Cargo, out reported) || reported != a.Revision)
                        {
                            reportedAttachmentFailures[a.Cargo] = a.Revision;
                            Debug.WriteLine("[CargoCore] attachment lost cargo=" + a.Cargo + " trailer=" + a.Trailer + " distance=" + distance);
                            TriggerServerEvent("cargocore:attachmentLost", a.Cargo, a.Revision);
                        }
                        continue;
                    }
                    if (!NetworkHasControlOfEntity(trailer))
                    {
                        NetworkRequestControlOfEntity(trailer);
                        continue;
                    }
                    if (definition.Strategy == "carrier")
                    {
                        AttachVehicleOnToTrailer(cargo, trailer, 0f, 0f, 0f, a.X, a.Y, a.Z, a.RX, a.RY, a.RZ, 1f);
                        Debug.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "[CargoCore] carrier attached cargo={0} relative rotation={1:F2},{2:F2},{3:F2}", a.Cargo, a.RX, a.RY, a.RZ));
                    }
                    else
                    {
                        AttachEntityToEntity(cargo, trailer, 0, a.X, a.Y, a.Z, a.RX, a.RY, a.RZ, false, false, false, false, 2, true);
                        Debug.WriteLine("[CargoCore] rigid joint applied cargo=" + a.Cargo + " trailer=" + a.Trailer + " revision=" + a.Revision);
                    }
                }
                bool success = IsEntityAttachedToEntity(cargo, trailer);
                if (success)
                    applied.Add(a.Cargo);
                TriggerServerEvent("cargocore:ack", a.Cargo, a.Revision, success);
            }
        }

        void ReleaseAtCurrentPose(int cargo, int trailer)
        {
            ProcessEntityAttachments(cargo);
            var position = GetEntityCoords(cargo, true);
            var rotation = GetEntityRotation(cargo, 2);
            float health = GetVehicleBodyHealth(cargo);

            DetachEntity(cargo, false, true);
            SetEntityCollision(cargo, true, true);
            SetEntityAngularVelocity(cargo, 0f, 0f, 0f);
            _ = SettleReleaseRotation(cargo, NetworkGetNetworkIdFromEntity(cargo));
            Debug.WriteLine("[CargoCore] released at current world pose cargo=" + NetworkGetNetworkIdFromEntity(cargo));
            _ = MeasureRelease(cargo, NetworkGetNetworkIdFromEntity(cargo), position, rotation, health);
        }

        async Task SettleReleaseRotation(int cargo, int net)
        {
            try
            {
                int until = GetGameTimer() + 300;
                while (GetGameTimer() < until)
                {
                    await Delay(0);
                    if (!Exists(cargo) || NetworkGetNetworkIdFromEntity(cargo) != net
                        || !NetworkHasControlOfEntity(cargo) || IsEntityAttached(cargo))
                        return;
                    if (GetEntitySpeed(cargo) > 0.35f)
                        return;
                    SetEntityAngularVelocity(cargo, 0f, 0f, 0f);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[CargoCore] release settling failed: " + ex.Message);
            }
        }

        async Task MeasureRelease(int cargo, int net, Vector3 position, Vector3 rotation, float health)
        {
            try
            {
                int elapsed = 0;
                foreach (int delay in new[] { 0, 250, 750 })
                {
                    await Delay(delay);
                    elapsed += delay;
                    if (!Exists(cargo) || NetworkGetNetworkIdFromEntity(cargo) != net)
                        return;
                    var p = GetEntityCoords(cargo, true);
                    var r = GetEntityRotation(cargo, 2);
                    Debug.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "[CargoCore] release motion cargo={0} sample={1} displacement={2:F3} dz={3:F3} rotationDelta={4:F2},{5:F2},{6:F2} bodyDamage={7:F1} speed={8:F3}",
                        net, elapsed, (p - position).Length(), p.Z - position.Z, Rules.Angle(r.X - rotation.X), Rules.Angle(r.Y - rotation.Y), Rules.Angle(r.Z - rotation.Z), health - GetVehicleBodyHealth(cargo), GetEntitySpeed(cargo)));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[CargoCore] release measurement failed: " + ex.Message);
            }
        }

        void UpdateInteraction()
        {
            int ped = PlayerPedId();
            var pos = GetEntityCoords(ped, true);
            int trailer = 0;
            float closest = 12f;
            foreach (var vehicle in World.GetAllVehicles())
            {
                if (vehicle.Model.Hash != GetHashKey(definition.Model))
                    continue;
                float distance = (vehicle.Position - pos).Length();
                if (distance < closest)
                {
                    trailer = vehicle.Handle;
                    closest = distance;
                }
            }
            interactionTrailer = trailer;
            interactionText = null;
            if (!Exists(trailer))
            {
                interactionScanAfter = GetGameTimer() + 500;
                return;
            }
            int trailerNet = NetworkGetNetworkIdFromEntity(trailer);
            var secured = desired.Values.FirstOrDefault(a => a.Trailer == trailerNet && a.Attached);
            int cargo = GetVehiclePedIsIn(ped, false);
            if (cargo == 0)
                cargo = GetVehiclePedIsIn(ped, true);
            string text = "[G] Lower Ramp";
            bool canSecure = false;
            if (secured != null)
                text += " ~INPUT_CONTEXT~ release vehicle";
            else if (Exists(cargo) && cargo != trailer && NetworkGetEntityIsNetworked(cargo))
            {
                var c = GetEntityCoords(cargo, true);
                var offset = GetOffsetFromEntityGivenWorldCoords(trailer, c.X, c.Y, c.Z);
                var rot = GetEntityRotation(cargo, 2);
                var tr = GetEntityRotation(trailer, 2);
                string error = Rules.Validate(definition, offset.X, offset.Y, offset.Z, rot.Z - tr.Z, Math.Max(GetEntitySpeed(trailer), GetEntitySpeed(cargo)));
                canSecure = error == null && definition.CargoModels.Any(m => GetEntityModel(cargo) == GetHashKey(m.Trim()));
                text += canSecure ? " ~INPUT_CONTEXT~ secure vehicle" : " | " + (error ?? "Cargo model is not permitted");
            }
            interactionCargo = cargo;
            interactionSecured = secured;
            interactionCanSecure = canSecure;
            interactionText = text;
        }

        Task Interact()
        {
            int now = GetGameTimer();
            if (now >= interactionScanAfter)
            {
                interactionScanAfter = now + 250;
                UpdateInteraction();
            }
            if (interactionText == null || !Exists(interactionTrailer))
                return Task.FromResult(0);
            Help(interactionText);
            if (now >= nextRequest)
            {
                if (IsControlJustReleased(0, 51) && (interactionSecured != null || (interactionCanSecure && Exists(interactionCargo))))
                {
                    TriggerServerEvent("cargocore:request", interactionSecured != null ? "release" : "secure",
                        interactionSecured != null ? interactionSecured.Cargo : NetworkGetNetworkIdFromEntity(interactionCargo), NetworkGetNetworkIdFromEntity(interactionTrailer));
                    nextRequest = now + 1000;
                }
            }
            return Task.FromResult(0);
        }

        void Forget(int net)
        {
            Assignment a;
            if (desired.TryGetValue(net, out a))
            {
                int cargo = Entity(net), trailer = Entity(a.Trailer);
                if (Exists(cargo) && NetworkHasControlOfEntity(cargo) && Exists(trailer) && IsEntityAttachedToEntity(cargo, trailer))
                    ReleaseAtCurrentPose(cargo, trailer);
            }
            desired.Remove(net);
            applied.Remove(net);
            reportedAttachmentFailures.Remove(net);
        }

        void Stop(string resource)
        {
            if (resource != GetCurrentResourceName())
                return;
            foreach (var a in desired.Values)
            {
                int cargo = Entity(a.Cargo), trailer = Entity(a.Trailer);
                if (Exists(cargo) && NetworkHasControlOfEntity(cargo) && (applied.Contains(a.Cargo) || (Exists(trailer) && IsEntityAttachedToEntity(cargo, trailer))))
                    ReleaseAtCurrentPose(cargo, trailer);
            }
            desired.Clear();
            applied.Clear();
        }
    }
}
