using System.Text.Json;
using Ways.Domain.Clientes;
using Ways.Domain.Fiscal;
using Ways.Domain.Organizacion;

namespace Ways.Application.Fiscal;

/// <summary>
/// Lo que la respuesta de una venta dice de su facturación. <see cref="Estado"/> es uno de
/// <see cref="EstadosFiscalesDeVenta"/>; <see cref="Motivo"/> es la etiqueta de <c>motivo_facturacion</c> con la que
/// se decidió facturar (solo con una fila de decisión) y <see cref="IdFacturacion"/> el id de esa fila. La emisión del
/// espejo agrega, al final y sin cambiar los campos que ya existían, <see cref="Error"/> y <see cref="Comprobante"/> (la
/// factura, solo cuando está aprobada). En la respuesta de la venta <see cref="Error"/> es solo un código estable; los
/// endpoints de procesamiento por lote y de una decisión devuelven el texto guardado completo (el último motivo por el
/// que la decisión sigue pendiente, o los errores de ARCA si la rechazó).
/// </summary>
public sealed record EstadoFiscalDeVenta(
    string Estado,
    string? Motivo = null,
    int? IdFacturacion = null,
    string? Error = null,
    DatosDeFacturaFiscal? Comprobante = null)
{
    public static EstadoFiscalDeVenta NoCorresponde { get; } = new(EstadosFiscalesDeVenta.NoCorresponde);

    public static EstadoFiscalDeVenta SinDecidir { get; } = new(EstadosFiscalesDeVenta.SinDecidir);
}

public static class EstadosFiscalesDeVenta
{
    /// <summary>Al ticket no le corresponde factura: no se decidió facturarlo, no es una venta, la empresa no puede
    /// facturar todavía o la venta no tiene qué cobrar.</summary>
    public const string NoCorresponde = "no_corresponde";

    /// <summary>Se decidió facturar el ticket y la factura todavía no se emitió.</summary>
    public const string Pendiente = "pendiente";

    /// <summary>La decisión de facturar o no todavía está debida: falló al ingerir la venta y se completa al volver a
    /// llamar (reenvío de la venta o procesador de pendientes). La venta ya está registrada.</summary>
    public const string SinDecidir = "sin_decidir";

    public const string Aprobado = "aprobado";

    public const string Rechazado = "rechazado";

    public const string Cancelado = "cancelado";

    /// <summary>El estado de una venta que no tiene fila de decisión: sin la marca <c>facturacion_decidida_en</c> y sin ser un
    /// espejo, la decisión está debida (<see cref="SinDecidir"/>); en cualquier otro caso (decidida sin facturar, o un espejo,
    /// que nunca se decide) no le corresponde factura.</summary>
    public static string SinFila(int? idVentaOrigen, DateTimeOffset? facturacionDecididaEn) =>
        idVentaOrigen is null && facturacionDecididaEn is null ? SinDecidir : NoCorresponde;

    public static string De(EstadoFacturacionDeVenta estado) => estado switch
    {
        EstadoFacturacionDeVenta.Pendiente => Pendiente,
        EstadoFacturacionDeVenta.Emitida => Aprobado,
        EstadoFacturacionDeVenta.Rechazada => Rechazado,
        EstadoFacturacionDeVenta.Cancelada => Cancelado,
        _ => throw new ArgumentOutOfRangeException(nameof(estado), estado, null)
    };
}

/// <summary>La factura fiscal aprobada de una venta: lo que el POS necesita para imprimirla. <see cref="Fecha"/> es el día
/// de emisión (la fecha del comprobante en ARCA), no el de la venta. <see cref="Emisor"/>, <see cref="Receptor"/> y
/// <see cref="Totales"/> llevan los datos legales de la factura impresa y se agregan al final, sin cambiar los campos
/// que ya existían.</summary>
public sealed record DatosDeFacturaFiscal(
    int IdComprobante,
    string CodigoTipoComprobante,
    char Letra,
    int PuntoDeVentaFiscal,
    long Numero,
    DateOnly Fecha,
    string? Cae,
    DateOnly? CaeVencimiento,
    string? PayloadQr,
    IReadOnlyList<ObservacionArca> Observaciones,
    EmisorDeFacturaFiscal Emisor,
    ReceptorDeFacturaFiscal Receptor,
    TotalesDeFacturaFiscal Totales);

/// <summary>Quién emite la factura, tal como estaba al emitirla (congelado en el espejo; un espejo anterior a esa columna lo
/// toma de las filas actuales). <see cref="Domicilio"/> es el del punto de venta; <see cref="CondicionFiscal"/> el nombre
/// de la condición de la empresa (<c>null</c> si no tiene).</summary>
public sealed record EmisorDeFacturaFiscal(
    string RazonSocial,
    string? NombreFantasia,
    string? Cuit,
    string? CondicionFiscal,
    string? IngresosBrutos,
    DateOnly? InicioActividades,
    string? Domicilio)
{
    public static EmisorDeFacturaFiscal De(Empresa empresa, PuntoVenta puntoVenta, string? condicionFiscal) => new(
        empresa.RazonSocial, empresa.NombreFantasia, empresa.Cuit, condicionFiscal, empresa.IngresosBrutos,
        empresa.InicioActividades, puntoVenta.Domicilio);
}

/// <summary>Emisor y receptor congelados en el espejo fiscal en la transacción que lo inserta (columna
/// <c>datos_legales_fiscales</c>): la factura impresa y el QR salen de acá y no de las filas actuales de empresa y cliente.</summary>
public sealed record DatosLegalesDeFacturaFiscal(EmisorDeFacturaFiscal Emisor, ReceptorDeFacturaFiscal Receptor)
{
    private static readonly JsonSerializerOptions Opciones = new(JsonSerializerDefaults.Web);

    public string Serializar() => JsonSerializer.Serialize(this, Opciones);

    /// <summary><c>null</c> cuando el espejo es anterior a la columna (no hay snapshot).</summary>
    public static DatosLegalesDeFacturaFiscal? Leer(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<DatosLegalesDeFacturaFiscal>(json, Opciones)
                ?? throw new InvalidOperationException("Los datos legales fiscales del espejo no se pudieron leer.");
}

/// <summary>A quién se factura. <see cref="TipoDocumento"/> es la etiqueta del documento que viajó a ARCA
/// (<see cref="CodigoTipoDocumento"/>: 80 CUIT, 86 CUIL, 96 DNI, 94 Pasaporte, 99 sin identificar) y
/// <see cref="NumeroDocumento"/> el número enviado (0 con 99). Todos los campos son los de la emisión, congelados en el
/// espejo; solo un espejo anterior a esa columna los toma de la fila actual del cliente.</summary>
public sealed record ReceptorDeFacturaFiscal(
    string Nombre,
    string TipoDocumento,
    short CodigoTipoDocumento,
    long NumeroDocumento,
    string? CondicionFiscal,
    string? Domicilio)
{
    public static ReceptorDeFacturaFiscal De(Cliente cliente, short tipoDoc, long nroDoc, string? condicionFiscal) => new(
        NombreAImprimir(cliente), EtiquetaDeTipoDocumento(tipoDoc), tipoDoc, nroDoc, condicionFiscal, cliente.Domicilio);

    /// <summary>La razón social si hay; si no, nombre y apellido.</summary>
    public static string NombreAImprimir(Cliente cliente) =>
        !string.IsNullOrWhiteSpace(cliente.RazonSocial)
            ? cliente.RazonSocial.Trim()
            : string.Join(
                ' ', new[] { cliente.Nombre, cliente.Apellido }.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));

    public static string EtiquetaDeTipoDocumento(short tipoDoc) => tipoDoc switch
    {
        80 => "CUIT",
        86 => "CUIL",
        96 => "DNI",
        94 => "Pasaporte",
        _ => "Sin identificar"
    };
}

/// <summary>Los importes que ARCA recibió, recompuestos del snapshot de las líneas del comprobante con la misma composición
/// que la emisión. Con ImpTrib en cero, ImpTotal es ImpNeto + ImpIVA + ImpOpEx + ImpTotConc.</summary>
public sealed record TotalesDeFacturaFiscal(
    decimal ImpNeto,
    decimal ImpIva,
    decimal ImpOpEx,
    decimal ImpTotConc,
    decimal ImpTrib,
    decimal ImpTotal,
    IReadOnlyList<AlicuotaDeFacturaFiscal> Alicuotas)
{
    public static TotalesDeFacturaFiscal De(IReadOnlyList<LineaFiscal> lineas)
    {
        var totales = ComposicionDeTotalesFiscales.Componer(lineas);
        var porcentajes = ComposicionDeTotalesFiscales.PorcentajesDeIva(lineas);
        if (porcentajes.Count != totales.Iva.Count)
        {
            throw new InvalidOperationException("Los porcentajes de IVA no coinciden con las alícuotas compuestas.");
        }

        return new TotalesDeFacturaFiscal(
            totales.ImpNeto, totales.ImpIVA, totales.ImpOpEx, totales.ImpTotConc, totales.ImpTrib, totales.ImpTotal,
            [.. totales.Iva.Select((item, i) => new AlicuotaDeFacturaFiscal(porcentajes[i], item.BaseImp, item.Importe))]);
    }
}

/// <summary>Una alícuota gravada de la factura: porcentaje, neto (base imponible) e IVA.</summary>
public sealed record AlicuotaDeFacturaFiscal(decimal Porcentaje, decimal Neto, decimal Iva);
