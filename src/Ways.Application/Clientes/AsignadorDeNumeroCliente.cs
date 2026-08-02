using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;

namespace Ways.Application.Clientes;

/// <summary>
/// Asigna <c>clientes.numero</c> de forma atómica por tenant (design decisions 2 y 3):
/// <c>UPDATE numeraciones_clientes SET proximo_numero = proximo_numero + 1 ... RETURNING</c>
/// vía ADO.NET crudo sobre la conexión/transacción activa de <paramref name="db"/>, nunca
/// <c>Database.SqlQuery&lt;T&gt;()</c>/<c>FromSqlRaw&lt;T&gt;()</c> — confirmado en stage-1
/// slice 2 que esos dos revientan con <c>IndexOutOfRangeException</c> contra este modelo
/// (<see cref="Ways.Infrastructure.Persistencia.InicializadorDeBaseDeDatos"/>'s
/// <c>VerificarRolSinBypassAsync</c> documenta el mismo hallazgo y usa el mismo workaround).
///
/// Sin estado propio ni <see cref="IWaysDbContext"/> por constructor: cada método recibe el
/// contexto por parámetro para poder reusarse contra los tres scopes de DI distintos que lo
/// llaman con su propia transacción — <c>ServicioDeClientes.CrearAsync</c> (slice 2, sesión
/// de tenant), <c>ServicioDeAprovisionamiento.CrearTenantAsync</c> (bootstrap del Consumidor
/// Final) e <c>InicializadorDeBaseDeDatos</c>'s backfill (contexto de plataforma).
///
/// Abre la conexión con <c>Database.OpenConnectionAsync()</c> cuando hace falta, nunca con
/// <c>conexion.OpenAsync()</c> directo sobre el <see cref="DbConnection"/> crudo: ese segundo
/// camino no dispara <c>InterceptorDeContextoDeTenant</c> (que corre atado al ciclo de vida
/// de conexión de EF Core, no al del ADO.NET subyacente) y la conexión quedaría sin los GUC
/// de tenant que RLS necesita para autorizar la escritura. En el camino normal (dentro de la
/// transacción de <c>BeginTransactionAsync</c> de cada llamador) la conexión ya está abierta
/// por EF desde antes, así que esto es un no-op — solo importa cuando algún llamador futuro
/// invoque estos métodos fuera de una transacción ya abierta.
///
/// Estática a propósito: sin campos, sin ciclo de vida de DI que administrar — cada método
/// recibe el <see cref="IWaysDbContext"/> del llamador de turno.
/// </summary>
public static class AsignadorDeNumeroCliente
{
    public static async Task AsegurarContadorAsync(IWaysDbContext db, int idTenant, CancellationToken ct = default)
    {
        var conexion = await ObtenerConexionAbiertaAsync(db, ct);

        await using var comando = conexion.CreateCommand();
        comando.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        comando.CommandText =
            "INSERT INTO numeraciones_clientes (id_tenant, proximo_numero) VALUES ($1, 1) " +
            "ON CONFLICT (id_tenant) DO NOTHING";

        AgregarParametro(comando, idTenant);

        await comando.ExecuteNonQueryAsync(ct);
    }

    public static async Task<int> AsignarSiguienteAsync(IWaysDbContext db, int idTenant, CancellationToken ct = default)
    {
        var conexion = await ObtenerConexionAbiertaAsync(db, ct);

        await using var comando = conexion.CreateCommand();
        comando.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        comando.CommandText =
            "UPDATE numeraciones_clientes SET proximo_numero = proximo_numero + 1 " +
            "WHERE id_tenant = $1 RETURNING proximo_numero - 1";

        AgregarParametro(comando, idTenant);

        var resultado = await comando.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException(
                $"No existe contador de numeraciones para el tenant {idTenant}: " +
                $"llamá a {nameof(AsegurarContadorAsync)} antes de asignar.");

        return Convert.ToInt32(resultado);
    }

    private static async Task<DbConnection> ObtenerConexionAbiertaAsync(IWaysDbContext db, CancellationToken ct)
    {
        var conexion = db.Database.GetDbConnection();

        if (conexion.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        return conexion;
    }

    private static void AgregarParametro(DbCommand comando, int valor)
    {
        var parametro = comando.CreateParameter();
        parametro.Value = valor;
        comando.Parameters.Add(parametro);
    }
}
