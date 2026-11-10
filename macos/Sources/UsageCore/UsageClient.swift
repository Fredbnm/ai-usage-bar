import Foundation

public enum UsageError: Error, Equatable, Sendable {
    case notLoggedIn
    case unauthorized(expired: Bool)
    case rateLimited
    case http(Int)
    case network(String)
    case decoding

    public var message: String {
        switch self {
        case .notLoggedIn:
            return "Não achei o login do Claude Code neste Mac. Entre com o comando claude no terminal ou pelo app do Claude."
        case .unauthorized(expired: true):
            return "O login do Claude Code expirou. Abra o Claude Code uma vez para renová-lo e o medidor volta sozinho."
        case .unauthorized(expired: false):
            return "A Anthropic recusou o login atual. Saia e entre de novo no Claude Code."
        case .rateLimited:
            return "Muitas consultas seguidas. Tento de novo em 10 minutos."
        case .http(let code):
            return "O servidor respondeu com erro \(code). Tento de novo em breve."
        case .network:
            return "Sem conexão com a Anthropic. Tento de novo em breve."
        case .decoding:
            return "O formato dos dados de uso mudou e o app precisa ser atualizado."
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
