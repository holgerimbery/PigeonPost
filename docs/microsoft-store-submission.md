# Microsoft Store Submission Guide for PigeonPost

This guide walks through the one-time setup needed to publish PigeonPost
to the Microsoft Store and activate the automated `store.yml` CI workflow.

**You will need:**
- A browser logged in to your Microsoft account (same one used for Partner Center)
- A browser tab open to the Azure portal (<https://portal.azure.com>)
- The `Package.appxmanifest` file open in an editor

---

## Step 1 — Partner Center: enroll (if not done yet)

1. Open <https://partner.microsoft.com/dashboard>
2. If prompted to enroll: choose **Individual developer** → complete registration (free for free apps)

---

## Step 2 — Partner Center: find your Publisher DN

> This is the value that goes into `Package.appxmanifest` → `<Identity Publisher="…">`

1. Open <https://partner.microsoft.com/dashboard/account/v3/settings/account>
2. In the left sidebar click **Account settings** → **Legal info** (or **Developer profile** for individuals)
3. Find the field labelled **Publisher display name** — note it, but what you need is the **Publisher DN**
4. Scroll to find the field labelled **Publisher** — it looks like:
   ```
   CN=Holger Imbery, O=Holger Imbery, L=Berlin, C=DE
   ```
   *(The exact format depends on how you registered — it may be just `CN=Holger Imbery`)*
5. Copy the entire value including `CN=`

**Update `Package.appxmanifest`:**
```xml
<Identity
  Name="HolgerImbery.PigeonPost"
  Publisher="CN=Holger Imbery, O=Holger Imbery, L=Berlin, C=DE"   <!-- ← paste your exact DN here -->
  Version="1.5.0.0"
  ProcessorArchitecture="neutral" />
```

---

## Step 3 — Partner Center: create the app listing + get STORE_APP_ID

1. Open <https://partner.microsoft.com/dashboard/windows/overview>
2. Click **+ New product** → **App**
3. Enter name: **PigeonPost** → click **Reserve product name**
4. After creation, look at the browser URL — it contains your App ID:
   ```
   https://partner.microsoft.com/en-us/dashboard/products/<APP_ID>/overview
   ```
5. Copy `<APP_ID>` — this is your `STORE_APP_ID` secret

---

## Step 4 — Azure portal: create an app registration

> This gives the CI workflow credentials to call the Partner Center API.

1. Open <https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps/CreateApplicationBlade>
2. Fill in:
   - **Name:** `PigeonPost Store CI`
   - **Supported account types:** *Accounts in this organizational directory only (single tenant)*
   - **Redirect URI:** leave blank
3. Click **Register**
4. On the app overview page, copy:
   - **Application (client) ID** → this is `STORE_CLIENT_ID`
   - **Directory (tenant) ID** → this is `STORE_TENANT_ID`
5. In the left sidebar click **Certificates & secrets** → **Client secrets** → **+ New client secret**
   - Description: `PigeonPost CI`
   - Expiry: 24 months
6. Click **Add** — copy the **Value** column immediately (it is never shown again)
   → this is `STORE_CLIENT_SECRET`

---

## Step 5 — Partner Center: grant the Azure AD app access

1. Open <https://partner.microsoft.com/dashboard/account/v3/usermanagement>
2. Click **Azure AD applications** tab → **+ Add Azure AD application**
3. Search for `PigeonPost Store CI` → select it → click **Next**
4. Assign role: **Manager**
5. Click **Save**

---

## Step 6 — GitHub: add secrets and variables

Open <https://github.com/holgerimbery/PigeonPost/settings/secrets/actions>

Click **New repository secret** for each:

| Secret name | Value |
|-------------|-------|
| `STORE_TENANT_ID` | Directory (tenant) ID from Step 4 |
| `STORE_CLIENT_ID` | Application (client) ID from Step 4 |
| `STORE_CLIENT_SECRET` | Client secret value from Step 4 |
| `STORE_APP_ID` | App ID from Step 3 |

Then open <https://github.com/holgerimbery/PigeonPost/settings/variables/actions>

Click **New repository variable** for each:

| Variable name | Value |
|---------------|-------|
| `STORE_ENABLED` | `true` |
| `STORE_SUBMIT_ENABLED` | `true` *(set only after first manual submission passes)* |

---

## Step 7 — Update Package.appxmanifest and commit

After completing Step 2, update the manifest Publisher DN and push:

```bash
# Edit Package.appxmanifest with the correct Publisher DN, then:
git add Package.appxmanifest
git commit -m "chore: set correct Partner Center Publisher DN in manifest"
git push origin feature/microsoft-store
```

---

## Step 8 — First manual Store submission

Before enabling `STORE_SUBMIT_ENABLED`, do one manual submission:

1. Push a release tag to trigger `store-bundle` (set `STORE_ENABLED=true`, `STORE_SUBMIT_ENABLED=false`)
2. Download the `.msixbundle` from the GitHub Release
3. In Partner Center → your app → **Start your submission**
4. Upload the `.msixbundle` in the **Packages** section
5. Complete: Store listing, age rating (IARC), pricing (free), submit
6. After certification passes → set `STORE_SUBMIT_ENABLED=true` for fully automated future releases

---

## Partner Center: Associate Azure AD tenant (if needed)

If Step 5 shows no Azure AD applications tab:

1. Open <https://partner.microsoft.com/dashboard/account/v3/settings/tenants>
2. Click **Associate Azure AD tenant**
3. Sign in with your Azure account and confirm the association
4. Then repeat Step 5

---

## Release flow (after full setup)

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
