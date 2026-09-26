using System.Reflection;
using FluentAssertions;
using Xunit;

namespace Nocturne.Core.Constants.Tests;

[Trait("Category", "Unit")]
public class DataSourcesTests
{
    public static TheoryData<string, string> Sources()
    {
        var data = new TheoryData<string, string>();
        foreach (var field in typeof(DataSources).GetFields(BindingFlags.Public | BindingFlags.Static)
                     .Where(f => f.IsLiteral && f.FieldType == typeof(string)))
        {
            data.Add(field.Name, (string)field.GetRawConstantValue()!);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public void IsConnector_HoldsExactlyForTheConnectorConstants(string name, string value)
    {
        DataSources.IsConnector(value).Should().Be(name.EndsWith("Connector", StringComparison.Ordinal));
    }

    [Fact]
    public void IsConnector_IsFalseForNull() => DataSources.IsConnector(null).Should().BeFalse();
}
