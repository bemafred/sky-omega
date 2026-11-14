# AvaTerm

A terminal emulator for macOS, Linux and Windows. .NET 10, Avalonia 11.3, no native code and
no third-party terminal library: the VT/xterm emulator, the PTY layer and the renderer are all here.

    dotnet run --project src/AvaTerm.App            # run
    dotnet run --project tests/AvaTerm.Tests        # tests (optional name filter as argument)
    dotnet publish src/AvaTerm.App -c Release -r osx-arm64 --self-contained   # or linux-x64, win-x64

## Layout

| Project | Contents |
| --- | --- |
| `AvaTerm.Core` | Emulator: parser, primary/alternate screen, scrollback, selection, key/mouse/paste encoding. No dependencies. |
| `AvaTerm.Pty` | `posix_openpt` + `posix_spawn` on macOS/Linux, ConPTY on Windows; `TerminalSession` ties a PTY to an emulator. |
| `AvaTerm.App` | Avalonia front end: cell renderer, input, tabs, scrollbar. Code only, no XAML. |
| `AvaTerm.Tests` | Console test runner without a test framework. |

## Behaviour

- `TERM=xterm-256color`, 256 and 24-bit colour, alternate screen, scroll regions, bracketed paste,
  focus events, DEC line drawing, wide and combining characters, OSC 0/2 title, OSC 52 clipboard write.
- History: 10 000 lines by default. Mouse wheel, scrollbar, Shift+PageUp/PageDown, Shift+Home/End.
  The view stays put while output continues and returns to the live screen on typing.
- Resize follows the window. Rows move between screen and history; text cut off by narrowing
  comes back on widening. Long lines are not re-wrapped (see Limits).
- Mouse:
  - drag selects, double-click selects a word, triple-click a line; the selection is copied on release
  - right-click copies the selection, or pastes when there is none; middle-click pastes
  - a plain click on the line being edited moves the shell cursor to that cell (sent as cursor keys)
  - programs that request the mouse (tmux with `mouse on`, vim, htop) receive it; hold Shift to select instead
  - wheel in full-screen programs without mouse support (less, man) scrolls them with cursor keys
- Keys: Ctrl+Shift+C / V copy and paste (Cmd+C / V on macOS), Shift+Insert pastes, Ctrl+C copies while
  a selection exists. Ctrl+Shift+T / W new and close tab (Cmd+T / W), Ctrl+Tab next tab.
  Ctrl with + / - / 0 zooms (Cmd on macOS).
- macOS: Option composes characters (needed for `[ ] { } | \ @` on Swedish and other layouts);
  Option+Left/Right move by word. Set `MacOptionIsMeta` to get ESC-prefixed Option keys instead.
- Box-drawing and block characters are drawn as geometry, so tmux pane borders are continuous.

## tmux

Works as in xterm, including mouse mode and resize. On Windows, tmux runs inside WSL: start `wsl`
in a tab, or set `"Shell": "wsl.exe"` in the settings.

## Settings

Optional `settings.json` in `%APPDATA%\AvaTerm`, `~/.config/AvaTerm` or
`~/Library/Application Support/AvaTerm`; see `Settings.cs` for all keys.

    { "FontFamily": "JetBrains Mono", "FontSize": 13, "ScrollbackLines": 50000, "Shell": "/bin/zsh" }

## Verification status

Built and run on Ubuntu 24.04 x64 with .NET SDK 10.0.114:

- `AvaTerm.Core` and `AvaTerm.Pty`: 31 tests pass. They include real shells on a PTY (controlling
  terminal, job control, Ctrl+C, resize, UTF-8), a live tmux session (status line, pane split,
  mouse click selecting a pane, resize), and screen-for-screen comparison against tmux's own
  emulator for less, vim and nano.

Built and started on Windows 11 x64 with .NET SDK 10.0.403 and Avalonia 11.3: the app compiles,
ConPTY starts PowerShell 7, and output renders. Beyond that first screen (selection, mouse
reporting, click-to-place, resize, tabs, tmux through WSL) nothing in the front end has been
exercised yet.

Not run: the macOS path. The point to watch is the `ioctl` call on Apple Silicon (variadic ABI
workaround in `UnixPty.SetWindowSize`).

## Limits

- No re-wrap of long lines on resize.
- No IME pre-edit display; committed text and dead keys go through normal text input.
- Double-line box characters and diagonals come from the font.
- No ligatures (cells are drawn one by one), no sixel or other inline images, no hyperlinks (OSC 8).
- Ctrl+Alt combinations are left to the keyboard layout, because that is how Windows reports AltGr.
