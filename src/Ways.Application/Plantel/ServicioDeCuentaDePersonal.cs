using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Domain.Common;
using Ways.Domain.Plantel;

namespace Ways.Application.Plantel;

/// <summary>
/// Cuenta corriente del personal: carga de horas, ajuste y estado de cuenta. La autorización
/// (<c>GestionDeCuentaDePersonal</c> para escribir, <c>LecturaDePersonal</c> para leer) se aplica en la
/// capa de API. Un personal inexistente, dado de baja o de otro tenant responde 404 antes de validar
/// el cuerpo.
/// </summary>
public sealed class ServicioDeCuentaDePersonal(IWaysDbContext db, IRelojDelSistema reloj, IContextoDeUsuario contexto)
{
    public async Task<MovimientoDePersonal> CargarHorasAsync(
        int idPersonal, SolicitudDeCargaDeHoras solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var valorHoraVigente = await ResolverValorHoraAsync(idPersonal, ct);

        if (solicitud.Fecha is not { } fecha || fecha == default)
        {
            throw new ErrorDominio("fecha_requerida", "La fecha trabajada es obligatoria.", 400);
        }

        var valorHora = solicitud.ValorHora ?? valorHoraVigente;
        var importe = ReglaDeMovimientoDePersonal.CalcularImporteDeHoras(solicitud.Horas, valorHora);
        var detalle = ReglaDeMovimientoDePersonal.NormalizarDetalleOpcional(solicitud.Detalle);

        return await EscribirAsync(
            idTenant, idPersonal, fecha, TipoMovimientoPersonal.Horas, solicitud.Horas, valorHora, importe, detalle, ct);
    }

    public async Task<MovimientoDePersonal> RegistrarAjusteAsync(
        int idPersonal, SolicitudDeAjusteDePersonal solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        await ResolverValorHoraAsync(idPersonal, ct);

        if (solicitud.Fecha is not { } fecha || fecha == default)
        {
            throw new ErrorDominio("fecha_requerida", "La fecha del ajuste es obligatoria.", 400);
        }

        var detalle = ReglaDeMovimientoDePersonal.ValidarAjuste(solicitud.Importe, solicitud.Detalle);

        return await EscribirAsync(
            idTenant, idPersonal, fecha, TipoMovimientoPersonal.Ajuste, horas: null, valorHora: null,
            solicitud.Importe, detalle, ct);
    }

    public async Task<PaginaDeCuentaDePersonal> ObtenerCuentaAsync(
        int idPersonal, DateOnly? desde, DateOnly? hasta, bool historico, int pagina, int tamanio,
        CancellationToken ct = default)
    {
        var personal = await db.Personal
            .Where(p => p.Id == idPersonal)
            .Select(p => new { p.Nombre, p.Saldo })
            .FirstOrDefaultAsync(ct)
            ?? throw SinPersonal(idPersonal);

        pagina = Math.Max(pagina, 1);
        tamanio = Math.Clamp(tamanio, 1, 200);

        if ((long)(pagina - 1) * tamanio > int.MaxValue)
        {
            throw new ErrorDominio("pagina_invalida", "La página pedida está fuera de rango.", 400);
        }

        if (hasta is { } hastaPedido && hastaPedido < DateOnly.MinValue.AddMonths(1))
        {
            throw new ErrorDominio("fecha_invalida", "La fecha hasta está fuera de rango.", 400);
        }

        DateOnly? desdeEfectivo = null;
        DateOnly? hastaEfectivo = null;
        if (!historico)
        {
            desdeEfectivo = desde ?? (hasta is { } hastaSinDesde ? hastaSinDesde.AddMonths(-1) : HoyUtc().AddMonths(-1));
            hastaEfectivo = hasta;
        }

        var consulta = db.MovimientosPersonal.Where(m => m.IdPersonal == idPersonal);

        if (desdeEfectivo is { } desdeAplicado)
        {
            consulta = consulta.Where(m => m.Fecha >= desdeAplicado);
        }

        if (hastaEfectivo is { } hastaAplicado)
        {
            consulta = consulta.Where(m => m.Fecha <= hastaAplicado);
        }

        var total = await consulta.CountAsync(ct);

        // El desempate por id mantiene estable la paginación cuando varias cargas comparten el día.
        var items = await consulta
            .OrderByDescending(m => m.Fecha)
            .ThenByDescending(m => m.Id)
            .Skip((pagina - 1) * tamanio)
            .Take(tamanio)
            .Select(m => new MovimientoDePersonal(
                m.Id, m.Fecha, m.Tipo, m.Horas, m.ValorHora, m.Importe, m.SaldoResultante, m.Detalle, m.CreatedAt))
            .ToListAsync(ct);

        var idsDePagos = items.Where(i => i.Tipo == TipoMovimientoPersonal.Pago).Select(i => i.IdMovimiento).ToList();
        if (idsDePagos.Count > 0)
        {
            var desgloses = await LectorDeDesgloseDePagos.LeerAsync(db, idsDePagos, ct);
            items = items
                .Select(i => desgloses.TryGetValue(i.IdMovimiento, out var desglose) ? i with { Desglose = desglose } : i)
                .ToList();
        }

        return new PaginaDeCuentaDePersonal(
            new EncabezadoDeCuentaDePersonal(idPersonal, personal.Nombre, personal.Saldo),
            items, total, pagina, tamanio, historico, desdeEfectivo, hastaEfectivo);
    }

    /// <summary>Existencia del personal vivo del tenant, primera sentencia de las escrituras. Devuelve su
    /// valor hora vigente.</summary>
    private async Task<decimal> ResolverValorHoraAsync(int idPersonal, CancellationToken ct)
    {
        var valores = await db.Personal
            .Where(p => p.Id == idPersonal)
            .Select(p => new { p.ValorHora })
            .FirstOrDefaultAsync(ct);

        return valores?.ValorHora ?? throw SinPersonal(idPersonal);
    }

    private async Task<MovimientoDePersonal> EscribirAsync(
        int idTenant, int idPersonal, DateOnly fecha, TipoMovimientoPersonal tipo, decimal? horas,
        decimal? valorHora, decimal importe, string? detalle, CancellationToken ct)
    {
        var idEmpleado = contexto.UsuarioId;
        var ahora = reloj.Ahora;

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        return await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);
            var conexion = await ObtenerConexionAbiertaAsync(ct);
            var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

            var nuevoSaldo = await EscriturasDePersonal.ActualizarSaldoAsync(
                conexion, transaccionCruda, idTenant, idPersonal, importe, ct)
                ?? throw SinPersonal(idPersonal);

            // El UPDATE de arriba ya tomó el lock de la fila del personal: el total del día se lee recién ahora.
            decimal? totalDelDia = null;
            if (tipo == TipoMovimientoPersonal.Horas)
            {
                var delDia = await EscriturasDePersonal.LeerAsientosDeHorasAsync(
                    conexion, transaccionCruda, idTenant, idPersonal, [fecha], ct);
                var acreditadas = delDia.TryGetValue(fecha, out var asientos) ? asientos.Sum(a => a.Horas) : 0m;

                totalDelDia = acreditadas + horas!.Value;
                if (totalDelDia > ReglaDeMovimientoDePersonal.HorasMaximas)
                {
                    throw new ErrorDominio(
                        "horas_del_dia_excedidas",
                        $"El día {fecha:yyyy-MM-dd} ya tiene {acreditadas:0.##} horas cargadas y no puede superar las {ReglaDeMovimientoDePersonal.HorasMaximas:0}.",
                        400);
                }
            }

            var idMovimiento = await EscriturasDePersonal.InsertarMovimientoAsync(
                conexion, transaccionCruda, idTenant, idPersonal, idEmpleado, fecha, tipo, horas, valorHora,
                importe, nuevoSaldo, detalle, ahora, ct);

            if (totalDelDia is { } total)
            {
                await EscriturasDePersonal.UpsertarJornadaAsync(
                    conexion, transaccionCruda, idTenant, idPersonal, idEmpleado, fecha, total, ahora, ct);
            }

            await transaccion.CommitAsync(ct);

            return new MovimientoDePersonal(
                idMovimiento, fecha, tipo, horas, valorHora, importe, nuevoSaldo, detalle, ahora);
        });
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

    private DateOnly HoyUtc() => DateOnly.FromDateTime(reloj.Ahora.UtcDateTime);

    private static ErrorDominio SinPersonal(int id) =>
        ErrorDominio.NoEncontrado($"No existe el personal {id}.");

    private int ExigirTenantDeLaSesion() =>
        contexto.IdTenant
            ?? throw new InvalidOperationException(
                "ServicioDeCuentaDePersonal requiere un actor de tenant; las políticas de este servicio no admiten plataforma.");
}
