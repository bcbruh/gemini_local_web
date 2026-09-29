using AgentLocalWeb.Agent.Core;

namespace AgentLocalWeb.Agent.Core.Tests;

public sealed class RunStateMachineTests
{
    [Fact]
    public void SupportsReadOnlyToolLoopToCompletion()
    {
        var run = new RunStateMachine();

        run.TransitionTo(RunState.PreparingContext);
        run.TransitionTo(RunState.WaitingForBrain);
        run.TransitionTo(RunState.ValidatingAction);
        run.TransitionTo(RunState.RunningTool);
        run.TransitionTo(RunState.WaitingForBrain);
        run.TransitionTo(RunState.Completed);

        Assert.True(run.IsTerminal);
        Assert.Equal(RunState.Completed, run.State);
    }

    [Fact]
    public void SupportsApprovalRejectionReturningToBrain()
    {
        var run = At(RunState.ValidatingAction);

        run.TransitionTo(RunState.WaitingForApproval);
        run.TransitionTo(RunState.WaitingForBrain);

        Assert.Equal(RunState.WaitingForBrain, run.State);
    }

    [Theory]
    [InlineData(RunState.Failed)]
    [InlineData(RunState.Cancelled)]
    [InlineData(RunState.Interrupted)]
    public void AnyActiveStateCanFinishWithNonSuccessTerminalState(RunState terminal)
    {
        var run = At(RunState.WaitingForBrain);

        run.TransitionTo(terminal);

        Assert.True(run.IsTerminal);
        Assert.Equal(terminal, run.State);
    }

    [Fact]
    public void RejectsSkippedOrPostTerminalTransitions()
    {
        var run = new RunStateMachine();

        var skipped = Assert.Throws<InvalidRunStateTransitionException>(
            () => run.TransitionTo(RunState.RunningTool));
        Assert.Equal(RunState.Queued, skipped.Current);
        Assert.Equal(RunState.RunningTool, skipped.Requested);

        run.TransitionTo(RunState.Cancelled);
        Assert.Throws<InvalidRunStateTransitionException>(
            () => run.TransitionTo(RunState.PreparingContext));
    }

    private static RunStateMachine At(RunState state)
    {
        var run = new RunStateMachine();
        run.TransitionTo(RunState.PreparingContext);
        if (state == RunState.PreparingContext)
        {
            return run;
        }

        run.TransitionTo(RunState.WaitingForBrain);
        if (state == RunState.WaitingForBrain)
        {
            return run;
        }

        run.TransitionTo(RunState.ValidatingAction);
        return run;
    }
}
