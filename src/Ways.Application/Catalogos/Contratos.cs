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

public sealed record MedioPagoAlta(
    string Nombre,
    int? IdEmpresa,
    int Orden,
    ComportamientoMedioPago Comportamiento,
    bool AdmiteVuelto,
    bool RequiereReferencia,
    decimal? RecargoPorcentaje,
    bool Activo = true) : AltaDeCatalogo(Nombre, IdEmpresa, Activo);

public sealed record MedioPagoListado(
    int Id,
    string Nombre,
    bool Activo,
    int? IdEmpresa,
    int Orden,
    ComportamientoMedioPago Comportamiento,
    bool AdmiteVuelto,
    bool RequiereReferencia,
    decimal? RecargoPorcentaje) : ListadoDeCatalogo(Id, Nombre, Activo, IdEmpresa);

public sealed record CategoriaAlta(
    string Nombre, int? IdEmpresa, int Orden, int? IdCategoriaPadre, bool Activo = true)
    : AltaDeCatalogo(Nombre, IdEmpresa, Activo);

public sealed record CategoriaListado(
    int Id, string Nombre, bool Activo, int? IdEmpresa, int Orden, int? IdCategoriaPadre)
    : ListadoDeCatalogo(Id, Nombre, Activo, IdEmpresa);

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
    bool Activo);
