// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Bindery.Ra2.Adapter;

public enum RelayPacketType : byte
{
    Data = 1,
    Register = 2,
    Heartbeat = 3,
}

public sealed record RelayPacket(
    RelayPacketType Type,
    string AllocationId,
    string SenderId,
    string RecipientId,
    ulong Sequence,
    byte[] Payload);

public sealed record RelayPacketHeader(
    RelayPacketType Type,
    string AllocationId,
    string SenderId,
    string RecipientId,
    ulong Sequence,
    int PayloadBytes);

public sealed class RelayProtocolException : FormatException
{
    public RelayProtocolException(string message) : base(message) { }
    public RelayProtocolException(string message, Exception innerException) : base(message, innerException) { }
}

public static class RelayV1Protocol
{
    public const byte Version = 1;
    public const int HeaderBytes = 98;
    public const int DefaultDatagramLimit = 1400;
    public const int TransportKeyBytes = 32;

    public static byte[] Encode(RelayPacket packet, ReadOnlySpan<byte> transportKey, int datagramLimit = DefaultDatagramLimit)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ValidateKey(transportKey);
        ValidatePacketType(packet.Type);
        byte[] allocation = ParseUuid(packet.AllocationId, "allocation id");
        byte[] sender = ParseUuid(packet.SenderId, "sender id");
        byte[] recipient = ParseUuid(packet.RecipientId, "recipient id");
        byte[] payload = packet.Payload ?? throw new RelayProtocolException("payload is required");
        if (payload.Length > ushort.MaxValue) throw new RelayProtocolException("payload exceeds uint16 length");
        if (datagramLimit <= 0) datagramLimit = DefaultDatagramLimit;
        if (HeaderBytes + payload.Length > datagramLimit) throw new RelayProtocolException("relay datagram exceeds configured limit");

        byte[] datagram = new byte[HeaderBytes + payload.Length];
        datagram[0] = (byte)'B';
        datagram[1] = (byte)'R';
        datagram[2] = (byte)'L';
        datagram[3] = (byte)'Y';
        datagram[4] = Version;
        datagram[5] = (byte)packet.Type;
        allocation.CopyTo(datagram, 8);
        sender.CopyTo(datagram, 24);
        recipient.CopyTo(datagram, 40);
        BinaryPrimitives.WriteUInt64BigEndian(datagram.AsSpan(56, 8), packet.Sequence);
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(64, 2), (ushort)payload.Length);
        payload.CopyTo(datagram, HeaderBytes);
        byte[] macInput = new byte[66 + payload.Length];
        datagram.AsSpan(0, 66).CopyTo(macInput);
        payload.CopyTo(macInput, 66);
        HMACSHA256.HashData(transportKey, macInput).CopyTo(datagram, 66);
        return datagram;
    }

    public static RelayPacketHeader Peek(ReadOnlySpan<byte> datagram, int datagramLimit = DefaultDatagramLimit)
    {
        if (datagramLimit <= 0) datagramLimit = DefaultDatagramLimit;
        if (datagram.Length < HeaderBytes) throw new RelayProtocolException("relay datagram is shorter than the fixed header");
        if (datagram.Length > datagramLimit) throw new RelayProtocolException("relay datagram exceeds configured limit");
        if (!datagram[..4].SequenceEqual("BRLY"u8) || datagram[4] != Version) throw new RelayProtocolException("relay magic or version is invalid");
        if (datagram[6] != 0 || datagram[7] != 0) throw new RelayProtocolException("relay flags are reserved and must be zero");
        RelayPacketType type = (RelayPacketType)datagram[5];
        ValidatePacketType(type);
        int payloadBytes = BinaryPrimitives.ReadUInt16BigEndian(datagram.Slice(64, 2));
        if (HeaderBytes + payloadBytes != datagram.Length) throw new RelayProtocolException("relay payload length does not match datagram length");
        return new RelayPacketHeader(
            type,
            FormatUuid(datagram.Slice(8, 16)),
            FormatUuid(datagram.Slice(24, 16)),
            FormatUuid(datagram.Slice(40, 16)),
            BinaryPrimitives.ReadUInt64BigEndian(datagram.Slice(56, 8)),
            payloadBytes);
    }

    public static RelayPacket Decode(ReadOnlySpan<byte> datagram, ReadOnlySpan<byte> transportKey, int datagramLimit = DefaultDatagramLimit)
    {
        ValidateKey(transportKey);
        RelayPacketHeader header = Peek(datagram, datagramLimit);
        byte[] macInput = new byte[66 + header.PayloadBytes];
        datagram[..66].CopyTo(macInput);
        datagram[HeaderBytes..].CopyTo(macInput.AsSpan(66));
        byte[] expected = HMACSHA256.HashData(transportKey, macInput);
        if (!CryptographicOperations.FixedTimeEquals(expected, datagram.Slice(66, 32))) throw new RelayProtocolException("relay datagram authentication failed");
        return new RelayPacket(header.Type, header.AllocationId, header.SenderId, header.RecipientId, header.Sequence, datagram[HeaderBytes..].ToArray());
    }

    internal static byte[] ParseUuid(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 36 || value[8] != '-' || value[13] != '-' || value[18] != '-' || value[23] != '-')
            throw new RelayProtocolException($"{label} must be a canonical UUID");
        try
        {
            return Convert.FromHexString(value.Replace("-", string.Empty, StringComparison.Ordinal));
        }
        catch (FormatException exception)
        {
            throw new RelayProtocolException($"{label} must be a canonical UUID", exception);
        }
    }

    private static string FormatUuid(ReadOnlySpan<byte> value)
    {
        if (value.Length != 16) throw new RelayProtocolException("relay UUID has an invalid length");
        string hex = Convert.ToHexString(value).ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

    private static void ValidateKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != TransportKeyBytes) throw new RelayProtocolException("transport key must be 32 bytes");
    }

    private static void ValidatePacketType(RelayPacketType type)
    {
        if (type is not (RelayPacketType.Data or RelayPacketType.Register or RelayPacketType.Heartbeat))
            throw new RelayProtocolException("unsupported relay packet type");
    }
}
