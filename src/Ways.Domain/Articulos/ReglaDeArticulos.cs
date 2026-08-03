using Ways.Domain.Common;

namespace Ways.Domain.Articulos;

/// <summary>
/// Regla de negocio pura, sin dependencias (spec: Availability Model, escenario "Restricting
/// availability requires at least one subset row") — se testea sin base de datos (mismo
/// criterio que <see cref="Clientes.ReglaDeClientes"/>/<see cref="Catalogos.ReglaDeCategorias"/>).
/// El guard vive acá, no solo en la UI: todo camino de edición de <see cref="Articulo"/> que
/// deje <see cref="Articulo.DisponibleParaTodas"/> en <c>false</c> tiene que pasar por acá antes
/// de tocar la fila.
///
/// <para>judgment-day ronda 1 (root cause de un par de CRITICAL): valida el ESTADO RESULTANTE,
/// no la transición. Antes de este fix la regla solo disparaba en el pasaje <c>true -&gt;
/// false</c> — un artículo YA restringido que se volvía a guardar con <c>IdsEmpresas</c> en
/// <c>null</c> (false -&gt; false, sin transición) esquivaba el guard acá y reventaba más abajo,
/// en <c>ServicioDeArticulos.ExigirEmpresasValidasAsync</c>, con un <see
/// cref="NullReferenceException"/> al iterar la lista nula. El estado "restringido sin ninguna
/// fila de subset" es inválido sin importar de dónde vino.</para>
/// </summary>
public static class ReglaDeArticulos
{
    /// <summary>Rechaza cualquier estado resultante <c>disponibleParaTodasNuevo = false</c> sin
    /// al menos una fila de <see cref="ArticuloEmpresa"/> — no hay backstop de esquema para
    /// esta regla (design: Protection Rules, "none" a nivel DB, service-only esta etapa): a
    /// diferencia de <c>ux_articulos_codigo_interno</c>/<c>ux_codigos_barra_codigo_tenant</c>,
    /// no hay una constraint SQL equivalente a "al menos una fila de otra tabla" sin un
    /// trigger, que queda fuera de alcance de esta etapa.</summary>
    public static void ValidarRestriccionDeDisponibilidad(bool disponibleParaTodasNuevo, int cantidadDeFilasSubset)
    {
        if (!disponibleParaTodasNuevo && cantidadDeFilasSubset == 0)
        {
            throw new ErrorDominio(
                "subset_de_empresas_requerido",
                "Para restringir la disponibilidad del artículo hay que indicar al menos una empresa.",
                400);
        }
    }
}
