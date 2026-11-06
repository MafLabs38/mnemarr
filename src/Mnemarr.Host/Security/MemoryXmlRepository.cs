using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;

namespace Mnemarr.Host.Security;

/// <summary>
/// A key ring that lives and dies with the process, used only while the data folder does not exist: ASP.NET creates its keys at start,
/// and writing them to disk would create the folder that /health/ready must find missing. Nothing signed with these keys outlives the process.
/// </summary>
public sealed class MemoryXmlRepository : IXmlRepository
{
    private readonly List<XElement> _elements = [];
    private readonly Lock _gate = new();

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        lock (_gate)
        {
            return [.. _elements];
        }
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        lock (_gate)
        {
            _elements.Add(element);
        }
    }
}
