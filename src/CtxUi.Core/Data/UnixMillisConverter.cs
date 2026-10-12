using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CtxUi.Core.Data;

/// <summary>SQLite не умеет сравнивать DateTimeOffset, поэтому время хранится числом миллисекунд UTC.</summary>
public sealed class UnixMillisConverter() : ValueConverter<DateTimeOffset, long>(
    value => value.ToUnixTimeMilliseconds(),
    value => DateTimeOffset.FromUnixTimeMilliseconds(value));
