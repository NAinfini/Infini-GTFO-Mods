using System.Text.Json;
using ForgeRuntime.Framework;
using ForgeWeapon.Native;
using Player;

namespace ForgeWeapon.Tests.SupplyFacts;

/// <summary>The two ammunition rows, driven through the real handlers over the game doubles: what each one
/// commits, what it refuses by name, and what it reports when the native body answers something other than what
/// the request asked for.</summary>
public sealed class AmmoSupplyTests
{
    private static readonly object Clamp = new { ammo_type = "class", overflow_policy = "clamp" };
    private static readonly object Reject = new { ammo_type = "class", overflow_policy = "reject" };
    private static readonly object Partial = new { ammo_type = "class", failure_policy = "partial" };
    private static readonly object Strict = new { ammo_type = "class", failure_policy = "reject" };

    private static CommandResult Add(SupplyWorld world, EntityReference reference, object? parameters = null,
        params (string Port, object? Value)[] extra)
    {
        var inputs = new List<(string, object?)> { ("player", reference), ("amount", 20) };
        inputs.AddRange(extra);
        return WeaponSupplyAdapter.HandleAdd(world.Adapter(), world.Context(parameters ?? Clamp, inputs.ToArray()));
    }

    private static CommandResult Consume(SupplyWorld world, EntityReference reference, object? parameters = null,
        params (string Port, object? Value)[] extra)
    {
        var inputs = new List<(string, object?)> { ("player", reference), ("amount", 20) };
        inputs.AddRange(extra);
        return WeaponSupplyAdapter.HandleConsume(world.Adapter(), world.Context(parameters ?? Strict, inputs.ToArray()));
    }

    [Fact]
    public void add_lands_the_rounds_the_pool_reports_and_says_so()
    {
        using var world = new SupplyWorld();
        world.Start();
        var (_, _, storage, reference) = world.PlayerRef(1);
        SupplyWorld.Pool(storage, AmmoType.Class, bullets: 10, cap: 100);

        var result = Add(world, reference);

        Assert.Equal(CommandStatuses.Succeeded, result.Status);
        Assert.Equal(30, PlayerBackpackManager.GetBulletsInPack(AmmoType.Class, PlayerOf(world, reference)));
        var row = SupplyWorld.Row(result);
        Assert.Equal(WeaponSupplyAdapter.CommittedCode, row.GetProperty("code").GetString());
        Assert.Equal(20, row.GetProperty("amount").GetInt32());
        Assert.Equal(reference.Id, row.GetProperty("target").GetProperty("id").GetString());
        // One gift went out, addressed to the recipient the row named, carrying the pool's own fraction.
        var gift = Assert.Single(PlayerBackpackManager.Gifts);
        Assert.Equal(0.2f, gift.Class, 3);
        Assert.Equal(0f, gift.Standard);
    }

    [Fact]
    public void add_clamps_at_the_pools_own_cap_and_reports_what_landed()
    {
        using var world = new SupplyWorld();
        world.Start();
        var (_, _, storage, reference) = world.PlayerRef(1);
        SupplyWorld.Pool(storage, AmmoType.Class, bullets: 95, cap: 100);

        var row = SupplyWorld.Row(Add(world, reference));

        Assert.Equal(5, row.GetProperty("amount").GetInt32());
    }

    [Fact]
    public void add_reject_policy_refuses_an_overflow_without_writing()
    {
        using var world = new SupplyWorld();
        world.Start();
        var (_, _, storage, reference) = world.PlayerRef(1);
        SupplyWorld.Pool(storage, AmmoType.Class, bullets: 95, cap: 100);

        var result = Add(world, reference, Reject);

        Assert.Equal(CommandStatuses.Rejected, result.Status);
        Assert.Equal(WeaponSupplyAdapter.OverflowCode, result.Code);
        Assert.Equal(95, PlayerBackpackManager.GetBulletsInPack(AmmoType.Class, PlayerOf(world, reference)));
        Assert.Empty(PlayerBackpackManager.Gifts);
    }

    [Fact]
    public void add_refuses_invalid_policy_type_and_amount_before_native_write()
    {
        using var world = new SupplyWorld();
        world.Start();
        var (_, _, storage, reference) = world.PlayerRef(1);
        SupplyWorld.Pool(storage, AmmoType.Class, bullets: 10, cap: 100);

        Assert.Equal(WeaponSupplyAdapter.TypeCode, Add(world, reference, new { ammo_type = "nonsense", overflow_policy = "clamp" }).Code);
        Assert.Equal(WeaponSupplyAdapter.PolicyCode, Add(world, reference, new { ammo_type = "class", overflow_policy = "nonsense" }).Code);
        Assert.Equal(WeaponSupplyAdapter.AmountCode, Add(world, reference, Clamp, ("amount", 0)).Code);
        Assert.Equal(WeaponSupplyAdapter.AmountCode, Add(world, reference, Clamp, ("amount", WeaponSupplyAdapter.MaximumAmount + 1)).Code);
        Assert.Empty(PlayerBackpackManager.Gifts);
    }

    [Fact]
    public void add_refuses_a_pool_the_gift_has_no_amount_for()
    {
        using var world = new SupplyWorld();
        world.Start();
        var (_, _, storage, reference) = world.PlayerRef(1);
        SupplyWorld.Pool(storage, AmmoType.ResourcePackRel, bullets: 0, cap: 100);

        var result = Add(world, reference, new { ammo_type = "resource_pack_rel", overflow_policy = "clamp" });

        Assert.Equal(WeaponSupplyAdapter.GiftPoolCode, result.Code);
        Assert.Empty(PlayerBackpackManager.Gifts);
    }

    [Fact]
    public void both_rows_refuse_before_any_native_call_when_this_machine_is_not_the_host()
    {
        using var world = new SupplyWorld();
        world.Start();
        var (_, _, storage, reference) = world.PlayerRef(1);
        SupplyWorld.Pool(storage, AmmoType.Class, bullets: 10, cap: 100);

        SNetwork.SNet.IsMaster = false;
        Assert.Equal(WeaponSupplyAdapter.AuthorityCode, Add(world, reference).Code);
        Assert.Equal(WeaponSupplyAdapter.AuthorityCode, Consume(world, reference).Code);
        SNetwork.SNet.IsMaster = true;
        world.Authoritative = false;
        Assert.Equal(WeaponSupplyAdapter.AuthorityCode, Add(world, reference).Code);
        Assert.Equal(WeaponSupplyAdapter.AuthorityCode, Consume(world, reference).Code);
        Assert.Empty(PlayerBackpackManager.Gifts);
    }

    [Fact]
    public void both_rows_refuse_a_recipient_this_session_cannot_name()
    {
        using var world = new SupplyWorld();
        world.Start();
        var (_, _, storage, reference) = world.PlayerRef(1);
        SupplyWorld.Pool(storage, AmmoType.Class, bullets: 10, cap: 100);

        Assert.Equal(WeaponSupplyAdapter.RecipientCode, Add(world, world.Other("9")).Code);
        world.CanNamePlayers = false;
        Assert.Equal(WeaponSupplyAdapter.RecipientCode, Add(world, reference).Code);
        world.CanNamePlayers = true;
        world.LookupThrows = true;
        Assert.Equal(WeaponSupplyAdapter.RecipientCode, Add(world, reference).Code);
        world.LookupThrows = false;
        var (_, _, _, homeless) = world.PlayerRef(2, withBackpack: false);
        Assert.Equal(WeaponSupplyAdapter.PoolCode, Add(world, homeless).Code);
    }

    [Fact]
    public void add_reports_an_unknown_commit_when_the_gift_throws_and_refuses_an_unreadable_pool()
    {
        using var world = new SupplyWorld();
        world.Start();
        var (_, _, storage, reference) = world.PlayerRef(1);
        SupplyWorld.Pool(storage, AmmoType.Class, bullets: 10, cap: 100);

        // The gift is submitted and then throws: whether it reached the player is not observable, so the commit is
        // unknown and the row never claims a number it did not read back.
        PlayerBackpackManager.ThrowOnGift = new InvalidOperationException("fixture gift failure");
        var thrown = Add(world, reference);
        Assert.Equal(CommandStatuses.Failed, thrown.Status);
        Assert.Equal(WeaponSupplyAdapter.CommitExceptionCode, SupplyWorld.Row(thrown).GetProperty("code").GetString());
        PlayerBackpackManager.ThrowOnGift = null;

        // A pool that cannot be read before the write is a refusal: nothing was submitted.
        PlayerBackpackManager.ThrowOnRead = new InvalidOperationException("fixture read failure");
        var unread = Add(world, reference);
        Assert.Equal(CommandStatuses.Rejected, unread.Status);
        Assert.Equal(WeaponSupplyAdapter.ReadbackCode, unread.Code);
        PlayerBackpackManager.ThrowOnRead = null;
    }

    [Fact]
    public void consume_spends_from_a_pool_this_machine_owns()
    {
        using var world = new SupplyWorld();
        world.Start();
        var (player, _, storage, reference) = world.PlayerRef(1);
        player.IsLocal = true;
        SupplyWorld.Pool(storage, AmmoType.Class, bullets: 40, cap: 100);

        var result = Consume(world, reference);

        Assert.Equal(CommandStatuses.Succeeded, result.Status);
        Assert.Equal(20, PlayerBackpackManager.GetBulletsInPack(AmmoType.Class, PlayerOf(world, reference)));
        Assert.Equal(WeaponSupplyAdapter.CommittedCode, SupplyWorld.Row(result).GetProperty("code").GetString());
        Assert.Equal(20, SupplyWorld.Row(result).GetProperty("amount").GetInt32());
        Assert.Equal((AmmoType.Class, -20), Assert.Single(storage.Writes));
    }

    [Fact]
    public void consume_spends_a_bots_pool_and_clamps_at_zero()
    {
        using var world = new SupplyWorld();
        world.Start();
        var (player, _, storage, reference) = world.PlayerRef(1);
        player.IsBot = true;
        SupplyWorld.Pool(storage, AmmoType.Class, bullets: 5, cap: 100);

        var partial = Consume(world, reference, Partial);
        Assert.Equal(CommandStatuses.Succeeded, partial.Status);
        Assert.Equal(0, PlayerBackpackManager.GetBulletsInPack(AmmoType.Class, PlayerOf(world, reference)));
        Assert.Equal(5, SupplyWorld.Row(partial).GetProperty("amount").GetInt32());

        var empty = Consume(world, reference, Partial);
        Assert.Equal(WeaponSupplyAdapter.EmptyCode, empty.Code);
    }

    [Fact]
    public void consume_reject_policy_refuses_when_the_pool_holds_too_little()
    {
        using var world = new SupplyWorld();
        world.Start();
        var (player, _, storage, reference) = world.PlayerRef(1);
        player.IsLocal = true;
        SupplyWorld.Pool(storage, AmmoType.Class, bullets: 5, cap: 100);

        var result = Consume(world, reference, Strict);

        Assert.Equal(WeaponSupplyAdapter.InsufficientCode, result.Code);
        Assert.Equal(5, PlayerBackpackManager.GetBulletsInPack(AmmoType.Class, PlayerOf(world, reference)));
        Assert.Empty(storage.Writes);
    }

    [Fact]
    public void consume_refuses_a_pool_another_machine_owns()
    {
        using var world = new SupplyWorld();
        world.Start();
        var (_, _, storage, reference) = world.PlayerRef(1);
        SupplyWorld.Pool(storage, AmmoType.Class, bullets: 40, cap: 100);

        var result = Consume(world, reference);

        Assert.Equal(WeaponSupplyAdapter.NotOwnedCode, result.Code);
        Assert.Equal(40, PlayerBackpackManager.GetBulletsInPack(AmmoType.Class, PlayerOf(world, reference)));
        Assert.Empty(storage.Writes);
    }

    [Fact]
    public void consume_refuses_when_the_pool_cannot_be_read_at_all()
    {
        using var world = new SupplyWorld();
        world.Start();
        var (player, _, storage, reference) = world.PlayerRef(1);
        player.IsLocal = true;
        SupplyWorld.Pool(storage, AmmoType.Class, bullets: 40, cap: 100);

        PlayerBackpackManager.ThrowOnRead = new InvalidOperationException("fixture read failure");
        var result = Consume(world, reference);
        Assert.Equal(WeaponSupplyAdapter.ReadbackCode, result.Code);
        PlayerBackpackManager.ThrowOnRead = null;
        Assert.Empty(storage.Writes);
    }

    [Fact]
    public void the_two_rows_declare_the_catalog_shape_and_pair_with_their_handlers()
    {
        var add = (JsonElement)WeaponSupplyContract.AddRow();
        var consume = (JsonElement)WeaponSupplyContract.ConsumeRow();
        Assert.Equal(WeaponSupplyContract.AmmoAddCapability, add.GetProperty("id").GetString());
        Assert.Equal(WeaponSupplyContract.AmmoConsumeCapability, consume.GetProperty("id").GetString());
        Assert.Equal("host", add.GetProperty("graph").GetProperty("execution").GetString());
        Assert.Equal("one", add.GetProperty("graph").GetProperty("recipients").GetProperty("cardinality").GetString());
        // The rows declare the five pools a request can act on; `none` stays in the native name table because it
        // is the index-aligned name of the native member no row offers.
        Assert.Equal(new[] { "standard", "special", "class", "resource_pack_rel", "current_consumable" },
            add.GetProperty("graph").GetProperty("parameters").EnumerateArray().First().GetProperty("values")
                .EnumerateArray().Select(value => value.GetString()).ToArray());

        CommandHandler body = _ => CommandResult.Rejected("never");
        var shapes = WeaponSupplyContract.Shapes(body, body);
        Assert.Equal(new[] { "player", "amount" }, shapes[WeaponSupplyContract.AmmoAddHandler].InputPorts);
        Assert.Equal(new[] { "player", "amount" }, shapes[WeaponSupplyContract.AmmoConsumeHandler].InputPorts);
        Assert.Equal(new[] { "ammo_type", "overflow_policy" }, shapes[WeaponSupplyContract.AmmoAddHandler].ParameterIds);
        Assert.Equal(new[] { "ammo_type", "failure_policy" }, shapes[WeaponSupplyContract.AmmoConsumeHandler].ParameterIds);

        Assert.Equal(2, WeaponSupplyContract.Rows(body, body).Count);
        Assert.Equal(2, WeaponSupplyContract.Bindings(body, body).Count);
        Assert.Equal(2, WeaponSupplyContract.Support(body, body).Count);
        Assert.Equal(2, WeaponSupplyContract.Handlers(body, body).Count);
        // A row nothing answers is not declared at all.
        Assert.Single(WeaponSupplyContract.Rows(body, null));
        Assert.Empty(WeaponSupplyContract.Bindings(null, null));
        Assert.Empty(WeaponSupplyContract.Support(null, null));
    }

    private static SNetwork.SNet_Player PlayerOf(SupplyWorld world, EntityReference reference)
        => (SNetwork.SNet_Player)world.PlayerOf(reference)!;
}
