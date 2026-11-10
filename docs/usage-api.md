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

Claude offers all four options (default `both`); Grok Bot and GitHub Copilot offer `weekly` (default; for Copilot it is the monthly premium-request allowance, labelled "Monthly") and `hidden`. If the chosen limit is missing from the response, use the other one; if neither is there, use the highest. Without data, show `–` (loading) or `!` (error). The ring or pie color follows the limit's severity (normal, warning or critical) unless `menuBar.colorAlerts` is `false`, in which case everything stays in the menu bar color.

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

## 6. GitHub Copilot

> **Status: not verified against a real account.** `copilot_internal/user` is what GitHub's own editor plugins call. It is not documented by GitHub; the field names below come from third-party tools that use it (CodexBar, opencode-mystatus and similar), and [`sample-copilot-usage.json`](sample-copilot-usage.json) is an illustration of that shape with made-up numbers, not a captured response. Check a real response (`AIUsageBar --dump --raw`) before relying on it, and treat any change as a format change (`decoding` error).

Copilot has no desktop app of its own; the login belongs to an editor plugin or to the `gh` CLI.

### Login

Read-only, first match wins; only `github.com` entries count (not GitHub Enterprise hosts):

1. `apps.json`, then `hosts.json` in the Copilot plugin folder: `%LOCALAPPDATA%\github-copilot\` on Windows, `~/.config/github-copilot/` on macOS and Linux. `apps.json` is `{"github.com:<client id>": {"user": "…", "oauth_token": "ghu_…"}}`, `hosts.json` is `{"github.com": {"user": "…", "oauth_token": "…"}}`. Take `oauth_token`.
2. The `GH_TOKEN`, then `GITHUB_TOKEN` environment variable.
3. The output of `gh auth token --hostname github.com` (spawns a process: cache the result for about 10 minutes, one minute when nothing was found, because availability is checked on every refresh; on macOS add `/opt/homebrew/bin` and `/usr/local/bin` to `PATH`, since a Finder-launched app has a minimal one).

Which token types the endpoint accepts is **unconfirmed**: the editor plugin's `ghu_` token is the expected one; `gh` and personal tokens may be refused (HTTP 401/403 → `unauthorized`). Never write, refresh, cache or log the token.

If none is found the service is hidden (like Grok Bot), and the Agents tab shows how to sign in.

### Request

```
GET https://api.github.com/copilot_internal/user
Authorization: token <token>
Accept: application/json
Editor-Version: vscode/1.99.0
X-GitHub-Api-Version: 2022-11-28
```

HTTP statuses are handled as in section 2. A 403 can also mean the account has no Copilot seat.

### Response

| Field | Use |
|---|---|
| `copilot_plan` | Plan: `individual_pro` → "Pro", `individual_pro_plus` → "Pro+", `business` → "Business", `free` → "Free" (drop `individual_`/`copilot_`, `_plus` → `+`, other `_` → space, capitalize). |
| `quota_reset_date` | When the monthly allowance resets (`2026-12-01`, a date without time: read as UTC). |
| `quota_snapshots.premium_interactions` | **Premium requests**, the limit that runs out. Shown as `kind` `premium_requests`, group `weekly` (so the monthly ring uses it). |
| `quota_snapshots.chat`, `.completions` | Shown (kinds `chat`, `completions`, group `other`) only when not unlimited. |
| `<quota>.unlimited` | `true` = skip this quota, there is nothing to measure. |
| `<quota>.percent_remaining` | Percent **left**: used = `100 − percent_remaining`. Preferred. |
| `<quota>.entitlement`, `<quota>.remaining` | Fallback when there is no percent: used = `(entitlement − remaining) / entitlement × 100`. |
| `<quota>.overage_count` | Premium requests beyond the allowance; the app does not show it yet. |
| `monthly_quotas`, `limited_user_quotas`, `limited_user_reset_date` | **Free plan** shape, used only when `quota_snapshots` gives nothing: limit and remaining per `chat` / `completions`; used = `(limit − remaining) / limit × 100`. |

If no quota can be read (organization-managed seats may not report one) the response gives no snapshot and the app reports a format error. Percentages are clamped to 0–999. Severity uses the usual thresholds (75% warning, 90% critical). The cached response holds the usage data only.

## 7. Tabs and first-run setup (UI contract)

Once there is more than one agent the popup shows **one agent at a time**:

- **Strip:** shown only when two or more agents are connected, so a single agent looks exactly as before. One tab per connected agent (`ProviderID.shortName`: "Claude", "Copilot", "Grok") plus a trailing **＋** tab, which keeps a narrow slot while the agent tabs share the rest equally.
- **Tab icon:** 16 pt, beside the name, the same glyph as the tray / menu bar (white artwork cut out of the installed app's icon), drawn in the theme's text color at reduced opacity when not selected. With no app to borrow from (Copilot) it is the agent's initial ("Co") in a thin circle. When the name does not fit, it goes first (icon and number stay): Windows hides the name of unselected tabs with four or more agents; macOS drops it whenever the tab is too narrow.
- **Tab summary:** the agent's *fullest* limit as a percentage, in the severity color; `!` (warning) when the agent failed and has nothing to show, `–` while loading, and `⚠` after the number when it has data but the last refresh failed. Spoken label: "Claude, 83% used" / "no data".
- **Which tab shows (`resolve`):** nothing connected, or onboarding not done → Agents. Otherwise the requested tab if it is still connected (or Agents if asked), else the first connected agent. The last agent tab is saved (`SelectedProvider` / `selectedProvider`).
- **Keys:** Left/Right arrows move between tabs, wrapping (also Ctrl+Tab on Windows). On Windows, a provider's own tray icon opens the popup on its tab.
- **Agents tab:** every supported agent, connected or not. Connected: ✓, "Connected · <plan>", and a **Show** checkbox (`HiddenProviders` / `hiddenProviders`; a hidden agent leaves the tabs, the tray and the refresh). Not connected: ⚠, "No login found" and the agent's setup hint. A **Done** button closes the first run (`OnboardingDismissed` / `onboardingDismissed`) and selects the first agent; afterwards the same button reads **Back**, which matters with one agent because there is no strip to leave by. Reachable from the gear menu → "Set up agents…".
- **First run:** `OnboardingDismissed` is false only for a fresh install. A settings file or cache saved by an older version counts as already onboarded, so updating never forces the setup tab.
- **Detection (`detect`):** `connected` when the login is found. It defaults to "is available"; Claude overrides it because it is always shown (it checks the Keychain/credentials file). Statuses are re-read on every refresh, so signing in elsewhere makes the tab appear without a restart.
- **Popup height** changes with the selected tab; on Windows the popup is re-anchored to the taskbar after a tab switch.
