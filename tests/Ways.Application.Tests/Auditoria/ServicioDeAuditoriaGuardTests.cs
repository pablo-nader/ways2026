using Ways.Application.Auditoria;
using Ways.Domain.Auditoria;

namespace Ways.Application.Tests.Auditoria;

/// <summary>
/// stage-14-auditoria-trazabilidad, Slice 1 (tasks 1.20-1.21, design decisión 1): el guard
/// <c>transaccion is null ⇒ throw</c> de <see cref="ServicioDeAuditoria.RegistrarAsync"/> — una
/// fila de auditoría jamás se escribe fuera de una transacción, la mitad ADO del fail-closed. No
/// necesita Postgres real: el guard lanza ANTES de tocar <c>conexion</c>, así que un
/// <c>null!</c> alcanza para probarlo sin abrir ninguna conexión.
/// </summary>
public class ServicioDeAuditoriaGuardTests
{
    [Fact]
    public async Task RegistrarAsyncSinTransaccionLanza()
    {
        var servicio = new ServicioDeAuditoria(db: null!, reloj: null!, contexto: null!);
        var registro = new RegistroDeAuditoria(
            idTenant: 1, idPuntoVenta: null, AccionAuditada.PrecioCambio, idEntidad: 1,
            valorAnterior: null, valorNuevo: new Dictionary<string, object?> { ["monto"] = 100m });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            servicio.RegistrarAsync(conexion: null!, transaccion: null, registro, CancellationToken.None));
    }
}
