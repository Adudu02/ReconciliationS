namespace Reconciliation.Core.Sat;

public interface ISatClient
{
    Task<SatStatusResponse> CheckAsync(SatStatusRequest request, CancellationToken cancellationToken);
}

public sealed record SatStatusRequest(Guid Uuid, string IssuerRfc, string ReceiverRfc, decimal Total, Guid? AnonymizedUuid = null);
public enum SatCode { Unknown, Success, InvalidExpression, NotFound }
public enum SatState { Unknown, Current, Cancelled, NotFound }
public enum SatCancellability { Unknown, WithoutAcceptance, WithAcceptance, NotCancellable }
public enum SatCancellationStatus { Unknown, None, InProcess, DeadlineExpired, CancelledWithoutAcceptance, CancelledWithAcceptance, Rejected }
public sealed record SatValue<T>(T Value, string Raw) where T : struct, Enum;
public sealed record SatReply(SatValue<SatCode> Code, SatValue<SatState> State, SatValue<SatCancellability> Cancellability,
    SatValue<SatCancellationStatus> CancellationStatus, string? EfosRaw, string RawText)
{
    public bool EfosFlagged => EfosRaw != "200";
}
public abstract record SatStatusResponse(SatReply Reply);
public sealed record SatAnswered(SatReply Reply) : SatStatusResponse(Reply);
public sealed record SatInvalidExpression(SatReply Reply) : SatStatusResponse(Reply);
public sealed record SatNotFound(SatReply Reply) : SatStatusResponse(Reply);
public sealed class SatTransientException(string message, Exception? inner = null) : Exception(message, inner);
public sealed class SatPermanentException(string message, Exception? inner = null) : Exception(message, inner);
