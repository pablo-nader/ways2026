using System.Linq.Expressions;
using Ways.Application.Listados;
using Ways.Domain.Usuarios;

namespace Ways.Application.Usuarios;

/// <summary>Columnas de datos de la grilla de usuarios: una por cada columna que la pantalla muestra, menos
/// Acciones.</summary>
public enum ColumnaDeOrdenDeUsuarios
{
    Id,
    Usuario,
    Mail,
    Tenant,
    Rol,
    Estado,
    UltimaConexion,
}

/// <summary>Filtros por columna de <c>GET /api/usuarios</c>. Usuario y mail contienen el texto, sin mayúsculas ni
/// acentos; el resto son exactos. <see cref="Tenant"/> es un id o <see cref="OrdenDeUsuarios.ValorSinTenant"/> y solo
/// lo puede pedir un actor de plataforma. <see cref="Conectado"/> separa a quien alguna vez inició sesión de quien
/// nunca lo hizo. Todos se combinan con Y.</summary>
public sealed record FiltrosDeUsuarios(
    int? Id = null,
    string? Usuario = null,
    string? Mail = null,
    string? Tenant = null,
    int? RolId = null,
    string? Estado = null,
    bool? Conectado = null);

public static class OrdenDeUsuarios
{
    /// <summary>Valor de <c>tenant</c> para el personal de plataforma (la cuenta sin tenant). No es un id: ningún
    /// <c>idTenant</c> puede escribirse así.</summary>
    public const string ValorSinTenant = "sin-tenant";

    /// <summary>Copia que la pantalla muestra en la columna Tenant de una cuenta sin tenant. Vive en la web
    /// (<c>ETIQUETA_PLATAFORMA</c>): el servidor la repite solo para ordenar por el texto que se ve. Dos tests la atan:
    /// <c>LasEtiquetasDeEstadoSonLosNombresDelEnumQueLaWebMuestra</c> fija el literal de este lado y
    /// <c>organizacion.test.ts</c> ("las etiquetas de tenant que el servidor repite para ordenar") fija el de la web.</summary>
    public const string EtiquetaPlataforma = "Plataforma";

    /// <summary>Copia de una cuenta cuyo tenant fue dado de baja (<c>ETIQUETA_SIN_DUENIO</c> en la web), atada por los mismos dos tests que <see cref="EtiquetaPlataforma"/>.</summary>
    public const string EtiquetaSinDuenio = "—";

    /// <summary>Lista cerrada de claves de <c>orden</c>: cada una ordena por lo que la columna muestra.</summary>
    public static readonly IReadOnlyDictionary<string, ColumnaDeOrdenDeUsuarios> Claves =
        new Dictionary<string, ColumnaDeOrdenDeUsuarios>(StringComparer.Ordinal)
        {
            ["id"] = ColumnaDeOrdenDeUsuarios.Id,
            ["usuario"] = ColumnaDeOrdenDeUsuarios.Usuario,
            ["mail"] = ColumnaDeOrdenDeUsuarios.Mail,
            ["tenant"] = ColumnaDeOrdenDeUsuarios.Tenant,
            ["rol"] = ColumnaDeOrdenDeUsuarios.Rol,
            ["estado"] = ColumnaDeOrdenDeUsuarios.Estado,
            ["ultimaConexion"] = ColumnaDeOrdenDeUsuarios.UltimaConexion,
        };

    /// <summary>Texto que la pantalla muestra por cada estado (<c>EstadoUsuario</c> de la web). La base guarda un enum
    /// nativo que ordena por declaración, no por este texto.</summary>
    public static readonly IReadOnlyDictionary<EstadoUsuario, string> EtiquetasDeEstado =
        Enum.GetValues<EstadoUsuario>().ToDictionary(e => e, e => e.ToString());

    /// <summary><c>CASE estado WHEN ... THEN etiqueta</c>: el orden de la columna Estado es el alfabético de su texto.</summary>
    public static readonly Expression<Func<Usuario, string>> EtiquetaDeEstado = ArmarEtiquetaDeEstado();

    /// <summary>La cuenta de plataforma no tiene tenant, así que ordenar por esa columna solo lo puede pedir un actor
    /// de plataforma: para el resto la columna ni se muestra.</summary>
    public static OrdenDeListado<ColumnaDeOrdenDeUsuarios>? Interpretar(
        string? orden, string? direccion, bool esDePlataforma) =>
        OrdenDeListado.Interpretar(
            orden, direccion, Claves, columna => columna != ColumnaDeOrdenDeUsuarios.Tenant || esDePlataforma);

    private static Expression<Func<Usuario, string>> ArmarEtiquetaDeEstado()
    {
        var u = Expression.Parameter(typeof(Usuario), "u");
        var estado = Expression.Property(u, nameof(Usuario.Estado));

        return Expression.Lambda<Func<Usuario, string>>(
            EtiquetaPorCaso.De(estado, EtiquetasDeEstado, Expression.Constant(string.Empty)), u);
    }
}
