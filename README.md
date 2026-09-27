# Drive Congestion Collector

A Windows-friendly .NET 8 background collector for building a local history of road congestion observations.

## MVP

- Runs continuously on your Windows PC.
- Polls TomTom Traffic Flow every 5 minutes.
- Stores samples locally in `data/traffic.jsonl`.
- Exposes a tiny local dashboard/API:
  - `/api/status`
  - `/api/samples`
- Keeps the API key outside source control.

## Setup

1. Install .NET 8 SDK.
2. Get a TomTom API key.
3. Set it in PowerShell:

```powershell
$env:TOMTOM_API_KEY="YOUR_KEY"
```

4. Edit `appsettings.json` and set `Collector:Point` to the latitude/longitude of the road segment you want to sample.
5. Run:

```powershell
dotnet run
```

The collector takes one sample immediately, then every 5 minutes.

## Data captured

Each line in `data/traffic.jsonl` stores:

- timestamp
- current speed
- free-flow speed
- current travel time
- free-flow travel time
- confidence
- road closure flag
- errors, if any

## Next step

This first version watches one representative point. The next iteration should define your full Scarborough-to-cottage corridor as a set of named checkpoints so one five-minute cycle can tell us exactly which portions of the trip are congested.
