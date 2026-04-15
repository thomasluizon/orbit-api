using System.Text.Json;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.ApiKeys.Commands;
using Orbit.Application.ApiKeys.Queries;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.ChecklistTemplates.Commands;
using Orbit.Application.ChecklistTemplates.Queries;
using Orbit.Application.Gamification.Commands;
using Orbit.Application.Gamification.Queries;
using Orbit.Application.Referrals.Queries;
using Orbit.Application.Profile.Commands;
using Orbit.Application.Subscriptions;
using Orbit.Application.Subscriptions.Commands;
using Orbit.Application.Subscriptions.Queries;
using Orbit.Application.Support.Commands;
using Orbit.Application.UserFacts.Commands;
using Orbit.Application.UserFacts.Queries;
using Orbit.Domain.Common;

namespace Orbit.Application.Tests.Chat.Tools;

public class ChecklistUserFactPlatformToolTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    [Fact]
    public void ToolMetadata_ExposesNamesAndSchemas()
    {
        var mediator = Substitute.For<IMediator>();
        var checklistTemplatesTool = new GetChecklistTemplatesTool(mediator);
        var createChecklistTemplateTool = new CreateChecklistTemplateTool(mediator);
        var deleteChecklistTemplateTool = new DeleteChecklistTemplateTool(mediator);
        var userFactsTool = new GetUserFactsTool(mediator);
        var deleteUserFactsTool = new DeleteUserFactsTool(mediator);
        var gamificationTool = new GetGamificationOverviewTool(mediator);
        var activateStreakFreezeTool = new ActivateStreakFreezeTool(mediator);
        var referralTool = new GetReferralOverviewTool(mediator);
        var subscriptionOverviewTool = new GetSubscriptionOverviewTool(mediator);
        var manageSubscriptionTool = new ManageSubscriptionTool(mediator);
        var apiKeysTool = new GetApiKeysTool(mediator);
        var manageApiKeysTool = new ManageApiKeysTool(mediator);
        var supportTool = new SendSupportRequestTool(mediator);
        var accountTool = new ManageAccountTool(mediator);

        checklistTemplatesTool.Name.Should().Be("get_checklist_templates");
        checklistTemplatesTool.IsReadOnly.Should().BeTrue();
        JsonSerializer.Serialize(checklistTemplatesTool.GetParameterSchema()).Should().Contain("properties");

        createChecklistTemplateTool.Name.Should().Be("create_checklist_template");
        JsonSerializer.Serialize(createChecklistTemplateTool.GetParameterSchema()).Should().Contain("items");

        deleteChecklistTemplateTool.Name.Should().Be("delete_checklist_template");
        JsonSerializer.Serialize(deleteChecklistTemplateTool.GetParameterSchema()).Should().Contain("template_id");

        userFactsTool.Name.Should().Be("get_user_facts");
        userFactsTool.IsReadOnly.Should().BeTrue();
        JsonSerializer.Serialize(userFactsTool.GetParameterSchema()).Should().Contain("properties");

        deleteUserFactsTool.Name.Should().Be("delete_user_facts");
        JsonSerializer.Serialize(deleteUserFactsTool.GetParameterSchema()).Should().Contain("fact_ids");

        gamificationTool.Name.Should().Be("get_gamification_overview");
        gamificationTool.IsReadOnly.Should().BeTrue();
        JsonSerializer.Serialize(gamificationTool.GetParameterSchema()).Should().Contain("include_achievements");

        activateStreakFreezeTool.Name.Should().Be("activate_streak_freeze");
        JsonSerializer.Serialize(activateStreakFreezeTool.GetParameterSchema()).Should().Contain("properties");

        referralTool.Name.Should().Be("get_referral_overview");
        referralTool.IsReadOnly.Should().BeTrue();

        subscriptionOverviewTool.Name.Should().Be("get_subscription_overview");
        subscriptionOverviewTool.IsReadOnly.Should().BeTrue();
        JsonSerializer.Serialize(subscriptionOverviewTool.GetParameterSchema()).Should().Contain("include_plans");

        manageSubscriptionTool.Name.Should().Be("manage_subscription");
        JsonSerializer.Serialize(manageSubscriptionTool.GetParameterSchema()).Should().Contain("create_portal");

        apiKeysTool.Name.Should().Be("get_api_keys");
        apiKeysTool.IsReadOnly.Should().BeTrue();

        manageApiKeysTool.Name.Should().Be("manage_api_keys");
        JsonSerializer.Serialize(manageApiKeysTool.GetParameterSchema()).Should().Contain("expires_at_utc");

        supportTool.Name.Should().Be("send_support_request");
        JsonSerializer.Serialize(supportTool.GetParameterSchema()).Should().Contain("message");

        accountTool.Name.Should().Be("manage_account");
        JsonSerializer.Serialize(accountTool.GetParameterSchema()).Should().Contain("confirm_deletion");
    }

    [Fact]
    public async Task GetChecklistTemplatesTool_ReturnsSuccess()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetChecklistTemplatesQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<ChecklistTemplateResponse>>([]));
        var tool = new GetChecklistTemplatesTool(mediator);

        var result = await tool.ExecuteAsync(Parse("{}"), UserId, CancellationToken.None);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task GetChecklistTemplatesTool_ReturnsFailure()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetChecklistTemplatesQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<IReadOnlyList<ChecklistTemplateResponse>>("templates_failed"));
        var tool = new GetChecklistTemplatesTool(mediator);

        var result = await tool.ExecuteAsync(Parse("{}"), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("templates_failed");
    }

    [Fact]
    public async Task CreateChecklistTemplateTool_RequiresNameAndItems()
    {
        var tool = new CreateChecklistTemplateTool(Substitute.For<IMediator>());

        var result = await tool.ExecuteAsync(Parse("""{"name":"Morning"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("name and at least one item are required.");
    }

    [Fact]
    public async Task CreateChecklistTemplateTool_ReturnsCreatedTemplate()
    {
        var templateId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<CreateChecklistTemplateCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(templateId));
        var tool = new CreateChecklistTemplateTool(mediator);

        var result = await tool.ExecuteAsync(Parse("""{"name":"Morning","items":["Water","Read"]}"""), UserId, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.EntityId.Should().Be(templateId.ToString());
        result.EntityName.Should().Be("Morning");
    }

    [Fact]
    public async Task CreateChecklistTemplateTool_PropagatesFailure()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<CreateChecklistTemplateCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<Guid>("create_failed"));
        var tool = new CreateChecklistTemplateTool(mediator);

        var result = await tool.ExecuteAsync(Parse("""{"name":"Morning","items":["Water"]}"""), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("create_failed");
    }

    [Fact]
    public async Task DeleteChecklistTemplateTool_RejectsInvalidGuid()
    {
        var tool = new DeleteChecklistTemplateTool(Substitute.For<IMediator>());

        var result = await tool.ExecuteAsync(Parse("""{"template_id":"bad"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("template_id must be a valid GUID.");
    }

    [Fact]
    public async Task DeleteChecklistTemplateTool_ReturnsFailure()
    {
        var templateId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<DeleteChecklistTemplateCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("delete_failed"));
        var tool = new DeleteChecklistTemplateTool(mediator);

        var result = await tool.ExecuteAsync(Parse($$"""{"template_id":"{{templateId}}"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("delete_failed");
    }

    [Fact]
    public async Task DeleteChecklistTemplateTool_ReturnsSuccess()
    {
        var templateId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<DeleteChecklistTemplateCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        var tool = new DeleteChecklistTemplateTool(mediator);

        var result = await tool.ExecuteAsync(Parse($$"""{"template_id":"{{templateId}}"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.EntityId.Should().Be(templateId.ToString());
        result.EntityName.Should().Be("Deleted checklist template");
    }

    [Fact]
    public async Task GetUserFactsTool_ReturnsSuccess()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetUserFactsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<UserFactDto>>([]));
        var tool = new GetUserFactsTool(mediator);

        var result = await tool.ExecuteAsync(Parse("{}"), UserId, CancellationToken.None);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task GetUserFactsTool_ReturnsFailure()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetUserFactsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<IReadOnlyList<UserFactDto>>("facts_failed"));
        var tool = new GetUserFactsTool(mediator);

        var result = await tool.ExecuteAsync(Parse("{}"), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("facts_failed");
    }

    [Fact]
    public async Task DeleteUserFactsTool_RequiresFactIdOrFactIds()
    {
        var tool = new DeleteUserFactsTool(Substitute.For<IMediator>());

        var result = await tool.ExecuteAsync(Parse("{}"), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("fact_id or fact_ids is required.");
    }

    [Fact]
    public async Task DeleteUserFactsTool_DeletesMultipleFacts()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<BulkDeleteUserFactsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(2));
        var tool = new DeleteUserFactsTool(mediator);
        var idOne = Guid.NewGuid();
        var idTwo = Guid.NewGuid();

        var result = await tool.ExecuteAsync(
            Parse($$"""{"fact_ids":["{{idOne}}","{{idTwo}}"]}"""),
            UserId,
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Deleted user facts");
        result.EntityId.Should().Be(UserId.ToString());
    }

    [Fact]
    public async Task DeleteUserFactsTool_PropagatesBulkFailure()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<BulkDeleteUserFactsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<int>("bulk_failed"));
        var tool = new DeleteUserFactsTool(mediator);
        var factId = Guid.NewGuid();

        var result = await tool.ExecuteAsync(
            Parse($$"""{"fact_ids":["{{factId}}"]}"""),
            UserId,
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("bulk_failed");
    }

    [Fact]
    public async Task DeleteUserFactsTool_DeletesSingleFact()
    {
        var factId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<DeleteUserFactCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        var tool = new DeleteUserFactsTool(mediator);

        var result = await tool.ExecuteAsync(Parse($$"""{"fact_id":"{{factId}}"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.EntityId.Should().Be(factId.ToString());
        result.EntityName.Should().Be("Deleted user fact");
    }

    [Fact]
    public async Task DeleteUserFactsTool_PropagatesSingleDeleteFailure()
    {
        var factId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<DeleteUserFactCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("single_failed"));
        var tool = new DeleteUserFactsTool(mediator);

        var result = await tool.ExecuteAsync(Parse($$"""{"fact_id":"{{factId}}"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("single_failed");
    }

    [Fact]
    public async Task GetGamificationOverviewTool_SkipsQueriesWhenAllFlagsAreFalse()
    {
        var mediator = Substitute.For<IMediator>();
        var tool = new GetGamificationOverviewTool(mediator);

        var result = await tool.ExecuteAsync(
            Parse("""{"include_profile":false,"include_achievements":false,"include_streak":false}"""),
            UserId,
            CancellationToken.None);

        result.Success.Should().BeTrue();
        await mediator.DidNotReceiveWithAnyArgs().Send(default!, default);
    }

    [Fact]
    public async Task GetGamificationOverviewTool_ReturnsFailureWhenProfileFails()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetGamificationProfileQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<GamificationProfileResponse>("profile_failed"));
        var tool = new GetGamificationOverviewTool(mediator);

        var result = await tool.ExecuteAsync(Parse("{}"), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("profile_failed");
    }

    [Fact]
    public async Task GetGamificationOverviewTool_ReturnsSuccessForAllSections()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetGamificationProfileQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new GamificationProfileResponse(
                100,
                2,
                "Climber",
                50,
                100,
                25,
                1,
                10,
                [],
                [],
                7,
                10,
                new DateOnly(2026, 4, 14))));
        mediator.Send(Arg.Any<GetAchievementsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AchievementsResponse([])));
        mediator.Send(Arg.Any<GetStreakInfoQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new StreakInfoResponse(7, 10, new DateOnly(2026, 4, 14), 0, 3, 3, false, [], 3, 3, 0, 3, false)));
        var tool = new GetGamificationOverviewTool(mediator);

        var result = await tool.ExecuteAsync(Parse("{}"), UserId, CancellationToken.None);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task GetGamificationOverviewTool_ReturnsFailureWhenAchievementsFail()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetGamificationProfileQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new GamificationProfileResponse(0, 1, "Starter", 0, 10, 10, 0, 1, [], [], 0, 0, null)));
        mediator.Send(Arg.Any<GetAchievementsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<AchievementsResponse>("achievements_failed"));
        var tool = new GetGamificationOverviewTool(mediator);

        var result = await tool.ExecuteAsync(Parse("{}"), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("achievements_failed");
    }

    [Fact]
    public async Task GetGamificationOverviewTool_ReturnsFailureWhenStreakFails()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetGamificationProfileQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new GamificationProfileResponse(0, 1, "Starter", 0, 10, 10, 0, 1, [], [], 0, 0, null)));
        mediator.Send(Arg.Any<GetAchievementsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AchievementsResponse([])));
        mediator.Send(Arg.Any<GetStreakInfoQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<StreakInfoResponse>("streak_failed"));
        var tool = new GetGamificationOverviewTool(mediator);

        var result = await tool.ExecuteAsync(Parse("{}"), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("streak_failed");
    }

    [Fact]
    public async Task ActivateStreakFreezeTool_ReturnsSuccess()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ActivateStreakFreezeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new StreakFreezeResponse(1, new DateOnly(2026, 4, 14), 5, 0)));
        var tool = new ActivateStreakFreezeTool(mediator);

        var result = await tool.ExecuteAsync(Parse("{}"), UserId, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Activated streak freeze");
    }

    [Fact]
    public async Task ActivateStreakFreezeTool_ReturnsFailure()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ActivateStreakFreezeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<StreakFreezeResponse>("freeze_failed"));
        var tool = new ActivateStreakFreezeTool(mediator);

        var result = await tool.ExecuteAsync(Parse("{}"), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("freeze_failed");
    }

    [Fact]
    public async Task GetReferralOverviewTool_ReturnsFailure()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetReferralDashboardQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<ReferralDashboardResponse>("referrals_failed"));
        var tool = new GetReferralOverviewTool(mediator);

        var result = await tool.ExecuteAsync(Parse("{}"), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("referrals_failed");
    }

    [Fact]
    public async Task GetReferralOverviewTool_ReturnsSuccess()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetReferralDashboardQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new ReferralDashboardResponse(
                "ORBIT",
                "https://useorbit.org/r/ORBIT",
                new ReferralStatsResponse("ORBIT", "https://useorbit.org/r/ORBIT", 1, 2, 5, "discount", 20))));
        var tool = new GetReferralOverviewTool(mediator);

        var result = await tool.ExecuteAsync(Parse("{}"), UserId, CancellationToken.None);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task GetSubscriptionOverviewTool_SkipsQueriesWhenAllFlagsAreFalse()
    {
        var mediator = Substitute.For<IMediator>();
        var tool = new GetSubscriptionOverviewTool(mediator);

        var result = await tool.ExecuteAsync(
            Parse("""{"include_status":false,"include_billing":false,"include_plans":false}"""),
            UserId,
            CancellationToken.None);

        result.Success.Should().BeTrue();
        await mediator.DidNotReceiveWithAnyArgs().Send(default!, default);
    }

    [Fact]
    public async Task GetSubscriptionOverviewTool_ReturnsFullSuccessPayload()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetSubscriptionStatusQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new SubscriptionStatusResponse("Pro", true, false, null, null, 3, 50, false, "monthly")));
        mediator.Send(Arg.Any<GetBillingDetailsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new BillingDetailsResponse(
                "active",
                new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
                false,
                "monthly",
                999,
                "usd",
                null,
                [])));
        mediator.Send(Arg.Any<GetPlansQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new PlansResponse(
                new PlanPriceDto(999, "usd"),
                new PlanPriceDto(9999, "usd"),
                16,
                null,
                "usd")));
        var tool = new GetSubscriptionOverviewTool(mediator);

        var result = await tool.ExecuteAsync(Parse("{}"), UserId, CancellationToken.None);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task GetSubscriptionOverviewTool_ReturnsFailureWhenStatusFails()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetSubscriptionStatusQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<SubscriptionStatusResponse>("status_failed"));
        var tool = new GetSubscriptionOverviewTool(mediator);

        var result = await tool.ExecuteAsync(Parse("{}"), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("status_failed");
    }

    [Fact]
    public async Task GetSubscriptionOverviewTool_ReturnsFailureWhenBillingFails()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetSubscriptionStatusQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<SubscriptionStatusResponse>(null!));
        mediator.Send(Arg.Any<GetBillingDetailsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<BillingDetailsResponse>("billing_failed"));
        var tool = new GetSubscriptionOverviewTool(mediator);

        var result = await tool.ExecuteAsync(Parse("{}"), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("billing_failed");
    }

    [Fact]
    public async Task GetSubscriptionOverviewTool_ReturnsFailureWhenPlansFail()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetSubscriptionStatusQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new SubscriptionStatusResponse("Free", false, false, null, null, 0, 10, false, null)));
        mediator.Send(Arg.Any<GetBillingDetailsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new BillingDetailsResponse(
                "active",
                new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
                false,
                "monthly",
                999,
                "usd",
                null,
                [])));
        mediator.Send(Arg.Any<GetPlansQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<PlansResponse>("plans_failed"));
        var tool = new GetSubscriptionOverviewTool(mediator);

        var result = await tool.ExecuteAsync(Parse("{}"), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("plans_failed");
    }

    [Fact]
    public async Task ManageSubscriptionTool_RequiresAction()
    {
        var tool = new ManageSubscriptionTool(Substitute.For<IMediator>());

        var result = await tool.ExecuteAsync(Parse("{}"), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("action is required.");
    }

    [Fact]
    public async Task ManageSubscriptionTool_RequiresIntervalForCheckout()
    {
        var tool = new ManageSubscriptionTool(Substitute.For<IMediator>());

        var result = await tool.ExecuteAsync(Parse("""{"action":"create_checkout"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("interval is required.");
    }

    [Fact]
    public async Task ManageSubscriptionTool_CreatesCheckout()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<CreateCheckoutCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new CheckoutResponse("https://checkout")));
        var tool = new ManageSubscriptionTool(mediator);

        var result = await tool.ExecuteAsync(Parse("""{"action":"create_checkout","interval":"monthly"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Created checkout session");
    }

    [Fact]
    public async Task ManageSubscriptionTool_HandlesCheckoutFailure()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<CreateCheckoutCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<CheckoutResponse>("checkout_failed"));
        var tool = new ManageSubscriptionTool(mediator);

        var result = await tool.ExecuteAsync(Parse("""{"action":"create_checkout","interval":"monthly"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("checkout_failed");
    }

    [Fact]
    public async Task ManageSubscriptionTool_HandlesPortalFailure()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<CreatePortalSessionCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<PortalResponse>("portal_failed"));
        var tool = new ManageSubscriptionTool(mediator);

        var result = await tool.ExecuteAsync(Parse("""{"action":"create_portal"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("portal_failed");
    }

    [Fact]
    public async Task ManageSubscriptionTool_CreatesPortal()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<CreatePortalSessionCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new PortalResponse("https://portal")));
        var tool = new ManageSubscriptionTool(mediator);

        var result = await tool.ExecuteAsync(Parse("""{"action":"create_portal"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Created billing portal session");
    }

    [Fact]
    public async Task ManageSubscriptionTool_ClaimsAdReward()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ClaimAdRewardCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AdRewardResponse(5, 10, 55)));
        var tool = new ManageSubscriptionTool(mediator);

        var result = await tool.ExecuteAsync(Parse("""{"action":"claim_ad_reward"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Claimed ad reward");
    }

    [Fact]
    public async Task ManageSubscriptionTool_HandlesAdRewardFailure()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ClaimAdRewardCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<AdRewardResponse>("reward_failed"));
        var tool = new ManageSubscriptionTool(mediator);

        var result = await tool.ExecuteAsync(Parse("""{"action":"claim_ad_reward"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("reward_failed");
    }

    [Fact]
    public async Task ManageSubscriptionTool_RejectsUnsupportedAction()
    {
        var tool = new ManageSubscriptionTool(Substitute.For<IMediator>());

        var result = await tool.ExecuteAsync(Parse("""{"action":"unknown"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Unsupported action 'unknown'.");
    }

    [Fact]
    public async Task GetApiKeysTool_ReturnsSuccess()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetApiKeysQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<ApiKeyResponse>>([]));
        var tool = new GetApiKeysTool(mediator);

        var result = await tool.ExecuteAsync(Parse("{}"), UserId, CancellationToken.None);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task GetApiKeysTool_ReturnsFailure()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetApiKeysQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<IReadOnlyList<ApiKeyResponse>>("keys_failed"));
        var tool = new GetApiKeysTool(mediator);

        var result = await tool.ExecuteAsync(Parse("{}"), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("keys_failed");
    }

    [Fact]
    public async Task ManageApiKeysTool_RequiresAction()
    {
        var tool = new ManageApiKeysTool(Substitute.For<IMediator>());

        var result = await tool.ExecuteAsync(Parse("{}"), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("action is required.");
    }

    [Fact]
    public async Task ManageApiKeysTool_RequiresNameForCreate()
    {
        var tool = new ManageApiKeysTool(Substitute.For<IMediator>());

        var result = await tool.ExecuteAsync(Parse("""{"action":"create"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("name is required.");
    }

    [Fact]
    public async Task ManageApiKeysTool_RejectsInvalidExpiration()
    {
        var tool = new ManageApiKeysTool(Substitute.For<IMediator>());

        var result = await tool.ExecuteAsync(
            Parse("""{"action":"create","name":"Claude","expires_at_utc":"not-a-date"}"""),
            UserId,
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("expires_at_utc must be a valid ISO-8601 UTC timestamp.");
    }

    [Fact]
    public async Task ManageApiKeysTool_CreatesKey()
    {
        var keyId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<CreateApiKeyCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new CreateApiKeyResponse(
                keyId,
                "Claude",
                "secret",
                "orb_",
                ["read_habits"],
                true,
                new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 4, 14, 0, 0, 0, DateTimeKind.Utc))));
        var tool = new ManageApiKeysTool(mediator);

        var result = await tool.ExecuteAsync(
            Parse("""{"action":"create","name":"Claude","scopes":["read_habits"],"is_read_only":true,"expires_at_utc":"2026-05-01T00:00:00Z"}"""),
            UserId,
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.EntityId.Should().Be(keyId.ToString());
        result.EntityName.Should().Be("Claude");
    }

    [Fact]
    public async Task ManageApiKeysTool_PropagatesCreateFailure()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<CreateApiKeyCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<CreateApiKeyResponse>("create_failed"));
        var tool = new ManageApiKeysTool(mediator);

        var result = await tool.ExecuteAsync(Parse("""{"action":"create","name":"Claude"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("create_failed");
    }

    [Fact]
    public async Task ManageApiKeysTool_RejectsInvalidKeyIdForRevoke()
    {
        var tool = new ManageApiKeysTool(Substitute.For<IMediator>());

        var result = await tool.ExecuteAsync(Parse("""{"action":"revoke","key_id":"bad"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("key_id must be a valid GUID.");
    }

    [Fact]
    public async Task ManageApiKeysTool_RevokesKey()
    {
        var keyId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<RevokeApiKeyCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        var tool = new ManageApiKeysTool(mediator);

        var result = await tool.ExecuteAsync(Parse($$"""{"action":"revoke","key_id":"{{keyId}}"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.EntityId.Should().Be(keyId.ToString());
        result.EntityName.Should().Be("Revoked API key");
    }

    [Fact]
    public async Task ManageApiKeysTool_PropagatesRevokeFailure()
    {
        var keyId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<RevokeApiKeyCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("revoke_failed"));
        var tool = new ManageApiKeysTool(mediator);

        var result = await tool.ExecuteAsync(Parse($$"""{"action":"revoke","key_id":"{{keyId}}"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("revoke_failed");
    }

    [Fact]
    public async Task ManageApiKeysTool_RejectsUnsupportedAction()
    {
        var tool = new ManageApiKeysTool(Substitute.For<IMediator>());

        var result = await tool.ExecuteAsync(Parse("""{"action":"unknown"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Unsupported action 'unknown'.");
    }

    [Fact]
    public async Task SendSupportRequestTool_RequiresAllFields()
    {
        var tool = new SendSupportRequestTool(Substitute.For<IMediator>());

        var result = await tool.ExecuteAsync(Parse("""{"name":"Thomas"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("name, email, subject, and message are required.");
    }

    [Fact]
    public async Task SendSupportRequestTool_ReturnsFailure()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<SendSupportCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("support_failed"));
        var tool = new SendSupportRequestTool(mediator);

        var result = await tool.ExecuteAsync(
            Parse("""{"name":"Thomas","email":"t@example.com","subject":"Help","message":"Need support"}"""),
            UserId,
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("support_failed");
    }

    [Fact]
    public async Task SendSupportRequestTool_ReturnsSuccess()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<SendSupportCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        var tool = new SendSupportRequestTool(mediator);

        var result = await tool.ExecuteAsync(
            Parse("""{"name":"Thomas","email":"t@example.com","subject":"Help","message":"Need support"}"""),
            UserId,
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Support request sent");
    }

    [Fact]
    public async Task ManageAccountTool_RequiresAction()
    {
        var tool = new ManageAccountTool(Substitute.For<IMediator>());

        var result = await tool.ExecuteAsync(Parse("{}"), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("action is required.");
    }

    [Fact]
    public async Task ManageAccountTool_ResetsAccount()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ResetAccountCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        var tool = new ManageAccountTool(mediator);

        var result = await tool.ExecuteAsync(Parse("""{"action":"reset_account"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Account reset completed");
    }

    [Fact]
    public async Task ManageAccountTool_RequestsDeletion()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<RequestAccountDeletionCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        var tool = new ManageAccountTool(mediator);

        var result = await tool.ExecuteAsync(Parse("""{"action":"request_deletion"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Deletion code requested");
    }

    [Fact]
    public async Task ManageAccountTool_PropagatesRequestDeletionFailure()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<RequestAccountDeletionCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("request_failed"));
        var tool = new ManageAccountTool(mediator);

        var result = await tool.ExecuteAsync(Parse("""{"action":"request_deletion"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("request_failed");
    }

    [Fact]
    public async Task ManageAccountTool_RequiresCodeForDeletionConfirmation()
    {
        var tool = new ManageAccountTool(Substitute.For<IMediator>());

        var result = await tool.ExecuteAsync(Parse("""{"action":"confirm_deletion"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("code is required.");
    }

    [Fact]
    public async Task ManageAccountTool_ConfirmsDeletion()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ConfirmAccountDeletionCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new DateTime(2026, 4, 20, 12, 0, 0, DateTimeKind.Utc)));
        var tool = new ManageAccountTool(mediator);

        var result = await tool.ExecuteAsync(
            Parse("""{"action":"confirm_deletion","code":"123456"}"""),
            UserId,
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Account deletion confirmed");
    }

    [Fact]
    public async Task ManageAccountTool_PropagatesConfirmDeletionFailure()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ConfirmAccountDeletionCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<DateTime>("confirm_failed"));
        var tool = new ManageAccountTool(mediator);

        var result = await tool.ExecuteAsync(
            Parse("""{"action":"confirm_deletion","code":"123456"}"""),
            UserId,
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("confirm_failed");
    }

    [Fact]
    public async Task ManageAccountTool_RejectsUnsupportedAction()
    {
        var tool = new ManageAccountTool(Substitute.For<IMediator>());

        var result = await tool.ExecuteAsync(Parse("""{"action":"unknown"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Unsupported action 'unknown'.");
    }

    private static JsonElement Parse(string json)
    {
        return JsonDocument.Parse(json).RootElement.Clone();
    }
}
