using System.Xml;
using System.Xml.Linq;

namespace Reconciliation.Core.Ingestion;

public sealed class SafeCfdiXml(IngestionOptions options)
{
    public XDocument Read(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length > options.MaxFileBytes) throw new InvalidDataException("File exceeds MaxFileBytes.");
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = options.MaxFileBytes
        };
        using var stream = new MemoryStream(content, writable: false);
        using (var reader = XmlReader.Create(stream, settings))
        {
            var nodes = 0;
            while (reader.Read())
                if (++nodes > options.MaxXmlNodes || reader.Depth > options.MaxXmlDepth)
                    throw new InvalidDataException("XML exceeds node-count or depth limit.");
        }
        stream.Position = 0;
        using var boundedReader = XmlReader.Create(stream, settings);
        return XDocument.Load(boundedReader);
    }
}
