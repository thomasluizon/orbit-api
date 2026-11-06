using FluentAssertions;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Orbit.Api.Extensions;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Validators;
using Orbit.Domain.Enums;

namespace Orbit.Infrastructure.Tests.Extensions;

public sealed class ValidatorRegistrationTests
{
    [Fact]
    public void DependencyFreeValidators_AreSingletonsAndValidatorsWithDependenciesStayScoped()
    {
        var services = new ServiceCollection().AddOrbitValidators();

        var validators = services.Where(descriptor => descriptor.ServiceType.IsGenericType
            && descriptor.ServiceType.GetGenericTypeDefinition() == typeof(IValidator<>)).ToArray();
        validators.Should().NotBeEmpty();
        validators.Should().OnlyContain(descriptor => descriptor.Lifetime == (descriptor.ImplementationType!.GetConstructors()
            .All(constructor => constructor.GetParameters().Length == 0) ? ServiceLifetime.Singleton : ServiceLifetime.Scoped));
        validators.Should().ContainSingle(descriptor => descriptor.ImplementationType == typeof(CreateHabitCommandValidator))
            .Which.Lifetime.Should().Be(ServiceLifetime.Singleton);
        validators.Should().ContainSingle(descriptor => descriptor.ImplementationType == typeof(UpdateHabitCommandValidator))
            .Which.Lifetime.Should().Be(ServiceLifetime.Scoped);
    }

    [Fact]
    public void RepeatedRequestValidation_CompilesNoNewMethods()
    {
        using var provider = new ServiceCollection().AddOrbitValidators().BuildServiceProvider();
        var command = new CreateHabitCommand(Guid.NewGuid(), "Read", "Ten pages", FrequencyUnit.Day, 1,
            Options: new HabitCommandOptions(ReminderTimes: [15], ChecklistItems: []));

        for (var warmup = 0; warmup < 3; warmup++)
            ValidateInNewScope(provider, command);
        var compiledBefore = System.Runtime.JitInfo.GetCompiledMethodCount(currentThread: true);
        for (var request = 0; request < 50; request++)
            ValidateInNewScope(provider, command);

        (System.Runtime.JitInfo.GetCompiledMethodCount(currentThread: true) - compiledBefore).Should().Be(0,
            "a request scope must not construct a validator that compiles its rule expressions again");
    }

    private static void ValidateInNewScope(IServiceProvider provider, CreateHabitCommand command)
    {
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IValidator<CreateHabitCommand>>().Validate(command).IsValid.Should().BeTrue();
    }
}
