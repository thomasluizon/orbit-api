using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UnmapLegacyHabitScheduleColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // https://github.com/thomasluizon/orbit-tickets/issues/227: retain physical columns for overlapping instances; only Schedule and NextDueDate own their values.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION sync_habit_schedule_expand()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    NEW."IsGeneral" := NEW."Schedule" = 3;
                    NEW."IsFlexible" := NEW."Schedule" = 2;
                    NEW."DueDate" := NEW."NextDueDate";
                    IF (NEW."IsGeneral" AND (NEW."FrequencyUnit" IS NOT NULL OR NEW."FrequencyQuantity" IS NOT NULL))
                       OR (NEW."IsGeneral" AND NEW."IsBadHabit")
                       OR (NEW."IsFlexible" AND NEW."FrequencyUnit" IS NULL)
                       OR (NEW."IsFlexible" AND cardinality(NEW."Days") > 0) THEN
                        RAISE EXCEPTION 'Invalid legacy habit schedule';
                    END IF;
                    IF TG_OP = 'INSERT' THEN
                        IF NEW."StartDate" IS NULL OR NEW."StartDate" = DATE '0001-01-01' THEN
                            NEW."StartDate" := NEW."DueDate";
                        END IF;
                    ELSIF NEW."StartDate" IS NULL OR NEW."StartDate" = DATE '0001-01-01' THEN
                        NEW."StartDate" := OLD."StartDate";
                    END IF;
                    RETURN NEW;
                END;
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION sync_habit_schedule_expand()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF (NEW."IsGeneral" AND (NEW."FrequencyUnit" IS NOT NULL OR NEW."FrequencyQuantity" IS NOT NULL))
                       OR (NEW."IsGeneral" AND NEW."IsBadHabit")
                       OR (NEW."IsFlexible" AND NEW."FrequencyUnit" IS NULL)
                       OR (NEW."IsFlexible" AND cardinality(NEW."Days") > 0) THEN
                        RAISE EXCEPTION 'Invalid legacy habit schedule';
                    END IF;
                    NEW."Schedule" := CASE
                        WHEN NEW."IsGeneral" THEN 3
                        WHEN NEW."IsFlexible" THEN 2
                        WHEN NEW."FrequencyUnit" IS NULL THEN 0
                        ELSE 1
                    END;
                    IF TG_OP = 'INSERT' THEN
                        IF NEW."StartDate" IS NULL OR NEW."StartDate" = DATE '0001-01-01' THEN
                            NEW."StartDate" := NEW."DueDate";
                        END IF;
                    ELSIF NEW."StartDate" IS NULL OR NEW."StartDate" = DATE '0001-01-01' THEN
                        NEW."StartDate" := OLD."StartDate";
                    END IF;
                    RETURN NEW;
                END;
                $$;
                """);
        }
    }
}
