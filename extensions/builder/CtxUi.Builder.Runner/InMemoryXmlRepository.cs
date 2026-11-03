using System.Collections.Concurrent;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;

namespace CtxUi.Builder.Runner;

public sealed class InMemoryXmlRepository : IXmlRepository
{
    private readonly ConcurrentQueue<XElement> _elements = new();

    public IReadOnlyCollection<XElement> GetAllElements() => _elements.ToArray();

    public void StoreElement(XElement element, string friendlyName) => _elements.Enqueue(element);
}
