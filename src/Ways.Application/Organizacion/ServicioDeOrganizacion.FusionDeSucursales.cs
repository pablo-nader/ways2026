using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Application.Auditoria;
using Ways.Application.Stock;
using Ways.Domain.Auditoria;
using Ways.Domain.Common;
using Ways.Domain.Usuarios;

namespace Ways.Application.Organizacion;

public partial class ServicioDeOrganizacion
{
    /// <summary>
    /// Fusiona la sucursal <paramref name="id"/> en otra viva de su misma empresa (<c>POST /api/sucursales/{id}/fusionar</c>),
    /// para el caso de dos cajas de un mismo local que quedaron con el stock partido en dos sucursales. En una sola
    /// transacción: las cajas vivas del origen pasan al destino, todo su stock (agregados, lotes y saldos negativos) pasa
    /// con un par de asientos de transferencia por saldo (<see cref="EscritorDeStock.FusionarSucursalesAsync"/>, que
    /// también lleva los umbrales) y el origen queda dado de baja con un rastro <c>sucursal.fusion</c>.
    ///
    /// 404 primero (<c>update-404-first</c>) para un origen inexistente, dado de baja o de otro tenant; después el cuerpo:
    /// sin destino es <c>400 sucursal_destino_requerida</c> y el mismo id es <c>400 fusion_misma_sucursal</c>, igual que
    /// una transferencia con el mismo origen y destino. Bajo lock, un destino inexistente es 404 y uno de otra empresa
    /// <c>409 sucursal_de_otra_empresa</c>.
    ///
    /// Orden de locks: el advisory del tenant (serializa contra el ABM, el movimiento y la baja de cajas, y contra otra
    /// fusión), después <c>FOR UPDATE</c> sobre las filas de <c>puntos_venta</c> de las dos sucursales en orden de id,
    /// después <c>FOR UPDATE</c> sobre las dos filas de <c>sucursales</c> en orden de id, y recién después el stock de
    /// las dos sucursales en el orden de <see cref="EscritorDeStock"/>. Un asiento nuevo toma su caja con
    /// <c>FOR SHARE</c> antes que cualquier fila de stock: o termina antes de que la fusión tome su caja o espera, sin
    /// haber tomado stock, y cuando sigue su caja ya resuelve la sucursal destino. Una reversa toma su caja y después
    /// su sucursal guardada con <c>FOR SHARE</c> (<see cref="EscritorDeStock.ResolverSucursalesDeReversaAsync"/>),
    /// también antes del stock: o termina antes de que la fusión tome la sucursal o espera y, cuando sigue, lee
    /// <c>id_sucursal_fusion</c> y asienta en el destino.
    /// </summary>
    public async Task<SucursalListado> FusionarSucursalAsync(int id, SucursalFusion datos, CancellationToken ct = default)
    {
        var idTenant = await IdTenantDeLaSucursalAsync(id, ct);

        var idDestino = datos.IdSucursalDestino
            ?? throw new ErrorDominio("sucursal_destino_requerida", "Elegí la sucursal que absorbe a esta.", 400);

        if (idDestino == id)
        {
            throw new ErrorDominio(
                "fusion_misma_sucursal", "La sucursal de origen y la de destino de una fusión tienen que ser distintas.", 400);
        }

        return await EnUnaTransaccionDeBajaAsync(async () =>
        {
            await TomarLockDeBajaAsync(idTenant, ct);
            await BloquearCajasDeSucursalesAsync(id, idDestino, ct);
            await BloquearSucursalesAsync(id, idDestino, ct);

            var origen = await BuscarSucursalAsync(id, ct);
            var destino = await BuscarSucursalDeLaEmpresaAsync(idDestino, origen.IdEmpresa, ct);

            var cajasDelOrigen = await db.PuntosVenta
                .Where(p => p.IdSucursal == origen.Id)
                .OrderBy(p => p.Id)
                .ToListAsync(ct);
            var cajaDelDestino = await db.PuntosVenta
                .Where(p => p.IdSucursal == destino.Id)
                .OrderBy(p => p.Id)
                .Select(p => (int?)p.Id)
                .FirstOrDefaultAsync(ct);

            var momento = reloj.Ahora;
            var conexion = await ObtenerConexionAbiertaAsync(ct);
            var transaccion = db.Database.CurrentTransaction?.GetDbTransaction();

            // Las cajas que firman el par de asientos: la de menor id de cada lado, o la del otro lado si uno no tiene.
            var cajaDeOrigen = cajasDelOrigen.Select(p => (int?)p.Id).FirstOrDefault() ?? cajaDelDestino;
            if (cajaDeOrigen is null)
            {
                await ExigirSinSaldoAsync(origen.Id, ct);
            }

            // Sin cajas en ninguna de las dos sucursales el origen no tiene saldo, así que no se escribe ningún asiento
            // y la caja no se usa: solo viajan los umbrales.
            var movido = await EscritorDeStock.FusionarSucursalesAsync(
                conexion, transaccion,
                new SolicitudDeFusionDeStock(
                    origen.IdTenant, origen.Id, destino.Id, cajaDeOrigen ?? 0, cajaDelDestino ?? cajaDeOrigen ?? 0,
                    contexto.UsuarioId, momento, $"Fusión de la sucursal {origen.Nombre} en {destino.Nombre}"),
                ct);

            foreach (var caja in cajasDelOrigen)
            {
                caja.IdSucursal = destino.Id;
                caja.UpdatedAt = momento;
            }

            origen.IdSucursalFusion = destino.Id;
            Marcar(origen, momento);

            var (valorAnterior, valorNuevo) = PayloadDeAuditoria.FusionDeSucursales(
                momento, destino.Id, cajasDelOrigen.Select(p => p.Id).ToList(), movido.Articulos, movido.Lotes,
                movido.Movimientos);
            auditoria.Registrar(new RegistroDeAuditoria(
                origen.IdTenant, idPuntoVenta: null, AccionAuditada.SucursalFusion, origen.Id, valorAnterior, valorNuevo));

            await db.SaveChangesAsync(ct);

            return await LeerSucursalAsync(destino.Id, ct);
        }, ct);
    }

    /// <summary>Ninguna de las dos sucursales tiene cajas vivas: no hay caja que firme los asientos, así que solo se
    /// fusiona un origen sin saldo que mover (<c>409 sucursal_sin_puntos_venta</c> si no).</summary>
    private async Task ExigirSinSaldoAsync(int idSucursalOrigen, CancellationToken ct)
    {
        if (await db.Stock.AnyAsync(s => s.IdSucursal == idSucursalOrigen && s.Cantidad != 0m, ct)
            || await db.StockLotes.AnyAsync(s => s.IdSucursal == idSucursalOrigen && s.Cantidad != 0m, ct))
        {
            throw ErrorDominio.Conflicto(
                "sucursal_sin_puntos_venta",
                "Ninguna de las dos sucursales tiene puntos de venta: creá uno en el destino antes de fusionar.");
        }
    }

    private async Task BloquearCajasDeSucursalesAsync(int idSucursalA, int idSucursalB, CancellationToken ct)
    {
        var conexion = await ObtenerConexionAbiertaAsync(ct);

        await using var comando = conexion.CreateCommand();
        comando.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        comando.CommandText =
            "SELECT id_punto_venta FROM puntos_venta WHERE id_sucursal IN ($1, $2) ORDER BY id_punto_venta FOR UPDATE";
        ParametrosDeComando.Agregar(comando, idSucursalA);
        ParametrosDeComando.Agregar(comando, idSucursalB);

        await comando.ExecuteNonQueryAsync(ct);
    }

    private async Task BloquearSucursalesAsync(int idSucursalA, int idSucursalB, CancellationToken ct)
    {
        var conexion = await ObtenerConexionAbiertaAsync(ct);

        await using var comando = conexion.CreateCommand();
        comando.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        comando.CommandText =
            "SELECT id_sucursal FROM sucursales WHERE id_sucursal IN ($1, $2) ORDER BY id_sucursal FOR UPDATE";
        ParametrosDeComando.Agregar(comando, idSucursalA);
        ParametrosDeComando.Agregar(comando, idSucursalB);

        await comando.ExecuteNonQueryAsync(ct);
    }
}
