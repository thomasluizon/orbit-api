using System.Reflection;
using FluentAssertions;
using FluentValidation;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Habits.Validators;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Common;

public class ValidationCopyTests
{
    [Fact]
    public void EveryDeclaredValidationCodeHasCopyInBothLanguages()
    {
        foreach (var field in typeof(ValidationErrorCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var code = (string)field.GetRawConstantValue()!;
            ErrorCopy.TryResolve(code, false, [], out var english).Should().BeTrue(code);
            ErrorCopy.TryResolve(code, true, [], out var portuguese).Should().BeTrue(code);
            english.Should().NotBeNullOrWhiteSpace();
            portuguese.Should().NotBeNullOrWhiteSpace().And.NotBe(english);
        }
    }

    [Fact]
    public void EveryValidatorRuleHasACatalogCode()
    {
        var validators = typeof(ValidationErrorCodes).Assembly.GetExportedTypes()
            .Where(type => !type.IsAbstract && typeof(IValidator).IsAssignableFrom(type));

        foreach (var type in validators)
        {
            var validator = type == typeof(UpdateHabitCommandValidator)
                ? new UpdateHabitCommandValidator(Substitute.For<IGenericRepository<Habit>>())
                : (IValidator)(Activator.CreateInstance(type)
                    ?? throw new InvalidOperationException($"Cannot create validator: {type.Name}"));
            foreach (var component in validator.CreateDescriptor().GetMembersWithValidators().SelectMany(g => g))
            {
                if (component.Item1.Name == "ChildValidatorAdaptor")
                    continue;

                var code = component.Item2.ErrorCode ?? component.Item1.Name;
                ErrorCopy.All.Should().ContainKey(code, $"{type.Name} must not fall back to raw validation copy");
            }
        }
    }
}
