using PirateChess.Api.Services;

namespace PirateChess.Api.Tests;

public class GzipTextTests
{
    [Theory]
    [InlineData("")]
    [InlineData("hello world")]
    [InlineData("{\"a\":1,\"b\":[1,2,3],\"ü\":\"äöß€\"}")]
    public void CompressDecompress_Roundtrips(string original)
    {
        var restored = GzipText.Decompress(GzipText.Compress(original));
        Assert.Equal(original, restored);
    }

    [Fact]
    public void Compress_ProducesValidBase64()
    {
        var b64 = GzipText.Compress("some text");
        // Wirft, wenn es kein gültiges Base64 ist.
        var bytes = Convert.FromBase64String(b64);
        Assert.NotEmpty(bytes);
    }

    [Fact]
    public void Compress_ShrinksHighlyRepetitiveJson()
    {
        var big = string.Concat(Enumerable.Repeat("{\"line\":\"1. e4 e5 2. Nf3 Nc6\"},", 500));
        var compressed = GzipText.Compress(big);
        Assert.True(compressed.Length < big.Length,
            $"erwartet kleiner: {compressed.Length} vs {big.Length}");
        Assert.Equal(big, GzipText.Decompress(compressed));
    }
}
