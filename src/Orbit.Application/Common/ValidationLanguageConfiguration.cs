using System.Globalization;
using FluentValidation;

namespace Orbit.Application.Common;

public static class ValidationLanguageConfiguration
{
    public static void ConfigureEnglishDefaults()
    {
        ValidatorOptions.Global.LanguageManager.Culture = CultureInfo.GetCultureInfo("en");
    }
}
