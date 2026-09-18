namespace Orbit.Domain.Common;

public class Result
{
    public const string PayGateErrorCode = "PAY_GATE";

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public string Error { get; }
    public string? ErrorCode { get; }

    /// <summary>
    /// The arguments behind any {0}-style placeholder in <see cref="Error"/>, carried so the
    /// response boundary can format the localized counterpart selected by <see cref="ErrorCode"/>
    /// with the same values.
    /// </summary>
    public IReadOnlyList<object?> ErrorArgs { get; }

    protected Result(bool isSuccess, string error, string? errorCode = null, IReadOnlyList<object?>? errorArgs = null)
    {
        if (isSuccess && error != string.Empty)
            throw new InvalidOperationException("A successful result cannot carry an error message.");

        if (!isSuccess && error == string.Empty)
            throw new InvalidOperationException("A failed result must carry an error message.");

        IsSuccess = isSuccess;
        Error = error;
        ErrorCode = errorCode;
        ErrorArgs = errorArgs ?? Array.Empty<object?>();
    }

    public static Result Success() => new(true, string.Empty);
    public static Result<T> Success<T>(T value) => new(value, true, string.Empty);
    public static Result Failure(string error) => new(false, error);
    public static Result Failure(string error, string errorCode) => new(false, error, errorCode);
    public static Result Failure(AppError error) => new(false, error.Message, error.Code, error.Args);
    public static Result<T> Failure<T>(string error) => new(default, false, error);
    public static Result<T> Failure<T>(string error, string errorCode) => new(default, false, error, errorCode);
    public static Result<T> Failure<T>(AppError error) => new(default, false, error.Message, error.Code, error.Args);
    public static Result PayGateFailure(string error) => new(false, error, PayGateErrorCode);
    public static Result<T> PayGateFailure<T>(string error) => new(default, false, error, PayGateErrorCode);
}

public class Result<T> : Result
{
    private readonly T? _value;

    public Result(T? value, bool isSuccess, string error, string? errorCode = null, IReadOnlyList<object?>? errorArgs = null)
        : base(isSuccess, error, errorCode, errorArgs)
    {
        _value = value;
    }

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("Cannot access the value of a failed result.");
}
