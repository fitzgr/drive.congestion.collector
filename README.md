# Drive Congestion Collector

Windows/.NET 8 collector for learning Hwy 401 congestion patterns between east Oshawa and Pickering in both directions.

## Collection plan

The collector samples 10 directional points: five westbound and five eastbound.

It runs only from **5:00 AM through 9:59 PM Eastern Time** and samples every **20 minutes**.

That schedule is intentionally conservative for TomTom's current **20,000 free monthly Flow Segment Data requests**:

- 17 collection hours/day
- 3 cycles/hour
- 10 API calls/cycle
- 30-day month = about **15,300 requests**
- 31-day month = about **15,810 requests**

That leaves roughly 4,000+ requests of headroom for testing/manual samples while remaining below 20,000, assuming TomTom continues to count each Flow Segment Data request as one request and the account has the standard free allowance.

## Setup

Set your key in PowerShell:

```powershell
$env:TOMTOM_API_KEY="YOUR_KEY"
dotnet run
```

The API key is never stored in GitHub.

## Local data

Samples are appended to:

```
data/traffic.jsonl
```

Each observation contains timestamp, checkpoint, direction, current speed, free-flow speed, travel times, confidence, closure state and calculated slowdown percentage.

## Important checkpoint validation

The initial checkpoint coordinates are deliberately placed near the eastbound/westbound Hwy 401 carriageways. On the first live run, verify that TomTom snaps each point to the intended 401 direction. If any point snaps to the wrong carriageway or ramp, adjust that point before accumulating the month-long dataset.

## API

- `GET /api/status`
- `GET /api/samples`
- `POST /api/collect-now`

Manual collection works outside the normal time window and is useful for setup/testing.
