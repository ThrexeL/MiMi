using MiMi.Agents;
using Xunit;

namespace MiMi.Tests;

public class MascotMoodTests
{
    private static MascotMoodInput Input(
        bool approval = false, bool error = false, int working = 0, bool music = false,
        double idleMinutes = 0, bool sleepy = true) =>
        new(approval, error, working, music, TimeSpan.FromMinutes(idleMinutes), sleepy,
            MascotMoodPolicy.DefaultSleepAfter);

    [Fact]
    public void ApprovalOutranksEverything()
    {
        var mood = MascotMoodPolicy.Persistent(Input(approval: true, error: true, working: 3,
            music: true, idleMinutes: 60));
        Assert.Equal(MascotMood.Alert, mood);
    }

    [Fact]
    public void ErrorOutranksWorkingMusicAndSleep()
    {
        var mood = MascotMoodPolicy.Persistent(Input(error: true, working: 2, music: true, idleMinutes: 60));
        Assert.Equal(MascotMood.Concerned, mood);
    }

    [Fact]
    public void WorkingOutranksMusicAndSleep()
    {
        Assert.Equal(MascotMood.Focused, MascotMoodPolicy.Persistent(Input(working: 1, music: true, idleMinutes: 60)));
    }

    [Fact]
    public void MusicOutranksSleep()
    {
        Assert.Equal(MascotMood.Lively, MascotMoodPolicy.Persistent(Input(music: true, idleMinutes: 60)));
    }

    [Fact]
    public void SleepRequiresTenMinutesOfUserIdle_AndTheSetting()
    {
        Assert.Equal(MascotMood.Calm, MascotMoodPolicy.Persistent(Input(idleMinutes: 9.9)));
        Assert.Equal(MascotMood.Sleepy, MascotMoodPolicy.Persistent(Input(idleMinutes: 10)));
        Assert.Equal(MascotMood.Calm, MascotMoodPolicy.Persistent(Input(idleMinutes: 60, sleepy: false)));
    }

    [Fact]
    public void StartedReaction_CuriousWhileFocused_ButNeverDuringUrgency()
    {
        Assert.Equal(MascotMood.Curious, MascotMoodPolicy.Reaction(MascotMood.Focused, MascotMoment.AgentStarted));
        Assert.Equal(MascotMood.Curious, MascotMoodPolicy.Reaction(MascotMood.Sleepy, MascotMoment.AgentStarted));
        Assert.Null(MascotMoodPolicy.Reaction(MascotMood.Alert, MascotMoment.AgentStarted));
        Assert.Null(MascotMoodPolicy.Reaction(MascotMood.Concerned, MascotMoment.AgentStarted));
    }

    [Fact]
    public void CompletionReaction_NeverInterruptsAnotherWorkingAgent()
    {
        Assert.Equal(MascotMood.Happy, MascotMoodPolicy.Reaction(MascotMood.Calm, MascotMoment.TurnCompleted));
        Assert.Equal(MascotMood.Happy, MascotMoodPolicy.Reaction(MascotMood.Lively, MascotMoment.TurnCompleted));
        Assert.Equal(MascotMood.Happy, MascotMoodPolicy.Reaction(MascotMood.Sleepy, MascotMoment.TurnCompleted));
        Assert.Null(MascotMoodPolicy.Reaction(MascotMood.Focused, MascotMoment.TurnCompleted));
        Assert.Null(MascotMoodPolicy.Reaction(MascotMood.Alert, MascotMoment.TurnCompleted));
        Assert.Null(MascotMoodPolicy.Reaction(MascotMood.Concerned, MascotMoment.TurnCompleted));
    }

    [Fact]
    public void StaleReactionIsDisallowedWhenUrgentStateArrives()
    {
        Assert.True(MascotMoodPolicy.ReactionAllowed(MascotMood.Calm));
        Assert.True(MascotMoodPolicy.ReactionAllowed(MascotMood.Lively));
        Assert.True(MascotMoodPolicy.ReactionAllowed(MascotMood.Sleepy));
        Assert.False(MascotMoodPolicy.ReactionAllowed(MascotMood.Focused));
        Assert.False(MascotMoodPolicy.ReactionAllowed(MascotMood.Alert));
        Assert.False(MascotMoodPolicy.ReactionAllowed(MascotMood.Concerned));
    }
}
