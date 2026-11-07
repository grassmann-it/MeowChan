# Passive diagnostics — next-release preview

**Status:** This guide describes the current implementation for the upcoming
release candidate. The extension has not shipped; full cross-platform release
validation is still pending.

## Understanding an entry

Open the existing **Diagnostics** page and select an entry under **Current
requests and problems**. This is a snapshot of the current state, not a complete
request history. The added details separate:

- **Operation:** for example, a thread check, board catalogue check, media download,
  storing captured posts, or media selection/filtering.
- **Observed result:** available error facts, HTTP status and, where supplied,
  a diagnostic code or reported retry time.
- **Next step:** guidance such as checking the source, checking storage or using an
  existing retry action. The guidance does not execute an action itself.

**Watched URL (not necessarily the failed endpoint)** identifies the watched
thread/board context. It is not necessarily the address of the failed media
request or its CDN server.

## Interpreting messages

| Display | Meaning and limits |
| --- | --- |
| **HTTP 403** | The server refused the request. This alone proves neither a ban nor an account-login problem. Check the source settings and reported status before retrying. |
| **HTTP 404 for a media file** | That file was not found. It does not mean the thread is gone. |
| **HTTP 429** | The server is throttling requests. The code alone establishes neither a current cooldown nor an already scheduled retry. |
| **DNS, connection or TLS failure** | These distinctions require corresponding typed error data, not words found in an error message. A TLS failure does not establish a certificate problem. |
| **Cancellation or timeout** | Cancellation is not proof of a network timeout. An expired deadline does not automatically identify the affected network phase. |
| **Unknown cause** | The available data is insufficient. Missing additional facts, for example after a restart, are not reconstructed from old messages. |

Local storage problems, unprocessable data and filtering decisions are not
automatically network failures. Follow the relevant guidance; for a storage
problem, for example, check the destination and free space.

## Showing waits and retrying

With **Problems only** enabled, **Also show waiting operations** adds request rows
and pending work to the list. This extra option starts off; source and text
filters still apply. Without **Problems only**, those rows are already included.
Showing a waiting operation does not turn it into an error or automatically
include it in a batch retry.

Read the actual status: **Waiting for request slot** and **Request cooldown**
describe a current wait, while **Checking** describes an active request. A
**Reported retry time** alone confirms neither a current retry plan nor a
successful outcome.

Use retry actions only when available. Queueing, pause, source limits and request
admission remain unchanged; do not bypass a cooldown. Batch actions cover the
threads/boards captured when the action starts, not the whole session. Media
retries cover missing files in those threads, not necessarily only the selected
file. “Scheduled” does not mean “fixed.”

## Sharing details carefully

**Copy details** may put unchanged URLs, local paths and raw error messages on the
clipboard. These values may be private. Review and remove sensitive information
yourself before sharing: this is **not a sanitized or anonymized support report**.

Passive diagnostics adds no automatic telemetry or active connection probes.
Viewing, refreshing and copying do not start additional network requests; normal
downloads may continue in the background.
