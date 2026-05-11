# Microsoft Store Submission Guide for PigeonPost

This guide walks through the one-time setup needed to publish PigeonPost
to the Microsoft Store and activate the automated `store.yml` CI workflow.

---

## 1 — Partner Center account

1. Go to <https://partner.microsoft.com/dashboard> and sign in with your Microsoft account.
2. If you haven't enrolled, choose **Register as an individual developer** (no fee for free apps).
3. Accept the agreements and complete the registration.

---

## 2 — Create the app listing

1. In Partner Center → **Apps and games** → **New product** → **App**.
2. Reserve the name **PigeonPost**.
3. After creating the app, note the **App ID** from the URL:
   `https://partner.microsoft.com/…/apps/<APP_ID>/…`
   — you'll need this as the `STORE_APP_ID` secret.

---

## 3 — Find your Publisher DN

1. Partner Center → **Account settings** → **Organization profile** (or **Developer profile**).
2. Copy the **Publisher** field — it looks like `CN=Holger Imbery, O=…, C=DE`.
3. Update `Package.appxmanifest` → `<Identity Publisher="…">` with this exact value **before** submitting.

> **Tip:** For local testing with sideloading you can use `CN=Holger Imbery` plus a self-signed certificate.
> For Store submission the Publisher DN must match Partner Center exactly.

---

## 4 — Create an Azure AD app for API access

The CI submission step authenticates via Azure AD (Entra ID).

1. Go to <https://portal.azure.com> → **Azure Active Directory** → **App registrations** → **New registration**.
   - Name: `PigeonPost Store CI`
   - Supported account types: *Accounts in this organizational directory only*
2. After creation, note the **Application (client) ID** and **Directory (tenant) ID**.
3. **Certificates & secrets** → **New client secret** → copy the generated value immediately.

### 4a — Grant Partner Center API access

1. In Partner Center → **Account settings** → **Tenants** → **Associate Azure AD**.
2. Link your Azure AD tenant.
3. Back in **Account settings** → **User management** → **Add Azure AD applications**.
4. Add the `PigeonPost Store CI` app with the **Manager** role.

---

## 5 — Add GitHub Secrets and Variables

### Secrets  (Settings → Secrets and variables → Actions → Secrets)

| Secret | Value |
|--------|-------|
| `STORE_TENANT_ID` | Azure AD Directory (tenant) ID |
| `STORE_CLIENT_ID` | Azure AD Application (client) ID |
| `STORE_CLIENT_SECRET` | Azure AD client secret value |
| `STORE_APP_ID` | Partner Center App ID (from Step 2) |

### Variables  (Settings → Secrets and variables → Actions → Variables)

| Variable | Description | Default |
|----------|-------------|---------|
| `STORE_ENABLED` | Set to `true` to activate MSIX builds | `false` |
| `STORE_SUBMIT_ENABLED` | Set to `true` to submit to Partner Center | `false` |
| `WINGET_ENABLED` | Set to `true` to activate Winget manifest update | `false` |

> **Recommended approach:**
> 1. Set `STORE_ENABLED=true` first; verify the MSIX artefacts are produced on the next release.
> 2. Submit manually via Partner Center once to establish the app baseline.
> 3. Only set `STORE_SUBMIT_ENABLED=true` after a successful manual submission.

---

## 6 — First manual submission checklist

Before the first automated submission, complete a manual submission in Partner Center:

- [ ] Upload the `.msixbundle` from a `store-bundle` run.
- [ ] Fill in Store listing (description, screenshots, category).
- [ ] Set age rating (IARC questionnaire).
- [ ] Set pricing (free).
- [ ] Submit for certification.

After approval the automated `store-submit` job can take over for all future releases.

---

## 7 — Visual assets

The `Assets\` folder currently contains **placeholder** 0x0067c0 blue PNGs.
Replace them with branded artwork before the first Store submission:

| File | Size | Usage |
|------|------|-------|
| `Assets\Square44x44Logo.png` | 44 × 44 | Taskbar / start menu small tile |
| `Assets\Square44x44Logo.targetsize-32_altform-unplated.png` | 32 × 32 | Notification badge |
| `Assets\Square150x150Logo.png` | 150 × 150 | Start menu medium tile |
| `Assets\Wide310x150Logo.png` | 310 × 150 | Start menu wide tile |
| `Assets\StoreLogo.png` | 50 × 50 | Store listing icon |
| `Assets\SplashScreen.png` | 620 × 300 | App splash screen |

Store certification requires proper branding — generic solid-colour images will be rejected.

---

## 8 — Release flow (after initial setup)

```bash
git tag v1.6.0 -m "Release 1.6.0"
git push origin v1.6.0
```

This triggers both workflows simultaneously:

| Workflow | Gate variable | What it does |
|----------|---------------|--------------|
| `release.yml` | `WINGET_ENABLED` | Builds Velopack installers, uploads to GitHub Release, updates Winget manifest |
| `store.yml` | `STORE_ENABLED` + `STORE_SUBMIT_ENABLED` | Builds MSIX (x64 + ARM64), bundles, submits to Partner Center |

You can enable/disable either workflow independently via the repository variables
without touching the workflow files.
