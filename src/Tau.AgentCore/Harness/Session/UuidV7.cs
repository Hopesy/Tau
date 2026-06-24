using System.Security.Cryptography;

namespace Tau.AgentCore.Harness.Session;

public static class UuidV7
{
    private static readonly UuidV7Generator SharedGenerator = new();

    public static string Create() => SharedGenerator.Create();
}

public sealed class UuidV7Generator
{
    private readonly object _gate = new();
    private readonly RandomNumberGenerator? _randomNumberGenerator;
    private readonly TimeProvider _timeProvider;
    private long _lastTimestamp = long.MinValue;
    private uint _sequence;

    public UuidV7Generator()
        : this(TimeProvider.System)
    {
    }

    public UuidV7Generator(TimeProvider timeProvider, RandomNumberGenerator? randomNumberGenerator = null)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _randomNumberGenerator = randomNumberGenerator;
    }

    public string Create()
    {
        Span<byte> random = stackalloc byte[16];
        FillRandomBytes(random);
        var timestamp = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        Span<byte> bytes = stackalloc byte[16];

        lock (_gate)
        {
            if (timestamp > _lastTimestamp)
            {
                _sequence =
                    ((uint)random[6] << 24) |
                    ((uint)random[7] << 16) |
                    ((uint)random[8] << 8) |
                    random[9];
                _lastTimestamp = timestamp;
            }
            else
            {
                _sequence++;
                if (_sequence == 0)
                    _lastTimestamp++;
            }

            bytes[0] = (byte)((_lastTimestamp >> 40) & 0xff);
            bytes[1] = (byte)((_lastTimestamp >> 32) & 0xff);
            bytes[2] = (byte)((_lastTimestamp >> 24) & 0xff);
            bytes[3] = (byte)((_lastTimestamp >> 16) & 0xff);
            bytes[4] = (byte)((_lastTimestamp >> 8) & 0xff);
            bytes[5] = (byte)(_lastTimestamp & 0xff);
            bytes[6] = (byte)(0x70 | ((_sequence >> 28) & 0x0f));
            bytes[7] = (byte)((_sequence >> 20) & 0xff);
            bytes[8] = (byte)(0x80 | ((_sequence >> 14) & 0x3f));
            bytes[9] = (byte)((_sequence >> 6) & 0xff);
            bytes[10] = (byte)(((_sequence & 0x3f) << 2) | ((uint)random[10] & 0x03));
            bytes[11] = random[11];
            bytes[12] = random[12];
            bytes[13] = random[13];
            bytes[14] = random[14];
            bytes[15] = random[15];
        }

        return FormatUuid(bytes);
    }

    private void FillRandomBytes(Span<byte> bytes)
    {
        if (_randomNumberGenerator is null)
        {
            RandomNumberGenerator.Fill(bytes);
            return;
        }

        _randomNumberGenerator.GetBytes(bytes);
    }

    private static string FormatUuid(ReadOnlySpan<byte> bytes)
    {
        var hex = Convert.ToHexString(bytes).ToLowerInvariant();
        return string.Concat(
            hex[..8],
            "-",
            hex.Substring(8, 4),
            "-",
            hex.Substring(12, 4),
            "-",
            hex.Substring(16, 4),
            "-",
            hex[20..]);
    }
}
