# Usage data: contract for the apps

This document describes everything an app on any system needs to show each service's usage: **Claude** (sections 1–3) and **Grok Bot** (section 5). The macOS app (`macos/Sources/UsageCore`) is the reference implementation; each service is a `UsageProvider` there.

> ⚠️ **None of these endpoints are public.** They are the ones Claude Code itself (`/usage`) and Grok Bot itself (its Usage screen) call. They can change without notice, so decode everything leniently (optional fields, ignore what you do not know, skip invalid list items).

## 1. Claude: login (Claude Code OAuth token)

The app has no login of its own: it reuses Claude Code's login, read only.

| System | Where Claude Code keeps its login |
|---|---|
| macOS | Keychain, generic password with service `Claude Code-credentials`. Read it with `/usr/bin/security find-generic-password -s "Claude Code-credentials" -w`. |
| Linux / Windows | File `~/.claude/.credentials.json` (`%USERPROFILE%\.claude\.credentials.json` on Windows). If `CLAUDE_CONFIG_DIR` is set, use `$CLAUDE_CONFIG_DIR/.credentials.json`. *Not verified on these systems yet.* |

Contents (only the fields used):

```json
{
  "claudeAiOauth": {
    "accessToken": "…",
    "expiresAt": 1762790400000,
    "subscriptionType": "max",
    "rateLimitTier": "default_claude_max_5x"
  }
}
```

- `expiresAt` is in **milliseconds** since 1970.
- Plan name: a `rateLimitTier` containing `max_20x` becomes "Max 20x", containing `max_5x` becomes "Max 5x". Otherwise use `subscriptionType` with its first letter capitalized ("Pro", "Team"…).
- **Never refresh the token** with the `refreshToken`. Refreshing rotates the refresh token and would sign Claude Code out. If the token expired, show the last cached data and ask the user to open Claude Code.
- Never write the token to a log, cache or file.

## 2. Claude: request

```
GET https://api.anthropic.com/api/oauth/usage
Authorization: Bearer <accessToken>
anthropic-beta: oauth-2025-04-20
Accept: application/json
```

| Status | What to do |
|---|---|
| 200 | Decode, show and cache |
| 401 / 403 | Invalid or expired login (compare `expiresAt` with the current time to pick the message) |
| 429 | Wait 10 minutes before trying again |
| other | Show an error and try on the next cycle |

Refresh cadence on the Mac: every 3 minutes, when the popover opens (if the data is older than 60 s) and when the computer wakes up.

## 3. Claude: response

Full example with made-up numbers: [`sample-usage.json`](sample-usage.json). The real response has dozens of other fields (mostly `null`) that can be ignored.

### `limits`: the main list

The same list as claude.ai → Settings → Usage. Show it in the order received.

```json
{
  "kind": "weekly_scoped",
  "group": "weekly",
  "percent": 27,
  "severity": "normal",
  "resets_at": "2026-11-12T02:59:59.554553+00:00",
  "scope": { "model": { "id": null, "display_name": "Fable" }, "surface": null },
  "is_active": false
}
```

| `kind` | Title |
|---|---|
| `session` | Current session (5-hour window) |
| `weekly_all` | Weekly · all models |
| `weekly_scoped` | Weekly · `scope.model.display_name` (or `scope.surface.display_name`) |
| other | `kind` with `_` replaced by spaces and capitalized, plus the scope name if any |

- `percent`: 0 to 100, already a percentage.
- `severity`: seen so far: `normal` and `warning`. Treat values containing "crit", "exceed", "high" or "block" as critical. For unknown values, use the percentage: 90 or more is critical, 75 or more is a warning.
- `resets_at`: ISO-8601 with **microseconds** (`.554353`), which some parsers reject. Round to the minute before showing it: limits that reset together arrive as `02:59:59` and `03:00:00`.

### Older fields (fallback)

If `limits` is missing, build the list from `five_hour`, `seven_day`, `seven_day_opus` and `seven_day_sonnet`, which carry `{ "utilization": 0-100, "resets_at": "…" }`.

### `seven_day_breakdown`: where the usage went

```json
{ "rows": [ { "key": "claude_code", "display_name": "Claude Code", "percent": 87 }, … ] }
```

The split of weekly usage between products. Values add up to about 100 and are **not** percentages of the limit. On the Mac, 0% rows are hidden, the list is sorted from largest to smallest, `chat` shows as "Chat" and `other` as "Other".

## 4. Menu bar

Each service becomes one block in the menu bar, with one or two numbers, all in the menu bar's color. Blocks are separated by a thin vertical line. The style, stored in `menuBar.style`, applies to all of them:

| `menuBar.style` | Gauge before the numbers |
|---|---|
| `icon` (default) | monochrome glyph (the white artwork of the app's icon, without its background) inside a 19 pt progress ring |
| `percent` | 14 pt outlined circle with a pie wedge that fills up (the version 0.1 look) |

Each service's choice is stored in `menuBar.<service>`:

| Value | Numbers | The ring shows |
|---|---|---|
| `session` | session (`kind` `session`) | session |
| `weekly` | weekly (`kind` `weekly_all`; for Grok Bot, its only weekly limit) | weekly |
| `both` | session on top, weekly below | session |
| `hidden` | the block is not shown | — |

Claude offers all four options (default `both`); Grok Bot offers `weekly` (default) and `hidden`. If the chosen limit is missing from the response, use the other one; if neither is there, use the highest. Without data, show `–` (loading) or `!` (error). The ring or pie color follows the limit's severity (normal, warning or critical) unless `menuBar.colorAlerts` is `false`, in which case everything stays in the menu bar color.

**Windows** has no shared menu bar: each service is its own tray icon (a 16–32 px square), so it shows one value only. Choices there are `session`, `weekly` or `hidden` per service, and the style is `Icon` (glyph in the ring) or `Percent` (the number in the ring, without `%`). The tooltip lists every limit. Settings live in `%APPDATA%\AIUsageBar\settings.json`.

Text follows the `language` preference (`en` or `pt`). On a fresh install it starts from the system language: Portuguese if the first preferred language starts with `pt`, English otherwise.

## 5. Grok Bot

Grok Bot (`/Applications/Grok Bot.app`, bundle `com.anysphere.sand`) is an Electron app built on Cursor's infrastructure. Format verified with version 0.79.3.

### Login

| System | Where it lives |
|---|---|
| macOS | `~/Library/Application Support/Grok Bot/sand-secrets.json`, with values encrypted by Electron's `safeStorage`. The key is in the Keychain: service `Grok Bot Safe Storage`, account `Grok Bot Key`. |
| Windows | `%APPDATA%\Grok Bot\sand-secrets.json`. The key is in `%APPDATA%\Grok Bot\Local State`: `os_crypt.encrypted_key` is base64(`DPAPI` + blob); `CryptUnprotectData` for the current user returns a 32-byte AES key. Implemented in `windows/`, *not verified on a real PC yet*. |
| Linux | *Not verified.* Electron uses libsecret/kwallet, and the file should sit in the app's `userData` folder. |

File structure (only the fields used):

```json
{
  "version": 1,
  "cursor-accounts": "{\"active\":\"<scope>\",\"accounts\":{\"<scope>\":{\"cursor-access-token\":\"<base64>\"}}}"
}
```

- `cursor-accounts` is JSON **inside a string**. Take `accounts[active]["cursor-access-token"]`.
- Older files keep `cursor-access-token` at the top level, sometimes as `scoped:v1:<64 hex>:<base64>`. `plaintext:v1:<base64>` values are not encrypted.
- Decryption on macOS (Chromium `OSCrypt`): the base64 blob starts with `v10`. The rest is AES-128-CBC with PKCS#7, key = PBKDF2-HMAC-SHA1(Keychain password, `saltysalt`, 1003 iterations, 16 bytes) and an IV of 16 spaces (`0x20`).
- Decryption on Windows: the blob is `v10` + 12-byte nonce + ciphertext + 16-byte tag, AES-256-GCM with the DPAPI-protected key above.
- The token is a JWT. Its `exp` claim says when it expires; do not use the refresh token.
- Reading the key makes macOS show a permission prompt. Read it once per run and keep the key **in memory only**.

### Request (Connect RPC over JSON)

```
POST https://api2.cursor.sh/aiserver.v1.DashboardService/GetSandUsageStatus
Authorization: Bearer <token>
Content-Type: application/json
Connect-Protocol-Version: 1
x-ghost-mode: true

{}
```

HTTP statuses are handled as in section 2.

### Response

Example with made-up numbers: [`sample-grok-usage.json`](sample-grok-usage.json). It is proto3 JSON: fields are camelCase, default values (`false`, `0`) are omitted and 64-bit integers arrive as strings.

| Field | Use |
|---|---|
| `usagePercent` | **Weekly** usage, 0 to 100 (`0.3` = 0.3%). Missing when there is no individual limit. |
| `nextResetTimestampUtc` | When the weekly limit resets (RFC 3339). |
| `hasAvailableUsage` | `false` = limit used up; show it as critical. |
| `usesPooledEnterpriseAllowance` | `true` = team allowance; do not show a percentage. |
| `availableBankedResetCount` | Banked resets the user can redeem (string). |
| `grokPlanLabel`, `cursorPlanName` | Plan name. `grokPlanLabel` is often the generic "Grok Bot Plan"; in that case use `cursorPlanName` ("Premium"). |
| `onDemandSettings.enabled` | On-demand usage is on. |

There is also `DashboardService/GetGrokBotUsageBreakdown`, which splits usage by day, source and bot, in parts per million of the weekly limit (`weeklyLimitPpm`), of the billing cycle (`billingCyclePpm`) or in cents (`billedCents`). The app does not use it yet.
