using System.Text.Json.Serialization;

namespace Reconciliation.Contracts;

[JsonSourceGenerationOptions(System.Text.Json.JsonSerializerDefaults.Web, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(RefreshBody))]
[JsonSerializable(typeof(SessionTokens))]
[JsonSerializable(typeof(CompanyItem[]))]
[JsonSerializable(typeof(OverviewResponse))]
[JsonSerializable(typeof(ReviewPage))]
[JsonSerializable(typeof(DecisionRequest))]
[JsonSerializable(typeof(DecisionResponse))]
[JsonSerializable(typeof(InvoicePage))]
[JsonSerializable(typeof(InvoiceSummary))]
[JsonSerializable(typeof(ImportReceipt))]
[JsonSerializable(typeof(RunReceipt))]
[JsonSerializable(typeof(ImportReportResponse))]
[JsonSerializable(typeof(JobResponse))]
[JsonSerializable(typeof(MovementPage))]
[JsonSerializable(typeof(ClassificationRequest))]
[JsonSerializable(typeof(System.Text.Json.JsonElement))]
public partial class WebJsonContext : JsonSerializerContext;
