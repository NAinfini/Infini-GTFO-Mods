using ForgeMap.Native;
using ForgeRuntime.Framework;
using LevelGeneration;

namespace ForgeMap.Tests.NativeEnvironment;

/// <summary>
/// The one registration this package takes on the kernel's own clock. Every case drives the clock through the
/// kernel's own <c>Advance</c>, so what is measured is the registration a real frame would take rather than a
/// call to <see cref="MapClock.Wake"/> in isolation: an idle package pays nothing per frame, a placed zone is
/// judged at the cadence of the game's own collision trigger (<c>COLLISION_CHECK_INTERVAL</c>), and the
/// registration is given back the moment the work is done.
/// </summary>
public sealed class MapClockTests
{
    private static RuntimeKernel Kernel()
    {
        var kernel = new RuntimeKernel(new RuntimeIdentity("forge.runtime", "1.0.0", RuntimeKernel.ApiVersion, "20403457"));
        // Advancing a tick is a world's own step, so the fixture starts the world the clock's frames belong to.
        kernel.BeginWorld(1);
        return kernel;
    }

    /// <summary>A registration with no rows of its own. The clock only needs a handle to hang its own lifecycle
    /// subscription on, so a module that declares nothing is what this fixture registers.</summary>
    private static RuntimeModuleHandle Registration(RuntimeKernel kernel) => kernel.RegisterModule(
        new RuntimeModule(RuntimeKernel.ApiVersion, RuntimeJson.From(new
        {
            providers = new[] { new { id = "test.clock", kind = "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
            capabilities = Array.Empty<object>(),
            bindings = Array.Empty<object>()
        }).GetRawText(), new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>()), RuntimeLogLevel.Off);

    [Fact]
    public void AnIdleClockTakesNoRegistration()
    {
        var kernel = Kernel();
        using var registration = Registration(kernel);
        kernel.StartRuntime(() => { });
        var zoneJudgements = 0;
        var fadeTicks = 0;
        using var clock = new MapClock(registration, () => { }, () => false, () => zoneJudgements++,
            () => false, _ => fadeTicks++, () => 1f);

        // A wake with nothing pending is the level-load and reconcile path announcing itself, which must not take
        // a per-frame callback for a session that has no work at all.
        clock.Wake();
        for (var tick = 1; tick <= 10; tick++) kernel.Advance(tick, true);

        Assert.False(clock.Registered);
        Assert.Equal(0, clock.Registrations);
        Assert.Equal(0, zoneJudgements);
        Assert.Equal(0, fadeTicks);
    }

    [Fact]
    public void APlacedZoneIsJudgedOncePerTenthOfASecond()
    {
        var kernel = Kernel();
        using var registration = Registration(kernel);
        kernel.StartRuntime(() => { });
        var zoneJudgements = 0;
        // Four hundredths of a second is a 25 fps frame: a cadence taken per frame would judge six times where the
        // game's own collision trigger, whose period is COLLISION_CHECK_INTERVAL, judges once.
        const float step = 0.04f;
        using var clock = new MapClock(registration, () => { }, () => true, () => zoneJudgements++,
            () => false, _ => { }, () => step);

        clock.Wake();
        Assert.True(clock.Registered);

        kernel.Advance(1, true);
        kernel.Advance(2, true);
        Assert.Equal(0, zoneJudgements);

        kernel.Advance(3, true);
        Assert.Equal(1, zoneJudgements);

        kernel.Advance(4, true);
        kernel.Advance(5, true);
        Assert.Equal(1, zoneJudgements);

        kernel.Advance(6, true);
        Assert.Equal(2, zoneJudgements);

        // One registration covers every judgement: the clock is not re-taken per frame.
        Assert.Equal(1, clock.Registrations);
        Assert.True(clock.Registered);
    }

    [Fact]
    public void AnUnevenFrameKeepsTheJudgementsPerSecond()
    {
        // Ten seconds of 32 fps frames: every step is a power of two, so the arithmetic is exact and the count is
        // the rate the constant asks for. (0.1f is one ulp above a tenth, which is why ten seconds hold 99 beats
        // rather than a hundred; the check is a window, not a promise of 100.)
        var uniform = Judgements(320, _ => 0.03125f);
        // The same ten seconds in uneven frames: a 64th and three 64ths alternating. The counts match because the
        // accumulator keeps the remainder of a beat. A cadence that dropped it would have judged only 80 times,
        // because every beat would restart the count from the frame that crossed it.
        var uneven = Judgements(320, frame => frame % 2 == 0 ? 0.015625f : 0.046875f);

        Assert.Equal(uniform, uneven);
        Assert.InRange(uniform, 98, 100);
    }

    [Fact]
    public void AStepThatFellBehindJudgesOnceAndDoesNotCatchUp()
    {
        var kernel = Kernel();
        using var registration = Registration(kernel);
        kernel.StartRuntime(() => { });
        var zoneJudgements = 0;
        var steps = new[] { 5f, 0.04f, 0.04f, 0.04f };
        var frame = 0;
        using var clock = new MapClock(registration, () => { }, () => true, () => zoneJudgements++,
            () => false, _ => { }, () => steps[frame++]);
        clock.Wake();

        // Five seconds in one step is one judgement, not fifty.
        kernel.Advance(1, true);
        Assert.Equal(1, zoneJudgements);

        // The beat that follows is the beat: the two frames after the hitch are not a burst of the beats it
        // swallowed, and the third frame is the next ordinary judgement.
        kernel.Advance(2, true);
        kernel.Advance(3, true);
        Assert.Equal(1, zoneJudgements);
        kernel.Advance(4, true);
        Assert.Equal(2, zoneJudgements);
    }

    /// <summary>Drives one clock through <paramref name="frames"/> advances, one step per frame, and answers how
    /// many zone judgements it made. The registration is the real one: every advance raises the lifecycle the clock
    /// is subscribed to, exactly as a frame does.</summary>
    private static int Judgements(int frames, Func<int, float> step)
    {
        var kernel = Kernel();
        using var registration = Registration(kernel);
        kernel.StartRuntime(() => { });
        var judgements = 0;
        var frame = 0;
        using var clock = new MapClock(registration, () => { }, () => true, () => judgements++,
            () => false, _ => { }, () => step(frame++));
        clock.Wake();
        for (var tick = 1; tick <= frames; tick++) kernel.Advance(tick, true);
        return judgements;
    }

    [Fact]
    public void AFinishedFadeGivesTheRegistrationBack()
    {
        var kernel = Kernel();
        using var registration = Registration(kernel);
        kernel.StartRuntime(() => { });
        LightColorFades.Clear();
        using var clock = new MapClock(registration, () => { }, () => false, () => { },
            () => LightColorFades.Count > 0, seconds => LightColorFades.Tick(1, seconds), () => 1f);
        LightColorFades.WorkScheduled = clock.Wake;
        try
        {
            var light = new LG_Light { m_category = LG_Light.LightCategory.General };
            LightColorFades.Schedule(1, new LightColorFade.Key(0, 0, 0, LightColorFade.EveryCategory),
                LightColorFade.Between(new[] { light }, new UnityEngine.Color(1f, 0f, 0f, 1f), null,
                    LightColorFade.EveryCategory, 1f));

            // Scheduling is what takes the clock: the table has no ticker of its own.
            Assert.True(clock.Registered);
            Assert.Equal(1, LightColorFades.Count);

            kernel.Advance(1, true);

            Assert.Equal(0, LightColorFades.Count);
            Assert.False(clock.Registered);
            Assert.Equal(1, clock.Registrations);
        }
        finally
        {
            LightColorFades.WorkScheduled = null;
            LightColorFades.Clear();
        }
    }
}
