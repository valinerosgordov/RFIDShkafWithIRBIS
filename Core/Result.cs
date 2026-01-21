using System;

namespace LibraryTerminal.Core
{
    /// <summary>
    /// Railway Oriented Programming result type. Never returns null.
    /// </summary>
    public readonly struct Result<T>
    {
        private readonly T _value;
        private readonly string _error;

        public bool IsSuccess { get; }
        public bool IsFailure => !IsSuccess;

        private Result(bool isSuccess, T value, string error)
        {
            IsSuccess = isSuccess;
            _value = value;
            _error = error ?? string.Empty;
        }

        public T Value => IsSuccess ? _value : throw new InvalidOperationException($"Cannot access Value on failed Result: {_error}");
        public string Error => IsFailure ? _error : throw new InvalidOperationException("Cannot access Error on successful Result");

        public static Result<T> Success(T value) => new Result<T>(true, value, null);
        public static Result<T> Failure(string error) => new Result<T>(false, default, error);

        public Result<TOut> Map<TOut>(Func<T, TOut> mapper)
        {
            return IsSuccess ? Result<TOut>.Success(mapper(_value)) : Result<TOut>.Failure(_error);
        }

        public Result<TOut> Bind<TOut>(Func<T, Result<TOut>> binder)
        {
            return IsSuccess ? binder(_value) : Result<TOut>.Failure(_error);
        }

        public T ValueOr(T defaultValue) => IsSuccess ? _value : defaultValue;
        public T ValueOr(Func<T> defaultValueFactory) => IsSuccess ? _value : defaultValueFactory();

        public static implicit operator Result<T>(T value) => Success(value);
    }

    /// <summary>
    /// Non-generic Result for operations without return values.
    /// </summary>
    public readonly struct Result
    {
        private readonly string _error;

        public bool IsSuccess { get; }
        public bool IsFailure => !IsSuccess;

        private Result(bool isSuccess, string error)
        {
            IsSuccess = isSuccess;
            _error = error ?? string.Empty;
        }

        public string Error => IsFailure ? _error : throw new InvalidOperationException("Cannot access Error on successful Result");

        public static Result Success() => new Result(true, null);
        public static Result Failure(string error) => new Result(false, error);

        public Result<TOut> Map<TOut>(Func<TOut> mapper)
        {
            return IsSuccess ? Result<TOut>.Success(mapper()) : Result<TOut>.Failure(_error);
        }

        public Result Bind(Func<Result> binder)
        {
            return IsSuccess ? binder() : Failure(_error);
        }
    }

    /// <summary>
    /// Option type for nullable-like semantics without null.
    /// </summary>
    public readonly struct Option<T>
    {
        private readonly T _value;

        public bool HasValue { get; }

        private Option(bool hasValue, T value)
        {
            HasValue = hasValue;
            _value = value;
        }

        public T Value => HasValue ? _value : throw new InvalidOperationException("Cannot access Value on empty Option");
        public bool IsEmpty => !HasValue;

        public static Option<T> Some(T value) => new Option<T>(true, value);
        public static Option<T> None => new Option<T>(false, default);

        public Option<TOut> Map<TOut>(Func<T, TOut> mapper)
        {
            return HasValue ? Option<TOut>.Some(mapper(_value)) : Option<TOut>.None;
        }

        public Result<T> ToResult(string errorIfNone) => HasValue ? Result<T>.Success(_value) : Result<T>.Failure(errorIfNone);

        public T ValueOr(T defaultValue) => HasValue ? _value : defaultValue;
        public T ValueOr(Func<T> defaultValueFactory) => HasValue ? _value : defaultValueFactory();

        public static implicit operator Option<T>(T value) => value != null ? Some(value) : None;
    }
}
