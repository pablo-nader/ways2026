using System.ComponentModel;
using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Ways.Api.Seguridad;
using Ways.Application.Reportes;
using Ways.Domain.Common;

namespace Ways.Api.ConectorMcp;

/// <summary>
/// Ficha de un artículo del conector MCP, de solo lectura. Reusa <see cref="ServicioDeFichaDeArticulo"/> y
/// devuelve texto compacto con tablas de <c>a | b | c</c>. La tool está abierta a todos los roles; lo que no puede
/// ver cada uno lo deciden las mismas políticas de la web, sección por sección.
/// </summary>
[McpServerToolType]
public sealed class HerramientasDeFichaDeArticulo
{
    [McpServerTool(
        Name = "articulo_info",
        Title = "Ficha de artículo",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "Ficha de un artículo: qué precio y stock tiene, cuánto se vendió en el último mes y, solo para Supervisor y " +
        "Admin, a qué costo y a quién se le compró por última vez. El artículo se indica por id, código interno, " +
        "código de barras o nombre; se prueba en ese orden y la primera coincidencia exacta (sin distinguir " +
        "mayúsculas) gana; recién si no hay ninguna se busca el texto contenido en el nombre o en el código. Si " +
        "queda más de un candidato devuelve hasta 10 para que se elija uno por id o código. Muestra: la " +
        "clasificación actual del artículo (un dato sin asignar o dado de baja figura como '(sin …)'); el precio " +
        "vigente hoy en cada lista de precios activa (el que rige en este momento, nunca uno vencido ni uno " +
        "programado a futuro; en las listas derivadas es el de su lista base con el porcentaje de la lista, igual " +
        "que en el POS); el stock de cada punto de venta que tiene saldo registrado (el mínimo, solo para Supervisor y Admin); y las ventas " +
        "de los últimos 30 días contando hoy (con el 'hoy' y la zona horaria de cada empresa), en unidades e " +
        "importe, igual que el reporte de ventas por artículo de la web. Las ventas son solo para Supervisor y " +
        "Admin; el Vendedor ve una línea que lo avisa. El costo nominal del artículo y las últimas compras son " +
        "solo para Supervisor y Admin: para el Vendedor no aparece ningún costo ni proveedor de compra. Las " +
        "compras son las confirmadas con una línea de este artículo (no cuentan borradores ni anuladas), de la más " +
        "reciente a la más antigua por momento de recepción; cada fila trae la fecha del comprobante, el " +
        "proveedor, el número del comprobante, la cantidad, el costo unitario cargado, el descuento (un importe de " +
        "la línea, no un porcentaje) y el costo efectivo por unidad de la línea, con el cálculo que usa la confirmación para el costo nominal " +
        "(total de la línea neto del descuento, más el IVA si el comprobante lo discrimina y los costos cargados " +
        "no lo incluyen, dividido por la cantidad; no indica si esa línea actualizó el costo). Con empresa, los precios son los de las listas compartidas y " +
        "las de esa empresa, y el stock, las ventas y las compras son los de esa empresa; sin empresa, todo el tenant.")]
    public static async Task<CallToolResult> ArticuloInfoAsync(
        IAuthorizationService autorizacion,
        IHttpContextAccessor http,
        ServicioDeFichaDeArticulo fichas,
        [Description("El artículo: id, código interno, código de barras o nombre (o parte del nombre).")]
        string articulo,
        [Description("Empresa, por id o por nombre. Si se omite, todas las empresas del tenant.")]
        string? empresa = null,
        [Description("Cuántas compras recientes listar, de 1 a 20. Por defecto 5.")]
        int compras = ServicioDeFichaDeArticulo.ComprasPorDefecto,
        CancellationToken ct = default)
    {
        var usuario = http.HttpContext!.User;
        var verVentas = (await autorizacion.AuthorizeAsync(usuario, Politicas.LecturaDeReportes)).Succeeded;
        var verCostos = (await autorizacion.AuthorizeAsync(usuario, Politicas.LecturaDeCostos)).Succeeded;

        try
        {
            var ficha = await fichas.ObtenerAsync(new SolicitudDeFichaDeArticulo(articulo, empresa, compras, verVentas, verCostos), ct);
            return HerramientasDeVentas.Resultado(Formatear(ficha, verVentas), esError: false);
        }
        catch (ErrorDominio error)
        {
            return HerramientasDeVentas.Resultado(error.Message, esError: true);
        }
    }

    private static string Formatear(FichaDeArticulo f, bool verMinimo)
    {
        var lineas = new List<string>
        {
            $"Ficha de artículo: {f.Nombre} (id {f.Id}, código {f.Codigo})",
            $"Estado: {(f.Activo ? "activo" : "inactivo")} | {(f.EsProducto ? "producto" : "servicio")} | unidad de venta: {f.UnidadDeVenta}",
            $"Clasificación actual: área {f.Area} | grupo {f.Grupo} | categoría {f.Categoria} | marca {f.Marca} | proveedor habitual {f.Proveedor}",
            $"Alcance: {f.Alcance}"
        };

        HerramientasDeVentas.AgregarTabla(
            lineas, "Precios vigentes por lista", ["lista", "precio"],
            f.Precios.Select(p => new[]
            {
                p.EsDerivada ? $"{p.Lista} (derivada)" : p.Lista,
                p.Precio is { } precio ? HerramientasDeVentas.Dinero(precio) : "sin precio"
            }).ToList());

        if (f.EsProducto)
        {
            HerramientasDeVentas.AgregarTabla(
                lineas, "Stock por punto de venta (solo los que tienen saldo registrado)",
                verMinimo ? ["punto de venta", "cantidad", "mínimo"] : ["punto de venta", "cantidad"],
                f.Stock.Select(s => verMinimo
                    ? new[]
                    {
                        s.PuntoVenta,
                        HerramientasDeVentas.Cantidad(s.Cantidad),
                        s.Minimo is { } minimo ? HerramientasDeVentas.Cantidad(minimo) : "-"
                    }
                    : [s.PuntoVenta, HerramientasDeVentas.Cantidad(s.Cantidad)]).ToList());
        }
        else
        {
            lineas.Add(string.Empty);
            lineas.Add("Stock: es un servicio, no maneja stock.");
        }

        lineas.Add(string.Empty);
        lineas.Add(f.Ventas is { } v
            ? $"Ventas de los últimos {ServicioDeFichaDeArticulo.DiasDeVentas} días ({HerramientasDeVentas.Rango(v.Desde, v.Hasta)}, zona horaria: " +
              $"{string.Join(", ", v.Zonas)}): {HerramientasDeVentas.Cantidad(v.Cantidad)} unidades, importe {HerramientasDeVentas.Dinero(v.Importe)}"
            : "Ventas: requiere Supervisor o Admin.");

        if (f.Costos is { } costos)
        {
            AgregarCostos(lineas, costos);
        }
        else
        {
            lineas.Add("Costos y compras: no disponibles para tu rol.");
        }

        return string.Join(Environment.NewLine, lineas);
    }

    private static void AgregarCostos(List<string> lineas, CostosDeFicha costos)
    {
        lineas.Add(string.Empty);
        lineas.Add(
            "Costo nominal (por unidad, con IVA y neto del descuento del proveedor): " +
            (costos.CostoNominal is { } nominal ? HerramientasDeVentas.Dinero(nominal) : "sin costo cargado"));

        if (costos.Compras.Count == 0)
        {
            lineas.Add("Compras: este artículo no tiene compras confirmadas.");
            return;
        }

        var ultima = costos.Compras[0];
        lineas.Add(
            $"Última compra: {HerramientasDeVentas.Fecha(ultima.Fecha)} a {ultima.Proveedor}, comprobante {ultima.Comprobante}, " +
            $"{HerramientasDeVentas.Cantidad(ultima.Cantidad)} unidades a costo unitario {Costo(ultima.CostoUnitario)}" +
            (ultima.CostoEfectivo is { } efectivo ? $" (costo efectivo {HerramientasDeVentas.Dinero(efectivo)})" : string.Empty));

        HerramientasDeVentas.AgregarTabla(
            lineas, $"Últimas {costos.Compras.Count} de {costos.TotalDeCompras} compras confirmadas",
            ["fecha", "proveedor", "comprobante", "cantidad", "costo unitario", "descuento", "costo efectivo"],
            costos.Compras.Select(c => new[]
            {
                HerramientasDeVentas.Fecha(c.Fecha),
                c.Proveedor,
                c.Comprobante,
                HerramientasDeVentas.Cantidad(c.Cantidad),
                Costo(c.CostoUnitario),
                HerramientasDeVentas.Dinero(c.Descuento),
                c.CostoEfectivo is { } e ? HerramientasDeVentas.Dinero(e) : "-"
            }).ToList());
    }

    // El costo unitario de una compra guarda hasta cuatro decimales.
    private static string Costo(decimal valor) => valor.ToString("N4", HerramientasDeVentas.FormatoDeNumeros);
}
