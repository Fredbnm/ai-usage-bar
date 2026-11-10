import Foundation

public enum UsageError: Error, Equatable, Sendable {
    case notLoggedIn
    case unauthorized(expired: Bool)
    case keychainDenied
    case rateLimited
    case http(Int)
    case network(String)
    case decoding

    public func message(for provider: ProviderID) -> String {
        let app = provider.loginSource
        switch self {
        case .notLoggedIn:
            switch provider {
            case .claude:
                return "Não achei o login do Claude Code neste Mac. Entre com o comando claude no terminal ou pelo app do Claude."
            case .grokBot:
                return "Não achei o login do Grok Bot. Abra o Grok Bot e entre na sua conta."
            }
        case .unauthorized(expired: true):
            return "O login do \(app) expirou. Abra o \(app) uma vez para renová-lo e o medidor volta sozinho."
        case .unauthorized(expired: false):
            return "O servidor recusou o login atual. Saia e entre de novo no \(app)."
        case .keychainDenied:
            return "O macOS não liberou a chave do \(app) no Keychain. Clique em atualizar e escolha “Permitir sempre”."
        case .rateLimited:
            return "Muitas consultas seguidas. Tento de novo em 10 minutos."
        case .http(let code):
            return "O servidor respondeu com erro \(code). Tento de novo em breve."
        case .network:
            return "Sem conexão. Tento de novo em breve."
        case .decoding:
            return "O formato dos dados de uso mudou e o medidor precisa ser atualizado."
        }
    }
}

enum HTTP {
    /// Sends the request and maps HTTP failures to `UsageError`.
    static func send(_ request: URLRequest) async throws -> Data {
        let result: (Data, URLResponse)
        do {
            result = try await URLSession.shared.data(for: request)
        } catch {
            throw UsageError.network(error.localizedDescription)
        }
        let (data, response) = result
        let status = (response as? HTTPURLResponse)?.statusCode ?? -1
        switch status {
        case 200..<300: return data
        case 401, 403: throw UsageError.unauthorized(expired: false)
        case 429: throw UsageError.rateLimited
        default: throw UsageError.http(status)
        }
    }
}

/// Calls the endpoint Claude Code's `/usage` uses. Undocumented, so it may
/// change; see docs/usage-api.md at the repository root.
public struct UsageClient: Sendable {
    public static let endpoint = URL(string: "https://api.anthropic.com/api/oauth/usage")!
    public static let betaHeader = "oauth-2025-04-20"

    public init() {}

    /// Returns the raw JSON so the caller can both decode and cache it.
    public func fetchUsage(accessToken: String) async throws -> Data {
        var request = URLRequest(url: Self.endpoint, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 20)
        request.setValue("Bearer \(accessToken)", forHTTPHeaderField: "Authorization")
        request.setValue(Self.betaHeader, forHTTPHeaderField: "anthropic-beta")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.setValue("claude-usage-bar", forHTTPHeaderField: "User-Agent")
        return try await HTTP.send(request)
    }
}
