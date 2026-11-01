using System.Xml;
using System.Xml.Linq;

namespace Reconciliation.Core.Ingestion;

public sealed class SafeCfdiXmlReader(IngestionOptions options)
{
    public XDocument Read(byte[] xmlBytes, int? length = null)
    {
        ArgumentNullException.ThrowIfNull(xmlBytes);
        var count = length ?? xmlBytes.Length;
        if (count > options.MaxFileBytes) throw new InvalidDataException(UploadErrors.FileLimit);
        var readerSettings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = options.MaxFileBytes
        };
        using var xmlStream = new MemoryStream(xmlBytes, 0, count, writable: false);
        using var xmlReader = XmlReader.Create(xmlStream, readerSettings);
        using var boundedReader = new ComplexityReader(xmlReader, options);
        return XDocument.Load(boundedReader);
    }

    private sealed class ComplexityReader(XmlReader xmlReader, IngestionOptions options) : XmlReader
    {
        private int nodes;
        public override bool Read()
        {
            var read = xmlReader.Read();
            if (read && ((nodes += 1 + xmlReader.AttributeCount) > options.MaxXmlNodes || xmlReader.Depth > options.MaxXmlDepth))
                throw new InvalidDataException("XML exceeds node-count or depth limit.");
            return read;
        }
        public override int AttributeCount => xmlReader.AttributeCount;
        public override string BaseURI => xmlReader.BaseURI;
        public override int Depth => xmlReader.Depth;
        public override bool EOF => xmlReader.EOF;
        public override bool IsEmptyElement => xmlReader.IsEmptyElement;
        public override string LocalName => xmlReader.LocalName;
        public override string NamespaceURI => xmlReader.NamespaceURI;
        public override XmlNameTable NameTable => xmlReader.NameTable;
        public override XmlNodeType NodeType => xmlReader.NodeType;
        public override string Prefix => xmlReader.Prefix;
        public override ReadState ReadState => xmlReader.ReadState;
        public override string Value => xmlReader.Value;
        public override string? GetAttribute(string name) => xmlReader.GetAttribute(name);
        public override string? GetAttribute(string name, string? namespaceURI) => xmlReader.GetAttribute(name, namespaceURI);
        public override string GetAttribute(int i) => xmlReader.GetAttribute(i);
        public override string? LookupNamespace(string prefix) => xmlReader.LookupNamespace(prefix);
        public override bool MoveToAttribute(string name) => xmlReader.MoveToAttribute(name);
        public override bool MoveToAttribute(string name, string? ns) => xmlReader.MoveToAttribute(name, ns);
        public override bool MoveToElement() => xmlReader.MoveToElement();
        public override bool MoveToFirstAttribute() => xmlReader.MoveToFirstAttribute();
        public override bool MoveToNextAttribute() => xmlReader.MoveToNextAttribute();
        public override bool ReadAttributeValue() => xmlReader.ReadAttributeValue();
        public override void ResolveEntity() => throw new XmlException("Entity resolution is prohibited.");
    }
}
