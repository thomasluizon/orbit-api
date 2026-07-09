using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

namespace Orbit.Analyzers.Tests;

public sealed class RollbackInUsingTransactionAnalyzerTests
{
    private const string EfStub = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;

        namespace Microsoft.EntityFrameworkCore.Storage
        {
            public interface IDbContextTransaction : IAsyncDisposable
            {
                Task CommitAsync(CancellationToken cancellationToken);
                Task RollbackAsync(CancellationToken cancellationToken);
            }
        }

        namespace App
        {
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.EntityFrameworkCore.Storage;

            public sealed class FakeTransaction : IDbContextTransaction
            {
                public Task CommitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
                public Task RollbackAsync(CancellationToken cancellationToken) => Task.CompletedTask;
                public ValueTask DisposeAsync() => default;
            }

            public sealed class FakeDatabase
            {
                public Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
                    Task.FromResult<IDbContextTransaction>(new FakeTransaction());
            }
        }

        """;

    [Fact]
    public Task Flags_RollbackAsync_In_AwaitUsing_Declaration()
    {
        var source = EfStub + """
            namespace App
            {
                using System.Threading;
                using System.Threading.Tasks;
                using Microsoft.EntityFrameworkCore.Storage;

                public sealed class Service
                {
                    private readonly FakeDatabase database = new();

                    public async Task RunAsync(CancellationToken ct)
                    {
                        await using var tx = await database.BeginTransactionAsync(ct);
                        try
                        {
                            await tx.CommitAsync(ct);
                        }
                        catch
                        {
                            await {|ORBIT0002:tx.RollbackAsync(ct)|};
                            throw;
                        }
                    }
                }
            }
            """;

        return VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task Flags_RollbackAsync_In_AwaitUsing_Statement_Block()
    {
        var source = EfStub + """
            namespace App
            {
                using System.Threading;
                using System.Threading.Tasks;
                using Microsoft.EntityFrameworkCore.Storage;

                public sealed class Service
                {
                    private readonly FakeDatabase database = new();

                    public async Task RunAsync(CancellationToken ct)
                    {
                        await using (var tx = await database.BeginTransactionAsync(ct))
                        {
                            try
                            {
                                await tx.CommitAsync(ct);
                            }
                            catch
                            {
                                await {|ORBIT0002:tx.RollbackAsync(ct)|};
                            }
                        }
                    }
                }
            }
            """;

        return VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task CodeFix_Removes_Redundant_Rollback()
    {
        var source = EfStub + """
            namespace App
            {
                using System.Threading;
                using System.Threading.Tasks;
                using Microsoft.EntityFrameworkCore.Storage;

                public sealed class Service
                {
                    private readonly FakeDatabase database = new();

                    public async Task RunAsync(CancellationToken ct)
                    {
                        await using var tx = await database.BeginTransactionAsync(ct);
                        try
                        {
                            await tx.CommitAsync(ct);
                        }
                        catch
                        {
                            await {|ORBIT0002:tx.RollbackAsync(ct)|};
                            throw;
                        }
                    }
                }
            }
            """;

        var fixedSource = EfStub + """
            namespace App
            {
                using System.Threading;
                using System.Threading.Tasks;
                using Microsoft.EntityFrameworkCore.Storage;

                public sealed class Service
                {
                    private readonly FakeDatabase database = new();

                    public async Task RunAsync(CancellationToken ct)
                    {
                        await using var tx = await database.BeginTransactionAsync(ct);
                        try
                        {
                            await tx.CommitAsync(ct);
                        }
                        catch
                        {
                            throw;
                        }
                    }
                }
            }
            """;

        return VerifyCodeFixAsync(source, fixedSource);
    }

    [Fact]
    public Task Ignores_Manually_Owned_Transaction_Without_Using()
    {
        var source = EfStub + """
            namespace App
            {
                using System.Threading;
                using System.Threading.Tasks;
                using Microsoft.EntityFrameworkCore.Storage;

                public sealed class Service
                {
                    private readonly FakeDatabase database = new();

                    public async Task RunAsync(CancellationToken ct)
                    {
                        var tx = await database.BeginTransactionAsync(ct);
                        try
                        {
                            await tx.CommitAsync(ct);
                        }
                        catch
                        {
                            await tx.RollbackAsync(ct);
                            throw;
                        }
                    }
                }
            }
            """;

        return VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task Ignores_Rollback_On_Non_Transaction_Receiver()
    {
        var source = EfStub + """
            namespace App
            {
                using System;
                using System.Threading;
                using System.Threading.Tasks;

                public sealed class CustomResource : IAsyncDisposable
                {
                    public Task RollbackAsync(CancellationToken cancellationToken) => Task.CompletedTask;
                    public ValueTask DisposeAsync() => default;
                }

                public sealed class OtherService
                {
                    public async Task RunAsync(CancellationToken ct)
                    {
                        await using var custom = new CustomResource();
                        try
                        {
                        }
                        catch
                        {
                            await custom.RollbackAsync(ct);
                            throw;
                        }
                    }
                }
            }
            """;

        return VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task Ignores_Rollback_On_Manually_Owned_Inner_Transaction()
    {
        var source = EfStub + """
            namespace App
            {
                using System.Threading;
                using System.Threading.Tasks;
                using Microsoft.EntityFrameworkCore.Storage;

                public sealed class Service
                {
                    private readonly FakeDatabase database = new();

                    public async Task RunAsync(CancellationToken ct)
                    {
                        await using var outer = await database.BeginTransactionAsync(ct);
                        var inner = await database.BeginTransactionAsync(ct);
                        try
                        {
                            await inner.CommitAsync(ct);
                            await outer.CommitAsync(ct);
                        }
                        catch
                        {
                            await inner.RollbackAsync(ct);
                            throw;
                        }
                    }
                }
            }
            """;

        return VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task Ignores_Rollback_On_Transaction_Parameter()
    {
        var source = EfStub + """
            namespace App
            {
                using System.Threading;
                using System.Threading.Tasks;
                using Microsoft.EntityFrameworkCore.Storage;

                public sealed class Service
                {
                    public Task UndoAsync(IDbContextTransaction tx, CancellationToken ct) =>
                        tx.RollbackAsync(ct);
                }
            }
            """;

        return VerifyAnalyzerAsync(source);
    }

    private static Task VerifyAnalyzerAsync(string source)
    {
        var test = new CSharpAnalyzerTest<RollbackInUsingTransactionAnalyzer, DefaultVerifier>
        {
            TestCode = source,
            ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
        };

        return test.RunAsync();
    }

    private static Task VerifyCodeFixAsync(string source, string fixedSource)
    {
        var test = new CSharpCodeFixTest<
            RollbackInUsingTransactionAnalyzer,
            RollbackInUsingTransactionCodeFixProvider,
            DefaultVerifier>
        {
            TestCode = source,
            FixedCode = fixedSource,
            ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
        };

        return test.RunAsync();
    }
}
