using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

namespace Orbit.Analyzers.Tests;

public sealed class ControllerAuthorizationAnalyzerTests
{
    private const string MvcStub = """
        namespace Microsoft.AspNetCore.Mvc
        {
            public abstract class ControllerBase { }

            public sealed class NonActionAttribute : System.Attribute { }
        }

        namespace Microsoft.AspNetCore.Authorization
        {
            public class AuthorizeAttribute : System.Attribute { }

            public sealed class AllowAnonymousAttribute : System.Attribute { }
        }

        """;

    [Fact]
    public Task Flags_Controller_Without_Any_Authorization_Attribute()
    {
        var source = MvcStub + """
            namespace App
            {
                using Microsoft.AspNetCore.Mvc;

                public sealed class {|ORBIT0003:UsersController|} : ControllerBase
                {
                    public string Get() => "users";
                }
            }
            """;

        return VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task Flags_Controller_With_Only_Some_Actions_Attributed()
    {
        var source = MvcStub + """
            namespace App
            {
                using Microsoft.AspNetCore.Authorization;
                using Microsoft.AspNetCore.Mvc;

                public sealed class {|ORBIT0003:BillingController|} : ControllerBase
                {
                    [Authorize]
                    public string GetInvoices() => "invoices";

                    public string GetPrices() => "prices";
                }
            }
            """;

        return VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task Passes_With_ClassLevel_Authorize()
    {
        var source = MvcStub + """
            namespace App
            {
                using Microsoft.AspNetCore.Authorization;
                using Microsoft.AspNetCore.Mvc;

                [Authorize]
                public sealed class UsersController : ControllerBase
                {
                    public string Get() => "users";
                }
            }
            """;

        return VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task Passes_With_ClassLevel_AllowAnonymous()
    {
        var source = MvcStub + """
            namespace App
            {
                using Microsoft.AspNetCore.Authorization;
                using Microsoft.AspNetCore.Mvc;

                [AllowAnonymous]
                public sealed class HealthController : ControllerBase
                {
                    public string Get() => "healthy";
                }
            }
            """;

        return VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task Passes_When_Every_Action_Carries_An_Attribute()
    {
        var source = MvcStub + """
            namespace App
            {
                using Microsoft.AspNetCore.Authorization;
                using Microsoft.AspNetCore.Mvc;

                public sealed class AuthController : ControllerBase
                {
                    [AllowAnonymous]
                    public string SendCode() => "sent";

                    [Authorize]
                    public string LogoutAll() => "done";

                    [NonAction]
                    public string Helper() => "not an action";
                }
            }
            """;

        return VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task Passes_When_Authorize_Is_Inherited_From_Base_Controller()
    {
        var source = MvcStub + """
            namespace App
            {
                using Microsoft.AspNetCore.Authorization;
                using Microsoft.AspNetCore.Mvc;

                [Authorize]
                public abstract class SecureController : ControllerBase
                {
                }

                public sealed class GoalsController : SecureController
                {
                    public string Get() => "goals";
                }
            }
            """;

        return VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task Passes_With_Derived_Authorize_Attribute()
    {
        var source = MvcStub + """
            namespace App
            {
                using Microsoft.AspNetCore.Authorization;
                using Microsoft.AspNetCore.Mvc;

                public sealed class AdminOnlyAttribute : AuthorizeAttribute { }

                [AdminOnly]
                public sealed class AdminController : ControllerBase
                {
                    public string Get() => "admin";
                }
            }
            """;

        return VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task Passes_When_Partial_Declarations_Split_Attribute_And_Actions()
    {
        var source = MvcStub + """
            namespace App
            {
                using Microsoft.AspNetCore.Authorization;
                using Microsoft.AspNetCore.Mvc;

                [Authorize]
                public sealed partial class HabitsController : ControllerBase
                {
                }

                public sealed partial class HabitsController
                {
                    public string Get() => "habits";
                }
            }
            """;

        return VerifyAnalyzerAsync(source);
    }

    [Fact]
    public Task Ignores_Class_Named_Controller_That_Is_Not_An_Mvc_Controller()
    {
        var source = MvcStub + """
            namespace App
            {
                public sealed class SyncFlowController
                {
                    public string Run() => "not mvc";
                }
            }
            """;

        return VerifyAnalyzerAsync(source);
    }

    private static Task VerifyAnalyzerAsync(string source)
    {
        var test = new CSharpAnalyzerTest<ControllerAuthorizationAnalyzer, DefaultVerifier>
        {
            TestCode = source,
            ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
        };

        return test.RunAsync();
    }
}
