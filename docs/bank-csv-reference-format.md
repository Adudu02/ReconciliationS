# Reference bank CSV format

The supported bank identifier is `reference`. This format is synthetic, not a real bank export.
Upload UTF-8 (an optional BOM is accepted), with LF or CRLF line endings and this exact header:

`fecha,concepto,abono,cargo,saldo`

| Column | Meaning and convention |
|---|---|
| fecha | Date only, exactly `yyyy-MM-dd`; no timezone or locale-dependent date parsing |
| concepto | Nonempty description; CSV double quotes may enclose commas and doubled quotes escape a quote |
| abono | Nonnegative credit, decimal point, at most two decimals; no thousands separator or exponent |
| cargo | Nonnegative debit with the same number rules; exactly one of credit/debit must be positive |
| saldo | Signed running balance with the same number rules; two decimals are preserved exactly |

An empty credit or debit is zero. Blank data lines are ignored. A malformed row rejects the whole statement
with a physical line number and reason; no movements or account are created from that statement.
Quoted fields may span physical lines. Descriptions are trimmed at their outside edges.

`RASTREO:<key>` or `REF:<number>` in concepto records a reference up to the next whitespace. If both occur,
RASTREO takes precedence. Tokens are case-sensitive; no token means a null reference, not a rejected line.

```csv
fecha,concepto,abono,cargo,saldo
2026-10-10,SPEI SINTETICO RASTREO:ABC123 PAGO FACT 0001,5800.00,0.00,5800.00
2026-10-20,COBRO SINTETICO REF:0002,5800.00,0.00,11600.00
2026-10-21,"COMISION SINTETICA, SERVICIO",0.00,10.00,11590.00
```

The import supplies `bank=reference` and `accountReference`. The latter is a synthetic account label, at most
40 letters/digits/spaces/hyphens/underscores (for example `Synthetic-1234`). Never use a full real account number;
for a real installation use an appropriate label such as the last four digits. The account is created or reused
by company, bank identifier and label; there is no account-management API in the MVP.

A movement is unique within its company/account by date, normalized description, credit/debit amount and running
balance. Overlapping exports create only new movements. Otherwise identical rows with different balances are
distinct. Accepted original statement bytes and their SHA-256 hash are retained, with no DELETE endpoint.
