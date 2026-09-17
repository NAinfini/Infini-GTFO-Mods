using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeWeapon.Native;

/// <summary>
/// The `forge.action.combat.projectile_launch` body: one of the game's own projectiles, from a point and a
/// direction, as many copies as the request asked for.
///
/// The launch is the manager's own prefab table and the manager's own spawn path
/// (<c>ProjectileManager.SpawnProjectileType(ProjectileType, Vector3, Quaternion)</c>), which is the body the
/// game itself uses every time a projectile appears — the glue gun, an enemy's spit, the infection bomb. Nothing
/// here builds a projectile, applies damage or simulates a flight: the spawned object runs its own
/// <c>ProjectileBase</c> life from that point on, and the profile the request named is one of the native
/// <c>ProjectileType</c> members or the request is refused.
///
/// The spawn path is private in the shipped build: it is the game's own entry point, but not a public one. It is
/// therefore resolved once and cached, and a build that no longer carries it refuses every launch by name instead
/// of silently doing nothing. The two vector types it takes are reached through <see cref="UnityValueBridge"/> for
/// the same reason, which is also what keeps this file compilable against the plain Unity surface a game-free
/// build supplies. The action is host-tier because the spawn is: the manager's table and the projectile's own
/// network replicator live where the world does.
/// </summary>
internal sealed class WeaponProjectileActions
{
    /// <summary>The refusal for a command dispatched where this package may not write, spelled the same way the
    /// package's other actions spell it.</summary>
    internal const string AuthorityCode = "authority-or-phase";
    /// <summary>The refusal for a profile name this build has no projectile for.</summary>
    internal const string UnknownProfileCode = "projectile-profile-unknown";
    /// <summary>The refusal for a count or a spread outside the range a launch can honour.</summary>
    internal const string CountOutOfRangeCode = "projectile-count-out-of-range";
    /// <summary>The refusal for a request whose origin or direction is missing or not a finite vector.</summary>
    internal const string MalformedVectorCode = "projectile-vector-invalid";
    /// <summary>The refusal for a build whose own spawn path could not be resolved, which is a fact about the
    /// build and not about the request.</summary>
    internal const string SpawnUnavailableCode = "projectile-spawn-unavailable";

    /// <summary>The most copies one request may launch. A bound rather than an unbounded loop: a number a plan
    /// cannot mean is refused instead of being run.</summary>
    internal const int MaximumCount = 16;
    /// <summary>The widest cone one request may scatter over, in degrees.</summary>
    internal const double MaximumSpread = 90d;

    /// <summary>The profile names this build answers for, mapped to the native member each one is. The names are
    /// the row's own vocabulary; a name missing here is a profile the row must not have declared.</summary>
    private static readonly IReadOnlyDictionary<string, string> Profiles =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["targeting_small"] = "TargetingSmall",
            ["targeting_medium"] = "TargetingMedium",
            ["targeting_large"] = "TargetingLarge",
            ["semi_targeting_quick"] = "SemiTargetingQuick",
            ["not_targeting_small_fast"] = "NotTargetingSmallFast",
            ["glue_flying"] = "GlueFlying",
            ["infection_bomb"] = "InfectionBomb"
        };

    private readonly RuntimeKernel _kernel;
    private readonly Func<bool> _canExecute;
    private readonly Action<string> _report;
    private readonly MethodInfo? _spawn;
    private readonly Type? _profileType;
    private readonly UnityValueBridge? _unity;

    internal WeaponProjectileActions(RuntimeKernel kernel, Func<bool> canExecute, Action<string> report)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _canExecute = canExecute ?? throw new ArgumentNullException(nameof(canExecute));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _spawn = Resolve(SpawnMethod, 3);
        _profileType = _spawn?.GetParameters()[0].ParameterType;
        _unity = UnityValueBridge.Resolve();
    }

    /// <summary>`forge.action.combat.projectile_launch`. Refusals, in the order they are checked: a command this
    /// machine may not run, a frame whose vectors do not read, a profile this build has no prefab for, a count or
    /// spread outside what a launch can honour, and finally the game's own spawn path. The result's own field is
    /// how many copies really left the origin, which is what the plan can check its request against.</summary>
    internal CommandResult Launch(CommandContext context)
    {
        if (!Authoritative()) return CommandResult.Rejected(AuthorityCode);
        if (!Vector(context.Inputs, "origin", out var origin) || !Vector(context.Inputs, "direction", out var direction))
            return CommandResult.Rejected(MalformedVectorCode);
        if (Length(direction) < 1e-4d) return CommandResult.Rejected(MalformedVectorCode);
        var profile = Text(context.Parameters, "profile");
        if (profile == null || !Profiles.TryGetValue(profile, out var member))
            return CommandResult.Rejected(UnknownProfileCode);
        var count = Integer(context.Parameters, "count", 1);
        if (count < 1 || count > MaximumCount) return CommandResult.Rejected(CountOutOfRangeCode);
        var spread = Number(context.Parameters, "spread", 0d);
        if (!double.IsFinite(spread) || spread < 0d || spread > MaximumSpread)
            return CommandResult.Rejected(CountOutOfRangeCode);
        if (_spawn == null || _profileType == null || _unity is not { Complete: true }
            || !Enum.IsDefined(_profileType, Enum.Parse(_profileType, member)))
            return CommandResult.Rejected(SpawnUnavailableCode);
        var kind = Enum.Parse(_profileType, member);
        var launched = 0;
        for (var index = 0; index < count; index++)
        {
            var heading = count == 1 || spread <= 0d ? Normalize(direction) : Scatter(direction, spread, index, count);
            var rotation = Heading(heading);
            if (rotation != null && Spawn(kind, origin, rotation)) launched++;
        }
        if (launched == 0)
        {
            _report("weapon.projectile-launch-refused profile=" + profile + " spawned=0");
            return CommandResult.Rejected(SpawnUnavailableCode);
        }
        return CommandResult.Succeeded(Rows(context, launched), new[]
        {
            new RuntimeFact(CombatPrimitiveContract.ProjectileLaunchBinding,
                RuntimeJson.From(new { launched, profile }))
        });
    }

    /// <summary>One spawn, through the game's own static body. A throw is answered as a refusal rather than
    /// reported as a launch that happened, because a projectile this machine could not create is exactly the case
    /// a plan must not be told succeeded. The return is read as "a prefab came back" and nothing else: this body
    /// never names the prefab's own type, which is what keeps the call compilable against the plain Unity surface
    /// every build of the game exposes.</summary>
    private bool Spawn(object kind, object origin, object rotation)
    {
        try
        {
            return _spawn!.Invoke(null, new[] { kind, origin, rotation }) != null;
        }
        catch (Exception error)
        {
            _report("weapon.projectile-spawn-failed: " + Describe(error));
            return false;
        }
    }

    /// <summary>
    /// The heading one copy of a multi-shot request leaves by: the copies alternate either side of the forward
    /// axis around the world's up, stepping outwards with the index, which is a deterministic pattern rather than
    /// a random one — a random spread would make the same plan produce a different result every run, and the row's
    /// parameters carry no seed to make that reproducible. The turn is Rodrigues' own formula applied to the
    /// three numbers, so the scatter needs no engine call at all.
    /// </summary>
    private object Scatter(object direction, double spread, int index, int count)
    {
        var forward = Normalize(direction);
        Components(forward, out var fx, out var fy, out var fz);
        var steps = Math.Max(1, (count - 1) / 2d);
        var step = (index + 1) / 2d;
        var sign = index % 2 == 1 ? 1d : -1d;
        var radians = spread * (step / steps) * sign * Math.PI / 180d;
        // The axis is the world's up crossed with the heading, so the scatter stays in the horizontal plane the
        // shot was fired in; a heading parallel to up falls back to the world's forward.
        Components(Cross(Up(), forward), out var ax, out var ay, out var az);
        if (ax * ax + ay * ay + az * az < 1e-12d) Components(Cross(Forward(), forward), out ax, out ay, out az);
        var axisLength = Math.Sqrt(ax * ax + ay * ay + az * az);
        if (!double.IsFinite(axisLength) || axisLength < 1e-6d) return forward;
        ax /= axisLength; ay /= axisLength; az /= axisLength;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        var dot = ax * fx + ay * fy + az * fz;
        var cx = ay * fz - az * fy;
        var cy = az * fx - ax * fz;
        var cz = ax * fy - ay * fx;
        var turned = _unity!.Vector(
            fx * cos + cx * sin + ax * dot * (1d - cos),
            fy * cos + cy * sin + ay * dot * (1d - cos),
            fz * cos + cz * sin + az * dot * (1d - cos));
        return turned == null ? forward : Normalize(turned);
    }

    /// <summary>
    /// The rotation the native spawn takes, built from the heading and the world's up: the game's own
    /// `Quaternion.LookRotation` is what every other caller of this spawn path uses, and this is the same
    /// orientation written out from the basis the heading and the up axis already are. A heading parallel to the
    /// up axis has no basis to build, which is answered as null and refused rather than launched with an invented
    /// rotation.
    /// </summary>
    private object? Heading(object direction)
    {
        var forward = Normalize(direction);
        Components(forward, out var fx, out var fy, out var fz);
        Components(Cross(Up(), forward), out var rx, out var ry, out var rz);
        var rightLength = Math.Sqrt(rx * rx + ry * ry + rz * rz);
        if (rightLength < 1e-6d)
        {
            Components(Cross(Forward(), forward), out rx, out ry, out rz);
            rightLength = Math.Sqrt(rx * rx + ry * ry + rz * rz);
        }
        if (!double.IsFinite(rightLength) || rightLength < 1e-6d) return null;
        rx /= rightLength; ry /= rightLength; rz /= rightLength;
        // The second axis is the heading crossed with the right one, so the basis is orthogonal by construction.
        var ux = fy * rz - fz * ry;
        var uy = fz * rx - fx * rz;
        var uz = fx * ry - fy * rx;
        return Basis(rx, ry, rz, ux, uy, uz, fx, fy, fz);
    }

    /// <summary>The rotation of one orthonormal basis, written as the engine's own quaternion. The trace form is
    /// the numerically stable branch for each of the four cases, and every component is finite by construction
    /// because the basis was normalised before it arrived.</summary>
    private object? Basis(double m00, double m01, double m02, double m10, double m11, double m12,
        double m20, double m21, double m22)
    {
        // The basis above is stored column-wise: the right axis is the first column, the up axis the second and
        // the heading the third, which is the layout the trace form is written against.
        var trace = m00 + m11 + m22;
        if (trace > 0d)
        {
            var s = Math.Sqrt(trace + 1d) * 2d;
            return _unity!.Quaternion((m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, 0.25d * s);
        }
        if (m00 > m11 && m00 > m22)
        {
            var s = Math.Sqrt(1d + m00 - m11 - m22) * 2d;
            return _unity!.Quaternion(0.25d * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s);
        }
        if (m11 > m22)
        {
            var s = Math.Sqrt(1d + m11 - m00 - m22) * 2d;
            return _unity!.Quaternion((m01 + m10) / s, 0.25d * s, (m12 + m21) / s, (m02 - m20) / s);
        }
        var last = Math.Sqrt(1d + m22 - m00 - m11) * 2d;
        return _unity!.Quaternion((m02 + m20) / last, (m12 + m21) / last, 0.25d * last, (m10 - m01) / last);
    }

    private object? Cross(object? left, object? right)
        => left == null || right == null || _unity == null ? null : _unity.Cross(left, right);

    /// <summary>The world's up axis, built through the bridge so no engine type is named here.</summary>
    private object? Up() => _unity?.Vector(0d, 1d, 0d);

    /// <summary>The world's forward axis, the stand-in when the heading is parallel to up.</summary>
    private object? Forward() => _unity?.Vector(0d, 0d, 1d);

    /// <summary>One vector's length, read through the bridge.</summary>
    private double Length(object value)
    {
        if (_unity == null || !_unity.Components(value, out var x, out var y, out var z)) return 0d;
        return Math.Sqrt(x * x + y * y + z * z);
    }

    /// <summary>The unit vector of one heading, or the same reading when it has no length to divide by — the
    /// caller has already refused a zero direction, so this is the belt to that refusal's braces.</summary>
    private object Normalize(object value)
    {
        var length = Length(value);
        if (length < 1e-9d || _unity == null) return value;
        Components(value, out var x, out var y, out var z);
        return _unity.Vector(x / length, y / length, z / length) ?? value;
    }

    private void Components(object value, out double x, out double y, out double z)
    {
        x = y = z = 0d;
        if (_unity != null) _unity.Components(value, out x, out y, out z);
    }

    private bool Authoritative()
    {
        if (!_canExecute()) return false;
        var state = _kernel.Lifecycle;
        return state.StartupState == RuntimeStartupState.Ready && state.IsHost == true;
    }

    /// <summary>The one result row: the four fixed columns, with `target` naming the source the request carried,
    /// and this row's own count of the copies that really left.</summary>
    private static JsonElement Rows(CommandContext context, int launched)
    {
        var source = TryEntity(context.Inputs, "source", out var named) ? named : null;
        return RuntimeJson.From(new
        {
            rows = new[]
            {
                new
                {
                    target = (EntityReference?)source, status = CommandStatuses.Succeeded,
                    committed = CommitStates.Confirmed, code = "", target_count = launched
                }
            }
        });
    }

    /// <summary>
    /// The private static spawn path of this build, or null when it is not there. Resolved once at construction:
    /// the method cannot change while the process runs, and a lookup per launch would be a second thing that can
    /// fail.
    ///
    /// The manager's own type is reached by name rather than by `typeof`, which keeps this file compilable where
    /// the game's engine assemblies are not referenced: naming the type would pull its base class in with it, and
    /// the spawn call's arguments and return are all read through reflection anyway.
    /// </summary>
    private static readonly string ManagerType = "ProjectileManager";
    /// <summary>The game's own spawn member, spelled here rather than taken from `nameof` for the reason the
    /// manager's type is: naming the member would name the type it is declared on.</summary>
    private const string SpawnMethod = "SpawnProjectileType";

    private static MethodInfo? Resolve(string name, int parameters)
    {
        var owner = Manager();
        if (owner == null) return null;
        foreach (var candidate in owner.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
            if (string.Equals(candidate.Name, name, StringComparison.Ordinal)
                && candidate.GetParameters().Length == parameters) return candidate;
        return null;
    }

    /// <summary>The projectile manager's own type, looked up by name in the assemblies this process has already
    /// loaded. A build without it answers null, which every caller above turns into a named refusal.</summary>
    private static Type? Manager()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var candidate = assembly.GetType("Gear." + ManagerType, throwOnError: false)
                ?? assembly.GetType(ManagerType, throwOnError: false);
            if (candidate != null) return candidate;
        }
        return null;
    }

    /// <summary>One vector read out of the request frame, built through the bridge. The components are read as
    /// plain numbers, so a frame whose vector is malformed is refused before any engine type is touched.</summary>
    private bool Vector(JsonElement inputs, string id, out object value)
    {
        value = new object();
        if (inputs.ValueKind != JsonValueKind.Object || !inputs.TryGetProperty(id, out var element)) return false;
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != 3) return false;
        var parts = new double[3];
        var index = 0;
        foreach (var component in element.EnumerateArray())
        {
            if (component.ValueKind != JsonValueKind.Number || !component.TryGetDouble(out parts[index])) return false;
            if (!double.IsFinite(parts[index])) return false;
            index++;
        }
        var built = _unity?.Vector(parts[0], parts[1], parts[2]);
        if (built == null) return false;
        value = built;
        return true;
    }

    private static bool TryEntity(JsonElement inputs, string id, out EntityReference reference)
    {
        reference = null!;
        if (inputs.ValueKind != JsonValueKind.Object || !inputs.TryGetProperty(id, out var value)) return false;
        if (value.ValueKind != JsonValueKind.Object) return false;
        var resolved = RuntimeJson.Entity(value);
        if (string.IsNullOrEmpty(resolved.Id)) return false;
        reference = resolved;
        return true;
    }

    private static string? Text(JsonElement parameters, string id)
        => parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(id, out var value)
           && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int Integer(JsonElement parameters, string id, int fallback)
        => parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(id, out var value)
           && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : fallback;

    private static double Number(JsonElement parameters, string id, double fallback)
        => parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(id, out var value)
           && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : fallback;

    private static string Describe(Exception error)
        => error is TargetInvocationException { InnerException: { } inner } ? inner.GetType().Name + ": " + inner.Message
            : error.GetType().Name + ": " + error.Message;
}
