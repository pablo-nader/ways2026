using Ways.Domain.Compras;
using Ways.Domain.Ventas;

namespace Ways.Application.Listados;

/// <summary>Etiquetas en español de los estados de los documentos, con las que los listados ordenan por la
/// columna Estado (se ordena por lo que la pantalla muestra, no por el orden de declaración del enum). Espejo
/// de <c>etiquetaDeEstadoCompra</c>, <c>etiquetaDeEstadoPresupuesto</c>, <c>etiquetaDeEstadoRemito</c> y
/// <c>etiquetaDeEstadoOrdenCompra</c> en <c>Ways.Web/src/api</c>; <c>EtiquetasDeEstadoDeDocumentoTests</c>
/// (backend) y <c>etiquetasDeEstadoDeDocumento.test.ts</c> (web) fijan la misma lista literal.</summary>
public static class EtiquetasDeEstadoDeDocumento
{
    public static readonly IReadOnlyDictionary<EstadoCompra, string> Compra =
        new Dictionary<EstadoCompra, string>
        {
            [EstadoCompra.Borrador] = "Borrador",
            [EstadoCompra.Confirmada] = "Confirmada",
            [EstadoCompra.Anulada] = "Anulada",
        };

    public static readonly IReadOnlyDictionary<EstadoPresupuesto, string> Presupuesto =
        new Dictionary<EstadoPresupuesto, string>
        {
            [EstadoPresupuesto.Borrador] = "Borrador",
            [EstadoPresupuesto.Enviado] = "Enviado",
            [EstadoPresupuesto.Convertido] = "Convertido",
            [EstadoPresupuesto.Anulado] = "Anulado",
        };

    public static readonly IReadOnlyDictionary<EstadoRemito, string> Remito =
        new Dictionary<EstadoRemito, string>
        {
            [EstadoRemito.Borrador] = "Borrador",
            [EstadoRemito.Emitido] = "Emitido",
            [EstadoRemito.Facturado] = "Facturado",
            [EstadoRemito.Anulado] = "Anulado",
        };

    public static readonly IReadOnlyDictionary<EstadoOrdenCompra, string> OrdenCompra =
        new Dictionary<EstadoOrdenCompra, string>
        {
            [EstadoOrdenCompra.Borrador] = "Borrador",
            [EstadoOrdenCompra.Enviada] = "Enviada",
            [EstadoOrdenCompra.RecibidaParcial] = "Recibida parcial",
            [EstadoOrdenCompra.Cerrada] = "Cerrada",
            [EstadoOrdenCompra.Anulada] = "Anulada",
        };
}
