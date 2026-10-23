using System.Globalization;
using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Orbit.Api.Branding;

namespace Orbit.Infrastructure.Tests.Middleware;

public class BimiLogoTests
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    [Fact]
    public void CommittedLogo_UsesThePortableSecureProfileAndAnOpaqueSquareCanvas()
    {
        var bytes = File.ReadAllBytes(Path.Combine(BrandingDirectory(), "Assets", "orbit-bimi.svg"));
        bytes.Length.Should().BeLessThanOrEqualTo(32000);
        var document = XDocument.Parse(Encoding.UTF8.GetString(bytes));
        document.DocumentType.Should().BeNull();
        document.DescendantNodes().OfType<XProcessingInstruction>().Should().BeEmpty();
        var root = document.Root!;
        root.Name.Should().Be(Svg + "svg");
        root.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration)
            .Select(attribute => attribute.Name.ToString())
            .Should().BeEquivalentTo("version", "baseProfile", "width", "height", "viewBox");
        root.Attribute("version")!.Value.Should().Be("1.2");
        root.Attribute("baseProfile")!.Value.Should().Be("tiny-ps");
        root.Attribute("width")!.Value.Should().Be("1024");
        root.Attribute("height")!.Value.Should().Be("1024");
        root.Attribute("viewBox")!.Value.Should().Be("0 0 1024 1024");
        root.Elements().Select(element => element.Name)
            .Should().Equal(Svg + "title", Svg + "desc", Svg + "rect", Svg + "path", Svg + "path");
        root.Element(Svg + "title")!.Value.Should().Be("Orbit");
        root.Element(Svg + "desc")!.Value.Should().NotBeNullOrWhiteSpace();
        foreach (var text in root.Elements().Take(2))
        {
            text.HasElements.Should().BeFalse();
            text.Attributes().Should().BeEmpty();
        }

        var background = root.Element(Svg + "rect")!;
        background.Attributes().Select(attribute => attribute.Name.ToString())
            .Should().BeEquivalentTo("width", "height", "fill");
        background.HasElements.Should().BeFalse();
        background.Attribute("width")!.Value.Should().Be("1024");
        background.Attribute("height")!.Value.Should().Be("1024");
        background.Attribute("fill")!.Value.Should().Be("#09090B");
        foreach (var path in root.Elements(Svg + "path"))
        {
            path.HasElements.Should().BeFalse();
            path.Attributes().Select(attribute => attribute.Name.ToString())
                .Should().OnlyContain(name => name == "fill" || name == "fill-rule" || name == "transform" || name == "d");
            path.Attribute("d")!.Value.Should().MatchRegex("^[MLCZ0-9. ,+-]+$");
        }

        var embedded = BrandAssets.TryRead("/bimi/orbit.svg");
        embedded.Should().NotBeNull();
        embedded!.Value.Content.Should().Equal(bytes);
        embedded.Value.ContentType.Should().Be("image/svg+xml");
    }

    [Fact]
    public void Logo_PreservesCanonicalGeometryAndCentersThePlatformIconComposition()
    {
        var source = XDocument.Load(Path.Combine(BrandingDirectory(), "Sources", "orbit-mark.svg"));
        var logo = XDocument.Load(Path.Combine(BrandingDirectory(), "Assets", "orbit-bimi.svg"));
        var paths = logo.Root!.Elements(Svg + "path").ToArray();
        paths.Select(path => path.Attribute("d")!.Value)
            .Should().Equal(source.Root!.Elements(Svg + "path").Select(path => path.Attribute("d")!.Value));
        paths.Select(path => path.Attribute("fill-rule")?.Value)
            .Should().Equal(source.Root!.Elements(Svg + "path").Select(path => path.Attribute("fill-rule")?.Value));
        paths.Select(path => path.Attribute("fill")!.Value).Should().Equal("#F4F4F6", "#C4530F");
        var transform = paths[0].Attribute("transform")!.Value;
        paths[1].Attribute("transform")!.Value.Should().Be(transform);
        transform.Should().MatchRegex(@"^translate\(-?[0-9.]+ -?[0-9.]+\) scale\([0-9.]+\)$");
        var values = transform.Replace("translate(", "").Replace(") scale(", " ").TrimEnd(')')
            .Split(' ').Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        var (dx, dy, scale) = (values[0], values[1], values[2]);
        scale.Should().BeGreaterThan(0);
        const double left = 226.5268556772084;
        const double right = 797.8539362998753;
        const double top = 358.6990440706325;
        const double bottom = 665.613481824317;
        (dx + scale * (left + right) / 2).Should().BeApproximately(512, 0.01);
        (dy + scale * (top + bottom) / 2).Should().BeApproximately(512, 0.01);
        (scale * (right - left)).Should().BeApproximately(1024 * 0.6, 0.01);
    }

    private static string BrandingDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Orbit.slnx")))
            directory = directory.Parent;
        return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Cannot locate Orbit.slnx."),
            "src", "Orbit.Api", "Branding");
    }
}
