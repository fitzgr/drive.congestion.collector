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

Recurring collection is now **bound to the validated Hwy 401 road match**, rather than sending the original approximate checkpoint directly to Flow Segment Data.

At process startup / first collection, each checkpoint is map-matched with TomTom Snap to Roads using its intended eastbound or westbound heading. A checkpoint is accepted only when the match is **FRC 0** with a highway-speed limit (90 km/h or greater). The TomTom-projected coordinate from that validated 401 match is cached for the running process.

Every subsequent 20-minute Flow Segment request uses that **projected Hwy 401 coordinate**. If a checkpoint cannot be validated as Hwy 401, its Flow request is skipped and an error sample is stored instead of silently collecting a nearby ramp or local road.

This adds only the setup Snap-to-Roads calls once per process; it does not double API usage on every 20-minute cycle.

Flow requests explicitly use **KMPH**, and samples include current speed and free-flow speed. The original requested checkpoint and TomTom Flow matched coordinates are both retained so road binding can be inspected during testing.

A low current speed can therefore represent real congestion. A suspicious low free-flow speed should still be reviewed, but the collector no longer intentionally relies on Flow's nearest-road choice from the raw checkpoint.

## Verify and edit Hwy 401 checkpoints

The dashboard now includes a **Verify & Edit Hwy 401 Checkpoints** panel. It is the working source for checkpoint maintenance after the first save.

- **Verify 401 Matches** runs the configured points through TomTom directional Snap-to-Roads and shows the matched road, road number, FRC, speed limit and projected coordinate.
- A green **VALIDATED_401** result means the current validation rule passed: FRC 0 and a speed limit of at least 90 km/h.
- **Add point** creates another editable checkpoint.
- **Remove** deletes a checkpoint from the editable list.
- ID, label, east/west direction, latitude/longitude and optional heading can all be edited.
- **Save points** writes the edited set to `data/points.json` and invalidates the in-memory road bindings so the collector must bind the new points again.
- **Collect now** triggers an immediate traffic collection using only points that can be rebound to a validated 401 match.

The original points in `appsettings.json` remain the seed/default configuration. Once `data/points.json` exists, the editable point store is loaded from that file so dashboard edits survive application restarts without rewriting source-controlled configuration.

A failed/review match is deliberately not used for recurring Flow collection. Edit its coordinate or heading and verify again until it binds to the intended Hwy 401 carriageway.

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

For that reason, **the current setup/test samples should not yet be treated as the official historical dataset**. The collector now binds recurring requests to validated FRC0/highway-speed Snap-to-Roads results and refuses to collect unvalidated checkpoints. Test all ten checkpoints in both directions, inspect requested versus matched positions and free-flow speeds, then reset `traffic.jsonl` and begin Day 1.
