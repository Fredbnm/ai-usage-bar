using static AIUsageBar.Core.Localization;

namespace AIUsageBar.Core;

public enum UsageErrorKind
{
    NotLoggedIn,
    Unauthorized,
    UnauthorizedExpired,
    /// <summary>Windows would not decrypt a stored key (DPAPI).</summary>
    SecretUnavailable,
    RateLimited,
    Http,
    Network,
    Decoding,
}

public sealed class UsageException(UsageErrorKind kind, int statusCode = 0, Exception? inner = null)
    : Exception(kind.ToString(), inner)
{
    public UsageErrorKind Kind { get; } = kind;
    public int StatusCode { get; } = statusCode;

    public string MessageFor(ProviderId provider) => Describe(Kind, provider, StatusCode);

    public static string Describe(UsageErrorKind kind, ProviderId provider, int statusCode = 0)
    {
        var app = provider.LoginSource();
        return kind switch
        {
            UsageErrorKind.NotLoggedIn when provider == ProviderId.Claude => T(
                "Não achei o login do Claude Code neste PC. Entre com o comando claude no terminal ou pelo app do Claude.",
                "No Claude Code login found on this PC. Sign in with the claude command in a terminal or in the Claude app."),
            UsageErrorKind.NotLoggedIn when provider == ProviderId.Copilot => T(
                "Não achei o login do GitHub Copilot. Entre no Copilot no VS Code ou rode gh auth login.",
                "No GitHub Copilot login found. Sign in to Copilot in VS Code, or run gh auth login."),
            UsageErrorKind.NotLoggedIn => T(
                "Não achei o login do Grok Bot. Abra o Grok Bot e entre na sua conta.",
                "No Grok Bot login found. Open Grok Bot and sign in."),
            UsageErrorKind.UnauthorizedExpired => T(
                $"O login do {app} expirou. Abra o {app} uma vez para renová-lo e o medidor volta sozinho.",
                $"The {app} login expired. Open {app} once to renew it and the meter picks up again."),
            UsageErrorKind.Unauthorized => T(
                $"O servidor recusou o login atual. Saia e entre de novo no {app}.",
                $"The server rejected the current login. Sign out and back in to {app}."),
            UsageErrorKind.SecretUnavailable => T(
                $"O Windows não liberou a chave do {app}. Abra o {app} uma vez e clique em atualizar.",
                $"Windows did not release the {app} key. Open {app} once and click refresh."),
            UsageErrorKind.RateLimited => T(
                "Muitas consultas seguidas. Tento de novo em 10 minutos.",
                "Too many requests. Trying again in 10 minutes."),
            UsageErrorKind.Http => T(
                $"O servidor respondeu com erro {statusCode}. Tento de novo em breve.",
                $"The server returned error {statusCode}. Trying again soon."),
            UsageErrorKind.Network => T("Sem conexão. Tento de novo em breve.", "No connection. Trying again soon."),
            _ => T(
                "O formato dos dados de uso mudou e o medidor precisa ser atualizado.",
                "The usage data format changed and the meter needs an update."),
        };
    }
}
