using System.Data.Common;
using Ways.Application.Abstracciones;
using Ways.Domain.Auditoria;

namespace Ways.Application.Auditoria;

/// <summary>
/// El writer de auditoría (design decisiones 1/2): DOS modos de encolamiento, UN contrato — nunca
/// abre transacción, nunca llama <c>SaveChanges</c>/<c>Commit</c>. <see cref="Registrar"/> es para
/// callers EF (<c>ServicioDePrecios</c>, la mayoría de <c>ServicioDeUsuarios</c>);
/// <see cref="RegistrarAsync"/> es para callers ADO (anulaciones, stock, reliquidación).
///
/// <see cref="Auditoria.Auditoria.IdActor"/>/<see cref="Auditoria.Auditoria.CreadoEl"/> se
/// estampan ACÁ, desde <paramref name="contexto"/>/<paramref name="reloj"/> — nunca como
/// parámetro de <see cref="RegistroDeAuditoria"/>: once call sites no pueden equivocarlos, y la
/// exención documentada del gate §B (<c>id_actor</c> siempre server-derived) queda estructural.
/// </summary>
public sealed class ServicioDeAuditoria(IWaysDbContext db, IRelojDelSistema reloj, IContextoDeUsuario contexto)
{
    /// <summary>MUNDO EF — no hace I/O: encola la fila en el MISMO <c>SaveChangesAsync</c> del
    /// llamador, así que es atómica con él tenga o no una transacción explícita abierta. Devuelve
    /// la entidad solo para que un test pueda inspeccionarla; ningún call site de producción usa
    /// el valor.</summary>
    public Domain.Auditoria.Auditoria Registrar(RegistroDeAuditoria registro)
    {
        var entidad = new Domain.Auditoria.Auditoria
        {
            IdTenant = registro.IdTenant,
            IdPuntoVenta = registro.IdPuntoVenta,
            IdActor = contexto.UsuarioId,
            Accion = registro.Accion.Accion,
            Entidad = registro.Accion.Entidad,
            IdEntidad = registro.IdEntidad,
            ValorAnterior = registro.ValorAnterior is null
                ? null
                : SerializadorDeAuditoria.Serializar(registro.ValorAnterior),
            ValorNuevo = SerializadorDeAuditoria.Serializar(registro.ValorNuevo),
            CreadoEl = reloj.Ahora
        };

        db.Auditoria.Add(entidad);
        return entidad;
    }

    /// <summary>MUNDO ADO — UN <c>INSERT</c> sin <c>RETURNING</c> sobre la conexión Y la
    /// transacción del llamador (convención de
    /// <c>EscriturasDeCuentaCorriente</c>/<c>InsertarMovimientoStockAsync</c>).
    /// <paramref name="transaccion"/> <c>null</c> ⇒ <see cref="InvalidOperationException"/>: una
    /// fila de auditoría jamás se escribe fuera de una transacción — el mutation target del
    /// fail-closed en el mundo ADO (design decisión 10).</summary>
    public async Task RegistrarAsync(
        DbConnection conexion, DbTransaction? transaccion, RegistroDeAuditoria registro, CancellationToken ct)
    {
        if (transaccion is null)
        {
            throw new InvalidOperationException(
                "Una fila de auditoría nunca se escribe fuera de una transacción.");
        }

        var valorAnterior = registro.ValorAnterior is null
            ? null
            : SerializadorDeAuditoria.Serializar(registro.ValorAnterior);
        var valorNuevo = SerializadorDeAuditoria.Serializar(registro.ValorNuevo);

        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "INSERT INTO auditoria " +
            "(id_tenant, id_punto_venta, id_actor, accion, entidad, id_entidad, valor_anterior, valor_nuevo, creado_el) " +
            "VALUES ($1, $2, $3, $4, $5, $6, $7::jsonb, $8::jsonb, $9)";

        AgregarParametro(comando, registro.IdTenant);
        AgregarParametroNulo(comando, registro.IdPuntoVenta);
        AgregarParametro(comando, contexto.UsuarioId);
        AgregarParametro(comando, registro.Accion.Accion);
        AgregarParametro(comando, registro.Accion.Entidad);
        AgregarParametro(comando, registro.IdEntidad);
        AgregarParametroNulo(comando, valorAnterior);
        AgregarParametro(comando, valorNuevo);
        AgregarParametro(comando, reloj.Ahora);

        await comando.ExecuteNonQueryAsync(ct);
    }

    private static void AgregarParametro(DbCommand comando, object valor)
    {
        var parametro = comando.CreateParameter();
        parametro.Value = valor;
        comando.Parameters.Add(parametro);
    }

    private static void AgregarParametroNulo(DbCommand comando, object? valor)
    {
        var parametro = comando.CreateParameter();
        parametro.Value = valor ?? DBNull.Value;
        comando.Parameters.Add(parametro);
    }
}
