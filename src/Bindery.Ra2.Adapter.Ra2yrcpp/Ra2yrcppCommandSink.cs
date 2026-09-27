// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bindery.Ra2.Adapter.Channel;
using Ra2Yrproto.Commands;
using Ra2Yrproto.Ra2Yr;

namespace Bindery.Ra2.Adapter.Ra2yrcpp;

/// <summary>The connected client does not play the seat's house.</summary>
public sealed class Ra2yrcppSeatException(string message) : InvalidOperationException(message);

/// <summary>
/// A seat's orders into its own game client's ra2yrcpp service, bound to one
/// house and failing closed.
/// </summary>
/// <remarks>
/// Before the first order, and again before every order, the sink reads the
/// latest snapshot and checks that the client's local house
/// (<c>current_player</c>) is the seat's house. A client that plays another
/// house is refused for good; one with no local house yet (still loading) is
/// refused until it has one. An observer client is refused by the fork itself
/// (<c>order rejected: local player is an observer</c>).
///
/// Orders name object addresses. An address that the latest snapshot does
/// not show as this house's is dropped, and so is one whose stable ID
/// (<c>Object.unique_id</c>) differs from the <c>unique_ids</c> the command
/// gave for it: that object is gone and the address was reused. An order
/// with no object left is refused before it reaches the game. The order
/// carries the snapshot's IDs (<c>object_unique_ids</c>, and
/// <c>target_unique_id</c> for a target) so the fork rejects it if the
/// objects change before it runs; fork builds without stable IDs send none
/// and ignore the fields.
/// The fork re-checks ownership on the game thread, and an ERROR result is
/// thrown as <see cref="Ra2yrcppCommandException"/>.
///
/// The vocabulary is <see cref="PlayerCommandKinds"/>; an unknown kind is
/// refused.
/// </remarks>
public sealed class Ra2yrcppCommandSink : IPlayerCommandSink, IAsyncDisposable
{
    public const string Deploy = PlayerCommandKinds.Deploy;
    public const string Move = PlayerCommandKinds.Move;
    public const string AttackMove = PlayerCommandKinds.AttackMove;
    public const string Attack = PlayerCommandKinds.Attack;
    public const string Stop = PlayerCommandKinds.Stop;
    public const string Produce = PlayerCommandKinds.Produce;
    public const string PlaceBuilding = PlayerCommandKinds.PlaceBuilding;

    private readonly Uri uri;
    private readonly Ra2yrcppClientOptions? options;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, ObjectTypeClass> types = new(StringComparer.OrdinalIgnoreCase);
    private Ra2yrcppClient? client;
    private string? refusal;

    public Ra2yrcppCommandSink(string house, Ra2YrcppEndpoint endpoint, Ra2yrcppClientOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(house);
        ArgumentNullException.ThrowIfNull(endpoint);
        House = house;
        uri = Ra2yrcppClient.UriFor(endpoint);
        this.options = options;
    }

    public string House { get; }

    /// <summary>The client's service this sink orders, and no other.</summary>
    public Uri Uri => uri;

    /// <summary>Connects and checks the seat's house, without sending an order.</summary>
    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LocalHouseAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SendAsync(PlayerCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            (GameState state, House own) = await LocalHouseAsync(cancellationToken).ConfigureAwait(false);
            JsonElement arguments = command.Arguments;
            switch (command.Kind)
            {
                case Deploy or Stop:
                    await Order(state, own, arguments, command.Kind == Deploy ? UnitAction.Deploy : UnitAction.Stop, cancellationToken).ConfigureAwait(false);
                    break;
                case Move or AttackMove:
                    await Order(state, own, arguments, command.Kind == Move ? UnitAction.Move : UnitAction.AttackMove, cancellationToken, coordinates: Coordinates(arguments)).ConfigureAwait(false);
                    break;
                case Attack:
                    await Order(state, own, arguments, UnitAction.Attack, cancellationToken, target: Address(arguments, "target")).ConfigureAwait(false);
                    break;
                case Produce:
                    await ProduceAsync(arguments, cancellationToken).ConfigureAwait(false);
                    break;
                case PlaceBuilding:
                    uint building = Address(arguments, "object");
                    if (!state.Factories.Any(f => f.Owner == own.Self && f.Object == building && f.Completed))
                        throw new InvalidOperationException($"place_building: {building:x} is not a finished building in a factory of {House}");
                    await Client.RunAsync(new Ra2Yrproto.Commands.PlaceBuilding { Building = new Ra2Yrproto.Ra2Yr.Object { PointerSelf = building }, Coordinates = Coordinates(arguments) }, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new ArgumentException($"unknown command kind {command.Kind}");
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private Ra2yrcppClient Client => client ?? throw new InvalidOperationException("the seat is not connected");

    private async Task Order(GameState state, House own, JsonElement arguments, UnitAction action, CancellationToken cancellationToken, Coordinates? coordinates = null, uint target = 0)
    {
        Dictionary<uint, uint> owned = state.Objects.Where(o => o.PointerHouse == own.Self && !o.InLimbo).GroupBy(static o => o.PointerSelf).ToDictionary(static g => g.Key, static g => g.First().UniqueId);
        uint[] requested = Addresses(arguments).ToArray();
        uint[]? seen = UniqueIds(arguments, requested.Length);
        uint[] addresses = requested
            .Where((address, i) => owned.TryGetValue(address, out uint id) && (seen is null || seen[i] == 0 || id == 0 || seen[i] == id))
            .Distinct()
            .ToArray();
        if (addresses.Length == 0) throw new InvalidOperationException($"{action}: none of the objects is {House}'s in the latest snapshot");
        UnitOrder order = new() { Action = action, TargetObject = target };
        order.ObjectAddresses.AddRange(addresses);
        uint[] ids = addresses.Select(a => owned[a]).ToArray();
        if (ids.All(static id => id != 0)) order.ObjectUniqueIds.AddRange(ids);
        if (target != 0 && state.Objects.FirstOrDefault(o => o.PointerSelf == target && !o.InLimbo) is { UniqueId: not 0 } targeted) order.TargetUniqueId = targeted.UniqueId;
        if (coordinates is not null) order.Coordinates = coordinates;
        await Client.RunAsync(order, cancellationToken).ConfigureAwait(false);
    }

    private async Task ProduceAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        string name = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("type", out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new ArgumentException("produce needs a type");
        ProduceAction action = (arguments.TryGetProperty("action", out JsonElement verb) && verb.ValueKind == JsonValueKind.String ? verb.GetString() : "begin") switch
        {
            "begin" => ProduceAction.Begin,
            "hold" => ProduceAction.Hold,
            "cancel" => ProduceAction.Cancel,
            string other => throw new ArgumentException($"unknown produce action {other}"),
            null => ProduceAction.Begin,
        };
        if (types.Count == 0)
        {
            ReadValue read = await Client.RunAsync(new ReadValue { Data = new StorageValue { InitialGameState = new GameState() } }, cancellationToken).ConfigureAwait(false);
            foreach (ObjectTypeClass type in read.Data?.InitialGameState?.ObjectTypes ?? []) types.TryAdd(type.Name, type);
        }
        if (!types.TryGetValue(name, out ObjectTypeClass? found)) throw new ArgumentException($"unknown object type {name}");
        await Client.RunAsync(new ProduceOrder { ObjectType = found, Action = action }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(GameState State, House Own)> LocalHouseAsync(CancellationToken cancellationToken)
    {
        if (refusal is not null) throw new Ra2yrcppSeatException(refusal);
        if (client is not { IsOpen: true })
        {
            if (client is not null) await client.DisposeAsync().ConfigureAwait(false);
            client = null;
            client = await Ra2yrcppClient.ConnectAsync(uri, options, cancellationToken).ConfigureAwait(false);
        }
        GameState state = (await client.RunAsync(new GetGameState(), cancellationToken).ConfigureAwait(false)).State ?? new GameState();
        House[] local = state.Houses.Where(static h => h.CurrentPlayer).ToArray();
        if (local.Length == 0) throw new InvalidOperationException($"the client at {uri} has no local house yet");
        if (local.Length > 1 || !string.Equals(local[0].Name, House, StringComparison.Ordinal))
        {
            refusal = $"the client at {uri} plays {string.Join(", ", local.Select(static h => h.Name))}, not the seat's house {House}";
            throw new Ra2yrcppSeatException(refusal);
        }
        return (state, local[0]);
    }

    private static IEnumerable<uint> Addresses(JsonElement arguments) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("objects", out JsonElement objects) && objects.ValueKind == JsonValueKind.Array
            ? objects.EnumerateArray().Select(static o => o.TryGetUInt32(out uint address) ? address : throw new ArgumentException("objects are object addresses"))
            : throw new ArgumentException("the order needs objects");

    /// <summary>The stable IDs the controller saw, one per object, or null if it gave none.</summary>
    private static uint[]? UniqueIds(JsonElement arguments, int count)
    {
        if (!arguments.TryGetProperty("unique_ids", out JsonElement ids) || ids.ValueKind == JsonValueKind.Null) return null;
        uint[] values = ids.ValueKind == JsonValueKind.Array
            ? ids.EnumerateArray().Select(static id => id.TryGetUInt32(out uint value) ? value : throw new ArgumentException("unique_ids are stable object IDs")).ToArray()
            : throw new ArgumentException("unique_ids is a list");
        return values.Length == count ? values : throw new ArgumentException("unique_ids needs one ID per object");
    }

    private static uint Address(JsonElement arguments, string field) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(field, out JsonElement value) && value.TryGetUInt32(out uint address)
            ? address
            : throw new ArgumentException($"the order needs {field}");

    private static Coordinates Coordinates(JsonElement arguments)
    {
        static int Read(JsonElement arguments, string field, bool required) =>
            arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(field, out JsonElement value) && value.TryGetInt32(out int number)
                ? number
                : required ? throw new ArgumentException($"the order needs {field}") : 0;
        return new Coordinates { X = Read(arguments, "x", true), Y = Read(arguments, "y", true), Z = Read(arguments, "z", false) };
    }

    public async ValueTask DisposeAsync()
    {
        if (client is not null) await client.DisposeAsync().ConfigureAwait(false);
        gate.Dispose();
    }
}
