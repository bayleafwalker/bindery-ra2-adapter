// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.Sockets;

namespace Bindery.Ra2.Adapter;

public sealed class BinderyRelayClient : IAsyncDisposable
{
    private readonly RelayPlacement placement;
    private readonly string senderId;
    private readonly byte[] transportKey;
    private readonly UdpClient socket;
    private readonly IPEndPoint endpoint;
    private readonly object receiveGate = new();
    private bool receiveInitialized;
    private ulong receiveMaximum;
    private ulong receiveSeen;
    private long sendSequence;
    private int disposed;

    private BinderyRelayClient(RelayPlacement placement, string senderId, byte[] transportKey, UdpClient socket, IPEndPoint endpoint)
    {
        this.placement = placement;
        this.senderId = senderId;
        this.transportKey = transportKey;
        this.socket = socket;
        this.endpoint = endpoint;
    }

    public RelayPlacement Placement => placement;
    public string SenderId => senderId;
    public IPEndPoint Endpoint => endpoint;
    public IPEndPoint LocalEndpoint => (IPEndPoint)socket.Client.LocalEndPoint!;

    public static async Task<BinderyRelayClient> ConnectAsync(
        RelayPlacement placement,
        string senderId,
        string transportCredential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(placement);
        byte[] key = DecodeTransportCredential(transportCredential);
        RelayV1Protocol.ParseUuid(senderId, "sender id");
        IPEndPoint endpoint = await ResolveEndpointAsync(placement.RelayEndpoint, cancellationToken).ConfigureAwait(false);
        UdpClient socket = new(endpoint.AddressFamily);
        socket.Connect(endpoint);
        return new BinderyRelayClient(placement, senderId, key, socket, endpoint);
    }

    public async ValueTask SendAsync(string recipientId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        RelayV1Protocol.ParseUuid(recipientId, "recipient id");
        long next = Interlocked.Increment(ref sendSequence);
        if (next <= 0) throw new RelayProtocolException("relay sequence exhausted");
        byte[] datagram = RelayV1Protocol.Encode(
            new RelayPacket(RelayPacketType.Data, placement.RelayAllocationId, senderId, recipientId, (ulong)next, payload.ToArray()),
            transportKey);
        await socket.SendAsync(datagram, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<RelayPacket> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        UdpReceiveResult received = await socket.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        RelayPacket packet = RelayV1Protocol.Decode(received.Buffer, transportKey);
        if (!string.Equals(packet.AllocationId, placement.RelayAllocationId, StringComparison.Ordinal)) throw new RelayProtocolException("relay allocation does not match placement");
        if (!string.Equals(packet.RecipientId, senderId, StringComparison.Ordinal)) throw new RelayProtocolException("relay recipient does not match this client");
        lock (receiveGate)
        {
            if (!AcceptSequence(packet.Sequence)) throw new RelayProtocolException("relay packet sequence was already observed");
        }
        return packet;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) socket.Dispose();
        return ValueTask.CompletedTask;
    }

    internal static byte[] DecodeTransportCredential(string credential)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credential);
        string padded = credential.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
        try
        {
            byte[] decoded = Convert.FromBase64String(padded);
            if (decoded.Length != RelayV1Protocol.TransportKeyBytes) throw new RelayProtocolException("transport credential must decode to 32 bytes");
            return decoded;
        }
        catch (FormatException exception)
        {
            throw new RelayProtocolException("transport credential is not base64url", exception);
        }
    }

    private static async Task<IPEndPoint> ResolveEndpointAsync(string value, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new RelayProtocolException("relay endpoint is required");
        int separator = value.LastIndexOf(':');
        if (separator <= 0 || separator == value.Length - 1 || !int.TryParse(value[(separator + 1)..], out int port) || port is < 1 or > 65535)
            throw new RelayProtocolException("relay endpoint must be host:port");
        string host = value[..separator].Trim('[', ']');
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        IPAddress? address = addresses.FirstOrDefault(static candidate => candidate.AddressFamily == AddressFamily.InterNetwork)
            ?? addresses.FirstOrDefault();
        return address is null ? throw new RelayProtocolException("relay endpoint host did not resolve") : new IPEndPoint(address, port);
    }

    private bool AcceptSequence(ulong sequence)
    {
        if (!receiveInitialized)
        {
            receiveInitialized = true;
            receiveMaximum = sequence;
            receiveSeen = 1;
            return true;
        }
        if (sequence > receiveMaximum)
        {
            ulong shift = sequence - receiveMaximum;
            receiveSeen = shift >= 64 ? 1 : (receiveSeen << checked((int)shift)) | 1;
            receiveMaximum = sequence;
            return true;
        }
        ulong distance = receiveMaximum - sequence;
        if (distance >= 64) return false;
        ulong bit = 1UL << checked((int)distance);
        if ((receiveSeen & bit) != 0) return false;
        receiveSeen |= bit;
        return true;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
    }
}
