using System.Text.Json.Nodes;
using Diagram.Core.Contracts;

namespace Diagram.Host.Core;

/// <summary>Success value or a coded <see cref="AppError"/> (the same error shape the frontend returns).</summary>
public sealed class Result<T>
{
    readonly T? value;
    Result(T? value, AppError? error) { this.value = value; Error = error; }

    public bool IsOk => Error is null;
    public AppError? Error { get; }
    public T Value => IsOk ? value! : throw new InvalidOperationException($"result is an error: {Error!.Code}: {Error.Message}");

    public static Result<T> Ok(T value) => new(value, null);

    public static Result<T> Fail(string code, string message, bool retryable = false, JsonObject? details = null, string? outcome = null) =>
        new(default, new AppError { Code = code, Message = message, Retryable = retryable, Details = details, Outcome = outcome });

    public static Result<T> Fail(AppError error) => new(default, error);

    public Result<TOut> Map<TOut>(Func<T, TOut> f) => IsOk ? Result<TOut>.Ok(f(value!)) : Result<TOut>.Fail(Error!);
}

public readonly record struct Unit;
