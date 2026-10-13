namespace OpenReflect.Core.Abstractions;

/// <summary>Authoritative error taxonomy (ARCHITECTURE.md §4). Ranges: 1–9 arg, 10–19 source, 20–29 dest, 30–39 format, 40–49 crypto, 50–59 chain, 60–69 restore, 70–79 scheduling, 80+ lifecycle, 99 internal.</summary>
public enum ErrorCode
{
    None = 0,
    InvalidArgument = 1,
    NotSupported = 2,
    SourceNotFound = 10,
    SourceAccessDenied = 11,
    SourceReadError = 12,
    VssFailure = 13,
    DestinationNotFound = 20,
    DestinationAccessDenied = 21,
    DestinationFull = 22,
    DestinationInUse = 23,
    BadFormat = 30,
    UnsupportedVersion = 31,
    CorruptHeader = 32,
    CorruptChunk = 33,
    IndexMissing = 34,
    WrongPassword = 40,
    CryptoFailure = 41,
    ChainBroken = 50,
    ChainIncomplete = 51,
    ChainTooDeep = 52,
    RegionMismatch = 53,
    RestoreSafetyGateBlocked = 60,
    RestoreTargetTooSmall = 61,
    RestorePlanInvalid = 62,
    SchedulerFailure = 70,
    JobNotFound = 71,
    Cancelled = 80,
    ImageIncomplete = 81,
    Internal = 99,
}

public sealed record Error(ErrorCode Code, string Message, string? Detail = null)
{
    public static Error Of(ErrorCode code, string message, string? detail = null) => new(code, message, detail);
    public override string ToString() => Detail is null ? $"{(int)Code} {Message}" : $"{(int)Code} {Message} ({Detail})";
}

public abstract class Result
{
    private readonly Error? _error;
    public bool IsSuccess => _error is null;
    public Error Error => _error ?? throw new InvalidOperationException("Result is success; branch on IsSuccess first.");
    protected Result(Error? error) => _error = error;
    public static Result Ok() => new ConcreteOk();
    public static Result Fail(Error error) => new ConcreteFail(error ?? throw new ArgumentNullException(nameof(error)));
    public static implicit operator Result(Error error) => Fail(error);
    private sealed class ConcreteOk : Result { public ConcreteOk() : base(null) { } }
    private sealed class ConcreteFail : Result { public ConcreteFail(Error e) : base(e) { } }
}

public sealed class Result<T> : Result
{
    private readonly T? _value;
    private Result(T value) : base(null) => _value = value;
    private Result(Error error) : base(error) => _value = default;
    public T Value => IsSuccess ? _value! : throw new InvalidOperationException("Result is failure; branch on IsSuccess first.");
    public static Result<T> Ok(T value) => new(value);
    public static new Result<T> Fail(Error error) => new(error);
    public static implicit operator Result<T>(T value) => Ok(value);
    public static implicit operator Result<T>(Error error) => Fail(error);
}
