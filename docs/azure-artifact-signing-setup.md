# Azure Artifact Signing Setup

This guide explains how to configure Azure Artifact Signing so the PigeonPost
release workflow signs all installers automatically.  
Signed binaries are trusted immediately by Windows SmartScreen and are not
blocked by Chrome, Edge, or Firefox on download.

---

## Cost

| Plan | Price | Signatures included |
|------|-------|---------------------|
| Basic | $9.99 / month | 5,000 |

Each release signs 4 files (2 architectures × inner exe + installer).
The Basic plan comfortably covers hundreds of releases per month.

Pricing is **per signing operation**, not per download.

---

## Step 1 — Create an Azure Artifact Signing account

1. Open the [Azure portal](https://portal.azure.com) and sign in.
2. Search for **Artifact Signing** and open the service.
3. Click **Create** and fill in:
   - **Subscription** — your Azure subscription
   - **Resource group** — create new or use existing
   - **Account name** — e.g. `pigeonpost-signing`
   - **Region** — choose the region closest to you (note the endpoint URL it shows)
4. Click **Review + create → Create**.

### Create a certificate profile

1. Open your new Artifact Signing account.
2. Go to **Certificate profiles → Add**.
3. Select **Public Trust** (required for SmartScreen).
4. Give it a name, e.g. `pigeonpost-profile`.
5. Complete the identity verification steps that Microsoft requires for Public Trust.

> **Note:** Public Trust profiles require identity verification by Microsoft.
> This is a one-time process and typically takes 1–3 business days.

---

## Step 2 — Create a Microsoft Entra App Registration

1. In the Azure portal, go to **Microsoft Entra ID → App registrations → New registration**.
2. Name it e.g. `pigeonpost-github-signing` and click **Register**.
3. Note the **Application (client) ID** and **Directory (tenant) ID** — you will need these later.

### Add a federated credential (OIDC)

1. In your App Registration, go to **Certificates & secrets → Federated credentials → Add credential**.
2. Select **GitHub Actions deploying Azure resources**.
3. Fill in:
   - **Organization**: `holgerimbery`
   - **Repository**: `PigeonPost`
   - **Entity type**: `Tag`
   - **GitHub tag**: `v*`
   - **Name**: e.g. `pigeonpost-release-tags`
4. Click **Add**.

This allows GitHub Actions to authenticate as your App Registration without
any long-lived secrets — only when a `v*` tag is pushed.

---

## Step 3 — Assign the signing role

1. Open your **Artifact Signing account** in the Azure portal.
2. Go to **Access control (IAM) → Add role assignment**.
3. Search for and select **Artifact Signing Certificate Profile Signer**.
4. Click **Next**, then under **Members** select **User, group, or service principal**.
5. Search for the App Registration you created (e.g. `pigeonpost-github-signing`) and select it.
6. Click **Review + assign**.

---

## Step 4 — Configure GitHub Secrets and Variables

In your GitHub repository go to **Settings → Secrets and variables → Actions**.

### Secrets

| Name | Value |
|------|-------|
| `AZURE_CLIENT_ID` | Application (client) ID of the App Registration |
| `AZURE_TENANT_ID` | Directory (tenant) ID of the App Registration |
| `AZURE_SUBSCRIPTION_ID` | Azure subscription ID |

### Variables

| Name | Example value |
|------|--------------|
| `AZURE_SIGNING_ENDPOINT` | `https://eus.codesigning.azure.net/` |
| `AZURE_SIGNING_ACCOUNT` | `pigeonpost-signing` |
| `AZURE_SIGNING_PROFILE` | `pigeonpost-profile` |

> The endpoint URL depends on the region you chose in Step 1.  
> Common endpoints:
> - East US: `https://eus.codesigning.azure.net/`
> - West Europe: `https://weu.codesigning.azure.net/`
> - West US: `https://wus.codesigning.azure.net/`

---

## How the workflow uses these

The release workflow (`release.yml`) checks whether `AZURE_SIGNING_ACCOUNT` is
set. If it is, signing runs automatically on every release tag:

1. **Before `vpk pack`** — signs all EXEs in the publish folder (inner binaries + Velopack stubs).
2. **After `vpk pack`** — signs the `PigeonPost-win-{arch}-Setup.exe` installer.

If the variable is not set, the workflow skips signing and still produces a
working (unsigned) release. This means forks and local builds are unaffected.

---

## Verifying a signed release

After a signed release is published, you can verify the signature in PowerShell:

```powershell
Get-AuthenticodeSignature .\PigeonPost-win-x64-Setup.exe
```

The `SignerCertificate` field should show your organisation name and the
status should be `Valid`.
