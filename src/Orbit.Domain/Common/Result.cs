namespace Orbit.Domain.Common;

public class Result
{
    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public string Error { get; }

    protected Result(bool isSuccess, string error)
    {
        if (isSuccess && error != string.Empty)
            throw new InvalidOperationException("A successful result cannot carry an error message.");

        if (!isSuccess && error == string.Empty)
            throw new InvalidOperationException("A failed result must carry an error message.");

        IsSuccess = isSuccess;
        Error = error;
    }

    public static Result Success() => new(true, string.Empty);
    public static Result Failure(string error) => new(false, error);
    public static Result<T> Success<T>(T value) => new(value, true, string.Empty);
    public static Result<T> Failure<T>(string error) => new(default, false, error);
}

public class Result<T>(T? value, bool isSuccess, string error) : Result(isSuccess, error)
{
    public T Value => IsSuccess
        ? value!
        : throw new InvalidOperationException("Cannot access the value of a failed result.");
}
