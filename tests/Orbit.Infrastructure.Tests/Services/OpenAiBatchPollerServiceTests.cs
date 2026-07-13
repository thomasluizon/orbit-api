using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class OpenAiBatchPollerServiceTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private static string BatchOutputJsonl(params string[] facts)
    {
        var content = JsonSerializer.Serialize(new
        {
            facts = facts.Select(f => new { factText = f, category = "context" }).ToArray()
        });

        var line = new
        {
            id = "batch_req_1",
            custom_id = "cid-1",
            response = new
            {
                status_code = 200,
                request_id = "req-1",
                body = new
                {
                    id = "chatcmpl-1",
                    choices = new[]
                    {
                        new { index = 0, message = new { role = "assistant", content } }
                    }
                }
            },
            error = (object?)null
        };

        return JsonSerializer.Serialize(line);
    }

    [Fact]
    public void ParseExtractedFacts_ValidOutput_ReturnsFactsWithoutWarning()
    {
        var jsonl = BatchOutputJsonl("User is a vegetarian", "User works night shifts");
        var logger = new CapturingLogger<OpenAiBatchPollerService>();

        var facts = OpenAiBatchPollerService.ParseExtractedFacts(jsonl, logger, "batch_1");

        facts.Should().HaveCount(2);
        facts.Select(f => f.FactText).Should().Contain("User is a vegetarian");
        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void ParseExtractedFacts_MalformedLine_LogsWarningAndReturnsEmpty()
    {
        var logger = new CapturingLogger<OpenAiBatchPollerService>();

        var facts = OpenAiBatchPollerService.ParseExtractedFacts("not json at all", logger, "batch_bad");

        facts.Should().BeEmpty();
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("batch_bad"));
    }

    [Fact]
    public void ParseExtractedFacts_EmptyOutput_LogsWarningAndReturnsEmpty()
    {
        var logger = new CapturingLogger<OpenAiBatchPollerService>();

        var facts = OpenAiBatchPollerService.ParseExtractedFacts(string.Empty, logger, "batch_empty");

        facts.Should().BeEmpty();
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("batch_empty"));
    }

    [Fact]
    public async Task PollPendingBatches_CompletedBatch_PersistsNewFactsDedupedAndDeletesFiles()
    {
        var batch = AiFactExtractionBatch.Create(UserId, "batch_1", "file_in_1");
        var existingFact = UserFact.Create(UserId, "User is a vegetarian", "context").Value;

        var harness = new PollerHarness()
            .WithPendingBatches(batch)
            .WithExistingFacts(existingFact)
            .WithBatchStatus("batch_1", new BatchStatusResult("completed", "file_out_1", null))
            .WithBatchOutput("file_out_1", BatchOutputJsonl("User is a vegetarian", "User works night shifts"));

        await harness.Service.PollPendingBatches(CancellationToken.None);

        harness.AddedFacts.Should().ContainSingle();
        harness.AddedFacts[0].FactText.Should().Be("User works night shifts");
        batch.Status.Should().Be(AiFactExtractionBatchStatus.Completed);
        batch.OutputFileId.Should().Be("file_out_1");
        await harness.BatchClient.Received(1).DeleteFileAsync("file_in_1", Arg.Any<CancellationToken>());
        await harness.BatchClient.Received(1).DeleteFileAsync("file_out_1", Arg.Any<CancellationToken>());
        await harness.UnitOfWork.Received().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PollPendingBatches_CompletedBatch_RespectsMaxUserFactsCap()
    {
        var batch = AiFactExtractionBatch.Create(UserId, "batch_1", "file_in_1");

        var harness = new PollerHarness()
            .WithMaxUserFacts(2)
            .WithPendingBatches(batch)
            .WithExistingFacts(
                UserFact.Create(UserId, "Existing fact one", "context").Value)
            .WithBatchStatus("batch_1", new BatchStatusResult("completed", "file_out_1", null))
            .WithBatchOutput("file_out_1", BatchOutputJsonl("New fact A", "New fact B", "New fact C"));

        await harness.Service.PollPendingBatches(CancellationToken.None);

        harness.AddedFacts.Should().ContainSingle();
        harness.AddedFacts[0].FactText.Should().Be("New fact A");
    }

    [Fact]
    public async Task PollPendingBatches_FailedBatch_MarksFailedAndDeletesInputFile()
    {
        var batch = AiFactExtractionBatch.Create(UserId, "batch_1", "file_in_1");

        var harness = new PollerHarness()
            .WithPendingBatches(batch)
            .WithBatchStatus("batch_1", new BatchStatusResult("failed", null, "file_err_1"));

        await harness.Service.PollPendingBatches(CancellationToken.None);

        batch.Status.Should().Be(AiFactExtractionBatchStatus.Failed);
        harness.AddedFacts.Should().BeEmpty();
        await harness.BatchClient.Received(1).DeleteFileAsync("file_in_1", Arg.Any<CancellationToken>());
        await harness.BatchClient.Received(1).DeleteFileAsync("file_err_1", Arg.Any<CancellationToken>());
        await harness.BatchClient.DidNotReceive().DownloadFileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PollPendingBatches_CompletedBatch_FileDeletionFailure_IsSwallowedAndLogged()
    {
        var batch = AiFactExtractionBatch.Create(UserId, "batch_1", "file_in_1");

        var harness = new PollerHarness()
            .WithPendingBatches(batch)
            .WithBatchStatus("batch_1", new BatchStatusResult("completed", "file_out_1", null))
            .WithBatchOutput("file_out_1", BatchOutputJsonl("User works night shifts"))
            .WithFileDeletionFailure();
        var service = harness.Service;

        var act = () => service.PollPendingBatches(CancellationToken.None);

        await act.Should().NotThrowAsync();
        batch.Status.Should().Be(AiFactExtractionBatchStatus.Completed);
        harness.AddedFacts.Should().ContainSingle();
        harness.Logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("batch_1"));
    }

    [Fact]
    public async Task PollPendingBatches_CompletedAndInFlightBatches_FinalizesCompletedAndLeavesInFlight()
    {
        var completedBatch = AiFactExtractionBatch.Create(UserId, "batch_done", "file_in_done");
        var inFlightBatch = AiFactExtractionBatch.Create(UserId, "batch_pending", "file_in_pending");

        var harness = new PollerHarness()
            .WithPendingBatches(completedBatch, inFlightBatch)
            .WithBatchStatus("batch_done", new BatchStatusResult("completed", "file_out_done", null))
            .WithBatchOutput("file_out_done", BatchOutputJsonl("User works night shifts"))
            .WithBatchStatus("batch_pending", new BatchStatusResult("in_progress", null, null));

        await harness.Service.PollPendingBatches(CancellationToken.None);

        completedBatch.Status.Should().Be(AiFactExtractionBatchStatus.Completed);
        inFlightBatch.Status.Should().Be(AiFactExtractionBatchStatus.Submitted);
        harness.AddedFacts.Should().ContainSingle().Which.FactText.Should().Be("User works night shifts");
        await harness.BatchClient.Received(1).DownloadFileAsync("file_out_done", Arg.Any<CancellationToken>());
        await harness.BatchClient.Received(1).DeleteFileAsync("file_in_done", Arg.Any<CancellationToken>());
        await harness.BatchClient.DidNotReceive().DeleteFileAsync("file_in_pending", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_HostedLifecycle_RunsOnePollTickThenStopsGracefully()
    {
        var batch = AiFactExtractionBatch.Create(UserId, "batch_1", "file_in_1");
        var harness = new PollerHarness()
            .WithPendingBatches(batch)
            .WithBatchStatus("batch_1", new BatchStatusResult("completed", "file_out_1", null))
            .WithBatchOutput("file_out_1", BatchOutputJsonl("User works night shifts"));

        var tickSaved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.UnitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(_ => { tickSaved.TrySetResult(); return Task.FromResult(1); });

        var service = harness.Service;

        await service.StartAsync(CancellationToken.None);
        await tickSaved.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await service.StopAsync(CancellationToken.None);

        batch.Status.Should().Be(AiFactExtractionBatchStatus.Completed);
        harness.AddedFacts.Should().ContainSingle();
        service.ExecuteTask.Should().NotBeNull();
        service.ExecuteTask!.IsCompleted.Should().BeTrue();
        service.ExecuteTask!.IsFaulted.Should().BeFalse();
    }

    private sealed class PollerHarness
    {
        public IAiBatchClient BatchClient { get; } = Substitute.For<IAiBatchClient>();
        public IUnitOfWork UnitOfWork { get; } = Substitute.For<IUnitOfWork>();
        public CapturingLogger<OpenAiBatchPollerService> Logger { get; } = new();
        public List<UserFact> AddedFacts { get; } = [];

        private readonly IGenericRepository<AiFactExtractionBatch> _batchRepository =
            Substitute.For<IGenericRepository<AiFactExtractionBatch>>();
        private readonly IGenericRepository<UserFact> _userFactRepository =
            Substitute.For<IGenericRepository<UserFact>>();
        private readonly IAppConfigService _appConfig = Substitute.For<IAppConfigService>();

        public PollerHarness()
        {
            _appConfig.GetAsync(AppConfigKeys.MaxUserFacts, AppConstants.MaxUserFacts, Arg.Any<CancellationToken>())
                .Returns(AppConstants.MaxUserFacts);
            _userFactRepository.FindAsync(Arg.Any<Expression<Func<UserFact, bool>>>(), Arg.Any<CancellationToken>())
                .Returns((IReadOnlyList<UserFact>)[]);
            _batchRepository.FindTrackedAsync(Arg.Any<Expression<Func<AiFactExtractionBatch, bool>>>(), Arg.Any<CancellationToken>())
                .Returns((IReadOnlyList<AiFactExtractionBatch>)[]);
            _userFactRepository.AddAsync(Arg.Do<UserFact>(AddedFacts.Add), Arg.Any<CancellationToken>());
        }

        public PollerHarness WithPendingBatches(params AiFactExtractionBatch[] batches)
        {
            _batchRepository.FindTrackedAsync(Arg.Any<Expression<Func<AiFactExtractionBatch, bool>>>(), Arg.Any<CancellationToken>())
                .Returns((IReadOnlyList<AiFactExtractionBatch>)batches);
            return this;
        }

        public PollerHarness WithExistingFacts(params UserFact[] facts)
        {
            _userFactRepository.FindAsync(Arg.Any<Expression<Func<UserFact, bool>>>(), Arg.Any<CancellationToken>())
                .Returns((IReadOnlyList<UserFact>)facts);
            return this;
        }

        public PollerHarness WithMaxUserFacts(int max)
        {
            _appConfig.GetAsync(AppConfigKeys.MaxUserFacts, AppConstants.MaxUserFacts, Arg.Any<CancellationToken>())
                .Returns(max);
            return this;
        }

        public PollerHarness WithBatchStatus(string batchId, BatchStatusResult status)
        {
            BatchClient.GetBatchAsync(batchId, Arg.Any<CancellationToken>()).Returns(status);
            return this;
        }

        public PollerHarness WithBatchOutput(string fileId, string jsonl)
        {
            BatchClient.DownloadFileAsync(fileId, Arg.Any<CancellationToken>()).Returns(jsonl);
            return this;
        }

        public PollerHarness WithFileDeletionFailure()
        {
            BatchClient.DeleteFileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException(new InvalidOperationException("delete failed")));
            return this;
        }

        public OpenAiBatchPollerService Service
        {
            get
            {
                var provider = new ServiceCollection()
                    .AddSingleton(BatchClient)
                    .AddSingleton(_batchRepository)
                    .AddSingleton(_userFactRepository)
                    .AddSingleton(UnitOfWork)
                    .AddSingleton(_appConfig)
                    .BuildServiceProvider();
                var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
                return new OpenAiBatchPollerService(
                    scopeFactory, Logger,
                    new ConfigurationBuilder().Build(), new MemoryCache(new MemoryCacheOptions()));
            }
        }
    }

    public sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
