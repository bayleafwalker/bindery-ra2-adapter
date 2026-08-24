// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.Sockets;
using System.Text;
using Bindery.Ra2.Adapter;
using Xunit;

namespace Bindery.Ra2.Adapter.Tests;

public sealed class RelayClientTests
{
    private const string AllocationId = "0198c2c3-4d5e-7f60-8123-456789abcdef";
    private const string ClientA = "0198c2c3-4d5e-7f61-8123-456789abcdef";
    private const string ClientB = "0198c2c3-4d5e-7f62-8123-456789abcdef";

    [Fact]
    public void CodecMatchesTheFixedEnvelopeAndRejectsWrongKey()
    {
        byte[] key = Enumerable.Repeat((byte)0x11, RelayV1Protocol.TransportKeyBytes).ToArray();
        RelayPacket packet = new(RelayPacketType.Data, AllocationId, ClientA, ClientB, 7, Encoding.UTF8.GetBytes("opaque"));

        byte[] datagram = RelayV1Protocol.Encode(packet, key);

        Assert.Equal(RelayV1Protocol.HeaderBytes + 6, datagram.Length);
        RelayPacketHeader header = RelayV1Protocol.Peek(datagram);
        Assert.Equal(ClientA, header.SenderId);
        Assert.Equal(ClientB, header.RecipientId);
        Assert.Equal((ulong)7, header.Sequence);
        Assert.Equal(packet.Payload, RelayV1Protocol.Decode(datagram, key).Payload);
        Assert.Throws<RelayProtocolException>(() => RelayV1Protocol.Decode(datagram, Enumerable.Repeat((byte)0x22, RelayV1Protocol.TransportKeyBytes).ToArray()));
    }

    [Fact]
    public async Task TwoClientsExchangeOpaqueDatagramsThroughOneAuthenticatedAllocation()
    {
        byte[] keyA = Enumerable.Repeat((byte)0x11, RelayV1Protocol.TransportKeyBytes).ToArray();
        byte[] keyB = Enumerable.Repeat((byte)0x22, RelayV1Protocol.TransportKeyBytes).ToArray();
        await using LoopbackRelay relay = new(AllocationId, new Dictionary<string, byte[]>
        {
            [ClientA] = keyA,
            [ClientB] = keyB,
        });
        RelayPlacement placement = new("eu-north", "bindery-native", AllocationId, relay.Endpoint.ToString(), "relay-placement/v1");

        await using BinderyRelayClient clientA = await BinderyRelayClient.ConnectAsync(placement, ClientA, ToCredential(keyA));
        await using BinderyRelayClient clientB = await BinderyRelayClient.ConnectAsync(placement, ClientB, ToCredential(keyB));
        relay.Bind(ClientA, clientA.LocalEndpoint);
        relay.Bind(ClientB, clientB.LocalEndpoint);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));

        await clientA.SendAsync(ClientB, Encoding.UTF8.GetBytes("from-a"), timeout.Token);
        await relay.Forwarded.WaitAsync(timeout.Token);
        RelayPacket receivedByB = await clientB.ReceiveAsync(timeout.Token);
        Assert.Equal(ClientA, receivedByB.SenderId);
        Assert.Equal("from-a", Encoding.UTF8.GetString(receivedByB.Payload));
        Assert.Throws<RelayProtocolException>(() => RelayV1Protocol.Decode(relay.LastForwarded!, keyA));

        await clientB.SendAsync(ClientA, Encoding.UTF8.GetBytes("from-b"), timeout.Token);
        RelayPacket receivedByA = await clientA.ReceiveAsync(timeout.Token);
        Assert.Equal(ClientB, receivedByA.SenderId);
        Assert.Equal("from-b", Encoding.UTF8.GetString(receivedByA.Payload));
    }

    private static string ToCredential(byte[] key) => Convert.ToBase64String(key).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class LoopbackRelay : IAsyncDisposable
    {
        private readonly string allocationId;
        private readonly IReadOnlyDictionary<string, byte[]> keys;
        private readonly Dictionary<string, IPEndPoint> endpoints = new(StringComparer.Ordinal);
        private readonly UdpClient socket = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly CancellationTokenSource shutdown = new();
        private readonly Task loop;
        private readonly TaskCompletionSource<byte[]> forwarded = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public LoopbackRelay(string allocationId, IReadOnlyDictionary<string, byte[]> keys)
        {
            this.allocationId = allocationId;
            this.keys = keys;
            loop = RunAsync();
        }

        public IPEndPoint Endpoint => (IPEndPoint)socket.Client.LocalEndPoint!;
        public byte[]? LastForwarded { get; private set; }
        public Task<byte[]> Forwarded => forwarded.Task;

        public void Bind(string clientId, IPEndPoint endpoint) => endpoints[clientId] = endpoint;

        private async Task RunAsync()
        {
            try
            {
                while (true)
                {
                    UdpReceiveResult incoming = await socket.ReceiveAsync(shutdown.Token);
                    RelayPacketHeader header;
                    try { header = RelayV1Protocol.Peek(incoming.Buffer); }
                    catch (RelayProtocolException) { continue; }
                    if (!keys.TryGetValue(header.SenderId, out byte[]? senderKey)) continue;
                    RelayPacket packet;
                    try { packet = RelayV1Protocol.Decode(incoming.Buffer, senderKey); }
                    catch (RelayProtocolException) { continue; }
                    if (!string.Equals(packet.AllocationId, allocationId, StringComparison.Ordinal) || !keys.TryGetValue(packet.RecipientId, out byte[]? recipientKey) || !endpoints.TryGetValue(packet.RecipientId, out IPEndPoint? recipientEndpoint)) continue;
                    byte[] forwarded = RelayV1Protocol.Encode(packet, recipientKey);
                    LastForwarded = forwarded;
                    this.forwarded.TrySetResult(forwarded);
                    await socket.SendAsync(forwarded, recipientEndpoint);
                }
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (shutdown.IsCancellationRequested) { }
            catch (SocketException) when (shutdown.IsCancellationRequested) { }
            catch (Exception exception)
            {
                this.forwarded.TrySetException(exception);
            }
        }

        public async ValueTask DisposeAsync()
        {
            shutdown.Cancel();
            socket.Dispose();
            await loop;
            shutdown.Dispose();
        }
    }
}
