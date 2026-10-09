# CargoCore

A C# vehicle hauling demo for FiveM. Load a Blista onto a car carrier, secure it, haul it to the destination, and unload it to finish the job.

The demo uses a Phantom, a `tr2` trailer, and one cargo slot. It runs without a roleplay framework and requires OneSync.

## Setup

Build with the .NET 10 SDK:

```powershell
.\build.cmd
```

Copy the contents of `dist` into a server resource named `jai-trucking`. Enable OneSync and add these permissions for your test group in `server.cfg`:

```text
set onesync on
add_ace group.admin cargocore.dev allow
add_ace group.admin cargocore.interact allow
add_ace group.admin cargocore.manage allow
ensure jai-trucking
```

Your player must belong to that group. The manage permission allows releasing another player's cargo and is optional for normal testing.

After rebuilding an installed resource, run `refresh` and `restart jai-trucking` in the server console.

## Playing a delivery

1. Go to the yellow CargoCore pickup blip and press **H**. Start on foot with enough room around you for the rig to spawn.
2. Couple the truck to the trailer. Press **G** near the trailer to open the ramp.
3. Drive the Blista onto the lower deck, line it up, stop, and press **E** to secure it.
4. Close the ramp and follow the GPS route. Stop the loaded rig within the destination arrival area.
5. Open the ramp, release the car with **E**, and drive it into the blue drop-off bay. Leave at least 12 metres between the car and the trailer.
6. Stop the car and rig. The objective shows whether delivery is ready or what still needs attention. Press **H** to complete it.

Completion keeps the vehicles in place. Use `/cargo_cleanup` before starting another run.

## Commands

| Command | Purpose |
| --- | --- |
| `/cargo_job` | Start a delivery at pickup |
| `/cargo_spawn` | Spawn a rig for free testing without a delivery |
| `/cargo_ramp` | Toggle the nearby ramp |
| `/cargo_deliver` | Submit the current delivery |
| `/cargo_status` | Show the delivery phase and blocking condition |
| `/cargo_cancel` | Cancel the job and remove its rig |
| `/cargo_cleanup` | Remove your test vehicles and reset |
| `/cargo_coords pickup` or `/cargo_coords dropoff` | Print your coordinates as route settings in F8 |
| `/cargo_debug` | Print the car's position relative to the loading zone |

G and H can be rebound in FiveM key settings.

## Configuration

Edit `route.cfg` for pickup and drop-off positions, zone sizes, and speed limits. Edit `trailer.cfg` for loading bounds, cargo models, ramp settings, and attachment settings. Rebuild and restart after changing either file.
## Code

- `Client` handles controls, map guidance, ramp movement, and cargo attachment.
- `Server` owns test rigs and delivery state, validates requests, and sends updates.
- `Shared` contains configuration parsing, loading rules, and state transitions.
- `Tests` checks the shared rules and delivery conditions.

Cargo uses a rigid joint at its loaded position. The current network owner applies attachment changes, and revisions prevent old updates from overriding newer ones. Delivery checks use the server's assigned vehicles and entity data.

Run the tests with:

```powershell
dotnet run --project Tests\CargoCore.Tests.csproj -- trailer.cfg route.cfg
```
## Limits

This is a demo with one trailer type and one assigned cargo vehicle per player. Jobs are stored in memory and reset on resource restart. There are no payouts, database storage, NPCs, or framework integrations. Spawn positions follow the player's position and heading, so be mindful of that.

Client attachment receipts are checked against the current network owner, but are not independent proof of a physical attachment. Reward integration would need further validation and multiplayer testing.
