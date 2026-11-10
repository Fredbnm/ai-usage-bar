# Claude Usage Bar

Um medidor do seu limite de uso de IA que fica na barra de menu. O ícone mostra a porcentagem mais alta do momento; um clique abre o pop-up com cada serviço:

**Claude**
- **Sessão atual** (janela de 5 h): quanto já foi usado e quando reinicia
- **Semanal, todos os modelos**
- **Semanal por modelo** (ex.: Fable), quando o seu plano tem esse limite separado
- **Onde foi o uso da semana**: Claude Code, Chat, Cowork…

**Grok Bot**
- **Semanal**: quanto já foi usado e quando reinicia
- Resets guardados disponíveis e uso sob demanda, quando houver

Os números são os mesmos de claude.ai → Configurações → Uso, do `/usage` do Claude Code e da tela de Uso do Grok Bot. O Grok Bot só aparece se estiver instalado e logado neste Mac.

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

Cada sistema terá um app nativo e um instalador próprios, construídos e testados naquele sistema. Tudo de que um novo port precisa está em [`docs/usage-api.md`](docs/usage-api.md): de onde vem cada login, quais endpoints chamar e como ler as respostas. Use [`docs/sample-usage.json`](docs/sample-usage.json) e [`docs/sample-grok-usage.json`](docs/sample-grok-usage.json) como dados de teste.

## Instalar no Mac

1. Abra o `ClaudeUsage-<versão>.dmg` e arraste **Claude Usage** para **Aplicativos**.
2. Na primeira vez, clique com o botão direito no app → **Abrir**. O app ainda não é assinado com um certificado da Apple, então o macOS pede essa confirmação uma vez.
3. Se você usa o Grok Bot, o macOS pergunta se o app pode acessar **"Grok Bot Safe Storage"** no Keychain. Escolha **Permitir sempre** (veja [Como funciona](#como-funciona)).
4. Para abrir junto com o Mac: ⚙︎ no pop-up → **Abrir ao iniciar o Mac**.

**Requisitos:** o Claude Code logado neste Mac (pelo app do Claude ou pelo `claude` no terminal). Para o Grok Bot, o app Grok Bot instalado e logado.

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

Cada serviço é um "provedor" (`UsageProvider`) que lê o login de um app já instalado e consulta o mesmo endpoint que esse app usa na própria tela de uso. A consulta acontece a cada 3 minutos, ao abrir o pop-up e quando o Mac acorda. A última resposta fica salva para aparecer na hora em que o app abre.

| Serviço | Login reaproveitado | Endpoint |
|---|---|---|
| Claude | Keychain, item `Claude Code-credentials`, lido via `/usr/bin/security` | `GET api.anthropic.com/api/oauth/usage` |
| Grok Bot | `~/Library/Application Support/Grok Bot/sand-secrets.json`, criptografado pelo Electron com a chave do item **Grok Bot Safe Storage** do Keychain | `POST api2.cursor.sh/aiserver.v1.DashboardService/GetSandUsageStatus` |

Os tokens são **só lidos**, em memória: o app nunca grava, renova ou envia um token para outro lugar além do próprio serviço. O cache guarda apenas as respostas de uso.

## Limitações

- **Os endpoints não são públicos.** São os que o próprio Claude Code e o próprio Grok Bot usam, e podem mudar sem aviso. O app ignora campos desconhecidos; se o formato mudar de vez, ele avisa que precisa de atualização.
- **Os logins expiram.** Claude Code e Grok Bot renovam os próprios tokens quando são usados. Se um deles ficar horas parado, o medidor mostra o último valor e pede para abrir aquele app uma vez.
- **Grok Bot e Keychain:** o macOS libera a chave do Grok Bot para *esta build* do app. Como o app é assinado só localmente (ad-hoc), cada versão nova pede o "Permitir sempre" de novo. Com um certificado Developer ID isso deixa de acontecer.
- **Sem assinatura da Apple ainda**, por isso o passo do botão direito → Abrir na primeira vez.

## Depuração

```bash
APP="/Applications/Claude Usage.app/Contents/MacOS/ClaudeUsageBar"
"$APP" --dump                 # imprime o uso atual em texto
"$APP" --dump --raw           # … e as respostas brutas (sem tokens)
"$APP" --snapshot pop.png     # renderiza o pop-up com seus dados
"$APP" --snapshot pop.png --fixture docs/sample-usage.json --grok-fixture docs/sample-grok-usage.json --dark
```

## Estrutura

```
docs/
  usage-api.md            contrato de dados para todos os ports
  sample-usage.json       resposta de exemplo do Claude (números fictícios)
  sample-grok-usage.json  resposta de exemplo do Grok Bot (números fictícios)
macos/
  Sources/UsageCore/      provedores (Claude, Grok Bot), logins, clientes HTTP, textos em pt-BR (testado)
  Sources/ClaudeUsageBar/ ícone na barra (AppKit) + pop-up (SwiftUI)
  Tests/                  testes da camada de dados
  Support/                Info.plist e ícone
  scripts/                build do .app/.dmg e geração do ícone
.github/workflows/        build do .dmg no GitHub Actions
```
