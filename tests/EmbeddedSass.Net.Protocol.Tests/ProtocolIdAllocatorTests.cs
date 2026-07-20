using EmbeddedSass.Internal.Protocol;

namespace EmbeddedSass.Protocol.Tests;

public sealed class ProtocolIdAllocatorTests
{
    [Fact]
    public void WraparoundSkipsReservedValues()
    {
        var allocator = new ProtocolIdAllocator(uint.MaxValue - 1);

        uint last = allocator.Rent(static _ => false);
        uint wrapped = allocator.Rent(static _ => false);

        Assert.Equal(uint.MaxValue - 1, last);
        Assert.Equal(1u, wrapped);
    }

    [Fact]
    public void SkipsIdsThatAreInUse()
    {
        var allocator = new ProtocolIdAllocator(7);

        uint id = allocator.Rent(candidate => candidate is 7 or 8);

        Assert.Equal(9u, id);
    }
}
