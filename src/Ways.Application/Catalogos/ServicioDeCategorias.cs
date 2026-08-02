using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;

namespace Ways.Application.Catalogos;

/// <summary>
/// Escape hatch de ADR-11: <c>categorias</c> es un árbol con una regla de profundidad
/// (ADR-12), así que no puede vivir solo con lo que <see cref="ServicioDeCatalogo{T,TL,TA}"/>
/// ya resuelve — reusa esas 5 operaciones para todo lo común y agrega la validación de
/// profundidad/ciclo antes de crear o mover un nodo.
///
/// La profundidad se computa con una CTE recursiva por escritura (ADR-12: no se guarda un
/// nivel denormalizado). <c>Database.SqlQuery&lt;T&gt;()</c> no se puede usar acá — crashea
/// contra el modelo de este proyecto (ver el comentario de
/// <c>InicializadorDeBaseDeDatos.VerificarRolSinBypassAsync</c>) — así que la CTE corre por
/// ADO.NET crudo. A diferencia de ese método, esta consulta SÍ necesita RLS activo (lee
/// <c>categorias</c>, tabla scopeada) — así que la conexión se abre con
/// <c>Database.OpenConnectionAsync()</c> de EF, no con <c>GetDbConnection().OpenAsync()</c>
/// crudo: abrir la conexión "cruda" directamente salta por completo el pipeline de
/// interceptores de EF, así que <c>InterceptorDeContextoDeTenant</c> nunca corre el
/// <c>set_config</c> del GUC de tenant — la CTE entonces corre sobre una conexión sin
/// <c>app.tenant_id</c> seteado, y RLS le oculta todas las filas sin excepción (fail-closed,
/// ADR-4) en vez de tirar un error: el síntoma es "siempre ve 0 filas", no una excepción, lo
/// que lo hizo bastante más difícil de encontrar que un fallo ruidoso.
/// </summary>
public class ServicioDeCategorias(IWaysDbContext db, IRelojDelSistema reloj, ITenantActual tenantActual)
    : ServicioDeCatalogo<Categoria, CategoriaListado, CategoriaAlta>(db, reloj)
{
    // "nivelDelPadre" en la convención de ReglaDeCategorias.ValidarProfundidad es la
    // profundidad 1-indexada del padre elegido (1 = padre raíz, 2 = padre hijo de una raíz…),
    // no la cantidad de sus ancestros (0-indexada) — por eso acá es count(*) sin "- 1": la
    // cadena ya incluye al propio padre. Confirmado contra los casos ya aprobados de
    // ReglaDeCategoriasTests (batch 1): "hijo de una raíz" espera nivelDelPadre = 1.
    // "deleted_at IS NULL" en cada rama (ancla y recursiva): una baja lógica no participa
    // del árbol para ninguna de las tres CTEs — ni como ancestro/altura/descendiente ni,
    // vía ExistePadreAsync más abajo, como padre elegible para un alta o un PUT.
    private const string SqlNivel =
        """
        WITH RECURSIVE cadena AS (
            SELECT id_categoria, id_categoria_padre FROM categorias
            WHERE id_categoria = $1 AND id_tenant = $2 AND deleted_at IS NULL
            UNION ALL
            SELECT c.id_categoria, c.id_categoria_padre
            FROM categorias c JOIN cadena ON c.id_categoria = cadena.id_categoria_padre
            WHERE c.deleted_at IS NULL
        )
        SELECT count(*) FROM cadena
        """;

    private const string SqlAltura =
        """
        WITH RECURSIVE descendientes AS (
            SELECT id_categoria, 0 AS profundidad FROM categorias
            WHERE id_categoria_padre = $1 AND id_tenant = $2 AND deleted_at IS NULL
            UNION ALL
            SELECT c.id_categoria, d.profundidad + 1
            FROM categorias c JOIN descendientes d ON c.id_categoria_padre = d.id_categoria
            WHERE c.deleted_at IS NULL
        )
        SELECT COALESCE(MAX(profundidad), -1) + 1 FROM descendientes
        """;

    private const string SqlDescendientes =
        """
        WITH RECURSIVE descendientes AS (
            SELECT id_categoria FROM categorias
            WHERE id_categoria_padre = $1 AND id_tenant = $2 AND deleted_at IS NULL
            UNION ALL
            SELECT c.id_categoria
            FROM categorias c JOIN descendientes d ON c.id_categoria_padre = d.id_categoria
            WHERE c.deleted_at IS NULL
        )
        SELECT id_categoria FROM descendientes
        """;

    private const string SqlExistePadre =
        """
        SELECT EXISTS (
            SELECT 1 FROM categorias
            WHERE id_categoria = $1 AND id_tenant = $2 AND deleted_at IS NULL
        )
        """;

    protected override DbSet<Categoria> Conjunto => Db.Categorias;

    protected override CategoriaListado Proyectar(Categoria entidad) => new(
        entidad.Id, entidad.Nombre, entidad.Activo, entidad.IdEmpresa, entidad.Orden, entidad.IdCategoriaPadre);

    protected override Categoria Instanciar(string nombre, int? idEmpresa, bool activo, DateTimeOffset ahora) =>
        new() { Nombre = nombre, IdEmpresa = idEmpresa, Activo = activo, CreatedAt = ahora, UpdatedAt = ahora };

    protected override void AplicarPropios(Categoria entidad, CategoriaAlta datos)
    {
        entidad.Orden = datos.Orden;
        entidad.IdCategoriaPadre = datos.IdCategoriaPadre;
    }

    public override async Task<CategoriaListado> CrearAsync(CategoriaAlta datos, CancellationToken ct = default)
    {
        await ValidarProfundidadAsync(datos.IdCategoriaPadre, idPropio: null, ct);
        return await base.CrearAsync(datos, ct);
    }

    public override async Task<CategoriaListado> ActualizarAsync(
        int id, CategoriaAlta datos, CancellationToken ct = default)
    {
        if (datos.IdCategoriaPadre is not null)
        {
            var descendientes = await DescendientesAsync(id, ct);
            ReglaDeCategorias.ValidarSinCiclo(id, datos.IdCategoriaPadre.Value, descendientes);
        }

        await ValidarProfundidadAsync(datos.IdCategoriaPadre, idPropio: id, ct);
        return await base.ActualizarAsync(id, datos, ct);
    }

    private async Task ValidarProfundidadAsync(int? idCategoriaPadre, int? idPropio, CancellationToken ct)
    {
        if (idCategoriaPadre is not null && !await ExistePadreAsync(idCategoriaPadre.Value, ct))
        {
            // Sin este chequeo, un padre inexistente o dado de baja resolvía "nivel 0" en
            // SqlNivel (la CTE simplemente no encuentra filas) y quedaba indistinguible de
            // "sin padre" — la fila se creaba/movía igual, colgada de un id que no existe
            // para este tenant.
            throw new ErrorDominio(
                "categoria_padre_invalido",
                "La categoría padre no existe o fue eliminada.",
                400);
        }

        var alturaDelSubarbol = idPropio is null ? 0 : await AlturaDelSubarbolAsync(idPropio.Value, ct);
        var nivelDelPadre = idCategoriaPadre is null ? 0 : await NivelDeAsync(idCategoriaPadre.Value, ct);

        ReglaDeCategorias.ValidarProfundidad(nivelDelPadre, alturaDelSubarbol);
    }

    private Task<int> NivelDeAsync(int idCategoria, CancellationToken ct) =>
        EjecutarEscalarAsync(SqlNivel, idCategoria, ct);

    private Task<int> AlturaDelSubarbolAsync(int idCategoria, CancellationToken ct) =>
        EjecutarEscalarAsync(SqlAltura, idCategoria, ct);

    private async Task<bool> ExistePadreAsync(int idCategoria, CancellationToken ct)
    {
        var laAbrimosAca = await AbrirSiHaceFaltaAsync(ct);

        try
        {
            var conexion = Db.Database.GetDbConnection();
            await using var comando = conexion.CreateCommand();
            comando.CommandText = SqlExistePadre;
            AgregarParametro(comando, idCategoria);
            AgregarParametro(comando, tenantActual.Id);

            var resultado = await comando.ExecuteScalarAsync(ct);
            return resultado is bool existe && existe;
        }
        finally
        {
            if (laAbrimosAca)
            {
                await Db.Database.CloseConnectionAsync();
            }
        }
    }

    private async Task<IReadOnlyCollection<int>> DescendientesAsync(int idCategoria, CancellationToken ct)
    {
        var laAbrimosAca = await AbrirSiHaceFaltaAsync(ct);

        try
        {
            var conexion = Db.Database.GetDbConnection();
            await using var comando = conexion.CreateCommand();
            comando.CommandText = SqlDescendientes;
            AgregarParametro(comando, idCategoria);
            AgregarParametro(comando, tenantActual.Id);

            var descendientes = new List<int>();
            await using var lector = await comando.ExecuteReaderAsync(ct);
            while (await lector.ReadAsync(ct))
            {
                descendientes.Add(lector.GetInt32(0));
            }

            return descendientes;
        }
        finally
        {
            if (laAbrimosAca)
            {
                await Db.Database.CloseConnectionAsync();
            }
        }
    }

    private async Task<int> EjecutarEscalarAsync(string sql, int idCategoria, CancellationToken ct)
    {
        var laAbrimosAca = await AbrirSiHaceFaltaAsync(ct);

        try
        {
            var conexion = Db.Database.GetDbConnection();
            await using var comando = conexion.CreateCommand();
            comando.CommandText = sql;
            AgregarParametro(comando, idCategoria);
            AgregarParametro(comando, tenantActual.Id);

            var resultado = await comando.ExecuteScalarAsync(ct);
            return Convert.ToInt32(resultado);
        }
        finally
        {
            if (laAbrimosAca)
            {
                await Db.Database.CloseConnectionAsync();
            }
        }
    }

    /// <summary><c>Database.OpenConnectionAsync()</c>, no <c>GetDbConnection().OpenAsync()</c>
    /// crudo: solo el primero pasa por el pipeline de interceptores de EF (ver el comentario
    /// de la clase). Devuelve si esta llamada fue quien abrió la conexión — así el llamador
    /// solo la cierra si la abrió él, sin interferir con una conexión que ya estaba abierta
    /// por otra operación de EF en curso en el mismo request.</summary>
    private async Task<bool> AbrirSiHaceFaltaAsync(CancellationToken ct)
    {
        if (Db.Database.GetDbConnection().State == System.Data.ConnectionState.Open)
        {
            return false;
        }

        await Db.Database.OpenConnectionAsync(ct);
        return true;
    }

    private static void AgregarParametro(System.Data.IDbCommand comando, object? valor)
    {
        var parametro = comando.CreateParameter();
        parametro.Value = valor ?? DBNull.Value;
        comando.Parameters.Add(parametro);
    }
}
