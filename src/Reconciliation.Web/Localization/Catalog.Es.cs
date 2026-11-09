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
    };
}
