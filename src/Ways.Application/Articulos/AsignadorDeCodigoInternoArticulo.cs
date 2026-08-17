using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;

namespace Ways.Application.Articulos;

/// <summary>
/// Asigna <c>articulos.codigo_interno</c> de forma atómica por tenant (design decision 6,
/// mismo shape que <see cref="Clientes.AsignadorDeNumeroCliente"/>): <c>UPDATE
/// numeraciones_articulos SET proximo_numero = proximo_numero + 1 ... RETURNING</c> vía ADO.NET
/// crudo sobre la conexión/transacción activa de <paramref name="db"/>, nunca
/// <c>Database.SqlQuery&lt;T&gt;()</c>/<c>FromSqlRaw&lt;T&gt;()</c> (mismo hallazgo de
/// stage-1-slice-2 que documenta <see cref="Clientes.AsignadorDeNumeroCliente"/>).
///
/// Orchestrator decision 1 (tasks.md, resuelta antes de esta slice): el correlativo se genera
/// como <c>int</c> y el llamador (<c>ServicioDeArticulos</c>, Slice 2) lo convierte a
/// <c>string</c> sin padding antes de persistirlo en la columna <c>citext</c> — la UI puede
/// hacer zero-padding solo para mostrarlo. <b>Restricción heredada por la etapa 5 (documentada,
/// no impuesta acá):</b> el valor tiene que quedarse por debajo de 7 dígitos para que la futura
/// resolución de escaneo del POS (etapa 5) pueda distinguir un código interno corto de un EAN
/// de 13 dígitos solo por longitud. Ningún tenant realista llega al millón de artículos, así
/// que esta etapa no codifica un tope — queda documentado acá para quien construya la etapa 5.
///
/// Estática a propósito: sin estado propio, cada método recibe el <see cref="IWaysDbContext"/>
/// del llamador de turno (mismo criterio que <see cref="Clientes.AsignadorDeNumeroCliente"/>).
/// </summary>
public static class AsignadorDeCodigoInternoArticulo
{
    public static async Task AsegurarContadorAsync(IWaysDbContext db, int idTenant, CancellationToken ct = default)
    {
        var conexion = await ObtenerConexionAbiertaAsync(db, ct);

        await using var comando = conexion.CreateCommand();
        comando.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        comando.CommandText =
            "INSERT INTO numeraciones_articulos (id_tenant, proximo_numero) VALUES ($1, 1) " +
            "ON CONFLICT (id_tenant) DO NOTHING";

        ParametrosDeComando.Agregar(comando, idTenant);

        await comando.ExecuteNonQueryAsync(ct);
    }

    public static async Task<int> AsignarSiguienteAsync(IWaysDbContext db, int idTenant, CancellationToken ct = default)
    {
        var conexion = await ObtenerConexionAbiertaAsync(db, ct);

        await using var comando = conexion.CreateCommand();
        comando.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        comando.CommandText =
            "UPDATE numeraciones_articulos SET proximo_numero = proximo_numero + 1 " +
            "WHERE id_tenant = $1 RETURNING proximo_numero - 1";

        ParametrosDeComando.Agregar(comando, idTenant);

        var resultado = await comando.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException(
                $"No existe contador de codigo_interno para el tenant {idTenant}: " +
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
}
