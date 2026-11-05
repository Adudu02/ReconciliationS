using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Reconciliation.Core.Ingestion;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class MatchingReferenceImportTests(ApiFixture fixture)
{
    [Fact]
    public async Task Serie_and_folio_are_retained_and_duplicate_UUID_upload_never_rewrites_them()
    {
        var (user, company) = await fixture.SeedCompanyWithAdminAsync();
        var uuid = Guid.NewGuid();
        var importer = fixture.App.Services.GetRequiredService<CfdiImporter>();
        foreach (var suffix in new[] { "488", "999" })
        {
            var bytes = SyntheticCfdi.Invoice(root =>
            {
                root.SetAttributeValue("Serie", "FC");
                root.SetAttributeValue("Folio", suffix);
                root.Descendants(SyntheticCfdi.Stamp + "TimbreFiscalDigital").Single().SetAttributeValue("UUID", uuid);
            });
            var result = await importer.ImportAsync(company, user.Id, [UploadedFile.FromBytes("synthetic.xml", bytes)], TestContext.Current.CancellationToken);
            Assert.Equal(suffix == "488" ? "imported" : "already_imported", result.Files[0].Status);
        }
        await using var owner = await fixture.Database.OpenOwnerAsync();
        var stored = await owner.QuerySingleAsync<(string Serie, string Folio)>(new CommandDefinition("SELECT serie,folio FROM cfdi WHERE company_id=@company AND uuid=@uuid", new { company, uuid }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(("FC", "488"), stored);
    }
}
