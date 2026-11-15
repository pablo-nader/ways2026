using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ways.Application.Ventas;

namespace Ways.IntegrationTests;

/// <summary>Apoyo de las pruebas que emiten una nota de crédito (NCX): su reintegro tiene que salir en efectivo por exactamente su total
/// (<c>ValidadorDePagos</c>, regla 0c) y el total depende del reparto por unidades acumuladas del ticket de origen, que las pruebas no
/// quieren recalcular.</summary>
internal static class ApoyoDeDevoluciones
{
    /// <summary>Emite la solicitud tal cual; si es una devolución y el servidor la rechaza porque su reintegro no coincide con el total
    /// (<c>devolucion_reintegro_invalido</c>), la reenvía con un único pago en efectivo por <c>datos.reintegro</c>, que el rechazo
    /// informa. Cualquier otro resultado se devuelve sin tocar.</summary>
    public static async Task<HttpResponseMessage> PostearAsync(HttpClient cliente, SolicitudDeVenta solicitud, int idMedioEfectivo)
    {
        var respuesta = await cliente.PostAsJsonAsync("/api/ventas", solicitud);
        if (respuesta.StatusCode != HttpStatusCode.BadRequest)
        {
            return respuesta;
        }

        var problema = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync()).RootElement;
        if (problema.GetProperty("codigo").GetString() != "devolucion_reintegro_invalido")
        {
            return respuesta;
        }

        var reintegro = problema.GetProperty("datos").GetProperty("reintegro").GetDecimal();
        return await cliente.PostAsJsonAsync(
            "/api/ventas", solicitud with { Pagos = [new PagoDeVenta(idMedioEfectivo, reintegro, null, 0m)] });
    }
}
