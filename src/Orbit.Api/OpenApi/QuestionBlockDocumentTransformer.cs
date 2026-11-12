using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Orbit.Domain.Models;

namespace Orbit.Api.OpenApi;

public sealed class QuestionBlockDocumentTransformer : IOpenApiDocumentTransformer
{
    public async Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        foreach (var type in new[] { typeof(AiQuestionBlock), typeof(AiBlockAnswer), typeof(AiQuestion),
                     typeof(AiQuestionAnswer), typeof(AiQuestionOption), typeof(AiQuestionProgress) })
        {
            var schema = await context.GetOrCreateSchemaAsync(type, cancellationToken: cancellationToken);
            document.AddComponent(type.Name, schema);
        }
    }
}
