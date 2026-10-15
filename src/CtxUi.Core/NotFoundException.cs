namespace CtxUi.Core;

public sealed class NotFoundException(string message) : DomainException(message);
