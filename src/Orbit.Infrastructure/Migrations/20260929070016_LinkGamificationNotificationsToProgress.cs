using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class LinkGamificationNotificationsToProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE TABLE "__LinkGamificationNotificationsToProgress" (
                    "Id" uuid PRIMARY KEY,
                    "Title" text NOT NULL,
                    "Body" text NOT NULL
                );

                DO $$
                DECLARE
                    batch_count integer;
                    total_count integer := 0;
                BEGIN
                    LOOP
                        WITH candidates AS (
                            SELECT "Id", "Title", "Body"
                            FROM "Notifications"
                            WHERE "Url" IS NULL
                              AND (
                                  "Title" LIKE 'Conquista Desbloqueada: %'
                                  OR "Title" LIKE 'Achievement Unlocked: %'
                                  OR "Title" LIKE 'Subiu de nível! Agora você está no nível %'
                                  OR "Title" LIKE 'Level Up! You''re now Level %'
                              )
                            ORDER BY "Id"
                            LIMIT 1000
                            FOR UPDATE
                        ), archived AS (
                            INSERT INTO "__LinkGamificationNotificationsToProgress" ("Id", "Title", "Body")
                            SELECT "Id", "Title", "Body" FROM candidates
                            RETURNING "Id"
                        )
                        UPDATE "Notifications" AS notification
                        SET "Url" = '/progress',
                            "Title" = CASE
                                WHEN notification."Title" LIKE 'Conquista Desbloqueada: %' THEN
                                    'Nova conquista: ' || substring(notification."Title" FROM length('Conquista Desbloqueada: ') + 1)
                                WHEN notification."Title" LIKE 'Achievement Unlocked: %' THEN
                                    'New achievement: ' || substring(notification."Title" FROM length('Achievement Unlocked: ') + 1)
                                WHEN notification."Title" LIKE 'Subiu de nível! Agora você está no nível %' THEN
                                    'Você chegou ao nível ' || substring(notification."Title" FROM length('Subiu de nível! Agora você está no nível ') + 1)
                                ELSE
                                    'You reached level ' || substring(notification."Title" FROM length('Level Up! You''re now Level ') + 1)
                            END,
                            "Body" = CASE
                                WHEN notification."Title" LIKE 'Subiu de nível! Agora você está no nível %' THEN
                                    'O nível ' || substring(notification."Title" FROM length('Subiu de nível! Agora você está no nível ') + 1)
                                    || ' se chama ' || regexp_replace(notification."Body", '^Você alcançou (.*)! Continue assim!$', '\1') || '.'
                                WHEN notification."Title" LIKE 'Level Up! You''re now Level %' THEN
                                    'Level ' || substring(notification."Title" FROM length('Level Up! You''re now Level ') + 1)
                                    || ' is called ' || regexp_replace(notification."Body", '^You''ve reached (.*)! Keep going!$', '\1') || '.'
                                ELSE notification."Body"
                            END
                        FROM archived
                        WHERE notification."Id" = archived."Id";

                        GET DIAGNOSTICS batch_count = ROW_COUNT;
                        total_count := total_count + batch_count;
                        EXIT WHEN batch_count = 0;
                    END LOOP;

                    RAISE NOTICE 'Linked % gamification notifications to progress', total_count;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                    batch_count integer;
                BEGIN
                    LOOP
                        WITH restored AS (
                            DELETE FROM "__LinkGamificationNotificationsToProgress"
                            WHERE "Id" IN (
                                SELECT "Id" FROM "__LinkGamificationNotificationsToProgress"
                                ORDER BY "Id"
                                LIMIT 1000
                            )
                            RETURNING "Id", "Title", "Body"
                        ), updated AS (
                            UPDATE "Notifications" AS notification
                            SET "Url" = NULL,
                                "Title" = restored."Title",
                                "Body" = restored."Body"
                            FROM restored
                            WHERE notification."Id" = restored."Id"
                            RETURNING notification."Id"
                        )
                        SELECT count(*) INTO batch_count FROM restored;

                        EXIT WHEN batch_count = 0;
                    END LOOP;
                END $$;

                DROP TABLE "__LinkGamificationNotificationsToProgress";
                """);
        }
    }
}
