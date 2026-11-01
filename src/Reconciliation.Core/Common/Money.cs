namespace Reconciliation.Core.Common;

public static class Money
{
    public const decimal MaxAmount = 9999999999999999.99m;
    public static bool HasAtMostTwoDecimals(decimal amount) => decimal.Round(amount, 2) == amount;
    public static bool HasAtMostTwoDecimals(string? declaredAmount)
    {
        if (declaredAmount is null) return false;
        var decimalPoint = declaredAmount.IndexOf('.');
        return decimalPoint < 0 || declaredAmount.Length - decimalPoint - 1 <= 2;
    }
}
