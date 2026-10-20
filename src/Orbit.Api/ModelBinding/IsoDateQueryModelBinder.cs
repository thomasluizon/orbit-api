using System.Globalization;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Orbit.Application.Common;

namespace Orbit.Api.ModelBinding;

public sealed class IsoDateQueryModelBinder : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext bindingContext)
    {
        var value = bindingContext.ValueProvider.GetValue(bindingContext.ModelName);
        if (value == ValueProviderResult.None)
        {
            bindingContext.Result = ModelBindingResult.Success(null);
            return Task.CompletedTask;
        }

        if (value.Length != 1 || !DateOnly.TryParseExact(
                value.FirstValue, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            throw new ValidationException([
                new ValidationFailure(bindingContext.ModelName, ErrorCopy.English(ValidationErrorCodes.CalendarDateValid))
                {
                    ErrorCode = ValidationErrorCodes.CalendarDateValid
                }
            ]);
        }

        bindingContext.Result = ModelBindingResult.Success(date);
        return Task.CompletedTask;
    }
}
