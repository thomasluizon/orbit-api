# Persisted broadcast invocation

`LegacyBroadcastPage.json` was serialized by Hangfire 1.8.25 from the unchanged
page job and command sources at `38d2d79505b9836d57b84efc0c0defb700110a63`.
It uses the deployed `Version_180` compatibility level, simple assembly type
names and recommended serializer settings. It was not serialized from the
transition shim. The recipient IDs, content and expiry are synthetic inputs
to the real producer.

To reproduce after building the repository, create a temporary .NET 10 console
project with assembly name `Orbit.Application`. Copy these files with
`git show 38d2d79505b9836d57b84efc0c0defb700110a63:<path>`:

- `src/Orbit.Application/Marketing/Jobs/SendMarketingBroadcastPageJob.cs`
- `src/Orbit.Application/Marketing/Jobs/IMarketingBroadcastDeliveryStore.cs`
- `src/Orbit.Application/Marketing/Jobs/SendMarketingEmailJob.cs`
- `src/Orbit.Application/Marketing/Commands/SendMarketingBroadcastCommand.cs`
- `src/Orbit.Application/Common/MarketingSettings.cs`
- `src/Orbit.Application/Common/LocaleHelper.cs`

Enable implicit usings and nullable reference types. Reference the built
`Orbit.Domain.dll`, the `Microsoft.AspNetCore.App` framework, and packages
`Hangfire.Core` 1.8.25, `MediatR` 14.2.0, `Newtonsoft.Json` 13.0.3 and
`Microsoft.Extensions.Logging.Abstractions` 10.0.14. Use this entry point and run
`dotnet run --project <temporary project> -- <fixture output path>`:

```csharp
using Hangfire;
using Hangfire.Common;
using Hangfire.Storage;
using Orbit.Application.Marketing.Commands;
using Orbit.Application.Marketing.Jobs;

GlobalConfiguration.Configuration.SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer().UseRecommendedSerializerSettings();
var broadcastId = Guid.Parse("19560000-0000-0000-0000-000000000001");
var content = new SendMarketingBroadcastCommand("Subject", "Assunto", "<p>Body</p>", "<p>Corpo</p>", null);
var recipients = new[] {
    new MarketingRecipient(Guid.Parse("19560000-0000-0000-0000-000000000002"), "sent@example.com", "en"),
    new MarketingRecipient(Guid.Parse("19560000-0000-0000-0000-000000000003"), "pending@example.com", "pt-BR"),
    new MarketingRecipient(Guid.Parse("19560000-0000-0000-0000-000000000004"), "other@example.com", "en") };
var expiresAtUtc = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
var job = Job.FromExpression<SendMarketingBroadcastPageJob>(job => job.ExecuteAsync(
    broadcastId, 0, content, recipients, expiresAtUtc, CancellationToken.None));
File.WriteAllText(args[0], InvocationData.SerializeJob(job).SerializePayload());
```

The installed package's `lib/netstandard2.0/Hangfire.Core.dll` was decompiled to
confirm `InvocationData.DeserializeJob` resolves the job type and parameter types
before deserializing arguments. Its NuGet manifest identifies source commit
`1d4778c23410b365b5f9ce35458202efe0b0f3ea`:
[`InvocationData.cs:94`](https://github.com/HangfireIO/Hangfire/blob/1d4778c23410b365b5f9ce35458202efe0b0f3ea/src/Hangfire.Core/Storage/InvocationData.cs#L94)
calls the resolver at line 100, then reads arguments at line 107;
[`CachedDeserializeMethod:331`](https://github.com/HangfireIO/Hangfire/blob/1d4778c23410b365b5f9ce35458202efe0b0f3ea/src/Hangfire.Core/Storage/InvocationData.cs#L331)
resolves the job type at line 339, parameter types at line 341, and the matching
method at line 343. The regression test uses this package's real deserializer.
