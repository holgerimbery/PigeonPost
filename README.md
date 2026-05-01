# PigeonPost

PigeonPost is a lightweight Windows 11 tray application that exposes a simple
local HTTP API for transferring files and clipboard content from any network-connected
device to your Windows PC.

Any HTTP-capable client can use it — automation tools like Apple Shortcuts or
Android Tasker, command-line utilities like `curl`, scripts, or custom apps.
No cloud service, no account, no setup beyond the app itself.

---

## What you get

- **Local HTTP API** on port 2560 — receive files, read and write the clipboard
- **Pigeon + envelope app icon** shown in the title bar, taskbar, and Alt+Tab
- **Mica window** with automatic dark / light theming (Windows 11 Fluent colour palette)
- **Live status indicator** mirrored on the tray icon (green = running, amber = paused)
- **Stat cards**: Files received · Clipboard sends · Clipboard reads · Uptime
- **Collapsible activity log** with colour-coded entries that adapt to the current theme
- **Pause / Resume** — keeps the port open but returns `503` to all incoming requests
- **Open Downloads** button
- **Minimize to tray** — the close button hides the window; left-click the icon to restore
- **Tray context menu**: Show window · Pause / Resume · Quit

---

## Prerequisites (one-time setup — build from source only)

| Dependency | Version | Where to get it |
|---|---|---|
| **.NET 9 SDK** | 9.0.100 or newer | <https://dotnet.microsoft.com/download/dotnet/9.0> |
| **Windows App SDK runtime** | 2.0.1 (auto-restored by NuGet) | No manual install needed for builds |
| Windows | 10 1809 (build 17763) or later | Mica backdrop requires Windows 11; gracefully skipped on Windows 10 |

> Run `dotnet --version` in a terminal to confirm .NET 9 is on PATH.
> The first build takes 30–90 seconds while NuGet downloads the Windows App SDK.

---

## Build & run

```bat
cd PigeonPost
dotnet restore
dotnet build PigeonPost.csproj -c Debug -r win-x64
dotnet run --project PigeonPost.csproj -r win-x64
```

On first launch, Windows Defender Firewall may ask whether to allow incoming
connections on port 2560. Tick **Private networks** and click **Allow access**.

---

## Publish — portable single-file EXE

The following command produces **one self-contained EXE** (~207 MB) that runs on
any Windows 10/11 x64 machine without installing .NET or Windows App SDK:

```bat
dotnet publish PigeonPost.csproj -c Release -r win-x64 ^
    --self-contained true ^
    /p:WindowsAppSdkSelfContained=true ^
    /p:PublishSingleFile=true ^
    /p:PublishReadyToRun=false
```

Output:

```
bin\Release\net9.0-windows10.0.19041.0\win-x64\publish\PigeonPost.exe
```

Copy just `PigeonPost.exe` to the target machine and run it directly.

> **First launch:** Windows extracts the bundled native App SDK DLLs to `%TEMP%`
> (a one-time step, ~2–3 seconds). Subsequent launches are instant.
>
> **ARM-based PCs:** use `-r win-arm64` instead of `-r win-x64`.
>
> **No console window.** The project is built as `WinExe`.

---

## Project layout

```
PigeonPost/
├── PigeonPost.csproj            # WinUI 3 + .NET 9, unpackaged
├── app.manifest                 # Per-monitor DPI awareness, Win10/11 compat IDs
├── App.xaml / App.xaml.cs       # Application bootstrap; ThemeDictionaries (Fluent Light/Dark)
├── MainWindow.xaml / .xaml.cs   # Mica window, layout, tray icon wiring, theme-change handler
├── Constants.cs                 # Port number, Downloads folder path
├── Assets/
│   └── PigeonPost.ico           # Pigeon + envelope icon (7 sizes: 256 → 16 px)
├── Models/
│   ├── LogLevel.cs              # Enum: Info / Success / Warn / Error / File / Clipboard
│   └── LogEntry.cs              # Immutable log record; LevelBrush resolves from ThemeDictionaries
├── Services/
│   ├── AppState.cs              # Thread-safe shared state + events
│   ├── ListenerService.cs       # HttpListener, clipboard ops, file upload; localhost fallback
│   └── NetworkHelper.cs         # Local-IP discovery for the address card
└── ViewModels/
    └── MainViewModel.cs         # MVVM: observable properties, relay commands, uptime timer;
                                 # RefreshStatusColors() resolves Fluent brushes from resources
```

---

## HTTP API reference

All requests use **POST** to the root path `/` on port **2560**.
The action is selected by a single custom request header.

### Clipboard operations

| Header | Request body | Response body | Description |
|---|---|---|---|
| `clipboard: send`    | UTF-8 text | `Data copied to clipboard` | Writes the body to the PC clipboard |
| `clipboard: receive` | *(empty)*  | UTF-8 text | Returns the current PC clipboard content |
| `clipboard: clear`   | *(empty)*  | `Clipboard cleared` | Empties the PC clipboard |

### File transfer

| Header | Request body | Response body | Description |
|---|---|---|---|
| `filename: <name>` | binary | `File uploaded successfully` | Saves the body as `<name>` in the Downloads folder |

### Status codes

| Code | Meaning |
|---|---|
| `200` | Request handled successfully |
| `400` | Missing or invalid header / filename |
| `405` | HTTP method other than POST was used |
| `500` | Unexpected server-side error |
| `503` | Server is currently paused |

### curl examples

```bash
# Write text to clipboard
curl -X POST http://192.168.1.5:2560 -H "clipboard: send" -d "Hello from curl"

# Read clipboard
curl -X POST http://192.168.1.5:2560 -H "clipboard: receive"

# Clear clipboard
curl -X POST http://192.168.1.5:2560 -H "clipboard: clear"

# Upload a file
curl -X POST http://192.168.1.5:2560 -H "filename: photo.jpg" --data-binary @photo.jpg
```

---

## How the HTTP listener binds

On Windows, binding `HttpListener` to a wildcard prefix (`http://+:2560/`) or to a
specific non-loopback IP address requires administrator privileges or a URL ACL entry.

PigeonPost handles this automatically:

1. At startup it tries to bind to every IPv4 address on every non-loopback, operational
   network interface so requests arrive on any interface.
2. If that fails with *"Access is denied"* (no admin rights, no ACL), it falls back to
   `http://localhost:2560/` automatically and logs a warning in the activity log.

The address card in the main window always shows the address that was actually bound.

> If your PC's IP changes (DHCP renewal, switching networks), restart the app to
> re-bind to the new address.

---

## Dark and light mode

PigeonPost follows the Windows system colour mode automatically:

- Status indicator (header ellipse + tray icon) and activity-log labels use Windows 11
  Fluent colours defined in `App.xaml` `ThemeDictionaries`.
- The `MainViewModel.RefreshStatusColors()` method is called at startup and whenever
  the OS theme changes (`RootGrid.ActualThemeChanged`) so colours update live without
  restarting the app.

---

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| **Address card shows `localhost`** | LAN binding failed — no admin rights and no URL ACL | See activity log for the warning. To bind to LAN: run as Administrator, or run `netsh http add urlacl url=http://YOUR.IP:2560/ user=Everyone` in an elevated prompt |
| **`HTTP Error 183`** or **`Address already in use`** on startup | A previous PigeonPost process still holds port 2560 | Open Task Manager → end any stale `PigeonPost.exe`, then restart |
| **Requests are not received** | Windows Defender Firewall is blocking port 2560 | Allow the app at the first-launch prompt, or add an inbound rule for TCP 2560 manually |
| **Window is transparent / no Mica effect** | Running on Windows 10 | Expected — Mica is Windows 11 only; the app functions normally otherwise |
| **`The Windows App Runtime is not installed`** when running the published exe | Published without `--self-contained` flags | Re-publish with the full `dotnet publish` command shown above |
| **Tray icon does not appear** | Windows hides overflow icons | Right-click taskbar → *Taskbar settings* → *Other system tray icons* → enable **PigeonPost** |
| **First launch of the portable EXE is slow** | Native DLLs are being extracted to `%TEMP%` | One-time extraction; subsequent launches are instant |

---

## Security notes

- **No authentication.** Only run on trusted private networks. To add a shared-secret
  check, inspect the request headers in `ListenerService.HandleAsync` before routing.
- **No HTTPS / TLS.** Not necessary for a trusted LAN scenario.
- **Path-traversal protection.** `Path.GetFileName` strips all directory components from
  uploaded filenames, so a filename like `../../evil.exe` is saved as `evil.exe`.

---

## What is intentionally not included (v1)

- HTTPS / TLS
- Authentication
- Run on login (add a value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`)
- Auto-update

---

## License

Reference implementation; reuse freely.
