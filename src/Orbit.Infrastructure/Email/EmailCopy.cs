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

    public sealed record AccountDeletionCopy(
        string Subject, string Heading, string Intro, string CodeLabel, string Warning, string Footer, string Preheader);

    public sealed record WaitlistConfirmationCopy(
        string Subject, string Heading, string Intro, string Cta, string Warning, string Footer, string Preheader);

    public static VerificationCodeCopy VerificationCode(bool isPtBr)
    {
        var intro = isPtBr
            ? "Use o código abaixo para entrar no Orbit. Ele expira em 5 minutos."
            : "Use the code below to sign in to Orbit. It expires in 5 minutes.";

        return new VerificationCodeCopy(
            Subject: isPtBr ? "Seu código de verificação do Orbit" : "Your Orbit verification code",
            Heading: isPtBr ? "Seu código de verificação" : "Your verification code",
            Intro: intro,
            Cta: isPtBr ? "Entrar no Orbit" : "Sign in to Orbit",
            Warning: isPtBr
                ? "Se você não solicitou este código, pode ignorar este e-mail."
                : "If you didn't request this code, you can safely ignore this email.",
            Footer: TeamFooter(isPtBr),
            Preheader: intro);
    }

    public static WelcomeCopy Welcome(bool isPtBr, string userName)
    {
        var intro = isPtBr
            ? "Estamos animados em ter você no Orbit. Agora você pode construir hábitos melhores, acompanhar seu progresso e manter suas metas em dia."
            : "We're excited to have you on Orbit. You're now ready to build better habits, track your progress, and stay on top of your goals.";

        return new WelcomeCopy(
            Subject: isPtBr ? "Boas-vindas ao Orbit!" : "Welcome to Orbit!",
            Heading: isPtBr ? $"Boas-vindas, {userName}!" : $"Welcome aboard, {userName}!",
            Intro: intro,
            FeaturesTitle: isPtBr ? "O que você pode fazer:" : "Here's what you can do:",
            Feature1: isPtBr ? "Crie hábitos diários, semanais ou personalizados" : "Create daily, weekly, or custom habits",
            Feature2: isPtBr ? "Acompanhe sequências e veja seu progresso" : "Track streaks and view your progress",
            Feature3: isPtBr ? "Receba insights de IA sobre suas rotinas" : "Get AI-powered insights on your routines",
            Cta: isPtBr ? "Começar" : "Get Started",
            Footer: TeamFooter(isPtBr),
            Preheader: intro);
    }

    public static AccountDeletionCopy AccountDeletion(bool isPtBr)
    {
        var intro = isPtBr
            ? "Você solicitou a exclusão da sua conta Orbit. Essa ação é irreversível. Todos os seus dados serão permanentemente excluídos, incluindo hábitos, histórico, conversas e configurações."
            : "You requested to delete your Orbit account. This action is irreversible. All your data will be permanently deleted, including habits, history, conversations, and settings.";

        return new AccountDeletionCopy(
            Subject: isPtBr ? "Confirme a exclusão da sua conta Orbit" : "Confirm your Orbit account deletion",
            Heading: isPtBr ? "Exclusão de conta" : "Account deletion",
            Intro: intro,
            CodeLabel: isPtBr ? "Use o código abaixo para confirmar:" : "Use the code below to confirm:",
            Warning: isPtBr
                ? "Se você não solicitou isso, ignore este e-mail. Sua conta permanecerá segura."
                : "If you didn't request this, ignore this email. Your account will remain safe.",
            Footer: TeamFooter(isPtBr),
            Preheader: intro);
    }

    public static WaitlistConfirmationCopy WaitlistConfirmation(bool isPtBr)
    {
        var intro = isPtBr
            ? "Você está quase na lista. Toque no botão abaixo para confirmar sua vaga no Orbit para iPhone. Avisaremos assim que estiver pronto."
            : "You're almost on the list. Tap the button below to confirm your spot for Orbit on iPhone. We'll email you the moment it's ready.";

        return new WaitlistConfirmationCopy(
            Subject: isPtBr ? "Confirme sua vaga na lista de espera do Orbit para iOS" : "Confirm your spot on the Orbit iOS waitlist",
            Heading: isPtBr ? "Confirme sua vaga na lista de espera" : "Confirm your waitlist spot",
            Intro: intro,
            Cta: isPtBr ? "Confirmar minha vaga" : "Confirm my spot",
            Warning: isPtBr
                ? "Se você não se inscreveu na lista de espera do Orbit, pode ignorar este e-mail com segurança."
                : "If you didn't sign up for the Orbit waitlist, you can safely ignore this email.",
            Footer: TeamFooter(isPtBr),
            Preheader: intro);
    }

    private static string TeamFooter(bool isPtBr) => isPtBr ? "Equipe Orbit" : "The Orbit Team";
}
