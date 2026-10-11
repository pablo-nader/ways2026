using System.Data;
using System.Data.Common;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Application.Organizacion;
using Ways.Domain.Common;
using Ways.Domain.Organizacion;

namespace Ways.Application.Dominios;

/// <summary>
/// Alta, listado, verificación y baja de los dominios propios de un tenant. Es plataforma-only
/// (<c>Politicas.SoloPlataforma</c> en la API): el dominio decide a qué tenant llega una request
/// anónima, así que ningún tenant puede reclamarse uno por su cuenta.
///
/// Las tres escrituras corren bajo <see cref="FabricaDeEstrategiaSinReintento"/> (skill
/// <c>ef-retry-safe-writes</c>, forma (b)): el alta agrega una fila y la baja es lógica, y un
/// reintento sobre un commit ambiguo devolvería un 409 <c>host_duplicado</c> (o un 404) sobre una
/// operación que sí tuvo éxito. Todas toman primero el mismo lock de tenant que las bajas de
/// organización, así que un dominio no puede cargarse mientras se da de baja su tenant, y leen la
/// fila que mutan una sola vez, adentro de la transacción y después del lock.
///
/// Cada escritura invalida el host que toca en <see cref="IResolvedorDeDominios"/> DESPUÉS del
/// commit. Una request que ya estaba resolviendo ese host puede volver a guardar el valor viejo; lo
/// que se recuerda vence solo, así que el desfasaje máximo es el de esa vigencia.
/// </summary>
public class ServicioDeDominios(
    IWaysDbContext db,
    IRelojDelSistema reloj,
    IResolvedorDeDominios resolvedor)
{
    private static readonly Expression<Func<Dominio, DominioListado>> Proyeccion = d => new DominioListado(
        d.Id, d.IdTenant, d.Host, d.EsPrincipal, d.VerificadoEn, d.CreatedAt);

    private static readonly Func<Dominio, DominioListado> Mapear = Proyeccion.Compile();

    public async Task<IReadOnlyList<DominioListado>> ListarAsync(int idTenant, CancellationToken ct = default)
    {
        await ExigirTenantAsync(idTenant, ct);

        return await db.Dominios
            .AsNoTracking()
            .Where(d => d.IdTenant == idTenant)
            .OrderByDescending(d => d.EsPrincipal)
            .ThenBy(d => d.Host)
            .Select(Proyeccion)
            .ToListAsync(ct);
    }

    public async Task<DominioListado> AgregarAsync(
        int idTenant, SolicitudDeDominio datos, CancellationToken ct = default)
    {
        await ExigirTenantAsync(idTenant, ct);

        var host = Dominio.NormalizarHost(datos.Host)
            ?? throw new ErrorDominio(
                "host_invalido",
                "El host no es válido: indicá solo el nombre de dominio, sin https:// ni rutas.",
                400);

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        var creado = await EnUnaTransaccionAsync(estrategia, async () =>
        {
            await TomarLockDelTenantAsync(idTenant, ct);
            await ExigirTenantAsync(idTenant, ct);

            // Pre-chequeo de UX: la garantía es ux_dominios_host, que ManejadorDeErrores traduce al
            // mismo 409 si dos altas del mismo host pasan este chequeo a la vez (incluso entre
            // tenants distintos, que este lock no serializa).
            if (await db.Dominios.AnyAsync(d => d.Host == host, ct))
            {
                throw ErrorDominio.Conflicto("host_duplicado", "Ese host ya está cargado en otro dominio.");
            }

            var ahora = reloj.Ahora;

            if (datos.EsPrincipal)
            {
                // El UPDATE va en su propio SaveChanges: en uno solo, EF puede ordenar el INSERT del
                // nuevo principal antes del UPDATE que baja al viejo y chocar con el índice parcial.
                var principales = await db.Dominios
                    .Where(d => d.IdTenant == idTenant && d.EsPrincipal)
                    .ToListAsync(ct);

                foreach (var principal in principales)
                {
                    principal.EsPrincipal = false;
                    principal.UpdatedAt = ahora;
                }

                await db.SaveChangesAsync(ct);
            }

            var dominio = new Dominio
            {
                IdTenant = idTenant,
                Host = host,
                EsPrincipal = datos.EsPrincipal,
                CreatedAt = ahora,
                UpdatedAt = ahora
            };

            db.Dominios.Add(dominio);
            await db.SaveChangesAsync(ct);

            return Mapear(dominio);
        }, ct);

        resolvedor.Invalidar(host);

        return creado;
    }

    /// <summary>Marca el dominio como verificado. Idempotente: verificar uno ya verificado devuelve
    /// el estado actual sin mover <c>verificado_en</c>.</summary>
    public async Task<DominioListado> VerificarAsync(
        int idTenant, int idDominio, CancellationToken ct = default)
    {
        await ExigirDominioDelTenantAsync(idTenant, idDominio, ct);

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        var verificado = await EnUnaTransaccionAsync(estrategia, async () =>
        {
            await TomarLockDelTenantAsync(idTenant, ct);

            var dominio = await BuscarDominioDelTenantAsync(idTenant, idDominio, ct);

            if (dominio.VerificadoEn is null)
            {
                var ahora = reloj.Ahora;
                dominio.VerificadoEn = ahora;
                dominio.UpdatedAt = ahora;
                await db.SaveChangesAsync(ct);
            }

            return Mapear(dominio);
        }, ct);

        resolvedor.Invalidar(verificado.Host);

        return verificado;
    }

    /// <summary>Baja lógica. Si era el principal, la marca queda en la fila dada de baja y el
    /// tenant se queda sin principal hasta que se cargue o se promueva otro.</summary>
    public async Task EliminarAsync(int idTenant, int idDominio, CancellationToken ct = default)
    {
        await ExigirDominioDelTenantAsync(idTenant, idDominio, ct);

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        var host = await EnUnaTransaccionAsync(estrategia, async () =>
        {
            await TomarLockDelTenantAsync(idTenant, ct);

            var dominio = await BuscarDominioDelTenantAsync(idTenant, idDominio, ct);

            var ahora = reloj.Ahora;
            dominio.DeletedAt = ahora;
            dominio.UpdatedAt = ahora;
            await db.SaveChangesAsync(ct);

            return dominio.Host;
        }, ct);

        resolvedor.Invalidar(host);
    }

    // --- Común ---

    private async Task ExigirTenantAsync(int idTenant, CancellationToken ct)
    {
        if (!await db.Tenants.AnyAsync(t => t.Id == idTenant, ct))
        {
            throw ErrorDominio.NoEncontrado("El tenant no existe.");
        }
    }

    /// <summary>404 primero, antes de cualquier otra cosa: el tenant no existe, o el dominio no existe
    /// o es de otro tenant. Es el chequeo barato fuera de la transacción; la lectura que se muta nace
    /// recién adentro, después del lock (<see cref="BuscarDominioDelTenantAsync"/>).</summary>
    private async Task ExigirDominioDelTenantAsync(int idTenant, int idDominio, CancellationToken ct)
    {
        await ExigirTenantAsync(idTenant, ct);

        if (!await db.Dominios.AnyAsync(d => d.Id == idDominio && d.IdTenant == idTenant, ct))
        {
            throw ErrorDominio.NoEncontrado("El dominio no existe.");
        }
    }

    private async Task<Dominio> BuscarDominioDelTenantAsync(int idTenant, int idDominio, CancellationToken ct) =>
        await db.Dominios.FirstOrDefaultAsync(d => d.Id == idDominio && d.IdTenant == idTenant, ct)
            ?? throw ErrorDominio.NoEncontrado("El dominio no existe.");

    private async Task<T> EnUnaTransaccionAsync<T>(
        IExecutionStrategy estrategia, Func<Task<T>> cuerpo, CancellationToken ct) =>
        await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);

            var resultado = await cuerpo();

            await transaccion.CommitAsync(ct);
            return resultado;
        });

    /// <summary>
    /// <c>pg_advisory_xact_lock</c> sobre el tenant dueño con la MISMA clave que las bajas de
    /// organización (<see cref="ServicioDeOrganizacion.ClaveDeLockDeBaja"/>): cargar o bajar un
    /// dominio se serializa contra la baja de su tenant. Es el único lock que toma, así que no
    /// puede formar un ciclo. Mismo idioma de ADO crudo que <c>ServicioDeOrganizacion</c>: la
    /// conexión se abre por <c>Database.OpenConnectionAsync</c> para que el interceptor aplique los
    /// GUC de RLS.
    /// </summary>
    private async Task TomarLockDelTenantAsync(int idTenant, CancellationToken ct)
    {
        var conexion = db.Database.GetDbConnection();

        if (conexion.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        await using var comando = conexion.CreateCommand();
        comando.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        comando.CommandText = "SELECT pg_advisory_xact_lock($1, $2)";

        ParametrosDeComando.Agregar(comando, idTenant);
        ParametrosDeComando.Agregar(comando, ServicioDeOrganizacion.ClaveDeLockDeBaja);

        await comando.ExecuteNonQueryAsync(ct);
    }
}
