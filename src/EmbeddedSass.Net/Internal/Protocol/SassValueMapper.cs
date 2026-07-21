using EmbeddedSass.Diagnostics;
using EmbeddedSass.Values;
using ProtocolValue = Sass.EmbeddedProtocol.Value;

namespace EmbeddedSass.Internal.Protocol;

internal sealed class SassValueMapper
{
    private readonly Dictionary<uint, SassArgumentListValue> _argumentLists = [];
    private readonly HashSet<uint> _compilerFunctions = [];
    private readonly HashSet<uint> _compilerMixins = [];

    public SassValue[] MapArguments(
        Google.Protobuf.Collections.RepeatedField<ProtocolValue> arguments)
    {
        var mapped = new SassValue[arguments.Count];
        for (int index = 0; index < mapped.Length; index++)
        {
            mapped[index] = FromProtocol(arguments[index]);
        }

        return mapped;
    }

    public IEnumerable<uint> AccessedArgumentLists => _argumentLists
        .Where(static pair => pair.Value.KeywordsAccessed && pair.Key != 0)
        .Select(static pair => pair.Key);

    public SassValue FromProtocol(ProtocolValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ValueCase switch
        {
            ProtocolValue.ValueOneofCase.String =>
                new SassStringValue(value.String.Text, value.String.Quoted),
            ProtocolValue.ValueOneofCase.Number => MapNumber(value.Number),
            ProtocolValue.ValueOneofCase.Color => MapColor(value.Color),
            ProtocolValue.ValueOneofCase.List => new SassListValue(
                MapValues(value.List.Contents),
                MapSeparator(value.List.Separator),
                value.List.HasBrackets),
            ProtocolValue.ValueOneofCase.Map => new SassMapValue(
                value.Map.Entries.Select(entry =>
                    new SassMapEntry(FromProtocol(entry.Key), FromProtocol(entry.Value))).ToArray()),
            ProtocolValue.ValueOneofCase.Singleton => value.Singleton switch
            {
                Sass.EmbeddedProtocol.SingletonValue.True => new SassBooleanValue(true),
                Sass.EmbeddedProtocol.SingletonValue.False => new SassBooleanValue(false),
                Sass.EmbeddedProtocol.SingletonValue.Null => SassNullValue.Instance,
                _ => throw new SassProtocolException(
                    $"The compiler sent unknown singleton value {value.Singleton}.")
            },
            ProtocolValue.ValueOneofCase.CompilerFunction =>
                MapCompilerFunction(value.CompilerFunction.Id),
            ProtocolValue.ValueOneofCase.CompilerMixin =>
                MapCompilerMixin(value.CompilerMixin.Id),
            ProtocolValue.ValueOneofCase.ArgumentList => MapArgumentList(value.ArgumentList),
            ProtocolValue.ValueOneofCase.Calculation => MapCalculation(value.Calculation),
            ProtocolValue.ValueOneofCase.HostFunction => throw new SassProtocolException(
                "The compiler sent a host function value, which is forbidden by the protocol."),
            _ => throw new SassProtocolException(
                "The compiler sent a custom-function argument without a value.")
        };
    }

    public ProtocolValue ToProtocol(SassValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value switch
        {
            SassStringValue text => new ProtocolValue
            {
                String = new ProtocolValue.Types.String
                {
                    Text = RequireString(text.Text, nameof(text.Text)),
                    Quoted = text.Quoted
                }
            },
            SassNumberValue number => new ProtocolValue { Number = MapNumber(number) },
            SassColorValue color => new ProtocolValue { Color = MapColor(color) },
            SassListValue list => new ProtocolValue
            {
                List = new ProtocolValue.Types.List
                {
                    Separator = MapSeparator(list.Separator),
                    HasBrackets = list.HasBrackets,
                    Contents = { MapValues(list.Contents) }
                }
            },
            SassMapValue map => new ProtocolValue
            {
                Map = new ProtocolValue.Types.Map
                {
                    Entries = { MapEntries(map.Entries) }
                }
            },
            SassBooleanValue boolean => new ProtocolValue
            {
                Singleton = boolean.Value
                    ? Sass.EmbeddedProtocol.SingletonValue.True
                    : Sass.EmbeddedProtocol.SingletonValue.False
            },
            SassNullValue => new ProtocolValue
            {
                Singleton = Sass.EmbeddedProtocol.SingletonValue.Null
            },
            SassCompilerFunctionValue function => MapCompilerFunction(function),
            SassCompilerMixinValue mixin => MapCompilerMixin(mixin),
            SassArgumentListValue argumentList => MapArgumentList(argumentList),
            SassCalculationValue calculation => new ProtocolValue
            {
                Calculation = MapCalculation(calculation)
            },
            _ => throw new ArgumentException(
                $"Unsupported Sass value type '{value.GetType().FullName}'.",
                nameof(value))
        };
    }

    private static SassNumberValue MapNumber(ProtocolValue.Types.Number number) =>
        new(number.Value)
        {
            NumeratorUnits = Array.AsReadOnly(number.Numerators.ToArray()),
            DenominatorUnits = Array.AsReadOnly(number.Denominators.ToArray())
        };

    private static ProtocolValue.Types.Number MapNumber(SassNumberValue number)
    {
        ArgumentNullException.ThrowIfNull(number.NumeratorUnits);
        ArgumentNullException.ThrowIfNull(number.DenominatorUnits);
        var mapped = new ProtocolValue.Types.Number { Value = number.Value };
        mapped.Numerators.AddRange(MapUnits(number.NumeratorUnits));
        mapped.Denominators.AddRange(MapUnits(number.DenominatorUnits));
        return mapped;
    }

    private static SassColorValue MapColor(ProtocolValue.Types.Color color) =>
        new(
            color.Space,
            color.HasChannel1 ? color.Channel1 : null,
            color.HasChannel2 ? color.Channel2 : null,
            color.HasChannel3 ? color.Channel3 : null,
            color.HasAlpha ? color.Alpha : null);

    private static ProtocolValue.Types.Color MapColor(SassColorValue color)
    {
        var mapped = new ProtocolValue.Types.Color
        {
            Space = RequireString(color.Space, nameof(color.Space))
        };
        if (color.Channel1 is { } channel1) mapped.Channel1 = channel1;
        if (color.Channel2 is { } channel2) mapped.Channel2 = channel2;
        if (color.Channel3 is { } channel3) mapped.Channel3 = channel3;
        if (color.Alpha is { } alpha) mapped.Alpha = alpha;
        return mapped;
    }

    private SassCompilerFunctionValue MapCompilerFunction(uint id)
    {
        _compilerFunctions.Add(id);
        return new SassCompilerFunctionValue(id);
    }

    private SassCompilerMixinValue MapCompilerMixin(uint id)
    {
        _compilerMixins.Add(id);
        return new SassCompilerMixinValue(id);
    }

    private ProtocolValue MapCompilerFunction(SassCompilerFunctionValue function)
    {
        if (!_compilerFunctions.Contains(function.Id))
        {
            throw new ArgumentException(
                "A compiler function may only be returned during the call in which it was received.");
        }

        return new ProtocolValue
        {
            CompilerFunction = new ProtocolValue.Types.CompilerFunction { Id = function.Id }
        };
    }

    private ProtocolValue MapCompilerMixin(SassCompilerMixinValue mixin)
    {
        if (!_compilerMixins.Contains(mixin.Id))
        {
            throw new ArgumentException(
                "A compiler mixin may only be returned during the call in which it was received.");
        }

        return new ProtocolValue
        {
            CompilerMixin = new ProtocolValue.Types.CompilerMixin { Id = mixin.Id }
        };
    }

    private SassArgumentListValue MapArgumentList(ProtocolValue.Types.ArgumentList list)
    {
        if (list.Id == 0)
        {
            throw new SassProtocolException(
                "The compiler used reserved argument-list ID 0.");
        }

        if (_argumentLists.ContainsKey(list.Id))
        {
            throw new SassProtocolException(
                $"The compiler sent duplicate argument-list ID {list.Id}.");
        }

        var keywords = list.Keywords.ToDictionary(
            static pair => pair.Key,
            pair => FromProtocol(pair.Value),
            StringComparer.Ordinal);
        var mapped = new SassArgumentListValue(
            list.Id,
            MapValues(list.Contents),
            MapSeparator(list.Separator),
            keywords);
        _argumentLists.Add(list.Id, mapped);
        return mapped;
    }

    private ProtocolValue MapArgumentList(SassArgumentListValue list)
    {
        if (!_argumentLists.TryGetValue(list.Id, out var original) ||
            !ReferenceEquals(original, list))
        {
            throw new ArgumentException(
                "An argument list may only be returned during the call in which it was received.");
        }

        var mapped = new ProtocolValue.Types.ArgumentList
        {
            Id = list.Id,
            Separator = MapSeparator(list.Separator)
        };
        mapped.Contents.AddRange(MapValues(list.Contents));
        foreach (var pair in list.RawKeywords)
        {
            mapped.Keywords.Add(pair.Key, ToProtocol(pair.Value));
        }

        return new ProtocolValue { ArgumentList = mapped };
    }

    private SassCalculationValue MapCalculation(ProtocolValue.Types.Calculation calculation) =>
        new(
            calculation.Name,
            calculation.Arguments.Select(MapCalculationArgument).ToArray());

    private SassCalculationArgument MapCalculationArgument(
        ProtocolValue.Types.Calculation.Types.CalculationValue value) =>
        value.ValueCase switch
        {
            ProtocolValue.Types.Calculation.Types.CalculationValue.ValueOneofCase.Number =>
                new SassCalculationNumber(MapNumber(value.Number)),
            ProtocolValue.Types.Calculation.Types.CalculationValue.ValueOneofCase.String =>
                new SassCalculationString(value.String),
            ProtocolValue.Types.Calculation.Types.CalculationValue.ValueOneofCase.Interpolation =>
                new SassCalculationString($"({value.Interpolation})"),
            ProtocolValue.Types.Calculation.Types.CalculationValue.ValueOneofCase.Operation =>
                new SassCalculationOperation(
                    MapOperator(value.Operation.Operator),
                    MapCalculationArgument(value.Operation.Left),
                    MapCalculationArgument(value.Operation.Right)),
            ProtocolValue.Types.Calculation.Types.CalculationValue.ValueOneofCase.Calculation =>
                new SassNestedCalculation(MapCalculation(value.Calculation)),
            _ => throw new SassProtocolException(
                "The compiler sent a calculation argument without a value.")
        };

    private ProtocolValue.Types.Calculation MapCalculation(SassCalculationValue calculation)
    {
        ArgumentNullException.ThrowIfNull(calculation.Arguments);
        var mapped = new ProtocolValue.Types.Calculation
        {
            Name = RequireString(calculation.Name, nameof(calculation.Name))
        };
        mapped.Arguments.AddRange(calculation.Arguments.Select(MapCalculationArgument));
        return mapped;
    }

    private ProtocolValue.Types.Calculation.Types.CalculationValue MapCalculationArgument(
        SassCalculationArgument argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        return argument switch
        {
            SassCalculationNumber number => new()
            {
                Number = MapNumber(number.Value)
            },
            SassCalculationString text => new()
            {
                String = RequireString(text.Value, nameof(text.Value))
            },
            SassCalculationOperation operation => new()
            {
                Operation = new ProtocolValue.Types.Calculation.Types.CalculationOperation
                {
                    Operator = MapOperator(operation.Operator),
                    Left = MapCalculationArgument(operation.Left),
                    Right = MapCalculationArgument(operation.Right)
                }
            },
            SassNestedCalculation nested => new()
            {
                Calculation = MapCalculation(nested.Value)
            },
            _ => throw new ArgumentException(
                $"Unsupported calculation argument type '{argument.GetType().FullName}'.",
                nameof(argument))
        };
    }

    private SassValue[] MapValues(
        IEnumerable<ProtocolValue> values) =>
        values.Select(FromProtocol).ToArray();

    private IEnumerable<ProtocolValue> MapValues(IReadOnlyList<SassValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values.Select(ToProtocol);
    }

    private IEnumerable<ProtocolValue.Types.Map.Types.Entry> MapEntries(
        IReadOnlyList<SassMapEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return entries.Select(entry =>
        {
            ArgumentNullException.ThrowIfNull(entry);
            return new ProtocolValue.Types.Map.Types.Entry
            {
                Key = ToProtocol(entry.Key),
                Value = ToProtocol(entry.Value)
            };
        });
    }

    private static SassListSeparator MapSeparator(Sass.EmbeddedProtocol.ListSeparator separator) =>
        separator switch
        {
            Sass.EmbeddedProtocol.ListSeparator.Comma => SassListSeparator.Comma,
            Sass.EmbeddedProtocol.ListSeparator.Space => SassListSeparator.Space,
            Sass.EmbeddedProtocol.ListSeparator.Slash => SassListSeparator.Slash,
            Sass.EmbeddedProtocol.ListSeparator.Undecided => SassListSeparator.Undecided,
            _ => throw new SassProtocolException($"The compiler sent unknown list separator {separator}.")
        };

    private static Sass.EmbeddedProtocol.ListSeparator MapSeparator(SassListSeparator separator) =>
        separator switch
        {
            SassListSeparator.Comma => Sass.EmbeddedProtocol.ListSeparator.Comma,
            SassListSeparator.Space => Sass.EmbeddedProtocol.ListSeparator.Space,
            SassListSeparator.Slash => Sass.EmbeddedProtocol.ListSeparator.Slash,
            SassListSeparator.Undecided => Sass.EmbeddedProtocol.ListSeparator.Undecided,
            _ => throw new ArgumentOutOfRangeException(nameof(separator), separator, "Unknown list separator.")
        };

    private static SassCalculationOperator MapOperator(
        Sass.EmbeddedProtocol.CalculationOperator operation) => operation switch
        {
            Sass.EmbeddedProtocol.CalculationOperator.Plus => SassCalculationOperator.Add,
            Sass.EmbeddedProtocol.CalculationOperator.Minus => SassCalculationOperator.Subtract,
            Sass.EmbeddedProtocol.CalculationOperator.Times => SassCalculationOperator.Multiply,
            Sass.EmbeddedProtocol.CalculationOperator.Divide => SassCalculationOperator.Divide,
            _ => throw new SassProtocolException($"The compiler sent unknown calculation operator {operation}.")
        };

    private static Sass.EmbeddedProtocol.CalculationOperator MapOperator(
        SassCalculationOperator operation) => operation switch
        {
            SassCalculationOperator.Add => Sass.EmbeddedProtocol.CalculationOperator.Plus,
            SassCalculationOperator.Subtract => Sass.EmbeddedProtocol.CalculationOperator.Minus,
            SassCalculationOperator.Multiply => Sass.EmbeddedProtocol.CalculationOperator.Times,
            SassCalculationOperator.Divide => Sass.EmbeddedProtocol.CalculationOperator.Divide,
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown calculation operator.")
        };

    private static IEnumerable<string> MapUnits(IReadOnlyList<string> units)
    {
        foreach (string? unit in units)
        {
            if (string.IsNullOrWhiteSpace(unit))
            {
                throw new ArgumentException("Sass number units cannot be null or whitespace.");
            }

            yield return unit;
        }
    }

    private static string RequireString(string? value, string name)
    {
        ArgumentNullException.ThrowIfNull(value, name);
        return value;
    }
}
