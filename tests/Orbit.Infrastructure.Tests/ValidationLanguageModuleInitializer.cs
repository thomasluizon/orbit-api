using System.Runtime.CompilerServices;
using Orbit.Application.Common;

namespace Orbit.Infrastructure.Tests;

internal static class ValidationLanguageModuleInitializer
{
    [ModuleInitializer]
    internal static void Initialize() => ValidationLanguageConfiguration.ConfigureEnglishDefaults();
}
