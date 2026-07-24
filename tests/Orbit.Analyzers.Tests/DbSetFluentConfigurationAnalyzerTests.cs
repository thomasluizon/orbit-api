using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

namespace Orbit.Analyzers.Tests;

public sealed class DbSetFluentConfigurationAnalyzerTests
{
    private const string EfStub = """
        namespace Microsoft.EntityFrameworkCore
        {
            using System;

            public class DbContext
            {
                public DbSet<TEntity> Set<TEntity>() where TEntity : class => new DbSet<TEntity>();

                protected internal virtual void OnModelCreating(ModelBuilder modelBuilder) { }
            }

            public class DbSet<TEntity> where TEntity : class { }

            public class EntityTypeBuilder<TEntity> where TEntity : class { }

            public interface IEntityTypeConfiguration<TEntity> where TEntity : class
            {
                void Configure(EntityTypeBuilder<TEntity> builder);
            }

            public class ModelBuilder
            {
                public EntityTypeBuilder<TEntity> Entity<TEntity>() where TEntity : class =>
                    new EntityTypeBuilder<TEntity>();

                public ModelBuilder Entity<TEntity>(Action<EntityTypeBuilder<TEntity>> buildAction) where TEntity : class =>
                    this;

                public ModelBuilder ApplyConfiguration<TEntity>(IEntityTypeConfiguration<TEntity> configuration) where TEntity : class =>
                    this;

                public ModelBuilder ApplyConfigurationsFromAssembly(System.Reflection.Assembly assembly) => this;
            }
        }

        namespace App
        {
            public sealed class User { }

            public sealed class Habit { }
        }

        """;

    [Fact]
    public Task Flags_DbSet_Without_Matching_Entity_Configuration()
    {
        var source = EfStub + """
            namespace App
            {
                using Microsoft.EntityFrameworkCore;

                public sealed class AppDbContext : DbContext
                {
                    public DbSet<User> Users => Set<User>();
                    public DbSet<Habit> {|ORBIT0005:Habits|} => Set<Habit>();

                    protected internal override void OnModelCreating(ModelBuilder modelBuilder)
                    {
                        modelBuilder.Entity<User>(entity => { });
                    }
                }
            }
            """;

        return VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task Flags_Every_DbSet_When_There_Is_No_OnModelCreating()
    {
        var source = EfStub + """
            namespace App
            {
                using Microsoft.EntityFrameworkCore;

                public sealed class AppDbContext : DbContext
                {
                    public DbSet<User> {|ORBIT0005:Users|} => Set<User>();
                }
            }
            """;

        return VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task Passes_When_Configuration_Lives_In_A_Helper_Method()
    {
        var source = EfStub + """
            namespace App
            {
                using Microsoft.EntityFrameworkCore;

                public sealed class AppDbContext : DbContext
                {
                    public DbSet<User> Users => Set<User>();
                    public DbSet<Habit> Habits => Set<Habit>();

                    protected internal override void OnModelCreating(ModelBuilder modelBuilder)
                    {
                        ConfigureUserEntity(modelBuilder);
                        MapToken(modelBuilder.Entity<Habit>());
                    }

                    private static void ConfigureUserEntity(ModelBuilder modelBuilder)
                    {
                        modelBuilder.Entity<User>(entity => { });
                    }

                    private static void MapToken(EntityTypeBuilder<Habit> entity) { }
                }
            }
            """;

        return VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task Passes_When_Configured_Via_ApplyConfiguration()
    {
        var source = EfStub + """
            namespace App
            {
                using Microsoft.EntityFrameworkCore;

                public sealed class UserConfiguration : IEntityTypeConfiguration<User>
                {
                    public void Configure(EntityTypeBuilder<User> builder) { }
                }

                public sealed class AppDbContext : DbContext
                {
                    public DbSet<User> Users => Set<User>();

                    protected internal override void OnModelCreating(ModelBuilder modelBuilder)
                    {
                        modelBuilder.ApplyConfiguration(new UserConfiguration());
                    }
                }
            }
            """;

        return VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task Skips_Context_That_Applies_Configurations_From_Assembly()
    {
        var source = EfStub + """
            namespace App
            {
                using Microsoft.EntityFrameworkCore;

                public sealed class AppDbContext : DbContext
                {
                    public DbSet<User> Users => Set<User>();

                    protected internal override void OnModelCreating(ModelBuilder modelBuilder)
                    {
                        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
                    }
                }
            }
            """;

        return VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task Passes_When_Partial_Declarations_Split_DbSets_And_Configuration()
    {
        var source = EfStub + """
            namespace App
            {
                using Microsoft.EntityFrameworkCore;

                public sealed partial class AppDbContext : DbContext
                {
                    public DbSet<User> Users => Set<User>();
                }

                public sealed partial class AppDbContext
                {
                    protected internal override void OnModelCreating(ModelBuilder modelBuilder)
                    {
                        modelBuilder.Entity<User>(entity => { });
                    }
                }
            }
            """;

        return VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task Ignores_DbSet_Properties_Outside_A_DbContext()
    {
        var source = EfStub + """
            namespace App
            {
                using Microsoft.EntityFrameworkCore;

                public sealed class Repository
                {
                    public DbSet<User> Users { get; } = new DbSet<User>();
                }
            }
            """;

        return VerifyAnalyzerAsync(source);
    }

    private static Task VerifyAnalyzerAsync(string source)
    {
        var test = new CSharpAnalyzerTest<DbSetFluentConfigurationAnalyzer, DefaultVerifier>
        {
            TestCode = source,
            ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
        };

        return test.RunAsync();
    }
}
