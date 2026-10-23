using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DropRetiredFeatureTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountabilityCheckIns");

            migrationBuilder.DropTable(
                name: "AccountabilityPairHabits");

            migrationBuilder.DropTable(
                name: "AiFactExtractionBatches");

            migrationBuilder.DropTable(
                name: "BlockedUsers");

            migrationBuilder.DropTable(
                name: "ChallengeParticipantHabits");

            migrationBuilder.DropTable(
                name: "FriendFeedEvents");

            migrationBuilder.DropTable(
                name: "Friendships");

            migrationBuilder.DropTable(
                name: "Reports");

            migrationBuilder.DropTable(
                name: "UserFacts");

            migrationBuilder.DropTable(
                name: "AccountabilityPairs");

            migrationBuilder.DropTable(
                name: "ChallengeParticipants");

            migrationBuilder.DropTable(
                name: "Cheers");

            migrationBuilder.DropTable(
                name: "Challenges");

            migrationBuilder.DeleteData(
                table: "AppConfigs",
                keyColumn: "Key",
                keyValue: "MaxUserFacts");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccountabilityPairs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AcceptedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AddresseeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Cadence = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RequesterId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountabilityPairs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccountabilityPairs_Users_AddresseeId",
                        column: x => x.AddresseeId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AccountabilityPairs_Users_RequesterId",
                        column: x => x.RequesterId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AiFactExtractionBatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BatchId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    InputFileId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    OutputFileId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiFactExtractionBatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiFactExtractionBatches_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BlockedUsers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BlockedId = table.Column<Guid>(type: "uuid", nullable: false),
                    BlockerId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlockedUsers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BlockedUsers_Users_BlockedId",
                        column: x => x.BlockedId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BlockedUsers_Users_BlockerId",
                        column: x => x.BlockerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Challenges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatorId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    JoinCode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PeriodEndUtc = table.Column<DateOnly>(type: "date", nullable: true),
                    PeriodStartUtc = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TargetCount = table.Column<int>(type: "integer", nullable: true),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Challenges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Challenges_Users_CreatorId",
                        column: x => x.CreatorId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Cheers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    HabitId = table.Column<Guid>(type: "uuid", nullable: true),
                    Note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RecipientId = table.Column<Guid>(type: "uuid", nullable: false),
                    SenderId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Cheers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Cheers_Habits_HabitId",
                        column: x => x.HabitId,
                        principalTable: "Habits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Cheers_Users_RecipientId",
                        column: x => x.RecipientId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Cheers_Users_SenderId",
                        column: x => x.SenderId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FriendFeedEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AchievementId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Value = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FriendFeedEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FriendFeedEvents_Users_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Friendships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AddresseeId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RequesterId = table.Column<Guid>(type: "uuid", nullable: false),
                    RespondedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Friendships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Friendships_Users_AddresseeId",
                        column: x => x.AddresseeId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Friendships_Users_RequesterId",
                        column: x => x.RequesterId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserFacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Category = table.Column<string>(type: "text", nullable: true),
                    DeletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExtractedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FactText = table.Column<string>(type: "text", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserFacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserFacts_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AccountabilityCheckIns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PairId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountabilityCheckIns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccountabilityCheckIns_AccountabilityPairs_PairId",
                        column: x => x.PairId,
                        principalTable: "AccountabilityPairs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AccountabilityPairHabits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    HabitId = table.Column<Guid>(type: "uuid", nullable: false),
                    PairId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountabilityPairHabits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccountabilityPairHabits_AccountabilityPairs_PairId",
                        column: x => x.PairId,
                        principalTable: "AccountabilityPairs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AccountabilityPairHabits_Habits_HabitId",
                        column: x => x.HabitId,
                        principalTable: "Habits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ChallengeParticipants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChallengeId = table.Column<Guid>(type: "uuid", nullable: false),
                    JoinedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LeftAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChallengeParticipants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChallengeParticipants_Challenges_ChallengeId",
                        column: x => x.ChallengeId,
                        principalTable: "Challenges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChallengeParticipants_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Reports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CheerId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Details = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ReportedUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReporterId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReviewedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Reports_Cheers_CheerId",
                        column: x => x.CheerId,
                        principalTable: "Cheers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Reports_Users_ReportedUserId",
                        column: x => x.ReportedUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Reports_Users_ReporterId",
                        column: x => x.ReporterId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ChallengeParticipantHabits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChallengeParticipantId = table.Column<Guid>(type: "uuid", nullable: false),
                    HabitId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChallengeParticipantHabits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChallengeParticipantHabits_ChallengeParticipants_ChallengeP~",
                        column: x => x.ChallengeParticipantId,
                        principalTable: "ChallengeParticipants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChallengeParticipantHabits_Habits_HabitId",
                        column: x => x.HabitId,
                        principalTable: "Habits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "AppConfigs",
                columns: new[] { "Key", "Description", "Value" },
                values: new object[] { "MaxUserFacts", "Maximum number of facts the AI can remember per user", "50" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountabilityCheckIns_PairId_CreatedAtUtc",
                table: "AccountabilityCheckIns",
                columns: new[] { "PairId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountabilityCheckIns_PairId_UserId_Date",
                table: "AccountabilityCheckIns",
                columns: new[] { "PairId", "UserId", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccountabilityPairHabits_HabitId",
                table: "AccountabilityPairHabits",
                column: "HabitId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountabilityPairHabits_PairId_UserId_HabitId",
                table: "AccountabilityPairHabits",
                columns: new[] { "PairId", "UserId", "HabitId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccountabilityPairs_AddresseeId",
                table: "AccountabilityPairs",
                column: "AddresseeId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountabilityPairs_RequesterId",
                table: "AccountabilityPairs",
                column: "RequesterId");

            migrationBuilder.CreateIndex(
                name: "IX_AiFactExtractionBatches_BatchId",
                table: "AiFactExtractionBatches",
                column: "BatchId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AiFactExtractionBatches_Status",
                table: "AiFactExtractionBatches",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_AiFactExtractionBatches_UserId",
                table: "AiFactExtractionBatches",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_BlockedUsers_BlockedId",
                table: "BlockedUsers",
                column: "BlockedId");

            migrationBuilder.CreateIndex(
                name: "IX_BlockedUsers_BlockerId_BlockedId",
                table: "BlockedUsers",
                columns: new[] { "BlockerId", "BlockedId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChallengeParticipantHabits_ChallengeParticipantId_HabitId",
                table: "ChallengeParticipantHabits",
                columns: new[] { "ChallengeParticipantId", "HabitId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChallengeParticipantHabits_HabitId",
                table: "ChallengeParticipantHabits",
                column: "HabitId");

            migrationBuilder.CreateIndex(
                name: "IX_ChallengeParticipants_ChallengeId_UserId",
                table: "ChallengeParticipants",
                columns: new[] { "ChallengeId", "UserId" },
                unique: true,
                filter: "\"LeftAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ChallengeParticipants_UserId",
                table: "ChallengeParticipants",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Challenges_CreatorId",
                table: "Challenges",
                column: "CreatorId");

            migrationBuilder.CreateIndex(
                name: "IX_Challenges_JoinCode",
                table: "Challenges",
                column: "JoinCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Cheers_HabitId",
                table: "Cheers",
                column: "HabitId");

            migrationBuilder.CreateIndex(
                name: "IX_Cheers_RecipientId",
                table: "Cheers",
                column: "RecipientId");

            migrationBuilder.CreateIndex(
                name: "IX_Cheers_SenderId_CreatedAtUtc",
                table: "Cheers",
                columns: new[] { "SenderId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_FriendFeedEvents_ActorUserId_AchievementId",
                table: "FriendFeedEvents",
                columns: new[] { "ActorUserId", "AchievementId" },
                unique: true,
                filter: "\"AchievementId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_FriendFeedEvents_ActorUserId_CreatedAtUtc_Id",
                table: "FriendFeedEvents",
                columns: new[] { "ActorUserId", "CreatedAtUtc", "Id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_FriendFeedEvents_ActorUserId_Type_Value",
                table: "FriendFeedEvents",
                columns: new[] { "ActorUserId", "Type", "Value" },
                unique: true,
                filter: "\"AchievementId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Friendships_AddresseeId",
                table: "Friendships",
                column: "AddresseeId");

            migrationBuilder.CreateIndex(
                name: "IX_Friendships_RequesterId",
                table: "Friendships",
                column: "RequesterId");

            migrationBuilder.CreateIndex(
                name: "IX_Reports_CheerId",
                table: "Reports",
                column: "CheerId");

            migrationBuilder.CreateIndex(
                name: "IX_Reports_ReportedUserId",
                table: "Reports",
                column: "ReportedUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Reports_ReporterId",
                table: "Reports",
                column: "ReporterId");

            migrationBuilder.CreateIndex(
                name: "IX_Reports_Status",
                table: "Reports",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_UserFacts_UserId_IsDeleted",
                table: "UserFacts",
                columns: new[] { "UserId", "IsDeleted" });
        }
    }
}
