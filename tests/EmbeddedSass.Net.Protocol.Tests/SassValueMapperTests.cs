using EmbeddedSass.Diagnostics;
using EmbeddedSass.Internal.Protocol;
using EmbeddedSass.Values;
using Sass.EmbeddedProtocol;

namespace EmbeddedSass.Protocol.Tests;

public sealed class SassValueMapperTests
{
    [Fact]
    public void ValuesUseReferenceEquality()
    {
        var first = new SassStringValue("same");
        var second = new SassStringValue("same");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void AccessingArgumentListKeywordsDoesNotChangeHashCode()
    {
        var protocol = new Value
        {
            ArgumentList = new Value.Types.ArgumentList
            {
                Id = 1,
                Keywords =
                {
                    ["color"] = new Value
                    {
                        String = new Value.Types.String { Text = "red" }
                    }
                }
            }
        };
        var argumentList = Assert.IsType<SassArgumentListValue>(
            new SassValueMapper().FromProtocol(protocol));
        int before = argumentList.GetHashCode();

        _ = argumentList.Keywords;

        Assert.Equal(before, argumentList.GetHashCode());
    }

    [Fact]
    public void MapsCompositeProtocolValues()
    {
        var protocol = new Value
        {
            Map = new Value.Types.Map
            {
                Entries =
                {
                    new Value.Types.Map.Types.Entry
                    {
                        Key = new Value
                        {
                            String = new Value.Types.String { Text = "enabled", Quoted = true }
                        },
                        Value = new Value { Singleton = SingletonValue.True }
                    },
                    new Value.Types.Map.Types.Entry
                    {
                        Key = new Value
                        {
                            String = new Value.Types.String { Text = "colors", Quoted = true }
                        },
                        Value = new Value
                        {
                            List = new Value.Types.List
                            {
                                Separator = ListSeparator.Space,
                                HasBrackets = true,
                                Contents =
                                {
                                    new Value
                                    {
                                        Color = new Value.Types.Color
                                        {
                                            Space = "rgb",
                                            Channel1 = 255,
                                            Channel2 = 0,
                                            Channel3 = 0,
                                            Alpha = 0.5
                                        }
                                    },
                                    new Value { Singleton = SingletonValue.Null }
                                }
                            }
                        }
                    }
                }
            }
        };

        var map = Assert.IsType<SassMapValue>(new SassValueMapper().FromProtocol(protocol));

        Assert.Equal(2, map.Entries.Count);
        Assert.True(Assert.IsType<SassBooleanValue>(map.Entries[0].Value).Value);
        var list = Assert.IsType<SassListValue>(map.Entries[1].Value);
        Assert.Equal(SassListSeparator.Space, list.Separator);
        Assert.True(list.HasBrackets);
        var color = Assert.IsType<SassColorValue>(list.Contents[0]);
        Assert.Equal("rgb", color.Space);
        Assert.Equal(0.5, color.Alpha);
        Assert.Same(SassNullValue.Instance, list.Contents[1]);
    }

    [Fact]
    public void MapsCompositeHostValues()
    {
        SassValue host = new SassMapValue(
        [
            new SassMapEntry(
                new SassStringValue("size"),
                new SassListValue(
                [
                    new SassNumberValue(12) { NumeratorUnits = ["px"] },
                    new SassBooleanValue(false)
                ], SassListSeparator.Slash))
        ]);

        Value protocol = new SassValueMapper().ToProtocol(host);

        var entry = Assert.Single(protocol.Map.Entries);
        Assert.Equal("size", entry.Key.String.Text);
        Assert.Equal(ListSeparator.Slash, entry.Value.List.Separator);
        Assert.Equal(12, entry.Value.List.Contents[0].Number.Value);
        Assert.Equal(["px"], entry.Value.List.Contents[0].Number.Numerators);
        Assert.Equal(SingletonValue.False, entry.Value.List.Contents[1].Singleton);
    }

    [Fact]
    public void MapsCalculationInBothDirections()
    {
        var host = new SassCalculationValue(
            "calc",
            [
                new SassCalculationOperation(
                    SassCalculationOperator.Add,
                    new SassCalculationNumber(
                        new SassNumberValue(10) { NumeratorUnits = ["px"] }),
                    new SassCalculationString("var(--gap)"))
            ]);
        var mapper = new SassValueMapper();

        var roundTrip = Assert.IsType<SassCalculationValue>(
            mapper.FromProtocol(mapper.ToProtocol(host)));

        Assert.Equal("calc", roundTrip.Name);
        var operation = Assert.IsType<SassCalculationOperation>(Assert.Single(roundTrip.Arguments));
        Assert.Equal(SassCalculationOperator.Add, operation.Operator);
        Assert.Equal("var(--gap)", Assert.IsType<SassCalculationString>(operation.Right).Value);
    }

    [Fact]
    public void RejectsReservedCompilerArgumentListId()
    {
        var protocol = new Value
        {
            ArgumentList = new Value.Types.ArgumentList { Id = 0 }
        };

        Assert.Throws<SassProtocolException>(() => new SassValueMapper().FromProtocol(protocol));
    }

    [Fact]
    public void RejectsMissingProtocolValue()
    {
        Assert.Throws<SassProtocolException>(() =>
            new SassValueMapper().FromProtocol(new Value()));
    }
}
