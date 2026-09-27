# Drive Congestion Collector

Windows/.NET 8 collector and dashboard for learning Hwy 401 congestion patterns between east Oshawa and Pickering in both directions.

## Goal

Build a local historical picture of Hwy 401 traffic by time of day and weekday so recurring congestion can be distinguished from one-off traffic conditions. The collector records five westbound and five eastbound checkpoints.

## Collection schedule

Normal collection runs from **5:00 AM through 9:59 PM Eastern Time**, every **20 minutes**.

With 10 checkpoints this is approximately:

- 17 collection hours/day
- 3 cycles/hour
- 10 Flow Segment Data requests/cycle
- 30-day month: about **15,300 requests**
- 31-day month: about **15,810 requests**

Manual collections consume additional requests.

## TomTom APIs

The application uses two different TomTom capabilities.

### Snap to Roads — checkpoint validation

Before a checkpoint is trusted for historical collection, the application can use TomTom Snap to Roads to map-match a short directional GPS trace around the configured point.

The validator uses the intended direction of travel (westbound or eastbound) and requests the projected road position and road attributes. It checks information such as:

- projected/matched coordinates
- road name/number when available
- Functional Road Class (FRC)
- speed limit
- direction-aware road matching

A checkpoint is considered a strong 401 candidate when it maps to **FRC 0** and has a highway-appropriate speed limit. Points that do not pass are marked for review.

This validation is a setup operation; it is not intended to run every 20 minutes.

Validation endpoint:

```
POST /api/validate-points
```

### Traffic Flow Segment Data — recurring collection

Once checkpoints are validated, recurring collection uses TomTom Flow Segment Data. Requests explicitly use **KMPH**, and samples include current speed and free-flow speed.

A low current speed by itself can indicate congestion. A low **free-flow** speed is instead a warning that the query may have matched a ramp or another nearby road rather than the Hwy 401 mainline.

## API key

Preferred local setup is a key file:

```
secrets/tomtom.key
```

Put only the raw TomTom API key in that file. The entire `secrets/` directory is ignored by Git.

An environment variable can also be used:

```powershell
$env:TOMTOM_API_KEY="YOUR_KEY"
dotnet run
```

The environment variable takes precedence over the local key file.

Never commit or share an API key. If a key is exposed in terminal output, screenshots, chat, or source control, revoke/regenerate it.

## Running locally

Requires .NET 8.

```powershell
git pull
dotnet run
```

Open:

```
http://localhost:5000
```

The dashboard displays live checkpoint conditions, requested versus TomTom-matched coordinates, road-match warnings, historical weekday/time patterns, typical-day charts, and live-versus-typical comparisons.

## Local data

Samples are appended to:

```
data/traffic.jsonl
```

Observations include timestamp, checkpoint, direction, current/free-flow speeds, travel times, confidence, closure state, slowdown percentage, requested point, and TomTom matched-segment information.

### Resetting test data

Stop the collector first with **Ctrl+C**, then:

```powershell
Remove-Item .\data\traffic.jsonl -ErrorAction SilentlyContinue
Test-Path .\data\traffic.jsonl
```

`Test-Path` should return `False`. Restart with `dotnet run`.

Do the final reset only after all checkpoints have been validated. Setup/test observations should not be mixed into the real month-long dataset.

## HTTP API

- `GET /api/status` — collector state and latest checkpoint observations
- `GET /api/samples` — locally stored observations
- `POST /api/collect-now` — trigger an immediate Flow Segment collection
- `POST /api/validate-points` — direction-aware Snap-to-Roads checkpoint validation

Manual Flow collection works outside the normal collection window, so setup/testing should be kept modest.

## Current checkpoint-validation status

The original latitude/longitude checkpoints were approximate. Initial Flow Segment testing showed several suspicious matches with free-flow speeds around 44–71 km/h, while one correctly matched-looking Hwy 401 segment returned about 113 km/h.

For that reason, **the current setup/test samples should not yet be treated as the official historical dataset**. Run Snap-to-Roads validation, correct any checkpoints that do not map to the Hwy 401 mainline, verify both directions, then reset `traffic.jsonl` and begin Day 1.
