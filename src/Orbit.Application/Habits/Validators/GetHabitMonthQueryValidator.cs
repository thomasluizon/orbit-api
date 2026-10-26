using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Habits.Validators;

public sealed class GetHabitMonthQueryValidator : AbstractValidator<GetHabitMonthQuery>
{
    public GetHabitMonthQueryValidator()
    {
        RuleFor(query => query.UserId).NotEmpty().WithCopy(DomainErrors.UserIdRequired.Code);
        RuleFor(query => query)
            .Must(query => HabitCalendarWindow.Resolve(query.DateFrom, query.DateTo,
                new DateOnly(2000, 1, 1)).IsSuccess)
            .WithCopy(DomainErrors.HabitCalendarRangeInvalid.Code);
        RuleFor(query => query.Language).Must(language => language is "en" or "pt" or "pt-BR")
            .WithCopy(ValidationErrorCodes.LanguageSupported);
    }
}
