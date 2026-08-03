using Ways.Domain.Common;

namespace Ways.Domain.Articulos;

/// <summary>
/// Regla de negocio pura, sin dependencias (spec: Availability Model, escenario "Restricting
/// availability requires at least one subset row") — se testea sin base de datos (mismo
/// criterio que <see cref="Clientes.ReglaDeClientes"/>/<see cref="Catalogos.ReglaDeCategorias"/>).
/// El guard vive acá, no solo en la UI: todo camino de edición de <see cref="Articulo"/> que
/// intente restringir <see cref="Articulo.DisponibleParaTodas"/> a <c>false</c> tiene que pasar
/// por acá antes de tocar la fila.
/// </summary>
public static class ReglaDeArticulos
{
    /// <summary>Rechaza el pasaje de <c>true</c> a <c>false</c> cuando no se provee al menos
    /// una fila de <see cref="ArticuloEmpresa"/> — no hay backstop de esquema para esta regla
    /// (design: Protection Rules, "none" a nivel DB, service-only esta etapa): a diferencia de
    /// <c>ux_articulos_codigo_interno</c>/<c>ux_codigos_barra_codigo_tenant</c>, no hay una
    /// constraint SQL equivalente a "al menos una fila de otra tabla" sin un trigger, que
    /// queda fuera de alcance de esta etapa.</summary>
    public static void ValidarRestriccionDeDisponibilidad(
        bool disponibleParaTodasActual, bool disponibleParaTodasNuevo, int cantidadDeFilasSubset)
    {
        var pasaDeDisponibleATodasARestringido = disponibleParaTodasActual && !disponibleParaTodasNuevo;

        if (pasaDeDisponibleATodasARestringido && cantidadDeFilasSubset == 0)
        {
            throw new ErrorDominio(
                "disponibilidad_restriccion_sin_subset",
                "Para restringir la disponibilidad del artículo hay que indicar al menos una empresa.",
                400);
        }
    }
}
