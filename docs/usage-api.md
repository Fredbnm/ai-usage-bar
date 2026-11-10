# Dados de uso: contrato para os apps

Este documento descreve tudo o que um app de qualquer sistema precisa para mostrar o uso de cada serviço: **Claude** (seções 1–3) e **Grok Bot** (seção 5). O app de macOS (`macos/Sources/UsageCore`) é a implementação de referência; cada serviço é um `UsageProvider` ali.

> ⚠️ **Nenhum destes endpoints é público.** São os que o próprio Claude Code (`/usage`) e o próprio Grok Bot (tela de Uso) chamam. Podem mudar sem aviso, então decodifique tudo de forma tolerante (campos opcionais, ignorar o que não conhece, pular itens inválidos de listas).

## 1. Claude: login (token OAuth do Claude Code)

O app não tem login próprio: ele reaproveita o login do Claude Code, só para leitura.

| Sistema | Onde o Claude Code guarda o login |
|---|---|
| macOS | Keychain, senha genérica com serviço `Claude Code-credentials`. Leia com `/usr/bin/security find-generic-password -s "Claude Code-credentials" -w`. |
| Linux / Windows | Arquivo `~/.claude/.credentials.json` (`%USERPROFILE%\.claude\.credentials.json` no Windows). Se a variável `CLAUDE_CONFIG_DIR` existir, use `$CLAUDE_CONFIG_DIR/.credentials.json`. *Ainda não verificado nesses sistemas.* |

Conteúdo (só os campos usados):

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

- `expiresAt` está em **milissegundos** desde 1970.
- Nome do plano: `rateLimitTier` contendo `max_20x` vira "Max 20x", contendo `max_5x` vira "Max 5x". Nos outros casos, use `subscriptionType` com a primeira letra maiúscula ("Pro", "Team"…).
- **Nunca renove o token** com o `refreshToken`. A renovação troca o refresh token, e o Claude Code ficaria deslogado. Se o token expirou, mostre o último dado salvo e peça para o usuário abrir o Claude Code.
- Nunca grave o token em log, cache ou arquivo.

## 2. Claude: requisição

```
GET https://api.anthropic.com/api/oauth/usage
Authorization: Bearer <accessToken>
anthropic-beta: oauth-2025-04-20
Accept: application/json
```

| Status | O que fazer |
|---|---|
| 200 | Decodificar, mostrar e salvar como cache |
| 401 / 403 | Login inválido ou expirado (compare `expiresAt` com a hora atual para escolher a mensagem) |
| 429 | Esperar 10 minutos antes de tentar de novo |
| outros | Mostrar erro e tentar no próximo ciclo |

Frequência usada no Mac: a cada 3 minutos, ao abrir o pop-up (se o último dado tiver mais de 60 s) e quando o computador acorda.

## 3. Claude: resposta

Exemplo completo com números fictícios: [`sample-usage.json`](sample-usage.json). A resposta real tem dezenas de outros campos (a maioria `null`), que podem ser ignorados.

### `limits`: a lista principal

É a mesma lista de claude.ai → Configurações → Uso. Mostre na ordem recebida.

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

| `kind` | Título |
|---|---|
| `session` | Sessão atual (janela de 5 h) |
| `weekly_all` | Semanal · todos os modelos |
| `weekly_scoped` | Semanal · `scope.model.display_name` (ou `scope.surface.display_name`) |
| outro | `kind` com `_` trocado por espaço e iniciais maiúsculas, mais o nome do escopo se houver |

- `percent`: de 0 a 100, já em porcentagem.
- `severity`: já vimos `normal` e `warning`. Trate valores com "crit", "exceed", "high" ou "block" como crítico. Se o valor for desconhecido, use o percentual: 90 ou mais é crítico, 75 ou mais é alerta.
- `resets_at`: ISO-8601 com **microssegundos** (`.554353`), formato que alguns parsers recusam. Arredonde para o minuto antes de exibir: limites que reiniciam juntos chegam como `02:59:59` e `03:00:00`.

### Campos antigos (alternativa)

Se `limits` não vier, monte a lista a partir de `five_hour`, `seven_day`, `seven_day_opus` e `seven_day_sonnet`, que trazem `{ "utilization": 0-100, "resets_at": "…" }`.

### `seven_day_breakdown`: onde foi o uso

```json
{ "rows": [ { "key": "claude_code", "display_name": "Claude Code", "percent": 87 }, … ] }
```

É a divisão do uso semanal entre os produtos. Os valores somam cerca de 100 e **não** são porcentagem do limite. No Mac, as linhas com 0% ficam escondidas, a lista é ordenada da maior para a menor, `chat` aparece como "Chat" e `other` como "Outros".

## 4. Número na barra

Cada serviço vira um bloco na barra, com um ou dois números, tudo na cor da barra. Os blocos são separados por um traço vertical. O estilo, salvo em `menuBar.style`, vale para todos:

| `menuBar.style` | Medidor antes dos números |
|---|---|
| `icon` (padrão) | ícone monocromático (a arte branca do ícone do app, sem o fundo) dentro de um anel de progresso de 19 pt |
| `percent` | bolinha de 14 pt com contorno e uma fatia de pizza que se preenche (o visual da versão 0.1) |

A escolha de cada serviço fica salva em `menuBar.<serviço>`:

| Valor | Números | O anel mostra |
|---|---|---|
| `session` | sessão (`kind` `session`) | sessão |
| `weekly` | semanal (`kind` `weekly_all`; no Grok Bot, o único limite semanal) | semanal |
| `both` | sessão em cima, semanal embaixo | sessão |
| `hidden` | o bloco não aparece | — |

O Claude oferece as quatro opções (padrão `both`); o Grok Bot oferece `weekly` (padrão) e `hidden`. Se o limite escolhido não vier na resposta, use o outro; se nenhum vier, use o maior. Sem dados, mostre `–` (carregando) ou `!` (erro). A cor do anel ou da pizza segue a severidade do limite (normal, alerta ou crítico), a menos que `menuBar.colorAlerts` seja `false`; nesse caso tudo fica na cor da barra.

Os textos seguem a preferência `language` (`pt` ou `en`). Numa instalação nova, ela começa no idioma do sistema: português se o primeiro idioma preferido começar com `pt`, inglês nos outros casos.

## 5. Grok Bot

O Grok Bot (`/Applications/Grok Bot.app`, bundle `com.anysphere.sand`) é um app Electron que roda sobre a infraestrutura do Cursor. Formato verificado na versão 0.79.3.

### Login

| Sistema | Onde fica |
|---|---|
| macOS | `~/Library/Application Support/Grok Bot/sand-secrets.json`, com valores criptografados pelo `safeStorage` do Electron. A chave fica no Keychain: serviço `Grok Bot Safe Storage`, conta `Grok Bot Key`. |
| Windows / Linux | *Não verificado.* O Electron usa DPAPI no Windows e libsecret/kwallet no Linux, e o arquivo deve estar na pasta `userData` do app. |

Estrutura do arquivo (só os campos usados):

```json
{
  "version": 1,
  "cursor-accounts": "{\"active\":\"<escopo>\",\"accounts\":{\"<escopo>\":{\"cursor-access-token\":\"<base64>\"}}}"
}
```

- `cursor-accounts` é um JSON **dentro de uma string**. Pegue `accounts[active]["cursor-access-token"]`.
- Arquivos antigos guardam `cursor-access-token` direto na raiz, às vezes como `scoped:v1:<64 hex>:<base64>`. Valores `plaintext:v1:<base64>` não são criptografados.
- Descriptografia no macOS (`OSCrypt` do Chromium): o blob em base64 começa com `v10`. O resto é AES-128-CBC com PKCS#7, chave = PBKDF2-HMAC-SHA1(senha do Keychain, `saltysalt`, 1003 iterações, 16 bytes) e IV de 16 espaços (`0x20`).
- O token é um JWT. O campo `exp` diz quando expira; não use o refresh token.
- Ao ler a chave, o macOS mostra um pedido de permissão. Leia uma vez por execução e guarde a chave **só em memória**.

### Requisição (Connect RPC em JSON)

```
POST https://api2.cursor.sh/aiserver.v1.DashboardService/GetSandUsageStatus
Authorization: Bearer <token>
Content-Type: application/json
Connect-Protocol-Version: 1
x-ghost-mode: true

{}
```

Os status HTTP valem como na seção 2.

### Resposta

Exemplo com números fictícios: [`sample-grok-usage.json`](sample-grok-usage.json). É JSON proto3, então os campos vêm em camelCase, valores padrão (`false`, `0`) são omitidos e inteiros de 64 bits chegam como string.

| Campo | Uso |
|---|---|
| `usagePercent` | Uso **semanal**, de 0 a 100 (`0.3` = 0,3%). Ausente se não há limite individual. |
| `nextResetTimestampUtc` | Quando o limite semanal reinicia (RFC 3339). |
| `hasAvailableUsage` | `false` = limite esgotado; mostre como crítico. |
| `usesPooledEnterpriseAllowance` | `true` = cota da equipe; não mostre porcentagem. |
| `availableBankedResetCount` | Resets guardados que o usuário pode usar (string). |
| `grokPlanLabel`, `cursorPlanName` | Nome do plano. `grokPlanLabel` costuma ser o genérico "Grok Bot Plan"; nesse caso use `cursorPlanName` ("Premium"). |
| `onDemandSettings.enabled` | Uso sob demanda ligado. |

Existe também `DashboardService/GetGrokBotUsageBreakdown`, que divide o uso por dia, origem e bot, em partes por milhão do limite semanal (`weeklyLimitPpm`), do ciclo de cobrança (`billingCyclePpm`) ou em centavos (`billedCents`). O app ainda não usa essa chamada.
