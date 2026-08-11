using Ways.Domain.Common;

namespace Ways.Domain.Catalogos;

/// <summary>
/// Registro tipado de las claves de <see cref="Parametro"/> que el sistema conoce (ADR-13):
/// clave, tipo CLR y default declarado. Una fila ausente en <c>parametros</c> devuelve este
/// default documentado en vez de tirar error, y el ABM (etapa 4) va a poder renderizar el
/// editor correcto a partir del tipo declarado.
///
/// Claves de doc 10 §9: tolerancia de pago, vuelto máximo, adicional de recarga, slots de
/// tickets en espera. La lista es abierta — agregar una clave nueva es agregar una entrada
/// acá, no una migración.
/// </summary>
public sealed record ParametroConocido(string Clave, Type TipoClr, string ValorPorDefecto)
{
    public static readonly ParametroConocido ToleranciaPago =
        new("tolerancia_pago", typeof(decimal), "10");

    public static readonly ParametroConocido VueltoMaximo =
        new("vuelto_maximo", typeof(decimal), "20");

    public static readonly ParametroConocido ImporteAdicionalRecarga =
        new("importe_adicional_recarga", typeof(decimal), "5");

    public static readonly ParametroConocido SlotsTicketsEspera =
        new("slots_tickets_espera", typeof(int), "10");

    /// <summary>Zona horaria IANA usada para el bucketing de los reportes de gestión
    /// (stage-10): resuelve punto de venta → empresa → default como cualquier otro parámetro.
    /// Es el primer parámetro string-tipado del registro — el default va entre comillas porque
    /// <c>ResolucionDeParametros.Resolver</c> lo devuelve verbatim y los lectores lo
    /// deserializan con <c>JsonSerializer.Deserialize&lt;string&gt;</c>.</summary>
    public static readonly ParametroConocido ZonaHoraria =
        new("zona_horaria", typeof(string), "\"America/Argentina/Buenos_Aires\"");

    /// <summary>Porcentaje de comisión del reporte provisional de comisiones (stage-10):
    /// default <c>0</c>, es decir comisiones calculadas pero siempre en cero hasta que un
    /// Admin configure una tasa.</summary>
    public static readonly ParametroConocido ComisionPorcentaje =
        new("comision_porcentaje", typeof(decimal), "0");

    private static readonly IReadOnlyDictionary<string, ParametroConocido> PorClave =
        new[]
        {
            ToleranciaPago, VueltoMaximo, ImporteAdicionalRecarga, SlotsTicketsEspera,
            ZonaHoraria, ComisionPorcentaje
        }.ToDictionary(p => p.Clave, p => p, StringComparer.OrdinalIgnoreCase);

    /// <summary>Devuelve el registro de <paramref name="clave"/>, o rechaza con un error de
    /// dominio si no es una clave conocida (evita que un typo en el llamador se resuelva
    /// silenciosamente en "ningún valor").</summary>
    public static ParametroConocido Buscar(string clave)
    {
        if (!PorClave.TryGetValue(clave, out var conocido))
        {
            throw new ErrorDominio(
                "parametro_desconocido", $"'{clave}' no es un parámetro conocido.", 400);
        }

        return conocido;
    }
}
