using System.Runtime.CompilerServices;
using Orbit.Application.Common;

namespace Orbit.Application.Tests;

internal static class ValidationLanguageModuleInitializer
{
    [ModuleInitializer]
    internal static void Initialize() => ValidationLanguageConfiguration.ConfigureEnglishDefaults();
}
