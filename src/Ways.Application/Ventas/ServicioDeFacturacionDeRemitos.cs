using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.CuentaCorriente;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Common;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Organizacion;
using Ways.Domain.Ventas;

namespace Ways.Application.Ventas;

/// <summary>
/// stage-17-presupuestos-y-remitos, Slice 6 (design: Transactions — "FACTURAR REMITOS
/// (consolidación)"; tasks 6.2-6.9). Consolida N remitos <c>emitido</c> del MISMO cliente/PV/tenant
/// en UN comprobante <c>TXR</c> itemless — precedente <c>RC</c> (<see cref="CuentaCorriente.ServicioDeCuentaCorriente.RegistrarPagoAsync"/>):
/// dedicado y lean, no una extensión de <see cref="ServicioDeVentas"/> (misma razón que RC — sin
/// líneas, sin ofertas, precios YA congelados en <c>items_remito</c> desde el cuarto write site).
/// Reusa, tal cual, las mismas cuatro piezas que RC: <see cref="AsignadorDeNumeroComprobante"/>
/// (serie propia <c>'TXR'</c>), <see cref="ServicioDeTurnos.ExigirTurnoAbiertoBajoLockAsync"/> (a
/// diferencia del cuarto write site — decisión 13 del proposal, un remito mueve mercadería, la
/// consolidación mueve dinero), <see cref="EscriturasDeCuentaCorriente"/> (sin cambios) y
/// <see cref="ValidadorDePagos"/> (mismo backstop de límite de crédito re-implementado DENTRO de la
/// transacción que <c>ServicioDeVentas.EjecutarTransaccionAsync</c>, OD9/T9). La pieza NUEVA de este
/// archivo es <see cref="EscriturasDeRemito"/> — el lock ascendente + el guard atómico de N filas
/// que decide si el set sigue facturable (ver su propio doc-comment).
/// </summary>
public class ServicioDeFacturacionDeRemitos(
    IWaysDbContext db, IRelojDelSistema reloj, IContextoDeUsuario contexto, ServicioDeTurnos servicioDeTurnos)
{
    public async Task<ComprobanteEmitido> FacturarAsync(
        SolicitudDeFacturacionDeRemitos solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        var idsRemitoDistintos = (solicitud.IdsRemito ?? []).Distinct().ToList();
        if (idsRemitoDistintos.Count == 0)
        {
            throw new ErrorDominio(
                "remitos_no_seleccionados", "Tenés que seleccionar al menos un remito para facturar.", 400);
        }

        var puntoVenta = await ResolverPuntoVentaAsync(solicitud.IdPuntoVenta, ct);

        // fuera: remitos + items · mismo tenant/cliente/PV · todos 'emitido' y sin ligar (design:
        // Transactions — "FACTURAR REMITOS", tasks 6.16, mutation target 51). SIN lock — este guard
        // puede quedar obsoleto ante una carrera real; EscriturasDeRemito.LigarAsync (dentro de la
        // transacción, bajo el lock ascendente) es la autoridad race-safe, ver su doc-comment.
        var remitos = await db.Remitos.AsNoTracking()
            .Where(r => idsRemitoDistintos.Contains(r.Id))
            .ToListAsync(ct);

        if (remitos.Count != idsRemitoDistintos.Count)
        {
            var idsEncontrados = remitos.Select(r => r.Id).ToHashSet();
            var idFaltante = idsRemitoDistintos.First(id => !idsEncontrados.Contains(id));
            throw ErrorDominio.NoEncontrado($"No existe el remito {idFaltante}.");
        }

        var idCliente = remitos[0].IdCliente;
        var todosFacturables = remitos.All(r =>
            r.IdPuntoVenta == puntoVenta.Id && r.IdCliente == idCliente
            && r.Estado == EstadoRemito.Emitido && r.IdComprobanteVenta is null);

        if (!todosFacturables)
        {
            throw new ErrorDominio(
                "remito_no_facturable",
                "Los remitos seleccionados tienen que compartir cliente y punto de venta, y estar emitidos sin facturar.",
                409);
        }

        var items = await db.ItemsRemito.AsNoTracking()
            .Where(i => idsRemitoDistintos.Contains(i.IdRemito))
            .ToListAsync(ct);

        // totales := Σ headers, aserción contra Σ items congelados (design: Transactions —
        // "FACTURAR REMITOS"). Defensa en profundidad, nunca un caso de negocio alcanzable bajo
        // operación normal: cada header ya fue computado por CalculadorDeTotales al crear/editar/
        // emitir su propio remito — un desacuerdo acá solo puede significar una escritura cruda
        // que desincronizó remitos.total de items_remito (mismo criterio que el
        // presupuesto_inconsistente de Slice 3, pero sin domain code propio — esta clase nunca
        // recibió un test dedicado que lo exija, mismo criterio de tasks.md).
        var lineasParaCalcular = items
            .Select(i => new LineaParaCalcular(i.Cantidad, i.PrecioUnitario, i.Cantidad == 0m ? 0m : i.Descuento / i.Cantidad))
            .ToList();
        var totalesRecomputados = CalculadorDeTotales.Calcular(lineasParaCalcular);

        var subtotal = remitos.Sum(r => r.Subtotal);
        var descuentoTotal = remitos.Sum(r => r.DescuentoTotal);
        var total = remitos.Sum(r => r.Total);

        if (totalesRecomputados.Subtotal != subtotal
            || totalesRecomputados.DescuentoTotal != descuentoTotal
            || totalesRecomputados.Total != total)
        {
            throw new InvalidOperationException(
                $"Los totales recomputados de los items de los remitos [{string.Join(",", idsRemitoDistintos)}] " +
                "no coinciden con la suma de sus headers — invariante de escritura violado.");
        }

        var cliente = await db.Clientes.AsNoTracking().FirstAsync(c => c.Id == idCliente, ct);

        var pagos = solicitud.Pagos ?? [];
        var idsMedioPago = pagos.Select(p => p.IdMedioPago).Distinct().ToList();
        var medioPorId = await db.MediosPago
            .Where(m => idsMedioPago.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, ct);

        var idsMedioFaltantes = idsMedioPago.Except(medioPorId.Keys).ToList();
        if (idsMedioFaltantes.Count > 0)
        {
            throw new ErrorDominio("referencia_invalida", $"No existe el medio de pago {idsMedioFaltantes[0]}.", 400);
        }

        var pagosAValidar = pagos
            .Select(p =>
            {
                var medio = medioPorId[p.IdMedioPago];
                return new PagoAValidar(
                    p.IdMedioPago, medio.Comportamiento, medio.AdmiteVuelto, medio.RequiereReferencia,
                    p.Importe, p.Vuelto, p.Referencia);
            })
            .ToList();

        var (toleranciaPago, vueltoMaximo) = await ResolverParametrosDeFacturacionAsync(puntoVenta.IdEmpresa, puntoVenta.Id, ct);

        ValidadorDePagos.Validar(
            total, pagosAValidar, toleranciaPago, vueltoMaximo,
            cliente.EsConsumidorFinal, cliente.Saldo, cliente.LimiteCredito, cliente.CreditoIlimitado);

        // Turno SIEMPRE resuelto server-side (decisión 13 del proposal: la consolidación mueve
        // dinero — a diferencia del cuarto write site, que no exige turno). Pre-chequeo rápido,
        // FUERA de la transacción de escritura; el re-chequeo bajo FOR SHARE (mutation target 54)
        // es el PRIMER statement de EjecutarFacturacionAsync.
        var turno = await servicioDeTurnos.ResolverTurnoAbiertoAsync(puntoVenta.Id, ct);

        var tipo = await ResolverTipoTxrAsync(ct);

        // Misma corrección que ServicioDeVentas/ServicioDeCuentaCorriente: el número se reserva y
        // COMITEA en su propia transacción, ANTES de la que escribe el resto.
        var estrategiaNumeracion = db.Database.CreateExecutionStrategy();
        var numero = await estrategiaNumeracion.ExecuteAsync(async () =>
            await AsignadorDeNumeroComprobante.AsignarComprometidoAsync(db, idTenant, puntoVenta.Id, tipo.Codigo, ct));

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        return await estrategia.ExecuteAsync(async () =>
            await EjecutarFacturacionAsync(
                idTenant, idEmpleado, momento, tipo.Id, numero, puntoVenta.Id, turno.Id, idCliente,
                idsRemitoDistintos, subtotal, descuentoTotal, total, pagos, medioPorId,
                cliente.LimiteCredito, cliente.CreditoIlimitado, NormalizarOpcional(solicitud.Observaciones), ct));
    }

    /// <summary>design: Transactions — "FACTURAR REMITOS", orden de statements pineado (decisión
    /// 12/13). El comprobante <c>TXR</c> nace SIN items por construcción (precedente <c>RC</c>) —
    /// cero movimientos de stock: la mercadería ya salió por los cuatro write sites de los remitos
    /// individuales.</summary>
    private async Task<ComprobanteEmitido> EjecutarFacturacionAsync(
        int idTenant, int idEmpleado, DateTimeOffset momento, int idTipoComprobante, long numero, int idPuntoVenta,
        int idTurnoCaja, int idCliente, IReadOnlyList<int> idsRemito, decimal subtotal, decimal descuentoTotal,
        decimal total, IReadOnlyList<PagoDeVenta> pagos, IReadOnlyDictionary<int, MedioPago> medioPorId,
        decimal clienteLimiteCredito, bool clienteCreditoIlimitado, string? observaciones, CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        // 0. Turno — re-chequeo bajo FOR SHARE, PRIMER statement (decisión 13 del proposal,
        // mutation target 54): a diferencia del cuarto write site (ServicioDeRemitos.EmitirAsync,
        // que NO lo exige — un remito mueve mercadería, no dinero), la consolidación sí toca cuenta
        // corriente.
        await servicioDeTurnos.ExigirTurnoAbiertoBajoLockAsync(idTurnoCaja, ct);

        // 1. EscriturasDeRemito.BloquearAscendenteAsync — ANTES del INSERT del comprobante y ANTES
        // de clientes (design decisión 12, mutation target 49): el INSERT de fila nueva no es una
        // posición del orden de locks (T10), así que este es el único lock EXISTENTE que esta
        // transacción toma. Ver el doc-comment de EscriturasDeRemito sobre por qué el chequeo de
        // negocio real vive en LigarAsync (paso 5), no acá.
        //
        // Honestidad documental (mismo hallazgo que judgment-day slice-3, juez B, sobre la
        // POSICIÓN 1.5 de la conversión de presupuesto — ver
        // ServicioDeVentasPosicionDeConversionTests): esta POSICIÓN es FAIL-FAST DEFENSIVO, nunca
        // una cuestión de correctitud. Mutación real corrida y confirmada (mutation-proof-tests
        // regla 2): mover este bloque a después del loop de CC (justo antes de LigarAsync) NO tira
        // en rojo ninguno de los rendezvous de la tarea 6.15/6.23 — la ATOMICIDAD de la transacción
        // (cualquier throw revierte TODO lo ya escrito) más el guard final de LigarAsync siguen
        // garantizando la misma corrección observable sin importar en qué línea se tome este lock.
        // La posición SÍ importa por eficiencia (ahorra materializar comprobante/pagos/CC para una
        // consolidación que de todos modos va a fallar) y por mantener el orden total documentado
        // en el Lock order table del design — pineada acá por ese motivo real, sin afirmar una
        // correctitud que la posición no otorga. Ver
        // ServicioDeFacturacionDeRemitosPosicionDeLockTests (fuente de texto) para la prueba
        // estructural de esta posición.
        var filasBloqueadas = await EscriturasDeRemito.BloquearAscendenteAsync(conexion, transaccionCruda, idTenant, idsRemito, ct);
        if (filasBloqueadas.Count != idsRemito.Count)
        {
            throw new InvalidOperationException(
                $"Uno o más remitos de [{string.Join(",", idsRemito)}] desaparecieron bajo el lock — " +
                "invariante de escritura violado (ya se validaron fuera de la transacción).");
        }

        // 2. Comprobante TXR — CERO items por construcción (precedente RC). Subtotal/DescuentoTotal/
        // Total son la suma de los headers ya asertada contra la suma de los items congelados,
        // arriba, fuera de la transacción.
        var comprobante = new ComprobanteVenta
        {
            IdTipoComprobante = idTipoComprobante,
            Numero = numero,
            Fecha = momento,
            IdPuntoVenta = idPuntoVenta,
            IdTurnoCaja = idTurnoCaja,
            IdEmpleado = idEmpleado,
            IdCliente = idCliente,
            Subtotal = subtotal,
            DescuentoTotal = descuentoTotal,
            Total = total,
            Observaciones = observaciones,
            Estado = EstadoComprobante.Emitido,
            CreatedAt = momento,
            UpdatedAt = momento
        };
        db.ComprobantesVenta.Add(comprobante);
        await db.SaveChangesAsync(ct);

        // 3. Pagos.
        var pagosEntidad = pagos
            .Select(p => new PagoComprobante
            {
                IdComprobanteVenta = comprobante.Id,
                IdMedioPago = p.IdMedioPago,
                Importe = p.Importe,
                Referencia = p.Referencia,
                Vuelto = p.Vuelto,
                CreatedAt = momento,
                UpdatedAt = momento
            })
            .ToList();
        db.PagosComprobante.AddRange(pagosEntidad);
        await db.SaveChangesAsync(ct);

        // 4. Cuenta corriente — mismo criterio, mismo loop que ServicioDeVentas.EjecutarTransaccionAsync
        // paso 6 (EscriturasDeCuentaCorriente sin cambios): el backstop de límite de crédito se
        // RE-IMPLEMENTA acá, dentro de la transacción (OD9/T9, mutation target 53) — el
        // ValidadorDePagos de arriba ya corrió AFUERA, contra el saldo de ESE momento; esto atrapa
        // una venta concurrente del mismo cliente que subió el saldo entre el pre-chequeo y este
        // commit.
        for (var i = 0; i < pagos.Count; i++)
        {
            var pago = pagos[i];
            if (medioPorId[pago.IdMedioPago].Comportamiento != ComportamientoMedioPago.CuentaCorriente)
            {
                continue;
            }

            var nuevoSaldo = await EscriturasDeCuentaCorriente.ActualizarSaldoClienteAsync(
                conexion, transaccionCruda, idTenant, idCliente, pago.Importe, ct);

            if (!clienteCreditoIlimitado && nuevoSaldo > clienteLimiteCredito)
            {
                throw new ErrorDominio("limite_credito_excedido", "El pago supera el límite de crédito del cliente.", 400);
            }

            await EscriturasDeCuentaCorriente.InsertarMovimientoCcAsync(
                conexion, transaccionCruda, idTenant, idCliente, momento, idPuntoVenta, idEmpleado,
                TipoMovimientoCc.Consumo, comprobante.Id, pagosEntidad[i].Id, pago.Importe, nuevoSaldo,
                detalle: null, ct);
        }

        // 5. LigarAsync — filas == N o 409 remito_no_facturable (CONFLICT #4, mutation target 50).
        // La autoridad final: nada pudo cambiar estas filas entre el lock del paso 1 y este UPDATE
        // dentro de la MISMA transacción — bajo operación normal esta guardia nunca reduce el
        // rowcount; lo hace exactamente cuando otra consolidación (o una anulación de remito) ganó
        // la carrera del paso 1 sobre un set superpuesto.
        var filasLigadas = await EscriturasDeRemito.LigarAsync(conexion, transaccionCruda, idTenant, idsRemito, comprobante.Id, momento, ct);
        if (filasLigadas != idsRemito.Count)
        {
            throw new ErrorDominio(
                "remito_no_facturable",
                "Uno o más remitos ya no están disponibles para facturar (otra consolidación ganó, o fueron anulados).",
                409);
        }

        // (CERO movimientos_stock — la mercadería ya salió por los remitos.)

        await transaccion.CommitAsync(ct);

        return Proyectar(comprobante, pagosEntidad);
    }

    // ---- Resolución de datos, fuera de la transacción -----------------------------------------

    private async Task<PuntoVenta> ResolverPuntoVentaAsync(int idPuntoVenta, CancellationToken ct) =>
        await db.PuntosVenta.AsNoTracking().FirstOrDefaultAsync(pv => pv.Id == idPuntoVenta, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el punto de venta {idPuntoVenta}.");

    private async Task<TipoComprobante> ResolverTipoTxrAsync(CancellationToken ct) =>
        await db.TiposComprobante.AsNoTracking().FirstOrDefaultAsync(t => t.Codigo == "TXR", ct)
            // Sembrado idempotente para todo tenant desde la migración de Slice 4 (proposal §I data
            // statement 2) — su ausencia es un bug de aprovisionamiento, no un caso de negocio
            // alcanzable (mismo criterio que ResolverTipoRcAsync).
            ?? throw new InvalidOperationException("El tenant actual no tiene el tipo de comprobante TXR sembrado.");

    private async Task<(decimal ToleranciaPago, decimal VueltoMaximo)> ResolverParametrosDeFacturacionAsync(
        int idEmpresa, int idPuntoVenta, CancellationToken ct)
    {
        ParametroConocido[] conocidos = [ParametroConocido.ToleranciaPago, ParametroConocido.VueltoMaximo];
        var claves = conocidos.Select(c => c.Clave).ToList();

        var candidatos = await db.Parametros
            .Where(p => claves.Contains(p.Clave) && p.IdEmpresa == idEmpresa
                && (p.IdPuntoVenta == null || p.IdPuntoVenta == idPuntoVenta))
            .ToListAsync(ct);

        var resueltoPorClave = conocidos.ToDictionary(
            c => c.Clave,
            c => ResolucionDeParametros.Resolver(c.Clave, candidatos.Where(p => p.Clave == c.Clave).ToList(), idPuntoVenta));

        return (
            JsonSerializer.Deserialize<decimal>(resueltoPorClave[ParametroConocido.ToleranciaPago.Clave]),
            JsonSerializer.Deserialize<decimal>(resueltoPorClave[ParametroConocido.VueltoMaximo.Clave]));
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
            ?? throw new InvalidOperationException(
                "ServicioDeFacturacionDeRemitos requiere un actor de tenant; OperacionDePos no admite plataforma.");

    private static string? NormalizarOpcional(string? valor)
    {
        var limpio = valor?.Trim();
        return string.IsNullOrEmpty(limpio) ? null : limpio;
    }

    private static ComprobanteEmitido Proyectar(
        ComprobanteVenta comprobante, IReadOnlyList<PagoComprobante> pagos) => new(
        comprobante.Id, comprobante.Numero,
        NumeroDeComprobante.Formatear(comprobante.IdPuntoVenta, comprobante.Numero),
        comprobante.Estado, comprobante.Fecha, comprobante.IdPuntoVenta, comprobante.IdCliente,
        comprobante.IdComprobanteAsociado, comprobante.Subtotal, comprobante.DescuentoTotal, comprobante.Total,
        comprobante.DireccionEntrega, comprobante.Observaciones,
        [],
        pagos
            .Select(p => new PagoEmitido(p.IdMedioPago, p.Importe, p.Referencia, p.Vuelto))
            .ToList());
}
