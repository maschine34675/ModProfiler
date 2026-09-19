# Changelog

## [Unreleased]

## [1.3.0]

### Forge version notes

- A new **GC & heap panel**, in both UIs' headers and in the CSV sidecar,
  answers "is this heap growth a leak, or did the game just switch its
  garbage collector off" - EFT turns the collector off for the whole raid
  on machines with 25 GB of RAM or more, and once it is off the heap
  number only ever grows. The panel shows the collector's live on/off
  state (it follows a mid-raid switch, e.g. by ArenaMode, on the very
  next sample), the managed heap size and its peak since the last Reset,
  and the allocation rate.
- A new curated **`EFT (vanilla)`** row measures seven of the game's own
  per-frame methods next to your mods - world tick (both of the
  engine's alternating per-frame call trees), bot AI, player update,
  ballistics, the interaction prompt - answering "is this stutter my
  mod, or the base game itself". Honestly labeled: it includes any mod
  Harmony patch attached directly to the same method, and two of the
  seven rows (`Player.ComplexUpdate`, `BallisticsCalculator.ManualUpdate`)
  are flagged as already counted inside one of the other five top-level
  rows (`ComplexUpdate` under whichever of the two world-tick trees
  produced the call, `ManualUpdate` under `DoWorldTick`) rather than
  silently summed into an inflated total.

### Added

- GC mode (on/off, read live every frame), managed heap size, its peak
  since the last Reset, and the allocation rate (MB/s, averaged over the
  last 150 frames) on a header line of their own in both UIs and in the
  CSV sidecar's `measurement` object (`gc_mode_enabled`, `heap_mb`,
  `heap_peak_mb_since_reset`, `alloc_mb_s`, `alloc_source`). The rate's
  source is chosen once at startup: the per-thread allocation counter
  where the runtime supports it, otherwise - and that is the case on
  EFT, whose Boehm collector reports 0 for that counter - per-frame
  deltas of the heap size, marked "(heap delta)" in both UIs and
  `heap-delta` in the sidecar: exact while the collector is off, a lower
  bound while it is on. Only if neither can be read do both UIs show
  `n/a` and the sidecar write `alloc_mb_s` as JSON `null`, never a
  silently wrong `0`. A GC mode change is logged once, at most every 5
  seconds.
- An `EFT (vanilla)` mod row: `GameWorld.DoWorldTick`,
  `GameWorld.DoOtherWorldTick`, `AICoreController.Update`,
  `BotOwner.UpdateManual` and `InteractionButtonsContainer.Update` are
  five independent per-frame loops, summed as the row's own total;
  `Player.ComplexUpdate` and `BallisticsCalculator.ManualUpdate` are
  measured too, but each is fully covered by those rows (`ComplexUpdate`
  by `DoWorldTick`'s own tick or the sibling `DoOtherWorldTick` call tree
  the engine also reaches it from, `ManualUpdate` by `DoWorldTick`'s
  `BallisticsTick`) and so do not add to that total. `DoOtherWorldTick`
  exists specifically so that coverage is complete - without it, half of
  `ComplexUpdate`'s real cost would be silently dropped from the vanilla
  total instead of counted once. There is no per-player ballistics row:
  SPT never creates the online client's `ClientFirearmController`, and
  the hands controllers it does run inherit an empty `BallisticUpdate`.
  New `EFT` category value and `nested` flag value in the CSV export. A
  script that sums `total_ms` across all CSV rows should exclude rows
  flagged `nested` to avoid counting that time twice.
- `Profiling/VanillaEngineMethods` config entry (default on) turns the
  `EFT (vanilla)` bucket off.
- A one-time log warning, once a raid has been measured for a minute
  (time in the menu and the hideout does not count), for any curated
  `EFT (vanilla)` method that has recorded no calls at all - either the
  JIT inlined it before Harmony could attach the timer, or nothing in
  that raid ran it (the loot/door prompt stays idle in an Arena match,
  the bot rows in a raid without bots).

### Changed

- The web window's GC collection rate is now computed by the game, over
  the same 150-frame window as the other header numbers, instead of
  accumulated in the page from raw counter deltas, and shows a dash
  while the collector is off. The overlay window, which had no GC figure
  before, shows the same number.

## [1.2.0]

### Forge version notes

- A **growth marker** (▲) now flags a mod or method whose per-frame call count -
  its live instance count - keeps climbing for several minutes without falling
  back: an answer to a leak too cheap in milliseconds to show up anywhere else.
- CSV exports can now write themselves on a timer instead of only on a button
  press (`CSV/AutoExportIntervalSeconds`), so a long raid still leaves a trail of
  exports if you forget to click.
- The web window's spike log now names each contributor's own most expensive
  method in that frame, and marks a frame in which the garbage collector ran.
- The profiler now measures its own measurement overhead on your machine at
  every scan instead of assuming a number; the web window's µs/Call column
  greys out against that measured floor.

### Added

- `CSV/AutoExportIntervalSeconds` (display name "Auto-Export Every (s)",
  default 0 = off, values below 10 count as 10): writes a CSV/sidecar pair on
  its own every that many seconds of measured time (a closed or paused window
  does not count) into the same per-session export folder as the CSV button.
  The sidecar's `export.trigger` records `manual` or `auto`, the status line
  shows "auto-export written: `<file>`", a successful write is logged at debug
  level only, and a failing one is logged once per distinct error and retried
  after a full interval.
- Growth marker: samples every Update/LateUpdate/FixedUpdate row's calls per
  frame (FixedUpdate: per physics step) every 20 measured seconds, and marks a
  row when its lowest values keep rising, never falling - over about 4 minutes
  if the count has at least doubled, or over about 10 minutes for a smaller
  rise - holding the mark for about 3 minutes after the last reconfirmation and
  clearing it at once if a sample falls back below the confirmed floor. Survives
  closing and reopening the window; cleared by Reset. Shown as a ▲ on the mod
  and method rows in both UIs (a tooltip in the web window, a line under the
  method in the IMGUI overlay), carried in the CSV `growing` flag and the
  sidecar's `growing` list (`mod`, `method`, `from_per_frame`, `to_per_frame`,
  `measured_minutes`).
- Calibration: at every scan the profiler now measures, on the machine it runs
  on, the floor (the share of a measured call's own cost that lands inside its
  value) and the detour (the total extra cost of an instrumented call) of its
  own instrumentation. Both are lower bounds, logged once per scan, and carried
  in the sidecar as `measurement.overhead_floor_us_per_call` /
  `overhead_detour_us_per_call` (`null` if calibration failed).
- Spike log: each contributor now carries its own most expensive method in that
  frame (`method`, `method_ms` in the sidecar; shown in the web window's log
  line), and a spike entry records whether the garbage collector ran in it
  (`gc`; a green "GC" mark in the web window).
- Header (both UIs): shows the frames and time measured since the last Reset,
  and the time of the last export.

### Changed

- The web window's `µs/Call` column now greys rows against the calibrated
  measurement floor (twice the floor, or 2 µs before a scan has calibrated)
  instead of a fixed ~1 µs guess.

### Fixed

- After **Reset**, a method that runs once per frame is counted exactly once
  per measured frame again. The frame in which Reset was pressed was counted
  although part of it had already been thrown away with the old totals, so
  such a method stayed one call behind the exported `frames` for the rest of
  the session. That frame is now left out, as the frame in which measuring is
  switched on already was.

## [1.1.0]

### Forge version notes

- The CSV export explains itself now: every row carries the frames it has
  seen, so totals turn into per-frame values without guessing a denominator,
  plus deltas since the last export and flags for idle, maybe-inlined,
  off-thread and rare-heavy rows. A sidecar file next to the CSV adds
  measured seconds, the spike log and patch failures.
- Every export of one game session now lands in its own folder instead of
  piling up next to the plugin. If you have scripts reading the export: the
  files moved, `avg_ms_per_call` is now `avg_us_per_call`, and the `method`
  column lost its `(prefix -> target)` suffix in favour of a `target` column.
- The CSV button no longer waits on the write: it answers immediately, and
  the status line reports once the file is actually written, so a slow disk
  does not hold up the game.
- The spike log now names the mods that actually made a frame slow. Before,
  work that ran early in a frame was blamed on the frame before it.

### Added

- CSV export: a `frames` column per row (how many frames it has seen since
  it was instrumented - a row added by a later Rescan has seen fewer),
  `target` for the patched original method(s) of a Harmony patch
  (`;`-separated when one patch method is attached to several), `method_token`,
  `ms_per_frame` and `calls_per_frame`, `max_ms_in_frame` (main thread only),
  `off_thread_calls`/`off_thread_ms`, delta columns since the previous export
  (`calls_delta`, `total_ms_delta`, `ms_per_frame_delta`,
  `calls_per_frame_delta`, empty for new rows), a `flags` column (`idle`,
  `maybe-inlined`, `rare-heavy`, `off-thread`, `multi-target`, `new`) and a
  constant `csv_format` column.
- A `.meta.json` sidecar next to every CSV: session id, game and machine
  info, measured frames and seconds, reset and rescan counts, the last 200
  entries of the spike log, the patch-failure list with reasons, and whether
  the profiler's own timing patches are still intact (`timers_intact`).
- Exports now go into
  `BepInEx/plugins/maschine-ModProfiler/exports/<session>/`, one folder per
  game session; a second export landing in the same second gets a `_2`
  suffix instead of overwriting the first, so the files sort by name in the
  order they were written.
- Off-thread calls and time are now measured and reported per row
  (`off_thread_calls`, `off_thread_ms`) instead of only being described as a
  limitation in the README.
- The CSV button now answers "writing `<file>`..." immediately while the
  file is built and written on a background thread; the status line then
  shows "CSV written: `<path>`" once it is done. The export frame itself is
  kept out of the spike log and the frame-time window, so the export does not
  show up as a stutter of its own making.

### Changed

- `avg_ms_per_call` is replaced by `avg_us_per_call` (microseconds instead of
  milliseconds) - a script reading the old column needs a one-line change to
  the new name and unit.
- The `method` column no longer carries the `(prefix -> target)` or
  `(coroutine/async)` suffix. A Harmony patch's target method(s) now have
  their own `target` column; the coroutine/async distinction was already in
  `category`. A script matching on the old suffix needs a one-line change to
  read `target` and `category` instead.
- CSV exports move from
  `BepInEx/plugins/maschine-ModProfiler/modprofiler-<time>.csv` to
  `BepInEx/plugins/maschine-ModProfiler/exports/<session>/modprofiler-<time>.csv`
  - a script pointed at the old flat folder needs a one-line change to the
  new per-session subfolder.

### Fixed

- The spike log names the right contributors. Measurements were collected
  during the profiler's own Update but compared against the length of the
  previous frame, so everything that ran earlier in the frame - most other
  plugins among it - was blamed on the frame before. A slow frame could show
  a mod with 4 ms that had in fact taken 197 ms of it. Measurements are now
  collected at the very start of each frame, where frame length and work
  line up; if another mod removes that hook from Unity's player loop, the old
  timing takes over and the log says so.
- A measured sample that straddled Pause and Resume no longer lands as a
  false peak in the first frame after Resume.
- The frame in which a Rescan runs counts like any other frame: its samples go
  into the totals and it adds one to the frame count, so a once-per-frame
  method's calls match the exported `frames` exactly, also across rescans.
  Only its frame time stays out of the window, as before. The frame in which
  measuring is switched on is left out entirely, since only its second half
  was measured.
- The display name of a coroutine or async state machine nested inside a
  lambda no longer reads as the truncated `<Prefix`.
- The profiler's own timing patches now survive another mod calling
  Harmony's global `UnpatchAll()`: a Rescan detects the missing patches and
  reinstates them, and says so in the log.

## [1.0.1]

### Forge version notes

- Fixed: with the profiler window open you can look around again. Needs
  Anvil-WebOverlay 1.8.8 or newer for its own window; with an older library the
  built-in overlay is used instead, as before.

### Fixed

- With the profiler window open, the mouse no longer stops turning the player.
  The window covers the middle of the picture, and that is the exact point the
  game reads mouse movement from while you turn - so the movement went to the
  profiler and the player stood still, with nothing anywhere to say why. The
  mouse now reaches the game whenever the game is the window in front; press
  F11 to bring the profiler back.
- The cursor is asked for through the game's own input tree instead of being
  set directly. Writing `Cursor.visible` meant the game disagreed and set it
  back every frame, which is what made the cursor flicker; a vote in the input
  tree makes the game agree instead of being overruled.

### Changed

- Needs Anvil-WebOverlay **1.8.8** or newer for the profiler's own window,
  raised from 1.8.0 - that is the first release where the mouse fix behaves.
  This is not a break: with an older library the built-in overlay takes over,
  which is what it has always done, and the log says which case applied.
- `Diagnostics / LogInputState` is marked advanced and shows as
  **Log Input State**. It answers one kind of bug report and is of no use
  otherwise; it stays rather than being removed, because the fault it was
  written for could not be told apart from correct behaviour without it.

## [1.0.0]

### Forge version notes

- Shows live how much CPU time each installed client mod costs per frame, so you
  can find what is slowing your game down without disabling mods one by one.
- Press **F11** to open it. With the optional
  [Anvil-WebOverlay](https://sp-mod.com/mod/2879/weboverlay) library (1.8.0 or
  newer) it opens as its own window over the game, with a sortable table, a
  filter field, per-mod trend graphs, a frame-time history, the garbage-collector
  rate and a spike log that names the top contributors of a stuttering frame.
  Without the library a built-in overlay shows the core table instead.
- Read the "Limits" section of the README before drawing conclusions from the
  numbers: they rank mods usefully, they are not a frame-exact truth.

### Added

- Per-mod CPU profiling on the Dubs Performance Analyzer principle: the Harmony
  patch methods other mods applied to the game are themselves Harmony-patched
  with a stopwatch, as are the Unity frame methods and compiler-generated
  coroutine/async state machines of every mod assembly. Each sample is
  attributed to the assembly that owns the code.
- Core table in both interfaces: average and peak milliseconds per frame, calls
  per frame, share of the frame and the accumulated total, with sorting,
  expandable per-method detail, pause/resume, a rescan for patches created later
  (raid start), reset and CSV export.
- Web window through the optional Anvil-WebOverlay library: draggable with
  remembered position and size, filter field, per-mod trend sparklines, frame
  history, GC rate with markers on the frame graph, a spike log for frames over
  `UI/SpikeThresholdMs`, a clickable patch-failure counter listing the methods
  Harmony could not wrap and why, and tooltips on every column, metric and
  button. It also adds a `µs/Call` column that greys out rows whose value
  approaches the ~1 µs measurement overhead, because such a row is mostly
  profiler cost and its ranking is not reliable.
- Built-in IMGUI overlay as the fallback, with a mouse mode that frees the
  cursor and mutes the game's controls. It takes over when the library is
  missing, too old, or the window fails at runtime, with the cause in the log.
