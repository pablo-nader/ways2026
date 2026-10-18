using System.ComponentModel;
using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Ways.Application.Abstracciones;
using Ways.Api.Seguridad;
using Ways.Application.Reportes;
using Ways.Domain.Common;

namespace Ways.Api.ConectorMcp;

/// <summary>
/// Consultas de ventas del conector MCP, de solo lectura. Reusan <see cref="ServicioDeConsultasDeVentas"/>,
/// que a su vez usa los reportes de ventas de la web, y devuelven texto compacto: tablas de
/// <c>a | b | c</c> con tope de filas.
/// </summary>
[McpServerToolType]
public sealed class HerramientasDeVentas
{
    private const int TopeDeFilasPorTabla = 31;
    private const int LimitePorDefecto = 20;
    private const int LimiteMaximo = 50;

    // Coma decimal y punto de miles (es-AR) armados a mano: no dependen de que el host tenga ICU.
    private static readonly NumberFormatInfo FormatoDeNumeros = new()
    {
        NumberDecimalSeparator = ",",
        NumberGroupSeparator = ".",
        NumberGroupSizes = [3]
    };

    [McpServerTool(
        Name = "ventas_resumen",
        Title = "Resumen de ventas",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "Resumen de lo vendido en un período: total vendido, cantidad de comprobantes, ticket promedio, notas de crédito, " +
        "y desglose por medio de pago, por punto de venta y por día. 'Vendido' son los comprobantes de venta emitidos, " +
        "sin los anulados, con las notas de crédito restadas, al importe final con descuentos y recargos. El desglose por " +
        "medio de pago muestra lo cobrado neto del vuelto. El período se indica con un nombre (hoy, ayer, esta_semana, " +
        "semana_pasada, este_mes, mes_pasado), que se resuelve en la zona horaria de cada empresa (semana de lunes a " +
        "domingo; esta_semana y este_mes terminan hoy), o con desde y hasta (yyyy-MM-dd, ambos inclusivos). Sin empresa " +
        "suma todas las empresas del tenant. Solo para Supervisor y Admin.")]
    public static async Task<CallToolResult> VentasResumenAsync(
        IContextoDeUsuario actual,
        IAuthorizationService autorizacion,
        IHttpContextAccessor http,
        ServicioDeConsultasDeVentas consultas,
        [Description("Nombre del período: hoy, ayer, esta_semana, semana_pasada, este_mes o mes_pasado. No se combina con desde/hasta.")]
        string? periodo = null,
        [Description("Primer día del rango, yyyy-MM-dd, inclusivo. Va junto con hasta. Máximo 366 días de rango.")]
        string? desde = null,
        [Description("Último día del rango, yyyy-MM-dd, inclusivo. Va junto con desde.")]
        string? hasta = null,
        [Description("Empresa, por id o por nombre. Si se omite, todas las empresas del tenant.")]
        string? empresa = null,
        [Description("Punto de venta, por id o por nombre. Si se omite, todos los de la empresa.")]
        string? punto_venta = null,
        CancellationToken ct = default) =>
        await EjecutarAsync(actual, autorizacion, http, async () =>
        {
            var resumen = await consultas.ResumirAsync(new SolicitudDeConsultaDeVentas(periodo, desde, hasta, empresa, punto_venta), ct);
            return FormatearResumen(resumen);
        });

    [McpServerTool(
        Name = "ventas_agrupadas",
        Title = "Ventas agrupadas",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "Lo vendido en un período agrupado por punto_venta, medio_pago, vendedor o dia, con importe, cantidad y porcentaje " +
        "del total de cada grupo. 'Vendido' son los comprobantes de venta emitidos, sin los anulados, con las notas de " +
        "crédito restadas, al importe final con descuentos y recargos. La cantidad cuenta comprobantes, salvo en medio_pago, " +
        "donde cuenta pagos y el importe es lo cobrado neto del vuelto. Ordena por importe descendente (dia, por fecha) y " +
        "devuelve hasta 'limite' grupos. El período se indica con un nombre (hoy, ayer, esta_semana, semana_pasada, " +
        "este_mes, mes_pasado), resuelto en la zona horaria de cada empresa, o con desde y hasta (yyyy-MM-dd, inclusivos). " +
        "Solo para Supervisor y Admin.")]
    public static async Task<CallToolResult> VentasAgrupadasAsync(
        IContextoDeUsuario actual,
        IAuthorizationService autorizacion,
        IHttpContextAccessor http,
        ServicioDeConsultasDeVentas consultas,
        [Description("Cómo agrupar: punto_venta, medio_pago, vendedor o dia.")]
        string agrupar_por,
        [Description("Nombre del período: hoy, ayer, esta_semana, semana_pasada, este_mes o mes_pasado. No se combina con desde/hasta.")]
        string? periodo = null,
        [Description("Primer día del rango, yyyy-MM-dd, inclusivo. Va junto con hasta. Máximo 366 días de rango.")]
        string? desde = null,
        [Description("Último día del rango, yyyy-MM-dd, inclusivo. Va junto con desde.")]
        string? hasta = null,
        [Description("Empresa, por id o por nombre. Si se omite, todas las empresas del tenant.")]
        string? empresa = null,
        [Description("Punto de venta, por id o por nombre. Si se omite, todos los de la empresa.")]
        string? punto_venta = null,
        [Description("Cantidad máxima de grupos a devolver, de 1 a 50. Por defecto 20.")]
        int limite = LimitePorDefecto,
        CancellationToken ct = default) =>
        await EjecutarAsync(actual, autorizacion, http, async () =>
        {
            if (limite is < 1 or > LimiteMaximo)
            {
                throw new ErrorDominio("limite_invalido", $"'limite' tiene que estar entre 1 y {LimiteMaximo}; llegó {limite}.", 400);
            }

            var por = ServicioDeConsultasDeVentas.ParsearAgrupacion(agrupar_por);
            var agrupacion = await consultas.AgruparAsync(
                new SolicitudDeConsultaDeVentas(periodo, desde, hasta, empresa, punto_venta), por, ct);
            return FormatearAgrupacion(agrupacion, limite);
        });

    /// <summary>La puerta de rol es la misma política que protege los reportes de ventas de la web
    /// (<see cref="Politicas.LecturaDeReportes"/>), evaluada sobre el usuario de la request. Un rol
    /// rechazado recibe un resultado de texto normal; un error del usuario (período desconocido,
    /// empresa inexistente…) vuelve como resultado con <c>IsError</c> y su mensaje.</summary>
    private static async Task<CallToolResult> EjecutarAsync(
        IContextoDeUsuario actual, IAuthorizationService autorizacion, IHttpContextAccessor http, Func<Task<string>> consulta)
    {
        var usuario = http.HttpContext!.User;
        if (!(await autorizacion.AuthorizeAsync(usuario, Politicas.LecturaDeReportes)).Succeeded)
        {
            return Resultado(
                $"Tu rol ({actual.Rol}) no puede ver reportes de ventas: requiere Supervisor o Admin.", esError: false);
        }

        try
        {
            return Resultado(await consulta(), esError: false);
        }
        catch (ErrorDominio error)
        {
            return Resultado(error.Message, esError: true);
        }
    }

    private static CallToolResult Resultado(string texto, bool esError) =>
        new() { IsError = esError, Content = [new TextContentBlock { Text = texto }] };

    private static string FormatearResumen(ResumenDeVentasConsultado r)
    {
        var lineas = new List<string>
        {
            $"Ventas {Encabezado(r.Periodo, r.Desde, r.Hasta, r.Zonas)}",
            $"Alcance: {r.Alcance}",
            $"Total vendido: {Dinero(r.NetoVendido)}",
            $"Comprobantes: {r.CantidadTx}",
            $"Ticket promedio: {(r.TicketPromedio is { } ticket ? Dinero(ticket) : "sin comprobantes")}"
        };

        if (r.CantidadNcx > 0)
        {
            lineas.Add($"Notas de crédito: {r.CantidadNcx} por {Dinero(Math.Abs(r.NetoNcx))} (ya restadas del total)");
        }

        if (r.Empresas.Count > 1)
        {
            AgregarTabla(
                lineas, "Por empresa", ["empresa", "período", "importe", "comprobantes"],
                r.Empresas.Select(e => new[]
                {
                    e.Nombre, Rango(e.Desde, e.Hasta), Dinero(e.Neto), e.CantidadTx.ToString(CultureInfo.InvariantCulture)
                }).ToList());
        }

        AgregarTabla(
            lineas, "Por medio de pago (lo cobrado neto del vuelto)", ["medio", "importe", "pagos"],
            r.PorMedioPago.Select(f => new[] { f.Grupo, Dinero(f.Importe), Entero(f.Cantidad) }).ToList());

        if (!r.FiltraPuntoVenta)
        {
            AgregarTabla(
                lineas, "Por punto de venta", ["punto de venta", "importe", "comprobantes"],
                r.PorPuntoVenta.Select(f => new[] { f.Grupo, Dinero(f.Importe), Entero(f.Cantidad) }).ToList());
        }

        if (r.PorDia is not null)
        {
            AgregarTabla(
                lineas, "Por día", ["fecha", "importe", "comprobantes"],
                r.PorDia.Select(f => new[] { f.Grupo, Dinero(f.Importe), Entero(f.Cantidad) }).ToList());
        }

        return string.Join(Environment.NewLine, lineas);
    }

    private static string FormatearAgrupacion(AgrupacionConsultada a, int limite)
    {
        var (titulo, columna, cantidad) = a.Por switch
        {
            AgrupacionDeVentas.PuntoVenta => ("punto de venta", "punto de venta", "comprobantes"),
            AgrupacionDeVentas.MedioPago => ("medio de pago", "medio", "pagos"),
            AgrupacionDeVentas.Vendedor => ("vendedor", "vendedor", "comprobantes"),
            AgrupacionDeVentas.Dia => ("día", "fecha", "comprobantes"),
            _ => throw new ArgumentOutOfRangeException(nameof(a))
        };

        var lineas = new List<string>
        {
            $"Ventas por {titulo} {Encabezado(a.Periodo, a.Desde, a.Hasta, a.Zonas)}",
            $"Alcance: {a.Alcance}",
            $"{columna} | importe | {cantidad} | % del total"
        };

        foreach (var fila in a.Filas.Take(limite))
        {
            var porcentaje = a.Total == 0 ? 0m : fila.Importe / a.Total * 100m;
            lineas.Add($"{fila.Grupo} | {Dinero(fila.Importe)} | {Entero(fila.Cantidad)} | {porcentaje.ToString("N1", FormatoDeNumeros)}%");
        }

        if (a.Filas.Count > limite)
        {
            lineas.Add($"… y {a.Filas.Count - limite} más");
        }

        var nota = a.Por == AgrupacionDeVentas.MedioPago ? " (lo cobrado neto del vuelto)" : string.Empty;
        lineas.Add($"Total{nota}: {Dinero(a.Total)} en {a.Filas.Count} grupos");

        return string.Join(Environment.NewLine, lineas);
    }

    private static void AgregarTabla(List<string> lineas, string titulo, string[] encabezado, IReadOnlyList<string[]> filas)
    {
        lineas.Add(string.Empty);
        lineas.Add($"{titulo}:");
        if (filas.Count == 0)
        {
            lineas.Add("sin movimientos");
            return;
        }

        lineas.Add(string.Join(" | ", encabezado));
        lineas.AddRange(filas.Take(TopeDeFilasPorTabla).Select(fila => string.Join(" | ", fila)));
        if (filas.Count > TopeDeFilasPorTabla)
        {
            lineas.Add($"… y {filas.Count - TopeDeFilasPorTabla} más");
        }
    }

    private static string Encabezado(string? periodo, DateOnly desde, DateOnly hasta, IReadOnlyList<string> zonas) =>
        $"{(periodo is null ? string.Empty : periodo + " ")}({Rango(desde, hasta)}), zona horaria: {string.Join(", ", zonas)}";

    private static string Rango(DateOnly desde, DateOnly hasta) =>
        desde == hasta
            ? Fecha(desde)
            : $"{Fecha(desde)} a {Fecha(hasta)}";

    private static string Fecha(DateOnly fecha) => fecha.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Dinero(decimal importe) => importe.ToString("N2", FormatoDeNumeros);

    private static string Entero(int valor) => valor.ToString("N0", FormatoDeNumeros);
}
