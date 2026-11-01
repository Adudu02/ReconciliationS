using System.Xml;
using System.Xml.Linq;

namespace Reconciliation.Core.Ingestion;

public sealed class SafeCfdiXml(IngestionOptions options)
{
    public XDocument Read(byte[] content, int? length = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        var count = length ?? content.Length;
        if (count > options.MaxFileBytes) throw new InvalidDataException("File exceeds MaxFileBytes.");
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = options.MaxFileBytes
        };
        using var stream = new MemoryStream(content, 0, count, writable: false);
        using var reader = XmlReader.Create(stream, settings);
        using var boundedReader = new ComplexityReader(reader, options);
        return XDocument.Load(boundedReader);
    }

    private sealed class ComplexityReader(XmlReader reader, IngestionOptions options) : XmlReader
    {
        private int nodes;
        public override bool Read()
        {
            var read = reader.Read();
            if (read && ((nodes += 1 + reader.AttributeCount) > options.MaxXmlNodes || reader.Depth > options.MaxXmlDepth))
                throw new InvalidDataException("XML exceeds node-count or depth limit.");
            return read;
        }
        public override int AttributeCount => reader.AttributeCount;
        public override string BaseURI => reader.BaseURI;
        public override int Depth => reader.Depth;
        public override bool EOF => reader.EOF;
        public override bool IsEmptyElement => reader.IsEmptyElement;
        public override string LocalName => reader.LocalName;
        public override string NamespaceURI => reader.NamespaceURI;
        public override XmlNameTable NameTable => reader.NameTable;
        public override XmlNodeType NodeType => reader.NodeType;
        public override string Prefix => reader.Prefix;
        public override ReadState ReadState => reader.ReadState;
        public override string Value => reader.Value;
        public override string? GetAttribute(string name) => reader.GetAttribute(name);
        public override string? GetAttribute(string name, string? namespaceURI) => reader.GetAttribute(name, namespaceURI);
        public override string GetAttribute(int i) => reader.GetAttribute(i);
        public override string? LookupNamespace(string prefix) => reader.LookupNamespace(prefix);
        public override bool MoveToAttribute(string name) => reader.MoveToAttribute(name);
        public override bool MoveToAttribute(string name, string? ns) => reader.MoveToAttribute(name, ns);
        public override bool MoveToElement() => reader.MoveToElement();
        public override bool MoveToFirstAttribute() => reader.MoveToFirstAttribute();
        public override bool MoveToNextAttribute() => reader.MoveToNextAttribute();
        public override bool ReadAttributeValue() => reader.ReadAttributeValue();
        public override void ResolveEntity() => throw new XmlException("Entity resolution is prohibited.");
    }
}
