# maschine-ModProfiler

In-game profiler for SPT modeled after **Dubs Performance Analyzer** (RimWorld):
shows live how much CPU time each installed client mod costs per frame — to find the
culprit behind performance drops without having to disable mods one by one.

![The profiler window over a running game](preview.png)

## Usage

- **F11** (configurable): open/close the profiler. The first activation instruments
  all mod code and can freeze the game for a few seconds — that is normal.
- If the [Anvil-WebOverlay](https://github.com/maschine34675/WebOverlay) library is
  installed (1.8.8 or newer), the profiler opens as **its own window on top of the
  game** (HTML UI):
  draggable with remembered position/size, sorting via column headers, filter field,
  per-mod trend sparklines and a frame history in the header. **Esc** or the toggle key
  closes it. A mouse mode is unnecessary here: while the window has focus it captures
  mouse and keyboard itself; one click into the game returns both.
- Without the library — or with `UI/PreferWebOverlay = false` — the previous
  **IMGUI overlay** appears. There, the mouse cursor is left untouched on opening and you
  can keep playing normally. The **"Mouse"** button in its toolbar activates mouse mode:
  cursor free, all game controls blocked (shooting, movement, mouse look), so you can
  safely click inside the window; the same button switches back. If that block could not
  be installed — a game update changed what it hooks — the mod says so in the log at
  startup; the cursor is still freed, but the game keeps reacting.
  In menus (cursor already free), hovering over the window is enough: clicks then no
  longer reach the game UI underneath.
- Table: one row per mod with **avg ms/frame**, **max ms**, **calls/frame**, **% frame**
  and **Σ ms** (accumulated since activation). Clicking a column header sorts.
  The web window additionally shows **µs/call** (average cost of a single call):
  the profiler measures its own overhead on this machine at every scan (see
  [Limits](#limits-important-for-interpretation) below), and values within twice
  that calibrated floor — 2 µs until a scan has calibrated — are mostly measurement
  cost; such rows are grayed out, and their ranking is not reliable either. In the
  web window, all column headers, metrics and buttons also explain themselves via
  tooltips.
- Clicking a mod name expands the mod's most expensive methods
  (`[H]` Harmony patch, `[U]` Unity frame method, `[C]` coroutine/async).
- A **▲** marker flags a mod and its method (both UIs) whose calls per frame
  keep climbing without falling back — for Update/LateUpdate that is the live instance
  count, for FixedUpdate the count per physics step. Sampled every 20 measured
  seconds; confirmed once the lowest values have kept rising, never falling, for
  about 4 minutes and at least doubled, or for about 10 minutes with a smaller
  rise. Once confirmed, the marker is held for about 3 minutes after the last
  reconfirmation and fades on its own if the climb does not continue that long;
  it also clears at once if a sample drops back below where it was confirmed.
  It survives closing and reopening the window; only **Reset** clears it. The web
  window shows the exact numbers in a tooltip, the IMGUI overlay prints them on a line
  under the method: `growing: a → b per frame over n measured min`.
- A second header line in both windows shows the **GC mode, heap size and allocation
  rate** — see [GC & heap](#gc--heap) below — and amber ticks on the web window's
  frame graph mark intervals in which a collection ran. A **spike log** records frames
  slower than
  `UI/SpikeThresholdMs` (default 30 ms, and at least 1.5× the current average) together
  with the top measured contributors of that exact frame and, for each one, its own most
  expensive method in that frame; a green **GC** mark on the line says the garbage
  collector ran in it — a spike without contributors says whether a collection ran, and
  otherwise points at the engine or unmeasured code.
- **Pause/Resume**: stop measuring, window stays open. **Rescan**: picks up patches
  created later (e.g. at raid start — press it once during the raid!).
  **Reset**: zero the counters. **CSV**: writes the current totals into
  `BepInEx/plugins/maschine-ModProfiler/exports/<session>/` — one folder per game
  session — as `modprofiler-<time>.csv` plus a `.meta.json` sidecar of the same
  name; see [CSV export](#csv-export) below. The button answers "writing
  `<file>`..." right away, then the status line shows "CSV written: `<path>`" once
  the background write has finished, so a slow disk does not hold up the game.
- **Auto-export**: set `CSV/AutoExportIntervalSeconds` (default 0 = off; values below
  10 count as 10) to also write a CSV/sidecar pair on its own, into the same
  per-session export folder as the button above, every that many seconds of
  *measured* time — a closed or paused window does not add up, unless a
  [headless capture](#headless-capture) is measuring. The status line then
  shows "auto-export written: `<file>`"; a successful auto-export is logged at debug
  level only, a failing one once per distinct error, and retried after a full
  interval.

## GC & heap

Both UIs show these live numbers on a header line of their own, and every CSV
sidecar records them:

- **GC mode** (`on`/`off`): Unity's garbage collector, read directly from
  `UnityEngine.Scripting.GarbageCollector.GCMode` every frame — never cached, so a mod
  that flips it mid-raid (ArenaMode is a known case) shows up on the very next sample.
  While it is on, the collection rate (collections per second over the last 150
  frames) stands next to it; the sidecar carries the count since the last Reset
  (`gc_collections_since_reset`) instead.
  EFT itself switches the collector off for the whole raid on machines with 25 GB of
  RAM or more (`InGameMemoryManagement.GCEnabled`). **With the collector off, the heap
  number only ever grows** — that is expected, not a leak; watch the allocation-rate
  figure for anomalies instead. A GC mode change is also logged once, at most every
  5 seconds.
- **Heap size** (`GC.GetTotalMemory(false)`) and its **peak since the last Reset**.
  Never forces a collection to read it — that would falsify the very "is it running"
  picture this panel exists to show.
- **Allocation rate**, MB/s, averaged over the last 150 frames like every other
  windowed number here. Roughly 7 MB/s is normal in an Arena deathmatch. The source is
  chosen once at startup: `GC.GetAllocatedBytesForCurrentThread()` where the runtime
  supports it - EFT's Boehm collector reports 0 for that counter, so on EFT the rate
  comes from per-frame deltas of the heap size instead, shown with a "(heap delta)"
  suffix: exact while the collector is off (the heap only grows then), a lower bound
  while it is on (a frame in which a collection ran contributes 0). The sidecar names
  the source in `alloc_source`. Only if even the heap size cannot be read do both UIs
  show `n/a` and the sidecar write `alloc_mb_s` as JSON `null`, never a silently wrong `0`.

## What is measured

1. **Harmony patches**: every prefix/postfix/finalizer method a mod has patched onto the
   game is itself wrapped with a stopwatch via Harmony and attributed to the assembly
   (= mod).
2. **MonoBehaviours**: `Update`/`FixedUpdate`/`LateUpdate`/`OnGUI` of all components
   defined in mod assemblies.
3. **Coroutines & async**: `MoveNext` of the compiler-generated state machines from
   mod assemblies.
4. **A curated set of the game's own per-frame methods**, labeled `EFT (vanilla)` —
   not a mod: `EFT.GameWorld.DoWorldTick`, `EFT.GameWorld.DoOtherWorldTick`,
   `AICoreController.Update` (bot goal/layer selection), `EFT.BotOwner.UpdateManual`
   (bot mechanics), `EFT.UI.InteractionButtonsContainer.Update` (the loot/door
   prompt) are five independent per-frame loops, so their sum is honest.
   `EFT.Player.ComplexUpdate` and `EFT.Ballistics.BallisticsCalculator.ManualUpdate`
   are measured too, but each is fully covered by those rows (`DoWorldTick`'s
   `PlayerTick` or the sibling `DoOtherWorldTick` for `ComplexUpdate`;
   `DoWorldTick`'s `BallisticsTick` for `ManualUpdate` — SPT has no per-player
   ballistics path, the hands controllers it runs inherit an empty
   `BallisticUpdate`), so both are
   nested rows (flagged `nested` in the CSV, prefixed `↳` in both UIs, tooltip
   names the covering row or rows by the exact name the row above is shown under)
   — they do not add to any row's total, and must not be added to one by hand
   either. Any mod Harmony patch attached directly to one of these seven methods
   is included in its number: this is not isolated engine code, the timer wraps
   outside every other patch on the same method the same way it does for mod
   code. A curated method that has recorded no calls once a raid has been measured
   for a minute is named once in the log — usually harmless (the loot/door prompt
   stays idle in an Arena match), otherwise a sign the game inlined it before the
   timer could attach. Turned off with `Profiling/VanillaEngineMethods`.

## CSV export

Each export writes two files into
`BepInEx/plugins/maschine-ModProfiler/exports/<session>/`, where `<session>` is
the game's start time (`yyyyMMdd-HHmmss`) — every export of one game session
lands in the same folder. A second export landing in the same second gets a
`_2` suffix instead of overwriting the first, which keeps sorting by file name in
export order:

- `modprofiler-<yyyyMMdd-HHmmss>.csv` — one row per instrumented method.
- `modprofiler-<yyyyMMdd-HHmmss>.meta.json` — the sidecar described below.

### Columns

| Column | Meaning |
|---|---|
| `mod`, `category` | as in the table above; `category` is `Harmony` (a prefix/postfix/finalizer another mod applied), `Unity` (`Update`/`FixedUpdate`/`LateUpdate`/`OnGUI` of a MonoBehaviour), `Coroutine` (`MoveNext` of a coroutine or async state machine), or `EFT` (one of the seven curated `EFT (vanilla)` engine methods — see [What is measured](#what-is-measured)). |
| `kind` | `prefix` / `postfix` / `finalizer` / `Update` / `FixedUpdate` / `LateUpdate` / `OnGUI` / `MoveNext`; for `category=EFT` rows: `WorldTick` / `BotAI-Goals` / `BotAI-Mechanics` / `UI` / `PlayerUpdate` / `Ballistics` (one per curated method, see [What is measured](#what-is-measured)). |
| `method` | `Class.Method`, the same form as the table, but without the `(prefix -> target)` or `(coroutine/async)` suffix. |
| `target` | the patched original method(s) of a Harmony patch; `;`-separated when one patch method is attached to several originals. |
| `method_token` | the method's metadata token. Unique only together with `mod` — tokens are assigned per assembly, so different mods reuse the same token. |
| `frames` | frames this row has seen. A row added by a later Rescan has seen fewer frames than one instrumented from the start. |
| `calls`, `total_ms` | cumulative since the last Reset, as in the table. |
| `ms_per_frame`, `calls_per_frame` | `total_ms`/`calls` divided by this row's own `frames` — not a session-wide total. |
| `avg_us_per_call` | replaces the old `avg_ms_per_call`, in microseconds. |
| `max_ms_in_frame` | the most expensive single frame for this row since Reset, main thread only. |
| `off_thread_calls`, `off_thread_ms` | the share of calls and time measured on a thread other than the main thread. |
| `calls_delta`, `total_ms_delta`, `ms_per_frame_delta`, `calls_per_frame_delta` | since the previous export (see Delta semantics below); empty for rows flagged `new`. |
| `flags` | space-separated, see below. |
| `csv_format` | constant `2`, so a script can recognize the file shape even without the sidecar. |

### Flags

| Flag | Condition |
|---|---|
| `idle` | 0 calls, and not `maybe-inlined`. |
| `maybe-inlined` | 0 calls, although another patch on the same original method does have calls — the detour likely never fires because a caller was compiled (and inlined the target) before the patch was applied, or the patch failed. |
| `rare-heavy` | `calls_per_frame` below 0.01 and `max_ms_in_frame` at or above 10 ms: few calls, at least one of them a stall. The profiler cannot tell a blocking wait on the main thread from real computation — both stall the frame the same way. |
| `off-thread` | `off_thread_ms` is at least half of `total_ms`. |
| `multi-target` | `target` lists more than one method; `calls` and `total_ms` are summed over all of them. |
| `growing` | the growth marker (see [Usage](#usage) above) is currently active for this row: its calls per frame have kept climbing without falling back. Only `Update`/`LateUpdate`/`FixedUpdate` rows can carry it. The sidecar's `growing` list (below) gives the exact from/to per-frame values and how long it has been climbing. |
| `new` | added since the previous export; the delta columns are empty. |
| `nested` | this row's time is already counted inside one of two parent rows (currently only the two `EFT (vanilla)` child rows, `EFT.Player.ComplexUpdate` and `EFT.Ballistics.BallisticsCalculator.ManualUpdate` — see [What is measured](#what-is-measured)). A script that sums `total_ms` across all rows must exclude rows flagged `nested`, or the same time is counted twice. |

Rows with 0 calls are kept in the file on purpose: a constant row count
across exports proves two exports are comparable, and the rows document what
was instrumented even when it never ran.

### Sidecar (`.meta.json`)

Alongside the CSV, per export: `format` (2), `profiler` version, `session_id`,
`export` (time, index, `trigger` — `manual` for the CSV button, `auto` for the
auto-export timer, `headless-start` / `headless-stop` for the two exports that
open and close a [headless capture](#headless-capture) — `dir`, the session
folder, and the csv file), `game` (Unity, EFT, SPT, WebOverlay versions),
`machine` (CPU, GPU, RAM, resolution, refresh rate, VSync, target FPS),
`measurement` (frames, seconds, average FPS, active, `headless` — whether a
headless capture was running — reset/rescan counts,
instrumented method count, idle row count, patch-failure count, GC collections
since Reset, `timers_intact` — false when another mod's global Harmony
`UnpatchAll()` has removed the profiler's own timing patches; the next Rescan
reinstates them — `frame_start_drain`, see Limits,
`overhead_floor_us_per_call` / `overhead_detour_us_per_call`, the same
calibration numbers as the log line at each scan, `null` if calibration
failed, and `gc_mode_enabled` / `heap_mb` / `heap_peak_mb_since_reset` /
`alloc_mb_s` / `alloc_source` — the [GC & heap](#gc--heap) panel's numbers at export
time, `alloc_source` being `per-thread-counter` or `heap-delta` (a lower bound while
`gc_mode_enabled` is true), `alloc_mb_s` being `null` instead of `0` only if neither
source can be read),
`delta` (since export or Reset, frames, seconds, previous file,
validity), `mods` (one entry per mod, see below), `spikes` (the last 200 entries of the spike log, each with its `gc`
flag and its top contributors — every contributor also carries its own most
expensive `method` and `method_ms` in that frame; the web window shows the
latest 20), `growing` (one entry per row currently marked, with `mod`,
`method`, `from_per_frame`, `to_per_frame` and `measured_minutes`), and
`patch_failures` (mod, method and reason for every method Harmony could not
wrap). `game.eft` is the client build as the executable reports it.

`mods` is the CSV added up per mod, by the CSV's own rules, so a per-mod number
does not need the CSV at all. It has one entry for every mod that has rows in
the CSV (the mod count in the log lines also includes mods without any):
`name`, `methods` (the rows that were added up —
rows flagged `nested` are left out, their time is already inside another row),
`methods_new` (how many of them are flagged `new`), `calls`, `total_ms`,
`ms_per_frame` and `calls_per_frame` (since the last Reset, the sum of the mod's
rows in those columns), and `calls_delta`, `total_ms_delta`,
`ms_per_frame_delta`, `calls_per_frame_delta` (since the previous export, over
the rows that have a delta; `null` when none of the mod's rows has one). The
window's own averages and peaks are not in it: they cover the last 150 frames
only and would not match an interval of any other length.

### The row key and delta semantics

A row's key is `(mod, method_token)`, not `method_token` alone — the token is
only unique within its own assembly.

The `*_delta` columns are the change since the previous export. If a Reset
happened in between, they are since that Reset instead, and never negative;
the sidecar's `delta.since` says which one applies. For the very first
export of a session they are since the start. Rescanning does not invalidate
existing deltas; a method added by a Rescan gets `new` with empty delta
fields until the export after it.

### Number format

Numbers use a period as the decimal separator (invariant culture). A
German-locale Excel shows them as text on a plain double-click open; use
"Data > From Text/CSV" to import the file correctly instead.

## Headless capture

For automated test runs: measuring without opening the window, switched by a
setting instead of the hotkey, with the results read from the export files.
Nothing here is needed for ordinary use, and nothing changes while the setting
stays off.

`Capture/Headless` — an advanced setting, off by default — starts a capture when
it is switched on and ends it when it is switched off, from the F12 menu or from
any tool that changes BepInEx settings in the running game. It applies to the
running game only: a value still on when the game starts is switched back off,
with a line in the log, so one left behind in the settings file never starts a
capture on its own.

- **Start.** All mod code is scanned first — the scan of the first window
  opening, or a Rescan if one has run before, so patches other mods applied in
  the meantime are covered. That freezes the game for a few seconds. Then an
  export is begun (`trigger` `headless-start`) and the log says
  `headless capture running: N methods across M mods, exports in <session> from
  index <n>, pointer file latest-<machine>.json, ...`. Measuring runs from that
  line on; a script should wait for it rather than for a fixed time. The files
  of that first export follow a moment later.
- **While it runs.** Either window can be opened and closed; neither ends the
  capture or clears anything. **Pause** is disabled, because it could not stop a
  capture. **Rescan** works as usual. **Reset** works too, but it starts the
  counters over in the middle of the series (see below).
  `CSV/AutoExportIntervalSeconds` adds an export every interval, the first one a
  full interval after the start; at 0 only the start and the final export are
  written.
- **Stop.** A final export is written (`trigger` `headless-stop`), and once its
  files exist the log says `headless capture stopped: final export <path>`. If
  that export cannot be written, the log says `headless capture ended without a
  final export ...` instead — an error line that deliberately does not contain
  `stopped`.

Every export of a capture also replaces one more file,
`exports/latest-<machine>.json`: a copy of that export's sidecar under a name
that does not change, so a script has one file to poll. The machine name is
part of it because an exports folder that several computers share (a synced
install) would otherwise have them overwrite each other's file. It is replaced
as a whole, never written in place; if something else holds it open, the
profiler waits briefly and, failing that, leaves the old file and logs a
warning — the export itself is not affected.

For a script reading it:

- Take the session folder and the index from the `running` line and accept only
  a file whose `export.dir` is that folder and whose `export.index` is that
  index or higher. Do not compare `export.time` with another machine's clock.
  After a crash the last file stays behind and looks valid.
- Retry a file that is missing, locked, empty or does not parse, and expect a
  network share to show a change some seconds late.
- `mods` has the per-mod numbers (see the sidecar section above); the first
  export of a capture is its baseline, so every later export's `*_delta` values
  cover exactly the time since the export before it.
- The end of a capture is `export.trigger` `headless-stop`. In that export
  `measurement.headless` is false, and `measurement.active` is false unless a
  window is still measuring. The path in the `stopped` line is that export's
  CSV, with its sidecar next to it; should the line end in `(pointer file not
  updated)`, read that sidecar instead of the pointer file.
- A **Reset** during a capture raises `measurement.reset_count`; the next
  export's deltas are since that Reset and `delta.valid` is false.
- Switch it on inside the raid, not in the menu before it: some mods only apply
  their patches at raid start, and a capture started earlier does not see them
  (`rescan_count` and `instrumented_methods` show whether a later scan added
  any). Switching it off and on again scans again.
- Switch it off and wait for `headless capture stopped: final export` before
  closing the game. Quitting with the capture still running writes no final
  export.

## Limits (important for interpretation)

- **Transpilers** run only once, at patch time; their runtime cost lives inside the
  original method and cannot be attributed.
- **Indirect costs are now only partly captured.** A mod that spawns more bots or
  generates more loot causes load in the game engine (AI, world tick, GC), not in its
  own code — the `EFT (vanilla)` rows (see [What is measured](#what-is-measured)) give
  a partial answer for bot AI and world-tick load, and [GC & heap](#gc--heap) gives one
  for the collector. Rendering, culling and physics outside those seven curated methods
  stay invisible. If everything is green here but FPS still drop, it can still be such
  a mod or the base game — compare with the mod on/off, or use the SimpleMonoProfiler
  from [BepInEx.Debug](https://github.com/BepInEx/BepInEx.Debug).
- **A blocking GC still lengthens whichever sample happens to be running** when it
  fires — which method's stopwatch absorbs the pause is essentially random, so an
  occasional max outlier can be the GC misattributed to an unrelated method, not that
  method's own cost. [GC & heap](#gc--heap) now shows the collector's mode, the heap
  size and the allocation rate, and the web window's amber frame-graph ticks still mark
  exactly which intervals had a collection — but the profiler still cannot say which
  specific measured sample absorbed a given pause.
- **The calibration floor is a per-call lower bound, not a per-frame budget.** It is
  paid once per curated `EFT (vanilla)` call that ran or is nested that frame —
  `DoWorldTick` + `DoOtherWorldTick` + `AICoreController.Update` +
  `InteractionButtonsContainer.Update` (each once globally) + one `BotOwner.
  UpdateManual` per living bot (`BotsList.UpdateByUnity` calls it once per entry,
  not once globally) + one `ComplexUpdate` per living player +
  `BallisticsCalculator.ManualUpdate` (once globally through `DoWorldTick`'s own
  `BallisticsTick`). A frame with active combat and several bots therefore pays
  it well beyond seven times, scaling with bot/player count rather
  than a fixed number — with no automatic correction applied for it.
- Times are **inclusive**: if measured code calls other measured code, the time counts
  twice; the sum can therefore exceed 100% of a frame. This also applies across mods —
  a measured prefix of mod B on a measured method of mod A counts in both rows.
- **Coroutine and async rows** measure only the code between two `yield`/`await`
  points — never the waiting itself. A row with very few calls and a large
  `max_ms_in_frame` (see [CSV export](#csv-export) above) is therefore a genuine
  single-frame stall, not a multi-frame wait — and the profiler cannot tell that
  stall apart from a blocking wait on the main thread (`Task.Wait()`, `.Result` on
  an unfinished task); both freeze the frame the same way. Such rows are usually
  flagged `rare-heavy`.
- **Off-thread work** (async continuations, worker threads) is counted separately
  per row — `off_thread_calls` and `off_thread_ms` in the CSV export — and excluded
  from `max_ms_in_frame`, so a worker-thread call that runs for several seconds
  across many frames no longer looks like a single frame's stall.
- **Spike contributors** are matched to their frame by collecting the measurements at
  the very start of each frame, from a hook in Unity's player loop. If another mod
  resets the player loop so that the hook cannot stay, the profiler collects in its own
  Update instead and says so in the log, and the export sidecar reports
  `frame_start_drain` as false. Code that runs before the profiler in a frame can then
  be named one frame late.
- **One-time lifecycle methods** (Awake/Start/OnEnable/OnDestroy) are not captured —
  a mod's loading and raid-start spikes stay invisible.
- The **instrumentation persists until the game is restarted**: even with the window
  closed or paused, all captured methods keep their (small) Harmony detour;
  only the timing itself is skipped. For before/after measurements of other mods,
  restart once without opening the profiler.
- **Patch failures** (the amber counter in the header) are methods Harmony could not
  wrap — most often because another mod's patch on the same method already uses a
  `__state` of a different type. The originals are untouched, but their cost is
  invisible here, so such a mod looks cheaper than it is. In the web window the
  counter is clickable and lists mod, method and Harmony's reason; the CSV export's
  sidecar carries the same list under `patch_failures`, plus the count, for offline
  reports.
- Very small patch methods may have been inlined by the JIT — the detour is then
  never reached, so they show up with 0 calls, not a low time. The CSV export flags
  such a row `idle`, or `maybe-inlined` when a sibling patch on the same original
  method does have calls (see [CSV export](#csv-export) above).
- Every measured call carries a small fixed cost, and how large it is depends on the
  machine it runs on. At every scan the profiler measures it for itself instead of
  assuming a number: the **floor** (the share of that cost landing inside the measured
  value, so no row can read below it) and the **detour** (the total extra cost of an
  instrumented call, paid by the game around it and only entering a row when it wraps
  other measured calls). Both are lower bounds — one probe method called in a tight
  loop is the best case, which a real frame spread over 1,500+ instrumented methods is
  not. The numbers are written to the log once per scan and to the CSV sidecar as
  `measurement.overhead_floor_us_per_call` / `overhead_detour_us_per_call` (see
  [CSV export](#csv-export) above); the web window greys out rows within twice the
  floor.
- **The growth marker** cannot see a leak hidden inside a population that swings by
  several instances — an ordinary loss can mask the climb. Components that are
  disabled instead of destroyed are invisible to it, since Unity stops calling their
  `Update`. And a population that genuinely follows bots or players nearby can be
  marked while it climbs; it clears again once the count falls back.

## Requirements and compatibility

- **SPT:** built and tested on 4.1.x, single player. Client-side only, no server
  component. Only one piece touches the game itself — the input mute described under
  Usage looks up `EFT.InputSystem.InputManager` by name — so a game update can disable
  that one feature (a warning appears in the log); the profiling itself keeps working.
  Other SPT lines are untested.
- **Optional:** [Anvil-WebOverlay](https://github.com/maschine34675/WebOverlay) 1.8.8
  or newer for the web window. Without it — or with an older version, which is
  reported in the log — the built-in IMGUI overlay is used instead. The web window
  additionally needs the Microsoft WebView2 runtime, which current Windows 10 and 11
  installations already include, and borderless windowed or windowed mode.
- **Fika (co-op):** not tested. The mod only measures and draws locally; it changes
  no game state and sends nothing over the network.

## Installation

Extract the zip over the SPT game directory (contains
`BepInEx/plugins/maschine-ModProfiler/`). Nothing else to set up: the profiler stays
idle until you press its hotkey.

## Configuration

Through the in-game config manager, or `BepInEx/config/com.maschine.ModProfiler.cfg`:

In the order the F12 menu shows them:

| Option | Default | Meaning |
|---|---|---|
| `General/ToggleOverlay` | F11 | Opens and closes the profiler. |
| `UI/PreferWebOverlay` | on | Use the web window when the library is installed. Read once at startup. |
| `UI/RefreshInterval` | 0.5 s | Seconds between table refreshes. |
| `UI/TopMethodsPerMod` | 15 | How many methods an expanded mod row lists. |
| `UI/SpikeThresholdMs` | 30 ms | Frames slower than this enter the spike log; 0 disables it. |
| `CSV/AutoExportIntervalSeconds` | 0 s (off) | Writes a CSV export on its own every this many seconds of measuring (closed or paused time does not count, except during a headless capture), into the same folder as the CSV button; values below 10 count as 10. |
| `Capture/Headless` | off | Advanced, only shown with the menu's advanced settings: measures without the window until switched off again — see [Headless capture](#headless-capture). Always off when the game starts. |
| `Profiling/HarmonyPatches` | on | Measure the Harmony patches other mods applied. |
| `Profiling/MonoBehaviours` | on | Measure mod `Update`/`FixedUpdate`/`LateUpdate`/`OnGUI`. |
| `Profiling/CoroutinesAndAsync` | on | Measure coroutine and async state machine steps. |
| `Profiling/IncludeSptCorePlugins` | on | Also profile the SPT core plugins. |
| `Profiling/VanillaEngineMethods` | on | Measure the curated `EFT (vanilla)` engine methods — see [What is measured](#what-is-measured). |

Two things do not take effect immediately. `UI/PreferWebOverlay` is read once at
startup, so changing it mid-session does nothing — restart the game. And changing
`Profiling/*` only affects methods that have not been instrumented yet; instrumentation
already applied also stays until the game restarts.

## Support

Report problems on the [issue tracker](https://github.com/maschine34675/ModProfiler/issues)
with your exact ModProfiler and SPT versions, what you expected and what happened,
short reproduction steps, and your complete `BepInEx/LogOutput.log` — not thousands of
pasted lines. A CSV export together with its `.meta.json` sidecar (the **CSV** button
writes both) is the most useful attachment for anything about the numbers themselves.

## Build

```
dotnet build -c Release
```

The project resolves BepInEx, Harmony and the Unity assemblies through relative paths
two levels up, so the checkout has to sit inside an SPT installation — clone it as
`<your SPT folder>/Development/ModProfiler`, or adjust the `HintPath`s. The optional
`Anvil-WebOverlay.dll` reference resolves from
`<your SPT folder>/BepInEx/plugins/Anvil-WebOverlay/`; without that library installed the
build fails, since the web window is compiled against it (it stays optional at runtime).

The tests cover the parts that need neither Unity nor the game and run without
building the plugin — always give the path:

```
dotnet test tests/ModProfiler.Tests
```

A successful build copies the DLL to `..\..\BepInEx\plugins\maschine-ModProfiler\` and
creates the release zip next to the project file. Pass `-p:SkipDeploy=true` to build
without that copy step — useful for a scratch/worktree build that must not touch a live
install.

## License and credits

MIT, see [LICENSE](LICENSE). The approach — wrapping other mods' Harmony patches with
a stopwatch instead of profiling the engine — is taken from **Dubs Performance
Analyzer** for RimWorld; no code is shared with it. For what this profiler cannot see,
the **SimpleMonoProfiler** from [BepInEx.Debug](https://github.com/BepInEx/BepInEx.Debug)
is the complement.
