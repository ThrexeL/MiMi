# Portable AI assistants — 2026-11-25

## Providers

Quick Access has a single **AI** tile. Its picker shows:

- **Codex** — MiMi's embedded read-only conversation with one-shot approvals.
- **Claude** — Claude Code opened in its own foreground console window.
- **OpenCode** — the `opencode` terminal assistant opened in the MiMi workspace.
- **Grok** — the `grok` terminal assistant opened in the MiMi workspace.
- **Custom AI** — either a user-configured executable/script/PATH command or a text-only
  OpenAI-compatible API chat.

Codex, Claude, OpenCode, and Grok are discovered on `PATH`. MiMi contains no hard-coded
user paths. Missing providers are disabled with an honest explanation.

Custom API presets include **Grok API**, **OpenAI API**, **OpenRouter**, and a custom
compatible endpoint. Endpoint, model, system prompt, display name, and default provider
are stored in per-user MiMi settings. Legacy Quick Access hidden-page value `codex`
continues to migrate to `ai`.

Settings, Codex, API chat, and the AI picker are taskbar-visible, use the MiMi application
icon, and are brought to the foreground when opened.

## Configuration

Open **Settings → Integrations → AI assistants**:

1. Choose the default assistant.
2. Optionally set a custom display name.
3. For **External command or app**, browse to an executable/script or enter a PATH command,
   then add optional arguments.
4. For **API (OpenAI-compatible)**:
   - Choose a preset or a custom compatible API.
   - Enter the API base URL (MiMi appends `/chat/completions`) or a full completions URL.
   - Enter the exact model identifier offered by that provider.
   - Enter the API key. It is encrypted with Windows DPAPI for the current user when the
     field loses focus or Settings closes. Leaving the field blank keeps the saved key.
   - Optionally set MIMI_AI_API_KEY instead of saving a key in MiMi.
   - Optionally edit the text-only system prompt.
5. Refresh availability, then open **Custom AI** from the AI picker.

API requests use `Authorization: Bearer <key>` and an OpenAI-compatible
`POST /chat/completions` body with `stream: false`. Both string content and text-part
arrays are accepted in responses. A key is never sent to a non-loopback HTTP endpoint;
use HTTPS for remote APIs.

## Approval-card layout fix

Approval cards measure their content without the previous pill height constraining the
new template. A second measurement runs after the approval template and bindings load,
so a command block becoming visible can no longer leave Approve/Deny/Copy session below
the clipped pill edge. Approval IDs, expiry, single-resolution, and fail-closed behavior
are unchanged.

## Safety

- API keys are stored only as current-user DPAPI ciphertext and are never logged or shown
  after saving. Ciphertext copied to another Windows user/PC is not portable; that user
  must enter their own key or use MIMI_AI_API_KEY.
- API chats are **text only**. MiMi does not give an API model local files, shell access,
  tool execution, or approval payloads. Prompts and response bodies are not logged.
- API errors are sanitized; provider response bodies and authorization headers are not
  included in error messages or logs.
- External AIs and custom commands run with the current user's permissions in the selected
  MiMi workspace. They are not MiMi's read-only sandbox. Do not put secrets in command
  arguments; those arguments are ordinary per-user settings, not encrypted secret storage.
- Codex keeps its existing read-only sandbox, one-shot approvals, and fail-closed approval
  path. Custom commands are explicitly user-configured; MiMi does not download or execute
  remote content by itself.

## Verification (19:15–19:27 IST, 2026-11-25)

- Full test suite: **424 passed, 0 failed, 0 skipped**.
- Debug and Release builds: **0 warnings, 0 errors**.
- Release `MiMi.exe --selftest`: **ALL PASSED**.
- Approval regression reproduces the old 236-DIP constraint, performs the post-load
  measurement, and asserts both Approve and Deny bottoms are inside the pill. The rendered
  card shows all three actions: `artifacts/mimi-final-qa/approval-buttons-fit.png`.
- The API settings panel renders with preset, endpoint, model, protected-key status, and
  system prompt: `artifacts/mimi-final-qa/api-settings-panel.png`.
- A local fake HTTP handler verified the OpenAI-compatible URL, Bearer header, request
  messages, response parsing, plaintext-HTTP rejection, and sanitized errors. No paid API
  quota or live provider credentials were used.
- DPAPI tests verified that plaintext keys never appear in `settings.json`, a saved key
  round-trips for the current user, and a replacement key is encrypted on focus loss.
- Icon tests verify seven embedded resolutions and that taskbar windows receive the
  selected 64-pixel frame.
- A live WPF probe using MiMi's actual Settings and AI window classes showed the gold MiMi
  taskbar icon and active taskbar underline:
  `artifacts/mimi-taskbar-icons-live.png` and `artifacts/mimi-taskbar-icon-crop.png`.
- OpenCode and Codex were detected on this PC. Claude and the Grok CLI were not on PATH,
  so their external-process launch paths were not live-tested. Grok/OpenCode command names
  were checked against official documentation; no provider was installed automatically.
- Final MiMi process: PID `19544`, launched from the verified Release build without taking
  foreground focus.

Provider references checked during implementation:

- OpenCode CLI: https://opencode.ai/docs/cli/
- Grok CLI: https://docs.x.ai/build/overview
- xAI OpenAI-compatible API: https://docs.x.ai/developers/rest-api-reference/inference

## Previous verification (18:08 IST, before API/icon/layout additions)

- Full test suite: **417 passed, 0 failed, 0 skipped**.
- Debug and Release builds: **0 warnings, 0 errors**.
- `MiMi.exe --selftest`: **ALL PASSED**.
- Live desktop check: Settings, AI picker, and Codex opened in the foreground and reported
  taskbar visibility. Codex was detected; Claude and Custom reported unavailable until
  configured.

Previous visual evidence:

- `C:\Users\basit\MiMi\artifacts\ai-quick-refresh\island-pill-quick-access.png`
- `C:\Users\basit\MiMi\artifacts\ai-quick-refresh\live-quick-ai.png`
- `C:\Users\basit\MiMi\artifacts\ai-quick-refresh\live-ai-picker.png`
- `C:\Users\basit\MiMi\artifacts\ai-quick-refresh\live-codex.png`
- `C:\Users\basit\MiMi\artifacts\ai-quick-refresh\live-settings-ai.png`
