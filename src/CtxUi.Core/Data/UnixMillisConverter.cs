using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CtxUi.Core.Data;

public sealed class UnixMillisConverter() : ValueConverter<DateTimeOffset, long>(
    value => value.ToUnixTimeMilliseconds(),
    value => DateTimeOffset.FromUnixTimeMilliseconds(value));
