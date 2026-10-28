using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.CuentaCorriente;
using Ways.Application.Pagos;
using Ways.Domain.Common;
using Ways.Domain.Pagos;
using Ways.Domain.Plantel;
using Ways.Domain.Usuarios;

namespace Ways.Application.Plantel;

/// <summary>
/// Pago a un empleado en una sola transacción: un asiento <c>pago</c> en el libro del personal (la cabecera),
/// el descuento opcional de la cuenta corriente del cliente vinculado, y un pago de sueldos por cada línea de
/// dinero, con su egreso de tesorería o su impacto en el arqueo del turno según el origen elegido en cada pago.
/// La autorización (<c>GestionDeCuentaDePersonal</c>: Admin y Supervisor) se aplica en la capa de API; además,
/// el origen tesorería es solo de Admin y se rechaza acá con 403. Un personal inexistente, dado de baja o de
/// otro tenant responde 404 antes de validar el cuerpo.
///
/// Forma (b) de ef-retry-safe-writes: <see cref="FabricaDeEstrategiaSinReintento"/>, porque el pago no tiene
/// clave de idempotencia y un reintento sobre un commit ambiguo pagaría dos veces.
///
/// Orden de locks, en este orden y sin excepciones:
/// 1) turno <c>FOR SHARE</c> (solo origen cajón; mismo primer statement que el alta de pago del POS) →
/// 2) fila del personal (<c>UPDATE ... RETURNING</c>, que también lee el cliente vinculado bajo el lock) →
/// 3) fila del cliente (<c>UPDATE ... RETURNING</c> que además exige <c>deleted_at IS NULL</c>, solo con descuento;
///    sin fila viva responde 409 <c>personal_sin_cliente_vinculado</c> y todo el pago se revierte) →
/// 4) inserts sin lock explícito: asiento del personal, asiento de cuenta corriente y pagos →
/// 5) advisory de tesorería de la empresa, ÚLTIMO (solo origen tesorería).
/// Sin ciclo con los escritores existentes: el alta de pago, el pago de una compra y el cierre de turno usan
/// el mismo orden relativo (turno, filas de negocio, advisory al final); la edición y la baja de pagos toman
/// turno, pago, proveedor y advisory, y nunca lockean un pago de este pago porque lo rechazan antes de los
/// locks; las ventas y los pagos a cuenta toman turno y después el cliente, que acá viene después del turno;
/// la carga de horas, el ajuste y la baja del personal solo lockean la fila del personal, que acá es el primer
/// lock de fila; y ningún escritor toma la fila del cliente y después la del personal (la baja de un cliente,
/// <c>ServicioDeClientes.EliminarAsync</c>, es una baja lógica común: no toma lock explícito ni consulta
/// referencias, y solo escribe la fila del cliente). Como esa baja no está serializada con el pago por ningún lock
/// propio, el pago no confía en una lectura previa del cliente: el predicado <c>deleted_at IS NULL</c> del
/// <c>UPDATE</c> atómico espera la baja en curso y la reevalúa. Dos pagos al mismo personal se
/// serializan en el paso 2; dos pagos de personales distintos no comparten filas y solo se cruzan en el
/// advisory, que ambos toman al final.
/// </summary>
public sealed class ServicioDePagoDePersonal(
    IWaysDbContext db, ServicioDePagos servicioDePagos, ServicioDeTurnos servicioDeTurnos, IRelojDelSistema reloj,
    IContextoDeUsuario contexto)
{
    public async Task<MovimientoDePersonal> PagarAsync(
        int idPersonal, SolicitudDePagoDePersonal solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var nombre = await db.Personal
            .Where(p => p.Id == idPersonal)
            .Select(p => p.Nombre)
            .FirstOrDefaultAsync(ct)
            ?? throw SinPersonal(idPersonal);

        // Pagar de la tesorería es de Admin, igual que el alta administrativa de pagos
        // (GestionDeCatalogo): el Supervisor de GestionDeCuentaDePersonal paga del cajón o descuenta de la cuenta
        // corriente. Se decide con el origen pedido y antes de cualquier 400 que dependa del resto del cuerpo.
        if (solicitud.OrigenFondos == OrigenFondosPago.Tesoreria && contexto.Rol != RolConocido.Admin)
        {
            throw ErrorDominio.Prohibido("Pagar de la tesorería requiere un administrador.");
        }

        if (solicitud.Fecha is not { } fecha || fecha == default)
        {
            throw new ErrorDominio("fecha_requerida", "La fecha del pago es obligatoria.", 400);
        }

        var plan = ReglaDePagoDePersonal.Planificar(
            solicitud.DescuentoDeCuentaCorriente,
            solicitud.Lineas?.Select(l => l is null ? null : new LineaDePagoDePersonal(l.IdMedioPago, l.Importe, l.Referencia)).ToList(),
            solicitud.OrigenFondos,
            solicitud.Detalle);
        ReglaDePagoDePersonal.ExigirUbicacion(plan, solicitud.IdEmpresa, solicitud.IdPuntoVenta);

        var entorno = await servicioDePagos.PrepararPagosDeSueldoAsync(
            plan.OrigenFondos, solicitud.IdEmpresa, solicitud.IdPuntoVenta, fecha,
            plan.Lineas.Select(l => l.IdMedioPago).ToList(), ct);

        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        return await estrategia.ExecuteAsync(async () =>
            await EjecutarAsync(idTenant, idPersonal, nombre, fecha, plan, entorno, idEmpleado, momento, ct));
    }

    private async Task<MovimientoDePersonal> EjecutarAsync(
        int idTenant, int idPersonal, string nombre, DateOnly fecha, PlanDePagoDePersonal plan,
        EntornoDePagoDeSueldo entorno, int idEmpleado, DateTimeOffset momento, CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        if (entorno.IdTurnoCaja is { } idTurnoCaja)
        {
            await servicioDeTurnos.ExigirTurnoAbiertoBajoLockAsync(idTurnoCaja, ct);
        }

        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        var personal = await EscriturasDePersonal.ActualizarSaldoYLeerVinculoAsync(
            conexion, transaccionCruda, idTenant, idPersonal, -plan.Total, ct)
            ?? throw SinPersonal(idPersonal);

        var idCliente = 0;
        var saldoDelCliente = 0m;
        if (plan.DescuentoDeCuentaCorriente is { } descuento)
        {
            // El vínculo se leyó bajo el lock del personal; que el cliente siga vivo y la deuda alcance se deciden
            // sobre lo que devuelve el UPDATE atómico, nunca sobre una lectura previa. Sin fila viva, el pago
            // entero se revierte.
            idCliente = personal.IdCliente ?? throw SinClienteVinculado();
            saldoDelCliente = await EscriturasDeCuentaCorriente.ActualizarSaldoDeClienteVivoAsync(
                conexion, transaccionCruda, idTenant, idCliente, -descuento, ct)
                ?? throw SinClienteVinculado();

            if (saldoDelCliente < 0m)
            {
                throw ErrorDominio.Conflicto(
                    "descuento_supera_la_deuda", "El descuento supera la deuda actual del cliente vinculado.");
            }
        }

        var idMovimiento = await EscriturasDePersonal.InsertarMovimientoAsync(
            conexion, transaccionCruda, idTenant, idPersonal, idEmpleado, fecha, TipoMovimientoPersonal.Pago,
            horas: null, valorHora: null, -plan.Total, personal.Saldo, plan.Detalle, momento, ct);

        if (plan.DescuentoDeCuentaCorriente is { } descuentoPagado)
        {
            await EscriturasDeCuentaCorriente.InsertarDescuentoDePersonalAsync(
                conexion, transaccionCruda, idTenant, idCliente, momento, entorno.IdPuntoVenta!.Value, idEmpleado,
                idMovimiento, -descuentoPagado, saldoDelCliente, $"Descuento por pago de personal: {nombre}", ct);
        }

        var lineas = new List<LineaDeDesglose>();
        if (plan.OrigenFondos is { } origenFondos)
        {
            var pagos = await servicioDePagos.PersistirPagosDeSueldoAsync(
                idTenant, entorno, origenFondos, nombre, idMovimiento, plan.Lineas, idEmpleado, momento, ct);

            lineas.AddRange(pagos.Select(g => new LineaDeDesglose(
                g.Pago.Id, g.Item.Id, g.Item.IdMedioPago, entorno.NombresDeMedios.GetValueOrDefault(g.Item.IdMedioPago),
                g.Item.Importe, g.Pago.OrigenFondos, g.Pago.Detalle)));
        }

        await transaccion.CommitAsync(ct);

        return new MovimientoDePersonal(
            idMovimiento, fecha, TipoMovimientoPersonal.Pago, Horas: null, ValorHora: null, -plan.Total, personal.Saldo,
            plan.Detalle, momento, new DesgloseDePago(plan.DescuentoDeCuentaCorriente, lineas));
    }

    /// <summary>Un cliente dado de baja se trata como sin vincular.</summary>
    private static ErrorDominio SinClienteVinculado() =>
        ErrorDominio.Conflicto(
            "personal_sin_cliente_vinculado",
            "El personal no tiene un cliente vinculado para descontar de su cuenta corriente.");

    private async Task<DbConnection> ObtenerConexionAbiertaAsync(CancellationToken ct)
    {
        var conexion = db.Database.GetDbConnection();

        if (conexion.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        return conexion;
    }

    private static ErrorDominio SinPersonal(int id) =>
        ErrorDominio.NoEncontrado($"No existe el personal {id}.");

    private int ExigirTenantDeLaSesion() =>
        contexto.IdTenant
            ?? throw new InvalidOperationException(
                "ServicioDePagoDePersonal requiere un actor de tenant; las políticas de este servicio no admiten plataforma.");
}
