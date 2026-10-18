using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Parametros;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Reportes;
using Ways.Domain.Ventas;

namespace Ways.Application.Reportes;

public enum AgrupacionDeVentas
{
    PuntoVenta,
    MedioPago,
    Vendedor,
    Dia
}

/// <summary>Lo que pide el conector MCP: un período (nombre o fechas) y, opcionalmente, empresa y
/// punto de venta por id o por nombre. Todo llega como texto y se interpreta acá.</summary>
public sealed record SolicitudDeConsultaDeVentas(
    string? Periodo, string? Desde, string? Hasta, string? Empresa, string? PuntoVenta);

public sealed record EmpresaConsultada(
    int IdEmpresa, string Nombre, string ZonaHoraria, DateOnly Desde, DateOnly Hasta, decimal Neto, int CantidadTx);

/// <summary>Una fila de una tabla de la consulta: <see cref="Cantidad"/> cuenta comprobantes, salvo
/// en el desglose por medio de pago, donde cuenta pagos.</summary>
public sealed record FilaDeVentas(string Grupo, decimal Importe, int Cantidad);

public sealed record ResumenDeVentasConsultado(
    string? Periodo, DateOnly Desde, DateOnly Hasta, IReadOnlyList<string> Zonas, string Alcance,
    bool FiltraPuntoVenta, decimal NetoVendido, int CantidadTx, decimal? TicketPromedio, int CantidadNcx, decimal NetoNcx,
    IReadOnlyList<EmpresaConsultada> Empresas, IReadOnlyList<FilaDeVentas> PorMedioPago,
    IReadOnlyList<FilaDeVentas> PorPuntoVenta, IReadOnlyList<FilaDeVentas>? PorDia);

/// <param name="Total">Suma de <see cref="Filas"/>: para punto de venta, vendedor y día coincide con
/// lo vendido; para medio de pago es lo cobrado neto de vuelto.</param>
public sealed record AgrupacionConsultada(
    string? Periodo, DateOnly Desde, DateOnly Hasta, IReadOnlyList<string> Zonas, string Alcance,
    AgrupacionDeVentas Por, decimal Total, IReadOnlyList<FilaDeVentas> Filas);

/// <summary>
/// Consultas de ventas del conector MCP. No calcula "vendido" por su cuenta: delega en
/// <see cref="ServicioDeReportesDeVentas"/> (mismo criterio que el reporte de la web) y solo suma lo
/// que el reporte entrega por empresa. La excepción es el desglose por medio de pago, que es una
/// consulta propia porque descuenta el vuelto (ver <see cref="ConsultarMediosDePagoAsync"/>).
/// Cada empresa resuelve su propia zona horaria y su propio "hoy".
/// </summary>
public class ServicioDeConsultasDeVentas(
    IWaysDbContext db, IRelojDelSistema reloj, ServicioDeParametros parametros, ServicioDeReportesDeVentas reportes)
{
    private static readonly IReadOnlyDictionary<string, AgrupacionDeVentas> Agrupaciones = new Dictionary<string, AgrupacionDeVentas>
    {
        ["punto_venta"] = AgrupacionDeVentas.PuntoVenta,
        ["medio_pago"] = AgrupacionDeVentas.MedioPago,
        ["vendedor"] = AgrupacionDeVentas.Vendedor,
        ["dia"] = AgrupacionDeVentas.Dia
    };

    private const int DiasMaximosDeLaTablaPorDia = 31;

    public static IReadOnlyCollection<string> NombresDeAgrupacion => Agrupaciones.Keys.ToList();

    public static AgrupacionDeVentas ParsearAgrupacion(string? texto) =>
        texto is not null && Agrupaciones.TryGetValue(texto.Trim().ToLowerInvariant(), out var agrupacion)
            ? agrupacion
            : throw new ErrorDominio(
                "agrupacion_desconocida",
                $"'agrupar_por' tiene que ser uno de: {string.Join(", ", Agrupaciones.Keys)}.",
                400);

    public async Task<ResumenDeVentasConsultado> ResumirAsync(SolicitudDeConsultaDeVentas solicitud, CancellationToken ct = default)
    {
        var alcance = await ResolverAlcanceAsync(solicitud, ct);

        decimal neto = 0, netoNcx = 0, netoTx = 0;
        int cantidadTx = 0, cantidadNcx = 0;
        var empresas = new List<EmpresaConsultada>();
        var medios = new Dictionary<int, Acumulado>();
        var puntos = new Dictionary<int, Acumulado>();
        var dias = new Dictionary<int, Acumulado>();
        var tablaPorDia = true;

        foreach (var contexto in alcance.Contextos)
        {
            var empresa = contexto.Empresa;
            var resumen = await reportes.ObtenerResumenAsync(
                empresa.Id, empresa.IdPuntoVenta, contexto.Desde, contexto.Hasta, Granularidad.Dia, ct);

            neto += resumen.NetoVendido;
            cantidadTx += resumen.CantidadTx;
            cantidadNcx += resumen.CantidadNcx;
            netoNcx += resumen.NetoNcx;
            netoTx += resumen.NetoVendido - resumen.NetoNcx;
            empresas.Add(new EmpresaConsultada(
                empresa.Id, empresa.Nombre, contexto.ZonaId, contexto.Desde, contexto.Hasta, resumen.NetoVendido, resumen.CantidadTx));

            tablaPorDia &= contexto.Hasta.DayNumber - contexto.Desde.DayNumber + 1 <= DiasMaximosDeLaTablaPorDia;
            foreach (var bucket in resumen.Serie)
            {
                Sumar(dias, bucket.Inicio.DayNumber, bucket.Neto, bucket.CantidadTx);
            }

            foreach (var fila in await ConsultarMediosDePagoAsync(contexto, ct))
            {
                Sumar(medios, fila.Key, fila.Value.Importe, fila.Value.Cantidad);
            }

            if (empresa.IdPuntoVenta is null)
            {
                var porPuntoVenta = await reportes.ObtenerPorPuntoVentaAsync(empresa.Id, contexto.Desde, contexto.Hasta, ct);
                foreach (var fila in porPuntoVenta.Filas)
                {
                    Sumar(puntos, fila.IdPuntoVenta, fila.Neto, fila.CantidadTx);
                }
            }
        }

        var primero = alcance.Contextos[0];
        var porDia = tablaPorDia
            ? dias.OrderBy(d => d.Key)
                .Select(d => new FilaDeVentas(
                    DateOnly.FromDayNumber(d.Key).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), d.Value.Importe, d.Value.Cantidad))
                .ToList()
            : null;

        return new ResumenDeVentasConsultado(
            NombreSiEsConNombre(solicitud.Periodo),
            alcance.Contextos.Min(c => c.Desde),
            alcance.Contextos.Max(c => c.Hasta),
            alcance.Contextos.Select(c => c.ZonaId).Distinct().ToList(),
            alcance.Descripcion,
            primero.Empresa.IdPuntoVenta is not null,
            neto, cantidadTx, cantidadTx > 0 ? netoTx / cantidadTx : null, cantidadNcx, netoNcx,
            empresas,
            await NombrarAsync(AgrupacionDeVentas.MedioPago, medios, alcance, ct),
            await NombrarAsync(AgrupacionDeVentas.PuntoVenta, puntos, alcance, ct),
            porDia);
    }

    public async Task<AgrupacionConsultada> AgruparAsync(
        SolicitudDeConsultaDeVentas solicitud, AgrupacionDeVentas por, CancellationToken ct = default)
    {
        var alcance = await ResolverAlcanceAsync(solicitud, ct);
        var acumulados = new Dictionary<int, Acumulado>();

        foreach (var contexto in alcance.Contextos)
        {
            var empresa = contexto.Empresa;
            switch (por)
            {
                case AgrupacionDeVentas.Dia:
                    var serie = await reportes.ObtenerResumenAsync(
                        empresa.Id, empresa.IdPuntoVenta, contexto.Desde, contexto.Hasta, Granularidad.Dia, ct);
                    foreach (var bucket in serie.Serie)
                    {
                        Sumar(acumulados, bucket.Inicio.DayNumber, bucket.Neto, bucket.CantidadTx);
                    }

                    break;
                case AgrupacionDeVentas.PuntoVenta when empresa.IdPuntoVenta is { } idPuntoVenta:
                    // Con punto de venta elegido se usa el resumen de ese punto: así el corte de día
                    // sale de la misma zona con la que se calculó el período.
                    var delPunto = await reportes.ObtenerResumenAsync(
                        empresa.Id, idPuntoVenta, contexto.Desde, contexto.Hasta, Granularidad.Dia, ct);
                    Sumar(acumulados, idPuntoVenta, delPunto.NetoVendido, delPunto.CantidadTx);
                    break;
                case AgrupacionDeVentas.PuntoVenta:
                    var porPuntoVenta = await reportes.ObtenerPorPuntoVentaAsync(empresa.Id, contexto.Desde, contexto.Hasta, ct);
                    foreach (var fila in porPuntoVenta.Filas)
                    {
                        Sumar(acumulados, fila.IdPuntoVenta, fila.Neto, fila.CantidadTx);
                    }

                    break;
                case AgrupacionDeVentas.Vendedor:
                    var porVendedor = await reportes.ObtenerPorVendedorAsync(
                        empresa.Id, empresa.IdPuntoVenta, contexto.Desde, contexto.Hasta, ct);
                    foreach (var fila in porVendedor.Filas)
                    {
                        Sumar(acumulados, fila.IdEmpleado, fila.Neto, fila.CantidadTx);
                    }

                    break;
                case AgrupacionDeVentas.MedioPago:
                    foreach (var fila in await ConsultarMediosDePagoAsync(contexto, ct))
                    {
                        Sumar(acumulados, fila.Key, fila.Value.Importe, fila.Value.Cantidad);
                    }

                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(por));
            }
        }

        var filas = await NombrarAsync(por, acumulados, alcance, ct);

        return new AgrupacionConsultada(
            NombreSiEsConNombre(solicitud.Periodo),
            alcance.Contextos.Min(c => c.Desde),
            alcance.Contextos.Max(c => c.Hasta),
            alcance.Contextos.Select(c => c.ZonaId).Distinct().ToList(),
            alcance.Descripcion,
            por,
            filas.Sum(f => f.Importe),
            filas);
    }

    /// <summary>Lo cobrado por medio de pago, NETO del vuelto: <c>pagos_comprobante.importe</c> guarda
    /// lo entregado por el cliente y el vuelto va aparte, así que el efectivo realmente cobrado es
    /// <c>importe - vuelto</c>. Los mismos filtros de comprobante que el reporte de ventas (clase venta,
    /// no anulado, sin espejos fiscales, puntos de venta del alcance, fecha del rango) y el signo del
    /// tipo de comprobante, de modo que una nota de crédito resta. Es un método aparte a propósito:
    /// el reporte de la web suma el importe sin descontar el vuelto y puede cambiar sin tocar esta
    /// regla.</summary>
    private async Task<Dictionary<int, Acumulado>> ConsultarMediosDePagoAsync(ContextoDeEmpresa contexto, CancellationToken ct)
    {
        var empresa = contexto.Empresa;
        var idsPuntoVenta = empresa.IdPuntoVenta is { } idPuntoVenta
            ? [idPuntoVenta]
            : await db.PuntosVenta.Where(pv => pv.IdEmpresa == empresa.Id).Select(pv => pv.Id).ToListAsync(ct);
        var resultado = new Dictionary<int, Acumulado>();
        if (idsPuntoVenta.Count == 0)
        {
            return resultado;
        }

        var agregados = await db.ComprobantesVenta
            .SinEspejosFiscales()
            .Where(cv => idsPuntoVenta.Contains(cv.IdPuntoVenta))
            .Where(cv => cv.Estado != EstadoComprobante.Anulado)
            .Where(cv => cv.Fecha >= contexto.Rango.DesdeUtc && cv.Fecha < contexto.Rango.HastaUtcExclusivo)
            .Join(
                db.TiposComprobante.Where(t => t.Clase == ClaseComprobante.Venta),
                cv => cv.IdTipoComprobante, t => t.Id, (cv, t) => new { cv.Id, t.Signo })
            .Join(
                db.PagosComprobante, cv => cv.Id, p => p.IdComprobanteVenta,
                (cv, p) => new { cv.Signo, p.IdMedioPago, p.Importe, p.Vuelto })
            .GroupBy(x => x.IdMedioPago)
            .Select(g => new { Clave = g.Key, Neto = g.Sum(x => (x.Importe - x.Vuelto) * x.Signo), CantidadPagos = g.Count() })
            .ToListAsync(ct);

        foreach (var agregado in agregados)
        {
            resultado[agregado.Clave] = new Acumulado(agregado.Neto, agregado.CantidadPagos);
        }

        return resultado;
    }

    private async Task<AlcanceResuelto> ResolverAlcanceAsync(SolicitudDeConsultaDeVentas solicitud, CancellationToken ct)
    {
        var empresasDelTenant = await db.Empresas
            .AsNoTracking()
            .OrderBy(e => e.Id)
            .Select(e => new CandidatoEmpresa(e.Id, e.NombreFantasia ?? e.RazonSocial, e.RazonSocial))
            .ToListAsync(ct);
        if (empresasDelTenant.Count == 0)
        {
            throw ErrorDominio.NoEncontrado("El tenant no tiene empresas cargadas.");
        }

        CandidatoEmpresa? empresaElegida = string.IsNullOrWhiteSpace(solicitud.Empresa)
            ? null
            : Buscar(solicitud.Empresa, "la empresa", empresasDelTenant, e => e.Id, e => [e.Nombre, e.RazonSocial], e => e.Nombre);

        var enAlcance = new List<EmpresaEnAlcance>();
        string descripcion;

        if (!string.IsNullOrWhiteSpace(solicitud.PuntoVenta))
        {
            var puntosDelTenant = await db.PuntosVenta
                .AsNoTracking()
                .OrderBy(pv => pv.Id)
                .Select(pv => new CandidatoPuntoVenta(pv.Id, pv.IdEmpresa, pv.Nombre))
                .ToListAsync(ct);
            var texto = solicitud.PuntoVenta.Trim();

            // El id se busca entre todos los puntos del tenant para poder decir que no pertenece a
            // la empresa pedida; el nombre, en cambio, solo entre los de esa empresa.
            var porId = int.TryParse(texto, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                ? puntosDelTenant.FirstOrDefault(pv => pv.Id == id)
                : null;
            var punto = porId ?? Buscar(
                texto, "el punto de venta",
                empresaElegida is null ? puntosDelTenant : puntosDelTenant.Where(pv => pv.IdEmpresa == empresaElegida.Id).ToList(),
                pv => pv.Id, pv => [pv.Nombre], pv => pv.Nombre);

            if (empresaElegida is not null && punto.IdEmpresa != empresaElegida.Id)
            {
                throw new ErrorDominio(
                    "punto_venta_no_pertenece_a_la_empresa",
                    $"El punto de venta '{punto.Nombre}' no pertenece a la empresa '{empresaElegida.Nombre}'.",
                    400);
            }

            var empresaDelPunto = empresasDelTenant.FirstOrDefault(e => e.Id == punto.IdEmpresa)
                ?? throw ErrorDominio.NoEncontrado($"No existe la empresa del punto de venta '{punto.Nombre}'.");
            enAlcance.Add(new EmpresaEnAlcance(empresaDelPunto.Id, empresaDelPunto.Nombre, punto.Id));
            descripcion = $"empresa {empresaDelPunto.Nombre}, punto de venta {punto.Nombre}";
        }
        else if (empresaElegida is not null)
        {
            enAlcance.Add(new EmpresaEnAlcance(empresaElegida.Id, empresaElegida.Nombre, null));
            descripcion = $"empresa {empresaElegida.Nombre}";
        }
        else
        {
            enAlcance.AddRange(empresasDelTenant.Select(e => new EmpresaEnAlcance(e.Id, e.Nombre, null)));
            descripcion = enAlcance.Count == 1
                ? $"empresa {enAlcance[0].Nombre}"
                : $"todas las empresas ({string.Join(", ", enAlcance.Select(e => e.Nombre))})";
        }

        var contextos = new List<ContextoDeEmpresa>();
        foreach (var empresa in enAlcance)
        {
            var resuelto = await parametros.ResolverAsync(
                ParametroConocido.ZonaHoraria.Clave, empresa.Id, empresa.IdPuntoVenta, ct);
            var zonaId = JsonSerializer.Deserialize<string>(resuelto.Valor)!;
            var zona = TimeZoneInfo.FindSystemTimeZoneById(zonaId);

            var hoy = PeriodoDeConsulta.HoyLocal(reloj.Ahora, zona);
            var (desde, hasta) = PeriodoDeConsulta.Resolver(solicitud.Periodo, solicitud.Desde, solicitud.Hasta, hoy);
            var rango = RangoDeReporte.Crear(desde, hasta, Granularidad.Dia, zona);
            contextos.Add(new ContextoDeEmpresa(empresa, zonaId, desde, hasta, rango));
        }

        return new AlcanceResuelto(contextos, descripcion);
    }

    /// <summary>Id numérico primero; si no hay, nombre exacto sin distinguir mayúsculas; si no hay,
    /// el único cuyo nombre lo contiene. Más de uno o ninguno es un error que lista los candidatos.</summary>
    private static T Buscar<T>(
        string texto, string que, IReadOnlyList<T> candidatos, Func<T, int> id, Func<T, string[]> nombres, Func<T, string> etiqueta)
        where T : class
    {
        var buscado = texto.Trim();
        if (int.TryParse(buscado, NumberStyles.None, CultureInfo.InvariantCulture, out var numero) &&
            candidatos.FirstOrDefault(c => id(c) == numero) is { } porId)
        {
            return porId;
        }

        var exactos = candidatos.Where(c => nombres(c).Any(n => string.Equals(n, buscado, StringComparison.OrdinalIgnoreCase))).ToList();
        if (exactos.Count == 1)
        {
            return exactos[0];
        }

        var coincidencias = exactos.Count > 1
            ? exactos
            : candidatos.Where(c => nombres(c).Any(n => n.Contains(buscado, StringComparison.OrdinalIgnoreCase))).ToList();
        if (coincidencias.Count == 1)
        {
            return coincidencias[0];
        }

        var lista = string.Join(", ", (coincidencias.Count > 1 ? coincidencias : candidatos).Take(10).Select(c => $"{etiqueta(c)} (id {id(c)})"));
        throw coincidencias.Count > 1
            ? new ErrorDominio("alcance_ambiguo", $"'{texto}' coincide con más de {que}: {lista}. Indicá el id.", 400)
            : new ErrorDominio("alcance_no_encontrado", $"No encontré {que} '{texto}'. Disponibles: {lista}.", 404);
    }

    private static void Sumar(Dictionary<int, Acumulado> destino, int clave, decimal importe, int cantidad) =>
        destino[clave] = destino.TryGetValue(clave, out var actual)
            ? new Acumulado(actual.Importe + importe, actual.Cantidad + cantidad)
            : new Acumulado(importe, cantidad);

    private async Task<List<FilaDeVentas>> NombrarAsync(
        AgrupacionDeVentas por, Dictionary<int, Acumulado> acumulados, AlcanceResuelto alcance, CancellationToken ct)
    {
        var ids = acumulados.Keys.ToList();
        Func<int, string> nombre;

        switch (por)
        {
            case AgrupacionDeVentas.Dia:
                nombre = clave => DateOnly.FromDayNumber(clave).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                break;
            case AgrupacionDeVentas.MedioPago:
                var medios = await db.MediosPago.AsNoTracking()
                    .Where(m => ids.Contains(m.Id)).Select(m => new { m.Id, m.Nombre }).ToDictionaryAsync(m => m.Id, m => m.Nombre, ct);
                nombre = clave => medios.GetValueOrDefault(clave) ?? $"(medio de pago id {clave})";
                break;
            case AgrupacionDeVentas.PuntoVenta:
                var puntos = await db.PuntosVenta.AsNoTracking()
                    .Where(pv => ids.Contains(pv.Id)).Select(pv => new { pv.Id, pv.Nombre, pv.IdEmpresa }).ToDictionaryAsync(pv => pv.Id, ct);
                var variasEmpresas = alcance.Contextos.Count > 1;
                nombre = clave => puntos.TryGetValue(clave, out var punto)
                    ? variasEmpresas
                        ? $"{punto.Nombre} ({alcance.Contextos.FirstOrDefault(c => c.Empresa.Id == punto.IdEmpresa)?.Empresa.Nombre})"
                        : punto.Nombre
                    : $"(punto de venta id {clave})";
                break;
            case AgrupacionDeVentas.Vendedor:
                var vendedores = await db.Usuarios.AsNoTracking()
                    .Where(u => ids.Contains(u.Id)).Select(u => new { u.Id, u.NombreUsuario }).ToDictionaryAsync(u => u.Id, u => u.NombreUsuario, ct);
                nombre = clave => vendedores.GetValueOrDefault(clave) ?? $"(vendedor id {clave})";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(por));
        }

        var filas = acumulados.Select(a => new { a.Key, Fila = new FilaDeVentas(nombre(a.Key), a.Value.Importe, a.Value.Cantidad) });
        return (por == AgrupacionDeVentas.Dia
                ? filas.OrderBy(f => f.Key)
                : filas.OrderByDescending(f => f.Fila.Importe).ThenBy(f => f.Fila.Grupo, StringComparer.OrdinalIgnoreCase))
            .Select(f => f.Fila)
            .ToList();
    }

    private static string? NombreSiEsConNombre(string? periodo) =>
        string.IsNullOrWhiteSpace(periodo) ? null : periodo.Trim().ToLowerInvariant();

    private readonly record struct Acumulado(decimal Importe, int Cantidad);

    private sealed record CandidatoEmpresa(int Id, string Nombre, string RazonSocial);

    private sealed record CandidatoPuntoVenta(int Id, int IdEmpresa, string Nombre);

    private sealed record EmpresaEnAlcance(int Id, string Nombre, int? IdPuntoVenta);

    private sealed record ContextoDeEmpresa(
        EmpresaEnAlcance Empresa, string ZonaId, DateOnly Desde, DateOnly Hasta, RangoDeReporte Rango);

    private sealed record AlcanceResuelto(IReadOnlyList<ContextoDeEmpresa> Contextos, string Descripcion);
}
