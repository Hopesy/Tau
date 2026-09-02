using System.Text;
using System.Text.Json;
using Tau.CodingAgent.Runtime;

namespace Tau.CodingAgent.Tests;

public sealed class CurrentReferenceParityTests
{
    [Fact]
    public void FramedProtocol_UsesBigEndianLength()
    {
        var frame = FramedProtocol.Encode(Encoding.UTF8.GetBytes("abc"));
        Assert.Equal([0, 0, 0, 3], frame[..4]);
        var decoder = new FramedProtocolDecoder();
        Assert.Empty(decoder.Push(frame[..2]));
        Assert.Equal("abc", Encoding.UTF8.GetString(decoder.Push(frame[2..])[0]));
    }

    [Fact]
    public void CborCodec_MatchesDefiniteLengthKnownVector()
    {
        var bytes = CborCodec.Encode(new Dictionary<string, object?> { ["a"] = 1, ["b"] = true });
        Assert.Equal(new byte[] { 0xA2, 0x61, 0x61, 0x01, 0x61, 0x62, 0xF5 }, bytes);
        var decoded = Assert.IsType<Dictionary<string, object?>>(CborCodec.Decode(bytes));
        Assert.Equal(1L, decoded["a"]);
        Assert.Equal(true, decoded["b"]);
    }

    [Fact]
    public void CborCodec_RejectsDuplicateKeysAndTrailingData()
    {
        Assert.Throws<CborException>(() => CborCodec.Decode(new byte[] { 0xA2, 0x61, 0x61, 0x01, 0x61, 0x61, 0x02 }));
        Assert.Throws<CborException>(() => CborCodec.Decode(new byte[] { 0x01, 0x00 }));
        Assert.Throws<CborException>(() => CborCodec.Decode(new byte[] { 0x61, 0xC3 }));
    }

    [Fact]
    public void FrameDecoder_EndRejectsTruncatedPayload()
    {
        var decoder = new FramedProtocolDecoder();
        decoder.Push(new byte[] { 0, 0, 0, 3, 1 });
        Assert.Throws<InvalidDataException>(() => decoder.End());
    }

    [Fact]
    public void ClientMessageDecoder_HandlesFragmentedCborEnvelope()
    {
        var wire = FramedProtocol.EncodeCbor(new Dictionary<string, object?> { ["type"] = "hello", ["version"] = 1 });
        var decoder = new ClientMessageDecoder();
        Assert.Empty(decoder.Push(wire.AsSpan(0, 2)));
        var result = Assert.Single(decoder.Push(wire.AsSpan(2)));
        var message = Assert.IsType<Dictionary<string, object?>>(result);
        Assert.Equal("hello", message["type"]);
    }

    [Fact]
    public void ClientMessageDecoder_RejectsUnknownCommandAndLocksAfterFailure()
    {
        var wire = FramedProtocol.EncodeCbor(new Dictionary<string, object?>
        {
            ["type"] = "request",
            ["id"] = "r1",
            ["request"] = new Dictionary<string, object?> { ["command"] = "unknown" }
        });
        var decoder = new ClientMessageDecoder();
        Assert.Throws<ProtocolValidationException>(() => decoder.Push(wire));
        Assert.Throws<ProtocolValidationException>(() => decoder.End());
    }

    [Fact]
    public void ClientMessageEncoder_RejectsNullOptionalProtocolFields()
    {
        var message = new Dictionary<string, object?>
        {
            ["type"] = "request",
            ["id"] = "r1",
            ["request"] = new Dictionary<string, object?>
            {
                ["command"] = "create",
                ["cwd"] = null
            }
        };
        Assert.Throws<ProtocolValidationException>(() => FramedProtocol.EncodeClientMessage(message));
    }
}
