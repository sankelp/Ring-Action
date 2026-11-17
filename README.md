# RingAction

A radial ring menu for Windows. Press one mouse button, a ring of actions opens at your cursor, click one, done.
Shortcuts, macros, apps, links and commands, all one click away, with a different ring for each app.

Version 1.0.0 (MIT License)

## Quick start

1. Unzip the folder somewhere you can write to (Desktop or Documents). Not `C:\Program Files`.
2. Run `RingAction.exe`. A tray icon appears (check the hidden icons arrow if you don't see it).
3. Click the tray icon, then **Open editor**. The tray menu also has **Pause** and **Quit**.
4. Open the ring: press the button that sends **Win+D** (see below).

Windows may show "Windows protected your PC" the first time, because this build is not code-signed.
Choose **More info**, then **Run anyway**, if you trust where you got the file.

## Setting up the trigger button

RingAction opens when it sees the trigger shortcut, which is **Win+D** by default.
Most gaming mice and mouse utilities (your mouse maker's own app, or tools like X-Mouse Button Control)
can map a button to a keyboard shortcut. Map one button to **Win+D**, then press it.

You can use another shortcut: **Settings** in the editor, **Trigger**, click, and press your mouse button
or key combination. The trigger must end in a normal key (not only Ctrl, Alt, Shift or Win).

Optional: in the same place, RingAction can lock the trigger to the device you recorded it on, so
your keyboard's own Win+D keeps working normally. **Use Any Keyboard** turns that off.

## Using the ring

- Press the trigger to open the ring at the cursor. Click a tile to run it. Click the middle, or press Esc, to cancel.
- A ring holds up to 12 tiles. Each ring can have up to 4 sets, and a **Switch set** tile jumps between them.
- **Select on hover** (Settings, or per app) picks a tile when you hold the pointer on it.

## Tile types

| Type | What it does |
|---|---|
| Shortcut | Sends a key combination, for example `Ctrl+Shift+T`. |
| Macro | Runs steps in order: press keys, type text, wait. Each key step has a hold time and a "Then wait". Use **Record** to capture keys with your real timing. |
| Launch | Opens a program, with optional arguments. |
| Link | Opens a web address. |
| Command | Runs a command line (`cmd /c ...`). |
| Switch set | Changes to another set of tiles on the same ring. |

## One ring per app

The **Default** ring is used everywhere. Add an app ring (the **+** next to Rings) and that app gets its own tiles
whenever it is in front. An app ring with no tiles turns the ring off for that app, and the trigger works normally there.

## Backup and sharing

Settings, **Backup**: **Export** saves your whole setup to a file. **Import** asks whether to **Add** it to what
you have or **Replace** it. Your settings live in `ring.json` next to `RingAction.exe`.

Only import files from people you trust. A shared setup can contain **Command** and **Launch** tiles that run programs.

## Privacy and permissions

- RingAction watches the keyboard and mouse system-wide, only to spot your trigger and, while the ring is open, your clicks.
  It does not record, store or send what you type. While you use the macro or shortcut recorder it captures only the keys you press there.
- Macros and shortcut tiles send keystrokes to whichever window is in front. Check a macro before running it.
- Nothing leaves your computer. There is no account, no analytics and no update check. The editor is a page served only to
  your own PC (127.0.0.1) and opens in Microsoft Edge's app window.
- Security software may flag programs that hook the keyboard. That is expected for this kind of tool; the exe is not signed yet.

## Requirements

Windows 10 (version 1903 or later) or Windows 11, and Microsoft Edge for the editor. Nothing else to install.

## Quit and uninstall

Tray icon, then **Quit**. To remove it, quit, then delete the folder.

## Known issues

- Sending macro text to some elevated (administrator) windows or games can fail unless RingAction also runs as administrator.

## License

MIT. See the LICENSE file.

## Feedback

Found a bug or have an idea? Open an issue on this repository's **Issues** tab. Say what you did, what you expected and what happened, and your Windows version.
