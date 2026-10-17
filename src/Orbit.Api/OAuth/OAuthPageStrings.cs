namespace Orbit.Api.OAuth;

/// <summary>
/// Every word the authorize page renders, in one locale. The email and code wording matches the app's own
/// sign-in surface, so a person meets the same vocabulary in both places.
/// </summary>
public sealed record OAuthPageStrings(
    string Locale,
    string DocumentTitle,
    string MarkLabel,
    string Eyebrow,
    string EmailTitle,
    string EmailBody,
    string EmailLabel,
    string EmailPlaceholder,
    string EmailInvalid,
    string ContinueLabel,
    string OrLabel,
    string GoogleLabel,
    string SendFailed,
    string CodeTitle,
    string SentToLine,
    string CodeSent,
    string CodeResent,
    string ChangeEmailLabel,
    string CodeLabel,
    string CodeHint,
    string VerifyLabel,
    string VerifyFailed,
    string ResendLabel,
    string ResendCountdown,
    string AuthorizedLine,
    string ScopeTitle,
    string ScopeHabits,
    string ScopeProfile,
    string ScopeProgress)
{
    /// <summary>The locale tag the page keeps when the request asks for neither supported language.</summary>
    public const string DefaultLocale = "en";

    /// <summary>The locale tag the page keeps for a Brazilian Portuguese request.</summary>
    public const string PortugueseLocale = "pt-BR";

    private static readonly OAuthPageStrings English = new(
        Locale: DefaultLocale,
        DocumentTitle: "Connect Orbit to Claude",
        MarkLabel: "Orbit",
        Eyebrow: "Connect to Claude",
        EmailTitle: "Sign in with your email",
        EmailBody: "Claude asked for access to your Orbit account.",
        EmailLabel: "Email",
        EmailPlaceholder: "name@example.com",
        EmailInvalid: "Use a complete email, like name@example.com",
        ContinueLabel: "Continue",
        OrLabel: "or",
        GoogleLabel: "Continue with Google",
        SendFailed: "The code was not sent. Try again.",
        CodeTitle: "Enter the code",
        SentToLine: "We sent 6 digits to {0}.",
        CodeSent: "Code sent.",
        CodeResent: "A new code was sent.",
        ChangeEmailLabel: "Use another email",
        CodeLabel: "6 digit code",
        CodeHint: "The code lasts 5 minutes.",
        VerifyLabel: "Connect",
        VerifyFailed: "That code does not match. Check the 6 digits and enter it again.",
        ResendLabel: "Send another code",
        ResendCountdown: "You can ask for another code in {0}",
        AuthorizedLine: "Connected. Taking you back to Claude.",
        ScopeTitle: "In your account, Claude can:",
        ScopeHabits: "Manage your habits, goals and tags",
        ScopeProfile: "See your profile and your stats",
        ScopeProgress: "Log what you finish and follow your progress");

    private static readonly OAuthPageStrings Portuguese = new(
        Locale: PortugueseLocale,
        DocumentTitle: "Conectar o Orbit ao Claude",
        MarkLabel: "Orbit",
        Eyebrow: "Conectar ao Claude",
        EmailTitle: "Entre com o seu email",
        EmailBody: "O Claude pediu acesso à sua conta do Orbit.",
        EmailLabel: "Email",
        EmailPlaceholder: "nome@exemplo.com",
        EmailInvalid: "Use um email completo, como nome@exemplo.com",
        ContinueLabel: "Continuar",
        OrLabel: "ou",
        GoogleLabel: "Continuar com o Google",
        SendFailed: "O código não foi enviado. Tente de novo.",
        CodeTitle: "Digite o código",
        SentToLine: "Enviamos 6 dígitos para {0}.",
        CodeSent: "Código enviado.",
        CodeResent: "Um novo código foi enviado.",
        ChangeEmailLabel: "Trocar de email",
        CodeLabel: "Código de 6 dígitos",
        CodeHint: "O código vale 5 minutos.",
        VerifyLabel: "Conectar",
        VerifyFailed: "O código não confere. Confira os 6 dígitos e digite de novo.",
        ResendLabel: "Enviar outro código",
        ResendCountdown: "Você pode pedir outro código em {0}",
        AuthorizedLine: "Tudo certo. Voltando para o Claude.",
        ScopeTitle: "Na sua conta, o Claude pode:",
        ScopeHabits: "Gerenciar seus hábitos, metas e tags",
        ScopeProfile: "Ver seu perfil e suas estatísticas",
        ScopeProgress: "Registrar o que você concluir e acompanhar seu progresso");

    /// <summary>Picks the copy for a resolved language tag, falling back to English.</summary>
    public static OAuthPageStrings For(string language) =>
        language.StartsWith("pt", StringComparison.OrdinalIgnoreCase) ? Portuguese : English;
}
