using Ways.Domain.Common;

namespace Ways.Application.Exportacion;

/// <summary>
/// Parsea el parámetro de query <c>formato</c> de toda ruta <c>/export</c> (design decisión 9):
/// se bindea como <c>string</c> y se valida acá, nunca como enum bindeado por el framework — el
/// 400 automático de un bindeo de enum fallido no lleva <c>codigo</c>, indistinguible de un
/// <c>desde</c> mal formado, y el spec fija el código. En v1 hay un único valor legal:
/// <c>"xlsx"</c>. <c>formato</c> ausente en la query string queda como 400 de framework
/// (parámetro requerido no-nullable) — no necesita código acá.
/// </summary>
public static class FormatoDeExportacion
{
    public const string Xlsx = "xlsx";

    public static string Parsear(string valor)
    {
        if (!string.Equals(valor, Xlsx, StringComparison.OrdinalIgnoreCase))
        {
            throw new ErrorDominio(
                "formato_no_soportado",
                $"El formato \"{valor}\" no está soportado. Formatos válidos: {Xlsx}.",
                400);
        }

        return Xlsx;
    }
}
