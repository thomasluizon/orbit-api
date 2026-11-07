using FluentAssertions;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Orbit.Api.Extensions;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Validators;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using NSubstitute;

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

    /// <summary>
    /// The scope factory is resolved once: the dynamic DI engine compiles a resolver for each accessor on a pool
    /// thread after its second call, and the next call JIT-compiles it on the caller's thread at a time that
    /// depends on load. The window then holds only the scopes, the validator resolution and the validation.
    /// </summary>
    [Fact]
    public void RepeatedRequestValidation_CompilesNoNewMethods()
    {
        using var provider = new ServiceCollection().AddOrbitValidators().BuildServiceProvider();
        var scopes = provider.GetRequiredService<IServiceScopeFactory>();
        var command = new CreateHabitCommand(Guid.NewGuid(), "Read", "Ten pages", FrequencyUnit.Day, 1,
            Options: new HabitCommandOptions(ReminderTimes: [15], ChecklistItems: []));

        for (var warmup = 0; warmup < 3; warmup++)
            ValidateInNewScope(scopes, command);
        var compiledBefore = System.Runtime.JitInfo.GetCompiledMethodCount(currentThread: true);
        for (var request = 0; request < 50; request++)
            ValidateInNewScope(scopes, command);

        (System.Runtime.JitInfo.GetCompiledMethodCount(currentThread: true) - compiledBefore).Should().Be(0,
            "a request scope must not construct a validator that compiles its rule expressions again");
    }

    /// <summary>
    /// Each request scope constructs the scoped update validator, which
    /// <see cref="DependencyFreeValidators_AreSingletonsAndValidatorsWithDependenciesStayScoped"/> pins. The window
    /// constructs it directly with one repository double, so neither a DI resolver compiled on a pool thread nor a
    /// proxy constructor stub enters the count.
    /// </summary>
    [Fact]
    public void RepeatedUpdateValidatorConstruction_CompilesNoNewMethods()
    {
        var repository = Substitute.For<IGenericRepository<Habit>>();

        for (var warmup = 0; warmup < 3; warmup++)
            new UpdateHabitCommandValidator(repository).Should().NotBeNull();
        var compiledBefore = System.Runtime.JitInfo.GetCompiledMethodCount(currentThread: true);
        for (var request = 0; request < 50; request++)
            new UpdateHabitCommandValidator(repository).Should().NotBeNull();

        (System.Runtime.JitInfo.GetCompiledMethodCount(currentThread: true) - compiledBefore).Should().Be(0,
            "the scoped update validator must include its repository-free rules instead of compiling them again");
    }

    private static void ValidateInNewScope(IServiceScopeFactory scopes, CreateHabitCommand command)
    {
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<IValidator<CreateHabitCommand>>().Validate(command).IsValid.Should().BeTrue();
    }
}
