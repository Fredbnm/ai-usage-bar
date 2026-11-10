import AppKit
import SwiftUI
import UsageCore

struct PopoverView: View {
    @ObservedObject var store: UsageStore
    var onQuit: () -> Void = {}

    @State private var launchAtLogin = LaunchAtLogin.isEnabled

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            header
            ForEach(store.visibleProviders, id: \.self) { id in
                Divider()
                ProviderSection(provider: id, state: store.state(for: id))
            }
            Divider()
            footer
        }
        .padding(16)
        .frame(width: 320)
        .id(store.language)
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
                ForEach(store.visibleProviders, id: \.self) { id in
                    Picker(id.displayName + tr(" na barra", " in menu bar"), selection: menuBarBinding(for: id)) {
                        ForEach(id.menuBarDisplayOptions) { option in
                            Text(option.label(for: id)).tag(option)
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
        case .grokBot: tr("Abrir o Grok Bot", "Open Grok Bot")
        }
    }

    @MainActor
    func open() {
        switch self {
        case .claude:
            NSWorkspace.shared.open(URL(string: "https://claude.ai/settings/usage")!)
        case .grokBot:
            if FileManager.default.fileExists(atPath: GrokBotProvider.appURL.path) {
                NSWorkspace.shared.open(GrokBotProvider.appURL)
            } else if let url = URL(string: "grokbot://") {
                NSWorkspace.shared.open(url)
            }
        }
    }
}
