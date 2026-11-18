using Orbit.Application.Chat;
using Orbit.ChangeListProbe;

var dryRun = args.Contains("--dry-run", StringComparer.Ordinal);
var shapeArgument = args.SkipWhile(arg => arg != "--shape").Skip(1).FirstOrDefault() ?? "both";
ChangeDeclarationShape[]? shapes = shapeArgument switch
{
    "wrapper" => [ChangeDeclarationShape.Wrapper],
    "kinds" => [ChangeDeclarationShape.Kinds],
    "both" => [ChangeDeclarationShape.Wrapper, ChangeDeclarationShape.Kinds],
    _ => null
};
if (shapes is null)
{
    Console.WriteLine("PROBE_ERROR --shape takes wrapper, kinds or both.");
    return 2;
}
return await ProbeRunner.RunAsync(shapes, dryRun);
