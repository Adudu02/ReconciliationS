using System.Text;
using System.Xml;
using Reconciliation.Core.Ingestion;

namespace Reconciliation.Tests;

public sealed class SafeXmlTests
{
    [Theory]
    [InlineData("<!DOCTYPE root [<!ENTITY xxe SYSTEM 'file:///synthetic-secret'>]><root>&xxe;</root>")]
    [InlineData("<!DOCTYPE root SYSTEM 'http://127.0.0.1:1/synthetic.dtd'><root/>")]
    [InlineData("<!DOCTYPE root [<!ENTITY a 'aaa'><!ENTITY b '&a;&a;&a;'>]><root>&b;</root>")]
    public void External_entities_DTDs_and_entity_expansion_are_prohibited(string xml)
    {
        Assert.Throws<XmlException>(() => new SafeCfdiXml(new IngestionOptions()).Read(Encoding.UTF8.GetBytes(xml)));
    }

    [Fact]
    public void Size_node_count_and_depth_are_bounded()
    {
        Assert.Throws<InvalidDataException>(() => new SafeCfdiXml(new IngestionOptions { MaxFileBytes = 10 }).Read(Encoding.UTF8.GetBytes("<root>oversize</root>")));
        Assert.Throws<InvalidDataException>(() => new SafeCfdiXml(new IngestionOptions { MaxXmlNodes = 3 }).Read(Encoding.UTF8.GetBytes("<root><a/><b/><c/></root>")));
        Assert.Throws<InvalidDataException>(() => new SafeCfdiXml(new IngestionOptions { MaxXmlDepth = 2 }).Read(Encoding.UTF8.GetBytes("<a><b><c><d/></c></b></a>")));
    }

    [Fact]
    public async Task Synthetic_CFDI_is_read_without_following_schema_network_locations()
    {
        var path = Path.GetFullPath("../../../../../samples/synthetic-income-ppd-retention.xml", AppContext.BaseDirectory);
        var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var root = new SafeCfdiXml(new IngestionOptions()).Read(bytes).Root!;
        Assert.Equal("Comprobante", root.Name.LocalName);
        Assert.Equal("1939.27", root.Attribute("Total")!.Value);
    }
}
