# Dados de uso: contrato para os apps

Este documento descreve tudo o que um app de qualquer sistema precisa para mostrar o uso do plano Claude. O app de macOS (`macos/Sources/UsageCore`) é a implementação de referência.

> ⚠️ **Não é uma API pública.** É o mesmo endpoint que o Claude Code chama no comando `/usage`. Ele pode mudar sem aviso, então decodifique tudo de forma tolerante (campos opcionais, ignorar o que não conhece, pular itens inválidos de listas).

## 1. Login (token OAuth do Claude Code)

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

## 2. Requisição

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

## 3. Resposta

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

O usuário escolhe o que aparece ao lado do ícone:

| Opção | Valor |
|---|---|
| Sessão atual (padrão) | `percent` do item `session` |
| Semanal | `percent` do item `weekly_all` |
| O mais alto | maior `percent` entre todos os itens |
