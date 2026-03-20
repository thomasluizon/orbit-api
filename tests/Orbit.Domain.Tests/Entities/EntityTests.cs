using FluentAssertions;
using Orbit.Domain.Common;

namespace Orbit.Domain.Tests.Entities;

public class EntityTests
{
    private class TestEntityA : Entity { }
    private class TestEntityB : Entity { }

    [Fact]
    public void Equals_SameId_ReturnsTrue()
    {
        var id = Guid.NewGuid();
        var a = new TestEntityA { Id = id };
        var b = new TestEntityA { Id = id };

        a.Equals(b).Should().BeTrue();
    }

    [Fact]
    public void Equals_DifferentId_ReturnsFalse()
    {
        var a = new TestEntityA { Id = Guid.NewGuid() };
        var b = new TestEntityA { Id = Guid.NewGuid() };

        a.Equals(b).Should().BeFalse();
    }

    [Fact]
    public void Equals_DifferentType_ReturnsFalse()
    {
        var id = Guid.NewGuid();
        var a = new TestEntityA { Id = id };
        var b = new TestEntityB { Id = id };

        a.Equals(b).Should().BeFalse();
    }

    [Fact]
    public void Equals_EmptyId_ReturnsFalse()
    {
        var a = new TestEntityA { Id = Guid.Empty };
        var b = new TestEntityA { Id = Guid.Empty };

        a.Equals(b).Should().BeFalse();
    }

    [Fact]
    public void Equals_Null_ReturnsFalse()
    {
        var a = new TestEntityA();

        a.Equals(null).Should().BeFalse();
    }

    [Fact]
    public void Equals_SameReference_ReturnsTrue()
    {
        var a = new TestEntityA();

        a.Equals(a).Should().BeTrue();
    }

    [Fact]
    public void GetHashCode_SameId_SameHash()
    {
        var id = Guid.NewGuid();
        var a = new TestEntityA { Id = id };
        var b = new TestEntityA { Id = id };

        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void OperatorEquals_BothNull_ReturnsTrue()
    {
        TestEntityA? a = null;
        TestEntityA? b = null;

        (a == b).Should().BeTrue();
    }

    [Fact]
    public void OperatorNotEquals_DifferentId_ReturnsTrue()
    {
        var a = new TestEntityA { Id = Guid.NewGuid() };
        var b = new TestEntityA { Id = Guid.NewGuid() };

        (a != b).Should().BeTrue();
    }
}
