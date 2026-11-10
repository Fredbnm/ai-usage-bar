# Claude Usage Bar

Um medidor do seu limite de uso do Claude que fica na barra de menu. O ícone mostra a porcentagem atual; um clique abre o pop-up com:

- **Sessão atual** (janela de 5 h): quanto já foi usado e quando reinicia
- **Semanal, todos os modelos**
- **Semanal por modelo** (ex.: Fable), quando o seu plano tem esse limite separado
- **Onde foi o uso da semana**: Claude Code, Chat, Cowork…

São os mesmos números de claude.ai → Configurações → Uso e do `/usage` do Claude Code. O limite é compartilhado, então a porcentagem soma o que você usa em todos os lugares.

<p align="center">
  <img src="docs/screenshot-light.png" width="320" alt="Pop-up no tema claro">
  <img src="docs/screenshot-dark.png" width="320" alt="Pop-up no tema escuro">
</p>

## Plataformas

| Sistema | Situação | Onde |
|---|---|---|
| macOS 13+ (Apple Silicon e Intel) | ✅ App nativo (Swift/SwiftUI), instalador `.dmg` | [`macos/`](macos/) |
| Windows | Planejado | — |
| Linux | Planejado | — |

Cada sistema terá um app nativo e um instalador próprios, construídos e testados naquele sistema. Tudo de que um novo port precisa está em [`docs/usage-api.md`](docs/usage-api.md): de onde vem o login, qual endpoint chamar e como ler a resposta. Use [`docs/sample-usage.json`](docs/sample-usage.json) como dado de teste.

## Instalar no Mac

1. Abra o `ClaudeUsage-<versão>.dmg` e arraste **Claude Usage** para **Aplicativos**.
2. Na primeira vez, clique com o botão direito no app → **Abrir**. O app ainda não é assinado com um certificado da Apple, então o macOS pede essa confirmação uma vez.
3. Para abrir junto com o Mac: ⚙︎ no pop-up → **Abrir ao iniciar o Mac**.

**Requisito:** o Claude Code precisa estar logado neste Mac (pelo app do Claude ou pelo `claude` no terminal). O medidor usa esse mesmo login.

## Compilar

Precisa do Xcode (ou das Command Line Tools) com Swift 5.10 ou mais novo.

```bash
cd macos
make test      # testes da camada de dados
make build     # gera dist/Claude Usage.app e dist/ClaudeUsage-<versão>.dmg
make install   # copia para /Applications e abre
```

A versão sai do arquivo `macos/VERSION`. Para assinar com um certificado Developer ID, use `SIGN_IDENTITY="Developer ID Application: …" make build`.

## Como funciona

1. Lê o login do Claude Code no Keychain (item `Claude Code-credentials`) usando `/usr/bin/security`. Se não houver Keychain, lê `~/.claude/.credentials.json`.
2. Chama `GET https://api.anthropic.com/api/oauth/usage` a cada 3 minutos, quando o pop-up abre e quando o Mac acorda.
3. Guarda a última resposta para mostrar algo na hora em que o app abre.

O app **só lê** o token: nunca grava, renova ou envia o token para outro lugar além da própria Anthropic.

## Limitações

- **O endpoint não é público.** É o que o próprio Claude Code usa no `/usage` e pode mudar sem aviso. O app ignora campos desconhecidos; se o formato mudar de vez, ele avisa que precisa de atualização.
- **O login expira.** O Claude Code renova o token quando é usado. Se ele ficar horas parado, o medidor mostra o último valor e pede para abrir o Claude Code uma vez.
- **Sem assinatura da Apple ainda**, por isso o passo do botão direito → Abrir na primeira vez.

## Depuração

```bash
APP="/Applications/Claude Usage.app/Contents/MacOS/ClaudeUsageBar"
"$APP" --dump                                    # imprime o uso atual em texto
"$APP" --snapshot pop.png                        # renderiza o pop-up com seus dados
"$APP" --snapshot pop.png --fixture docs/sample-usage.json --dark
```

## Estrutura

```
docs/
  usage-api.md          contrato de dados para todos os ports
  sample-usage.json     resposta de exemplo (números fictícios)
macos/
  Sources/UsageCore/    login, cliente HTTP, modelos, textos em pt-BR (testado)
  Sources/ClaudeUsageBar/  ícone na barra (AppKit) + pop-up (SwiftUI)
  Tests/                testes da camada de dados
  Support/              Info.plist e ícone
  scripts/              build do .app/.dmg e geração do ícone
.github/workflows/      build do .dmg no GitHub Actions
```
