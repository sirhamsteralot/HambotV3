# Torque Log Upload Server: Wire Format and Endpoint Requirements

This document describes the real-time web upload feature of the **Torque** OBD2 Android app (`org.prowl.torque`, Torque Pro and Torque Lite): what HTTP traffic it sends, in what format, and what a server endpoint must do to receive and process the data correctly.

## 1. How the upload is triggered

In the app, under **Settings → Data Logging & Upload**, the relevant settings are:

| Setting | Purpose |
|---|---|
| **Upload to webserver** (under *Realtime Web Upload*) | Master switch for real-time uploads |
| **Webserver URL** | Your endpoint, e.g. `https://yourhost/upload` (appends its own query string) |
| **User Email Address** | Free-form string sent as the `eml` parameter; the only reliable per-vehicle/per-user identifier |
| **Web Logging Interval** | How often a sample is uploaded (default is a few seconds; earlier versions upload roughly once per second) |
| **Only when OBD connected** | Suppress uploads when no adapter is connected |
| **Select what to log** (`Add PID to log`) | Determines which `k*` parameters appear in each request |
| **Send HTTPS: Bearer Token** (Torque Pro ≥ 1.12.46) | Sends an `Authorization: Bearer <token>` header so the endpoint can be put behind TLS |

The app also has a **Test settings** button that fires one request at the Webserver URL; the app reports success only if your endpoint responds correctly (see section 3).

## 2. Wire format

### 2.1 Request

Torque sends one request per logging interval with **all data encoded in the URL query string**. The HTTP method is not reliable to depend on: some servers observe a `GET`, others observe a `POST` with an empty body (parameters still in the query string). **A compliant endpoint must accept both GET and POST on the same URL and always parse the query string, never the body.**

Example request:

```http
GET /upload?v=8&eml=my@email.com&userUnits=1&session=1521995741941&id=80c9065bff55006e9f5f3d4f8d9456ae&time=1521996644906&kff1005=19.96196812&kff1006=50.08527848&kff1001=27.504&kff1007=275.4&k2d=99.21875&k4=0.0&kc=2257.0&kd=99.0&kff1226=8.839944&kff1238=14.1 HTTP/1.1
Host: yourhost
```

Notes:

- The URL the app requests is your configured Webserver URL plus `?` and the parameters.
- Values are URL-encoded plain floats/strings; no JSON, no multipart, no form body.
- The request may repeat the same `k*` key more than once in one URL; handle duplicates (last or first occurrence, consistently).
- **Do not put spaces in the User Email Address field**: spaces break the query string encoding in some Torque versions. Use underscores or hyphens if you need separators.
- If HTTPS with a Bearer Token is enabled, the request also carries `Authorization: Bearer <your token>` (requires an `https://` URL).

### 2.2 Standard parameters

| Parameter | Type | Meaning |
|---|---|---|
| `v` | int | Upload format version (e.g. `3`, `8`); store it, do not assume a fixed value |
| `session` | string | Session identifier = epoch-milliseconds timestamp of the drive start; all requests in one drive share it |
| `id` | string (32 hex chars) | Second session/device identifier; store alongside `session` |
| `time` | string (epoch ms) | Timestamp of this data sample |
| `eml` | string | The app's *User Email Address* field, verbatim; main vehicle/user discriminator |
| `k...` | float(s) | One parameter per logged PID; value as decimal string |
| `userUnit*`, `defaultUnit*` | mixed | Unit metadata keys (e.g. user-display units); safe to ignore |
| `profile*`, `profileName` | mixed | Vehicle-profile metadata; `profileName` can be useful, other `profile*` keys are safe to ignore |

### 2.3 Common PID keys (`k*`)

Standard mode-01 PIDs use short keys; extended/GPS/logic PIDs use `kff` + a hex code. Common ones observed in the reference implementations:

| Key | Meaning |
|---|---|
| `kc` | Engine RPM |
| `kd` | Speed (OBD) |
| `k4` | Engine load |
| `k5` | Engine coolant temperature |
| `k11` | Throttle position |
| `k2f` | Fuel level |
| `kf` | Intake air temperature |
| `k46` | Ambient air temperature |
| `kff1001` | Speed (GPS) |
| `kff1005` | GPS latitude |
| `kff1006` | GPS longitude |
| `kff1007` | GPS bearing |
| `kff1220` / `kff1221` | Accelerometer X / Y |
| `kff1225` | Air-fuel ratio (calculated) |
| `kff1226` | Horsepower (calculated) |
| `kff1201` | Trip distance |
| `kff1238` / `kff1239` | Engine oil temp / fuel rate (typical extended PIDs) |
| `kff5201`–`kff5203` | Sensor/extension PIDs |

Custom user-defined extended PIDs appear as further `kff` + hex keys. **Treat the set of `k*` keys as open-ended**: any new PID (or new Torque version) can introduce keys you have never seen.

## 3. What the endpoint must do (processing checklist)

1. **Accept both GET and POST** on the upload URL; parse parameters from the query string only.
2. **Respond fast with `OK!`**: HTTP 200, body exactly containing `OK!` (plain text). This is the success signal the app checks, both in "Test settings" and during driving. Anything else (404, slow response, different body) is treated as failure and the app may retry or alert.
3. **Do the actual work asynchronously.** Respond immediately, then insert into storage from a queue or background task. Requests arrive as often as your Web Logging Interval; a blocking save per request will lag the uploader behind real-time.
4. **Identify the sample**: primary keys are (`session` or `id`) + `time`. `session` groups a whole drive; `time` orders samples within it. Consider UPSERT semantics on (session, id, time) to make retries idempotent.
5. **Handle unknown `k*` keys gracefully.** Two strategies:
   - *Wide table*: auto-add a column per new `k` key on first sighting (the approach of the classic PHP receiver).
   - *Narrow table* (recommended): one row per (sample, key) — columns `(session, id, time, eml, key, value)` — or a JSON column, so schema never changes.
6. **Persist metadata**: store `v`, `eml`, `session`, `id`, `time` and all `k*` values; you can drop `userUnit*`, `defaultUnit*`, and `profile*` (keep `profileName` if you want it).
7. **Treat every parameter as untrusted input.** `eml` and `profileName` are free user-entered strings; `k*` values should parse as floats but may be empty or malformed. Never interpolate them into SQL; use prepared statements (the well-known PHP example predates this and its string-built SQL must not be copied).
8. **Authenticate**: if using the modern "HTTPS: Bearer Token" option, validate the `Authorization` header and reject with 401 otherwise. On plain HTTP, at minimum require an unguessable path or token parameter and filter on your known `eml` value(s).
9. **Log, don't crash**: a malformed request should still get `OK!` (or a 400 you deliberately choose); never let one bad sample kill the listener, and log it for diagnosis.

## 4. Suggested minimum storage schema

```sql
-- Narrow, schema-stable layout
CREATE TABLE torque_samples (
  session    TEXT,        -- session param
  id         TEXT,        -- id param
  ts         TIMESTAMP,   -- time param (epoch ms converted)
  version    INTEGER,     -- v param
  email      TEXT,        -- eml param
  pid        TEXT,        -- e.g. 'kc', 'kff1005'
  value      REAL,
  PRIMARY KEY (session, id, ts, pid)
);
```

Or a per-sample wide row with a JSON payload if your database supports it. Build sessions (`GROUP BY session`), then charts per PID over `ts`.

## 5. Failure modes and edge cases

- **Method confusion**: implementing only POST-with-body parsing (e.g. `request.body` in Express, `request.form` in Flask) silently receives nothing — the data is in the query string.
- **Duplicate keys / duplicated values** in a single URL: pick one policy.
- **Repeated uploads** of the same `(session, time)` after app reconnects or retries: make inserts idempotent.
- **Empty or zero values**: `0.0` is a valid reading; absent PIDs simply do not appear in that request (per-request key sets can differ within a session).
- **GPS privacy**: requests contain live coordinates of your car. Use TLS, a token, or send only over a VPN (e.g. WireGuard) if the endpoint has no other authentication.
- **Spaces in the email field** break the query string in some versions of the app.

## 6. Reference implementations worth studying

- **econpy/torque** (PHP + MySQL): the original receiver (`upload_data.php`, wide-table auto-ALTER approach) plus map/chart viewers.
- **LeoRX/torque**: Dockerized fork of the above with HTTPS (Traefik), 2FA viewer, and a separate HTTP-only upload endpoint.
- **seanauff/node-red-torque** (Node-RED → MQTT → Telegraf → InfluxDB → Grafana): good example of a pipeline architecture.
- **dejwoo/node-torque-obd** (Node + MongoDB): minimal receiver, documents the `OK!` requirement.
- **davekrejci/torque-dash** (Python/Postgres): receiver + dashboard, supports forwarding raw requests to another URL.
- **eladbar/torque-api** (Docker): listener with pluggable outputs.
- **Home Assistant native `torque` integration**: accepts data at `/api/torque` and requires the `eml` value to match configuration.