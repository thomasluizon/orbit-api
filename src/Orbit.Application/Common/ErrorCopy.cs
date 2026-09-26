using Orbit.Domain.Common;

namespace Orbit.Application.Common;

public static class ErrorCopy
{
    /// <summary>Accounts, sessions, sign-in and the emailed confirmation codes.</summary>
    private static readonly (string Code, string En, string PtBr)[] AccountAndAccess =
    [
        (ErrorCodes.UserNotFound, "That account no longer exists. Sign in again to continue.", "Essa conta não existe mais. Entre de novo para continuar."),
        (ErrorCodes.InvalidSession, "This session ended. Sign in again to continue.", "Esta sessão terminou. Entre de novo para continuar."),
        (ErrorCodes.SessionCreationFailed, "We could not start a session. Try signing in again.", "Não conseguimos iniciar a sessão. Tente entrar de novo."),
        (ErrorCodes.InvalidVerificationCode, "That code is not correct. Check it and enter it again.", "Esse código não está certo. Confira e digite de novo."),
        (ErrorCodes.CodeExpired, "That code expired. Ask for a new one.", "Esse código expirou. Peça um novo."),
        (ErrorCodes.TooManyAttempts, $"Too many tries. Wait {AppConstants.VerificationAttemptWindowMinutes} minutes and start again.", $"Tentativas demais. Espere {AppConstants.VerificationAttemptWindowMinutes} minutos e comece de novo."),
        (ErrorCodes.CodeRequestCooldown, "Wait a moment before asking for another code.", "Espere um instante antes de pedir outro código."),
        (ErrorCodes.InvalidGoogleToken, "That Google sign-in expired. Sign in with Google again.", "Esse acesso com o Google expirou. Entre com o Google de novo."),
        (ErrorCodes.GoogleEmailUnavailable, "Google did not share an email address. Allow email access and try again.", "O Google não compartilhou um e-mail. Permita o acesso ao e-mail e tente de novo."),
        (ErrorCodes.GoogleTokenAudienceMismatch, "That Google sign-in was issued for another app. Sign in from Orbit again.", "Esse acesso do Google foi emitido para outro aplicativo. Entre pelo Orbit de novo."),
        (ErrorCodes.InvalidUnsubscribeToken, "That unsubscribe link expired. Use the link in a newer email.", "Esse link para cancelar inscrição expirou. Use o link de um e-mail mais recente."),
        (ErrorCodes.InvalidWaitlistConfirmation, "That confirmation link expired. Join the list again to get a new one.", "Esse link de confirmação expirou. Entre na lista de novo para receber outro."),
        (ErrorCodes.UpgradeRequired, "This version of Orbit is no longer supported. Update the app to continue.", "Esta versão do Orbit não é mais suportada. Atualize o aplicativo para continuar."),
        (ErrorCodes.NoPermission, "This habit belongs to another account, so you cannot delete it.", "Este hábito é de outra conta, então você não pode excluí-lo."),
        (ErrorCodes.HabitNotOwned, "This habit belongs to another account.", "Este hábito é de outra conta."),
        (ErrorCodes.ValidationError, "Some of the details need a change before we can save this.", "Alguns dados precisam de ajuste antes de salvarmos isto."),
        (ErrorCodes.RateLimited, "You are doing that faster than we can keep up. Wait a moment and try again.", "Você está fazendo isso mais rápido do que conseguimos acompanhar. Espere um instante e tente de novo."),
        (ErrorCodes.InternalServerError, "We could not finish that. Try again, and write to support if it keeps happening.", "Não conseguimos concluir isso. Tente de novo, e escreva para o suporte se continuar."),
        (ErrorCodes.ConcurrentUpdateConflict, "Another change landed at the same time. Open this again and redo your edit.", "Outra alteração chegou ao mesmo tempo. Abra de novo e refaça sua edição."),
    ];

    /// <summary>Step-up confirmation and API keys.</summary>
    private static readonly (string Code, string En, string PtBr)[] StepUpAndApiKeys =
    [
        (ErrorCodes.StepUpNotRequired, "This action does not need an extra confirmation.", "Esta ação não precisa de confirmação extra."),
        (ErrorCodes.StepUpCooldown, "Wait a moment before asking for another confirmation code.", "Espere um instante antes de pedir outro código de confirmação."),
        (ErrorCodes.StepUpChallengeNotFound, "That confirmation expired. Start the action again.", "Essa confirmação expirou. Comece a ação de novo."),
        (ErrorCodes.InvalidStepUpCode, "That confirmation code is not correct. Check it and enter it again.", "Esse código de confirmação não está certo. Confira e digite de novo."),
        (ErrorCodes.ApiKeyNotFound, "That API key is not here any more.", "Essa chave de API não está mais aqui."),
        (ErrorCodes.ApiKeyCreationChallengeRequired, "Confirm the emailed code before managing API keys.", "Confirme o código enviado por e-mail antes de gerenciar chaves de API."),
        (ErrorCodes.MaxApiKeys, "You can keep {0} active API keys. Revoke one to create another.", "Você pode manter {0} chaves de API ativas. Revogue uma para criar outra."),
        (DomainErrors.ApiKeyNameRequired.Code, "Enter a name for the API key.", "Digite um nome para a chave de API."),
        (DomainErrors.ApiKeyNameTooLong.Code, "An API key name fits 50 characters. Shorten it.", "Um nome de chave de API cabe em 50 caracteres. Encurte."),
        (DomainErrors.ApiKeyExpiryInPast.Code, "Pick an expiry date in the future.", "Escolha uma data de validade no futuro."),
        (DomainErrors.ApiKeyScopesInvalid.Code, "Pick at least one permission for this key.", "Escolha pelo menos uma permissão para esta chave."),
    ];

    /// <summary>Habits, sub-habits, logging and skipping.</summary>
    private static readonly (string Code, string En, string PtBr)[] Habits =
    [
        (ErrorCodes.HabitNotFound, "That habit is not here any more. It may have been deleted on another device.", "Esse hábito não está mais aqui. Ele pode ter sido excluído em outro aparelho."),
        (ErrorCodes.HabitLimitReached, "You've reached the {0} habit limit.", "Você chegou ao limite de {0} hábitos."),
        (ErrorCodes.ParentHabitNotFound, "We could not find the habit this sits under. Pick another one.", "Não encontramos o hábito principal. Escolha outro."),
        (ErrorCodes.TargetParentNotFound, "We could not find the habit you are moving this under. Pick another one.", "Não encontramos o hábito de destino. Escolha outro."),
        (ErrorCodes.SelfParent, "A habit cannot sit under itself. Pick a different one.", "Um hábito não pode ficar dentro de si mesmo. Escolha outro."),
        (ErrorCodes.CircularReference, "A habit cannot sit under one of its own sub-habits. Pick a different one.", "Um hábito não pode ficar dentro de um sub-hábito dele mesmo. Escolha outro."),
        (ErrorCodes.MaxDepthReached, "Habits nest {0} levels deep. Move this one higher up.", "Hábitos chegam a {0} níveis. Mova este para um nível acima."),
        (ErrorCodes.GeneralMismatchWithParent, "A sub-habit follows its parent on the general setting. Match them and try again.", "Um sub-hábito segue o hábito principal na opção geral. Deixe os dois iguais e tente de novo."),
        (ErrorCodes.GeneralMismatchWithChildren, "The sub-habits here use a different general setting. Change them first.", "Os sub-hábitos aqui usam outra opção geral. Mude os sub-hábitos primeiro."),
        (ErrorCodes.HabitAlreadyCompleted, "You already marked this done for that day.", "Você já marcou isto como feito nesse dia."),
        (ErrorCodes.CannotSkipFutureDate, "You can skip a day once it arrives.", "Você pode pular um dia quando ele chegar."),
        (ErrorCodes.CannotLogFutureDate, "You can log a day once it arrives.", "Você pode registrar um dia quando ele chegar."),
        (ErrorCodes.HabitNotYetDue, "This is not due yet, so there is nothing to skip.", "Isto ainda não venceu, então não há o que pular."),
        (ErrorCodes.HabitNotOverdue, "This is not overdue.", "Isto não está atrasado."),
        (ErrorCodes.NotScheduledOnDate, "This habit is not scheduled on that day.", "Este hábito não está agendado nesse dia."),
        (ErrorCodes.AllInstancesDone, "Every round for this period is already done or skipped.", "Todas as rodadas deste período já foram feitas ou puladas."),
        (ErrorCodes.BeyondOverdueWindow, "That day is too far back to log. Log a more recent one.", "Esse dia está distante demais para registrar. Registre um mais recente."),
        (DomainErrors.TitleRequired.Code, "Give this a title.", "Dê um título a isto."),
        (DomainErrors.AlreadyLoggedForDate.Code, "You already logged this habit on that day.", "Você já registrou este hábito nesse dia."),
        (DomainErrors.OnlyFlexibleHabitsSkippable.Code, "Only a flexible habit can be skipped this way.", "Só um hábito flexível pode ser pulado assim."),
        (DomainErrors.CannotSkipOneTimeTask.Code, "A one-time task cannot be skipped. Delete it if you no longer need it.", "Uma tarefa única não pode ser pulada. Exclua se você não precisar mais dela."),
        (DomainErrors.LogNotFoundForDate.Code, "There is no entry on that day.", "Não há registro nesse dia."),
        (DomainErrors.GeneralHabitHasFrequency.Code, "A general habit has no schedule, so it carries no frequency.", "Um hábito geral não tem agenda, então ele não tem frequência."),
        (DomainErrors.GeneralHabitIsBadHabit.Code, "A general habit cannot be one you are quitting.", "Um hábito geral não pode ser um hábito que você está largando."),
        (DomainErrors.FrequencyQuantityInvalid.Code, "Set the frequency to 1 or more.", "Defina a frequência como 1 ou mais."),
        (DomainErrors.IntervalWeeksInvalid.Code, $"Set the repeat interval between 1 and {DomainConstants.MaxIntervalWeeks} weeks.", $"Defina o intervalo de repetição entre 1 e {DomainConstants.MaxIntervalWeeks} semanas."),
        (DomainErrors.RecurrenceScheduleUnsatisfiable.Code, "No future date fits that schedule. Widen the days or the interval.", "Nenhuma data futura encaixa nessa agenda. Amplie os dias ou o intervalo."),
        (DomainErrors.FlexibleNeedsFrequencyUnit.Code, "Pick how often this flexible habit repeats.", "Escolha com que frequência este hábito flexível se repete."),
        (DomainErrors.FlexibleHasDays.Code, "A flexible habit has no fixed days. Clear the days, or turn flexible off.", "Um hábito flexível não tem dias fixos. Limpe os dias, ou desative o modo flexível."),
        (DomainErrors.DaysRequireQuantityOne.Code, "Specific days work only when the habit repeats once a day. Set it to once a day, or clear the days.", "Dias específicos só funcionam quando o hábito se repete uma vez ao dia. Defina uma vez ao dia, ou limpe os dias."),
        (DomainErrors.EndTimeBeforeStartTime.Code, "Set the end time after the start time.", "Defina o horário de fim depois do horário de início."),
        (DomainErrors.OneTimeTaskHasEndDate.Code, "A one-time task carries no end date.", "Uma tarefa única não tem data de fim."),
        (DomainErrors.GeneralHabitHasEndDate.Code, "A general habit carries no end date.", "Um hábito geral não tem data de fim."),
        (DomainErrors.EndDateBeforeStartDate.Code, "Set the end date on or after the start date.", "Defina a data de fim igual ou depois da data de início."),
        (DomainErrors.MaxScheduledReminders.Code, "A habit holds {0} scheduled reminders. Remove one to add another.", "Um hábito guarda {0} lembretes agendados. Remova um para adicionar outro."),
        (DomainErrors.MaxReminderTimes.Code, "A habit holds {0} reminder times. Remove one to add another.", "Um hábito guarda {0} horários de lembrete. Remova um para adicionar outro."),
        (DomainErrors.DuplicateScheduledReminders.Code, "Two reminders point at the same moment. Change one of them.", "Dois lembretes apontam para o mesmo momento. Mude um deles."),
        (DomainErrors.EmojiTooLong.Code, "Pick a single emoji for this habit.", "Escolha um único emoji para este hábito."),
    ];

    /// <summary>Goals and their progress.</summary>
    private static readonly (string Code, string En, string PtBr)[] Goals =
    [
        (ErrorCodes.GoalNotFound, "That goal is not here any more.", "Essa meta não está mais aqui."),
        (ErrorCodes.MaxHabitsPerGoal, "A goal links {0} habits. Unlink one to add another.", "Uma meta liga {0} hábitos. Desligue um para adicionar outro."),
        (ErrorCodes.InvalidGoalStatus, "Pick a status from the list.", "Escolha um status da lista."),
        (ErrorCodes.DeadlineInPast, "Pick a deadline from today onward.", "Escolha um prazo de hoje em diante."),
        (ErrorCodes.NoActiveGoals, "You have no active goals right now.", "Você não tem metas ativas agora."),
        (ErrorCodes.NoGoalsData, "There is nothing to review yet.", "Ainda não há nada para revisar."),
        (DomainErrors.TargetValueInvalid.Code, "Set the target above 0.", "Defina o alvo acima de 0."),
        (DomainErrors.UnitRequired.Code, "Name what you are counting, such as pages or minutes.", "Diga o que você conta, como páginas ou minutos."),
        (DomainErrors.GoalNotActive.Code, "Only an active goal takes progress. Reopen it first.", "Só uma meta ativa recebe progresso. Reabra a meta primeiro."),
        (DomainErrors.ProgressValueNegative.Code, "Progress cannot go below 0.", "O progresso não pode ficar abaixo de 0."),
        (DomainErrors.GoalProgressDerived.Code, "This goal counts progress from its linked habits, so it cannot be set by hand.", "Esta meta conta o progresso pelos hábitos ligados, então ela não pode ser ajustada à mão."),
        (DomainErrors.NotStreakGoal.Code, "This goal does not follow a streak.", "Esta meta não acompanha sequência."),
        (DomainErrors.NotStandardGoal.Code, "This goal does not count completions.", "Esta meta não conta conclusões."),
        (DomainErrors.StandardGoalHasNoLinkedHabits.Code, "Link at least one habit before this goal can count completions.", "Ligue pelo menos um hábito antes desta meta contar conclusões."),
        (DomainErrors.GoalAlreadyCompleted.Code, "This goal is already complete.", "Esta meta já está concluída."),
        (DomainErrors.GoalAlreadyAbandoned.Code, "This goal is already dropped.", "Esta meta já foi abandonada."),
        (DomainErrors.GoalAlreadyActive.Code, "This goal is already active.", "Esta meta já está ativa."),
    ];

    /// <summary>Tags, saved facts and checklist templates.</summary>
    private static readonly (string Code, string En, string PtBr)[] TagsFactsAndTemplates =
    [
        (ErrorCodes.TagNotFound, "That tag is not here any more.", "Essa etiqueta não está mais aqui."),
        (ErrorCodes.DuplicateTagName, "A tag already uses that name. Pick another one.", "Já existe uma etiqueta com esse nome. Escolha outro."),
        (ErrorCodes.MaxTagsPerHabit, "A habit carries {0} tags. Remove one to add another.", "Um hábito carrega {0} etiquetas. Remova uma para adicionar outra."),
        (ErrorCodes.FactNotFound, "Astra no longer has that saved note.", "A Astra não tem mais essa anotação salva."),
        (ErrorCodes.UserFactsLimitReached, "Astra holds {0} saved notes. Delete one to add another.", "A Astra guarda {0} anotações. Exclua uma para adicionar outra."),
        (ErrorCodes.DuplicateFact, "Astra already remembers something like that.", "A Astra já lembra de algo assim."),
        (ErrorCodes.TemplateNotFound, "That checklist is not here any more.", "Essa lista não está mais aqui."),
        (ErrorCodes.SuggestionNotFound, "That suggestion expired. Ask Astra again.", "Essa sugestão expirou. Pergunte à Astra de novo."),
        (DomainErrors.TagNameRequired.Code, "Enter a tag name.", "Digite um nome para a etiqueta."),
        (DomainErrors.TagNameTooLong.Code, "A tag name fits 50 characters. Shorten it.", "Um nome de etiqueta cabe em 50 caracteres. Encurte."),
        (DomainErrors.TagColorRequired.Code, "Pick a colour for the tag.", "Escolha uma cor para a etiqueta."),
        (DomainErrors.FactTextRequired.Code, "Write the note you want Astra to remember.", "Escreva a anotação que você quer que a Astra lembre."),
        (DomainErrors.FactTextTooLong.Code, "A note for Astra fits 500 characters. Shorten it.", "Uma anotação para a Astra cabe em 500 caracteres. Encurte."),
        (DomainErrors.FactTextSuspicious.Code, "We cannot save that note. Rewrite it in plain words and try again.", "Não podemos salvar essa anotação. Reescreva com palavras simples e tente de novo."),
        (DomainErrors.TemplateNameRequired.Code, "Enter a checklist name.", "Digite um nome para a lista."),
        (DomainErrors.TemplateNameTooLong.Code, "A checklist name fits 100 characters. Shorten it.", "Um nome de lista cabe em 100 caracteres. Encurte."),
        (DomainErrors.TemplateItemsRequired.Code, "Add at least one item to the checklist.", "Adicione pelo menos um item à lista."),
    ];

    /// <summary>Astra, the daily summary and every other model-backed surface.</summary>
    private static readonly (string Code, string En, string PtBr)[] Astra =
    [
        (ErrorCodes.AiUnavailable, "Astra is not answering right now. Try again in a moment.", "A Astra não está respondendo agora. Tente de novo em instantes."),
        (ErrorCodes.AiEmptyResponse, "Astra did not return an answer. Ask again.", "A Astra não devolveu uma resposta. Pergunte de novo."),
        (ErrorCodes.AiNoActiveConversation, "That conversation is no longer open. Start a new one.", "Essa conversa não está mais aberta. Comece outra."),
        (ErrorCodes.AiSummaryDisabled, "The daily summary is turned off. Turn it on in settings.", "O resumo diário está desativado. Ative nas configurações."),
        (ErrorCodes.ChatHistoryTooLarge, "This conversation is too long for Astra to carry. Start a new one.", "Esta conversa está longa demais para a Astra. Comece outra."),
        (ErrorCodes.MessageTooLong, "Your message fits between 1 and {0} characters.", "Sua mensagem cabe entre 1 e {0} caracteres."),
        (ErrorCodes.InvalidChatHistory, "We could not read that conversation. Start a new one.", "Não conseguimos ler essa conversa. Comece outra."),
        (ErrorCodes.InvalidClientContext, "We could not read what the app sent. Try again.", "Não conseguimos ler o que o aplicativo enviou. Tente de novo."),
        (ErrorCodes.ClarificationNotFound, "That question expired. Ask Astra again.", "Essa pergunta expirou. Pergunte à Astra de novo."),
        (ErrorCodes.ClarificationAlreadyResolved, "You already answered that question.", "Você já respondeu essa pergunta."),
        (ErrorCodes.ClarificationValueEmpty, "Write an answer before sending it.", "Escreva uma resposta antes de enviar."),
        (ErrorCodes.ClarificationValueTooLong, "That answer fits {0} characters. Shorten it.", "Essa resposta cabe em {0} caracteres. Encurte."),
        (ErrorCodes.ClarificationValueNotJsonObject, "We could not read that answer. Pick one of the offered options.", "Não conseguimos ler essa resposta. Escolha uma das opções oferecidas."),
        (ErrorCodes.ClarificationValueNotOffered, "Pick one of the options Astra offered.", "Escolha uma das opções que a Astra ofereceu."),
        (ErrorCodes.PendingOperationNotFound, "That pending action expired. Ask Astra again.", "Essa ação pendente expirou. Peça à Astra de novo."),
        (ErrorCodes.NoHabitsForPeriod, "You have no habits in this period yet.", "Você ainda não tem hábitos neste período."),
        (ErrorCodes.InvalidPeriod, "Pick a period: week, month, quarter, semester or year.", "Escolha um período: semana, mês, trimestre, semestre ou ano."),
        (ErrorCodes.InvalidClosedMonthParameters, "Pick a year and a month together, and only with the month period.", "Escolha um ano e um mês juntos, e só com o período de mês."),
        (ErrorCodes.RecapMonthNotClosed, "That month has not finished yet. Come back after it ends to read the recap.", "Esse mês ainda não terminou. Volte depois que ele acabar para ler a retrospectiva."),
        (ErrorCodes.RecapMonthBeforeAccount, "That month is before this account existed.", "Esse mês é anterior à criação desta conta."),
        (ErrorCodes.RecapPeriodBeforeAccount, "That period is before this account existed. Choose a later one.", "Esse período é anterior à criação desta conta. Escolha um período mais recente."),
        (ErrorCodes.InvalidClosedWeekParameters, "Choose a week that begins on Sunday or Monday.", "Escolha uma semana que comece no domingo ou na segunda-feira."),
        (ErrorCodes.InvalidClosedYearParameters, "Choose a valid year, and use it only with the year period.", "Escolha um ano válido e use apenas com o período de ano."),
        (ErrorCodes.RecapWeekNotClosed, "That week has not finished yet. Come back after it ends to read the recap.", "Essa semana ainda não terminou. Volte depois que ela acabar para ler a retrospectiva."),
        (ErrorCodes.RecapYearNotClosed, "That year has not finished yet. Come back after it ends to read the recap.", "Esse ano ainda não terminou. Volte depois que ele acabar para ler a retrospectiva."),
        (DomainErrors.ClosedMonthRangeInvalid.Code, "A monthly recap covers one whole calendar month.", "Uma retrospectiva mensal cobre um mês inteiro."),
        (DomainErrors.ClosedWeekRangeInvalid.Code, "A weekly recap covers seven full days. Choose a complete week.", "Uma retrospectiva semanal cobre sete dias completos. Escolha uma semana completa."),
        (DomainErrors.ClosedYearRangeInvalid.Code, "A yearly recap covers one full calendar year. Choose a complete year.", "Uma retrospectiva anual cobre um ano inteiro. Escolha um ano completo."),
        (DomainErrors.ClosedMonthRecapResponseInvalid.Code, "We could not read that recap. Open it again.", "Não conseguimos ler essa retrospectiva. Abra de novo."),
    ];

    /// <summary>Billing, the Pro gate and the store webhooks.</summary>
    private static readonly (string Code, string En, string PtBr)[] Billing =
    [
        (ErrorCodes.PayGate, "This is a Pro feature.", "Este é um recurso Pro."),
        (ErrorCodes.SubscriptionNotFound, "We could not find a subscription on this account.", "Não encontramos nenhuma assinatura nesta conta."),
        (ErrorCodes.NoActiveSubscription, "This account has no active subscription.", "Esta conta não tem assinatura ativa."),
        (ErrorCodes.InvalidBillingInterval, "Choose the monthly or the yearly plan.", "Escolha o plano mensal ou o anual."),
        (ErrorCodes.PlayPurchaseNotActive, "This Google Play purchase is not active. Check your Play subscriptions.", "Esta compra do Google Play não está ativa. Confira suas assinaturas na Play."),
        (ErrorCodes.PlayPurchaseAccountMismatch, "This Google Play purchase belongs to another Orbit account. Sign in with that account.", "Esta compra do Google Play é de outra conta Orbit. Entre com aquela conta."),
        (ErrorCodes.PaymentServiceUnavailable, "Our payment provider is not answering. Try again in a moment.", "Nosso provedor de pagamento não está respondendo. Tente de novo em instantes."),
        (ErrorCodes.BillingDetailsUnavailable, "We could not load your billing details. Try again in a moment.", "Não conseguimos carregar seus dados de cobrança. Tente de novo em instantes."),
        (ErrorCodes.WebhookSecretNotConfigured, "We could not verify that payment update. Write to support if your plan looks wrong.", "Não conseguimos verificar essa atualização de pagamento. Escreva para o suporte se seu plano parecer errado."),
        (ErrorCodes.InvalidWebhookSignature, "We could not verify that payment update. Write to support if your plan looks wrong.", "Não conseguimos verificar essa atualização de pagamento. Escreva para o suporte se seu plano parecer errado."),
        (ErrorCodes.WebhookStripeApiError, "Our payment provider returned an error. Your plan is unchanged.", "Nosso provedor de pagamento retornou um erro. Seu plano segue como estava."),
        (ErrorCodes.WebhookProcessingFailed, "We could not apply that payment update. Write to support if your plan looks wrong.", "Não conseguimos aplicar essa atualização de pagamento. Escreva para o suporte se seu plano parecer errado."),
        (ErrorCodes.PlayNotificationVerificationFailed, "We could not verify that Google Play update. Your plan is unchanged.", "Não conseguimos verificar essa atualização do Google Play. Seu plano segue como estava."),
        (ErrorCodes.InvalidReferralCode, "That referral code is not valid. Check it and enter it again.", "Esse código de indicação não é válido. Confira e digite de novo."),
        (ErrorCodes.ReferralCapReached, "You have used every referral on this account.", "Você já usou todas as indicações desta conta."),
        (ErrorCodes.SelfReferral, "You cannot use your own referral code.", "Você não pode usar seu próprio código de indicação."),
        (ErrorCodes.AlreadyReferred, "This account already used a referral code.", "Esta conta já usou um código de indicação."),
        (DomainErrors.ProUsersDoNotSeeAds.Code, "Pro accounts show no ads, so there is nothing to watch here.", "Contas Pro não mostram anúncios, então não há nada para assistir aqui."),
        (DomainErrors.AdRewardLimitReached.Code, "You collected every ad reward for today. Come back tomorrow.", "Você já coletou todas as recompensas de anúncio de hoje. Volte amanhã."),
    ];

    /// <summary>Streaks, freezes and gamification.</summary>
    private static readonly (string Code, string En, string PtBr)[] Streaks =
    [
        (ErrorCodes.StreakRepairUnavailable, "There is no streak to repair for yesterday.", "Não há sequência para reparar ontem."),
        (ErrorCodes.StreakGapRepairUnavailable, "Those days do not cover the whole gap ending yesterday. Pick the missing ones too.", "Esses dias não cobrem todo o intervalo que termina ontem. Escolha também os que faltam."),
        (DomainErrors.NoStreakFreezesAccumulated.Code, "You have no streak freezes banked yet.", "Você ainda não tem congelamentos de sequência guardados."),
        (DomainErrors.InvalidStreakGap.Code, "Pick days that run without a break and end yesterday.", "Escolha dias seguidos que terminem ontem."),
        (DomainErrors.InsufficientStreakFreezes.Code, "You do not have enough banked freezes to cover the whole gap.", "Você não tem congelamentos guardados suficientes para cobrir todo o intervalo."),
    ];

    /// <summary>Friends, cheers, challenges and accountability pairs.</summary>
    private static readonly (string Code, string En, string PtBr)[] Social =
    [
        (ErrorCodes.SocialDisabled, "Social features are off. Turn them on in settings to use this.", "Os recursos sociais estão desativados. Ative nas configurações para usar isto."),
        (ErrorCodes.HandleTaken, "Someone already uses that handle. Pick another one.", "Alguém já usa esse nome de usuário. Escolha outro."),
        (ErrorCodes.FriendLimitReached, "You have {0} friends, which is the limit. Remove one to add another.", "Você tem {0} amigos, que é o limite. Remova um para adicionar outro."),
        (ErrorCodes.AlreadyFriends, "You are already connected with this person.", "Você já está conectado com esta pessoa."),
        (ErrorCodes.Blocked, "This is not available with this person.", "Isto não está disponível com esta pessoa."),
        (ErrorCodes.NotFriends, "You can only do this with someone who accepted your request.", "Você só pode fazer isto com alguém que aceitou seu pedido."),
        (ErrorCodes.ContentRejected, "This note cannot be sent. Reword it and try again.", "Esta mensagem não pode ser enviada. Reescreva e tente de novo."),
        (ErrorCodes.FriendRequestNotFound, "That friend request is not here any more.", "Esse pedido de amizade não está mais aqui."),
        (ErrorCodes.CheerNotFound, "That cheer is not here any more.", "Essa torcida não está mais aqui."),
        (ErrorCodes.ChallengeNotFound, "That challenge is not here any more.", "Esse desafio não está mais aqui."),
        (ErrorCodes.ChallengeFull, "This challenge holds {0} people and is full.", "Este desafio comporta {0} pessoas e está cheio."),
        (ErrorCodes.AlreadyJoinedChallenge, "You already joined this challenge.", "Você já entrou neste desafio."),
        (ErrorCodes.NotChallengeParticipant, "You are not in this challenge.", "Você não está neste desafio."),
        (ErrorCodes.InvalidJoinCode, "That join code is not valid. Check it and enter it again.", "Esse código de entrada não é válido. Confira e digite de novo."),
        (ErrorCodes.ChallengeClosed, "This challenge is no longer open to new people.", "Este desafio não aceita mais participantes."),
        (ErrorCodes.PairNotFound, "That accountability pair is not here any more.", "Essa dupla não está mais aqui."),
        (ErrorCodes.PairLimitReached, "You have {0} accountability pairs, which is the limit. End one to start another.", "Você tem {0} duplas, que é o limite. Encerre uma para começar outra."),
        (ErrorCodes.AlreadyPaired, "You already have an accountability pair with this person.", "Você já tem uma dupla com esta pessoa."),
        (ErrorCodes.AlreadyCheckedIn, "You already checked in today.", "Você já fez check-in hoje."),
        (DomainErrors.InvalidHandle.Code, "A handle uses 3 to 20 letters, numbers or underscores.", "Um nome de usuário usa de 3 a 20 letras, números ou sublinhados."),
        (DomainErrors.CannotFriendSelf.Code, "You cannot send yourself a friend request.", "Você não pode enviar um pedido de amizade para você mesmo."),
        (DomainErrors.FriendshipNotPending.Code, "This friend request was already answered.", "Este pedido de amizade já foi respondido."),
        (DomainErrors.CannotCheerSelf.Code, "You can cheer a friend, not yourself.", "Você pode torcer por um amigo, não por você."),
        (DomainErrors.CheerNoteTooLong.Code, "A cheer note fits {0} characters. Shorten it.", "Uma mensagem de torcida cabe em {0} caracteres. Encurte."),
        (DomainErrors.CannotPairSelf.Code, "You can pair with a friend, not yourself.", "Você pode formar dupla com um amigo, não com você."),
        (DomainErrors.PairNotPending.Code, "This accountability invite was already answered.", "Este convite de dupla já foi respondido."),
        (DomainErrors.AccountabilityNoteTooLong.Code, "A check-in note fits {0} characters. Shorten it.", "Uma nota de check-in cabe em {0} caracteres. Encurte."),
        (DomainErrors.CannotBlockSelf.Code, "You can block someone else, not yourself.", "Você pode bloquear outra pessoa, não você."),
        (DomainErrors.CannotReportSelf.Code, "You can report someone else, not yourself.", "Você pode denunciar outra pessoa, não você."),
        (DomainErrors.ReportDetailsTooLong.Code, "Report details fit {0} characters. Shorten them.", "Os detalhes da denúncia cabem em {0} caracteres. Encurte."),
        (DomainErrors.ChallengeTargetRequired.Code, "Set a target above 0 for a goal challenge.", "Defina um alvo acima de 0 para um desafio de meta."),
        (DomainErrors.ChallengeTargetNotAllowed.Code, "A streak challenge carries no target count.", "Um desafio de sequência não tem contagem alvo."),
        (DomainErrors.ChallengePeriodInvalid.Code, "Set the challenge end date on or after its start date.", "Defina o fim do desafio igual ou depois do início."),
        (DomainErrors.ChallengeJoinCodeRequired.Code, "Enter the join code.", "Digite o código de entrada."),
    ];

    /// <summary>Google Calendar connection and sync.</summary>
    private static readonly (string Code, string En, string PtBr)[] Calendar =
    [
        (ErrorCodes.CalendarNotConnected, "Google Calendar is not connected. Sign in with Google to connect it.", "O Google Agenda não está conectado. Entre com o Google para conectar."),
        (ErrorCodes.CalendarReconnectRequired, "Your Google Calendar connection expired. Connect it again.", "Sua conexão com o Google Agenda expirou. Conecte de novo."),
        (ErrorCodes.CalendarFetchFailed, "We could not load your calendar. Try again in a moment.", "Não conseguimos carregar sua agenda. Tente de novo em instantes."),
        (ErrorCodes.AutoSyncEnabledRequired, "Choose whether auto-sync is on or off.", "Escolha se a sincronização automática fica ligada ou desligada."),
        (ErrorCodes.CalendarIdsRequired, "Pick at least one calendar to sync.", "Escolha pelo menos uma agenda para sincronizar."),
        (DomainErrors.CalendarAutoSyncProRequired.Code, "Calendar auto-sync is a Pro feature.", "A sincronização automática da agenda é um recurso Pro."),
        (DomainErrors.CalendarAutoSyncNotConnected.Code, "Connect Google Calendar before turning auto-sync on.", "Conecte o Google Agenda antes de ligar a sincronização automática."),
    ];

    /// <summary>Push delivery registration.</summary>
    private static readonly (string Code, string En, string PtBr)[] Notifications =
    [
        (ErrorCodes.NotificationNotFound, "That notification is not here any more.", "Essa notificação não está mais aqui."),
        (ErrorCodes.NoPushSubscriptions, "No device on this account is set up for notifications. Turn them on in settings.", "Nenhum aparelho desta conta está configurado para notificações. Ative nas configurações."),
        (ErrorCodes.PushEndpointInvalid, "This device could not register for notifications. Turn them off and on again in settings.", "Este aparelho não conseguiu registrar as notificações. Desative e ative de novo nas configurações."),
        (ErrorCodes.FcmTokenRequired, "This device could not register for notifications. Turn them off and on again in settings.", "Este aparelho não conseguiu registrar as notificações. Desative e ative de novo nas configurações."),
        (ErrorCodes.PushEndpointOwnedByOtherUser, "This device already receives notifications for another account. Sign out there first.", "Este aparelho já recebe notificações de outra conta. Saia daquela conta primeiro."),
        (DomainErrors.PushEndpointRequired.Code, "This device could not register for notifications. Turn them off and on again in settings.", "Este aparelho não conseguiu registrar as notificações. Desative e ative de novo nas configurações."),
        (DomainErrors.PushP256dhRequired.Code, "This browser could not register for notifications. Allow them and try again.", "Este navegador não conseguiu registrar as notificações. Permita e tente de novo."),
        (DomainErrors.PushAuthKeyRequired.Code, "This browser could not register for notifications. Allow them and try again.", "Este navegador não conseguiu registrar as notificações. Permita e tente de novo."),
    ];

    /// <summary>Uploaded images and recorded audio.</summary>
    private static readonly (string Code, string En, string PtBr)[] Media =
    [
        (ErrorCodes.ImageTooLarge, "That image is over {0}MB. Pick a smaller one.", "Essa imagem passa de {0}MB. Escolha uma menor."),
        (ErrorCodes.ImageEmpty, "That file is empty. Pick another one.", "Esse arquivo está vazio. Escolha outro."),
        (ErrorCodes.ImageExtensionNotAllowed, "Orbit does not take {0} files. Use one of these: {1}.", "O Orbit não aceita arquivos {0}. Use um destes: {1}."),
        (ErrorCodes.ImageFormatUnknown, "We could not read that image. Save it as a PNG or a JPG and try again.", "Não conseguimos ler essa imagem. Salve como PNG ou JPG e tente de novo."),
        (ErrorCodes.ImageNotAnImage, "That file is not an image. We read it as {0}.", "Esse arquivo não é uma imagem. Nós o lemos como {0}."),
        (ErrorCodes.AudioRequired, "Record something before sending it.", "Grave algo antes de enviar."),
        (ErrorCodes.AudioTranscriptionFailed, "We could not turn that recording into words. Record it again.", "Não conseguimos transformar essa gravação em palavras. Grave de novo."),
        (ErrorCodes.AudioTranscriptionEmpty, "We heard no speech in that recording. Record it again.", "Não ouvimos fala nessa gravação. Grave de novo."),
    ];

    /// <summary>Offline sync, batched mutations and support.</summary>
    private static readonly (string Code, string En, string PtBr)[] SyncAndSupport =
    [
        (ErrorCodes.SyncWindowExceeded, "This device was offline too long to catch up from where it stopped. Open Orbit again to load your data fresh.", "Este aparelho ficou offline tempo demais para continuar de onde parou. Abra o Orbit de novo para carregar seus dados do zero."),
        (ErrorCodes.NoMutations, "There is nothing to sync.", "Não há nada para sincronizar."),
        (ErrorCodes.TooManyMutations, "Orbit syncs {0} changes at a time. The rest are still on this device. Try again to send them.", "O Orbit sincroniza {0} mudanças por vez. O resto continua neste aparelho. Tente de novo para enviá-las."),
        (ErrorCodes.MutationFailed, "We could not apply one of your offline changes. Open the item and redo it.", "Não conseguimos aplicar uma das suas mudanças offline. Abra o item e refaça."),
        (ErrorCodes.SubjectRequired, "Enter a subject.", "Digite um assunto."),
        (ErrorCodes.MessageRequired, "Write your message.", "Escreva sua mensagem."),
    ];

    /// <summary>Profile and preference guards that live on the user entity.</summary>
    private static readonly (string Code, string En, string PtBr)[] Profile =
    [
        (DomainErrors.UserIdRequired.Code, "We could not tell which account this belongs to. Sign in again.", "Não conseguimos identificar a conta. Entre de novo."),
        (DomainErrors.TokenHashRequired.Code, "That sign-in link is incomplete. Ask for a new one.", "Esse link de acesso está incompleto. Peça um novo."),
        (DomainErrors.SessionNotActive.Code, "This session ended. Sign in again to continue.", "Esta sessão terminou. Entre de novo para continuar."),
        (DomainErrors.NameRequired.Code, "Enter a name.", "Digite um nome."),
        (DomainErrors.NameTooLong.Code, "A name fits {0} characters. Shorten it.", "Um nome cabe em {0} caracteres. Encurte."),
        (DomainErrors.EmailRequired.Code, "Enter your email address.", "Digite seu e-mail."),
        (DomainErrors.InvalidEmailFormat.Code, "Check the email address. It needs an at sign and a domain.", "Confira o e-mail. Ele precisa de um arroba e um domínio."),
        (DomainErrors.InvalidTimezone.Code, "We do not recognise the timezone {0}. Pick one from the list.", "Não reconhecemos o fuso horário {0}. Escolha um da lista."),
        (DomainErrors.InvalidThemePreference.Code, "Choose the dark or the light theme.", "Escolha o tema escuro ou o claro."),
        (DomainErrors.InvalidColorScheme.Code, "Choose a colour scheme from the list.", "Escolha um esquema de cores da lista."),
        (DomainErrors.InvalidWeekStartDay.Code, "Choose Sunday or Monday as the first day of the week.", "Escolha domingo ou segunda como primeiro dia da semana."),
    ];

    private static readonly (string Code, string En, string PtBr)[] CountedVariants =
    [
        (ErrorCodes.InvalidVerificationCode,
            "That code is not correct. Remaining attempts: {0}",
            "Esse código não está certo. Remaining attempts: {0}"),
    ];

    private static readonly Dictionary<string, (string En, string PtBr)> CountedCatalog =
        CountedVariants.ToDictionary(entry => entry.Code, entry => (entry.En, entry.PtBr), StringComparer.Ordinal);

    private static readonly Dictionary<string, (string En, string PtBr)> Catalog = BuildCatalog();

    /// <summary>Every error code this API can return, paired with its copy in both languages.</summary>
    public static IReadOnlyDictionary<string, (string En, string PtBr)> All => Catalog;

    public static string Resolve(string code, bool isPtBr)
    {
        var copy = Catalog.TryGetValue(code, out var found)
            ? found
            : throw new InvalidOperationException($"Error code has no user-facing copy: {code}");
        return isPtBr ? copy.PtBr : copy.En;
    }

    /// <inheritdoc cref="Resolve"/>
    public static string English(string code) =>
        Catalog.TryGetValue(code, out var copy)
            ? copy.En
            : throw new InvalidOperationException($"Error code has no user-facing copy: {code}");

    /// <summary>
    /// The English counted variant for <paramref name="code"/>, which <see cref="ErrorMessages"/>
    /// takes as the message of a constant whose call site always applies a count.
    /// </summary>
    public static string EnglishWithCount(string code) =>
        CountedCatalog.TryGetValue(code, out var copy)
            ? copy.En
            : throw new InvalidOperationException($"Error code has no counted copy: {code}");

    /// <summary>Every counted variant, paired with its code.</summary>
    public static IReadOnlyDictionary<string, (string En, string PtBr)> AllCounted => CountedCatalog;

    /// <summary>
    /// The user-facing message for <paramref name="code"/>, formatted with
    /// <paramref name="args"/> when the copy carries a placeholder. Returns false when the
    /// code has no entry, which leaves the caller on the raw message.
    /// </summary>
    public static bool TryResolve(
        string? code, bool isPtBr, IReadOnlyList<object?> args, out string message)
    {
        if (code is null)
        {
            message = string.Empty;
            return false;
        }

        if (args.Count > 0 && CountedCatalog.TryGetValue(code, out var counted))
        {
            message = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                isPtBr ? counted.PtBr : counted.En,
                [.. args]);
            return true;
        }

        if (!Catalog.TryGetValue(code, out var copy))
        {
            message = string.Empty;
            return false;
        }

        var template = isPtBr ? copy.PtBr : copy.En;
        message = args.Count == 0
            ? template
            : string.Format(System.Globalization.CultureInfo.InvariantCulture, template, [.. args]);
        return true;
    }

    private static Dictionary<string, (string En, string PtBr)> BuildCatalog()
    {
        var catalog = new Dictionary<string, (string En, string PtBr)>(StringComparer.Ordinal);

        foreach (var group in new[]
                 {
                     AccountAndAccess, StepUpAndApiKeys, Habits, Goals, TagsFactsAndTemplates,
                     Astra, Billing, Streaks, Social, Calendar, Notifications, Media,
                     SyncAndSupport, Profile,
                 })
        {
            foreach (var (code, en, ptBr) in group)
                catalog[code] = (en, ptBr);
        }

        return catalog;
    }
}
