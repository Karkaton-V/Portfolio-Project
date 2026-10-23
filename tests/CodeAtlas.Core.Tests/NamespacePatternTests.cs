using CodeAtlas.Core.Rules;

namespace CodeAtlas.Core.Tests;

public class NamespacePatternTests
{
    [Theory]
    [InlineData("Shop.Domain", "Shop.Domain", true)]
    [InlineData("Shop.Domain", "Shop.Domain.Orders", false)]
    [InlineData("Shop.Domain", "ShopXDomain", false)] // the dot is literal, not "any character"
    [InlineData("Shop.Domain.**", "Shop.Domain", true)]
    [InlineData("Shop.Domain.**", "Shop.Domain.Orders.Internal", true)]
    [InlineData("Shop.Domain.**", "Shop.DomainEvents", false)]
    [InlineData("Shop.*.Api", "Shop.Orders.Api", true)]
    [InlineData("Shop.*.Api", "Shop.Orders.Internal.Api", false)]
    [InlineData("Shop.**.Api", "Shop.Orders.Internal.Api", true)]
    [InlineData("**", "", true)]
    [InlineData("**", "Anything.At.All", true)]
    public void Matches_namespaces(string pattern, string @namespace, bool expected)
    {
        Assert.Equal(expected, NamespacePattern.Parse(pattern).Matches(@namespace));
    }

    [Fact]
    public void Rejects_an_empty_pattern()
    {
        Assert.Throws<ArchitectureConfigException>(() => NamespacePattern.Parse(" "));
    }
}
