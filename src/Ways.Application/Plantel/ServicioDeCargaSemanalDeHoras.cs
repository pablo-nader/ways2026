using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Plantel;

namespace Ways.Application.Plantel;

/// <summary>
/// Carga semanal de horas: la grilla (lunes a domingo × personal vivo) y el guardado de la semana entera. La
/// autorización (<c>LecturaDePersonal</c> para leer, <c>GestionDeCuentaDePersonal</c> para guardar) se aplica
/// en la capa de API. Guardar escribe solo las diferencias en el libro (append-only) y confirma cada día en
/// <c>jornadas_personal</c>; nada se acredita antes de guardar.
/// </summary>
public sealed class ServicioDeCargaSemanalDeHoras(IWaysDbContext db, IRelojDelSistema reloj, IContextoDeUsuario contexto)
{
    private const string DetalleDeCargaSemanal = "Carga semanal";

    public async Task<SemanaDeCargaDeHoras> ObtenerSemanaAsync(DateOnly? desde, CancellationToken ct = default)
    {
        var zonaPorDefecto = ZonaPorDefecto();
        var hoy = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(reloj.Ahora, zonaPorDefecto).DateTime);

        var lunes = desde ?? ReglaDeSemanaDeCarga.LunesDe(hoy);
        ReglaDeSemanaDeCarga.ExigirLunes(lunes);
        var domingo = lunes.AddDays(ReglaDeSemanaDeCarga.DiasPorSemana - 1);

        var personal = await db.Personal
            .OrderBy(p => p.Nombre).ThenBy(p => p.Id)
            .Select(p => new { p.Id, p.Nombre, p.ValorHora, p.HorarioHabitual, p.IdEmpresa })
            .ToListAsync(ct);

        var zonas = await ResolverZonasAsync(personal.Select(p => p.IdEmpresa), zonaPorDefecto, ct);

        var cargadas = (await db.MovimientosPersonal
                .Where(m => m.Tipo == TipoMovimientoPersonal.Horas && m.Fecha >= lunes && m.Fecha <= domingo)
                .GroupBy(m => new { m.IdPersonal, m.Fecha })
                .Select(g => new { g.Key.IdPersonal, g.Key.Fecha, Horas = g.Sum(m => m.Horas ?? 0m) })
                .ToListAsync(ct))
            .ToDictionary(c => (c.IdPersonal, c.Fecha), c => c.Horas);

        var jornadas = (await db.JornadasPersonal
                .Where(j => j.Fecha >= lunes && j.Fecha <= domingo)
                .Select(j => new { j.IdPersonal, j.Fecha })
                .ToListAsync(ct))
            .Select(j => (j.IdPersonal, j.Fecha))
            .ToHashSet();

        var filas = personal.Select(p =>
        {
            var hoyDelPersonal = zonas.HoyDe(p.IdEmpresa, reloj.Ahora);
            var dias = Enumerable.Range(0, ReglaDeSemanaDeCarga.DiasPorSemana).Select(i =>
            {
                var fecha = lunes.AddDays(i);
                var horasCargadas = cargadas.GetValueOrDefault((p.Id, fecha));
                var confirmado = jornadas.Contains((p.Id, fecha)) || cargadas.ContainsKey((p.Id, fecha));
                var futuro = fecha > hoyDelPersonal;
                var horas = confirmado
                    ? horasCargadas
                    : !futuro && p.HorarioHabitual is { } horario ? horario[i] : (decimal?)null;

                return new DiaDeCargaSemanal(fecha, horasCargadas, confirmado, horas, !confirmado && horas is not null, futuro);
            }).ToList();

            return new FilaDeCargaSemanal(p.Id, p.Nombre, p.ValorHora, p.HorarioHabitual, dias);
        }).ToList();

        return new SemanaDeCargaDeHoras(lunes, domingo, hoy, filas);
    }

    public async Task<ResultadoDeCargaSemanal> GuardarSemanaAsync(
        SolicitudDeCargaSemanal? solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();

        var entradas = solicitud?.Dias;
        if (entradas is null || entradas.Count == 0)
        {
            throw new ErrorDominio("solicitud_invalida", "La solicitud debe traer al menos un día.", 400);
        }

        if (entradas.Count > ReglaDeSemanaDeCarga.EntradasMaximasPorGuardado)
        {
            throw new ErrorDominio(
                "solicitud_invalida",
                $"La solicitud no puede traer más de {ReglaDeSemanaDeCarga.EntradasMaximasPorGuardado} días.",
                400);
        }

        if (entradas.Any(e => e is null))
        {
            throw new ErrorDominio("solicitud_invalida", "Los días de la solicitud no pueden ser nulos.", 400);
        }

        var dias = await ValidarAsync(entradas, ct);

        var idEmpleado = contexto.UsuarioId;
        var ahora = reloj.Ahora;

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        return await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);
            var conexion = await ObtenerConexionAbiertaAsync(ct);
            var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

            var movimientosCreados = 0;

            // Locks en orden ascendente de id: dos guardados que comparten personal no se abrazan.
            foreach (var delPersonal in dias.GroupBy(d => d.IdPersonal).OrderBy(g => g.Key))
            {
                var idPersonal = delPersonal.Key;
                var valorHora = await EscriturasDePersonal.BloquearPersonalAsync(
                        conexion, transaccionCruda, idTenant, idPersonal, ct)
                    ?? throw SinPersonal(idPersonal);

                var fechas = delPersonal.Select(d => d.Fecha).ToList();
                var existentes = await EscriturasDePersonal.LeerAsientosDeHorasAsync(
                    conexion, transaccionCruda, idTenant, idPersonal, fechas, ct);

                foreach (var dia in delPersonal.OrderBy(d => d.Fecha))
                {
                    var delDia = existentes.TryGetValue(dia.Fecha, out var asientos) ? asientos : [];
                    var plan = PlanificadorDeHorasDelDia.Planificar(delDia, dia.Horas, valorHora);

                    foreach (var asiento in plan)
                    {
                        var nuevoSaldo = await EscriturasDePersonal.ActualizarSaldoAsync(
                                conexion, transaccionCruda, idTenant, idPersonal, asiento.Importe, ct)
                            ?? throw SinPersonal(idPersonal);

                        await EscriturasDePersonal.InsertarMovimientoAsync(
                            conexion, transaccionCruda, idTenant, idPersonal, idEmpleado, dia.Fecha,
                            TipoMovimientoPersonal.Horas, asiento.Horas, asiento.ValorHora, asiento.Importe,
                            nuevoSaldo, DetalleDeCargaSemanal, ahora, ct);
                        movimientosCreados++;
                    }

                    await EscriturasDePersonal.UpsertarJornadaAsync(
                        conexion, transaccionCruda, idTenant, idPersonal, idEmpleado, dia.Fecha, dia.Horas, ahora, ct);
                }
            }

            await transaccion.CommitAsync(ct);

            return new ResultadoDeCargaSemanal(movimientosCreados, dias.Count);
        });
    }

    private sealed record DiaValidado(int IdPersonal, DateOnly Fecha, decimal Horas);

    /// <summary>Todo lo que se puede rechazar sin escribir. Un personal inexistente, dado de baja o de otro tenant
    /// responde 404 antes de validar el resto del cuerpo, igual que la carga individual.</summary>
    private async Task<List<DiaValidado>> ValidarAsync(IReadOnlyList<DiaACargarSemanal> entradas, CancellationToken ct)
    {
        var ids = entradas.Select(e => e.IdPersonal).Distinct().ToList();
        var existentes = await db.Personal
            .Where(p => ids.Contains(p.Id))
            .Select(p => new { p.Id, p.IdEmpresa })
            .ToListAsync(ct);

        var faltante = ids.Order().Where(id => existentes.All(p => p.Id != id)).Select(id => (int?)id).FirstOrDefault();
        if (faltante is { } idFaltante)
        {
            throw SinPersonal(idFaltante);
        }

        var zonas = await ResolverZonasAsync(existentes.Select(p => p.IdEmpresa), ZonaPorDefecto(), ct);
        var empresaDe = existentes.ToDictionary(p => p.Id, p => p.IdEmpresa);

        var vistos = new HashSet<(int, DateOnly)>();
        var resultado = new List<DiaValidado>(entradas.Count);

        foreach (var entrada in entradas)
        {
            if (entrada?.Fecha is not { } fecha || fecha == default)
            {
                throw new ErrorDominio("fecha_requerida", "La fecha de cada día es obligatoria.", 400);
            }

            if (!vistos.Add((entrada.IdPersonal, fecha)))
            {
                throw new ErrorDominio(
                    "dia_duplicado", $"El personal {entrada.IdPersonal} viene repetido el {fecha:yyyy-MM-dd}.", 400);
            }

            if (fecha > zonas.HoyDe(empresaDe[entrada.IdPersonal], reloj.Ahora))
            {
                throw new ErrorDominio(
                    "fecha_futura", $"El {fecha:yyyy-MM-dd} todavía no llegó: no se pueden cargar horas de días futuros.", 400);
            }

            if (entrada.Horas is not { } horas)
            {
                throw new ErrorDominio("horas_invalidas", "Las horas de cada día son obligatorias.", 400);
            }

            ReglaDeSemanaDeCarga.ValidarHorasDelDia(horas);
            resultado.Add(new DiaValidado(entrada.IdPersonal, fecha, horas));
        }

        return resultado;
    }

    /// <summary>Zona horaria de cada empresa (parámetro <c>zona_horaria</c> a nivel empresa) o la de por
    /// defecto del sistema: una sola consulta, sin una por personal.</summary>
    private async Task<ZonasHorarias> ResolverZonasAsync(
        IEnumerable<int?> idsDeEmpresa, TimeZoneInfo zonaPorDefecto, CancellationToken ct)
    {
        var empresas = idsDeEmpresa.OfType<int>().Distinct().ToList();
        var porEmpresa = new Dictionary<int, TimeZoneInfo>();

        if (empresas.Count > 0)
        {
            var filas = await db.Parametros
                .Where(p => p.Clave == ParametroConocido.ZonaHoraria.Clave && p.IdPuntoVenta == null
                    && empresas.Contains(p.IdEmpresa))
                .Select(p => new { p.IdEmpresa, p.Valor })
                .ToListAsync(ct);

            foreach (var fila in filas)
            {
                porEmpresa[fila.IdEmpresa] = TimeZoneInfo.FindSystemTimeZoneById(JsonSerializer.Deserialize<string>(fila.Valor)!);
            }
        }

        return new ZonasHorarias(porEmpresa, zonaPorDefecto);
    }

    private sealed record ZonasHorarias(IReadOnlyDictionary<int, TimeZoneInfo> PorEmpresa, TimeZoneInfo PorDefecto)
    {
        public DateOnly HoyDe(int? idEmpresa, DateTimeOffset ahora)
        {
            var zona = idEmpresa is { } id && PorEmpresa.TryGetValue(id, out var propia) ? propia : PorDefecto;
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(ahora, zona).DateTime);
        }
    }

    private static TimeZoneInfo ZonaPorDefecto() =>
        TimeZoneInfo.FindSystemTimeZoneById(JsonSerializer.Deserialize<string>(ParametroConocido.ZonaHoraria.ValorPorDefecto)!);

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
                "ServicioDeCargaSemanalDeHoras requiere un actor de tenant; las políticas de este servicio no admiten plataforma.");
}
