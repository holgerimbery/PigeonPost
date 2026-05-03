/**
 * PigeonPost Demo Server — Cloudflare Worker
 *
 * Simulates the PigeonPost Windows server API so Apple review testers
 * can test the iOS companion app without a Windows machine.
 *
 * Endpoints (same protocol as the real server):
 *   Header  clipboard: receive  → returns current clipboard text
 *   Header  clipboard: send     → stores text as clipboard (body = plain text or JSON)
 *   Header  clipboard: clear    → clears stored clipboard
 *   Header  filename: <name>    → accepts a file upload (acknowledged only)
 *
 * Auth (optional):
 *   Header  Authorization: Bearer pigeonpost-demo
 *   Leave blank in the iOS app if no token is set.
 *
 * KV namespace "CLIPBOARD" stores the current clipboard value per demo session.
 * Falls back to a static demo string if KV is not bound.
 */

const DEMO_TOKEN   = 'pigeonpost-demo';
const DEMO_DEFAULT = 'Hello from PigeonPost! 📋\n\nThis is demo clipboard content served by the PigeonPost mock server.\nThe iOS app can send, receive and clear this text in real time.';

const cors = {
  'Access-Control-Allow-Origin':  '*',
  'Access-Control-Allow-Methods': 'GET, POST, PUT, OPTIONS',
  'Access-Control-Allow-Headers': '*',
};

function resp(body, status = 200, extra = {}) {
  return new Response(body, { status, headers: { ...cors, ...extra } });
}

export default {
  async fetch(request, env) {
    // Pre-flight
    if (request.method === 'OPTIONS') return resp(null, 204);

    // Auth — only enforced when a token is provided in the request
    const authHeader = (request.headers.get('Authorization') || '').trim();
    if (authHeader) {
      const expected = `Bearer ${DEMO_TOKEN}`;
      if (authHeader !== expected) {
        return resp('Unauthorized', 401, { 'WWW-Authenticate': 'Bearer' });
      }
    }

    // ── Clipboard actions ──────────────────────────────────────────────
    const clipboardAction = (request.headers.get('clipboard') || '').trim().toLowerCase();
    if (clipboardAction) {
      switch (clipboardAction) {
        case 'receive': {
          const text = (env.CLIPBOARD ? await env.CLIPBOARD.get('value') : null) ?? DEMO_DEFAULT;
          return resp(text);
        }

        case 'send': {
          const raw  = await request.text();
          const text = extractText(raw);
          if (env.CLIPBOARD) await env.CLIPBOARD.put('value', text, { expirationTtl: 3600 });
          return resp('Data copied to clipboard');
        }

        case 'clear': {
          if (env.CLIPBOARD) await env.CLIPBOARD.delete('value');
          return resp('Clipboard cleared');
        }

        default:
          return resp('Invalid clipboard action', 400);
      }
    }

    // ── File upload ────────────────────────────────────────────────────
    const filename = request.headers.get('filename')
                  || new URL(request.url).searchParams.get('filename');
    if (filename) {
      // Read and discard the body (simulate acceptance)
      await request.arrayBuffer();
      return resp(`File "${filename}" received by demo server`);
    }

    // ── Health / info ──────────────────────────────────────────────────
    return resp(
      JSON.stringify({ server: 'PigeonPost Demo', version: '1.4.0', status: 'ok' }),
      200,
      { 'Content-Type': 'application/json' }
    );
  },
};

/**
 * Accepts three body formats (matching the real server):
 *   1. Plain text:           hello world
 *   2. JSON string literal:  "hello world"
 *   3. JSON object with key: {"text":"hello"}
 */
function extractText(raw) {
  const trimmed = raw.trim();
  try {
    const parsed = JSON.parse(trimmed);
    if (typeof parsed === 'string') return parsed;
    if (parsed && typeof parsed.text === 'string') return parsed.text;
  } catch (_) { /* not JSON */ }
  return trimmed;
}
