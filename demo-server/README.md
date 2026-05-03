# PigeonPost Demo Server

A Cloudflare Worker that simulates the PigeonPost Windows server API.
Allows Apple review testers to test the iOS companion app without a Windows machine.

## What it does

Implements the full PigeonPost HTTP protocol:

| Header | Action |
|--------|--------|
| `clipboard: receive` | Returns current clipboard text |
| `clipboard: send` + body | Stores text (plain or JSON) |
| `clipboard: clear` | Clears stored text |
| `filename: <name>` + body | Acknowledges a file upload |

Optional auth: `Authorization: Bearer pigeonpost-demo`

## Deploy in 3 steps

### 1. Install Wrangler

```bash
npm install -g wrangler
wrangler login
```

### 2. Set the bearer token secret

Generate a strong random token and set it as a Cloudflare secret (it is **never stored in the repo**):

```bash
wrangler secret put DEMO_TOKEN
# Paste your privately generated token when prompted
```

Share this token privately with Apple review testers — anyone without it gets a 401.

### 3. Create the KV namespace (enables live clipboard state)

```bash
cd demo-server
wrangler kv namespace create CLIPBOARD
```

Copy the `id` from the output and uncomment + fill in the `[[kv_namespaces]]` block in `wrangler.toml`.

### 4. Deploy

```bash
wrangler deploy
```

Wrangler prints your worker URL, e.g.:
```
https://pigeonpost-demo.<your-subdomain>.workers.dev
```

## iOS App Setup (for testers)

In the PigeonPost Companion iOS app:

- **Host**: `pigeonpost-demo.<your-subdomain>.workers.dev`
- **Port**: `443` (HTTPS) — or leave blank if the app auto-detects
- **Use HTTPS**: on
- **Bearer Token**: the secret token you set with `wrangler secret put DEMO_TOKEN`

The worker is available 24/7 on Cloudflare's free tier with no sleep/cold-start.
