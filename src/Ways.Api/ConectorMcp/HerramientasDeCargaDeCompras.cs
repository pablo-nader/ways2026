using System.ComponentModel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Npgsql;
using Ways.Api.Seguridad;
using Ways.Api.Spa;
using Ways.Application.Abstracciones;
using Ways.Application.Compras;
using Ways.Domain.Common;
using Ways.Domain.Compras;

namespace Ways.Api.ConectorMcp;

/// <summary>Carga una factura de proveedor como borrador de compra con <see cref="OrigenCompra.Asistente"/>, con la
/// identidad y el tenant de la request. El borrador no mueve stock ni cuenta corriente; lo confirma una persona en
/// la web.</summary>
[McpServerToolType]
public sealed class HerramientasDeCargaDeCompras
{
    [McpServerTool(
        Name = "crear_borrador_de_compra",
        Title = "Crear borrador de compra",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false)]
    [Description(
        "Carga una factura de proveedor en Ways como BORRADOR de compra, con origen asistente, y devuelve el link " +
        "para que el usuario la revise y la confirme en la web. El borrador no mueve stock, costos ni la cuenta " +
        "corriente del proveedor hasta esa confirmación. Antes de crear busca una compra no anulada del proveedor con " +
        "el mismo tipo y número: si la hay, no crea nada y la informa. Los ids salen de contexto_de_compras (punto de " +
        "venta, tipo de comprobante, alícuotas de IVA), buscar_proveedor y buscar_articulos; cada línea es un " +
        "artículo de Ways (idArticulo) o un concepto (concepto, por ejemplo un flete). Ways calcula subtotal, IVA " +
        "y total, acepta el IVA impreso de cada alícuota si difiere del calculado en hasta $1 y valida las " +
        "percepciones; si algo no cierra devuelve un error que dice qué corregir. No es idempotente: un remito sin " +
        "número no tiene control de duplicado. Solo para administradores.")]
    public static Task<CallToolResult> CrearBorradorDeCompraAsync(
        IAuthorizationService autorizacion,
        IHttpContextAccessor accesor,
        IWaysDbContext db,
        ServicioDeCompras compras,
        UrlsDelConector urls,
        ILogger<HerramientasDeCargaDeCompras> log,
        [Description("Id del proveedor que emite la factura, el de buscar_proveedor (buscalo por el CUIT impreso).")]
        int idProveedor,
        [Description(
            "Id del tipo de comprobante de compra (de contexto_de_compras) según lo impreso: Factura A, B o C " +
            "(C-FA, C-FB, C-FC); un remito o comprobante no fiscal es C-RM.")]
        int idTipoComprobante,
        [Description(
            "Id del punto de venta de Ways que recibe la compra (de contexto_de_compras). Si la factura no lo deja " +
            "claro, preguntale al usuario.")]
        int idPuntoVenta,
        [Description("Fecha de emisión impresa en la factura, yyyy-MM-dd.")]
        string fechaComprobante,
        [Description("Las líneas de la factura en el orden en que aparecen, hasta 100.")]
        IReadOnlyList<LineaDeBorradorDeCompra> lineas,
        [Description(
            "Solo en una factura (C-FA, C-FB, C-FC) y obligatorio: el punto de venta impreso en el número, la parte " +
            "antes del guion (3 en 00003-00012345).")]
        long? puntoVentaFactura = null,
        [Description(
            "Solo en una factura y obligatorio: el número impreso después del guion (12345 en 00003-00012345).")]
        long? numeroFactura = null,
        [Description("Solo en un remito o comprobante no fiscal (C-RM), opcional: su número como texto, tal como lo imprime.")]
        string? numeroRemito = null,
        [Description(
            "Si el comprobante discrimina IVA. En una factura lo fija la letra (la A sí, la B y la C no): no lo " +
            "mandes. En un remito, true si muestra el IVA aparte.")]
        bool? discriminaIva = null,
        [Description(
            "true si los costos unitarios de las líneas ya incluyen el IVA aunque la factura lo discrimine al pie " +
            "(buscar_proveedor dice si el proveedor factura así). Solo en un comprobante que discrimina IVA.")]
        bool preciosIncluyenIva = false,
        [Description(
            "Opcional, solo si el comprobante discrimina IVA: el IVA que imprime la factura por alícuota. Si difiere " +
            "del calculado en hasta $1 se guarda el impreso; si difiere más, es un error.")]
        IReadOnlyList<IvaImpresoDeLaFactura>? ivaImpreso = null,
        [Description(
            "Opcional: las percepciones que imprime la factura, una por tipo (iibb, iva). Solo en una factura; la de " +
            "IVA, solo si discrimina IVA.")]
        IReadOnlyList<PercepcionDeLaFactura>? percepciones = null,
        [Description(
            "El total impreso en la factura. Conviene mandarlo: no se guarda, pero la respuesta muestra la diferencia " +
            "con el total que calcula Ways.")]
        decimal? totalImpreso = null,
        [Description("Opcional: una nota para el borrador.")]
        string? observaciones = null,
        CancellationToken ct = default) =>
        ResultadoDeHerramienta.EjecutarAsync(async () =>
        {
            await PermisoDeCompras.ExigirAsync(autorizacion, accesor);

            var factura = new FacturaParaBorrador(
                idProveedor, idTipoComprobante, idPuntoVenta, fechaComprobante, lineas, puntoVentaFactura, numeroFactura,
                numeroRemito, discriminaIva, preciosIncluyenIva, ivaImpreso, percepciones, observaciones, totalImpreso);
            ArmadoDelBorradorDeCompra.ValidarForma(factura);

            var tipo = await db.TiposComprobante
                .AsNoTracking()
                .Where(t => t.Id == idTipoComprobante)
                .Select(t => new { t.Nombre, t.RegistraLibroIva })
                .FirstOrDefaultAsync(ct)
                ?? throw new ErrorDeHerramienta(
                    $"No existe el tipo de comprobante {idTipoComprobante}: usá uno de los tipos de compra de contexto_de_compras.");

            var idsArticulo = ArmadoDelBorradorDeCompra.IdsDeArticulo(factura);
            var nombreDeArticulo = await db.Articulos
                .AsNoTracking()
                .Where(a => idsArticulo.Contains(a.Id))
                .Select(a => new { a.Id, a.Nombre })
                .ToDictionaryAsync(a => a.Id, a => a.Nombre, ct);

            var solicitud = ArmadoDelBorradorDeCompra.Armar(factura, tipo.RegistraLibroIva, nombreDeArticulo);
            string Link(int id) => $"{urls.Base(accesor.HttpContext!)}{RutasDeLaSpa.PrefijoAdmin}/compras/{id}";

            CompraDetalle compra;
            try
            {
                var duplicadas = await compras.BuscarDuplicadosAsync(
                    idProveedor, idTipoComprobante, solicitud.NumeroExterno, excluirIdCompra: null, ct);
                if (duplicadas.Count > 0)
                {
                    return TextosDelBorradorDeCompra.DescribirDuplicadas(duplicadas, Link);
                }

                compra = await compras.CrearBorradorAsync(solicitud, OrigenCompra.Asistente, ct);
            }
            catch (ErrorDominio error)
            {
                throw new ErrorDeHerramienta(TextosDelBorradorDeCompra.Traducir(error));
            }
            catch (DbUpdateException error) when (error.InnerException is PostgresException pg
                && ManejadorDeErrores.ClasificarPostgresException(pg, log) is { } clasificado)
            {
                // Misma clasificación que la API: el 23505 del número es la carrera de dos cargas simultáneas, que
                // pasan las dos el chequeo de duplicado previo a la transacción.
                throw new ErrorDeHerramienta(TextosDelBorradorDeCompra.TraducirFalloDeBase(clasificado.Codigo, clasificado.Titulo));
            }
            catch (Exception error) when (FallosTransitorios.EsTransitorioEnLaCadena(error))
            {
                // Sin reintento automático: el alta no tiene clave de idempotencia y el commit pudo haber llegado.
                log.LogError(error, "Conector MCP: no se pudo confirmar si quedó creado el borrador de compra.");
                throw new ErrorDeHerramienta(TextosDelBorradorDeCompra.ResultadoIncierto);
            }

            return TextosDelBorradorDeCompra.DescribirCreado(compra, tipo.Nombre, Link(compra.Id), factura.TotalImpreso);
        });
}
