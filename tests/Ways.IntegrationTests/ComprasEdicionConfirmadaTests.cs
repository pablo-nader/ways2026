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

        var editada = await EditarConExitoAsync(
            ctx, creada.Id, Solicitud(ctx, ctx.IdArticuloConLote, 12m, creada.NumeroExterno, lote: "L-VIEJO", vencimiento: ayer));

        Assert.Equal(idLote, editada.Items.Single().IdLote);
        Assert.Equal([10m, 2m], (await MovimientosAsync(ctx, creada.Id)).Select(m => m.Cantidad));
        Assert.All(await MovimientosAsync(ctx, creada.Id), m => Assert.Equal(idLote, m.IdLote));

        var vencida = await EditarAsync(
            ctx, creada.Id,
            Solicitud(ctx, ctx.IdArticuloConLote, 12m, creada.NumeroExterno, lote: "L-NUEVO", vencimiento: ayer));
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

    // ---- TEMPORAL (E6-S3): pagos aplicados ---------------------------------------------------------------------

    /// <summary>TEMPORAL — E6-S3 reemplaza este rechazo por el tratamiento de las aplicaciones. Con la compra pagada en
    /// parte, bajar el total por debajo de lo pagado es 409 <c>compra_edicion_con_aplicaciones</c>; subirlo, o bajarlo sin
    /// pasar de lo pagado, se acepta.</summary>
    [Fact]
    public async Task BajarElTotalPorDebajoDeLoPagadoEs409ConAplicacionesYSubirloSeAcepta()
    {
        var ctx = await PrepararAsync(nameof(BajarElTotalPorDebajoDeLoPagadoEs409ConAplicacionesYSubirloSeAcepta));
        var creada = await CrearYConfirmarAsync(ctx, Solicitud(ctx, ctx.IdArticulo, 10m));
        var pago = await ctx.Admin.PostAsJsonAsync(
            $"/api/pagos/de-compra/{creada.Id}",
            new SolicitudDePagoDeCompraConLineas(
                DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), [new LineaDePago(ctx.IdEfectivo, 605m, null)], null));
        Assert.True(pago.StatusCode == HttpStatusCode.Created, await pago.Content.ReadAsStringAsync());

        var baja = await EditarAsync(ctx, creada.Id, Solicitud(ctx, ctx.IdArticulo, 4m, creada.NumeroExterno));
        Assert.Equal("compra_edicion_con_aplicaciones", await CodigoAsync(baja, HttpStatusCode.Conflict));
        Assert.Single(await MovimientosAsync(ctx, creada.Id));
        Assert.Equal(10m, await StockAsync(ctx, ctx.IdArticulo));

        var ajustada = await EditarConExitoAsync(ctx, creada.Id, Solicitud(ctx, ctx.IdArticulo, 5m, creada.NumeroExterno));
        Assert.Equal(605m, ajustada.Total);
        Assert.Equal(605m, ajustada.Pagado);

        var sube = await EditarConExitoAsync(ctx, creada.Id, Solicitud(ctx, ctx.IdArticulo, 8m, creada.NumeroExterno));
        Assert.Equal(8m * 121m, sube.Total);
        Assert.Equal(605m, sube.Pagado);
    }

    [Fact]
    public async Task CambiarElProveedorDeUnaCompraConPagosAplicadosEs409ConAplicaciones()
    {
        var ctx = await PrepararAsync(nameof(CambiarElProveedorDeUnaCompraConPagosAplicadosEs409ConAplicaciones));
        var creada = await CrearYConfirmarAsync(ctx, Solicitud(ctx, ctx.IdArticulo, 10m));
        var pago = await ctx.Admin.PostAsJsonAsync(
            $"/api/pagos/de-compra/{creada.Id}",
            new SolicitudDePagoDeCompraConLineas(
                DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), [new LineaDePago(ctx.IdEfectivo, 100m, null)], null));
        Assert.True(pago.StatusCode == HttpStatusCode.Created, await pago.Content.ReadAsStringAsync());

        var respuesta = await EditarAsync(
            ctx, creada.Id, Solicitud(ctx, ctx.IdArticulo, 10m, creada.NumeroExterno, idProveedor: ctx.IdProveedor2));

        Assert.Equal("compra_edicion_con_aplicaciones", await CodigoAsync(respuesta, HttpStatusCode.Conflict));
    }

    // ---- auditoría y estado de cuenta --------------------------------------------------------------------------

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
