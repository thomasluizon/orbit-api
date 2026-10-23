namespace Orbit.Infrastructure.Services.Prompts;

public static class NotificationVoice
{
    private const string DashAndEmojiRules = """
        - Use no dash character: no em dash, no en dash, and no doubled hyphen. A comma or a full stop does that job.
        - Use no emoji in the reply's own words.
        """;

    public const string CharacterRules = """
        Character bans are absolute in every language:
        - Use no exclamation mark anywhere, in the title or in the body.
        """ + "\n" + DashAndEmojiRules;

    public const string Rules = CharacterRules + "\n" + """
        Voice, and every line here is absolute:
        - Write calmly. Describe what is true. Never sell, never hype, never perform enthusiasm.
        """ + "\n" + FormattingAndRespectRules;

    public const string SlipAlertRules = """
        Character rules in every language:
        - At most one exclamation mark across the title and body, only for natural encouragement.
        """ + "\n" + DashAndEmojiRules + "\n" + """
        Voice:
        - Write warmly and encourage the person's positive choice directly. Never sell or hype; avoid slogans and exaggerated praise.
        """ + "\n" + FormattingAndRespectRules;

    private const string FormattingAndRespectRules = """
        - Use no markdown, no bullet, no heading, no greeting, no sign-off, and no quotation marks around the message.
        - Never shame, blame, scold or warn. A missed day is ordinary and is never a failure.
        - Address the reader directly as you. Never call them the user.
        - Use plain verbs. Never use these words or their translations: unlock, elevate, empower, supercharge, leverage, harness, delve, seamless, robust, crucial, essential, vital, revolutionary, maximize, optimize.
        - Never frame anything as a journey, a mission, a transformation or a battle.
        - Vary the shape of the sentence between messages, and never buy that variety by breaking a rule above.
        """;
}
