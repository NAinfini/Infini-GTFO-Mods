using System.Globalization;
using System.Text.Json;
using ForgeRuntime.Framework;
using ForgeWeapon.Native;
using Gear;
using HarmonyLib;
using Player;
using SNetwork;
using UnityEngine;
using Host = ForgeRuntime.Plugin;

namespace ForgeWeapon.Tests.NativeAdapter;

    /// <summary>One case's whole native world. A world owns the kernel it made and the one session over it, and it
    /// outlives nothing: the case disposes it before it returns, on the thread that built it. The kernel and the
    /// session both refuse to be touched from another thread, so a world is never handed to a later case and never
    /// torn down by the shared hook.
    ///
    /// The static state that outlives one case belongs to the process, not to a world — the doubles' backpack
    /// table, the host plugin's mode and runtime, the Harmony patch counters and `SNet.IsMaster` — and
    /// <see cref="Reset"/> restores exactly those, while the per-case data a case cares about (its native objects,
    /// its player references, its recorded reports) starts empty with the world itself.</summary>
    internal sealed class World : IDisposable
    {
        internal const string Consumer = "fixture.consumer";
        internal const string Sink_ = Consumer + ".sink";
        internal const string SinkBinding = Consumer + ".binding.sink";
        internal const string RecordPermission = "fixture.consumer.record";
        /// <summary>The one level this world's fixture plans are mounted on. No mount kind belongs to the kernel
        /// any more, so the world owns the `level` kind itself and answers one identity for it.</summary>
        internal const string LevelReference = "31:A:0";
        private static RuntimeModule LevelMount() => new(RuntimeKernel.ApiVersion, RuntimeJson.From(new
        {
            providers = new[] { new { id = "fixture.level", kind = "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
            capabilities = Array.Empty<object>(), bindings = Array.Empty<object>()
        }).GetRawText(), new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>())
        {
            AttachmentMatchers = new Dictionary<string, AttachmentMatcherRegistration>
            {
                // A level names no event subject, so the kind is judged from the mount target alone.
                ["level"] = AttachmentMatcherRegistration.ByScope((category, reference) =>
                    category == null && reference == LevelReference)
            }
        };
        private static readonly string[] PortTypes = { "execution", "boolean", "integer", "number", "string", "enum", "vector3", "entity", "resource", "handle", "event", "result", "policy" };
        internal RuntimeKernel Kernel { get; } = new(new("fixture.weapon.native", "1.0.0", RuntimeKernel.ApiVersion, "synthetic-no-game"));
        internal WeaponNativeSession? Session => WeaponNativeSession.Current;
        internal readonly List<object> LookupInputs = new();
        /// <summary>The objects the map-object kind was asked with, kept apart from the player lookup's own list
        /// so a case can assert what was handed over without reading another domain's calls.</summary>
        internal readonly List<object> MapObjectLookupInputs = new();
        internal readonly Dictionary<SNet_Player, EntityReference> PlayerRefs = new();
        internal readonly HashSet<EntityReference> LivePlayers = new();
        internal readonly Dictionary<Enemies.EnemyAgent, EntityReference> EnemyRefs = new();
        internal readonly HashSet<EntityReference> LiveEnemies = new();
        /// <summary>The map objects this world's map-object kind answers for, keyed by the object a hit is
        /// resolved against. A map object is not this package's kind, so the fixture stands in for the domain
        /// that owns it: the hit path only ever hands the lookup the collider it holds.</summary>
        internal readonly Dictionary<UnityEngine.Component, EntityReference> MapObjectRefs = new();
        internal readonly HashSet<EntityReference> LiveMapObjects = new();
        internal readonly List<(string EventId, EntityReference Target, EntityReference Actor)> Sink = new();
        internal readonly List<string> Reports = new(), Infos = new();
        internal int Installs, Removes;
        internal bool CanExecute = true;
        internal Action? DuringInstall;
        /// <summary>The session this world started, if it started one. A world never disposes a session it did not
        /// create, which is what keeps a case from tearing down another case's native bindings.</summary>
        internal WeaponNativeSession? Started { get; private set; }
        /// <summary>Where this world's gear-part files are read from: a private directory holding no package of its
        /// own until the case places one, so a world with no authored parts never reads another fixture's files.</summary>
        internal string GearPartsRoot { get; } = Path.Combine(Path.GetTempPath(), "forge-weapon-parts", Guid.NewGuid().ToString("N"));
        private readonly RuntimeModuleHandle players, enemies, consumer, level, mapObjects;
        private long tick;
        private bool disposed;

        internal sealed record Fixture(SNet_Player Net, PlayerAgent Agent, PlayerInventoryBase Inventory, PlayerBackpack Backpack, EntityReference Reference);
        internal sealed record EnemyFixture(Enemies.EnemyAgent Agent, EntityReference Reference);

        internal World(bool start = true, bool playerLookup = true)
        {
            SNet.IsMaster = true;
            Kernel.BeginWorld(7);
            // The host registers the runtime's own contract providers before any domain package; every binding
            // this world's session registers names a Trigger capability declared by the trigger contract.
            Kernel.RegisterModule(TriggerContracts.Module(), RuntimeLogLevel.Off);
            // Stand-in for ForgeMap's gtfo.player surface: the resolver plus, unless disabled, the SNet_Player instance lookup.
            players = Kernel.RegisterModule(new RuntimeModule(RuntimeKernel.ApiVersion, RuntimeJson.From(new
            {
                providers = new[] { new { id = "fixture.players", kind = "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
                capabilities = Array.Empty<object>(), bindings = Array.Empty<object>()
            }).GetRawText(), new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>(),
                new Dictionary<string, Func<EntityReference, bool>> { [EquipmentNativeAdapter.PlayerKind] = r => LivePlayers.Contains(r) })
            {
                EntityInstanceResolvers = playerLookup ? new Dictionary<string, Func<object, EntityReference?>>
                {
                    [EquipmentNativeAdapter.PlayerKind] = instance =>
                    {
                        LookupInputs.Add(instance);
                        return instance is SNet_Player player && PlayerRefs.TryGetValue(player, out var reference) ? reference : null;
                    }
                } : null
            }, RuntimeLogLevel.Off);
            // Stand-in for ForgeEnemy's gtfo.enemy surface: the agent-keyed resolver plus the namespace resolver,
            // so a hit target is answered exactly the way the enemy domain answers it in the game.
            enemies = Kernel.RegisterModule(new RuntimeModule(RuntimeKernel.ApiVersion, RuntimeJson.From(new
            {
                providers = new[] { new { id = "fixture.enemies", kind = "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
                capabilities = Array.Empty<object>(), bindings = Array.Empty<object>()
            }).GetRawText(), new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>(),
                new Dictionary<string, Func<EntityReference, bool>> { [EquipmentNativeAdapter.EnemyKind] = r => LiveEnemies.Contains(r) })
            {
                EntityInstanceResolvers = new Dictionary<string, Func<object, EntityReference?>>
                {
                    [EquipmentNativeAdapter.EnemyKind] = instance =>
                        instance is Enemies.EnemyAgent agent && EnemyRefs.TryGetValue(agent, out var reference) ? reference : null
                }
            }, RuntimeLogLevel.Off);
            consumer = Kernel.RegisterModule(ConsumerModule(), RuntimeLogLevel.Off);
            level = Kernel.RegisterModule(LevelMount(), RuntimeLogLevel.Off);
            // Stand-in for ForgeMap's `gtfo.map_object` surface: the map domain answers for the objects a hit
            // collider belongs to, and it climbs the hierarchy exactly as its own hit reader does — the object a
            // hit names is reached through the component above it, and a component with no map object above it is
            // not this kind's. Weapon asks this kind by name only and never reads a door or a terminal type.
            mapObjects = Kernel.RegisterModule(new RuntimeModule(RuntimeKernel.ApiVersion, RuntimeJson.From(new
            {
                providers = new[] { new { id = "fixture.mapobjects", kind = "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
                capabilities = Array.Empty<object>(), bindings = Array.Empty<object>()
            }).GetRawText(), new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>(),
                new Dictionary<string, Func<EntityReference, bool>> { [EquipmentNativeAdapter.MapObjectKind] = r => LiveMapObjects.Contains(r) })
            {
                EntityInstanceResolvers = new Dictionary<string, Func<object, EntityReference?>>
                {
                    [EquipmentNativeAdapter.MapObjectKind] = instance =>
                    {
                        MapObjectLookupInputs.Add(instance);
                        return Climb(instance is UnityEngine.Component component ? component : null) is { } mapObject
                            && MapObjectRefs.TryGetValue(mapObject, out var reference) ? reference : null;
                    }
                }
            }, RuntimeLogLevel.Off);
            if (!start) return;
            StartSession();
            StartRuntime();
        }

        /// <summary>Starts this world's one session. The session belongs to the simulation thread that made it and
        /// refuses to be touched anywhere else, so it is created in the case body and disposed by the same case;
        /// nothing else may start one while it is current.</summary>
        internal WeaponNativeSession StartSession(Action? install = null, Action? remove = null, string? gearPartsRoot = null)
        {
            var session = WeaponNativeSession.Start(Kernel, RuntimeLogLevel.Off, () => CanExecute, Reports.Add, Infos.Add,
                install ?? (() => { Installs++; DuringInstall?.Invoke(); }), remove ?? (() => Removes++),
                gearPartsRoot ?? GearPartsRoot);
            Started = session;
            return session;
        }

        internal void StartRuntime(string? document = null)
        {
            var plan = document ?? Plan();
            Kernel.StartRuntime(() => { Kernel.LoadPlan(plan); MountPublishers(); });
            Kernel.Advance(0, true);
        }

        /// <summary>
        /// Subscribes the weapon provider's other publishing rows, so the facts this suite asserts on are built
        /// at all. The gate a publisher reads answers "is anybody listening", and this world's default plan
        /// subscribes to the two wield rows only, so every other row of the package is skipped before its native
        /// read: a case asserting `Adapter.Published` would otherwise be asserting on a fact that was never made.
        ///
        /// One plan per row, in the shape of this fixture's own plan: the level mount kind this world owns with a
        /// reference that names no level it runs, so the plan claims nothing and no step of it ever runs, plus one
        /// sink step, which is the only step a plan can carry without a handler of its own. The gate is what this
        /// adds; nothing else about the row's behaviour changes.
        /// </summary>
        private void MountPublishers()
        {
            var registry = RuntimeJson.Parse(Kernel.ExportManifest()).GetProperty("registry");
            var kinds = registry.GetProperty("capabilities").EnumerateArray()
                .ToDictionary(c => c.GetProperty("id").GetString()!, c => c.GetProperty("kind").GetString()!, StringComparer.Ordinal);
            // Only a row whose capability is a trigger can carry an entrypoint: the other roles are answered by an
            // evaluator or executed by a handler, and a plan subscribes to none of them.
            var rows = registry.GetProperty("bindings").EnumerateArray()
                .Where(b => b.GetProperty("providerId").GetString() == ModuleDefinition.ProviderId
                    && kinds[b.GetProperty("capabilityId").GetString()!] == "trigger")
                .Select(b => b.GetProperty("id").GetString()!).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            if (rows.Length == 0) throw new InvalidOperationException("The weapon provider declared no trigger binding.");
            var sink = Graph(Sink_);
            // The permissions a plan may hold are exactly the ones its pinned rows require, as the registry
            // declares them: the rows of this package carry three different read permissions, so the set is read
            // from the support table rather than written into the fixture.
            var support = RuntimeJson.Parse(Kernel.ExportManifest()).GetProperty("bindingSupport").EnumerateArray()
                .ToDictionary(s => s.GetProperty("bindingId").GetString()!,
                    s => s.GetProperty("requiredPermissions").EnumerateArray().Select(p => p.GetString()!).ToArray(), StringComparer.Ordinal);
            // A row whose own ports cannot feed the sink's recipient has no plan this fixture could dispatch:
            // the sink's recipient is the one input the action contract requires, and a trigger that carries no
            // non-nullable entity publishes a fact this world's own plan cannot consume either. The despawn row is
            // the one ending fact whose subject is its own `entity` port, so that name is a source like the others:
            // without it no plan is mounted on the despawn row and the ending is never built at all.
            var dispatchable = rows.Where(id => Wiring(sink, GraphOf(id)).ContainsKey(Slot(sink.GetProperty("inputs"), "target"))).ToArray();
            var plans = dispatchable.Select(id => (object)new
            {
                schemaVersion = 1, kind = "forge-runtime-plan", planId = "fixture.weapon.gate." + id,
                resource = new { id = "fixture.resource", revision = "r1" },
                runtime = Kernel.Identity, domain = "weapon", authority = "host", failurePolicy = "stop-entrypoint",
                permissions = support[SinkBinding].Concat(support[id]).Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToArray(),
                dependencies = Array.Empty<string>(),
                // A reference that parses as a level and is not the one this world runs: the mount is judged and
                // answers false, so the gate opens and nothing is ever claimed through it. Every case that means to
                // read an event through the sink mounts its own plan; a gate plan that claimed would run the sink
                // for events the cases about unpublished facts assert never reached anybody.
                attachments = new object[] { new { kind = "level", reference = "1:A:0" } },
                limits = new { maxEventsPerTick = 16, maxCommandsPerTick = 16, maxQueuedEvents = 16, maxCausalDepth = 4 },
                bindings = new[] { Pinned(SinkBinding, Sink_), Pinned(id) },
                entrypoints = new[] { new
                {
                    nodeId = "Entry", binding = 1, layout = Resolved(GraphOf(id), 0), start = 0,
                    steps = new[] { new { nodeId = "Entry_sink", nodeKind = "action", binding = 0, layout = Resolved(SinkContract(), 0),
                        inputs = Wiring(sink, GraphOf(id)).Select(wire => (object)new { slot = wire.Key, fromEventSlot = wire.Value }).ToArray(),
                        successors = Array.Empty<int?>() } }
                } }
            }).ToArray();
            var candidates = plans.Select(json => (Json: RuntimeJson.From(json).GetRawText(), Id: (string)json.GetType().GetProperty("planId")!.GetValue(json)!)).ToArray();
            var refused = Kernel.LoadPlans(candidates.Select(candidate => PlanCandidate.Loaded(candidate.Id + ".plan.json", candidate.Json)).ToArray())
                .Where(outcome => !outcome.Loaded).ToArray();
            if (refused.Length != 0) throw new RuntimeContractException(refused[0].Code!, refused[0].Code + ": " + refused[0].Detail
                + " [" + refused[0].Path + "]");
        }

        /// <summary>One binding lock for a binding this world's own providers declare: the capability, provider
        /// and handler are read back out of the registered row rather than pinned here, so a renamed row fails
        /// the manifest read instead of loading a plan that names a contract nothing implements. The sink is the
        /// one row whose capability is not the provider's own, so it is the one caller that names it.</summary>
        private object Pinned(string bindingId, string? capabilityId = null)
        {
            var row = Row(bindingId);
            var id = capabilityId ?? row.GetProperty("capabilityId").GetString()!;
            return new
            {
                bindingId, capabilityId = id, capabilityVersion = CapabilityVersion(id),
                providerId = row.GetProperty("providerId").GetString(), providerVersion = ProviderVersion(row.GetProperty("providerId").GetString()!),
                handler = row.GetProperty("handler").GetString()!
            };
        }

        /// <summary>One registered provider's version, read from the manifest for the same reason the capability
        /// version is: a binding row names its provider but does not repeat the provider's version, and a plan
        /// lock has to carry the version the registry declares.</summary>
        private string ProviderVersion(string providerId)
            => RuntimeJson.Parse(Kernel.ExportManifest()).GetProperty("registry").GetProperty("providers")
                .EnumerateArray().Single(p => p.GetProperty("id").GetString() == providerId).GetProperty("version").GetString()!;

        /// <summary>One registered binding row, read from the manifest the kernel exports. This is the same
        /// source `CapabilityVersion` reads, so a plan lock and the row it locks cannot drift apart.</summary>
        private JsonElement Row(string bindingId)
            => RuntimeJson.Parse(Kernel.ExportManifest()).GetProperty("registry").GetProperty("bindings")
                .EnumerateArray().Single(b => b.GetProperty("id").GetString() == bindingId);

        private JsonElement GraphOf(string bindingId) => Row(bindingId).GetProperty("capabilityId").GetString() is var id
            ? Kernel.ResolveGraphContract(id, CapabilityVersion(id), RuntimeJson.EmptyObject) : default;

        /// <summary>The sink's own contract, resolved the way the loader resolves the entry's: through the
        /// registered row and the kernel, never through a restated shape.</summary>
        private JsonElement SinkContract() => Kernel.ResolveGraphContract(Sink_, CapabilityVersion(Sink_), RuntimeJson.EmptyObject);

        /// <summary>The sink's inputs, wired from the trigger's own ports where the row has them, keyed by the
        /// sink's own input slot. The sink's recipient input is required and non-nullable by contract, so only a
        /// trigger port that is neither may feed it — an optional event port cannot be read by a step that always
        /// runs; which port feeds `target` is the first of the row's own that the order below accepts. An empty
        /// table is a row this fixture cannot dispatch into the sink.</summary>
        private static Dictionary<int, int> Wiring(JsonElement sink, JsonElement trigger)
        {
            var sources = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["target"] = new[] { "target", "equipment", "instance", "item", "deployed", "entity", "actor" },
                ["actor"] = new[] { "actor" }
            };
            var offered = trigger.GetProperty("outputs").EnumerateArray()
                .Where(port => port.GetProperty("type").GetString() == "entity"
                    && !(port.TryGetProperty("nullable", out var nullable) && nullable.GetBoolean())
                    && !(port.TryGetProperty("optional", out var optional) && optional.GetBoolean()))
                .Select(port => port.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal);
            var wiring = new Dictionary<int, int>();
            foreach (var input in sink.GetProperty("inputs").EnumerateArray())
            {
                var name = input.GetProperty("id").GetString()!;
                if (!sources.TryGetValue(name, out var candidates)) continue;
                var port = candidates.FirstOrDefault(offered.Contains);
                if (port == null) continue;
                wiring[Slot(sink.GetProperty("inputs"), name)] = Slot(trigger.GetProperty("outputs"), port);
            }
            return wiring;
        }

        internal void Tick(bool host = true) => Kernel.Advance(++tick, host);

        /// <summary>Writes one package's file exactly as an author's package would carry it: the package directory
        /// is one level under plugins/ and the file is named after the block.</summary>
        internal void Place(string package, uint blockId, string json)
            => Place(GearPartsRoot, package, blockId.ToString(CultureInfo.InvariantCulture) + ".json", json);

        /// <summary>Writes a file for a world, so a case can put one in another package's directory or under a name
        /// that is not a block id at all.</summary>
        internal void Place(string package, string fileName, string json) => Place(GearPartsRoot, package, fileName, json);

        internal static void Place(string root, string package, string fileName, string json)
        {
            var directory = Path.Combine(root, "plugins", package, "forge", "gear-parts");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, fileName), json);
        }

        /// <summary>The one holder shape the game hands to the presentation hook: the gear's own offline record plus
        /// the part objects. Only the fields a case uses exist here.</summary>
        internal static GearPartHolder Holder(uint blockId, params (GearPartSlot Slot, UnityEngine.GameObject Part)[] parts)
        {
            var gear = Gear.GearIDRange.Create();
            gear.PlayfabItemInstanceId = "OfflineGear_ID_" + blockId.ToString(CultureInfo.InvariantCulture);
            var holder = new GearPartHolder { GearIDRange = gear };
            foreach (var (slot, part) in parts) holder.SetPart(slot, part);
            return holder;
        }

        internal static UnityEngine.GameObject Part(float x = 0, float y = 0, float z = 0)
            => new(new Transform { localPosition = new Vector3(x, y, z) });

        /// <summary>A transform below a part, which is always a game object's own transform.</summary>
        internal static Transform Child(float x = 0, float y = 0, float z = 0)
            => Part(x, y, z).transform;

        internal Fixture Player(string id, bool synced = false, bool resolved = true)
        {
            var net = new SNet_Player(); var agent = new PlayerAgent { Owner = net };
            PlayerInventoryBase inventory = synced ? new PlayerInventorySynced() : new PlayerInventoryLocal();
            inventory.Owner = agent; agent.Inventory = inventory; net.PlayerAgent = new SNet_IPlayerAgent { Target = agent };
            var backpack = new PlayerBackpack { Owner = net }; PlayerBackpackManager.Backpacks[net] = backpack;
            var reference = new EntityReference(EquipmentNativeAdapter.PlayerKind + ":" + id, Kernel.WorldEpoch, 1);
            if (resolved) { PlayerRefs[net] = reference; LivePlayers.Add(reference); }
            return new(net, agent, inventory, backpack, reference);
        }

        /// <summary>A native enemy the fixture's own enemy module has registered, so a hit on it resolves exactly
        /// as the enemy domain resolves it in the game.</summary>
        internal EnemyFixture Enemy(ushort id)
        {
            var agent = new Enemies.EnemyAgent { GlobalID = id };
            var reference = new EntityReference(EquipmentNativeAdapter.EnemyKind + ":" + id, Kernel.WorldEpoch, 1);
            EnemyRefs[agent] = reference; LiveEnemies.Add(reference);
            return new(agent, reference);
        }

        /// <summary>One map object a hit can belong to, in the domain that owns it. The address text is the kind's
        /// own spelling; the fixture never parses or builds one, because Weapon never does either.</summary>
        internal sealed record MapObjectFixture(UnityEngine.MapObjectDouble Object, EntityReference Reference);

        internal MapObjectFixture MapObject(string address)
        {
            var mapObject = new UnityEngine.MapObjectDouble();
            var reference = new EntityReference(EquipmentNativeAdapter.MapObjectKind + ":" + address, Kernel.WorldEpoch, 1);
            MapObjectRefs[mapObject] = reference; LiveMapObjects.Add(reference);
            return new(mapObject, reference);
        }

        /// <summary>The object a hit is resolved against, standing on the map object it was hit through — the
        /// hierarchy a bullet's collider has in the game. The object handed over is the collider, never the map
        /// object, because that is all the hit path can see.</summary>
        internal static UnityEngine.Collider HitObject(UnityEngine.Component? above = null)
            => new() { Parent = above };

        /// <summary>The map object above a component, as the kind's own reader climbs for it: the component
        /// itself when it is one, otherwise the first one up the parent chain.</summary>
        private static UnityEngine.MapObjectDouble? Climb(UnityEngine.Component? component)
            => component switch
            {
                null => null,
                UnityEngine.MapObjectDouble mapObject => mapObject,
                _ => Climb(component.Parent)
            };

        internal static BackpackItem Put(PlayerBackpack backpack, InventorySlot slot, uint checksum, Item? instance = null,
            uint? offlineGearBlock = null)
        {
            var gear = Gear.GearIDRange.Create();
            gear.Checksum = checksum;
            // The spelling the game writes in GearManager.LoadOfflineGearDatas; the fixture keeps the literal rather
            // than the production constant so a reworded prefix fails a test.
            gear.PlayfabItemInstanceId = offlineGearBlock == null ? null : "OfflineGear_ID_" + offlineGearBlock.Value;
            var item = new BackpackItem
            {
                Instance = instance ?? new ItemEquippable(),
                GearIDRange = gear
            };
            backpack.Slots![(int)slot] = item; return item;
        }

        internal EntityReference? EntityOf(Item item) => Session!.Adapter.EntityOf(item);
        internal bool Current(EntityReference? reference) => reference != null && Session!.Identity.IsCurrent(reference);
        internal bool MatchesGearBlock(string? reference, EntityReference subject) => Session!.Adapter.MatchesGearBlock(reference, subject);
        /// <summary>The block text the game's own record on this gear carries, exactly as the adapter read it back.</summary>
        internal string? GearBlockOf(BackpackItem item) => EquipmentNativeAdapter.GearBlock(item);
       internal EquipmentNativeAdapter.PlacedObject? PlacementOf(ItemEquippable instance) => Session!.Adapter.PlacementOf(instance);

        private CommandResult Record(CommandContext context)
        {
            // The sink's recipient is always wired; its actor is not, because several of the rows this world's
            // gate plans subscribe to carry no actor at all. A recorder reads what the event wired.
            Sink.Add((context.EventId, context.GetEntityInput("target")!,
                context.Inputs.TryGetProperty("actor", out var actor) && actor.ValueKind == JsonValueKind.Object
                    ? RuntimeJson.Entity(actor) : new EntityReference("fixture.absent", Kernel.WorldEpoch, 1)));
            return CommandResult.Succeeded(RuntimeJson.From(new { fixtureOnly = true }));
        }

        // The fixture sink declares the framework's shared result row so its action is a well-formed contract.
        private static readonly object[] IdentityRow =
        {
            new { id = "target", type = "entity" }, new { id = "status", type = "enum", schema = "execution_outcome" },
            new { id = "committed", type = "enum", schema = "commit_state" }, new { id = "code", type = "string" }
        };

        private RuntimeModule ConsumerModule() => new(RuntimeKernel.ApiVersion, RuntimeJson.From(new
        {
            providers = new[] { new { id = Consumer, kind = "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
            capabilities = new[] { new { id = Sink_, owner = Consumer, kind = "action", label = "Fixture wield sink", version = "1.0.0", parameters = new { },
                graph = new
                {
                    domains = new[] { "weapon" }, execution = "host",
                    inputs = new object[]
                    {
                        new { id = "enter", type = "execution" },
                        new { id = "target", type = "entity" },
                        new { id = "actor", type = "entity", optional = true }
                    },
                    outputs = new[] { new { id = "result", type = "result", schema = "fixture.consumer.result", fields = IdentityRow } }, parameters = Array.Empty<object>(),
                    recipients = new { input = "target", target = "entity", cardinality = "one", requires = Array.Empty<string>(), result = "result" }
                } } },
            bindings = new[] { new { id = SinkBinding, capabilityId = Sink_, providerId = Consumer, handler = Sink_, role = "execute",
                status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>() } }
        }).GetRawText(), new Dictionary<string, CommandHandler> { [Sink_] = Record },
            new[] { new BindingSupport(SinkBinding, "implementation-only", new[] { RecordPermission }) })
        {
            // The sink reads both wired entity inputs and fills the action's result row.
            Shapes = new Dictionary<string, HandlerShape> { [Sink_] = new HandlerShape().Inputs("target", "actor").Outputs("result") }
        };

        private JsonElement Graph(string id) => RuntimeJson.Parse(Kernel.ExportManifest()).GetProperty("registry").GetProperty("capabilities")
            .EnumerateArray().Single(c => c.GetProperty("id").GetString() == id).GetProperty("graph");
        /// <summary>The version the registry declares for one capability row, read from the manifest rather than
        /// pinned here: the weapon rows this plan locks are declared by the runtime's own contract provider, so a
        /// version written into the fixture would be a second copy of somebody else's row and would silently stop
        /// matching the registration it is meant to pin.</summary>
        private string CapabilityVersion(string id) => RuntimeJson.Parse(Kernel.ExportManifest()).GetProperty("registry").GetProperty("capabilities")
            .EnumerateArray().Single(c => c.GetProperty("id").GetString() == id).GetProperty("version").GetString()!;
        private static object[] Slots(JsonElement ports) => ports.EnumerateArray().Select((p, index) => (object)new
        {
            index, type = Array.IndexOf(PortTypes, p.GetProperty("type").GetString()!),
            cardinality = p.TryGetProperty("cardinality", out var c) && c.GetString() == "many" ? 1 : 0, valueSet = -1, lifetime = -1,
            optional = p.TryGetProperty("optional", out var o) && o.GetBoolean(), nullable = p.TryGetProperty("nullable", out var n) && n.GetBoolean()
        }).ToArray();

        /// <summary>The layout the loader re-derives, read through the framework's own rule. A row whose ports
        /// name a value set — an enum, a resource kind, a handle kind — carries that set's index here, and only
        /// the framework's own table can say which index a schema has, so this is read rather than restated.</summary>
        private static JsonElement Sides(JsonElement contract, string side)
            => (JsonElement)typeof(RuntimeKernel).Assembly.GetType("ForgeRuntime.Framework.RuntimeGraphContracts")!
                .GetMethod("Layout", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(null, new object[] { contract, side })!;

        /// <summary>One plan port layout over an already-resolved contract.</summary>
        private static object Resolved(JsonElement contract, int constants) => new
        {
            inputs = Sides(contract, "inputs"), outputs = Sides(contract, "outputs"),
            constants = new object?[constants], promoted = Array.Empty<int>()
        };

        private static object Layout(JsonElement graph) => new { inputs = Slots(graph.GetProperty("inputs")), outputs = Slots(graph.GetProperty("outputs")),
            constants = Array.Empty<object>(), promoted = Array.Empty<int>() };
        private static int Slot(JsonElement ports, string name) => ports.EnumerateArray().Select((p, i) => (p, i)).Single(x => x.p.GetProperty("id").GetString() == name).i;

        internal string Plan(object[]? attachments = null)
        {
            var sink = Graph(Sink_);
            object Binding(string bindingId, string capabilityId, string providerId, string providerVersion, string handler)
                => new { bindingId, capabilityId, capabilityVersion = CapabilityVersion(capabilityId), providerId, providerVersion, handler };
            // schemaVersion 1 (Runtime API 1.0.0): the plan declares its mount targets, the entry names its first
            // step, and the sink step has no execution output, so its successor list is empty.
            object Entry(string name, int binding, JsonElement trigger) => new
            {
                nodeId = name, binding, layout = Layout(trigger), start = 0,
                steps = new[] { new { nodeId = name + "_sink", nodeKind = "action", binding = 0, layout = Layout(sink), inputs = new[]
                {
                    new { slot = Slot(sink.GetProperty("inputs"), "target"), fromEventSlot = Slot(trigger.GetProperty("outputs"), "equipment") },
                    new { slot = Slot(sink.GetProperty("inputs"), "actor"), fromEventSlot = Slot(trigger.GetProperty("outputs"), "actor") }
                }, successors = Array.Empty<int?>() } }
            };
            return RuntimeJson.From(new
            {
                schemaVersion = 1, kind = "forge-runtime-plan", planId = "fixture.weapon.plan", resource = new { id = "fixture.resource", revision = "r1" },
                runtime = Kernel.Identity, domain = "weapon", authority = "host", failurePolicy = "stop-entrypoint",
                permissions = new[] { RecordPermission, ModuleDefinition.WieldReadPermission }, dependencies = Array.Empty<string>(), // ordinal-sorted
                // The fixture plan mounts on the one level this world owns the mount kind for unless a case asks
                // for another kind: a `gear-block` mount needs the matcher the equipment provider registers.
                attachments = attachments ?? new object[] { new { kind = "level", reference = LevelReference } },
                limits = new { maxEventsPerTick = 16, maxCommandsPerTick = 16, maxQueuedEvents = 16, maxCausalDepth = 4 },
                // Plan binding locks are ordinal-sorted by bindingId.
                bindings = new[]
                {
                    Binding(SinkBinding, Sink_, Consumer, "1.0.0", Sink_),
                    Binding(ModuleDefinition.EquippedBinding, ModuleDefinition.EquippedCapability, ModuleDefinition.ProviderId, ModuleDefinition.Version, "gtfo.equipment.equipped"),
                    Binding(ModuleDefinition.UnequippedBinding, ModuleDefinition.UnequippedCapability, ModuleDefinition.ProviderId, ModuleDefinition.Version, "gtfo.equipment.unequipped")
                },
                entrypoints = new[] { Entry("equipped", 1, Graph(ModuleDefinition.EquippedCapability)), Entry("unequipped", 2, Graph(ModuleDefinition.UnequippedCapability)) }
            }).GetRawText();
        }

        /// <summary>Ends this world: its runtime, its session and its registered providers, all on the thread that
        /// built them. Every step tolerates the state a case can leave behind — a case that never started a session,
        /// one that already stopped the runtime, one that disposed the session itself and one that injected a
        /// failing logger — so a case that failed halfway still cleans up instead of reporting a second error.</summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (Started != null && ReferenceEquals(WeaponNativeSession.Current, Started))
            {
                Stop();
                Started.Dispose();
            }
            else Stop();
            consumer.Dispose(); enemies.Dispose(); players.Dispose(); mapObjects.Dispose(); level.Dispose();
            try { if (Directory.Exists(GearPartsRoot)) Directory.Delete(GearPartsRoot, true); }
            catch (IOException) { }
        }

        /// <summary>Stops this world's runtime when a case left it running. A runtime that never started, one that
        /// already stopped and one whose startup callback failed are all the same to the kernel: there is nothing
        /// left to stop, and asking would be rejected.</summary>
        private void Stop()
        {
            if (Kernel.StartupState is RuntimeStartupState.Ready or RuntimeStartupState.Failed) Kernel.StopRuntime();
        }

        /// <summary>The process-wide state a case inherits from whatever ran before it: the doubles' backpack table
        /// and master flag, the patch counters and the host plugin's mode, runtime and gameplay gate. A case calls
        /// this before it builds its world so it starts from the same state every time; per-world state is not here
        /// because a world does not outlive its case.</summary>
        internal static void Reset()
        {
            PlayerBackpackManager.Backpacks.Clear();
            Harmony.Reset(); Host.ConfiguredMode = ForgeRuntime.RuntimeMode.Play;
            Host.Runtime = null; Host.CanExecuteGameplay = true; SNet.IsMaster = true;
        }
}
