namespace PhotonCadRuntime;

public sealed class CadStateTransitionException : InvalidOperationException
{
    public CadStateTransitionException(string code)
        : base("The requested CAD session state transition is not permitted.")
    {
        Code = ContractGuards.Identifier(code, nameof(code), 64);
    }

    public string Code { get; }
}

public sealed record CadSessionStateSnapshot(
    CadSessionHandle Session,
    CadSessionState State,
    CadRequestId? ActiveRequest,
    long TransitionSequence);

public sealed class CadSessionStateGate
{
    private readonly object _sync = new();
    private readonly CadSessionHandle _session;
    private CadSessionState _state = CadSessionState.Created;
    private CadRequestId? _activeRequest;
    private long _transitionSequence;

    public CadSessionStateGate(CadSessionHandle session) =>
        _session = session ?? throw new CadContractException("required", nameof(session));

    public CadSessionStateSnapshot Snapshot()
    {
        lock (_sync)
            return new CadSessionStateSnapshot(_session, _state, _activeRequest, _transitionSequence);
    }

    public CadSessionStateSnapshot BeginOpening() => Transition(CadSessionState.Created, CadSessionState.Opening);

    public CadSessionStateSnapshot MarkReady()
    {
        lock (_sync)
        {
            if (_state is not CadSessionState.Opening and not CadSessionState.Busy)
                throw new CadStateTransitionException("invalid_ready_transition");
            _activeRequest = null;
            return SetState(CadSessionState.Ready);
        }
    }

    public CadSessionStateSnapshot BeginOperation(CadRequestId requestId)
    {
        ArgumentNullException.ThrowIfNull(requestId);
        lock (_sync)
        {
            if (_state != CadSessionState.Ready)
                throw new CadStateTransitionException(_state == CadSessionState.Busy
                    ? "operation_already_active"
                    : "session_not_ready");
            _activeRequest = requestId;
            return SetState(CadSessionState.Busy);
        }
    }

    public CadSessionStateSnapshot CompleteOperation(CadRequestId requestId)
    {
        ArgumentNullException.ThrowIfNull(requestId);
        lock (_sync)
        {
            if (_state != CadSessionState.Busy || _activeRequest != requestId)
                throw new CadStateTransitionException("operation_ownership_mismatch");
            _activeRequest = null;
            return SetState(CadSessionState.Ready);
        }
    }

    public CadSessionStateSnapshot BeginClosing()
    {
        lock (_sync)
        {
            if (_state is CadSessionState.Closed or CadSessionState.Closing)
                throw new CadStateTransitionException("session_already_closing");
            _activeRequest = null;
            return SetState(CadSessionState.Closing);
        }
    }

    public CadSessionStateSnapshot MarkClosed()
    {
        lock (_sync)
        {
            if (_state is not CadSessionState.Closing and not CadSessionState.Faulted)
                throw new CadStateTransitionException("invalid_closed_transition");
            _activeRequest = null;
            return SetState(CadSessionState.Closed);
        }
    }

    public CadSessionStateSnapshot MarkFaulted()
    {
        lock (_sync)
        {
            if (_state == CadSessionState.Closed)
                throw new CadStateTransitionException("closed_session_is_terminal");
            _activeRequest = null;
            return SetState(CadSessionState.Faulted);
        }
    }

    private CadSessionStateSnapshot Transition(CadSessionState expected, CadSessionState next)
    {
        lock (_sync)
        {
            if (_state != expected)
                throw new CadStateTransitionException("invalid_state_transition");
            return SetState(next);
        }
    }

    private CadSessionStateSnapshot SetState(CadSessionState state)
    {
        _state = state;
        _transitionSequence = checked(_transitionSequence + 1);
        return new CadSessionStateSnapshot(_session, _state, _activeRequest, _transitionSequence);
    }
}
