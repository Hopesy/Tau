using System.Buffers;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Tau.CodingAgent.Runtime;

/// <summary>严格 CBOR 编解码限制。</summary>
public sealed record CborOptions(
    int MaxByteLength = 16 * 1024 * 1024,
    int MaxContainerLength = 1_000_000,
    int MaxDepth = 64);

/// <summary>CBOR 输入或输出不符合协议子集时抛出的异常。</summary>
public sealed class CborException : Exception
{
    /// <summary>创建 CBOR 异常。</summary>
    /// <param name="message">错误信息。</param>
    public CborException(string message) : base(message) { }
}

/// <summary>
/// 实现参考 protocol 包使用的 RFC 8949 严格子集。
/// 仅支持 definite-length item，不支持 tag、indefinite-length、非有限数字和非字符串 Map key。
/// </summary>
public static class CborCodec
{
    private const long MaxSafeInteger = 9_007_199_254_740_991;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    /// <summary>将 .NET 值编码为严格 CBOR。</summary>
    /// <param name="value">可编码值，支持 JSON 值、字节数组、字典和普通对象。</param>
    /// <param name="options">长度、容器和递归深度限制。</param>
    /// <returns>CBOR 字节。</returns>
    public static byte[] Encode(object? value, CborOptions? options = null)
    {
        var limits = Resolve(options);
        var writer = new LimitedWriter(limits.MaxByteLength);
        EncodeValue(writer, value, limits, 0, new HashSet<object>(ReferenceEqualityComparer.Instance));
        return writer.ToArray();
    }

    /// <summary>解码一个且仅一个严格 CBOR item。</summary>
    /// <param name="bytes">CBOR 字节。</param>
    /// <param name="options">长度、容器和递归深度限制。</param>
    /// <returns>解码后的 .NET 值，Map 为字典、array 为数组。</returns>
    public static object? Decode(ReadOnlySpan<byte> bytes, CborOptions? options = null)
    {
        var limits = Resolve(options);
        if (bytes.Length > limits.MaxByteLength) throw new CborException($"CBOR byte length exceeds configured limit of {limits.MaxByteLength}");
        return new Reader(bytes, limits).ReadDocument();
    }

    /// <summary>兼容参考包命名的编码入口。</summary>
    public static byte[] EncodeCbor(object? value, CborOptions? options = null) => Encode(value, options);

    /// <summary>兼容参考包命名的解码入口。</summary>
    public static object? DecodeCbor(ReadOnlySpan<byte> bytes, CborOptions? options = null) => Decode(bytes, options);

    private static Limits Resolve(CborOptions? options)
    {
        var value = options ?? new CborOptions();
        if (value.MaxByteLength < 0 || value.MaxContainerLength < 0 || value.MaxDepth < 0 || value.MaxDepth > 512)
            throw new ArgumentOutOfRangeException(nameof(options), "CBOR limits must be non-negative and depth must not exceed 512.");
        return new Limits(value.MaxByteLength, value.MaxContainerLength, value.MaxDepth);
    }

    private static void EncodeValue(LimitedWriter writer, object? value, Limits limits, int depth, HashSet<object> ancestors)
    {
        if (depth > limits.MaxDepth) throw new CborException($"CBOR nesting depth exceeds configured limit of {limits.MaxDepth}");
        if (value is null) { writer.Byte(0xf6); return; }
        if (value is JsonElement element) { EncodeJsonElement(writer, element, limits, depth, ancestors); return; }
        switch (value)
        {
            case bool boolean: writer.Byte(boolean ? (byte)0xf5 : (byte)0xf4); return;
            case string text: EncodeText(writer, text, limits); return;
            case byte[] bytes: EncodeBytes(writer, bytes, limits); return;
            case sbyte signed: EncodeInteger(writer, signed, limits); return;
            case byte unsigned: EncodeUnsigned(writer, unsigned, 0); return;
            case short signed: EncodeInteger(writer, signed, limits); return;
            case ushort unsigned: EncodeUnsigned(writer, unsigned, 0); return;
            case int signed: EncodeInteger(writer, signed, limits); return;
            case uint unsigned: EncodeUnsigned(writer, unsigned, 0); return;
            case long signed: EncodeInteger(writer, signed, limits); return;
            case ulong unsigned when unsigned <= MaxSafeInteger: EncodeUnsigned(writer, unsigned, 0); return;
            case ulong: throw new CborException("CBOR integers must be safe integers");
            case float single: EncodeNumber(writer, single, limits); return;
            case double number: EncodeNumber(writer, number, limits); return;
            case decimal decimalValue: EncodeNumber(writer, (double)decimalValue, limits); return;
        }
        if (value is IDictionary<string, object?> stringDictionary)
        {
            EncodeMap(writer, stringDictionary, limits, depth, ancestors); return;
        }
        if (value is System.Collections.IDictionary dictionary)
        {
            var converted = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (System.Collections.DictionaryEntry entry in dictionary)
            {
                if (entry.Key is not string key) throw new CborException("CBOR map keys must be strings");
                if (!converted.TryAdd(key, entry.Value)) throw new CborException("CBOR map contains a duplicate key");
            }
            EncodeMap(writer, converted, limits, depth, ancestors); return;
        }
        if (value is System.Collections.IEnumerable enumerable)
        {
            if (!ancestors.Add(value)) throw new CborException("CBOR values must not contain cycles");
            try
            {
                var values = enumerable.Cast<object?>().ToArray();
                if (values.Length > limits.MaxContainerLength) throw new CborException($"CBOR array length exceeds configured limit of {limits.MaxContainerLength}");
                WriteArgument(writer, 4, values.Length);
                foreach (var item in values) EncodeValue(writer, item, limits, depth + 1, ancestors);
            }
            finally { ancestors.Remove(value); }
            return;
        }
        var properties = value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
            .ToDictionary(property => property.Name, property => property.GetValue(value), StringComparer.Ordinal);
        EncodeMap(writer, properties, limits, depth, ancestors);
    }

    private static void EncodeJsonElement(LimitedWriter writer, JsonElement value, Limits limits, int depth, HashSet<object> ancestors)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null: EncodeValue(writer, null, limits, depth, ancestors); break;
            case JsonValueKind.True: EncodeValue(writer, true, limits, depth, ancestors); break;
            case JsonValueKind.False: EncodeValue(writer, false, limits, depth, ancestors); break;
            case JsonValueKind.String: EncodeValue(writer, value.GetString()!, limits, depth, ancestors); break;
            case JsonValueKind.Number:
                if (value.TryGetInt64(out var integer)) EncodeValue(writer, integer, limits, depth, ancestors);
                else EncodeValue(writer, value.GetDouble(), limits, depth, ancestors);
                break;
            case JsonValueKind.Array:
                var array = value.EnumerateArray().Select(item => (object?)item).ToArray();
                EncodeValue(writer, array, limits, depth, ancestors); break;
            case JsonValueKind.Object:
                var map = value.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value, StringComparer.Ordinal);
                EncodeMap(writer, map, limits, depth, ancestors); break;
            default: throw new CborException("Unsupported JSON value kind");
        }
    }

    private static void EncodeMap(LimitedWriter writer, IEnumerable<KeyValuePair<string, object?>> entries, Limits limits, int depth, HashSet<object> ancestors)
    {
        var values = entries.ToArray();
        if (values.Length > limits.MaxContainerLength) throw new CborException($"CBOR map length exceeds configured limit of {limits.MaxContainerLength}");
        var marker = values;
        if (!ancestors.Add(marker)) throw new CborException("CBOR values must not contain cycles");
        try
        {
            WriteArgument(writer, 5, values.Length);
            foreach (var pair in values) { EncodeText(writer, pair.Key, limits); EncodeValue(writer, pair.Value, limits, depth + 1, ancestors); }
        }
        finally { ancestors.Remove(marker); }
    }

    private static void EncodeText(LimitedWriter writer, string value, Limits limits)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (!char.IsSurrogate(value[index])) continue;
            if (!char.IsHighSurrogate(value[index]) || index + 1 >= value.Length || !char.IsLowSurrogate(value[++index]))
                throw new CborException("CBOR text strings must contain valid Unicode scalar values");
        }
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > limits.MaxByteLength) throw new CborException($"CBOR text string length exceeds configured limit of {limits.MaxByteLength}");
        WriteArgument(writer, 3, bytes.Length); writer.Bytes(bytes);
    }

    private static void EncodeBytes(LimitedWriter writer, byte[] value, Limits limits)
    {
        if (value.Length > limits.MaxByteLength) throw new CborException($"CBOR byte string length exceeds configured limit of {limits.MaxByteLength}");
        WriteArgument(writer, 2, value.Length); writer.Bytes(value);
    }

    private static void EncodeInteger(LimitedWriter writer, long value, Limits limits)
    {
        if (value >= 0) EncodeUnsigned(writer, (ulong)value, 0);
        else EncodeUnsigned(writer, checked((ulong)(-1 - value)), 1);
    }

    private static void EncodeUnsigned(LimitedWriter writer, ulong value, int majorType)
    {
        if (value < 24) writer.Byte((byte)((majorType << 5) | (int)value));
        else if (value <= byte.MaxValue) { writer.Byte((byte)((majorType << 5) | 24)); writer.Byte((byte)value); }
        else if (value <= ushort.MaxValue) { writer.Byte((byte)((majorType << 5) | 25)); writer.UInt16((ushort)value); }
        else if (value <= uint.MaxValue) { writer.Byte((byte)((majorType << 5) | 26)); writer.UInt32((uint)value); }
        else { writer.Byte((byte)((majorType << 5) | 27)); writer.UInt64(value); }
    }

    private static void EncodeNumber(LimitedWriter writer, double value, Limits limits)
    {
        if (!double.IsFinite(value)) throw new CborException("CBOR numbers must be finite");
        var negativeZero = value == 0 && BitConverter.DoubleToInt64Bits(value) < 0;
        if (!negativeZero && value == Math.Truncate(value) && value is >= -MaxSafeInteger and <= MaxSafeInteger) EncodeInteger(writer, (long)value, limits);
        else { writer.Byte(0xfb); writer.Float64(value); }
    }

    private static void WriteArgument(LimitedWriter writer, int majorType, int value) => EncodeUnsigned(writer, (ulong)value, majorType);

    private readonly record struct Limits(int MaxByteLength, int MaxContainerLength, int MaxDepth);

    private sealed class LimitedWriter(int maximum)
    {
        private readonly ArrayBufferWriter<byte> _writer = new(Math.Min(maximum, 256));
        public void Byte(byte value) { Ensure(1); _writer.GetSpan(1)[0] = value; _writer.Advance(1); }
        public void Bytes(ReadOnlySpan<byte> value) { Ensure(value.Length); value.CopyTo(_writer.GetSpan(value.Length)); _writer.Advance(value.Length); }
        public void UInt16(ushort value) { Span<byte> span = stackalloc byte[2]; span[0] = (byte)(value >> 8); span[1] = (byte)value; Bytes(span); }
        public void UInt32(uint value) { Span<byte> span = stackalloc byte[4]; span[0] = (byte)(value >> 24); span[1] = (byte)(value >> 16); span[2] = (byte)(value >> 8); span[3] = (byte)value; Bytes(span); }
        public void UInt64(ulong value) { Span<byte> span = stackalloc byte[8]; for (var i = 7; i >= 0; i--) span[7 - i] = (byte)(value >> (i * 8)); Bytes(span); }
        public void Float64(double value) { Span<byte> span = stackalloc byte[8]; System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(span, BitConverter.DoubleToInt64Bits(value)); Bytes(span); }
        public byte[] ToArray() => _writer.WrittenSpan.ToArray();
        private void Ensure(int count) { if ((long)_writer.WrittenCount + count > maximum) throw new CborException($"CBOR byte length exceeds configured limit of {maximum}"); }
    }

    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _bytes;
        private readonly Limits _limits;
        private int _offset;
        public Reader(ReadOnlySpan<byte> bytes, Limits limits) { _bytes = bytes; _limits = limits; _offset = 0; }
        public object? ReadDocument() { var value = ReadItem(0); if (_offset != _bytes.Length) throw new CborException("CBOR payload contains trailing data"); return value; }
        private object? ReadItem(int depth)
        {
            if (depth > _limits.MaxDepth) throw new CborException($"CBOR nesting depth exceeds configured limit of {_limits.MaxDepth}");
            var initial = ReadByte(); var major = initial >> 5; var info = initial & 0x1f;
            return major switch
            {
                0 => ReadArgument(info),
                1 => checked(-1L - ReadArgument(info)),
                2 => ReadBytes(ReadLength(info, _limits.MaxByteLength, "byte string")).ToArray(),
                3 => DecodeText(ReadBytes(ReadLength(info, _limits.MaxByteLength, "text string"))),
                4 => ReadArray(ReadLength(info, _limits.MaxContainerLength, "array"), depth),
                5 => ReadMap(ReadLength(info, _limits.MaxContainerLength, "map"), depth),
                6 => throw new CborException("CBOR tags are not supported"),
                7 => ReadSimple(info),
                _ => throw new CborException("Malformed CBOR major type")
            };
        }
        private object? ReadSimple(int info) => info switch
        {
            20 => false,
            21 => true,
            22 => null,
            27 => ReadFloat64(),
            31 => throw new CborException("CBOR break marker is not supported"),
            _ => throw new CborException("Unsupported CBOR simple value or floating-point width")
        };
        private string DecodeText(ReadOnlySpan<byte> bytes)
        {
            try
            {
                return StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                throw new CborException("CBOR text string contains invalid UTF-8");
            }
        }
        private double ReadFloat64()
        {
            var span = ReadBytes(8); var value = BitConverter.Int64BitsToDouble(System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(span));
            if (!double.IsFinite(value)) throw new CborException("Decoded CBOR number must be finite");
            if (value == Math.Truncate(value) && BitConverter.DoubleToInt64Bits(value) >= 0 && (value < -MaxSafeInteger || value > MaxSafeInteger))
                throw new CborException("Decoded CBOR integer is outside the safe range");
            return value;
        }
        private object?[] ReadArray(int length, int depth) { var array = new object?[length]; for (var i = 0; i < length; i++) array[i] = ReadItem(depth + 1); return array; }
        private Dictionary<string, object?> ReadMap(int length, int depth)
        {
            var map = new Dictionary<string, object?>(length, StringComparer.Ordinal);
            for (var i = 0; i < length; i++) { var key = ReadItem(depth + 1) as string ?? throw new CborException("CBOR map keys must be strings"); if (!map.TryAdd(key, ReadItem(depth + 1))) throw new CborException("CBOR map contains a duplicate key"); }
            return map;
        }
        private long ReadArgument(int info)
        {
            ulong value = info switch { < 24 => (ulong)info, 24 => ReadByte(), 25 => ReadUInt16(), 26 => ReadUInt32(), 27 => ReadUInt64(), _ => throw new CborException("Indefinite-length CBOR items are not supported") };
            if (value > MaxSafeInteger) throw new CborException("Decoded CBOR integer or length is outside the safe range"); return (long)value;
        }
        private int ReadLength(int info, int limit, string kind) { var value = ReadArgument(info); if (value < 0 || value > limit) throw new CborException($"CBOR {kind} length exceeds configured limit of {limit}"); return checked((int)value); }
        private byte ReadByte() { if (_offset >= _bytes.Length) throw new CborException("Truncated CBOR payload"); return _bytes[_offset++]; }
        private ushort ReadUInt16() { var span = ReadBytes(2); return (ushort)((span[0] << 8) | span[1]); }
        private uint ReadUInt32() { var span = ReadBytes(4); return ((uint)span[0] << 24) | ((uint)span[1] << 16) | ((uint)span[2] << 8) | span[3]; }
        private ulong ReadUInt64() { var span = ReadBytes(8); ulong value = 0; foreach (var item in span) value = (value << 8) | item; return value; }
        private ReadOnlySpan<byte> ReadBytes(int length) { if (length < 0 || length > _bytes.Length - _offset) throw new CborException("Truncated CBOR payload"); var result = _bytes.Slice(_offset, length); _offset += length; return result; }
    }
}

/// <summary>提供与参考 protocol 包一致的静态 CBOR 入口。</summary>
public static class Cbor
{
    /// <summary>编码 CBOR。</summary>
    public static byte[] EncodeCbor(object? value, CborOptions? options = null) => CborCodec.Encode(value, options);
    /// <summary>解码 CBOR。</summary>
    public static object? DecodeCbor(ReadOnlySpan<byte> bytes, CborOptions? options = null) => CborCodec.Decode(bytes, options);
}
