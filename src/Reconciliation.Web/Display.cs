using System.Globalization;

namespace Reconciliation.Web;

public static class Display
{
    public static string Money(decimal amount) => amount.ToString("N2", CultureInfo.InvariantCulture);
}
