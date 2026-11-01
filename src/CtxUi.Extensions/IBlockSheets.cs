namespace CtxUi.Extensions;

internal interface IBlockSheets
{
    Task ShowAsync(string title, Type block, object? context);
}
