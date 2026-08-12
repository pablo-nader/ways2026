using Ways.Application.Abstracciones;
using Ways.Application.Exportacion;

namespace Ways.Api.Exportacion;

/// <summary>
/// Arma el <see cref="ContextoDeExportacion"/> de una request HTTP — el plomero compartido que
/// todo <c>/export</c> sibling de esta y las próximas slices reusa (design: Data Flow). Usuario y
/// reloj salen de los mismos servicios que ya inyecta el resto de la API, nunca de una segunda
/// derivación de negocio. Empresa y punto de venta se identifican por id (<see cref="ContextoDeExportacion.Empresa"/>
/// es el id solo, sin repetir la etiqueta que ya pone el encabezado del XLSX; <c>PuntoVenta</c> es
/// <c>"PV {id}"</c>), el mismo criterio "ids, no nombres" de <c>NombreDeArchivo</c> (design
/// decisión 7) — evita una consulta extra solo para mostrar una razón social en el encabezado.
/// </summary>
public static class ContextoDeExportacionHttp
{
    public static ContextoDeExportacion Construir(
        IContextoDeUsuario usuario,
        IRelojDelSistema reloj,
        int idEmpresa,
        int? idPuntoVenta,
        DateOnly desde,
        DateOnly hasta,
        string zonaHoraria,
        string? cobertura = null) =>
        Construir(
            usuario, reloj, idEmpresa.ToString(), idPuntoVenta is { } id ? $"PV {id}" : null, desde, hasta,
            zonaHoraria, cobertura);

    /// <summary>
    /// Sobrecarga genérica (stage-11, Slice 3): los exports de listado (ventas/compras/estado de
    /// cuenta) no tienen un <c>idEmpresa</c> propio en su ruta fuente — a diferencia de los
    /// reportes de gestión, esas rutas nunca lo pidieron. <paramref name="empresa"/> ya viene
    /// resuelto como texto por el caller ("Todas" cuando el listado no está acotado a una sola
    /// empresa, el id cuando sí).
    /// </summary>
    public static ContextoDeExportacion Construir(
        IContextoDeUsuario usuario,
        IRelojDelSistema reloj,
        string empresa,
        string? puntoVenta,
        DateOnly desde,
        DateOnly hasta,
        string zonaHoraria,
        string? cobertura = null) =>
        new(
            Empresa: empresa,
            PuntoVenta: puntoVenta,
            Desde: desde,
            Hasta: hasta,
            ZonaHoraria: zonaHoraria,
            Usuario: usuario.NombreUsuario,
            GeneradoEl: reloj.Ahora,
            Cobertura: cobertura);
}
