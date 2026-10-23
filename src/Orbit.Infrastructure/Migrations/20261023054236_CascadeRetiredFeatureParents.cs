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

            migrationBuilder.AddForeignKey(
                name: "FK_BlockedUsers_Users_BlockedId",
                table: "BlockedUsers",
                column: "BlockedId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_BlockedUsers_Users_BlockerId",
                table: "BlockedUsers",
                column: "BlockerId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ChallengeParticipants_Users_UserId",
                table: "ChallengeParticipants",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Challenges_Users_CreatorId",
                table: "Challenges",
                column: "CreatorId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Cheers_Habits_HabitId",
                table: "Cheers",
                column: "HabitId",
                principalTable: "Habits",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Cheers_Users_RecipientId",
                table: "Cheers",
                column: "RecipientId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Cheers_Users_SenderId",
                table: "Cheers",
                column: "SenderId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_FriendFeedEvents_Users_ActorUserId",
                table: "FriendFeedEvents",
                column: "ActorUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Friendships_Users_AddresseeId",
                table: "Friendships",
                column: "AddresseeId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Friendships_Users_RequesterId",
                table: "Friendships",
                column: "RequesterId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Reports_Users_ReportedUserId",
                table: "Reports",
                column: "ReportedUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Reports_Users_ReporterId",
                table: "Reports",
                column: "ReporterId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_UserFacts_Users_UserId",
                table: "UserFacts",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
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

            migrationBuilder.AddForeignKey(
                name: "FK_BlockedUsers_Users_BlockedId",
                table: "BlockedUsers",
                column: "BlockedId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_BlockedUsers_Users_BlockerId",
                table: "BlockedUsers",
                column: "BlockerId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ChallengeParticipants_Users_UserId",
                table: "ChallengeParticipants",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Challenges_Users_CreatorId",
                table: "Challenges",
                column: "CreatorId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Cheers_Habits_HabitId",
                table: "Cheers",
                column: "HabitId",
                principalTable: "Habits",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Cheers_Users_RecipientId",
                table: "Cheers",
                column: "RecipientId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Cheers_Users_SenderId",
                table: "Cheers",
                column: "SenderId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_FriendFeedEvents_Users_ActorUserId",
                table: "FriendFeedEvents",
                column: "ActorUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Friendships_Users_AddresseeId",
                table: "Friendships",
                column: "AddresseeId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Friendships_Users_RequesterId",
                table: "Friendships",
                column: "RequesterId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Reports_Users_ReportedUserId",
                table: "Reports",
                column: "ReportedUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Reports_Users_ReporterId",
                table: "Reports",
                column: "ReporterId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
