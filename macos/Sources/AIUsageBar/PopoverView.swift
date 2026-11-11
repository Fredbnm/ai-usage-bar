import AppKit
import SwiftUI
import UsageCore

struct PopoverView: View {
    @ObservedObject var store: UsageStore
    @ObservedObject var updater: Updater
    var onQuit: () -> Void = {}

    @State private var launchAtLogin = LaunchAtLogin.isEnabled

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            header
            if updater.showsBanner {
                UpdateBanner(updater: updater)
            }
            // One agent needs no strip: the popover looks as it always did.
            if store.showTabStrip {
                TabStrip(store: store)
            }
            if store.showsAllProviders {
                Divider()
                // Scrolls when every agent together is taller than the screen allows;
                // the header and the gear stay in view.
                ScrollView {
                    VStack(alignment: .leading, spacing: 14) {
                        ForEach(Array(store.visibleProviders.enumerated()), id: \.element) { index, id in
                            if index > 0 { Divider() }
                            ProviderSection(provider: id, state: store.state(for: id))
                        }
                    }
                }
                .frame(maxHeight: Self.maxListHeight)
            } else {
                Divider()
                if let id = store.currentProvider {
                    ProviderSection(provider: id, state: store.state(for: id))
                } else {
                    AgentsSection(store: store)
                }
            }
            Divider()
            footer
        }
        .padding(16)
        .frame(width: 320)
        .id(store.language)
        .background(tabKeyboardShortcuts)
    }

    /// Room for "All AIs together": the screen's height minus the header, banner and footer.
    static var maxListHeight: CGFloat {
        max(240, (NSScreen.main?.visibleFrame.height ?? 800) - 240)
    }

    /// Left/Right arrows move between tabs.
    private var tabKeyboardShortcuts: some View {
        HStack {
            Button("") { store.stepTab(-1) }.keyboardShortcut(.leftArrow, modifiers: [])
            Button("") { store.stepTab(1) }.keyboardShortcut(.rightArrow, modifiers: [])
        }
        .frame(width: 0, height: 0)
        .opacity(0)
        .accessibilityHidden(true)
    }

    private var header: some View {
        HStack(alignment: .center, spacing: 8) {
            VStack(alignment: .leading, spacing: 2) {
                Text(tr("Uso de IA", "AI Usage"))
                    .font(.system(size: 15, weight: .semibold))
                TimelineView(.periodic(from: .now, by: 30)) { context in
                    Text(store.lastUpdated.map { UsageFormat.updated($0, now: context.date) } ?? tr("Ainda sem dados", "No data yet"))
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }
            Spacer()
            if store.isLoading {
                ProgressView().controlSize(.small)
            } else {
                Button {
                    Task { await store.refresh(force: true) }
                } label: {
                    Image(systemName: "arrow.clockwise")
                }
                .buttonStyle(.borderless)
                .help(tr("Atualizar agora", "Refresh now"))
            }
        }
    }

    private var footer: some View {
        HStack {
            Spacer()
            Menu {
                Picker(tr("Estilo na barra", "Menu bar style"), selection: $store.menuBarStyle) {
                    ForEach(MenuBarStyle.allCases) { style in
                        Text(style.label).tag(style)
                    }
                }
                Picker(tr("Pop-up", "Popup"), selection: $store.popupLayout) {
                    ForEach(PopupLayout.allCases) { layout in
                        Text(layout.label).tag(layout)
                    }
                }
                Button(tr("Configurar agentes…", "Set up agents…")) { store.showAgents() }
                ForEach(store.visibleProviders, id: \.self) { id in
                    Menu(id.displayName) {
                        Picker(tr("Na barra de menu", "In the menu bar"), selection: menuBarBinding(for: id)) {
                            ForEach(id.menuBarDisplayOptions) { option in
                                Text(option.label(for: id)).tag(option)
                            }
                        }
                        if store.popupLayout == .tabs {
                            TabLimitPicker(store: store, provider: id)
                        }
                    }
                }
                Divider()
                Toggle(tr("Cores de alerta na barra", "Alert colors in menu bar"), isOn: $store.colorAlerts)
                Divider()
                Picker(tr("Idioma", "Language"), selection: $store.language) {
                    ForEach(AppLanguage.allCases) { language in
                        Text(language.nativeName).tag(language)
                    }
                }
                Toggle(tr("Abrir ao iniciar o Mac", "Open at login"), isOn: launchAtLoginBinding)
                Divider()
                Button(tr("Procurar atualizações", "Check for updates")) {
                    Task { await updater.check(manual: true) }
                }
                Text(tr("Versão \(updater.currentText)", "Version \(updater.currentText)"))
                Divider()
                Button(tr("Sair do medidor", "Quit")) { onQuit() }
            } label: {
                Image(systemName: "gearshape")
            }
            .menuStyle(.borderlessButton)
            .menuIndicator(.hidden)
            .fixedSize()
            .help(tr("Opções", "Options"))
        }
    }

    private func menuBarBinding(for id: ProviderID) -> Binding<MenuBarDisplay> {
        Binding(
            get: { store.menuBarDisplay(for: id) },
            set: { store.setMenuBarDisplay($0, for: id) }
        )
    }

    private var launchAtLoginBinding: Binding<Bool> {
        Binding(
            get: { launchAtLogin },
            set: { newValue in
                do {
                    try LaunchAtLogin.setEnabled(newValue)
                } catch {
                    NSSound.beep()
                }
                launchAtLogin = LaunchAtLogin.isEnabled
            }
        )
    }
}

private struct ProviderSection: View {
    let provider: ProviderID
    let state: UsageStore.ProviderState

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 6) {
                Text(provider.displayName)
                    .font(.system(size: 14, weight: .semibold))
                if let plan = state.snapshot?.planName {
                    PlanBadge(text: plan)
                }
                Spacer()
                Button {
                    provider.open()
                } label: {
                    Image(systemName: "arrow.up.right.square")
                }
                .buttonStyle(.borderless)
                .help(provider.openHelp)
            }

            if let error = state.error {
                ErrorBanner(text: error.message(for: provider))
            }

            if let snapshot = state.snapshot {
                ForEach(snapshot.items) { LimitRow(item: $0) }
                ForEach(snapshot.notes, id: \.self) { note in
                    Label(note.text, systemImage: "info.circle")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                if !snapshot.breakdown.isEmpty {
                    BreakdownSection(rows: snapshot.breakdown)
                        .padding(.top, 2)
                }
            } else if state.error == nil {
                HStack(spacing: 8) {
                    ProgressView().controlSize(.small)
                    Text(tr("Carregando…", "Loading…")).foregroundStyle(.secondary)
                }
            }
        }
    }
}

/// "New version" strip under the header: the update button, its progress, or the result of a manual check.
private struct UpdateBanner: View {
    @ObservedObject var updater: Updater

    var body: some View {
        HStack(alignment: .center, spacing: 8) {
            content
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 8)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(tint.opacity(0.12), in: RoundedRectangle(cornerRadius: 8))
    }

    private var tint: Color {
        if case .failed = updater.phase { return .orange }
        return .accentColor
    }

    private var newVersion: String { updater.available?.version.description ?? "" }

    @ViewBuilder
    private var content: some View {
        switch updater.phase {
        case .checking:
            progress(tr("Procurando atualizações…", "Checking for updates…"))
        case .downloading:
            progress(tr("Baixando a versão \(newVersion)…", "Downloading version \(newVersion)…"))
        case .installing:
            progress(tr("Instalando… o medidor reabre sozinho.", "Installing… the meter reopens by itself."))
        case .upToDate:
            Image(systemName: "checkmark.circle.fill").foregroundStyle(Severity.normal.color)
            Text(tr("Você já tem a versão mais recente (\(updater.currentText)).", "You're on the latest version (\(updater.currentText))."))
                .font(.callout)
                .fixedSize(horizontal: false, vertical: true)
        case .failed(let message):
            Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange)
            Text(message)
                .font(.callout)
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 4)
            if updater.available != nil {
                Button(tr("Abrir página", "Open page")) { updater.openReleasePage() }
                    .controlSize(.small)
            }
        case .idle:
            Image(systemName: "arrow.down.circle.fill")
                .font(.system(size: 18))
                .foregroundStyle(Color.accentColor)
            VStack(alignment: .leading, spacing: 1) {
                Text(tr("Nova versão \(newVersion)", "New version \(newVersion)"))
                    .font(.system(size: 13, weight: .semibold))
                Text(tr("Você está com a \(updater.currentText)", "You have \(updater.currentText)"))
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            Spacer(minLength: 4)
            Button(updater.canInstallInPlace ? tr("Atualizar", "Update") : tr("Baixar", "Download")) {
                updater.install()
            }
            .controlSize(.small)
            .help(updater.canInstallInPlace
                ? tr("Baixa, instala e reabre o medidor", "Downloads, installs and reopens the meter")
                : tr("Abre a página de download", "Opens the download page"))
        }
    }

    private func progress(_ text: String) -> some View {
        HStack(spacing: 8) {
            ProgressView().controlSize(.small)
            Text(text).font(.callout)
        }
    }
}

/// "In the tab" choices for one agent: the fullest limit or any one of its limits.
private struct TabLimitPicker: View {
    @ObservedObject var store: UsageStore
    let provider: ProviderID

    var body: some View {
        Picker(tr("Na aba", "In its tab"), selection: Binding(
            get: { store.tabLimit(for: provider) },
            set: { store.setTabLimit($0, for: provider) }
        )) {
            ForEach(store.tabLimitOptions(for: provider), id: \.self) { key in
                Text(TabLimit.title(for: key)).tag(key)
            }
        }
    }
}

/// One tab per agent (icon, name and the limit picked for it, by default the fullest) and the Agents tab.
private struct TabStrip: View {
    @ObservedObject var store: UsageStore

    var body: some View {
        HStack(spacing: 0) {
            ForEach(store.visibleProviders, id: \.self) { id in
                agentTab(id)
            }
            // "+" keeps a narrow slot of its own; the agent tabs share the rest.
            button(key: PopupTabs.agentsKey, spoken: tr("Agentes e conexões", "Agents and connections")) {
                Image(systemName: "plus").foregroundStyle(.secondary)
            }
            .frame(width: 30)
        }
    }

    private func agentTab(_ id: ProviderID) -> some View {
        let state = store.state(for: id)
        let summary = TabSummary.make(snapshot: state.snapshot, hasError: state.error != nil, limit: store.tabLimit(for: id))
        let selected = store.currentTab == id.rawValue
        let status = summary.isWarning && summary.text != "!" ? summary.text + " ⚠" : summary.text
        let statusColor: Color = summary.text == "–"
            ? .secondary
            : (summary.isWarning ? Severity.warning : summary.severity).color

        let icon = Image(nsImage: TabIcon.image(for: id))
            .renderingMode(.template)
            .resizable()
            .frame(width: 16, height: 16)
            .foregroundStyle(.primary)
            .opacity(selected ? 1 : 0.65)
        let statusText = Text(status)
            .font(.system(size: 11, weight: .semibold))
            .foregroundColor(statusColor)

        return button(key: id.rawValue, spoken: tabDescription(id, summary), menu: {
            // Right-click a tab to pick what it shows.
            TabLimitPicker(store: store, provider: id)
        }) {
            // Icon + name + number when they fit; the name is the first thing to go.
            ViewThatFits(in: .horizontal) {
                HStack(spacing: 4) {
                    icon
                    Text(id.shortName).font(.system(size: 12.5, weight: selected ? .semibold : .regular))
                    statusText
                }
                HStack(spacing: 4) {
                    icon
                    statusText
                }
            }
            .lineLimit(1)
        }
    }

    private func button<Label: View, MenuContent: View>(
        key: String, spoken: String,
        @ViewBuilder menu: () -> MenuContent = { EmptyView() },
        @ViewBuilder label: () -> Label
    ) -> some View {
        let selected = store.currentTab == key
        return Button {
            store.selectTab(key)
        } label: {
            VStack(spacing: 5) {
                label()
                Rectangle()
                    .fill(selected ? Color.accentColor : Color.clear)
                    .frame(height: 2)
            }
            .frame(maxWidth: .infinity)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .contextMenu { menu() }
        .help(spoken)
        .accessibilityLabel(spoken)
        .accessibilityAddTraits(selected ? [.isButton, .isSelected] : .isButton)
    }

    private func tabDescription(_ id: ProviderID, _ summary: TabSummary) -> String {
        let status = summary.text == "!" || summary.text == "–"
            ? tr("sem dados", "no data")
            : tr("\(summary.text) usado", "\(summary.text) used")
        return "\(id.displayName), \(status)"
    }
}

/// Every supported agent, whether it is connected, and first-run help.
private struct AgentsSection: View {
    @ObservedObject var store: UsageStore

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(tr("AGENTES DE IA", "AI AGENTS"))
                .font(.system(size: 10, weight: .semibold))
                .kerning(0.4)
                .foregroundStyle(.secondary)
            if !store.onboardingDismissed {
                Text(tr(
                    "Aqui estão os agentes que o medidor sabe ler. Os que você já usa neste Mac aparecem sozinhos; para os outros, siga a dica.",
                    "These are the agents the meter can read. The ones you already use on this Mac show up on their own; for the others, follow the hint."
                ))
                .font(.callout)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            }
            ForEach(store.agentRows) { row in
                AgentRowView(row: row) { store.setProviderHidden(row.id, row.isShown) }
            }
            HStack {
                Spacer()
                // After the first run the same button goes back, which matters
                // with one agent, where there is no tab strip to leave by.
                Button(store.onboardingDismissed ? tr("Voltar", "Back") : tr("Concluir", "Done")) {
                    store.dismissOnboarding()
                }
                .keyboardShortcut(.defaultAction)
            }
        }
    }
}

private struct AgentRowView: View {
    let row: UsageStore.AgentRow
    let toggle: () -> Void

    var body: some View {
        let signedIn = row.status == .connected
        HStack(alignment: .top, spacing: 8) {
            icon.frame(width: 14)
            VStack(alignment: .leading, spacing: 2) {
                Text(row.id.displayName).font(.system(size: 13, weight: .semibold))
                Text(status)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                if let hint {
                    Text(hint)
                        .font(.caption)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            Spacer()
            if signedIn {
                Toggle(tr("Mostrar", "Show"), isOn: Binding(get: { row.isShown }, set: { _ in toggle() }))
                    .toggleStyle(.checkbox)
            }
        }
    }

    @ViewBuilder
    private var icon: some View {
        if row.status != .connected {
            Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(Severity.warning.color)
        } else if row.hasData {
            Image(systemName: "checkmark").foregroundStyle(Severity.normal.color)
        } else {
            Image(systemName: "questionmark.circle").foregroundStyle(.secondary)
        }
    }

    private var status: String {
        if row.status != .connected { return tr("Login não encontrado", "No login found") }
        if row.hasData { return tr("Conectado", "Connected") + (row.plan.map { " · \($0)" } ?? "") }
        return tr("Login encontrado, mas sem dados de uso", "Login found, but no usage data")
    }

    private var hint: String? {
        if row.status != .connected { return row.id.setupHint }
        if row.hasData { return nil }
        return tr(
            "Talvez esta conta não tenha o \(row.id.displayName). Marque Mostrar para acompanhar mesmo assim.",
            "This account may not have \(row.id.displayName). Tick Show to track it anyway."
        )
    }
}

private struct LimitRow: View {
    let item: LimitItem

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(alignment: .firstTextBaseline) {
                Text(item.title)
                    .font(.system(size: 13, weight: .medium))
                Spacer()
                Text(UsageFormat.percent(item.percent))
                    .font(.system(size: 13, weight: .semibold).monospacedDigit())
                    .foregroundStyle(item.severity.color)
            }
            UsageBar(fraction: item.percent / 100, color: item.severity.color)
            if let resetsAt = item.resetsAt {
                TimelineView(.periodic(from: .now, by: 30)) { context in
                    Text(UsageFormat.reset(resetsAt, now: context.date))
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }
        }
    }
}

private struct BreakdownSection: View {
    let rows: [BreakdownItem]

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(tr("ONDE FOI O USO DA SEMANA", "WHERE THIS WEEK’S USAGE WENT"))
                .font(.system(size: 10, weight: .semibold))
                .kerning(0.4)
                .foregroundStyle(.secondary)
            ForEach(rows) { row in
                HStack(spacing: 8) {
                    Text(row.name)
                        .font(.callout)
                        .lineLimit(1)
                        .frame(width: 92, alignment: .leading)
                    UsageBar(fraction: row.percent / 100, color: Color.secondary.opacity(0.6), height: 4)
                    Text(UsageFormat.percent(row.percent))
                        .font(.callout.monospacedDigit())
                        .foregroundStyle(.secondary)
                        .frame(width: 40, alignment: .trailing)
                }
            }
        }
    }
}

private struct UsageBar: View {
    var fraction: Double
    var color: Color
    var height: CGFloat = 6

    var body: some View {
        GeometryReader { geometry in
            ZStack(alignment: .leading) {
                Capsule().fill(Color.primary.opacity(0.1))
                if fraction > 0 {
                    Capsule()
                        .fill(color)
                        .frame(width: max(height, geometry.size.width * min(fraction, 1)))
                }
            }
        }
        .frame(height: height)
    }
}

private struct ErrorBanner: View {
    let text: String

    var body: some View {
        HStack(alignment: .top, spacing: 8) {
            Image(systemName: "exclamationmark.triangle.fill")
                .foregroundStyle(.orange)
            Text(text)
                .font(.callout)
                .fixedSize(horizontal: false, vertical: true)
        }
        .padding(10)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Color.orange.opacity(0.12), in: RoundedRectangle(cornerRadius: 8))
    }
}

private struct PlanBadge: View {
    let text: String

    var body: some View {
        Text(text)
            .font(.system(size: 10, weight: .semibold))
            .lineLimit(1)
            .padding(.horizontal, 6)
            .padding(.vertical, 2)
            .background(Color.primary.opacity(0.08), in: Capsule())
    }
}

extension Severity {
    var color: Color {
        switch self {
        case .normal: .accentColor
        case .warning: .orange
        case .critical: .red
        }
    }
}

extension ProviderID {
    var openHelp: String {
        switch self {
        case .claude: tr("Ver uso no claude.ai", "View usage on claude.ai")
        case .codex: tr("Ver uso no ChatGPT", "View usage on ChatGPT")
        case .gemini: tr("Abrir o Gemini", "Open Gemini")
        case .copilot: tr("Gerenciar o Copilot no github.com", "Manage Copilot on github.com")
        case .grokBot: tr("Abrir o Grok Bot", "Open Grok Bot")
        }
    }

    @MainActor
    func open() {
        switch self {
        case .claude:
            NSWorkspace.shared.open(URL(string: "https://claude.ai/settings/usage")!)
        case .codex:
            NSWorkspace.shared.open(URL(string: "https://chatgpt.com/codex/settings/usage")!)
        case .gemini:
            NSWorkspace.shared.open(URL(string: "https://gemini.google.com/app")!)
        case .copilot:
            NSWorkspace.shared.open(URL(string: "https://github.com/settings/copilot")!)
        case .grokBot:
            if FileManager.default.fileExists(atPath: GrokBotProvider.appURL.path) {
                NSWorkspace.shared.open(GrokBotProvider.appURL)
            } else if let url = URL(string: "grokbot://") {
                NSWorkspace.shared.open(url)
            }
        }
    }
}
