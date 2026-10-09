using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CargoCore;
using CitizenFX.Core;
using static CitizenFX.Core.Native.API;

namespace jai_trucking.Client
{

    public sealed class DeliveryClient : BaseScript
    {
        DeliveryRoute route;
        int pickupBlip, dropoffBlip, jobId, jobRevision, requestAfter;
        ContractPhase phase;
        string deliveryStatus;
        public DeliveryClient()
        {
            EventHandlers["cargocore:route"] += new Action<string>(ConfigureRoute);
            EventHandlers["cargocore:jobState"] += new Action<int, int, int, int>(State);
            EventHandlers["cargocore:jobClear"] += new Action(Clear);
            EventHandlers["cargocore:deliveryStatus"] += new Action<int, string>((id, status) =>
            {
                if (id == jobId)
                    deliveryStatus = status;
            });
            EventHandlers["onClientResourceStop"] += new Action<string>(Stop);
            RegisterCommand("cargo_deliver", new Action<int, List<object>, string>((source, args, raw) => SubmitDelivery()), false);
            RegisterCommand("cargo_delivery_action", new Action<int, List<object>, string>((source, args, raw) =>
            {
                if (GetGameTimer() < requestAfter)
                    return;
                if (jobId == 0)
                {
                    requestAfter = GetGameTimer() + 1000;
                    TriggerServerEvent("cargocore:startJob");
                }
                else
                    SubmitDelivery();
            }), false);
            RegisterKeyMapping("cargo_delivery_action", "CargoCore: start or complete delivery", "keyboard", "H");
            Tick += Display;
            Tick += InitialSync;
        }

        async Task InitialSync()
        {
            await Delay(1500);
            TriggerServerEvent("cargocore:sync");
            if (route != null)
                Tick -= InitialSync;
        }

        void ConfigureRoute(string configuration)
        {
            if (route != null)
                return;
            route = DeliveryRoute.Parse(configuration);
            pickupBlip = AddBlipForCoord(route.PickupX, route.PickupY, route.PickupZ);
            SetBlipSprite(pickupBlip, 477);
            SetBlipColour(pickupBlip, 5);
            SetBlipDisplay(pickupBlip, 4);
            SetBlipAsShortRange(pickupBlip, false);
            NameBlip(pickupBlip, "CargoCore pickup");
            UpdateGuidance();
            Debug.WriteLine("[CargoCore] delivery route received; pickup blip created handle=" + pickupBlip);
        }

        static void NameBlip(int blip, string name)
        {
            BeginTextCommandSetBlipName("STRING");
            AddTextComponentSubstringPlayerName(name);
            EndTextCommandSetBlipName(blip);
        }

        void State(int id, int cargo, int stage, int revision)
        {
            if (revision <= jobRevision)
                return;
            bool changed = jobId != id || phase != (ContractPhase)stage;
            jobId = id;
            jobRevision = revision;
            phase = (ContractPhase)stage;
            if (changed)
                deliveryStatus = null;
            if (route != null && changed)
                UpdateGuidance();
        }

        void UpdateGuidance()
        {
            bool destination = jobId != 0 && (phase == ContractPhase.InTransit || phase == ContractPhase.AwaitingUnload);
            if (!destination && dropoffBlip != 0)
            {
                SetBlipRoute(dropoffBlip, false);
                RemoveBlip(ref dropoffBlip);
            }
            SetBlipRoute(pickupBlip, jobId != 0 && phase == ContractPhase.AwaitingLoad);
            if (destination && dropoffBlip == 0)
            {
                dropoffBlip = AddBlipForCoord(route.DropoffX, route.DropoffY, route.DropoffZ);
                SetBlipSprite(dropoffBlip, 1);
                SetBlipColour(dropoffBlip, 3);
                NameBlip(dropoffBlip, "CargoCore vehicle drop-off");
                SetBlipDisplay(dropoffBlip, 4);
                SetBlipAsShortRange(dropoffBlip, false);
                SetBlipRoute(dropoffBlip, true);
                SetBlipRouteColour(dropoffBlip, 3);
            }
        }

        void Clear()
        {
            jobId = 0;
            deliveryStatus = null;
            if (pickupBlip != 0)
                SetBlipRoute(pickupBlip, false);
            if (dropoffBlip != 0)
            {
                SetBlipRoute(dropoffBlip, false);
                RemoveBlip(ref dropoffBlip);
            }
        }

        void SubmitDelivery()
        {
            if (GetGameTimer() < requestAfter)
                return;
            requestAfter = GetGameTimer() + 1000;
            TriggerServerEvent("cargocore:deliver");
        }

        Task Display()
        {
            if (route == null)
                return Task.FromResult(0);
            var p = GetEntityCoords(PlayerPedId(), true);
            bool destination = jobId != 0 && (phase == ContractPhase.InTransit || phase == ContractPhase.AwaitingUnload);
            float x = destination ? route.DropoffX : route.PickupX, y = destination ? route.DropoffY : route.PickupY, z = destination ? route.DropoffZ : route.PickupZ;
            float distance = DeliveryRoute.Distance(p.X, p.Y, p.Z, x, y, z);
            if ((jobId == 0 || phase == ContractPhase.AwaitingLoad || destination) && distance < 150)
            {
                float size = destination ? route.DeliveryRadius * 2 : route.StartRadius * 2;
                DrawMarker(1, x, y, z - 0.9f,
                    0, 0, 0, 0, 0, 0,
                    size, size, 0.5f,
                    destination ? 40 : 245, destination ? 170 : 190, destination ? 240 : 50, 100,
                    false, false, 2, false, null, null, false);
            }
            string objective = null;
            if (jobId == 0 && distance <= route.StartRadius)
                objective = "CargoCore pickup: [H] start delivery";
            else if (jobId != 0)
            {
                switch (phase)
                {
                    case ContractPhase.AwaitingLoad:
                        objective = "Delivery: load and secure the assigned Blista at pickup [E]";
                        break;
                    case ContractPhase.InTransit:
                        objective = "Delivery: " + (distance < 60 && deliveryStatus != null ? deliveryStatus : "haul secured cargo to the GPS destination and stop");
                        break;
                    case ContractPhase.AwaitingUnload:
                        objective = "Delivery: " + (deliveryStatus ?? "Release the car and park it in the blue bay; [H] complete");
                        break;
                    case ContractPhase.Delivered:
                        objective = "Delivery complete! /cargo_cleanup to reset for another run";
                        break;
                    case ContractPhase.Cancelled:
                        objective = "Delivery cancelled";
                        break;
                }
            }
            if (objective != null)
            {
                SetTextFont(0);
                SetTextScale(0, 0.32f);
                SetTextColour(255, 255, 255, 230);
                SetTextCentre(true);
                SetTextOutline();
                BeginTextCommandDisplayText("STRING");
                AddTextComponentSubstringPlayerName(objective);
                EndTextCommandDisplayText(0.5f, 0.93f);
            }
            return Task.FromResult(0);
        }

        void Stop(string resource)
        {
            if (resource != GetCurrentResourceName())
                return;
            Clear();
            if (pickupBlip != 0)
                RemoveBlip(ref pickupBlip);
        }
    }
}
