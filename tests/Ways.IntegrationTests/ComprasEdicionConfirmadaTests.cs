using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Compras;
using Ways.Application.Organizacion;
using Ways.Application.Pagos;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Compras;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Organizacion;
using Ways.Domain.Pagos;
using Ways.Domain.Proveedores;
using Ways.Domain.Stock;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// E6-S2: editar una compra confirmada (<c>PUT /api/compras/{id}</c>). El stock y la cuenta del proveedor se corrigen por
/// diferencia, con movimientos nuevos; la anulación posterior revierte lo NETO. Cada test siembra una deuda previa del
/// proveedor (otra compra) para que <c>saldo_resultante</c> no coincida por aritmética con el importe de la diferencia
/// (<c>mutation-proof-tests</c> regla 11).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ComprasEdicionConfirmadaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, HttpClient Admin, string MailAdmin, string PasswordAdmin,
        int IdProveedor, int IdProveedor2, int IdArticulo, int IdArticulo2, int IdArticuloConLote, int IdAlicuotaIva21,
        int IdTipoCFA, int IdEfectivo);

    private async Task<Contexto> PrepararAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var respuesta = await root.PostAsJsonAsync(
            "/api/plataforma/tenants",
            new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin, ModoPuntoVenta.Web));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Edicion-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        var condicionFiscal = new CondicionFiscal { Codigo = $"{nombre}-CF", Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.CondicionesFiscales.Add(condicionFiscal);
        await db.SaveChangesAsync();

        var idAlicuotaIva21 = await db.AlicuotasIva.Where(a => a.Nombre == "21%").Select(a => a.Id).FirstAsync();

        var proveedor = new Proveedor
        {
            IdTenant = resultado.IdTenant, RazonSocial = nombre, IdCondicionFiscal = condicionFiscal.Id,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        var proveedor2 = new Proveedor
        {
            IdTenant = resultado.IdTenant, RazonSocial = $"{nombre}-2", IdCondicionFiscal = condicionFiscal.Id,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Proveedores.AddRange(proveedor, proveedor2);
        await db.SaveChangesAsync();

        Articulo NuevoArticulo(string sufijo, bool controlaLote) => new()
        {
            IdTenant = resultado.IdTenant, CodigoInterno = $"{nombre}-{sufijo}-{Guid.NewGuid():N}", Nombre = $"Articulo {sufijo}",
            IdArea = area.Id, IdAlicuotaIva = idAlicuotaIva21, UnidadVenta = UnidadVenta.Unidad, EsProducto = true,
            ControlaLote = controlaLote, CreatedAt = ahora, UpdatedAt = ahora
        };

        var articulo1 = NuevoArticulo("1", false);
        var articulo2 = NuevoArticulo("2", false);
        var articuloConLote = NuevoArticulo("lote", true);
        db.Articulos.AddRange(articulo1, articulo2, articuloConLote);

        db.Parametros.Add(new Parametro
        {
            IdTenant = resultado.IdTenant, IdEmpresa = resultado.IdEmpresa, IdPuntoVenta = null,
            Clave = "lotes_habilitado", Valor = "true", CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        var idTipoCFA = await db.TiposComprobante.Where(t => t.Codigo == "C-FA").Select(t => t.Id).SingleAsync();
        var idEfectivo = await db.MediosPago
            .Where(m => m.IdTenant == resultado.IdTenant && m.Comportamiento == ComportamientoMedioPago.Efectivo)
            .Select(m => m.Id).FirstAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, admin, mailAdmin, resultado.PasswordTemporal,
            proveedor.Id, proveedor2.Id, articulo1.Id, articulo2.Id, articuloConLote.Id, idAlicuotaIva21, idTipoCFA, idEfectivo);
    }

    private WaysDbContext Db(Contexto ctx) =>
        fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));

    private static SolicitudDeCompra Solicitud(
        Contexto ctx, int idArticulo, decimal unidades, string? numero = null, int? idPuntoVenta = null,
        int? idProveedor = null, int? idOrden = null, string? lote = null, DateOnly? vencimiento = null) =>
        new(
            idProveedor ?? ctx.IdProveedor, ctx.IdTipoCFA, idPuntoVenta ?? ctx.IdPuntoVenta,
            numero ?? DatosDePrueba.NumeroExternoUnico(), DateOnly.FromDateTime(DateTime.UtcNow), null,
            [
                new LineaDeCompraSolicitada(
                    idArticulo, "Item de prueba", unidades, null, null, 100m, 0m, ctx.IdAlicuotaIva21, true,
                    CodigoLote: lote, FechaVencimiento: vencimiento)
            ],
            IdOrdenCompra: idOrden);

    private static async Task<CompraDetalle> CrearYConfirmarAsync(Contexto ctx, SolicitudDeCompra solicitud)
    {
        var alta = await ctx.Admin.PostAsJsonAsync("/api/compras", solicitud);
        var cuerpoAlta = await alta.Content.ReadAsStringAsync();
        Assert.True(alta.StatusCode == HttpStatusCode.Created, cuerpoAlta);
        var creada = JsonSerializer.Deserialize<CompraDetalle>(cuerpoAlta, OpcionesJson)!;

        var confirmar = await ctx.Admin.PostAsync($"/api/compras/{creada.Id}/confirmar", null);
        var cuerpo = await confirmar.Content.ReadAsStringAsync();
        Assert.True(confirmar.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<CompraDetalle>(cuerpo, OpcionesJson)!;
    }

    private static Task<HttpResponseMessage> EditarAsync(Contexto ctx, int id, SolicitudDeCompra solicitud) =>
        ctx.Admin.PutAsJsonAsync($"/api/compras/{id}", solicitud);

    private static async Task<CompraDetalle> EditarConExitoAsync(Contexto ctx, int id, SolicitudDeCompra solicitud)
    {
        var respuesta = await EditarAsync(ctx, id, solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<CompraDetalle>(cuerpo, OpcionesJson)!;
    }

    private static async Task<string> CodigoAsync(HttpResponseMessage respuesta, HttpStatusCode esperado)
    {
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == esperado, cuerpo);
        return JsonDocument.Parse(cuerpo).RootElement.GetProperty("codigo").GetString()!;
    }

    private async Task<decimal> StockAsync(Contexto ctx, int idArticulo, int? idPuntoVenta = null)
    {
        await using var db = Db(ctx);
        var pv = idPuntoVenta ?? ctx.IdPuntoVenta;
        return await db.Stock.Where(s => s.IdArticulo == idArticulo && s.IdPuntoVenta == pv)
            .Select(s => (decimal?)s.Cantidad).FirstOrDefaultAsync() ?? 0m;
    }

    private async Task<decimal> SaldoAsync(Contexto ctx, int idProveedor)
    {
        await using var db = Db(ctx);
        return await db.Proveedores.Where(p => p.Id == idProveedor).Select(p => p.Saldo).SingleAsync();
    }

    private async Task<List<MovimientoStock>> MovimientosAsync(Contexto ctx, int idCompra, MotivoStock? motivo = null)
    {
        await using var db = Db(ctx);
        return await db.MovimientosStock
            .Where(m => m.IdComprobanteCompra == idCompra && (motivo == null || m.Motivo == motivo))
            .OrderBy(m => m.Id)
            .ToListAsync();
    }

    private async Task<List<MovimientoCuentaCorrienteProveedor>> AsientosAsync(
        Contexto ctx, int idCompra, TipoMovimientoCcProveedor? tipo = null)
    {
        await using var db = Db(ctx);
        return await db.MovimientosCuentaCorrienteProveedor
            .Where(m => m.IdComprobanteCompra == idCompra && (tipo == null || m.Tipo == tipo))
            .OrderBy(m => m.Id)
            .ToListAsync();
    }

    /// <summary>Una compra previa de OTRO artículo deja deuda real en el proveedor.</summary>
    private static async Task<decimal> SembrarDeudaPreviaAsync(Contexto ctx)
    {
        var previa = await CrearYConfirmarAsync(ctx, Solicitud(ctx, ctx.IdArticulo2, 7m));
        return previa.Total;
    }

    // ---- stock y cuenta por diferencia -------------------------------------------------------------------------

    [Fact]
    public async Task SubirLaCantidadSumaLaDiferenciaAlStockYALaCuentaDelProveedor()
    {
        var ctx = await PrepararAsync(nameof(SubirLaCantidadSumaLaDiferenciaAlStockYALaCuentaDelProveedor));
        var deudaPrevia = await SembrarDeudaPreviaAsync(ctx);
        var creada = await CrearYConfirmarAsync(ctx, Solicitud(ctx, ctx.IdArticulo, 10m));

        var editada = await EditarConExitoAsync(ctx, creada.Id, Solicitud(ctx, ctx.IdArticulo, 14m, creada.NumeroExterno));

        Assert.Equal(14m * 121m, editada.Total);
        Assert.Equal(EstadoCompra.Confirmada, editada.Estado);
        Assert.Equal(creada.FechaRecepcion, editada.FechaRecepcion);
        Assert.Equal(14m, await StockAsync(ctx, ctx.IdArticulo));

        var movimientos = await MovimientosAsync(ctx, creada.Id);
        Assert.Equal([10m, 4m], movimientos.Select(m => m.Cantidad));
        Assert.All(movimientos, m => Assert.Equal(MotivoStock.Compra, m.Motivo));
        Assert.Null(movimientos[0].Observaciones);
        Assert.Equal($"Edición de la compra #{creada.Id}", movimientos[1].Observaciones);

        var asientos = await AsientosAsync(ctx, creada.Id, TipoMovimientoCcProveedor.Compra);
        Assert.Equal([10m * 121m, 4m * 121m], asientos.Select(a => a.Importe));
        Assert.Equal(deudaPrevia + 14m * 121m, asientos[1].SaldoResultante);
        Assert.Contains("Edición de la compra", asientos[1].Detalle);
        Assert.Equal(deudaPrevia + 14m * 121m, await SaldoAsync(ctx, ctx.IdProveedor));
    }

    [Fact]
    public async Task BajarLaCantidadRestaLaDiferenciaDelStockYDeLaCuentaDelProveedor()
    {
        var ctx = await PrepararAsync(nameof(BajarLaCantidadRestaLaDiferenciaDelStockYDeLaCuentaDelProveedor));
        var deudaPrevia = await SembrarDeudaPreviaAsync(ctx);
        var creada = await CrearYConfirmarAsync(ctx, Solicitud(ctx, ctx.IdArticulo, 10m));

        var editada = await EditarConExitoAsync(ctx, creada.Id, Solicitud(ctx, ctx.IdArticulo, 6m, creada.NumeroExterno));

        Assert.Equal(6m * 121m, editada.Total);
        Assert.Equal(6m, await StockAsync(ctx, ctx.IdArticulo));
        Assert.Equal([10m, -4m], (await MovimientosAsync(ctx, creada.Id)).Select(m => m.Cantidad));

        var asientos = await AsientosAsync(ctx, creada.Id, TipoMovimientoCcProveedor.Compra);
        Assert.Equal([10m * 121m, -4m * 121m], asientos.Select(a => a.Importe));
        Assert.Equal(deudaPrevia + 6m * 121m, asientos[1].SaldoResultante);
        Assert.Equal(deudaPrevia + 6m * 121m, await SaldoAsync(ctx, ctx.IdProveedor));
    }

    /// <summary>Clausula bajo prueba: omitir la diferencia cero. Editar solo las observaciones no mueve stock ni cuenta.</summary>
    [Fact]
    public async Task UnaEdicionQueNoCambiaCantidadesNiTotalNoAsientaNada()
    {
        var ctx = await PrepararAsync(nameof(UnaEdicionQueNoCambiaCantidadesNiTotalNoAsientaNada));
        var creada = await CrearYConfirmarAsync(ctx, Solicitud(ctx, ctx.IdArticulo, 10m));

        var editada = await EditarConExitoAsync(
            ctx, creada.Id, Solicitud(ctx, ctx.IdArticulo, 10m, creada.NumeroExterno) with { Observaciones = "Solo una nota" });

        Assert.Equal("Solo una nota", editada.Observaciones);
        Assert.Single(await MovimientosAsync(ctx, creada.Id));
        Assert.Single(await AsientosAsync(ctx, creada.Id));
        Assert.Equal(10m, await StockAsync(ctx, ctx.IdArticulo));
    }

    [Fact]
    public async Task QuitarUnaLineaYAgregarOtraDeOtroArticuloMuevenLosDosStocks()
    {
        var ctx = await PrepararAsync(nameof(QuitarUnaLineaYAgregarOtraDeOtroArticuloMuevenLosDosStocks));
        var creada = await CrearYConfirmarAsync(ctx, Solicitud(ctx, ctx.IdArticulo, 10m));

        await EditarConExitoAsync(ctx, creada.Id, Solicitud(ctx, ctx.IdArticulo2, 3m, creada.NumeroExterno));

        Assert.Equal(0m, await StockAsync(ctx, ctx.IdArticulo));
        Assert.Equal(3m, await StockAsync(ctx, ctx.IdArticulo2));
        var movimientos = await MovimientosAsync(ctx, creada.Id);
        Assert.Equal(
            [(ctx.IdArticulo, 10m), (ctx.IdArticulo, -10m), (ctx.IdArticulo2, 3m)],
            movimientos.Select(m => (m.IdArticulo, m.Cantidad)));
    }

    // ---- stock negativo ----------------------------------------------------------------------------------------

    [Fact]
    public async Task BajarUnaCompraCuyaMercaderiaYaSeVendioEsUn409SinEfectoAlguno()
    {
        var ctx = await PrepararAsync(nameof(BajarUnaCompraCuyaMercaderiaYaSeVendioEsUn409SinEfectoAlguno));
        var deudaPrevia = await SembrarDeudaPreviaAsync(ctx);
        var creada = await CrearYConfirmarAsync(ctx, Solicitud(ctx, ctx.IdArticulo, 10m));
        await VenderAsync(ctx, ctx.IdArticulo, 8m, idLote: null);

        var respuesta = await EditarAsync(ctx, creada.Id, Solicitud(ctx, ctx.IdArticulo, 5m, creada.NumeroExterno));

        Assert.Equal("compra_edicion_stock_negativo", await CodigoAsync(respuesta, HttpStatusCode.Conflict));
        Assert.Equal(2m, await StockAsync(ctx, ctx.IdArticulo));
        Assert.Single(await MovimientosAsync(ctx, creada.Id));
        Assert.Single(await AsientosAsync(ctx, creada.Id));
        Assert.Equal(deudaPrevia + creada.Total, await SaldoAsync(ctx, ctx.IdProveedor));

        await using var db = Db(ctx);
        Assert.Equal(creada.Total, (await db.ComprobantesCompra.SingleAsync(c => c.Id == creada.Id)).Total);
        Assert.Equal(0, await db.Auditoria.CountAsync(a => a.Accion == "compra.edicion" && a.IdEntidad == creada.Id));
    }

    /// <summary>El agregado del artículo alcanza (otra compra de otro lote lo sostiene) y es el LOTE el que quedaría
    /// negativo: el rechazo nombra el lote.</summary>
    [Fact]
    public async Task UnLoteConsumidoPorUnaVentaImpideBajarSuCompraAunqueElAgregadoAlcance()
    {
        var ctx = await PrepararAsync(nameof(UnLoteConsumidoPorUnaVentaImpideBajarSuCompraAunqueElAgregadoAlcance));
        var vencimiento = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(365));
        var compraL1 = await CrearYConfirmarAsync(
            ctx, Solicitud(ctx, ctx.IdArticuloConLote, 10m, lote: "L-UNO", vencimiento: vencimiento));
        await CrearYConfirmarAsync(
            ctx, Solicitud(ctx, ctx.IdArticuloConLote, 10m, lote: "L-DOS", vencimiento: vencimiento.AddDays(30)));
        var idLote1 = (await MovimientosAsync(ctx, compraL1.Id)).Single().IdLote!.Value;
        await VenderAsync(ctx, ctx.IdArticuloConLote, 8m, idLote1);

        var respuesta = await EditarAsync(
            ctx, compraL1.Id,
            Solicitud(ctx, ctx.IdArticuloConLote, 5m, compraL1.NumeroExterno, lote: "L-UNO", vencimiento: vencimiento));

        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Conflict, cuerpo);
        var problema = JsonDocument.Parse(cuerpo).RootElement;
        Assert.Equal("compra_edicion_stock_negativo", problema.GetProperty("codigo").GetString());
        Assert.Contains($"lote {idLote1}", problema.GetProperty("mensaje").GetString());
        Assert.Contains($"artículo {ctx.IdArticuloConLote}", problema.GetProperty("mensaje").GetString());

        await using var db = Db(ctx);
        Assert.Equal(2m, (await db.StockLotes.SingleAsync(s => s.IdLote == idLote1)).Cantidad);
        Assert.Equal(12m, await StockAsync(ctx, ctx.IdArticuloConLote));
        Assert.Single(await MovimientosAsync(ctx, compraL1.Id));
    }

    /// <summary>La línea que conserva artículo, código y vencimiento conserva su lote y se edita aunque su vencimiento
    /// ya haya pasado desde la recepción (no se vuelve a verificar), mientras que una línea nueva con un vencimiento ya
    /// pasado se rechaza.</summary>
    [Fact]
    public async Task LaLineaQueConservaSuLoteNoRevalidaElVencimientoYUnaLineaNuevaVencidaSiSeRechaza()
    {
        var ctx = await PrepararAsync(nameof(LaLineaQueConservaSuLoteNoRevalidaElVencimientoYUnaLineaNuevaVencidaSiSeRechaza));
        var vencimiento = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10));
        var creada = await CrearYConfirmarAsync(
            ctx, Solicitud(ctx, ctx.IdArticuloConLote, 10m, lote: "L-VIEJO", vencimiento: vencimiento));
        var idLote = (await MovimientosAsync(ctx, creada.Id)).Single().IdLote!.Value;

        // El vencimiento pasa hoy a ser anterior a "hoy": se simula adelantando la fecha del lote Y de la línea guardada.
        var ayer = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
        await using (var owner = new NpgsqlConnection(fixture.OwnerConnectionString))
        {
            await owner.OpenAsync();
            await EjecutarComoDuenoAsync(owner, "UPDATE lotes SET fecha_vencimiento = $1 WHERE id_lote = $2", ayer, idLote);
            await EjecutarComoDuenoAsync(
                owner, "UPDATE items_comprobante_compra SET fecha_vencimiento = $1 WHERE id_comprobante_compra = $2", ayer, creada.Id);
        }

        // Subir la cantidad de un lote vencido mete unidades vencidas al stock: se rechaza como en la confirmación.
        var sube = await EditarAsync(
            ctx, creada.Id, Solicitud(ctx, ctx.IdArticuloConLote, 12m, creada.NumeroExterno, lote: "L-VIEJO", vencimiento: ayer));
        Assert.Equal("lote_vencido_en_recepcion", await CodigoAsync(sube, HttpStatusCode.Conflict));

        // Bajarla, o dejarla igual, solo toca lo ya recibido.
        var editada = await EditarConExitoAsync(
            ctx, creada.Id, Solicitud(ctx, ctx.IdArticuloConLote, 8m, creada.NumeroExterno, lote: "L-VIEJO", vencimiento: ayer));
        await EditarConExitoAsync(
            ctx, creada.Id, Solicitud(ctx, ctx.IdArticuloConLote, 8m, creada.NumeroExterno, lote: "L-VIEJO", vencimiento: ayer));

        Assert.Equal(idLote, editada.Items.Single().IdLote);
        Assert.Equal([10m, -2m], (await MovimientosAsync(ctx, creada.Id)).Select(m => m.Cantidad));
        Assert.All(await MovimientosAsync(ctx, creada.Id), m => Assert.Equal(idLote, m.IdLote));

        var vencida = await EditarAsync(
            ctx, creada.Id,
            Solicitud(ctx, ctx.IdArticuloConLote, 8m, creada.NumeroExterno, lote: "L-NUEVO", vencimiento: ayer));
        Assert.Equal("lote_vencido_en_recepcion", await CodigoAsync(vencida, HttpStatusCode.Conflict));
    }

    // ---- punto de venta, orden de compra, número ---------------------------------------------------------------

    [Fact]
    public async Task CambiarElPuntoDeVentaSacaElStockDelViejoYLoPoneEnElNuevo()
    {
        var ctx = await PrepararAsync(nameof(CambiarElPuntoDeVentaSacaElStockDelViejoYLoPoneEnElNuevo));
        var alta = await ctx.Admin.PostAsJsonAsync("/api/puntos-venta", new PuntoVentaAlta(ctx.IdEmpresa, "PV2", ModoPuntoVenta.Web));
        var cuerpoAlta = await alta.Content.ReadAsStringAsync();
        Assert.True(alta.StatusCode == HttpStatusCode.Created, cuerpoAlta);
        var idPv2 = JsonDocument.Parse(cuerpoAlta).RootElement.GetProperty("id").GetInt32();
        var creada = await CrearYConfirmarAsync(ctx, Solicitud(ctx, ctx.IdArticulo, 10m));

        var editada = await EditarConExitoAsync(
            ctx, creada.Id, Solicitud(ctx, ctx.IdArticulo, 10m, creada.NumeroExterno, idPuntoVenta: idPv2));

        Assert.Equal(idPv2, editada.IdPuntoVenta);
        Assert.Equal(0m, await StockAsync(ctx, ctx.IdArticulo));
        Assert.Equal(10m, await StockAsync(ctx, ctx.IdArticulo, idPv2));
        Assert.Equal(
            [(ctx.IdPuntoVenta, 10m), (ctx.IdPuntoVenta, -10m), (idPv2, 10m)],
            (await MovimientosAsync(ctx, creada.Id)).Select(m => (m.IdPuntoVenta, m.Cantidad)));
        Assert.Single(await AsientosAsync(ctx, creada.Id, TipoMovimientoCcProveedor.Compra));
    }

    [Fact]
    public async Task CambiarElProveedorMueveLaDeudaDeUnoAOtro()
    {
        var ctx = await PrepararAsync(nameof(CambiarElProveedorMueveLaDeudaDeUnoAOtro));
        var deudaPrevia = await SembrarDeudaPreviaAsync(ctx);
        var creada = await CrearYConfirmarAsync(ctx, Solicitud(ctx, ctx.IdArticulo, 10m));

        await EditarConExitoAsync(
            ctx, creada.Id, Solicitud(ctx, ctx.IdArticulo, 10m, creada.NumeroExterno, idProveedor: ctx.IdProveedor2));

        Assert.Equal(deudaPrevia, await SaldoAsync(ctx, ctx.IdProveedor));
        Assert.Equal(creada.Total, await SaldoAsync(ctx, ctx.IdProveedor2));
        var asientos = await AsientosAsync(ctx, creada.Id, TipoMovimientoCcProveedor.Compra);
        Assert.Equal(
            [(ctx.IdProveedor, creada.Total), (ctx.IdProveedor, -creada.Total), (ctx.IdProveedor2, creada.Total)],
            asientos.Select(a => (a.IdProveedor, a.Importe)));
    }

    [Fact]
    public async Task CambiarDeOrdenDeCompraReProyectaLasDosOrdenes()
    {
        var ctx = await PrepararAsync(nameof(CambiarDeOrdenDeCompraReProyectaLasDosOrdenes));
        var ordenA = await CrearYEnviarOrdenAsync(ctx);
        var ordenB = await CrearYEnviarOrdenAsync(ctx);
        var creada = await CrearYConfirmarAsync(ctx, Solicitud(ctx, ctx.IdArticulo, 10m, idOrden: ordenA));
        Assert.Equal(EstadoOrdenCompra.Cerrada, await EstadoDeLaOrdenAsync(ctx, ordenA));
        Assert.Equal(EstadoOrdenCompra.Enviada, await EstadoDeLaOrdenAsync(ctx, ordenB));

        await EditarConExitoAsync(ctx, creada.Id, Solicitud(ctx, ctx.IdArticulo, 10m, creada.NumeroExterno, idOrden: ordenB));

        Assert.Equal(EstadoOrdenCompra.Enviada, await EstadoDeLaOrdenAsync(ctx, ordenA));
        Assert.Equal(EstadoOrdenCompra.Cerrada, await EstadoDeLaOrdenAsync(ctx, ordenB));

        // Sin cambiar de orden, recibir menos de lo pedido la re-proyecta a recibida parcial.
        await EditarConExitoAsync(ctx, creada.Id, Solicitud(ctx, ctx.IdArticulo, 4m, creada.NumeroExterno, idOrden: ordenB));
        Assert.Equal(EstadoOrdenCompra.RecibidaParcial, await EstadoDeLaOrdenAsync(ctx, ordenB));
    }

    [Fact]
    public async Task LigarLaCompraAUnaOrdenInexistenteEs404()
    {
        var ctx = await PrepararAsync(nameof(LigarLaCompraAUnaOrdenInexistenteEs404));
        var creada = await CrearYConfirmarAsync(ctx, Solicitud(ctx, ctx.IdArticulo, 10m));

        var respuesta = await EditarAsync(
            ctx, creada.Id, Solicitud(ctx, ctx.IdArticulo, 10m, creada.NumeroExterno, idOrden: 999_999));

        await CodigoAsync(respuesta, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ElNumeroDeOtraCompraDelMismoProveedorEs409CompraDuplicadaYNoDejaNingunEfecto()
    {
        var ctx = await PrepararAsync(nameof(ElNumeroDeOtraCompraDelMismoProveedorEs409CompraDuplicadaYNoDejaNingunEfecto));
        var primera = await CrearYConfirmarAsync(ctx, Solicitud(ctx, ctx.IdArticulo2, 2m));
        var segunda = await CrearYConfirmarAsync(ctx, Solicitud(ctx, ctx.IdArticulo, 10m));

        var respuesta = await EditarAsync(ctx, segunda.Id, Solicitud(ctx, ctx.IdArticulo, 14m, primera.NumeroExterno));

        Assert.Equal("compra_duplicada", await CodigoAsync(respuesta, HttpStatusCode.Conflict));
        Assert.Equal(10m, await StockAsync(ctx, ctx.IdArticulo));
        Assert.Single(await MovimientosAsync(ctx, segunda.Id));
        Assert.Single(await AsientosAsync(ctx, segunda.Id));
        await using var db = Db(ctx);
        Assert.Equal(segunda.NumeroExterno, (await db.ComprobantesCompra.SingleAsync(c => c.Id == segunda.Id)).NumeroExterno);
    }

    // ---- editar y anular ---------------------------------------------------------------------------------------

    /// <summary>Se vendió parte de lo que la compra editada dejó: la reversa neta (−9) deja el stock negativo y se rechaza.</summary>
    [Fact]
    public async Task AnularUnaCompraEditadaCuyaMercaderiaSeVendioParcialmenteEs409()
    {
        var ctx = await PrepararAsync(nameof(AnularUnaCompraEditadaCuyaMercaderiaSeVendioParcialmenteEs409));
        var creada = await CrearYConfirmarAsync(ctx, Solicitud(ctx, ctx.IdArticulo, 10m));
        await EditarConExitoAsync(ctx, creada.Id, Solicitud(ctx, ctx.IdArticulo, 9m, creada.NumeroExterno));
        await VenderAsync(ctx, ctx.IdArticulo, 5m, idLote: null);

        var anulacion = await ctx.Admin.PostAsync($"/api/compras/{creada.Id}/anular", null);

        Assert.Equal("compra_anulacion_stock_negativo", await CodigoAsync(anulacion, HttpStatusCode.Conflict));
        Assert.Equal(4m, await StockAsync(ctx, ctx.IdArticulo));
    }

    /// <summary>Editar hacia arriba y hacia abajo y anular: el libro tiene +10, +4, −5 de la misma clave, y la anulación
    /// revierte lo NETO (−9) con un solo movimiento y con el stock en 9. Revertir cada asiento por separado empezaría por
    /// −10 y dejaría el stock en −1: un falso stock negativo.</summary>
    [Fact]
    public async Task EditarYDespuesAnularRevierteLoNetoConUnSoloMovimiento()
    {
        var ctx = await PrepararAsync(nameof(EditarYDespuesAnularRevierteLoNetoConUnSoloMovimiento));
        var deudaPrevia = await SembrarDeudaPreviaAsync(ctx);
        var creada = await CrearYConfirmarAsync(ctx, Solicitud(ctx, ctx.IdArticulo, 10m));
        await EditarConExitoAsync(ctx, creada.Id, Solicitud(ctx, ctx.IdArticulo, 14m, creada.NumeroExterno));
        await EditarConExitoAsync(ctx, creada.Id, Solicitud(ctx, ctx.IdArticulo, 9m, creada.NumeroExterno));

        var anulacion = await ctx.Admin.PostAsync($"/api/compras/{creada.Id}/anular", null);
        Assert.True(anulacion.StatusCode == HttpStatusCode.OK, await anulacion.Content.ReadAsStringAsync());

        Assert.Equal(0m, await StockAsync(ctx, ctx.IdArticulo));
        var reversas = await MovimientosAsync(ctx, creada.Id, MotivoStock.Anulacion);
        Assert.Equal(-9m, reversas.Single().Cantidad);

        Assert.Equal(deudaPrevia, await SaldoAsync(ctx, ctx.IdProveedor));
        var ajuste = (await AsientosAsync(ctx, creada.Id, TipoMovimientoCcProveedor.Ajuste)).Single();
        Assert.Equal(-9m * 121m, ajuste.Importe);
        Assert.Equal(deudaPrevia, ajuste.SaldoResultante);
    }

    /// <summary>Una clave que ya netea a cero (se agregó y se quitó) no se revierte: no hay movimiento de anulación.</summary>
    [Fact]
    public async Task LaClaveQueYaNeteabaACeroNoGeneraMovimientoDeAnulacion()
    {
        var ctx = await PrepararAsync(nameof(LaClaveQueYaNeteabaACeroNoGeneraMovimientoDeAnulacion));
        var creada = await CrearYConfirmarAsync(ctx, Solicitud(ctx, ctx.IdArticulo, 10m));
        await EditarConExitoAsync(ctx, creada.Id, Solicitud(ctx, ctx.IdArticulo2, 3m, creada.NumeroExterno));
        await EditarConExitoAsync(ctx, creada.Id, Solicitud(ctx, ctx.IdArticulo, 10m, creada.NumeroExterno));

        var anulacion = await ctx.Admin.PostAsync($"/api/compras/{creada.Id}/anular", null);
        Assert.True(anulacion.StatusCode == HttpStatusCode.OK, await anulacion.Content.ReadAsStringAsync());

        var reversas = await MovimientosAsync(ctx, creada.Id, MotivoStock.Anulacion);
        Assert.Equal([(ctx.IdArticulo, -10m)], reversas.Select(m => (m.IdArticulo, m.Cantidad)));
        Assert.Equal(0m, await StockAsync(ctx, ctx.IdArticulo));
        Assert.Equal(0m, await StockAsync(ctx, ctx.IdArticulo2));
    }

    // ---- estados, despacho y concurrencia ---------------------------------------------------------------------

    [Fact]
    public async Task UnaCompraAnuladaNoSeEdita()
    {
        var ctx = await PrepararAsync(nameof(UnaCompraAnuladaNoSeEdita));
        var creada = await CrearYConfirmarAsync(ctx, Solicitud(ctx, ctx.IdArticulo, 10m));
        var anulacion = await ctx.Admin.PostAsync($"/api/compras/{creada.Id}/anular", null);
        Assert.Equal(HttpStatusCode.OK, anulacion.StatusCode);

        var respuesta = await EditarAsync(ctx, creada.Id, Solicitud(ctx, ctx.IdArticulo, 14m, creada.NumeroExterno));

        Assert.Equal("compra_no_editable", await CodigoAsync(respuesta, HttpStatusCode.Conflict));
    }

    /// <summary>update-404-first: un id inexistente responde 404 aunque el cuerpo sea inválido o choque con otra compra.</summary>
    [Fact]
    public async Task UnaCompraInexistenteEs404AunConUnCuerpoInvalido()
    {
        var ctx = await PrepararAsync(nameof(UnaCompraInexistenteEs404AunConUnCuerpoInvalido));
        var invalida = Solicitud(ctx, ctx.IdArticulo, 10m) with { Items = [] };

        var respuesta = await EditarAsync(ctx, 999_999, invalida);

        await CodigoAsync(respuesta, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task EditarUnaConfirmadaSinLineasOSinNumeroEsUn400()
    {
        var ctx = await PrepararAsync(nameof(EditarUnaConfirmadaSinLineasOSinNumeroEsUn400));
        var creada = await CrearYConfirmarAsync(ctx, Solicitud(ctx, ctx.IdArticulo, 10m));

        var sinLineas = await EditarAsync(ctx, creada.Id, Solicitud(ctx, ctx.IdArticulo, 10m, creada.NumeroExterno) with { Items = [] });
        Assert.Equal("compra_sin_items", await CodigoAsync(sinLineas, HttpStatusCode.BadRequest));

        var sinNumero = await EditarAsync(ctx, creada.Id, Solicitud(ctx, ctx.IdArticulo, 10m) with { NumeroExterno = null });
        Assert.Equal("compra_numero_externo_requerido", await CodigoAsync(sinNumero, HttpStatusCode.BadRequest));
    }

    /// <summary>La edición pierde la carrera contra una anulación: pausada tras abrir su transacción, la compra se anula y
    /// comitea; al reanudar, el lock con <c>estado='confirmada'</c> no toma ninguna fila y la respuesta es 409
    /// <c>compra_cambio_concurrente</c>, sin movimientos de edición.</summary>
    [Fact]
    public async Task UnaAnulacionQueGanaLaCarreraHaceQueLaEdicionResponda409CambioConcurrente()
    {
        var ctx = await PrepararAsync(nameof(UnaAnulacionQueGanaLaCarreraHaceQueLaEdicionResponda409CambioConcurrente));
        var creada = await CrearYConfirmarAsync(ctx, Solicitud(ctx, ctx.IdArticulo, 10m));

        var transaccionIniciada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var puedeContinuar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interceptor = new InterceptorDePausaTrasIniciarLaTransaccion(transaccionIniciada, puedeContinuar);

        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddDbContext<WaysDbContext>((_, options) => options.AddInterceptors(interceptor))));

        using var clienteEditor = factory.CreateClient();
        var login = await clienteEditor.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(ctx.MailAdmin, ctx.PasswordAdmin));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var tareaEdicion = clienteEditor.PutAsJsonAsync(
            $"/api/compras/{creada.Id}", Solicitud(ctx, ctx.IdArticulo, 14m, creada.NumeroExterno));

        await transaccionIniciada.Task;

        var anulacion = await ctx.Admin.PostAsync($"/api/compras/{creada.Id}/anular", null);
        Assert.True(anulacion.StatusCode == HttpStatusCode.OK, await anulacion.Content.ReadAsStringAsync());

        puedeContinuar.TrySetResult();

        var respuesta = await tareaEdicion;
        Assert.Equal("compra_cambio_concurrente", await CodigoAsync(respuesta, HttpStatusCode.Conflict));

        await using var db = Db(ctx);
        Assert.Equal(EstadoCompra.Anulada, (await db.ComprobantesCompra.SingleAsync(c => c.Id == creada.Id)).Estado);
        Assert.Equal(0m, await StockAsync(ctx, ctx.IdArticulo));
        Assert.Equal([10m], (await MovimientosAsync(ctx, creada.Id, MotivoStock.Compra)).Select(m => m.Cantidad));
        Assert.Equal(0, await db.Auditoria.CountAsync(a => a.Accion == "compra.edicion" && a.IdEntidad == creada.Id));
    }

    // ---- pagos aplicados (E6-S3) -------------------------------------------------------------------------------

    private static (int? IdComprobanteCompra, int? IdProveedor) Vinculo(int? idCompra, int? idProveedor) => (idCompra, idProveedor);

    /// <summary>Paga la compra con un pago nuevo y devuelve su id: el de la aplicación más reciente de la compra.</summary>
    private async Task<int> PagarAsync(Contexto ctx, int idCompra, decimal importe)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync(
            $"/api/pagos/de-compra/{idCompra}",
            new SolicitudDePagoDeCompraConLineas(
                DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), [new LineaDePago(ctx.IdEfectivo, importe, null)], null));
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, await respuesta.Content.ReadAsStringAsync());

        await using var db = Db(ctx);
        return await db.AplicacionesPago
            .Where(a => a.IdComprobanteCompra == idCompra)
            .OrderByDescending(a => a.Id)
            .Select(a => a.IdPago)
            .FirstAsync();
    }

    private static async Task CerrarElSaldoConRedondeoAsync(Contexto ctx, int idCompra)
    {
        var respuesta = await ctx.Admin.PostAsync($"/api/compras/{idCompra}/ajuste-de-redondeo", null);
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, await respuesta.Content.ReadAsStringAsync());
    }

    /// <summary>Aplicaciones vivas de la compra como (pago, importe), en el orden en que se hicieron.</summary>
    private async Task<List<(int IdPago, decimal Importe)>> AplicacionesVivasAsync(Contexto ctx, int idCompra)
    {
        await using var db = Db(ctx);
        return (await db.AplicacionesPago
                .Where(a => a.IdComprobanteCompra == idCompra)
                .OrderBy(a => a.Id)
                .Select(a => new { a.IdPago, a.Importe })
                .ToListAsync())
            .Select(a => (a.IdPago, a.Importe))
            .ToList();
    }

    /// <summary>Lo que le queda disponible al pago (importe − aplicado vivo): lo liberado vuelve acá.</summary>
    private async Task<decimal> DisponibleAsync(Contexto ctx, int idPago)
    {
        await using var db = Db(ctx);
        var importe = await db.Pagos.Where(p => p.Id == idPago).Select(p => p.Importe).SingleAsync();
        var aplicado = await db.AplicacionesPago.Where(a => a.IdPago == idPago).SumAsync(a => (decimal?)a.Importe) ?? 0m;
        return importe - aplicado;
    }

    private async Task<(int? IdComprobanteCompra, int? IdProveedor)> VinculoDelPagoAsync(Contexto ctx, int idPago)
    {
        await using var db = Db(ctx);
        var pago = await db.Pagos.AsNoTracking().SingleAsync(p => p.Id == idPago);
        return (pago.IdComprobanteCompra, pago.IdProveedor);
    }

    private async Task<JsonElement> AuditoriaDeLaEdicionAsync(Contexto ctx, int idCompra)
    {
        await using var db = Db(ctx);
        var fila = await db.Auditoria
            .Where(a => a.Accion == "compra.edicion" && a.IdEntidad == idCompra)
            .OrderByDescending(a => a.Id)
            .FirstAsync();
        return JsonDocument.Parse(fila.ValorNuevo).RootElement.Clone();
    }

    /// <summary>Bajar el total por debajo de lo aplicado recorta la aplicación más NUEVA primero; lo recortado vuelve a ser
    /// disponible de ese pago y nada se reaplica: subir el total después no devuelve la aplicación.</summary>
    [Fact]
    public async Task BajarElTotalPorDebajoDeLoAplicadoRecortaLaAplicacionMasNuevaYLoRecortadoVuelveAlPago()
    {
        var ctx = await PrepararAsync(nameof(BajarElTotalPorDebajoDeLoAplicadoRecortaLaAplicacionMasNuevaYLoRecortadoVuelveAlPago));
        var creada = await CrearYConfirmarAsync(ctx, Solicitud(ctx, ctx.IdArticulo, 10m));
        var pagoViejo = await PagarAsync(ctx, creada.Id, 500m);
        var pagoNuevo = await PagarAsync(ctx, creada.Id, 500m);

        var ajustada = await EditarConExitoAsync(ctx, creada.Id, Solicitud(ctx, ctx.IdArticulo, 5m, creada.NumeroExterno));

        Assert.Equal(605m, ajustada.Total);
        Assert.Equal(605m, ajustada.Pagado);
        Assert.Equal([(pagoViejo, 500m), (pagoNuevo, 105m)], await AplicacionesVivasAsync(ctx, creada.Id));
        Assert.Equal(0m, await DisponibleAsync(ctx, pagoViejo));
        Assert.Equal(395m, await DisponibleAsync(ctx, pagoNuevo));

        var sube = await EditarConExitoAsync(ctx, creada.Id, Solicitud(ctx, ctx.IdArticulo, 8m, creada.NumeroExterno));
        Assert.Equal(8m * 121m, sube.Total);
        Assert.Equal(605m, sube.Pagado);
        Assert.Equal([(pagoViejo, 500m), (pagoNuevo, 105m)], await AplicacionesVivasAsync(ctx, creada.Id));

        var auditoria = await AuditoriaDeLaEdicionAsync(ctx, creada.Id);
        Assert.Empty(auditoria.GetProperty("aplicaciones_tratadas").EnumerateArray());
        Assert.Equal(0m, auditoria.GetProperty("redondeo_revertido").GetDecimal());
    }

    [Fact]
    public async Task ElRecorteDaDeBajaLasAplicacionesQueEntranEnterasYAchicaLaUltima()
    {
        var ctx = await PrepararAsync(nameof(ElRecorteDaDeBajaLasAplicacionesQueEntranEnterasYAchicaLaUltima));
        var creada = await CrearYConfirmarAsync(ctx, Solicitud(ctx, ctx.IdArticulo, 10m));
        var pagoA = await PagarAsync(ctx, creada.Id, 300m);
        var pagoB = await PagarAsync(ctx, creada.Id, 300m);
        var pagoC = await PagarAsync(ctx, creada.Id, 300m);

        var ajustada = await EditarConExitoAsync(ctx, creada.Id, Solicitud(ctx, ctx.IdArticulo, 4m, creada.NumeroExterno));

        Assert.Equal(484m, ajustada.Pagado);
        Assert.Equal([(pagoA, 300m), (pagoB, 184m)], await AplicacionesVivasAsync(ctx, creada.Id));
        Assert.Equal(300m, await DisponibleAsync(ctx, pagoC));
        Assert.Equal(116m, await DisponibleAsync(ctx, pagoB));

        var tratadas = (await AuditoriaDeLaEdicionAsync(ctx, creada.Id)).GetProperty("aplicaciones_tratadas")
            .EnumerateArray().Select(a => (a.GetProperty("id_pago").GetInt32(), a.GetProperty("importe_nuevo").GetDecimal())).ToList();
        Assert.Equal([(pagoC, 0m), (pagoB, 184m)], tratadas);
    }

    /// <summary>La compra con redondeo vigente que baja de total: el redondeo se revierte (el saldo del proveedor vuelve a
    /// reflejar lo perdonado) y el cupo se calcula con lo perdonado en cero, así que el recorte llega hasta el total nuevo.</summary>
    [Fact]
    public async Task BajarElTotalDeUnaCompraConRedondeoRevierteElRedondeoYDespuesRecorta()
    {
        var ctx = await PrepararAsync(nameof(BajarElTotalDeUnaCompraConRedondeoRevierteElRedondeoYDespuesRecorta));
        var deudaPrevia = await SembrarDeudaPreviaAsync(ctx);
        var creada = await CrearYConfirmarAsync(ctx, Solicitud(ctx, ctx.IdArticulo, 10m));
        var pago = await PagarAsync(ctx, creada.Id, 1000m);
        await CerrarElSaldoConRedondeoAsync(ctx, creada.Id);
        Assert.Equal(deudaPrevia, await SaldoAsync(ctx, ctx.IdProveedor));

        var ajustada = await EditarConExitoAsync(ctx, creada.Id, Solicitud(ctx, ctx.IdArticulo, 5m, creada.NumeroExterno));

        Assert.Equal(605m, ajustada.Total);
        Assert.Equal(605m, ajustada.Pagado);
        Assert.Equal([(pago, 605m)], await AplicacionesVivasAsync(ctx, creada.Id));
        Assert.Equal(395m, await DisponibleAsync(ctx, pago));
        Assert.Equal(0m, (await AsientosAsync(ctx, creada.Id, TipoMovimientoCcProveedor.Redondeo)).Sum(a => a.Importe));
        Assert.Equal(deudaPrevia - 395m, await SaldoAsync(ctx, ctx.IdProveedor));

        var auditoria = await AuditoriaDeLaEdicionAsync(ctx, creada.Id);
        Assert.Equal(210m, auditoria.GetProperty("redondeo_revertido").GetDecimal());
        await using var db = Db(ctx);
        // Una fila del ajuste que cerró el saldo y otra de la reversa de la edición.
        Assert.Equal(2, await db.Auditoria.CountAsync(a => a.Accion == "compra.redondeo" && a.IdEntidad == creada.Id));
    }

    /// <summary>Subir el total no recorta nada, pero el redondeo (que cerraba el saldo con el total anterior) se revierte y
    /// el saldo pendiente reaparece.</summary>
    [Fact]
    public async Task SubirElTotalDeUnaCompraConRedondeoRevierteElRedondeoYNoRecorta()
    {
        var ctx = await PrepararAsync(nameof(SubirElTotalDeUnaCompraConRedondeoRevierteElRedondeoYNoRecorta));
        var deudaPrevia = await SembrarDeudaPreviaAsync(ctx);
        var creada = await CrearYConfirmarAsync(ctx, Solicitud(ctx, ctx.IdArticulo, 10m));
        var pago = await PagarAsync(ctx, creada.Id, 1000m);
        await CerrarElSaldoConRedondeoAsync(ctx, creada.Id);

        var editada = await EditarConExitoAsync(ctx, creada.Id, Solicitud(ctx, ctx.IdArticulo, 12m, creada.NumeroExterno));

        Assert.Equal(1452m, editada.Total);
        Assert.Equal(1000m, editada.Pagado);
        Assert.Equal([(pago, 1000m)], await AplicacionesVivasAsync(ctx, creada.Id));
        Assert.Equal(0m, (await AsientosAsync(ctx, creada.Id, TipoMovimientoCcProveedor.Redondeo)).Sum(a => a.Importe));
        Assert.Equal(deudaPrevia + 452m, await SaldoAsync(ctx, ctx.IdProveedor));
    }

    /// <summary>Cambiar el proveedor libera TODAS las aplicaciones, revierte el redondeo y corta el vínculo de los pagos:
    /// cada pago conserva su proveedor y su crédito completo con él, y la deuda de la compra pasa al proveedor nuevo.</summary>
    [Fact]
    public async Task CambiarElProveedorLiberaLasAplicacionesRevierteElRedondeoYCortaElVinculoDeLosPagos()
    {
        var ctx = await PrepararAsync(nameof(CambiarElProveedorLiberaLasAplicacionesRevierteElRedondeoYCortaElVinculoDeLosPagos));
        var deudaPrevia = await SembrarDeudaPreviaAsync(ctx);
        var creada = await CrearYConfirmarAsync(ctx, Solicitud(ctx, ctx.IdArticulo, 10m));
        var pagoA = await PagarAsync(ctx, creada.Id, 600m);
        var pagoB = await PagarAsync(ctx, creada.Id, 400m);
        await CerrarElSaldoConRedondeoAsync(ctx, creada.Id);

        var editada = await EditarConExitoAsync(
            ctx, creada.Id, Solicitud(ctx, ctx.IdArticulo, 10m, creada.NumeroExterno, idProveedor: ctx.IdProveedor2));

        Assert.Equal(ctx.IdProveedor2, editada.IdProveedor);
        Assert.Equal(0m, editada.Pagado);
        Assert.Empty(await AplicacionesVivasAsync(ctx, creada.Id));
        Assert.Equal(600m, await DisponibleAsync(ctx, pagoA));
        Assert.Equal(400m, await DisponibleAsync(ctx, pagoB));
        Assert.Equal(Vinculo(null, ctx.IdProveedor), await VinculoDelPagoAsync(ctx, pagoA));
        Assert.Equal(Vinculo(null, ctx.IdProveedor), await VinculoDelPagoAsync(ctx, pagoB));
        Assert.Equal(0m, (await AsientosAsync(ctx, creada.Id, TipoMovimientoCcProveedor.Redondeo)).Sum(a => a.Importe));

        // El proveedor anterior queda con el crédito de lo que pagó; el nuevo, con la deuda entera de la compra.
        Assert.Equal(deudaPrevia - 1000m, await SaldoAsync(ctx, ctx.IdProveedor));
        Assert.Equal(1210m, await SaldoAsync(ctx, ctx.IdProveedor2));

        var auditoria = await AuditoriaDeLaEdicionAsync(ctx, creada.Id);
        Assert.Equal(2, auditoria.GetProperty("aplicaciones_tratadas").GetArrayLength());
        Assert.Equal(210m, auditoria.GetProperty("redondeo_revertido").GetDecimal());
        Assert.Equal(
            [pagoA, pagoB], auditoria.GetProperty("pagos_desvinculados").EnumerateArray().Select(p => p.GetInt32()).Order());
    }

    /// <summary>Un cambio de punto de venta DENTRO de la misma empresa no cambia a quién pertenece la compra: las aplicaciones y
    /// los vínculos de los pagos quedan como están.</summary>
    [Fact]
    public async Task CambiarElPuntoDeVentaDentroDeLaEmpresaConservaLasAplicacionesYElVinculoDeLosPagos()
    {
        var ctx = await PrepararAsync(nameof(CambiarElPuntoDeVentaDentroDeLaEmpresaConservaLasAplicacionesYElVinculoDeLosPagos));
        var alta = await ctx.Admin.PostAsJsonAsync("/api/puntos-venta", new PuntoVentaAlta(ctx.IdEmpresa, "PV2", ModoPuntoVenta.Web));
        var cuerpoAlta = await alta.Content.ReadAsStringAsync();
        Assert.True(alta.StatusCode == HttpStatusCode.Created, cuerpoAlta);
        var idPv2 = JsonDocument.Parse(cuerpoAlta).RootElement.GetProperty("id").GetInt32();
        var creada = await CrearYConfirmarAsync(ctx, Solicitud(ctx, ctx.IdArticulo, 10m));
        var pago = await PagarAsync(ctx, creada.Id, 700m);

        var editada = await EditarConExitoAsync(
            ctx, creada.Id, Solicitud(ctx, ctx.IdArticulo, 10m, creada.NumeroExterno, idPuntoVenta: idPv2));

        Assert.Equal(idPv2, editada.IdPuntoVenta);
        Assert.Equal(700m, editada.Pagado);
        Assert.Equal([(pago, 700m)], await AplicacionesVivasAsync(ctx, creada.Id));
        Assert.Equal(Vinculo(creada.Id, ctx.IdProveedor), await VinculoDelPagoAsync(ctx, pago));
    }

    /// <summary>Pasar la compra a un punto de venta de OTRA empresa libera las aplicaciones y corta los vínculos.</summary>
    [Fact]
    public async Task CambiarElPuntoDeVentaAOtraEmpresaLiberaLasAplicacionesYCortaElVinculoDeLosPagos()
    {
        var ctx = await PrepararAsync(nameof(CambiarElPuntoDeVentaAOtraEmpresaLiberaLasAplicacionesYCortaElVinculoDeLosPagos));
        int idPvOtraEmpresa;
        await using (var db = Db(ctx))
        {
            var ahora = DateTimeOffset.UtcNow;
            var empresa = new Empresa { IdTenant = ctx.IdTenant, RazonSocial = "Otra empresa", CreatedAt = ahora, UpdatedAt = ahora };
            db.Empresas.Add(empresa);
            await db.SaveChangesAsync();
            var puntoVenta = new PuntoVenta
            {
                IdTenant = ctx.IdTenant, IdEmpresa = empresa.Id, Nombre = "Otra empresa - Local", CreatedAt = ahora, UpdatedAt = ahora
            };
            db.PuntosVenta.Add(puntoVenta);
            await db.SaveChangesAsync();
            idPvOtraEmpresa = puntoVenta.Id;
        }

        var creada = await CrearYConfirmarAsync(ctx, Solicitud(ctx, ctx.IdArticulo, 10m));
        var pago = await PagarAsync(ctx, creada.Id, 700m);

        var editada = await EditarConExitoAsync(
            ctx, creada.Id, Solicitud(ctx, ctx.IdArticulo, 10m, creada.NumeroExterno, idPuntoVenta: idPvOtraEmpresa));

        Assert.Equal(idPvOtraEmpresa, editada.IdPuntoVenta);
        Assert.Equal(0m, editada.Pagado);
        Assert.Empty(await AplicacionesVivasAsync(ctx, creada.Id));
        Assert.Equal(700m, await DisponibleAsync(ctx, pago));
        Assert.Equal(Vinculo(null, ctx.IdProveedor), await VinculoDelPagoAsync(ctx, pago));
    }

    /// <summary>Un pago administrativo sin punto de venta propio, convertido por <c>vincular-compra</c>, tomaba el punto de
    /// venta de la compra: al desvincularlo se queda con el que la compra tenía y sigue siendo editable y borrable, con el
    /// saldo de cada proveedor igual a la suma de su libro.</summary>
    [Fact]
    public async Task UnPagoSinPuntoDeVentaVinculadoPorConversionSigueEditableYBorrableTrasCambiarElProveedor()
    {
        var ctx = await PrepararAsync(nameof(UnPagoSinPuntoDeVentaVinculadoPorConversionSigueEditableYBorrableTrasCambiarElProveedor));
        var creada = await CrearYConfirmarAsync(ctx, Solicitud(ctx, ctx.IdArticulo, 10m));

        var altaPago = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos/administracion",
            new SolicitudDePagoDeAdministracion(
                DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), ctx.IdEmpresa, null, CategoriaPago.Otros, null, null,
                "Pago sin punto de venta", null, ctx.IdEfectivo, null, 500m));
        var cuerpoPago = await altaPago.Content.ReadAsStringAsync();
        Assert.True(altaPago.StatusCode == HttpStatusCode.Created, cuerpoPago);
        var idPago = JsonDocument.Parse(cuerpoPago).RootElement.GetProperty("id").GetInt32();

        var vincular = await ctx.Admin.PostAsJsonAsync(
            $"/api/gastos/{idPago}/vincular-compra", new SolicitudDeVincularCompra(creada.Id));
        Assert.True(vincular.StatusCode == HttpStatusCode.OK, await vincular.Content.ReadAsStringAsync());

        await EditarConExitoAsync(
            ctx, creada.Id, Solicitud(ctx, ctx.IdArticulo, 10m, creada.NumeroExterno, idProveedor: ctx.IdProveedor2));

        await using (var db = Db(ctx))
        {
            var pago = await db.Pagos.AsNoTracking().SingleAsync(p => p.Id == idPago);
            Assert.Null(pago.IdComprobanteCompra);
            Assert.Equal(ctx.IdPuntoVenta, pago.IdPuntoVenta);
        }

        var edicion = await ctx.Admin.PutAsJsonAsync(
            $"/api/gastos/administracion/{idPago}",
            new SolicitudDeEdicionDePago(
                CategoriaPago.Proveedor, ctx.IdProveedor, null, "Concepto editado", null, ctx.IdEfectivo, null, 500m));
        Assert.True(edicion.StatusCode == HttpStatusCode.OK, await edicion.Content.ReadAsStringAsync());

        var baja = await ctx.Admin.DeleteAsync($"/api/gastos/administracion/{idPago}");
        Assert.True(baja.StatusCode == HttpStatusCode.NoContent, await baja.Content.ReadAsStringAsync());

        await using var contexto = Db(ctx);
        foreach (var idProveedor in new[] { ctx.IdProveedor, ctx.IdProveedor2 })
        {
            var enLibro = await contexto.MovimientosCuentaCorrienteProveedor
                .Where(m => m.IdProveedor == idProveedor).SumAsync(m => (decimal?)m.Importe) ?? 0m;
            Assert.Equal(enLibro, await SaldoAsync(ctx, idProveedor));
        }
    }

    /// <summary>Un pago vinculado por <c>pagos.id_comprobante_compra</c> sin aplicación viva (se desaplicó) tampoco queda
    /// atado: cambiar el proveedor lo desvincula.</summary>
    [Fact]
    public async Task CambiarElProveedorDesvinculaUnPagoQueYaNoTeniaAplicacionViva()
    {
        var ctx = await PrepararAsync(nameof(CambiarElProveedorDesvinculaUnPagoQueYaNoTeniaAplicacionViva));
        var creada = await CrearYConfirmarAsync(ctx, Solicitud(ctx, ctx.IdArticulo, 10m));
        var pago = await PagarAsync(ctx, creada.Id, 100m);

        await using (var db = Db(ctx))
        {
            var aplicacion = await db.AplicacionesPago.SingleAsync(a => a.IdComprobanteCompra == creada.Id);
            var desaplicar = await ctx.Admin.DeleteAsync($"/api/pagos/{aplicacion.IdPago}/aplicaciones/{aplicacion.Id}");
            Assert.Equal(HttpStatusCode.NoContent, desaplicar.StatusCode);
        }

        Assert.Equal(Vinculo(creada.Id, ctx.IdProveedor), await VinculoDelPagoAsync(ctx, pago));

        await EditarConExitoAsync(
            ctx, creada.Id, Solicitud(ctx, ctx.IdArticulo, 10m, creada.NumeroExterno, idProveedor: ctx.IdProveedor2));

        Assert.Equal(Vinculo(null, ctx.IdProveedor), await VinculoDelPagoAsync(ctx, pago));
        Assert.Equal(100m, await DisponibleAsync(ctx, pago));
    }

    /// <summary>Editar sin tocar el total, el proveedor ni el punto de venta deja las aplicaciones y los vínculos como están.</summary>
    [Fact]
    public async Task UnaEdicionQueNoTocaTotalProveedorNiPuntoDeVentaDejaLosPagosIntactos()
    {
        var ctx = await PrepararAsync(nameof(UnaEdicionQueNoTocaTotalProveedorNiPuntoDeVentaDejaLosPagosIntactos));
        var creada = await CrearYConfirmarAsync(ctx, Solicitud(ctx, ctx.IdArticulo, 10m));
        var pago = await PagarAsync(ctx, creada.Id, 500m);

        var solicitud = Solicitud(ctx, ctx.IdArticulo, 10m, creada.NumeroExterno) with { Observaciones = "solo una nota" };
        await EditarConExitoAsync(ctx, creada.Id, solicitud);

        Assert.Equal([(pago, 500m)], await AplicacionesVivasAsync(ctx, creada.Id));
        Assert.Equal(Vinculo(creada.Id, ctx.IdProveedor), await VinculoDelPagoAsync(ctx, pago));
    }

    // ---- auditoría y estado de cuenta --------------------------------------------------------------------------

    /// <summary>Tras pasar la compra de un proveedor a otro de id MENOR, el saldo pendiente sale en el asiento del
    /// proveedor actual y no en la reversa del anterior (que es la fila más nueva).</summary>
    [Fact]
    public async Task TrasCambiarDeProveedorElSaldoPendienteSaleEnElAsientoDelProveedorActual()
    {
        var ctx = await PrepararAsync(nameof(TrasCambiarDeProveedorElSaldoPendienteSaleEnElAsientoDelProveedorActual));
        var creada = await CrearYConfirmarAsync(ctx, Solicitud(ctx, ctx.IdArticulo, 10m, idProveedor: ctx.IdProveedor2));
        await EditarConExitoAsync(
            ctx, creada.Id, Solicitud(ctx, ctx.IdArticulo, 10m, creada.NumeroExterno, idProveedor: ctx.IdProveedor));

        async Task<List<JsonElement>> FilasAsync(int idProveedor)
        {
            var respuesta = await ctx.Admin.GetAsync($"/api/proveedores/{idProveedor}/cuenta-corriente?historico=true&tamanio=50");
            var cuerpo = await respuesta.Content.ReadAsStringAsync();
            Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
            return [.. JsonDocument.Parse(cuerpo).RootElement.GetProperty("items").EnumerateArray()
                .Where(f => f.GetProperty("idComprobanteCompra").GetInt32() == creada.Id)];
        }

        static bool TienePendiente(JsonElement f) =>
            f.TryGetProperty("saldoPendienteDeLaCompra", out var v) && v.ValueKind == JsonValueKind.Number;

        Assert.Equal(1, (await FilasAsync(ctx.IdProveedor)).Count(TienePendiente));
        Assert.DoesNotContain(await FilasAsync(ctx.IdProveedor2), TienePendiente);
    }

    [Fact]
    public async Task LaEdicionDejaUnaFilaDeAuditoriaConElAntesElDespuesYLasDiferencias()
    {
        var ctx = await PrepararAsync(nameof(LaEdicionDejaUnaFilaDeAuditoriaConElAntesElDespuesYLasDiferencias));
        var creada = await CrearYConfirmarAsync(ctx, Solicitud(ctx, ctx.IdArticulo, 10m));

        await EditarConExitoAsync(ctx, creada.Id, Solicitud(ctx, ctx.IdArticulo, 14m, creada.NumeroExterno));

        await using var db = Db(ctx);
        var fila = await db.Auditoria.SingleAsync(a => a.Accion == "compra.edicion" && a.IdEntidad == creada.Id);
        Assert.Equal("comprobante_compra", fila.Entidad);
        Assert.Equal(ctx.IdPuntoVenta, fila.IdPuntoVenta);

        var anterior = JsonDocument.Parse(fila.ValorAnterior!).RootElement;
        var nuevo = JsonDocument.Parse(fila.ValorNuevo).RootElement;
        Assert.Equal(1210m, anterior.GetProperty("total").GetDecimal());
        Assert.Equal(1694m, nuevo.GetProperty("total").GetDecimal());
        Assert.Equal(10m, anterior.GetProperty("items")[0].GetProperty("cantidad").GetDecimal());
        Assert.Equal(14m, nuevo.GetProperty("items")[0].GetProperty("cantidad").GetDecimal());
        Assert.False(anterior.TryGetProperty("diferencias_de_stock", out _));
        var difStock = nuevo.GetProperty("diferencias_de_stock").EnumerateArray().Single();
        Assert.Equal(ctx.IdArticulo, difStock.GetProperty("id_articulo").GetInt32());
        Assert.Equal(4m, difStock.GetProperty("cantidad").GetDecimal());
        var difCuenta = nuevo.GetProperty("diferencias_de_cuenta").EnumerateArray().Single();
        Assert.Equal(ctx.IdProveedor, difCuenta.GetProperty("id_proveedor").GetInt32());
        Assert.Equal(484m, difCuenta.GetProperty("importe").GetDecimal());
    }

    /// <summary>El saldo pendiente de la compra se muestra solo en el asiento <c>compra</c> más nuevo, no en cada uno.</summary>
    [Fact]
    public async Task ElEstadoDeCuentaMuestraElSaldoPendienteSoloEnElAsientoCompraMasNuevo()
    {
        var ctx = await PrepararAsync(nameof(ElEstadoDeCuentaMuestraElSaldoPendienteSoloEnElAsientoCompraMasNuevo));
        var otra = await CrearYConfirmarAsync(ctx, Solicitud(ctx, ctx.IdArticulo2, 2m));
        var creada = await CrearYConfirmarAsync(ctx, Solicitud(ctx, ctx.IdArticulo, 10m));
        await EditarConExitoAsync(ctx, creada.Id, Solicitud(ctx, ctx.IdArticulo, 14m, creada.NumeroExterno));

        var respuesta = await ctx.Admin.GetAsync($"/api/proveedores/{ctx.IdProveedor}/cuenta-corriente?historico=true&tamanio=50");
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        var items = JsonDocument.Parse(cuerpo).RootElement.GetProperty("items").EnumerateArray().ToList();

        decimal? Pendiente(JsonElement f) =>
            f.TryGetProperty("saldoPendienteDeLaCompra", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;

        var deLaEditada = items.Where(f => f.GetProperty("idComprobanteCompra").GetInt32() == creada.Id).OrderBy(f => f.GetProperty("idMovimiento").GetInt32()).ToList();
        Assert.Equal(2, deLaEditada.Count);
        Assert.Null(Pendiente(deLaEditada[0]));
        Assert.Equal(14m * 121m, Pendiente(deLaEditada[1]));

        var deLaOtra = items.Single(f => f.GetProperty("idComprobanteCompra").GetInt32() == otra.Id);
        Assert.Equal(otra.Total, Pendiente(deLaOtra));
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    private static async Task EjecutarComoDuenoAsync(NpgsqlConnection conexion, string sql, DateOnly fecha, int id)
    {
        await using var comando = new NpgsqlCommand(sql, conexion);
        comando.Parameters.Add(new NpgsqlParameter { Value = fecha });
        comando.Parameters.Add(new NpgsqlParameter { Value = id });
        Assert.Equal(1, await comando.ExecuteNonQueryAsync());
    }

    /// <summary>Descuenta stock como lo haría una venta (movimiento + agregado + lote), sin pasar por el checkout.</summary>
    private async Task VenderAsync(Contexto ctx, int idArticulo, decimal cantidad, int? idLote)
    {
        await using var db = Db(ctx);
        var idEmpleado = await db.Usuarios.Select(u => u.Id).FirstAsync();

        db.MovimientosStock.Add(new MovimientoStock
        {
            IdTenant = ctx.IdTenant, IdArticulo = idArticulo, IdPuntoVenta = ctx.IdPuntoVenta, Cantidad = -cantidad,
            Motivo = MotivoStock.Venta, IdEmpleado = idEmpleado, CreadoEl = DateTimeOffset.UtcNow, IdLote = idLote
        });
        var stock = await db.Stock.FirstAsync(s => s.IdArticulo == idArticulo && s.IdPuntoVenta == ctx.IdPuntoVenta);
        stock.Cantidad -= cantidad;
        if (idLote is { } lote)
        {
            var delLote = await db.StockLotes.FirstAsync(s => s.IdLote == lote && s.IdPuntoVenta == ctx.IdPuntoVenta);
            delLote.Cantidad -= cantidad;
        }

        await db.SaveChangesAsync();
    }

    private static async Task<int> CrearYEnviarOrdenAsync(Contexto ctx)
    {
        var solicitud = new SolicitudDeOrdenDeCompra(
            ctx.IdProveedor, ctx.IdPuntoVenta, null, null,
            [new LineaDeOrdenSolicitada(ctx.IdArticulo, "Item de orden", 10m, 100m)]);
        var alta = await ctx.Admin.PostAsJsonAsync("/api/ordenes-compra", solicitud);
        var cuerpoAlta = await alta.Content.ReadAsStringAsync();
        Assert.True(alta.StatusCode == HttpStatusCode.Created, cuerpoAlta);
        var id = JsonDocument.Parse(cuerpoAlta).RootElement.GetProperty("id").GetInt32();

        var envio = await ctx.Admin.PostAsync($"/api/ordenes-compra/{id}/enviar", null);
        Assert.True(envio.StatusCode == HttpStatusCode.OK, await envio.Content.ReadAsStringAsync());
        return id;
    }

    private async Task<EstadoOrdenCompra> EstadoDeLaOrdenAsync(Contexto ctx, int idOrden)
    {
        await using var db = Db(ctx);
        return await db.OrdenesCompra.Where(o => o.Id == idOrden).Select(o => o.Estado).SingleAsync();
    }
}
