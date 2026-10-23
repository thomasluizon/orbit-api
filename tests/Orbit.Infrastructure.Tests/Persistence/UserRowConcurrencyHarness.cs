using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;
using Orbit.Application.Profile.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Persistence;

internal sealed class UserRowConcurrencyHarness : IAsyncDisposable
{
    private readonly DbContextOptions<OrbitDbContext> _options;
    private readonly Action<User> _competingWrite;
    private readonly int _maxRaces;
    private bool _locked;
    public OrbitDbContext Context { get; }
    public IUnitOfWork Work { get; } = Substitute.For<IUnitOfWork>();
    public int SaveAttempts { get; private set; }
    public int ConcurrentWrites { get; private set; }
    public bool ForceUserConflict { get; set; }
    public int TransientUserConflicts { get; set; }

    public UserRowConcurrencyHarness(Action<User> competingWrite, int maxRaces = int.MaxValue)
    {
        _competingWrite = competingWrite;
        _maxRaces = maxRaces;
        _options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        Context = new TokenContext(new DbContextOptionsBuilder<OrbitDbContext>(_options)
            .AddInterceptors(new RacingSave(this)).Options);
        Work.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(call => Context.SaveChangesAsync(call.Arg<CancellationToken>()));
        Work.When(work => work.ResetTracking()).Do(_ => Context.ChangeTracker.Clear());
        Work.AcquireUserRowLockAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                if (ConcurrentWrites < _maxRaces) await WriteAsync();
                _locked = true;
            });
        Work.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                try { await call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)); }
                finally { _locked = false; }
            });
        ConfigureTransaction<Result>();
        ConfigureTransaction<Result<ApplyOnboardingResponse>>();
    }

    private void ConfigureTransaction<T>()
    {
        Work.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<T>>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                try { return await call.ArgAt<Func<CancellationToken, Task<T>>>(0)(call.ArgAt<CancellationToken>(1)); }
                finally { _locked = false; }
            });
    }

    public async Task SeedAsync(User user)
    {
        await using var seed = CreateContext();
        seed.Users.Add(user);
        seed.Entry(user).Property<uint>("xmin").CurrentValue = 100;
        await seed.SaveChangesAsync();
        await Context.Users.SingleAsync();
    }

    public OrbitDbContext CreateContext() => new TokenContext(_options);

    public async Task WriteAsync()
    {
        await using var writer = CreateContext();
        var user = await writer.Users.SingleAsync();
        _competingWrite(user);
        writer.Entry(user).Property<uint>("xmin").CurrentValue = (uint)(100 + ++ConcurrentWrites);
        await writer.SaveChangesAsync();
    }

    public ValueTask DisposeAsync() => Context.DisposeAsync();

    private sealed class TokenContext(DbContextOptions<OrbitDbContext> options) : OrbitDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<User>().Property<uint>("xmin").IsConcurrencyToken().ValueGeneratedNever();
        }
    }

    private sealed class RacingSave(UserRowConcurrencyHarness harness) : SaveChangesInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            harness.SaveAttempts++;
            var userModified = harness.Context.ChangeTracker.Entries<User>()
                .Any(entry => entry.State == EntityState.Modified);
            if (userModified && (harness.ForceUserConflict || harness.TransientUserConflicts > 0))
            {
                if (harness.TransientUserConflicts > 0) harness.TransientUserConflicts--;
                throw new DbUpdateConcurrencyException("simulated user conflict");
            }
            if (userModified && !harness._locked && harness.ConcurrentWrites < harness._maxRaces)
                await harness.WriteAsync();
            return result;
        }
    }
}
