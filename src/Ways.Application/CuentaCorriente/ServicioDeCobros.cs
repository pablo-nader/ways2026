using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Application.Auditoria;
using Ways.Domain.Auditoria;
using Ways.Domain.Common;
using Ways.Domain.Ventas;

namespace Ways.Application.CuentaCorriente;

/// <summary>Una aplicación viva de un recibo.</summary>
public sealed record AplicacionDeCobroListada(int Id, int IdComprobanteVenta, decimal Importe, DateTimeOffset Fecha);

/// <summary>Estado de un recibo después de aplicar o desaplicar: lo cobrado, lo aplicado, lo disponible y sus
/// aplicaciones vivas.</summary>
public sealed record EstadoDeCobro(
    int IdRecibo, decimal NetoCobrado, decimal Aplicado, decimal Disponible, IReadOnlyList<AplicacionDeCobroListada> Aplicaciones);

/// <summary>
/// E7-S2: aplicar el cobro de un recibo <c>RC</c> (pago a cuenta) a ventas en cuenta corriente del mismo cliente, y
/// desaplicarlo. Aplicar no mueve <c>clientes.saldo</c> ni escribe movimientos de cuenta corriente: el saldo ya bajó
/// cuando se registró el recibo; solo dice qué ventas cancela.
///
/// Orden de locks: los comprobantes involucrados (el recibo y las ventas) <c>FOR UPDATE</c> en id ascendente, y recién
/// después las filas de <c>aplicaciones_cobro</c>. La anulación (<c>ServicioDeVentas.EjecutarAnulacionAsync</c>) toma la
/// fila de su comprobante con su UPDATE de estado y después libera sus aplicaciones: como acá ninguna aplicación se toca
/// sin tener antes el lock de sus dos comprobantes, la anulación nunca espera una fila de aplicaciones que este servicio
/// tenga tomada sin que este servicio ya haya esperado su comprobante. No toma ni el turno ni la fila del cliente.
///
/// Forma (b) de ef-retry-safe-writes: <see cref="FabricaDeEstrategiaSinReintento"/>. Aplicar y desaplicar escriben
/// aplicaciones y auditoría sin clave de idempotencia: un reintento tras un commit ambiguo aplicaría dos veces, o
/// respondería 404 a una desaplicación que sí tuvo éxito.
/// </summary>
public class ServicioDeCobros(
    IWaysDbContext db, IRelojDelSistema reloj, IContextoDeUsuario contexto, ServicioDeAuditoria auditoria)
{
    /// <summary>
    /// Aplica lo disponible de un recibo a una o más ventas. Una sola aplicación viva por par recibo-venta: aplicar de nuevo
    /// la agranda. update-404-first: el recibo y cada venta inexistentes (o espejos fiscales) responden 404 antes de
    /// validar los importes. Las reglas son las de <see cref="ReglaDeAplicacionDeCobro"/>, evaluadas bajo los locks.
    /// </summary>
    public async Task<EstadoDeCobro> AplicarAsync(
        int idRecibo, IReadOnlyList<AplicacionDeCobroPedida?>? pedidas, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        await ExigirComprobanteExistenteAsync(idRecibo, "el recibo", ct);
        var validas = ReglaDeAplicacionDeCobro.ExigirPedidas(pedidas);
        foreach (var idVenta in validas.Select(p => p.IdComprobanteVenta).Order())
        {
            await ExigirComprobanteExistenteAsync(idVenta, "la venta", ct);
        }

        ReglaDeAplicacionDeCobro.ExigirImportesValidos(validas);

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        await estrategia.ExecuteAsync(async () =>
            await EjecutarAplicarAsync(idTenant, idRecibo, validas, idEmpleado, momento, ct));

        return await ObtenerEstadoAsync(idRecibo, ct);
    }

    private async Task<int> EjecutarAplicarAsync(
        int idTenant, int idRecibo, IReadOnlyList<AplicacionDeCobroPedida> pedidas, int idEmpleado, DateTimeOffset momento,
        CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        // Paso 1: el recibo y las ventas, FOR UPDATE en id ascendente. Es la ÚNICA lectura de su tipo, estado y cliente.
        var bloqueados = await BloquearComprobantesAsync(
            pedidas.Select(p => p.IdComprobanteVenta).Append(idRecibo), idTenant, ct);
        var recibo = bloqueados[idRecibo];
        ReglaDeAplicacionDeCobro.ExigirRecibo(recibo.Datos);
        foreach (var pedida in pedidas)
        {
            ReglaDeAplicacionDeCobro.ExigirVenta(recibo.Datos, bloqueados[pedida.IdComprobanteVenta].Datos);
        }

        // Paso 2: con los dos lados bloqueados, nadie más puede cambiar lo aplicado hasta el commit. Única lectura de las
        // aplicaciones del recibo, que son las que se mutan.
        var aplicaciones = await db.AplicacionesCobro.Where(a => a.IdComprobanteCobro == idRecibo).ToListAsync(ct);
        var disponible = (await LectorDeAplicacionesDeCobro.LeerDisponiblePorReciboAsync(db, [idRecibo], ct))[idRecibo];
        var saldos = await LectorDeAplicacionesDeCobro.LeerSaldoPorVentaAsync(
            db, pedidas.Select(p => p.IdComprobanteVenta).ToList(), ct);
        ReglaDeAplicacionDeCobro.ExigirTopes(
            disponible.Disponible, pedidas.Select(p => (p, saldos[p.IdComprobanteVenta].SaldoPendiente)));

        var anteriores = aplicaciones.Select(a => (a.IdComprobanteVenta, a.Importe)).ToList();

        foreach (var pedida in pedidas)
        {
            var existente = aplicaciones.FirstOrDefault(a => a.IdComprobanteVenta == pedida.IdComprobanteVenta);
            if (existente is not null)
            {
                existente.Importe += pedida.Importe;
                existente.UpdatedAt = momento;
                continue;
            }

            var nueva = new AplicacionCobro
            {
                IdTenant = idTenant,
                IdComprobanteCobro = idRecibo,
                IdComprobanteVenta = pedida.IdComprobanteVenta,
                Importe = pedida.Importe,
                IdEmpleado = idEmpleado,
                Fecha = momento,
                CreatedAt = momento,
                UpdatedAt = momento
            };
            db.AplicacionesCobro.Add(nueva);
            aplicaciones.Add(nueva);
        }

        var (valorAnterior, valorNuevo) = PayloadDeAuditoria.AplicacionDeCobro(
            disponible.NetoCobrado, anteriores, aplicaciones.Select(a => (a.IdComprobanteVenta, a.Importe)).ToList());
        auditoria.Registrar(new RegistroDeAuditoria(
            idTenant, recibo.IdPuntoVenta, AccionAuditada.CobroAplicacion, idRecibo, valorAnterior, valorNuevo));

        await db.SaveChangesAsync(ct);
        await transaccion.CommitAsync(ct);

        return idRecibo;
    }

    /// <summary>
    /// Libera una aplicación (baja lógica): lo aplicado vuelve al disponible del recibo y al saldo de la venta. No se
    /// reaplica nada solo. update-404-first: el recibo y la aplicación (que tiene que ser de ese recibo y estar viva)
    /// responden 404 antes de cualquier otra cosa. Los dos comprobantes se bloquean en id ascendente sin exigir su estado:
    /// un comprobante anulado ya liberó sus aplicaciones, así que la lectura bajo el lock responde 404.
    /// </summary>
    public async Task<EstadoDeCobro> DesaplicarAsync(int idRecibo, int idAplicacion, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var momento = reloj.Ahora;

        await ExigirComprobanteExistenteAsync(idRecibo, "el recibo", ct);
        var idVenta = await db.AplicacionesCobro
            .Where(a => a.Id == idAplicacion && a.IdComprobanteCobro == idRecibo)
            .Select(a => (int?)a.IdComprobanteVenta)
            .FirstOrDefaultAsync(ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe la aplicación {idAplicacion} del recibo {idRecibo}.");

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        await estrategia.ExecuteAsync(async () =>
            await EjecutarDesaplicarAsync(idTenant, idRecibo, idAplicacion, idVenta, momento, ct));

        return await ObtenerEstadoAsync(idRecibo, ct);
    }

    private async Task<int> EjecutarDesaplicarAsync(
        int idTenant, int idRecibo, int idAplicacion, int idVenta, DateTimeOffset momento, CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        var bloqueados = await BloquearComprobantesAsync([idRecibo, idVenta], idTenant, ct);

        // Única lectura de la fila, bajo los dos locks: si una anulación o otra desaplicación la liberó entre el chequeo y
        // el lock, es 404.
        var aplicacion = await db.AplicacionesCobro
            .FirstOrDefaultAsync(a => a.Id == idAplicacion && a.IdComprobanteCobro == idRecibo, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe la aplicación {idAplicacion} del recibo {idRecibo}.");

        aplicacion.DeletedAt = momento;
        aplicacion.UpdatedAt = momento;

        var (valorAnterior, valorNuevo) = PayloadDeAuditoria.DesaplicacionDeCobro(
            aplicacion.Id, aplicacion.IdComprobanteVenta, aplicacion.Importe, idComprobanteAnulado: null);
        auditoria.Registrar(new RegistroDeAuditoria(
            idTenant, bloqueados[idRecibo].IdPuntoVenta, AccionAuditada.CobroDesaplicacion, idRecibo, valorAnterior,
            valorNuevo));

        await db.SaveChangesAsync(ct);
        await transaccion.CommitAsync(ct);

        return idRecibo;
    }

    /// <summary>Lo cobrado, lo aplicado, lo disponible y las aplicaciones vivas de un recibo, en id ascendente.</summary>
    public async Task<EstadoDeCobro> ObtenerEstadoAsync(int idRecibo, CancellationToken ct = default)
    {
        var disponible = (await LectorDeAplicacionesDeCobro.LeerDisponiblePorReciboAsync(db, [idRecibo], ct))
            .GetValueOrDefault(idRecibo)
            ?? throw ErrorDominio.NoEncontrado($"No existe el recibo {idRecibo}.");

        var aplicaciones = await db.AplicacionesCobro
            .Where(a => a.IdComprobanteCobro == idRecibo)
            .OrderBy(a => a.Id)
            .Select(a => new AplicacionDeCobroListada(a.Id, a.IdComprobanteVenta, a.Importe, a.Fecha))
            .ToListAsync(ct);

        return new EstadoDeCobro(idRecibo, disponible.NetoCobrado, disponible.Aplicado, disponible.Disponible, aplicaciones);
    }

    private async Task ExigirComprobanteExistenteAsync(int id, string queEs, CancellationToken ct)
    {
        if (!await db.ComprobantesVenta.SinEspejosFiscales().AnyAsync(c => c.Id == id, ct))
        {
            // ADR-8: mismo 404 para "no existe" y "es de otro tenant".
            throw ErrorDominio.NoEncontrado($"No existe {queEs} {id}.");
        }
    }

    private sealed record ComprobanteBloqueado(
        int Id, string CodigoTipo, short Signo, EstadoComprobante Estado, int IdCliente, int IdPuntoVenta)
    {
        public ComprobanteParaAplicar Datos => new(Id, CodigoTipo, Signo, Estado, IdCliente);
    }

    /// <summary>
    /// <c>FOR UPDATE OF c</c> de cada comprobante, en id ascendente y sin repetir, con su tipo, estado, cliente y punto de
    /// venta leídos en la misma sentencia que el lock. Un espejo fiscal no es ni recibo ni venta: no matchea y es 404.
    /// </summary>
    private async Task<Dictionary<int, ComprobanteBloqueado>> BloquearComprobantesAsync(
        IEnumerable<int> ids, int idTenant, CancellationToken ct)
    {
        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        var bloqueados = new Dictionary<int, ComprobanteBloqueado>();
        foreach (var id in ids.Distinct().Order())
        {
            await using var comando = conexion.CreateCommand();
            comando.Transaction = transaccionCruda;
            comando.CommandText =
                "SELECT t.codigo, t.signo, c.estado::text, c.id_cliente, c.id_punto_venta " +
                "FROM comprobantes_venta c " +
                "JOIN tipos_comprobante t ON t.id_tipo_comprobante = c.id_tipo_comprobante " +
                "WHERE c.id_comprobante_venta = $1 AND c.id_tenant = $2 AND c.id_venta_origen IS NULL " +
                "FOR UPDATE OF c";
            ParametrosDeComando.Agregar(comando, id);
            ParametrosDeComando.Agregar(comando, idTenant);

            await using var lector = await comando.ExecuteReaderAsync(ct);
            if (!await lector.ReadAsync(ct))
            {
                throw ErrorDominio.NoEncontrado($"No existe el comprobante {id}.");
            }

            var estado = lector.GetString(2) == "anulado" ? EstadoComprobante.Anulado : EstadoComprobante.Emitido;
            bloqueados[id] = new ComprobanteBloqueado(
                id, lector.GetString(0), lector.GetInt16(1), estado, lector.GetInt32(3), lector.GetInt32(4));
        }

        return bloqueados;
    }

    private async Task<DbConnection> ObtenerConexionAbiertaAsync(CancellationToken ct)
    {
        var conexion = db.Database.GetDbConnection();
        if (conexion.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        return conexion;
    }

    private int ExigirTenantDeLaSesion() =>
        contexto.IdTenant
            ?? throw new InvalidOperationException("ServicioDeCobros requiere un actor de tenant.");
}
