using System.Text;
using System.Xml.Linq;

namespace Reconciliation.Core.Sat;

public sealed class SoapSatClient(HttpClient client, Uri endpoint, TimeSpan requestTimeout, TimeProvider clock) : ISatClient
{
    public async Task<SatStatusResponse> CheckAsync(SatStatusRequest request, CancellationToken cancellationToken)
    {
        var expression = SatContract.PrintedExpression(request.Uuid, request.IssuerRfc, request.ReceiverRfc, request.Total);
        using var timeout = new CancellationTokenSource(requestTimeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
        message.Headers.TryAddWithoutValidation("SOAPAction", '"' + SatContract.SoapAction + '"');
        message.Content = new StringContent(new XDocument(new XElement(SatContract.Soap + "Envelope", new XElement(SatContract.Soap + "Body",
            new XElement(SatContract.Service + "Consulta", new XElement(SatContract.Service + "expresionImpresa", expression))))).ToString(), Encoding.UTF8, "text/xml");
        try
        {
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if ((int)response.StatusCode >= 500) throw new SatTransientException("SAT HTTP service unavailable.");
            if (!response.IsSuccessStatusCode) throw new SatPermanentException("SAT HTTP request refused.");
            await using var stream = await response.Content.ReadAsStreamAsync(linked.Token);
            return SatContract.Parse(await SatContract.ReadBoundedAsync(stream, linked.Token));
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        { throw new SatTransientException("SAT request timed out.", error); }
        catch (HttpRequestException error) { throw new SatTransientException("SAT connection failed.", error); }
        catch (IOException error) { throw new SatTransientException("SAT response connection failed.", error); }
    }
}
