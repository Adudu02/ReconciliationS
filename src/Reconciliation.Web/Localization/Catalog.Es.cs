namespace Reconciliation.Web.Localization;

public static partial class Catalog
{
    public static IReadOnlyDictionary<string, string> Es { get; } = new Dictionary<string, string>
    {
        ["nav.overview"] = "Resumen",
        ["nav.review"] = "Bandeja de revisión",
        ["nav.invoices"] = "Facturas",
        ["nav.movements"] = "Movimientos",
        ["nav.imports"] = "Importar archivos",

        ["topbar.switch-company"] = "Cambiar de empresa",
        ["topbar.sign-out"] = "Cerrar sesión",
        ["topbar.demo"] = "Demostración",

        ["login.title"] = "Iniciar sesión",
        ["login.eyebrow"] = "Acceso",
        ["login.intro"] = "Ingrese con su correo electrónico y contraseña para revisar la conciliación de su empresa.",
        ["login.email"] = "Correo electrónico",
        ["login.password"] = "Contraseña",
        ["login.sign-in"] = "Iniciar sesión",
        ["login.signing-in"] = "Iniciando sesión…",
        ["login.service-unavailable"] = "El servicio no está disponible en este momento. Intente de nuevo en unos minutos.",

        ["notice.disclaimer"] = "Demostración: los resultados no tienen validez fiscal ni legal.",

        ["import.files-selected"] = "{0} archivo(s) seleccionado(s)",

        ["state.pending"] = "Pendiente",
        ["state.suggested"] = "Sugerido",
        ["state.reconciled"] = "Conciliado",
        ["state.received"] = "Recibido",
        ["state.overdue"] = "Vencido",
        ["state.missing"] = "Faltante",
        ["state.rejected"] = "Rechazado",
        ["state.cancelled"] = "Cancelado",
        ["state.closed"] = "Cerrado",

        ["confidence.high"] = "Alta",
        ["confidence.medium"] = "Media",
        ["confidence.low"] = "Baja",

        ["role.administrator"] = "Administrador",
        ["role.accountant"] = "Contador",
        ["role.viewer"] = "Lector",

        ["direction.issued"] = "Emitida",
        ["direction.received"] = "Recibida",

        ["jobstate.queued"] = "En cola",
        ["jobstate.running"] = "En ejecución",
        ["jobstate.succeeded"] = "Completado",
        ["jobstate.failed"] = "Fallido",

        ["complement.received"] = "Recibido",
        ["complement.overdue"] = "Vencido",
        ["complement.missing"] = "Faltante",

        ["classification.loan"] = "Préstamo",
        ["classification.partner_contribution"] = "Aportación de socios",
        ["classification.tax_payment"] = "Pago de impuestos",
        ["classification.late_invoicing"] = "Facturación tardía",
        ["classification.other"] = "Otro",

        ["criterion.payment-complement"] = "el complemento de pago emitido hace referencia al UUID de la factura; monto exacto del movimiento y fecha de pago dentro de la tolerancia",
        ["criterion.identifier-without-amount"] = "identificador sin coincidencia de monto; la asignación está limitada por ambos saldos restantes",
        ["criterion.rfc"] = "el RFC del emisor o del receptor aparece en la descripción",
        ["criterion.invoice-reference"] = "la serie/folio de la factura o un fragmento del UUID aparece en el texto",
        ["criterion.operation-number"] = "la referencia es igual al número de operación",
        ["criterion.amount-equals-total"] = "el monto es igual al total neto del CFDI",
        ["criterion.amount-equals-total.credit-notes"] = "el monto es igual al neto a pagar después de notas de crédito",
        ["criterion.gross-payment"] = "el monto es igual al total antes de retenciones",
        ["criterion.amount-equals-remaining"] = "el monto es igual al saldo pendiente de pago",
        ["criterion.amount-within-tolerance"] = "el monto está dentro de la tolerancia respecto al monto esperado",
        ["criterion.amount-within-tolerance.detail"] = "el monto está dentro de la tolerancia del objetivo {0}; diferencia de {1} centavos",
        ["criterion.target.total"] = "total",
        ["criterion.target.gross"] = "bruto (monto igual al total antes de retenciones)",
        ["criterion.target.remaining"] = "saldo pendiente de pago",
        ["criterion.date-proximity"] = "la fecha de la factura está dentro de la tolerancia",
        ["criterion.date-proximity.bonus"] = "la fecha de la factura está dentro de la tolerancia; bonificación por cercanía de {0}",
        ["criterion.replacement-of-cancelled-invoice"] = "sustitución de una factura cancelada",

        ["reason.fully-credited"] = "Totalmente acreditada con notas de crédito",
        ["reason.global-invoice"] = "Factura global",
        ["reason.overpaid"] = "Pagada en exceso",
        ["reason.cancelled-credit-note"] = "Nota de crédito cancelada",
        ["reason.credit-note-in-a-different-currency"] = "Nota de crédito en otra moneda",
        ["reason.cancelled-invoice"] = "Factura cancelada",
        ["reason.cancelled-payment-complement"] = "Complemento de pago cancelado",
        ["reason.complement-overdue"] = "Complemento de pago vencido",
        ["reason.efos-warning"] = "Alerta de lista EFOS del SAT",
        ["reason.inconsistent-balance"] = "Saldo inconsistente en el complemento de pago",

        ["explanation.equal-matches"] = "{0} facturas más coincidieron por igual",
        ["explanation.capped-matches"] = "{0}+ facturas más podrían haber coincidido por igual",
    };
}
