using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Tau.AgentCore.Harness.Session;

namespace Tau.AgentCore.Tests;

public sealed class UuidV7Tests
{
    private const long Timestamp = 0x0123456789ab;
    private static readonly Regex UuidV7Pattern = new(
        "^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact]
    public void Create_UsesRfc9562LayoutAndPreservesMonotonicOrder()
    {
        var random = new SequenceRandomNumberGenerator(
            [
                [0, 0, 0, 0, 0, 0, 0xff, 0xff, 0xff, 0xfe, 0x01, 0x11, 0x22, 0x33, 0x44, 0x55],
                new byte[16],
                new byte[16]
            ]);
        var generator = new UuidV7Generator(new FixedTimeProvider(Timestamp), random);

        var first = generator.Create();
        var second = generator.Create();
        var third = generator.Create();

        Assert.Equal("01234567-89ab-7fff-bfff-f91122334455", first);
        Assert.Equal("01234567-89ab-7fff-bfff-fc0000000000", second);
        Assert.Equal("01234567-89ac-7000-8000-000000000000", third);
        Assert.Matches(UuidV7Pattern, first);
        Assert.Matches(UuidV7Pattern, second);
        Assert.Matches(UuidV7Pattern, third);
        Assert.Equal(Timestamp, ParseTimestamp(first));
        Assert.Equal(Timestamp, ParseTimestamp(second));
        Assert.Equal(Timestamp + 1, ParseTimestamp(third));
        Assert.True(string.CompareOrdinal(first, second) < 0);
        Assert.True(string.CompareOrdinal(second, third) < 0);
        Assert.Equal(3, random.Calls);
    }

    [Fact]
    public void Create_StaticFactoryReturnsValidUuidV7()
    {
        var value = UuidV7.Create();

        Assert.Matches(UuidV7Pattern, value);
        Assert.True(Guid.TryParse(value, out _));
    }

    private static long ParseTimestamp(string uuid) =>
        Convert.ToInt64(uuid.Replace("-", string.Empty, StringComparison.Ordinal)[..12], 16);

    private sealed class FixedTimeProvider(long timestamp) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            DateTimeOffset.FromUnixTimeMilliseconds(timestamp);
    }

    private sealed class SequenceRandomNumberGenerator(IReadOnlyList<byte[]> values) : RandomNumberGenerator
    {
        private int _index;

        public int Calls { get; private set; }

        public override void GetBytes(byte[] data) =>
            FillNext(data);

        public override void GetBytes(Span<byte> data) =>
            FillNext(data);

        private void FillNext(Span<byte> data)
        {
            data.Clear();
            if (_index < values.Count)
                values[_index++].CopyTo(data);

            Calls++;
        }
    }
}
