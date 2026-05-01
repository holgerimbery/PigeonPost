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

> **LAN access from other devices (iPhone, Android, …):**
> Windows classifies newly joined Wi-Fi networks as *Public* by default and blocks
> inbound connections even if you accepted the firewall prompt.  Two steps are needed:
>
> **Step 1 — Set the Wi-Fi network to Private**
> `Settings → System → Network & Internet → Wi-Fi → <your network> → Network profile type → Private`
>
> **Step 2 — Add an inbound firewall rule for port 2560**
> Run PowerShell **as Administrator** and paste:
> ```powershell
> New-NetFirewallRule -DisplayName "PigeonPost (Port 2560)" `
>     -Direction Inbound -Protocol TCP -LocalPort 2560 `
>     -Action Allow -Profile Private,Domain,Public
> ```
> Both steps are required.  The rule alone is not enough if the network profile is *Public*.

---

## Publish — portable single-file EXE

The following command produces **one self-contained EXE** (~207 MB) that runs on
any Windows 10/11 x64 machine without installing .NET or Windows App SDK:

```bat
dotnet publish PigeonPost.csproj -c Release -r win-x64 ^
    --self-contained true ^
    /p:WindowsAppSdkSelfContained=true ^
    /p:PublishSingleFile=true ^
    /p:EnableMsixTooling=true ^
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
| `clipboard: send`    | UTF-8 text **or** JSON `{"text":"…"}` | `Data copied to clipboard` | Writes the body to the PC clipboard. Plain text and JSON object (key `text`) are both accepted — use JSON body type in iOS Shortcuts |
| `clipboard: receive` | *(empty)*  | UTF-8 text | Returns the current PC clipboard content |
| `clipboard: clear`   | *(empty)*  | `Clipboard cleared` | Empties the PC clipboard |

### File transfer

The filename can be passed as a **header** or as a **URL query parameter**.  
Use the query-parameter form from iOS Shortcuts — it avoids "invalid header" rejections caused by special characters (spaces, umlauts, date separators) in the filename.

| Method | Example | Request body | Response body |
|---|---|---|---|
| Header | `POST /` + header `filename: photo.jpg` | binary | `File uploaded successfully` |
| Query param *(recommended for Shortcuts)* | `POST /?filename=photo.jpg` | binary | `File uploaded successfully` |

### Status codes

| Code | Meaning |
|---|---|
| `200` | Request handled successfully |
| `400` | Missing or invalid header / filename |
| `405` | HTTP method other than POST was used |
| `500` | Unexpected server-side error |
| `503` | Server is currently paused |

### iOS Shortcuts

PigeonPost works with the built-in **Shortcuts** app (German: *Kurzbefehle*) on any iPhone
or iPad on the same Wi-Fi network.

> **Before you start / Vor dem Start:**  
> Find your PC's IP address in the PigeonPost address card (e.g. `192.168.1.205`).  
> Replace `YOUR_PC_IP` / `DEINE_PC_IP` in every URL below.

> iOS Shortcuts only offers JSON / Form / File as body types — there is no plain-text option.  
> PigeonPost accepts JSON `{"text":"…"}` as well as plain text, so both curl and Shortcuts work.

---

#### Shortcut 1 — Send iPhone clipboard to Windows / Zwischenablage an Windows senden

**What it does:** Sends whatever is copied on your iPhone to the Windows clipboard.  
**Was es tut:** Sendet die iPhone-Zwischenablage an die Windows-Zwischenablage.

**English steps**

1. Open **Shortcuts** → tap **+** (top right)
2. **Add Action** → search `Get Contents of URL` → tap it
3. Configure the action:
   - **URL**: `http://YOUR_PC_IP:2560`
   - **Method**: `POST`
   - Tap **Headers** → add a header: Key `clipboard` / Value `send`
   - Tap **Request Body** → select **JSON**
   - Add a field: Key `text` / Value → tap the variable button (`+`) → choose **Clipboard**
4. Tap the shortcut name at the top → rename to e.g. **📋 → Windows**
5. Tap **Done**

**Deutsche Schritte**

1. **Kurzbefehle** öffnen → oben rechts **+** tippen
2. **Aktion hinzufügen** → `Inhalt der URL abrufen` suchen → auswählen
3. Aktion konfigurieren:
   - **URL**: `http://DEINE_PC_IP:2560`
   - **Methode**: `POST`
   - **Header** antippen → Header hinzufügen: Schlüssel `clipboard` / Wert `send`
   - **Anforderungstext** antippen → **JSON** wählen
   - Feld hinzufügen: Schlüssel `text` / Wert → Variable-Taste (`+`) → **Zwischenablage** wählen
4. Namen oben antippen → umbenennen z. B. **📋 → Windows**
5. **Fertig** tippen

---

#### Shortcut 2 — Get Windows clipboard on iPhone / Windows-Zwischenablage auf iPhone lesen

**What it does:** Reads the current Windows clipboard and copies the text to your iPhone clipboard.  
**Was es tut:** Liest die Windows-Zwischenablage und kopiert den Text auf das iPhone.

**English steps**

1. Open **Shortcuts** → tap **+**
2. **Add Action** → search `Get Contents of URL` → tap it
3. Configure:
   - **URL**: `http://YOUR_PC_IP:2560`
   - **Method**: `POST`
   - **Headers** → Key `clipboard` / Value `receive`
   - *(No request body needed)*
4. **Add Action** → search `Copy to Clipboard` → tap it  
   *(the URL result is passed automatically as the text to copy)*
5. Rename to **Windows → 📋** → **Done**

**Deutsche Schritte**

1. **Kurzbefehle** → **+** tippen
2. **Aktion hinzufügen** → `Inhalt der URL abrufen` → auswählen
3. Konfigurieren:
   - **URL**: `http://DEINE_PC_IP:2560`
   - **Methode**: `POST`
   - **Header** → Schlüssel `clipboard` / Wert `receive`
   - *(Kein Anforderungstext nötig)*
4. **Aktion hinzufügen** → `In Zwischenablage kopieren` suchen → auswählen  
   *(das URL-Ergebnis wird automatisch als Text übergeben)*
5. Umbenennen: **Windows → 📋** → **Fertig**

---

#### Shortcut 3 — Send photo to Windows / Foto an Windows senden

**What it does:** Picks a photo from your gallery, converts it to JPEG, and uploads it to the
Windows Downloads folder with a timestamped filename.  
**Was es tut:** Wählt ein Foto aus der Galerie, konvertiert es zu JPEG und lädt es mit
einem Zeitstempel-Dateinamen in den Windows-Downloads-Ordner hoch.

**English steps**

1. Open **Shortcuts** → **+**
2. **Add Action** → search `Select Photos` → tap it  
   *(leave "Select Multiple" off for a single photo)*
3. **Add Action** → search `Convert Image` → tap it  
   Set **Format** to **JPEG** and **Quality** to **Best**
4. **Add Action** → search `Format Date` → tap it
   - **Date**: tap the variable button (`+`) → choose **Current Date**
   - **Format**: select **Custom** → type `yyyy-MM-dd_HH-mm-ss`
5. **Add Action** → search `Get Contents of URL` → tap it, configure:
   - **URL**: type `http://YOUR_PC_IP:2560?filename=` → tap `+` → choose **Formatted Date** → type `.jpg` directly after it  
     *(the full URL will look like `http://192.168.1.205:2560?filename=2026-05-01_13-00-00.jpg`)*
   - **Method**: `POST`
   - **Request Body** → select **File**
   - File value: tap `+` → choose **Converted Image**
6. Rename to **📸 → Windows** → **Done**

**Deutsche Schritte**

1. **Kurzbefehle** → **+** tippen
2. **Aktion hinzufügen** → `Fotos auswählen` suchen → auswählen  
   *("Mehrere auswählen" ausgeschaltet lassen für ein einzelnes Foto)*
3. **Aktion hinzufügen** → `Bild konvertieren` suchen → auswählen  
   **Format**: **JPEG**, **Qualität**: **Beste**
4. **Aktion hinzufügen** → `Datum formatieren` suchen → auswählen
   - **Datum**: Variable-Taste (`+`) → **Aktuelles Datum** wählen
   - **Format**: **Benutzerdefiniert** → `yyyy-MM-dd_HH-mm-ss` eingeben
5. **Aktion hinzufügen** → `Inhalt der URL abrufen` suchen → auswählen, konfigurieren:
   - **URL**: `http://DEINE_PC_IP:2560?filename=` eingeben → `+` → **Formatiertes Datum** wählen → direkt danach `.jpg` eingeben  
     *(Ergebnis: `http://192.168.1.205:2560?filename=2026-05-01_13-00-00.jpg`)*
   - **Methode**: `POST`
   - **Anforderungstext** → **Datei** wählen
   - Datei-Wert: `+` → **Konvertiertes Bild** wählen
6. Umbenennen: **📸 → Windows** → **Fertig**

> **Tip / Tipp:** Add any shortcut to your iPhone Home Screen:  
> Open the shortcut → tap **⋯** (top right) → **Add to Home Screen**.  
> Kurzbefehl zum Home-Bildschirm hinzufügen: Kurzbefehl öffnen → **⋯** (oben rechts) → **Zum Home-Bildschirm**.

### curl examples

```bash
# Write text to clipboard
curl -X POST http://192.168.1.5:2560 -H "clipboard: send" -d "Hello from curl"

# Read clipboard
curl -X POST http://192.168.1.5:2560 -H "clipboard: receive"

# Clear clipboard
curl -X POST http://192.168.1.5:2560 -H "clipboard: clear"

# Upload a file (header — simple filenames only)
curl -X POST http://192.168.1.5:2560 -H "filename: photo.jpg" --data-binary @photo.jpg

# Upload a file (query param — safe for any filename)
curl -X POST "http://192.168.1.5:2560?filename=Foto%202026-05-01.jpg" --data-binary @photo.jpg
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
| **Requests time out from phone / tablet** | Windows Firewall blocks the port, or the Wi-Fi is classified as *Public* | (1) Set the Wi-Fi network profile to **Private** (`Settings → Network & Internet → Wi-Fi → <network> → Private`). (2) Add an inbound rule: `New-NetFirewallRule -DisplayName "PigeonPost (Port 2560)" -Direction Inbound -Protocol TCP -LocalPort 2560 -Action Allow -Profile Private,Domain,Public` in an elevated PowerShell |
| **Requests are not received (localhost only)** | Windows Defender Firewall is blocking port 2560 | Allow the app at the first-launch prompt, or add an inbound rule for TCP 2560 as shown above |
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
