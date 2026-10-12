using System.Text.Json.Serialization;
using Ways.Domain.Catalogos;

namespace Ways.Application.Catalogos;

/// <summary>Campos comunes de alta/edición que todo catálogo comparte (ADR-11): cada
/// catálogo agrega el resto en su propio registro derivado.</summary>
public abstract record AltaDeCatalogo(string Nombre, int? IdEmpresa, bool Activo = true);

/// <summary>Campos comunes de listado que todo catálogo comparte (ADR-11).</summary>
public abstract record ListadoDeCatalogo(int Id, string Nombre, bool Activo, int? IdEmpresa);

public sealed record AreaAlta(string Nombre, int? IdEmpresa, int Orden, bool Activo = true)
    : AltaDeCatalogo(Nombre, IdEmpresa, Activo);

public sealed record AreaListado(int Id, string Nombre, bool Activo, int? IdEmpresa, int Orden)
    : ListadoDeCatalogo(Id, Nombre, Activo, IdEmpresa);

public sealed record MarcaAlta(string Nombre, int? IdEmpresa, bool Activo = true)
    : AltaDeCatalogo(Nombre, IdEmpresa, Activo);

public sealed record MarcaListado(int Id, string Nombre, bool Activo, int? IdEmpresa)
    : ListadoDeCatalogo(Id, Nombre, Activo, IdEmpresa);

public sealed record GrupoAlta(string Nombre, int? IdEmpresa, decimal? Margen, bool Activo = true)
    : AltaDeCatalogo(Nombre, IdEmpresa, Activo);

public sealed record GrupoListado(int Id, string Nombre, bool Activo, int? IdEmpresa, decimal? Margen)
    : ListadoDeCatalogo(Id, Nombre, Activo, IdEmpresa);

/// <summary>Alta y edición de un medio de pago. <c>PorcentajeFacturacion</c> y los cuatro campos de la cuenta de
/// tesorería son opcionales: ausentes, la edición conserva lo guardado y el alta nace sin cuenta y en 0; así un cliente
/// que no los conoce no los pisa. Enviados se aplican, incluido <c>IdCuentaTesoreria</c> en null (quita la cuenta) y un
/// porcentaje en 0. En los porcentajes, null equivale a ausente. Un medio que no es electrónico no tiene cuenta ni
/// porcentajes: ver <see cref="ReglasDeCuentaDeMedioPago.Resolver"/>.</summary>
public sealed record MedioPagoAlta(
    string Nombre,
    int? IdEmpresa,
    int Orden,
    ComportamientoMedioPago Comportamiento,
    bool AdmiteVuelto,
    bool RequiereReferencia,
    decimal? RecargoPorcentaje,
    decimal? PorcentajeFacturacion = null,
    bool Activo = true,
    decimal? ComisionPorcentaje = null,
    decimal? RetencionIibbPorcentaje = null,
    decimal? RetencionIvaPorcentaje = null,
    decimal? RetencionGananciasPorcentaje = null) : AltaDeCatalogo(Nombre, IdEmpresa, Activo)
{
    /// <summary>Propiedad y no parámetro del constructor: un parámetro de tipo struct con valor por defecto hace que el
    /// generador de OpenAPI intente serializar ese default y rompa el documento. En el esquema figura como entero o
    /// null (<c>EsquemaDeCampoOpcional</c>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public CampoOpcional<int?> IdCuentaTesoreria { get; init; }
}

/// <summary>Listado de medios de pago. Una cuenta dada de baja se lista como "sin cuenta" (<c>IdCuentaTesoreria</c>
/// null); una cuenta desactivada se lista con su nombre y <c>CuentaTesoreriaInactiva</c> en true. La cuenta, la comisión
/// y las retenciones solo las ve quien administra el catálogo (Admin); para el resto vienen en null, el mismo mecanismo que
/// los costos pero con otros roles (los costos también los ve el Supervisor). Para el Admin <c>CuentaTesoreriaInactiva</c>
/// siempre es un booleano (false si el medio no tiene cuenta); null significa solo "oculto para este rol". El POS y las
/// pantallas de operación leen este mismo listado.</summary>
public sealed record MedioPagoListado(
    int Id,
    string Nombre,
    bool Activo,
    int? IdEmpresa,
    int Orden,
    ComportamientoMedioPago Comportamiento,
    bool AdmiteVuelto,
    bool RequiereReferencia,
    decimal? RecargoPorcentaje,
    decimal PorcentajeFacturacion,
    int? IdCuentaTesoreria,
    decimal? ComisionPorcentaje,
    decimal? RetencionIibbPorcentaje,
    decimal? RetencionIvaPorcentaje,
    decimal? RetencionGananciasPorcentaje,
    string? NombreCuentaTesoreria = null,
    bool? CuentaTesoreriaInactiva = null) : ListadoDeCatalogo(Id, Nombre, Activo, IdEmpresa);

public sealed record CategoriaAlta(
    string Nombre, int? IdEmpresa, int Orden, int? IdCategoriaPadre, bool Activo = true)
    : AltaDeCatalogo(Nombre, IdEmpresa, Activo);

public sealed record CategoriaListado(
    int Id, string Nombre, bool Activo, int? IdEmpresa, int Orden, int? IdCategoriaPadre)
    : ListadoDeCatalogo(Id, Nombre, Activo, IdEmpresa);

/// <summary>stage-3-articulos-y-precios, Slice 4: un solo contrato para alta Y edición
/// (ADR-11, mismo criterio que <see cref="CategoriaAlta"/>) — deliberadamente NO
/// <c>AltaListaPrecio</c>/<c>EdicionListaPrecio</c> separados (tasks.md 4.2 los nombraba así,
/// pero <see cref="ServicioDeListasPrecio"/> extiende <see cref="ServicioDeCatalogo{T,TL,TA}"/>,
/// design decision 2 — ese genérico exige un único <c>TAlta</c> para
/// <c>CrearAsync</c>/<c>ActualizarAsync</c>; partir el contrato en dos hubiera forzado a
/// abandonar la reutilización del genérico, que es justamente lo que la design pide reusar).
/// <see cref="EsDefault"/>/<see cref="Modo"/>/<see cref="IdListaBase"/>/<see cref="Porcentaje"/>
/// son las cuatro columnas propias que <c>CatalogoSimple</c> no cubre.</summary>
public sealed record ListaPrecioAlta(
    string Nombre,
    int? IdEmpresa,
    bool EsDefault,
    ModoLista Modo,
    int? IdListaBase,
    decimal? Porcentaje,
    bool Activo = true) : AltaDeCatalogo(Nombre, IdEmpresa, Activo);

public sealed record ListaPrecioListado(
    int Id,
    string Nombre,
    bool Activo,
    int? IdEmpresa,
    bool EsDefault,
    ModoLista Modo,
    int? IdListaBase,
    decimal? Porcentaje) : ListadoDeCatalogo(Id, Nombre, Activo, IdEmpresa);

/// <summary>Los 3 catálogos globales (ADR-11, gate #4) son de solo lectura para la API en
/// esta etapa — sin ABM, sin alta/edición — así que solo llevan un listado, no un alta.</summary>
public sealed record CondicionFiscalListado(int Id, string Codigo, string Nombre, short? CodigoAfip, bool Activo);

public sealed record AlicuotaIvaListado(int Id, string Nombre, decimal Porcentaje, short? CodigoAfip, bool Activo);

public sealed record TipoComprobanteListado(
    int Id,
    ClaseComprobante Clase,
    string Codigo,
    string Nombre,
    char? Letra,
    short Signo,
    bool DiscriminaIva,
    bool EsFiscal,
    bool AfectaStock,
    short? CodigoAfip,
    bool Activo,
    bool RegistraLibroIva);
