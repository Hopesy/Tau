using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace Tau.Ai.Providers.Bedrock;

internal readonly record struct BedrockEventStreamMessage(
    string? MessageType,
    string? EventType,
    IReadOnlyDictionary<string, string> Headers,
    byte[] Payload);

internal static class BedrockEventStreamParser
{
    public static async IAsyncEnumerable<BedrockEventStreamMessage> ParseAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var prelude = await ReadExactlyOrNullAsync(stream, 12, cancellationToken).ConfigureAwait(false);
            if (prelude is null)
            {
                yield break;
            }

            var totalLength = BinaryPrimitives.ReadUInt32BigEndian(prelude.AsSpan(0, 4));
            var headersLength = BinaryPrimitives.ReadUInt32BigEndian(prelude.AsSpan(4, 4));
            if (totalLength < 16 || headersLength > totalLength - 16)
            {
                throw new InvalidDataException("Invalid AWS event stream frame length.");
            }

            var restLength = checked((int)totalLength - 12);
            var rest = await ReadExactlyOrThrowAsync(stream, restLength, cancellationToken).ConfigureAwait(false);
            var headersBytes = rest.AsSpan(0, checked((int)headersLength));
            var payloadLength = checked((int)totalLength - 12 - (int)headersLength - 4);
            var payload = rest.AsSpan((int)headersLength, payloadLength).ToArray();
            var headers = ParseHeaders(headersBytes);
            headers.TryGetValue(":message-type", out var messageType);
            headers.TryGetValue(":event-type", out var eventType);
            yield return new BedrockEventStreamMessage(messageType, eventType, headers, payload);
        }
    }

    private static async Task<byte[]?> ReadExactlyOrNullAsync(Stream stream, int count, CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (offset == 0)
                {
                    return null;
                }

                throw new EndOfStreamException("Unexpected end of AWS event stream frame.");
            }

            offset += read;
        }

        return buffer;
    }

    private static async Task<byte[]> ReadExactlyOrThrowAsync(Stream stream, int count, CancellationToken cancellationToken)
    {
        var result = await ReadExactlyOrNullAsync(stream, count, cancellationToken).ConfigureAwait(false);
        return result ?? throw new EndOfStreamException("Unexpected end of AWS event stream frame.");
    }

    private static Dictionary<string, string> ParseHeaders(ReadOnlySpan<byte> bytes)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var offset = 0;
        while (offset < bytes.Length)
        {
            var nameLength = bytes[offset++];
            if (offset + nameLength > bytes.Length)
            {
                throw new InvalidDataException("Invalid AWS event stream header name length.");
            }

            var name = Encoding.UTF8.GetString(bytes.Slice(offset, nameLength));
            offset += nameLength;
            if (offset >= bytes.Length)
            {
                throw new InvalidDataException("Invalid AWS event stream header type.");
            }

            var headerType = bytes[offset++];
            headers[name] = ReadHeaderValue(bytes, ref offset, headerType);
        }

        return headers;
    }

    private static string ReadHeaderValue(ReadOnlySpan<byte> bytes, ref int offset, byte headerType)
    {
        switch (headerType)
        {
            case 0:
                return "true";
            case 1:
                return "false";
            case 2:
                Ensure(bytes, offset, 1);
                return ((sbyte)bytes[offset++]).ToString(System.Globalization.CultureInfo.InvariantCulture);
            case 3:
                Ensure(bytes, offset, 2);
                var int16 = BinaryPrimitives.ReadInt16BigEndian(bytes.Slice(offset, 2));
                offset += 2;
                return int16.ToString(System.Globalization.CultureInfo.InvariantCulture);
            case 4:
                Ensure(bytes, offset, 4);
                var int32 = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(offset, 4));
                offset += 4;
                return int32.ToString(System.Globalization.CultureInfo.InvariantCulture);
            case 5:
            case 8:
                Ensure(bytes, offset, 8);
                var int64 = BinaryPrimitives.ReadInt64BigEndian(bytes.Slice(offset, 8));
                offset += 8;
                return int64.ToString(System.Globalization.CultureInfo.InvariantCulture);
            case 6:
                Ensure(bytes, offset, 2);
                var byteLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset, 2));
                offset += 2;
                Ensure(bytes, offset, byteLength);
                var base64 = Convert.ToBase64String(bytes.Slice(offset, byteLength));
                offset += byteLength;
                return base64;
            case 7:
                Ensure(bytes, offset, 2);
                var stringLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset, 2));
                offset += 2;
                Ensure(bytes, offset, stringLength);
                var value = Encoding.UTF8.GetString(bytes.Slice(offset, stringLength));
                offset += stringLength;
                return value;
            case 9:
                Ensure(bytes, offset, 16);
                var uuidBytes = bytes.Slice(offset, 16).ToArray();
                offset += 16;
                return new Guid(uuidBytes).ToString("D");
            default:
                throw new InvalidDataException($"Unsupported AWS event stream header type: {headerType}.");
        }
    }

    private static void Ensure(ReadOnlySpan<byte> bytes, int offset, int count)
    {
        if (offset + count > bytes.Length)
        {
            throw new InvalidDataException("Invalid AWS event stream header value length.");
        }
    }
}
