# OpsDeck Details V1 — M4.10-A

This is a local **display-only** protocol. It does not initiate Cloudflare requests,
inference, message reads, commands, resource changes, or host controls.

## Navigation and backwards compatibility

Panel log request: `opsdeck.ui: DETAILS_REQUEST slot=0 kind=0 page=0 request=2`.
Slots: 0..1. Categories: Worker=0, D1=1, R2=2, Queues=3, Workers AI=4,
AI Gateway=5. Page: 0..1023. Request identity: 1..2147483647.
Only the active details view repeats its request (every five seconds). Leaving it
clears local values and stops those requests. The host stops detail transmission
16 seconds after the last request. New selections are limited to one response per
second; unchanged selections receive one response every five seconds. Every frame
has the existing UART pacing. No second COM owner is created.

The existing M4.6 panel never sends this new request. Therefore the candidate host
sends it **no unsolicited detail frames**. A new panel with an older host shows
an explicit waiting message; this is not treated as completed hardware acceptance.

## Frame

`type=opsdeck.details.v1`; maximum serialized UTF-8 size 3000 bytes. Required fields:
`generation` (8 lower hex), `test` (boolean), `slot`, `kind`, `requested_page`,
`page`, `total_pages`, `request_id`, `account_name` (22 ASCII), `key` (16 lower hex),
`title` (48 ASCII), `scope` and `note` (80 ASCII each), `state` (0..6), `age_s`, `rows`.
A row has `label` (28 ASCII), `unit` (12 ASCII), and required `value`: number or
explicit JSON null. There are 0..4 unique metric labels per card. Numeric range is
finite 0..1e16. Zero is measured zero; null is missing. The panel displays missing
values as `--`; numeric values use compact six-significant-digit formatting.

`total_pages=0` means no cached display card, not a confirmed zero resource count.
It requires no rows and `age_s=-1`. A present card requires at least one row and a
nonnegative collection age. Requested pages clamp to the last available card.
Keys are opaque account-qualified hashes; full account IDs and internal errors are
not transmitted. The panel validates the exact selection/request ID before publish.
Duplicate JSON keys, mismatched selections, invalid ranges, missing fields and
invalid values leave the last accepted snapshot unchanged. Navigation clears it.

Source states use the existing enum: Setup=0, Ok=1, Error=2, Stale=3, NoData=4,
Partial=5, Denied=6. Fresh collected data has a 180-second TTL. Error/Denied are not
hidden by source age; the UI independently marks source age and USB age >=16s.
Windows lock produces no resource name or measurement, with account `Private`.
Test fixtures always carry `test=true` and the UI displays `TEST DATA`.

## Meaning and collection policy

Only values already obtained through the Windows app are projected. Opening this
page does **not** refresh them; reading the relevant category in Windows populates
its bounded cache. Restarting the host clears these detail caches. This milestone
does not add a persistent cache or a background detail-polling policy.

Worker traffic and latency use their source window. D1 query counts and billed row
signals stay separate. D1 metadata is explicitly identified as metadata at collection.
R2 operations and storage are separate cards; storage shows its actual snapshot
UTC date, distinct from host collection age (default jurisdiction only).
Queue backlog is approximate; oldest-message age is the value at collection, not
an invented continuously measured age. Queue history and AI totals cover observed
groups only. Missing hours are not filled with zero; observed hour coverage is shown.
No percentile/latency averages are fabricated. Workers AI and AI Gateway are
separate categories and are never added together or presented as billing.

## UI and acceptance boundary

Native LVGL page, 776x344 body within the 800x480 display, seven bottom tabs.
Account selection, category cycling, previous/next card. Names are bounded ASCII;
full Unicode names and historical charts remain available in the Windows app.
Labels copy their text and use bounded, single-line ellipsis. API references used:
LVGL 9.3 label and button documentation (lvgl.io/docs/open/9.3/details/widgets/label
and /button). No library version upgrade.

C# tests, exact-C parser tests with OS/time shims, cross-language fixtures, source
geometry/font-width checks and an ESP-IDF compile are software evidence only.
Flash, real touch/rendering, serial reconnection, sleep/lock/unlock, sensor/Cloud
regression on the physical panel and soak remain separate acceptance gates.
