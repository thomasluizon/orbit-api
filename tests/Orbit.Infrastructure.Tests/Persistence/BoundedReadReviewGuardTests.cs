using FluentAssertions;

namespace Orbit.Infrastructure.Tests.Persistence;

public class BoundedReadReviewGuardTests
{
    [Fact]
    public void Startup_DoesNotAwaitBoundedReadBackfill()
    {
        var pipeline = ReadSource("src/Orbit.Api/Extensions/WebApplicationExtensions.cs");
        pipeline.Should().NotContain("GetRequiredService<BoundedReadStateBackfill>().RunAsync()");
    }

    private static string ReadSource(string path)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Orbit.slnx")))
            directory = directory.Parent;
        return File.ReadAllText(Path.Combine(directory!.FullName, path));
    }
}
