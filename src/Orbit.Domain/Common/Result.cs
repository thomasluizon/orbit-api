namespace Orbit.Domain.Common;

public class Result
{
    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public string Error { get; }
    public string? ErrorCode { get; }

    protected Result(bool isSuccess, string error, string? errorCode = null)
    {
        if (isSuccess && error != string.Empty)
            throw new InvalidOperationException("A successful result cannot carry an error message.");

        if (!isSuccess && error == string.Empty)
            throw new InvalidOperationException("A failed result must carry an error message.");

        IsSuccess = isSuccess;
        Error = error;
        ErrorCode = errorCode;
    }

    public static Result Success() => new(true, string.Empty);
    public static Result<T> Success<T>(T value) => new(value, true, string.Empty);
    public static Result Failure(string error) => new(false, error);
    public static Result Failure(string error, string errorCode) => new(false, error, errorCode);
    public static Result<T> Failure<T>(string error) => new(default, false, error);
    public static Result<T> Failure<T>(string error, string errorCode) => new(default, false, error, errorCode);
    public static Result PayGateFailure(string error) => new(false, error, "PAY_GATE");
    public static Result<T> PayGateFailure<T>(string error) => new(default, false, error, "PAY_GATE");
}

public class Result<T> : Result
{
    private readonly T? _value;

    public Result(T? value, bool isSuccess, string error, string? errorCode = null)
        : base(isSuccess, error, errorCode)
    {
        _value = value;
    }

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("Cannot access the value of a failed result.");
}
