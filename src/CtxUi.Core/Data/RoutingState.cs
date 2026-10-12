namespace CtxUi.Core.Data;

/// <summary>Единственная строка со счётчиком изменений. По нему сервер узнаёт, что пора перечитать маршруты.</summary>
public sealed class RoutingState
{
    public int Id { get; set; }
    public long Revision { get; set; }
}
