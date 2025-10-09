namespace LibraryTerminal
{
    public sealed class Result
    {
        public bool Success { get; }
        public string Message { get; }

        private Result(bool ok, string msg)
        { Success = ok; Message = msg; }

        public static Result Ok() => new Result(true, null);

        public static Result Fail(string msg) => new Result(false, msg ?? "Ошибка");
    }

    public sealed class Result<T>
    {
        public bool Success { get; }
        public string Message { get; }
        public T Value { get; }

        private Result(bool ok, T value, string msg)
        { Success = ok; Value = value; Message = msg; }

        public static Result<T> Ok(T value) => new Result<T>(true, value, null);

        public static Result<T> Fail(string msg) => new Result<T>(false, default, msg ?? "Ошибка");
    }
}