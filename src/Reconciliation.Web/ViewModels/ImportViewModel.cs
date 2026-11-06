using Microsoft.AspNetCore.Components.Forms;
using Reconciliation.Contracts;

namespace Reconciliation.Web.ViewModels;

public sealed class ImportViewModel(ApiClient api, JobProgressViewModel jobs, SessionState session) : PageViewModel
{
    public const int MaxFileBytes = 10 * 1024 * 1024;
    public const int MaxTotalBytes = 20 * 1024 * 1024;
    public const int MaxFiles = 20;
    public string Kind { get; set; } = "cfdi";
    public string Bank { get; set; } = "";
    public string AccountReference { get; set; } = "";
    public string[] Banks { get; private set; } = [];
    public BrowserFileUpload[] Files { get; private set; } = [];
    public ImportReportResponse? Report { get; private set; }
    public JobProgressViewModel Jobs => jobs;
    public Task LoadBanksAsync() => RunAsync(async () =>
    {
        Banks = await api.GetAsync<string[]>(api.CompanyPath("banks"));
        Bank = Banks.FirstOrDefault() ?? "";
    });
    public Task SelectFilesAsync(IReadOnlyList<IBrowserFile> selected) => RunAsync(async () =>
    {
        Files = [];
        if (selected.Count is 0 or > MaxFiles || selected.Sum(file => file.Size) > MaxTotalBytes || selected.Any(file => file.Size > MaxFileBytes))
        { Error = "Choose 1–20 files, up to 10 MiB each and 20 MiB in total."; return; }
        var files = new List<BrowserFileUpload>();
        try
        {
            foreach (var file in selected)
            {
                await using var stream = file.OpenReadStream(MaxFileBytes);
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer);
                files.Add(new(file.Name, buffer.ToArray()));
            }
            Files = files.ToArray();
        }
        catch (IOException) { Error = "The selected files could not be read within the upload limit."; }
    });
    public Task UploadAsync(IReadOnlyList<BrowserFileUpload> files, CancellationToken token = default) => RunAsync(async () =>
    {
        Report = null;
        if (!session.CanWrite) { Error = "This workspace is read-only for your membership."; return; }
        if (Kind is not ("cfdi" or "bank-statement") || files.Count is 0 or > MaxFiles || files.Any(file => file.Content.Length > MaxFileBytes) || files.Sum(file => (long)file.Content.Length) > MaxTotalBytes ||
            (Kind == "bank-statement" && (files.Count != 1 || Bank.Length == 0 || string.IsNullOrWhiteSpace(AccountReference))))
        { Error = "Choose valid files and a bank/account reference for one statement. Limits: 20 files, 10 MiB each, 20 MiB total."; return; }
        var receipt = await api.ImportAsync(Kind, files, Bank, AccountReference, token);
        await jobs.WatchAsync(receipt.JobId, token);
        if (jobs.Current?.State is "succeeded" or "failed")
            Report = await api.GetAsync<ImportReportResponse>(api.CompanyPath($"imports/{receipt.UploadId}"), token);
        if (jobs.Error is not null) Error = jobs.Error;
    });
}
