namespace CtxUi.Core;

/// <summary>Нарушено правило предметной области. Текст показывается администратору как есть.</summary>
public sealed class DomainException(string message) : Exception(message);
