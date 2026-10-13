using System.Globalization;
using CtxUi.Core;

namespace CtxUi.Server;

public static class LimitArgument
{
    public const string None = "none";

    public static int? Parse(string option, string text)
    {
        if (text == None)
            return null;

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            throw new DomainException($"{option}: «{text}» не подходит, нужно целое число или {None}, чтобы снять ограничение");

        return value;
    }
}
