using MediatR;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tags.Queries;

public record SuggestedTag(string Name, string Color, bool IsExisting, Guid? Id);

public record SuggestTagsResponse(IReadOnlyList<SuggestedTag> Tags);

public record SuggestTagsQuery(Guid UserId, string Title, string? Description, string Language)
    : IRequest<Result<SuggestTagsResponse>>;

public class SuggestTagsQueryHandler(
    IPayGateService payGate,
    ITagSuggestionService tagSuggestionService,
    IGenericRepository<Tag> tagRepository,
    IGenericRepository<User> userRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<SuggestTagsQuery, Result<SuggestTagsResponse>>
{
    private const string NewTagColor = "#7c3aed";

    public async Task<Result<SuggestTagsResponse>> Handle(
        SuggestTagsQuery request,
        CancellationToken cancellationToken)
    {
        var gateCheck = await payGate.CanSendAiMessage(request.UserId, cancellationToken);
        if (gateCheck.IsFailure)
            return gateCheck.PropagateError<SuggestTagsResponse>();

        var existingTags = await tagRepository.FindAsync(
            tag => tag.UserId == request.UserId,
            cancellationToken);

        var existingNames = existingTags.Select(tag => tag.Name).ToList();

        var suggestionResult = await tagSuggestionService.SuggestTagsAsync(
            request.Title,
            request.Description,
            existingNames,
            request.Language,
            cancellationToken);

        if (suggestionResult.IsFailure)
            return suggestionResult.PropagateError<SuggestTagsResponse>();

        var suggestions = MapSuggestions(suggestionResult.Value, existingTags);

        await MeterAiMessageAsync(request.UserId, cancellationToken);

        return Result.Success(new SuggestTagsResponse(suggestions));
    }

    private static IReadOnlyList<SuggestedTag> MapSuggestions(
        IReadOnlyList<string> suggestedNames,
        IReadOnlyList<Tag> existingTags)
    {
        var existingByName = existingTags
            .GroupBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mapped = new List<SuggestedTag>();

        foreach (var rawName in suggestedNames)
        {
            var capitalized = Capitalize(rawName.Trim());
            if (string.IsNullOrEmpty(capitalized) || !seen.Add(capitalized))
                continue;

            mapped.Add(existingByName.TryGetValue(capitalized, out var existing)
                ? new SuggestedTag(existing.Name, existing.Color, IsExisting: true, existing.Id)
                : new SuggestedTag(capitalized, NewTagColor, IsExisting: false, Id: null));

            if (mapped.Count >= AppConstants.MaxTagsPerHabit)
                break;
        }

        return mapped;
    }

    private async Task MeterAiMessageAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await userRepository.FindOneTrackedAsync(
            candidate => candidate.Id == userId,
            cancellationToken: cancellationToken);
        if (user is null)
            return;

        user.IncrementAiMessageCount();
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private static string Capitalize(string value) =>
        string.IsNullOrEmpty(value) ? value : char.ToUpper(value[0]) + value[1..].ToLower();
}
