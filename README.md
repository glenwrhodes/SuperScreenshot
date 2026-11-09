# SuperScreenshot

<img src="docs/icon.png" width="72" align="right" alt="">

A Windows screenshot tool that works like Snipping Tool, except you can keep selecting
past the edge of the screen. Drag to the bottom of a web page and it scrolls while you
select, stitching the whole thing into one tall screenshot.

## Using it

SuperScreenshot starts with Windows and waits in the hidden icons tray (the ^ by the clock).

1. Press **Win+Shift+X** anywhere. That's one key over from Snipping Tool's Win+Shift+S.
   Clicking the tray icon or **New** in the window also works.
2. Drag to select. The screen dims and the selection is clear.
3. Need more than fits on screen? Either:
   - keep dragging to the bottom (or top) edge of the window; it scrolls, faster the further you go, or
   - turn the mouse wheel while you hold the button.

   The start of your selection moves up off the screen as the page scrolls. A label shows
   the full size, and how much is scrolled out of view.
4. Let go. The screenshot opens in the preview and is **already on the clipboard**.
   **Copy** and **Save…** are there too (PNG or JPEG).

Esc or a right-click cancels. Ctrl+N, Ctrl+C and Ctrl+S work in the preview window.

Closing the window keeps it in the tray. Right-click the tray icon to open the window, turn
**Start with Windows** on or off (it is on after the first run), or **Exit**. Only one copy
runs: starting it again just opens the window.

Why a tray app? Snipping Tool doesn't need one because Windows itself owns Win+Shift+S and
launches it. Other apps can't add shortcuts to that list, so like ShareX and Greenshot,
SuperScreenshot keeps a small process running to listen for its shortcut. Win+Shift+A
wasn't an option, because something on the system (probably Windows) already owns it.

## How it works

- **The screen isn't frozen.** Snipping Tool shows a still picture of the screen. SuperScreenshot
  instead shows a dim overlay on top of the live screen. The overlay is click-through and
  hidden from screen capture (`WDA_EXCLUDEFROMCAPTURE`), so the page underneath can
  scroll, and screenshots never include the overlay.
- **Clicks, Esc and the wheel** go through low-level mouse and keyboard hooks on their own
  thread, so they never reach the page and the mouse never lags. While you drag, every wheel
  turn, yours or the auto-scroll's, goes to the page where you started the selection, even
  if the cursor is over the taskbar.
- **Stitching** (`Tracker.cs`): about 30 times a second it grabs the window being scrolled
  and hashes every row, in vertical bands so a fixed sidebar can't spoil a match. Changed
  rows vote on how far the content moved, and the shift with the most votes wins. Newly
  revealed rows are added to a tall canvas. Rows that stay still while everything else moves
  are a sticky header, sticky footer or the browser toolbar, and they aren't repeated.
- Coordinates are physical pixels throughout (per-monitor DPI aware), so it's sharp at any
  scaling.

Limits: vertical scrolling only, up to 30,000 px tall. Anything that changes while it
scrolls past, like a video, an animation or a lazy-loading image, is captured as it was
when it went by. A page area that is completely blank and taller than the window can't be
tracked, because there is nothing to line up.

## Building

Requires the .NET 9 SDK, on Windows 10 2004 or later.

```powershell
dotnet build                 # debug build: src\SuperScreenshot\bin\Debug\net9.0-windows\SuperScreenshot.exe
dotnet test                  # stitching tests on synthetic pages
.\publish.ps1                # tests, then dist\SuperScreenshot.exe (one self-contained file, no .NET needed)
```

### Developer checks

- `SuperScreenshot.exe --scroll-test X Y NOTCHES OUT.png` tracks the window under (X, Y)
  in physical pixels, turns the wheel NOTCHES times there (negative scrolls up), and saves
  the stitched result plus a log at `OUT.png.txt`. Use it to check stitching against a real app.
- Set `SUPERSCREENSHOT_SHOW_OVERLAY=1` to let screenshots see the overlay, so you can check
  how it looks. Scrolling capture doesn't work in this mode.

## Code map

| File | What it does |
| --- | --- |
| `TrayApp.cs` | Tray icon and menu, start with Windows, the Win+Shift+X hotkey |
| `GlobalHotkey.cs` | Registers Win+Shift+X |
| `MainForm.cs` | The window: New/Copy/Save, preview; hides to the tray when closed |
| `CaptureSession.cs` | One capture: overlay drawing, selection, auto-scroll, wheel redirect |
| `Tracker.cs` | Follows the scrolling and stitches the tall image |
| `Overlay.cs` | Layered overlay window, input hooks thread, crosshair cursors |
| `ScreenHelpers.cs` | Window under a point, DPI, injecting wheel turns |
| `Pixels.cs` | Screen grabbing and bitmap building |
| `Native.cs` | Win32 declarations |
