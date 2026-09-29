namespace AgentLocalWeb.Agent.Core;

public enum RunState
{
    Queued,
    PreparingContext,
    WaitingForBrain,
    ValidatingAction,
    WaitingForApproval,
    RunningTool,
    Completed,
    Failed,
    Cancelled,
    Interrupted
}

public sealed class RunStateMachine
{
    private static readonly IReadOnlyDictionary<RunState, IReadOnlySet<RunState>> Transitions =
        new Dictionary<RunState, IReadOnlySet<RunState>>
        {
            [RunState.Queued] = States(RunState.PreparingContext),
            [RunState.PreparingContext] = States(RunState.WaitingForBrain),
            [RunState.WaitingForBrain] = States(
                RunState.ValidatingAction,
                RunState.Completed),
            [RunState.ValidatingAction] = States(
                RunState.WaitingForApproval,
                RunState.RunningTool,
                RunState.WaitingForBrain),
            [RunState.WaitingForApproval] = States(
                RunState.RunningTool,
                RunState.WaitingForBrain),
            [RunState.RunningTool] = States(RunState.WaitingForBrain)
        };

    public RunState State { get; private set; } = RunState.Queued;

    public bool IsTerminal => State is
        RunState.Completed or
        RunState.Failed or
        RunState.Cancelled or
        RunState.Interrupted;

    public void TransitionTo(RunState next)
    {
        if (!CanTransitionTo(next))
        {
            throw new InvalidRunStateTransitionException(State, next);
        }

        State = next;
    }

    public bool CanTransitionTo(RunState next)
    {
        if (IsTerminal || next == State)
        {
            return false;
        }

        if (next is RunState.Failed or RunState.Cancelled or RunState.Interrupted)
        {
            return true;
        }

        return Transitions.TryGetValue(State, out var allowed) && allowed.Contains(next);
    }

    private static IReadOnlySet<RunState> States(params RunState[] states) =>
        new HashSet<RunState>(states);
}

public sealed class InvalidRunStateTransitionException(
    RunState current,
    RunState requested)
    : InvalidOperationException($"Run state cannot transition from {current} to {requested}.")
{
    public RunState Current { get; } = current;

    public RunState Requested { get; } = requested;
}
