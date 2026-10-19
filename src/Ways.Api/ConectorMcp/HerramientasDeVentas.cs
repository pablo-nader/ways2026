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
        "domingo; esta_semana y este_mes terminan hoy), o con desde y hasta (yyyy-MM-dd, ambos inclusivos; pueden estar " +
        "separados como máximo 366 días). La tabla por día solo aparece cuando el período abarca 31 días distintos o " +
        "menos (contados entre todas las empresas). Sin empresa suma todas las empresas del tenant. Solo para " +
        "Supervisor y Admin.")]
    public static async Task<CallToolResult> VentasResumenAsync(
        IContextoDeUsuario actual,
        IAuthorizationService autorizacion,
        IHttpContextAccessor http,
        ServicioDeConsultasDeVentas consultas,
        [Description("Nombre del período: hoy, ayer, esta_semana, semana_pasada, este_mes o mes_pasado. No se combina con desde/hasta.")]
        string? periodo = null,
        [Description("Primer día del rango, yyyy-MM-dd, inclusivo. Va junto con hasta, que puede estar como máximo 366 días después.")]
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
        "Lo vendido en un período agrupado por punto_venta, medio_pago, vendedor, dia, area, grupo, categoria o marca, con " +
        "importe, cantidad y porcentaje del total de cada grupo. 'Vendido' son los comprobantes de venta emitidos, sin los " +
        "anulados, con las notas de crédito restadas, al importe final con descuentos y recargos. En punto_venta, vendedor " +
        "y dia la cantidad cuenta comprobantes; en medio_pago cuenta pagos y el importe es lo cobrado neto del vuelto. " +
        "area, grupo, categoria y marca suman por línea de comprobante, igual que el reporte de ventas por artículo de la " +
        "web: el importe es la suma del total de las líneas, la cantidad son unidades, solo cuentan las líneas con artículo " +
        "y la clasificación es la ACTUAL del artículo (si hoy cambió de categoría, sus ventas pasadas van a la nueva). " +
        "Un artículo sin esa clasificación, o cuya área, grupo, categoría o marca está dada de baja, o que ya no está " +
        "visible, va a '(sin área)', '(sin grupo)', '(sin categoría)' o '(sin marca)'. Ordena por importe descendente, salvo dia, que va por fecha " +
        "descendente (el día más reciente primero), así que 'limite' descarta los días más antiguos. Devuelve hasta " +
        "'limite' grupos. El período se indica con un nombre (hoy, ayer, esta_semana, semana_pasada, este_mes, " +
        "mes_pasado), resuelto en la zona horaria de cada empresa, o con desde y hasta (yyyy-MM-dd, inclusivos; pueden " +
        "estar separados como máximo 366 días). Solo para Supervisor y Admin.")]
    public static async Task<CallToolResult> VentasAgrupadasAsync(
        IContextoDeUsuario actual,
        IAuthorizationService autorizacion,
        IHttpContextAccessor http,
        ServicioDeConsultasDeVentas consultas,
        [Description("Cómo agrupar: punto_venta, medio_pago, vendedor, dia, area, grupo, categoria o marca.")]
        string agrupar_por,
        [Description("Nombre del período: hoy, ayer, esta_semana, semana_pasada, este_mes o mes_pasado. No se combina con desde/hasta.")]
        string? periodo = null,
        [Description("Primer día del rango, yyyy-MM-dd, inclusivo. Va junto con hasta, que puede estar como máximo 366 días después.")]
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
            ValidarLimite(limite);
            var por = ServicioDeConsultasDeVentas.ParsearAgrupacion(agrupar_por);
            var agrupacion = await consultas.AgruparAsync(
                new SolicitudDeConsultaDeVentas(periodo, desde, hasta, empresa, punto_venta), por, ct);
            return FormatearAgrupacion(agrupacion, limite);
        });

    [McpServerTool(
        Name = "ventas_por_articulo",
        Title = "Ventas por artículo",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "Los artículos más vendidos de un período, con cantidad e importe de cada uno. Es el reporte de ventas por " +
        "artículo de la web: suma por línea de comprobante, solo cuentan las líneas con artículo y las notas de crédito " +
        "restan. El importe es la suma del total de las líneas (con descuentos y recargos de la línea) y la cantidad son " +
        "unidades. Los filtros area, grupo, categoria (incluye sus subcategorías), articulo, marca y proveedor (el " +
        "habitual), por id o por nombre, y texto (contenido en el nombre o en el código interno, sin distinguir " +
        "mayúsculas) acotan los artículos devueltos; todos usan la clasificación ACTUAL del artículo. El porcentaje es " +
        "sobre el total de todos los artículos vendidos en el período y alcance, sin ningún filtro, así que sirve para " +
        "saber qué parte de lo vendido es un rubro o una marca. Ordena por importe (por defecto) o por cantidad y devuelve hasta 'limite' " +
        "artículos. El período se indica con un nombre (hoy, ayer, esta_semana, semana_pasada, este_mes, mes_pasado), " +
        "resuelto en la zona horaria de cada empresa, o con desde y hasta (yyyy-MM-dd, inclusivos; pueden estar separados " +
        "como máximo 366 días). Solo para Supervisor y Admin.")]
    public static async Task<CallToolResult> VentasPorArticuloAsync(
        IContextoDeUsuario actual,
        IAuthorizationService autorizacion,
        IHttpContextAccessor http,
        ServicioDeConsultasDeVentas consultas,
        [Description("Nombre del período: hoy, ayer, esta_semana, semana_pasada, este_mes o mes_pasado. No se combina con desde/hasta.")]
        string? periodo = null,
        [Description("Primer día del rango, yyyy-MM-dd, inclusivo. Va junto con hasta, que puede estar como máximo 366 días después.")]
        string? desde = null,
        [Description("Último día del rango, yyyy-MM-dd, inclusivo. Va junto con desde.")]
        string? hasta = null,
        [Description("Empresa, por id o por nombre. Si se omite, todas las empresas del tenant.")]
        string? empresa = null,
        [Description("Punto de venta, por id o por nombre. Si se omite, todos los de la empresa.")]
        string? punto_venta = null,
        [Description("Texto contenido en el nombre o en el código interno del artículo, sin distinguir mayúsculas.")]
        string? texto = null,
        [Description("Área actual del artículo, por id o por nombre.")]
        string? area = null,
        [Description("Grupo actual del artículo, por id o por nombre.")]
        string? grupo = null,
        [Description("Categoría actual del artículo, por id o por nombre; incluye sus subcategorías.")]
        string? categoria = null,
        [Description("Marca actual del artículo, por id o por nombre.")]
        string? marca = null,
        [Description("Proveedor habitual actual del artículo, por id o por nombre.")]
        string? proveedor = null,
        [Description("Un artículo puntual, por id, código interno o nombre.")]
        string? articulo = null,
        [Description("Orden: importe (por defecto) o cantidad.")]
        string? orden = null,
        [Description("Cantidad máxima de artículos a devolver, de 1 a 50. Por defecto 20.")]
        int limite = LimitePorDefecto,
        CancellationToken ct = default) =>
        await EjecutarAsync(actual, autorizacion, http, async () =>
        {
            ValidarLimite(limite);
            var resultado = await consultas.PorArticuloAsync(
                new SolicitudDeVentasPorArticulo(
                    new SolicitudDeConsultaDeVentas(periodo, desde, hasta, empresa, punto_venta),
                    texto, area, grupo, categoria, marca, proveedor, articulo, ServicioDeConsultasDeVentas.ParsearOrden(orden), limite),
                ct);
            return FormatearPorArticulo(resultado);
        });

    private static void ValidarLimite(int limite)
    {
        if (limite is < 1 or > LimiteMaximo)
        {
            throw new ErrorDominio("limite_invalido", $"'limite' tiene que estar entre 1 y {LimiteMaximo}; llegó {limite}.", 400);
        }
    }

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
            AgrupacionDeVentas.Area => ("área", "área", "unidades"),
            AgrupacionDeVentas.Grupo => ("grupo", "grupo", "unidades"),
            AgrupacionDeVentas.Categoria => ("categoría", "categoría", "unidades"),
            AgrupacionDeVentas.Marca => ("marca", "marca", "unidades"),
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
            lineas.Add($"{fila.Grupo} | {Dinero(fila.Importe)} | {Cantidad(fila.Cantidad)} | {porcentaje.ToString("N1", FormatoDeNumeros)}%");
        }

        if (a.Filas.Count > limite)
        {
            lineas.Add($"… y {a.Filas.Count - limite} más");
        }

        var nota = a.Por switch
        {
            AgrupacionDeVentas.MedioPago => " (lo cobrado neto del vuelto)",
            AgrupacionDeVentas.Area or AgrupacionDeVentas.Grupo or AgrupacionDeVentas.Categoria or AgrupacionDeVentas.Marca =>
                " (suma de las líneas con artículo)",
            _ => string.Empty
        };
        lineas.Add($"Total{nota}: {Dinero(a.Total)} en {a.Filas.Count} grupos");

        return string.Join(Environment.NewLine, lineas);
    }

    private static string FormatearPorArticulo(VentasPorArticuloConsultadas r)
    {
        var lineas = new List<string>
        {
            $"Ventas por artículo {Encabezado(r.Periodo, r.Desde, r.Hasta, r.Zonas)}",
            $"Alcance: {r.Alcance}"
        };

        if (r.Filtros.Count > 0)
        {
            lineas.Add($"Filtros: {string.Join(", ", r.Filtros)}");
        }

        lineas.Add($"Orden: {(r.Orden == OrdenDeArticulos.Cantidad ? "cantidad" : "importe")}");
        lineas.Add("código | artículo | cantidad | importe | % del total");

        foreach (var fila in r.Filas)
        {
            var porcentaje = r.ImporteDeTodosLosArticulos == 0 ? 0m : fila.Importe / r.ImporteDeTodosLosArticulos * 100m;
            lineas.Add(
                $"{fila.Codigo ?? "(sin código)"} | {fila.Articulo} | {Cantidad(fila.Cantidad)} | {Dinero(fila.Importe)} | " +
                $"{porcentaje.ToString("N1", FormatoDeNumeros)}%");
        }

        if (r.CantidadDeArticulos > r.Filas.Count)
        {
            lineas.Add($"… y {r.CantidadDeArticulos - r.Filas.Count} más");
        }

        lineas.Add($"Total de los artículos filtrados: {Dinero(r.ImporteFiltrado)} en {r.CantidadDeArticulos} artículos");
        lineas.Add($"Total de todos los artículos vendidos en el período (base del %): {Dinero(r.ImporteDeTodosLosArticulos)}");

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

    private static string Entero(decimal valor) => valor.ToString("N0", FormatoDeNumeros);

    // Unidades de línea: enteras o con hasta tres decimales (artículos por peso).
    private static string Cantidad(decimal valor) => valor.ToString("#,##0.###", FormatoDeNumeros);
}
