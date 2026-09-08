namespace Verce.SharedKernel.Results;

/// <summary>
/// Outcome of a domain/application operation that can fail in an expected way, without an
/// exception. Application handlers return this; endpoints map failures to stable error codes
/// (STATE-MACHINES §6) — never match on message text.
/// </summary>
public class Result
{
    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public string? ErrorCode { get; }
    public string? ErrorMessage { get; }

    protected Result(bool isSuccess, string? errorCode, string? errorMessage)
    {
        if (isSuccess && errorCode is not null)
            throw new InvalidOperationException("A successful result cannot carry an error code.");
        if (!isSuccess && errorCode is null)
            throw new InvalidOperationException("A failed result must carry an error code.");

        IsSuccess = isSuccess;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
    }

    public static Result Success() => new(true, null, null);
    public static Result Failure(string errorCode, string? errorMessage = null) => new(false, errorCode, errorMessage);

    public static Result<T> Success<T>(T value) => Result<T>.Success(value);
    public static Result<T> Failure<T>(string errorCode, string? errorMessage = null) => Result<T>.Failure(errorCode, errorMessage);
}

/// <summary>A <see cref="Result"/> carrying a value on success.</summary>
public sealed class Result<T> : Result
{
    private readonly T? _value;

    private Result(bool isSuccess, T? value, string? errorCode, string? errorMessage)
        : base(isSuccess, errorCode, errorMessage)
    {
        _value = value;
    }

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Cannot access Value of a failed result (error: {ErrorCode}).");

    public static Result<T> Success(T value) => new(true, value, null, null);
    public static new Result<T> Failure(string errorCode, string? errorMessage = null) => new(false, default, errorCode, errorMessage);
}
