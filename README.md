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
- **Mica window** with automatic dark / light theming
- **Live status indicator** mirrored on the tray icon (green = running, amber = paused)
- **Stat cards**: Files received · Clipboard sends · Clipboard reads · Uptime
- **Collapsible activity log** with colour-coded entries
- **Pause / Resume** — keeps the port open but returns `503` to all incoming requests
- **Open Downloads** button
- **Minimize to tray** — the close button hides the window; left-click the icon to restore
- **Tray context menu**: Show window · Pause / Resume · Quit

---

## Prerequisites (one-time setup)

| Dependency | Version | Where to get it |
|---|---|---|
| **.NET 8 SDK** | 8.0.100 or newer | <https://dotnet.microsoft.com/download/dotnet/8.0> |
| **Windows App SDK runtime** | 1.6 (auto-restored by NuGet) | No manual install needed |
| Windows | 10 1809 (build 17763) or later | Mica backdrop requires Windows 11; no-op on Windows 10 |

> Run `dotnet --version` in a terminal to confirm the SDK is on PATH.
> The first build takes 30–90 seconds while NuGet downloads the Windows App SDK.

---

## Build & run

```bat
cd PigeonPost
dotnet restore
dotnet build
dotnet run
```

On first launch, Windows Defender Firewall will ask whether to allow incoming
connections on port 2560. Tick **Private networks** and click **Allow access**.

---

## Publish a self-contained executable

```bat
dotnet publish -c Release -r win-x64 --self-contained true ^
    -p:WindowsAppSDKSelfContained=true
```

Output folder:

```
bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\
```

Copy the entire folder to the target machine and double-click `PigeonPost.exe`.
No .NET runtime needs to be pre-installed.

> **ARM-based PCs:** use `-r win-arm64` instead of `-r win-x64`.
>
> **No console window.** The project is built as `WinExe`.

---

## Project layout

```
PigeonPost/
├── PigeonPost.csproj            # WinUI 3 + .NET 8, unpackaged
├── app.manifest                 # Per-monitor DPI awareness, Win10/11 compat IDs
├── App.xaml / App.xaml.cs       # Application bootstrap; shared resource styles
├── MainWindow.xaml / .xaml.cs   # Mica window, layout, tray icon wiring
├── Constants.cs                 # Port number, Downloads folder path
├── Models/
│   ├── LogLevel.cs              # Enum: Info / Success / Warn / Error / File / Clipboard
│   └── LogEntry.cs              # Immutable log record with binding-ready properties
├── Services/
│   ├── AppState.cs              # Thread-safe shared state + events
│   ├── ListenerService.cs       # HttpListener, clipboard ops, file upload
│   └── NetworkHelper.cs         # Local-IP discovery (display + binding)
└── ViewModels/
    └── MainViewModel.cs         # MVVM: observable properties, relay commands, uptime timer
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

Using an `HttpListener` wildcard prefix (`http://+:2560/`) on Windows requires
administrator privileges.

To avoid needing elevation, PigeonPost **enumerates every IPv4 address on every
non-loopback, operational network interface** at startup and registers one explicit
prefix per address. Incoming requests on any interface are accepted without requiring
admin rights.

> If your PC's IP changes (DHCP renewal, switching networks), restart the app to
> re-bind to the new address.

---

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| **`HTTP Error 183`** or **`Address already in use`** on startup | A previous PigeonPost process still holds port 2560 | Open Task Manager → end any stale `PigeonPost.exe`, then restart |
| **`HTTP Error 5: Access is denied`** on startup | A URL ACL conflict from a wildcard prefix | Run `netsh http delete urlacl url=http://+:2560/` in an elevated prompt, then restart |
| **Requests are not received** | Windows Defender Firewall is blocking port 2560 | Allow the app at the first-launch prompt, or add an inbound rule for TCP 2560 manually |
| **Window is transparent / no Mica effect** | Running on Windows 10 | Expected — Mica is Windows 11 only; the app functions normally otherwise |
| **`The Windows App Runtime is not installed`** when running the published exe | Published without `--self-contained true` | Re-publish with `--self-contained true -p:WindowsAppSDKSelfContained=true` |
| **Tray icon does not appear** | Windows hides overflow icons | Right-click taskbar → *Taskbar settings* → *Other system tray icons* → enable **PigeonPost** |

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