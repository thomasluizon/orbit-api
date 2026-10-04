namespace Orbit.Application.Common;

public static class ValidationCopyKeys
{
    public const string EmailRequired = "field.EmailRequired";
    public const string EmailFormat = "field.EmailFormat";
    public const string HabitTitleRequired = "field.HabitTitleRequired";
    public const string TagNameRequired = "field.TagNameRequired";
    public const string TagColorRequired = "field.TagColorRequired";
    public const string GoalTitleRequired = "field.GoalTitleRequired";
    public const string GoalUnitRequired = "field.GoalUnitRequired";
    public const string GoalTargetPositive = "field.GoalTargetPositive";
    public const string GoalProgressNonnegative = "field.GoalProgressNonnegative";
    public const string FrequencyPositive = "field.FrequencyPositive";
    public const string NameRequired = "field.NameRequired";
    public const string SupportSubjectRequired = "field.SupportSubjectRequired";
    public const string SupportMessageRequired = "field.SupportMessageRequired";
    public const string ReferralCodeRequired = "field.ReferralCodeRequired";
    public const string TemplateNameRequired = "field.TemplateNameRequired";
    public const string HabitTitleLength = "field.HabitTitleLength";
    public const string HabitDescriptionLength = "field.HabitDescriptionLength";
    public const string HabitEmojiLength = "field.HabitEmojiLength";
    public const string SubHabitDescriptionLength = "field.SubHabitDescriptionLength";
    public const string SubHabitEmojiLength = "field.SubHabitEmojiLength";
    public const string TagNameLength = "field.TagNameLength";
    public const string GoalTitleLength = "field.GoalTitleLength";
    public const string GoalDescriptionLength = "field.GoalDescriptionLength";
    public const string GoalUnitLength = "field.GoalUnitLength";
    public const string GoalProgressNoteLength = "field.GoalProgressNoteLength";
    public const string SupportSubjectLength = "field.SupportSubjectLength";
    public const string SupportMessageLength = "field.SupportMessageLength";
    public const string ReferralCodeLength = "field.ReferralCodeLength";
    public const string TemplateNameLength = "field.TemplateNameLength";
}

public static partial class ErrorCopy
{
    private static (string Code, string En, string PtBr)[] ValidationFields =>
    [
        (ValidationCopyKeys.EmailRequired, "Enter your email", "Digite seu email"),
        (ValidationCopyKeys.EmailFormat, "Use a complete email, like name@example.com", "Use um email completo, como nome@exemplo.com"),
        (ValidationCopyKeys.HabitTitleRequired, "Give the habit a title", "Dê um título ao hábito"),
        (ValidationCopyKeys.TagNameRequired, "Give the tag a name", "Dê um nome à tag"),
        (ValidationCopyKeys.TagColorRequired, "Choose a color from the swatches", "Escolha uma cor da paleta"),
        (ValidationCopyKeys.GoalTitleRequired, "Give the goal a title", "Dê um título à meta"),
        (ValidationCopyKeys.GoalUnitRequired, "Set the unit", "Defina a unidade"),
        (ValidationCopyKeys.GoalTargetPositive, "Set the target to a value greater than {ComparisonValue}", "Defina a quantidade da meta como um valor maior que {ComparisonValue}"),
        (ValidationCopyKeys.GoalProgressNonnegative, "Enter a value of {ComparisonValue} or higher", "Informe um valor igual ou maior que {ComparisonValue}"),
        (ValidationCopyKeys.FrequencyPositive, "Set the frequency to 1 or higher", "Defina a frequência como 1 ou mais"),
        (ValidationCopyKeys.NameRequired, "Enter a name.", "Digite um nome."),
        (ValidationCopyKeys.SupportSubjectRequired, "Pick a subject so we can route it properly.", "Escolha um assunto para a gente encaminhar certo."),
        (ValidationCopyKeys.SupportMessageRequired, "Write at least one sentence about what happened.", "Escreva pelo menos uma frase sobre o que aconteceu."),
        (ValidationCopyKeys.ReferralCodeRequired, "Enter the referral code", "Digite o código de indicação"),
        (ValidationCopyKeys.TemplateNameRequired, "Give the checklist template a name", "Dê um nome ao modelo de checklist"),
        (ValidationCopyKeys.HabitTitleLength, "Shorten the habit title to {MaxLength} characters or fewer. You entered {TotalLength}.", "Encurte o título do hábito para até {MaxLength} caracteres. Você digitou {TotalLength}."),
        (ValidationCopyKeys.HabitDescriptionLength, "Shorten the habit description to {MaxLength} characters or fewer. You entered {TotalLength}.", "Encurte a descrição do hábito para até {MaxLength} caracteres. Você digitou {TotalLength}."),
        (ValidationCopyKeys.HabitEmojiLength, "Shorten the habit emoji to {MaxLength} characters or fewer. You entered {TotalLength}.", "Encurte o emoji do hábito para até {MaxLength} caracteres. Você digitou {TotalLength}."),
        (ValidationCopyKeys.SubHabitDescriptionLength, "Shorten the sub-habit description to {MaxLength} characters or fewer. You entered {TotalLength}.", "Encurte a descrição do sub-hábito para até {MaxLength} caracteres. Você digitou {TotalLength}."),
        (ValidationCopyKeys.SubHabitEmojiLength, "Shorten the sub-habit emoji to {MaxLength} characters or fewer. You entered {TotalLength}.", "Encurte o emoji do sub-hábito para até {MaxLength} caracteres. Você digitou {TotalLength}."),
        (ValidationCopyKeys.TagNameLength, "Shorten the tag name to {MaxLength} characters or fewer. You entered {TotalLength}.", "Encurte o nome da tag para até {MaxLength} caracteres. Você digitou {TotalLength}."),
        (ValidationCopyKeys.GoalTitleLength, "Shorten the goal title to {MaxLength} characters or fewer. You entered {TotalLength}.", "Encurte o título da meta para até {MaxLength} caracteres. Você digitou {TotalLength}."),
        (ValidationCopyKeys.GoalDescriptionLength, "Shorten the goal description to {MaxLength} characters or fewer. You entered {TotalLength}.", "Encurte a descrição da meta para até {MaxLength} caracteres. Você digitou {TotalLength}."),
        (ValidationCopyKeys.GoalUnitLength, "Shorten the goal unit to {MaxLength} characters or fewer. You entered {TotalLength}.", "Encurte a unidade da meta para até {MaxLength} caracteres. Você digitou {TotalLength}."),
        (ValidationCopyKeys.GoalProgressNoteLength, "Shorten the progress note to {MaxLength} characters or fewer. You entered {TotalLength}.", "Encurte a nota de progresso para até {MaxLength} caracteres. Você digitou {TotalLength}."),
        (ValidationCopyKeys.SupportSubjectLength, "Shorten the subject to {MaxLength} characters or fewer. You entered {TotalLength}.", "Encurte o assunto para até {MaxLength} caracteres. Você digitou {TotalLength}."),
        (ValidationCopyKeys.SupportMessageLength, "Shorten your support message to {MaxLength} characters or fewer. You entered {TotalLength}.", "Encurte sua mensagem de suporte para até {MaxLength} caracteres. Você digitou {TotalLength}."),
        (ValidationCopyKeys.ReferralCodeLength, "Shorten the referral code to {MaxLength} characters or fewer. You entered {TotalLength}.", "Encurte o código de indicação para até {MaxLength} caracteres. Você digitou {TotalLength}."),
        (ValidationCopyKeys.TemplateNameLength, "Shorten the checklist template name to {MaxLength} characters or fewer. You entered {TotalLength}.", "Encurte o nome do modelo de checklist para até {MaxLength} caracteres. Você digitou {TotalLength}."),
    ];
}
