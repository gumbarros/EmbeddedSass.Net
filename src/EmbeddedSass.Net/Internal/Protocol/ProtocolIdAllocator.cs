using EmbeddedSass.Diagnostics;

namespace EmbeddedSass.Internal.Protocol;

internal sealed class ProtocolIdAllocator(uint initialValue = 1)
{
    private readonly Lock _gate = new();
    private uint _next = Normalize(initialValue);

    public uint Rent(Func<uint, bool> isInUse)
    {
        ArgumentNullException.ThrowIfNull(isInUse);

        lock (_gate)
        {
            for (ulong attempt = 0; attempt < uint.MaxValue - 1UL; attempt++)
            {
                uint candidate = _next;
                _next = candidate == uint.MaxValue - 1 ? 1 : candidate + 1;

                if (!isInUse(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new SassProtocolException("No protocol IDs remain available.");
    }

    private static uint Normalize(uint value) =>
        value is 0 or uint.MaxValue ? 1 : value;
}
