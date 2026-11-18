# Echostep

Record and replay mouse and keyboard macros on Windows. Macros can wait for colors or images on screen,
make decisions, loop, and run on a schedule.

## Install

**[⬇ Download Echostep for Windows](https://github.com/bingermann/echostep/releases/download/v0.4.0/Echostep-Setup-0.4.0.exe)**

Run the downloaded file and click through the installer. It asks where to put Echostep: the app, your saved
macros (in `Macros`) and its settings all live in that one folder. If Windows says "Windows protected your PC",
click **More info → Run anyway** (the installer isn't code-signed).

No install wanted? Download the
[portable version](https://github.com/bingermann/Echostep/releases/latest/download/Echostep.exe)
and run it from any folder.

## What's in it

### Phase 1: v1
- **File menu:** New, Open…, Save, Save As…, Recent files and Exit. Ctrl+N, Ctrl+O and Ctrl+S work. The title shows the file name, with `*` when there are unsaved changes, and you are asked before changes are lost. You can also open a `.estep` file by passing its path as the first command-line argument.
- **Ribbon:** a tab row (File, Record and Edit, Playback, View) over the selected tab's buttons. See "Ribbon" below.
- **Action list:** columns #, Label, Action and Parameters.
  - Double-click a row or press Enter to edit it.
  - Delete removes the selected rows. Move up and Move down (or Alt+↑/↓) reorder them. Right-click for an Insert menu.
  - Select any number of rows (click, Shift+click, Ctrl+click, Ctrl+A), then Ctrl+C to copy, Ctrl+X to cut and Ctrl+V to paste below the last selected row. The clipboard holds `.estep` text, so it also works between windows and with a text editor. A pasted label that is already in use is renamed (`retry` → `retry2`), and jumps inside the pasted rows follow the new name.
  - Ctrl+Z undoes the last change to the list (insert, edit, delete, move, paste, cut, or a whole recording), up to 200 steps. Ctrl+Y or Ctrl+Shift+Z redoes.
  - Rows with a bad label or a missing jump target turn red, and their tooltip says why. Play refuses to start and names the row.
- **Record:** uses low-level hooks.
  - A click becomes Click and mouse movement becomes Move, thinned by distance. A key becomes Keypress, with Ctrl/Alt/Shift/Win folded in. A gap of more than 50 ms becomes Wait.
  - New rows go below the selected row, or at the end.
  - The following are not recorded: clicks and keys on Echostep itself (so the Stop click isn't recorded), Echostep's hotkeys, its own injected input, and the mouse moves leading to the Stop button.
- **Play:** runs off the UI thread with a row pointer.
  - Click, Move and Keypress use SendInput.
  - The row being played is highlighted, and the status line shows `Playing row N`.
  - Editing is locked while a macro plays.
- **Labels:** `start`, `end` and row numbers are built in. Row numbers are looked up when Play starts, so inserting rows changes where a numeric jump lands. Custom labels are unique and case-insensitive.
- **Goto, and Repeat:** each Repeat has its own counter, and counters reset when Play starts, so nested Repeat loops work.
- **Global Stop hotkey:** **Ctrl+Alt+Esc** works even while the window is hidden.

### Phase 2: pixel tools and background rules
- **Live readout:** the status bar shows the cursor x, y and the color under it at all times.
- **Picker** (View tab or **Ctrl+Alt+P**): a magnifier follows the cursor and shows the position, the color and the title of the window under the cursor. It only reads the screen; Enter and Esc are captured so they don't reach the app below. Enter creates a new pixel rule at the cursor.
- **Pick on screen… button:** every dialog that edits a position has one. The picker follows the mouse until you press Enter, which saves the position (and the color, for pixel actions). Esc cancels. The numbers can also be typed in. Tick *relative to window* to store coordinates relative to the client area of the window under the cursor, together with that window's title.
- **Test button:** pixel dialogs have one. It shows the current color and whether it matches.
- **Wait until time:** pauses until the PC clock next shows a time of day (tomorrow, if that time has already passed today). Stop interrupts it.
- **New actions:**
  - **Wait for pixel change:** continues once the pixel differs from its starting color by more than the tolerance.
  - **If pixel:** jumps to *Then* or *Else*. An empty target means the next row.
  - **If time / day / window** (Flow ▾): checks the time of day (before, after, or between two times; a range like 22:00–06:00 runs past midnight), the day of the week (any set of days), or whether a window is open or in front, then goes to *Then* or *Else* (empty = next row). The dialog's *Test now* shows which way it would go right now.
- **Rules tab:** reads and writes `rules.json`, using the spec's format.
  - Each row shows target and live color swatches, state, when the rule last fired (with a count), and an enabled checkbox.
  - Rules are edge-triggered and honour `holdForMs` and `cooldownMs`.
  - A rule's actions run on a worker task, and the rule is skipped while its actions are still running.
  - Each rule has its own action list (Goto and Repeat included). It can also run a `.estep` file through the `macroFile` field.
- **Watch** (View tab or **Ctrl+Alt+W**) pauses and resumes all rules.
- **Window-relative coordinates:** add `"window": "title"` to any Click, Move, pixel action or rule. An exact title match is tried first, then a partial one.
- **Color-distance matching:** set `"match": "distance"` to compare by Euclidean RGB distance (tolerance 0–442). This handles gradients better than the default per-channel tolerance.
- **Unreadable pixels:** a pixel that can't be read (off-screen, missing window, locked desktop) counts as *no match* and is logged once, not on every poll.
- **Crosshair:** while a macro plays, a click-through crosshair is drawn around the pixel that Wait for pixel, Wait for pixel change or If pixel is watching. Its centre is open, so it never covers the watched pixel. Turn it off in Settings.
- **Log tab:** records rule firings, timeouts, playback results and hotkey problems.
- **Settings tab:** hotkeys, recording thresholds, crosshair, watch on startup and magnifier size.

### Added from the Echostep v1 spec
Nothing that existed before was removed; where the spec and the existing app disagreed, the existing behaviour stayed and the spec's version was added beside it.

- **Start panel:** with no macro loaded, the list is replaced by *Record a new macro*, *Load a file…* and up to five recent files.
- **Recording:**
  - Record starts a new recording, which replaces the list after asking. The ▾ strip under it has *Append recording* and *Insert recording* (below the selected row).
  - A stop-recording key (default **Enter**) ends the recording and is never recorded. Ctrl+Alt+Esc still works.
  - The window hides while recording (Settings).
  - The mouse wheel and the X1/X2 buttons are recorded.
  - A *Window focus* row is recorded when another program comes to the front.
  - Optional: record buttons and keys as separate Down and Up rows, which captures drags and held keys. *Combine to click / press* merges a pair back.
  - The monitor layout is stored with the macro, and Play warns if it differs.
- **Playback:**
  - The ▾ strip under Play: *Play ignoring breakpoints*, *Play until selected*, *Play from selected*, *Play selected row* (Ctrl+Down). *Play from selected* only chooses where to start: a Repeat or Goto that jumps back above that row keeps playing. *Play selected row* runs just that row.
  - Pause and resume with the pause key (default **Esc**, real key presses only) or the Play button, which turns into Pause/Resume. *Step* runs one row while paused.
  - Breakpoints (F9) pause before their row.
  - Playback tab: mouse path (As recorded, Linear, Spline, Natural), repeat count, after-playback action (shutdown, restart, lock, log out, standby) with a cancellable 15-second countdown, and a type filter that hides and skips mouse moves, clicks, key presses, waits or focus changes.
  - The after-playback action always starts at *No action* when Echostep opens.
  - Optional abort on a real key press or mouse move (Settings).
- **Editing:**
  - Comment column. Double-click a Label or Comment cell (or F2) to edit it in the list. Renaming a label updates the jumps that pointed at it.
  - *Edit all selected*: one dialog, and only the fields you change are applied to every selected action of that type.
  - Drag rows to reorder. Delete asks first (with *Do not show again*).
  - Enable/disable rows, row background colors, groups (Ctrl+G / Ctrl+Shift+G) with a color, collapse/expand, and disable/enable for the whole group. Collapsed groups still run.
  - Find (Ctrl+F): find next, find previous, or select every matching row, in all parameters or one. Tick *Replace* in the dialog (or press Ctrl+H) to replace one or all. Go to line (Alt+G).
  - Line numbers can be hidden. Drag a column header divider to resize a column. Columns share the width in proportion, so they shrink and grow with the window, and the layout is remembered (in the Rules list too).
  - Every time setting (waits, timeouts, move durations, Repeat for a length of time, rule check/hold/cooldown, the recording wait threshold) takes an amount and a unit: milliseconds, seconds, minutes or hours, with decimals allowed. Files still store milliseconds; a time opens in the largest unit that shows it as a whole number (5000 ms opens as 5 seconds). Wait keeps the unit you chose.
- **New actions:** Wheel, Wait for screen change (a dragged zone or the entire screen, with an optional mouse action on the changed area) and Window focus (waits for a program's window, brings it to the front, optionally moves and resizes it).
- **Extended actions:**
  - Click: double-click, Down, Up, X1/X2, relative to the pointer, random wiggle.
  - Move: start point, travel time, relative.
  - Keypress: Down, Up, repeat count.
  - Wait for pixel and Wait for pixel change: a *go to* for success.
  - Repeat: a number of times, for a number of seconds, until a time of day, or forever, then an optional jump.
  - `next` is a new built-in target meaning the following row.
- **Status bar:** action count and selection, playback status with wait countdowns, elapsed play time and the macro's duration.
- **Live Value column while playing:** a Wait counts down its time; Wait for pixel / pixel change / screen change and Window focus count down their timeout; a Move with a duration counts down its travel time; Repeat shows the runs (or time) left; a Key press repeated N times counts down. When the row is done it shows its normal value again (so a Wait inside a loop starts from its full time each run), and Stop puts every row back. Wait until time is not counted down.
- **Wait for pixel in the list:** the Value column shows only the coordinates (and window, if set); the rest of the cell is filled with the color it is looking for. Its timeout counts down on the swatch while playing, and hovering the cell shows the full description (tolerance, timeout, targets).
- **Settings:** Recording, Playback, Hotkeys and User interface sections, including Start minimized and Start on login.
- **Files:** macros are `.estep` files; a new file is offered the name `Macro_<date>`. Files saved as `.omr` (OpenMR) or `.orec` before the rename still open.

Not taken from the spec: click-to-capture for positions (Enter still saves), tolerance in percent, `config.xml` (settings stay in `settings.json`) and named undo steps. Play from / until selected are on Alt+Shift+Down / Alt+Shift+Up, because Alt+Up / Alt+Down already move rows.

### More actions and playback extras

- **More** (Record and Edit tab), with these actions:
  - **Find image**: capture a button or icon once. The action waits for it (or checks once), can move to it
    or click it, and stores its center in `{foundX}` / `{foundY}`. You can limit the search to a zone (faster)
    and set a tolerance for smoothed text.
  - **Type text**: types any characters, whatever the keyboard layout. A new line presses Enter. It can pause
    between characters.
  - **Set variable**: set, add, subtract, copy from the clipboard or put on the clipboard. Write `{name}`
    anywhere text is typed to insert a value. Built-in values: `{date}`, `{time}`, `{clipboard}`.
  - **Run / open**: opens a program, file, folder or URL, and can wait until it closes.
  - **Message / sound**: plays a sound, shows a message, or asks Yes/No (Yes and No can go to different rows).
- **If → Variable**: compares a variable using =, ≠, <, > or contains.
- **Playback tab → Extras**, plus one setting:
  - **Speed** (0.25× to 10×): scales Wait and mouse-move times. Timeouts are not changed.
  - **Progress overlay** (Settings → Playback, off by default): a click-through box in the corner showing the row, status, time and pass.
  - **Macro hotkeys…**: a hotkey plays a macro file straight away, without opening it.
  - **Schedule…**: creates Windows Task Scheduler tasks that run `Echostep.exe "file" --play --exit`.

Command line: `Echostep.exe "macro.estep" --play --exit` opens the macro, plays it minimized and then closes.

### Appearance (Settings → Appearance)

- **Theme:** Dark, Light, or Match Windows (follows "Choose your app mode" in Windows settings).
- **Accent color:** Blue, Teal, Green, Purple, Orange, Pink or Red. The colors are darkened automatically in Light so text on them stays readable.
- **Text size:** Normal or Large.

Changes apply after a restart. Echostep offers to restart for you and reopens the same macro. Row and group colors
look like dark tints in Dark and pastels in Light, so a macro looks right in either theme.

### Look
Dark theme throughout: dark title bar, rounded buttons and cards, a ribbon with large icon buttons, compact icon buttons beside the lists (edit, move up/down, delete, find; tooltips give the name and shortcut), and taller list rows. Standard Windows message boxes and file dialogs keep the system look.

### Hotkeys (change them in Settings)
| Hotkey | Action |
|---|---|
| Ctrl+Alt+Esc | Stop recording or playback. Also halts running rule actions and turns watching off. |
| Ctrl+Alt+P | Color picker. |
| Ctrl+Alt+W | Pause or resume rules. |

## File formats

Macros are `.estep` files: UTF-8 JSON with one action per line, which keeps git diffs readable. A file that only uses v1 features is written as `"version": 1`. Using a Phase 2 feature (`ifPixel`, `waitPixelChange`, `waitUntil`, `window`, `match`, or a middle click) writes `"version": 2`. Hand-edited files may put `label` before `type` and may contain comments.

| type | fields |
|---|---|
| `click` | `x`, `y`, `button` (`left`/`right`/`middle`/`x1`/`x2`), optional `action` (`click`/`double`/`down`/`up`), `relative`, `wiggle`, `window` |
| `move` | `x`, `y`, optional `fromX`, `fromY`, `durationMs`, `relative`, `window` |
| `wheel` | `value` (120 = one notch; positive scrolls up/right), optional `horizontal` |
| `key` | `key` (e.g. `Enter`, `A`, `F5`, `Left`, `PageDown`), optional `ctrl`/`alt`/`shift`/`win`: `true`, `action` (`press`/`down`/`up`), `count` |
| `wait` | `ms`, optional `unit` (`ms`, `s`, `min`, `h`): the unit it is shown and edited in; `ms` is always what plays |
| `waitUntil` | `time`: local time of day, 24-hour, `"HH:mm"` or `"HH:mm:ss"`. If it has passed today, waits until tomorrow |
| `waitPixel` | `x`, `y`, `color`, `tolerance`, `timeoutMs` (0 = forever), `onTimeout` (label or `stop`), optional `then`, `window`, `match` |
| `waitPixelChange` | `x`, `y`, `tolerance`, `timeoutMs`, `onTimeout`, optional `then`, `window`, `match` |
| `waitScreenChange` | optional zone `x`, `y`, `width`, `height` (none = entire screen), `tolerance`, `timeoutMs`, `onTimeout`, optional `mouse`, `anchor`, `then` |
| `ifPixel` | `x`, `y`, `color`, `tolerance`, optional `then`, `else`, `window`, `match` |
| `repeat` | `target`, `count` (total runs of the block), optional `mode` (`count`/`seconds`/`until`/`infinite`), `seconds`, `until`, `after` |
| `goto` | `target` |
| `if` | `condition`: `time` (with `compare` `before`/`after`/`between`, `time`, and `time2` for between), `day` (with `days`, e.g. `["sat","sun"]`) or `window` (with `title`, `titleMatch`, `state` `open`/`front`); optional `then`, `else` |
| `windowFocus` | `program`, `titleMatch` (`exact`/`begins`/`ends`/`contains`/`any`), `title`, optional `left`, `top`, `width`, `height`, `timeoutMs`, `onTimeout`, `then` |

Every action can also have a `label`, and optionally `comment`, `disabled`, `breakpoint`, `rowColor`, `group` and `groupColor`. The file itself may carry `screen`, the monitor layout it was recorded on. `examples/` contains the spec's dialog-loop macro and its `rules.json` (the installer puts them in `Macros\Examples`).

**One addition to the spec:** `stop` is a reserved label, alongside `start`, `end` and numbers. It is valid as any jump target and stops playback. `onTimeout` defaults to `stop`.

## For developers

### Make a new release

Push a version tag, e.g. `git tag v0.4.1` then `git push origin v0.4.1`. GitHub Actions
(`.github/workflows/release.yml`) runs the tests, builds `Echostep.exe`, wraps it in an installer
(`installer/Echostep.iss`, Inno Setup) and publishes both on the Releases page. The download links at the
top always point to the newest release.

### Build the EXE yourself

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) on a Windows PC. From this folder, run:

```
build.cmd
```

or

```
dotnet publish src\Echostep -c Release -o dist
```

The result is `dist\Echostep.exe`, one file. Copy it anywhere; nothing else is needed to run it. `settings.json`, `rules.json` and the `Macros` folder are kept next to the EXE. If that folder is read-only, they go in `%APPDATA%\Echostep` instead.

To check the platform-independent core on any OS, run `dotnet run --project tests/CoreTests` (352 checks).


### Project layout

```
src/Echostep/
  Core/   platform-independent: actions, .estep/rules.json, labels and validation, Player,
          recorder event → action conversion, rule state machine, RuleEngine, settings
  Win/    Win32: Native (P/Invoke), SendInput/GetPixel drivers, low-level hooks, global hotkeys
  UI/     WinForms: MainForm, action list editor, action and rule dialogs, Rules tab, picker
tests/CoreTests/          tests for Core (the release build runs them first)
installer/Echostep.iss    the installer definition (Inno Setup)
.github/workflows/        the GitHub Actions build that makes the installer and the release
examples/                 sample macro and rules file (installed in Macros\Examples)
build.cmd                 builds dist\Echostep.exe locally (needs the .NET 10 SDK)
```

## Known limits

- The window can be made as small as 588 × 336. When it is narrower than the ribbon's buttons, the ribbon scrolls sideways; status bar fields that do not fit are hidden.

- By default a held drag is recorded as a click. Turn on *Record buttons and keys as separate Down and Up rows* in Settings to capture drags.
- Wait for screen change on the entire screen compares every pixel of every monitor about ten times a second, which uses noticeable CPU on large displays; a zone is much cheaper.
- Rule actions and macro playback can send input at the same time. If both are running, they interleave.
- Pixel-marker overlays, the tray icon, playback speed and the other later-phase items are not in this build.
- The Windows limits in the spec still apply: elevated apps need an elevated Echostep, and the secure desktop and some fullscreen or DRM content can't be read. Antivirus may also flag an unsigned EXE that hooks input.
