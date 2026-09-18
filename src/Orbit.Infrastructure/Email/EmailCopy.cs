using Orbit.Application.Common;

namespace Orbit.Infrastructure.Email;

/// <summary>
/// Localized strings (en / pt-BR) for every transactional email, including
/// subjects. Preheaders reuse each email's intro line. Markup lives in the
/// embedded templates; only copy lives here.
/// </summary>
public static class EmailCopy
{
    public sealed record VerificationCodeCopy(
        string Subject, string Heading, string Intro, string Cta, string Warning, string Footer, string Preheader);

    public sealed record WelcomeCopy(
        string Subject, string Heading, string Intro, string FeaturesTitle,
        string Feature1, string Feature2, string Feature3, string Cta, string Footer, string Preheader);

    /// <summary>
    /// The intro states the grace window and the way back out of it, because confirming
    /// deletion deactivates the account rather than erasing it:
    /// <c>ConfirmAccountDeletionCommandHandler</c> schedules removal at most
    /// <see cref="AppConstants.MaxDeletionGraceDays"/> days out, and signing in again calls
    /// <c>User.CancelDeactivation</c>. Copy that promised an immediate and permanent wipe was
    /// wrong in both directions: it told somebody a returning sign-in was impossible, and it
    /// told somebody who wanted a real wipe that it had already happened.
    /// </summary>
    public sealed record AccountDeletionCopy(
        string Subject, string Heading, string Intro, string CodeLabel, string Warning, string Footer, string Preheader);

    public sealed record ApiKeyCreationCopy(
        string Subject, string Heading, string Intro, string CodeLabel, string Warning, string Footer, string Preheader);

    public sealed record WaitlistConfirmationCopy(
        string Subject, string Heading, string Intro, string Cta, string Warning, string Footer, string Preheader);

    /// <summary>
    /// The support relay is addressed to the Orbit inbox rather than to a customer,
    /// so it carries no locale pair: it renders in English for whoever answers it.
    /// </summary>
    public sealed record SupportCopy(
        string Heading, string FromLabel, string SubjectLabel, string Footer);

    public static VerificationCodeCopy VerificationCode(bool isPtBr)
    {
        var intro = isPtBr
            ? "Você pediu para entrar no Orbit. Use o código abaixo, ou toque no botão. Ele vale pelos próximos 5 minutos."
            : "You asked to sign in to Orbit. Use the code below, or tap the button. It works for the next 5 minutes.";

        return new VerificationCodeCopy(
            Subject: isPtBr ? "Seu código de acesso do Orbit" : "Your Orbit sign-in code",
            Heading: isPtBr ? "Seu código de acesso" : "Your sign-in code",
            Intro: intro,
            Cta: isPtBr ? "Entrar" : "Sign in",
            Warning: isPtBr
                ? "Se você não pediu este código, ignore este e-mail. Ninguém entra na sua conta sem ele."
                : "If you did not ask for this code, ignore this email. Nobody can sign in without it.",
            Footer: TeamFooter(isPtBr),
            Preheader: intro);
    }

    public static WelcomeCopy Welcome(bool isPtBr, string userName)
    {
        var intro = isPtBr
            ? "O Orbit mantém uma rotina à sua frente e ajuda você a retomar depois de uma falha."
            : "Orbit keeps one routine in front of you, and helps you pick it back up after a miss.";

        return new WelcomeCopy(
            Subject: isPtBr ? "Boas-vindas ao Orbit" : "Welcome to Orbit",
            Heading: isPtBr ? $"Tudo pronto, {userName}" : $"You are in, {userName}",
            Intro: intro,
            FeaturesTitle: isPtBr ? "Por onde começar" : "Where to start",
            Feature1: isPtBr
                ? "Adicione um hábito que você quer manter. Um já basta para começar."
                : "Add one habit you want to keep. One is enough to start.",
            Feature2: isPtBr
                ? "Conte à Astra o que você fez, com suas palavras, e ela registra o resto."
                : "Tell Astra what you did, in your own words, and it records the rest.",
            Feature3: isPtBr
                ? "Se você perder um dia, o Orbit mostra o caminho de volta."
                : "Miss a day and Orbit shows you the way back in.",
            Cta: isPtBr ? "Adicionar meu primeiro hábito" : "Add your first habit",
            Footer: TeamFooter(isPtBr),
            Preheader: intro);
    }

    public static AccountDeletionCopy AccountDeletion(bool isPtBr)
    {
        var intro = isPtBr
            ? $"Você pediu para excluir sua conta Orbit. O Orbit desativa a conta primeiro e apaga tudo de vez em até {AppConstants.MaxDeletionGraceDays} dias. Entre de novo enquanto ela está desativada e a conta volta com tudo dentro."
            : $"You asked to delete your Orbit account. Orbit deactivates it first, then removes it for good within {AppConstants.MaxDeletionGraceDays} days. Sign in again while it is deactivated and the account comes back with everything in it.";

        return new AccountDeletionCopy(
            Subject: isPtBr ? "Confirme a exclusão da sua conta Orbit" : "Confirm that you want to delete your Orbit account",
            Heading: isPtBr ? "Excluir sua conta Orbit" : "Delete your Orbit account",
            Intro: intro,
            CodeLabel: isPtBr ? "Digite este código para confirmar:" : "Enter this code to confirm:",
            Warning: isPtBr
                ? "Se você não pediu isso, ignore este e-mail. Sua conta continua como está."
                : "If you did not ask for this, ignore this email. Your account stays as it is.",
            Footer: TeamFooter(isPtBr),
            Preheader: intro);
    }

    public static ApiKeyCreationCopy ApiKeyCreation(bool isPtBr)
    {
        var intro = isPtBr
            ? "Você pediu para criar uma chave de API do Orbit. Uma chave permite que uma ferramenta conectada leia e altere seus dados no Orbit. Este código vale pelos próximos 10 minutos."
            : "You asked to create an Orbit API key. A key lets a connected tool read and change your Orbit data. This code works for the next 10 minutes.";

        return new ApiKeyCreationCopy(
            Subject: isPtBr ? "Confirme sua nova chave de API do Orbit" : "Confirm your new Orbit API key",
            Heading: isPtBr ? "Criar uma chave de API" : "Create an API key",
            Intro: intro,
            CodeLabel: isPtBr ? "Digite este código para confirmar:" : "Enter this code to confirm:",
            Warning: isPtBr
                ? "Se você não pediu isso, ignore este e-mail. Nenhuma chave é criada."
                : "If you did not ask for this, ignore this email. No key is created.",
            Footer: TeamFooter(isPtBr),
            Preheader: intro);
    }

    public static WaitlistConfirmationCopy WaitlistConfirmation(bool isPtBr)
    {
        var intro = isPtBr
            ? "Falta um passo. Confirme sua vaga na lista do Orbit para iPhone, e a gente escreve para você no dia em que abrir."
            : "One step left. Confirm your place on the list for Orbit on iPhone, and we write to you the day it opens.";

        return new WaitlistConfirmationCopy(
            Subject: isPtBr ? "Confirme sua vaga na lista do Orbit para iOS" : "Confirm your place on the Orbit iOS list",
            Heading: isPtBr ? "Confirme sua vaga" : "Confirm your place",
            Intro: intro,
            Cta: isPtBr ? "Confirmar minha vaga" : "Confirm my place",
            Warning: isPtBr
                ? "Se você não entrou na lista do Orbit, pode ignorar este e-mail."
                : "If you did not join the Orbit list, you can ignore this email.",
            Footer: TeamFooter(isPtBr),
            Preheader: intro);
    }

    public static SupportCopy Support() => new(
        Heading: "Support request",
        FromLabel: "From",
        SubjectLabel: "Subject",
        Footer: "Reply to this email to answer them.");

    private static string TeamFooter(bool isPtBr) => isPtBr ? "Equipe Orbit" : "The Orbit Team";
}
