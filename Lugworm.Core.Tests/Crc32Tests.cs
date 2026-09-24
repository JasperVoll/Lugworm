using System.Text;
using Lugworm.Core.Duet;
using Xunit;

namespace Lugworm.Core.Tests;

public class Crc32Tests
{
    [Fact]
    public void StandardCheckVector()
    {
        // the canonical CRC-32/ISO-HDLC check value
        uint crc = Crc32.Compute(Encoding.ASCII.GetBytes("123456789"));
        Assert.Equal(0xCBF43926u, crc);
    }

    [Fact]
    public void EmptyInput_IsZero()
    {
        Assert.Equal(0u, Crc32.Compute(System.Array.Empty<byte>()));
    }

    [Fact]
    public void KnownStrings()
    {
        Assert.Equal(0x414FA339u, Crc32.Compute(Encoding.ASCII.GetBytes("The quick brown fox jumps over the lazy dog")));
    }

    [Fact]
    public void HexLower_Format()
    {
        Assert.Equal("cbf43926", Crc32.HexLower(0xCBF43926u));
        Assert.Equal("0000000a", Crc32.HexLower(10u));
    }
}
