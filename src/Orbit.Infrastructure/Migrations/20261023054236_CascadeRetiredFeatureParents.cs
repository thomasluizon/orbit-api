using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CascadeRetiredFeatureParents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BlockedUsers_Users_BlockedId",
                table: "BlockedUsers");

            migrationBuilder.DropForeignKey(
                name: "FK_BlockedUsers_Users_BlockerId",
                table: "BlockedUsers");

            migrationBuilder.DropForeignKey(
                name: "FK_ChallengeParticipants_Users_UserId",
                table: "ChallengeParticipants");

            migrationBuilder.DropForeignKey(
                name: "FK_Challenges_Users_CreatorId",
                table: "Challenges");

            migrationBuilder.DropForeignKey(
                name: "FK_Cheers_Habits_HabitId",
                table: "Cheers");

            migrationBuilder.DropForeignKey(
                name: "FK_Cheers_Users_RecipientId",
                table: "Cheers");

            migrationBuilder.DropForeignKey(
                name: "FK_Cheers_Users_SenderId",
                table: "Cheers");

            migrationBuilder.DropForeignKey(
                name: "FK_FriendFeedEvents_Users_ActorUserId",
                table: "FriendFeedEvents");

            migrationBuilder.DropForeignKey(
                name: "FK_Friendships_Users_AddresseeId",
                table: "Friendships");

            migrationBuilder.DropForeignKey(
                name: "FK_Friendships_Users_RequesterId",
                table: "Friendships");

            migrationBuilder.DropForeignKey(
                name: "FK_Reports_Users_ReportedUserId",
                table: "Reports");

            migrationBuilder.DropForeignKey(
                name: "FK_Reports_Users_ReporterId",
                table: "Reports");

            migrationBuilder.Sql(
                """
                ALTER TABLE "BlockedUsers" ADD CONSTRAINT "FK_BlockedUsers_Users_BlockedId" FOREIGN KEY ("BlockedId") REFERENCES "Users" ("Id") ON DELETE CASCADE NOT VALID;
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE "BlockedUsers" ADD CONSTRAINT "FK_BlockedUsers_Users_BlockerId" FOREIGN KEY ("BlockerId") REFERENCES "Users" ("Id") ON DELETE CASCADE NOT VALID;
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE "ChallengeParticipants" ADD CONSTRAINT "FK_ChallengeParticipants_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE NOT VALID;
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE "Challenges" ADD CONSTRAINT "FK_Challenges_Users_CreatorId" FOREIGN KEY ("CreatorId") REFERENCES "Users" ("Id") ON DELETE CASCADE NOT VALID;
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE "Cheers" ADD CONSTRAINT "FK_Cheers_Habits_HabitId" FOREIGN KEY ("HabitId") REFERENCES "Habits" ("Id") ON DELETE CASCADE NOT VALID;
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE "Cheers" ADD CONSTRAINT "FK_Cheers_Users_RecipientId" FOREIGN KEY ("RecipientId") REFERENCES "Users" ("Id") ON DELETE CASCADE NOT VALID;
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE "Cheers" ADD CONSTRAINT "FK_Cheers_Users_SenderId" FOREIGN KEY ("SenderId") REFERENCES "Users" ("Id") ON DELETE CASCADE NOT VALID;
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE "FriendFeedEvents" ADD CONSTRAINT "FK_FriendFeedEvents_Users_ActorUserId" FOREIGN KEY ("ActorUserId") REFERENCES "Users" ("Id") ON DELETE CASCADE NOT VALID;
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE "Friendships" ADD CONSTRAINT "FK_Friendships_Users_AddresseeId" FOREIGN KEY ("AddresseeId") REFERENCES "Users" ("Id") ON DELETE CASCADE NOT VALID;
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE "Friendships" ADD CONSTRAINT "FK_Friendships_Users_RequesterId" FOREIGN KEY ("RequesterId") REFERENCES "Users" ("Id") ON DELETE CASCADE NOT VALID;
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE "Reports" ADD CONSTRAINT "FK_Reports_Users_ReportedUserId" FOREIGN KEY ("ReportedUserId") REFERENCES "Users" ("Id") ON DELETE CASCADE NOT VALID;
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE "Reports" ADD CONSTRAINT "FK_Reports_Users_ReporterId" FOREIGN KEY ("ReporterId") REFERENCES "Users" ("Id") ON DELETE CASCADE NOT VALID;
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE "UserFacts" ADD CONSTRAINT "FK_UserFacts_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE NOT VALID;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BlockedUsers_Users_BlockedId",
                table: "BlockedUsers");

            migrationBuilder.DropForeignKey(
                name: "FK_BlockedUsers_Users_BlockerId",
                table: "BlockedUsers");

            migrationBuilder.DropForeignKey(
                name: "FK_ChallengeParticipants_Users_UserId",
                table: "ChallengeParticipants");

            migrationBuilder.DropForeignKey(
                name: "FK_Challenges_Users_CreatorId",
                table: "Challenges");

            migrationBuilder.DropForeignKey(
                name: "FK_Cheers_Habits_HabitId",
                table: "Cheers");

            migrationBuilder.DropForeignKey(
                name: "FK_Cheers_Users_RecipientId",
                table: "Cheers");

            migrationBuilder.DropForeignKey(
                name: "FK_Cheers_Users_SenderId",
                table: "Cheers");

            migrationBuilder.DropForeignKey(
                name: "FK_FriendFeedEvents_Users_ActorUserId",
                table: "FriendFeedEvents");

            migrationBuilder.DropForeignKey(
                name: "FK_Friendships_Users_AddresseeId",
                table: "Friendships");

            migrationBuilder.DropForeignKey(
                name: "FK_Friendships_Users_RequesterId",
                table: "Friendships");

            migrationBuilder.DropForeignKey(
                name: "FK_Reports_Users_ReportedUserId",
                table: "Reports");

            migrationBuilder.DropForeignKey(
                name: "FK_Reports_Users_ReporterId",
                table: "Reports");

            migrationBuilder.DropForeignKey(
                name: "FK_UserFacts_Users_UserId",
                table: "UserFacts");

            migrationBuilder.Sql(
                """
                ALTER TABLE "BlockedUsers" ADD CONSTRAINT "FK_BlockedUsers_Users_BlockedId" FOREIGN KEY ("BlockedId") REFERENCES "Users" ("Id") ON DELETE RESTRICT NOT VALID;
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE "BlockedUsers" ADD CONSTRAINT "FK_BlockedUsers_Users_BlockerId" FOREIGN KEY ("BlockerId") REFERENCES "Users" ("Id") ON DELETE RESTRICT NOT VALID;
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE "ChallengeParticipants" ADD CONSTRAINT "FK_ChallengeParticipants_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE RESTRICT NOT VALID;
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE "Challenges" ADD CONSTRAINT "FK_Challenges_Users_CreatorId" FOREIGN KEY ("CreatorId") REFERENCES "Users" ("Id") ON DELETE RESTRICT NOT VALID;
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE "Cheers" ADD CONSTRAINT "FK_Cheers_Habits_HabitId" FOREIGN KEY ("HabitId") REFERENCES "Habits" ("Id") ON DELETE SET NULL NOT VALID;
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE "Cheers" ADD CONSTRAINT "FK_Cheers_Users_RecipientId" FOREIGN KEY ("RecipientId") REFERENCES "Users" ("Id") ON DELETE RESTRICT NOT VALID;
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE "Cheers" ADD CONSTRAINT "FK_Cheers_Users_SenderId" FOREIGN KEY ("SenderId") REFERENCES "Users" ("Id") ON DELETE RESTRICT NOT VALID;
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE "FriendFeedEvents" ADD CONSTRAINT "FK_FriendFeedEvents_Users_ActorUserId" FOREIGN KEY ("ActorUserId") REFERENCES "Users" ("Id") ON DELETE RESTRICT NOT VALID;
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE "Friendships" ADD CONSTRAINT "FK_Friendships_Users_AddresseeId" FOREIGN KEY ("AddresseeId") REFERENCES "Users" ("Id") ON DELETE RESTRICT NOT VALID;
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE "Friendships" ADD CONSTRAINT "FK_Friendships_Users_RequesterId" FOREIGN KEY ("RequesterId") REFERENCES "Users" ("Id") ON DELETE RESTRICT NOT VALID;
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE "Reports" ADD CONSTRAINT "FK_Reports_Users_ReportedUserId" FOREIGN KEY ("ReportedUserId") REFERENCES "Users" ("Id") ON DELETE RESTRICT NOT VALID;
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE "Reports" ADD CONSTRAINT "FK_Reports_Users_ReporterId" FOREIGN KEY ("ReporterId") REFERENCES "Users" ("Id") ON DELETE RESTRICT NOT VALID;
                """);
        }
    }
}
