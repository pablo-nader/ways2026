using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Compras;
using Ways.Application.CuentaCorriente;
using Ways.Application.Organizacion;
using Ways.Application.Pagos;
using Ways.Application.Reportes;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Compras;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Organizacion;
using Ways.Domain.Pagos;
using Ways.Domain.Proveedores;
using Ways.Domain.Stock;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// Notas de crédito de compra (C-NCA/C-NCB/C-NCC, signo -1): se cargan con importes positivos y cada consumidor aplica el
/// signo. Cubre confirmar (stock, saldo, costos), la factura asociada, anular, el libro IVA, los listados y los reportes.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ComprasNotaDeCreditoTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, HttpClient Admin, int IdProveedor, int IdOtroProveedor, int IdArticulo,
        int IdAlicuotaIva21, int IdTipoFA, int IdTipoRM, int IdTipoNCA, int IdTipoNCB);

    private async Task<Contexto> PrepararAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var solicitud = new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin, ModoPuntoVenta.Web);
        var respuesta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "NC-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva21 = await db.AlicuotasIva.Where(a => a.Nombre == "21%").Select(a => a.Id).FirstAsync();

        var condicionFiscal = new CondicionFiscal { Codigo = $"{nombre}-CF", Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.CondicionesFiscales.Add(condicionFiscal);
        await db.SaveChangesAsync();

        var proveedor = new Proveedor
        {
            IdTenant = resultado.IdTenant, RazonSocial = nombre, IdCondicionFiscal = condicionFiscal.Id,
            Margen = 50m, CreatedAt = ahora, UpdatedAt = ahora
        };
        var otroProveedor = new Proveedor
        {
            IdTenant = resultado.IdTenant, RazonSocial = $"{nombre} otro", IdCondicionFiscal = condicionFiscal.Id,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Proveedores.AddRange(proveedor, otroProveedor);
        await db.SaveChangesAsync();

        var articulo = new Articulo
        {
            IdTenant = resultado.IdTenant, CodigoInterno = $"{nombre}-1-{Guid.NewGuid():N}", Nombre = "Articulo 1",
            IdArea = area.Id, IdAlicuotaIva = idAlicuotaIva21, UnidadVenta = UnidadVenta.Unidad, EsProducto = true,
            IdProveedorHabitual = proveedor.Id, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        async Task<int> TipoAsync(string codigo) =>
            await db.TiposComprobante.Where(t => t.Codigo == codigo).Select(t => t.Id).SingleAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, admin, proveedor.Id, otroProveedor.Id, articulo.Id,
            idAlicuotaIva21, await TipoAsync("C-FA"), await TipoAsync("C-RM"), await TipoAsync("C-NCA"), await TipoAsync("C-NCB"));
    }

    private static SolicitudDeCompra Factura(
        Contexto ctx, string numero = "0001-00000001", decimal unidades = 10m, decimal costo = 100m, int? idProveedor = null,
        int? idTipo = null) =>
        new(
            idProveedor ?? ctx.IdProveedor, idTipo ?? ctx.IdTipoFA, ctx.IdPuntoVenta, numero, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), null,
            [new LineaDeCompraSolicitada(ctx.IdArticulo, "Item", unidades, null, null, costo, 0m, ctx.IdAlicuotaIva21, true)]);

    private static SolicitudDeCompra Nota(
        Contexto ctx, int? asociada, string numero = "0001-00000050", decimal unidades = 4m, decimal costo = 150m,
        int? idProveedor = null, bool? actualizaCosto = null) =>
        new(
            idProveedor ?? ctx.IdProveedor, ctx.IdTipoNCA, ctx.IdPuntoVenta, numero, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), null,
            [new LineaDeCompraSolicitada(ctx.IdArticulo, "Devolución", unidades, null, null, costo, 0m, ctx.IdAlicuotaIva21, actualizaCosto)],
            IdComprobanteCompraAsociado: asociada);

    private static async Task<HttpResponseMessage> PostBorradorAsync(Contexto ctx, SolicitudDeCompra solicitud) =>
        await ctx.Admin.PostAsJsonAsync("/api/compras", solicitud);

    private static async Task<CompraDetalle> CrearBorradorAsync(Contexto ctx, SolicitudDeCompra solicitud)
    {
        var respuesta = await PostBorradorAsync(ctx, solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<CompraDetalle>(cuerpo, OpcionesJson)!;
    }

    private static async Task<CompraDetalle> ConfirmarAsync(Contexto ctx, int id)
    {
        var respuesta = await ctx.Admin.PostAsync($"/api/compras/{id}/confirmar", null);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<CompraDetalle>(cuerpo, OpcionesJson)!;
    }

    private static async Task<CompraDetalle> FacturaConfirmadaAsync(Contexto ctx, string numero = "0001-00000001") =>
        await ConfirmarAsync(ctx, (await CrearBorradorAsync(ctx, Factura(ctx, numero))).Id);

    private static async Task<string> CodigoDeRechazoAsync(HttpResponseMessage respuesta, HttpStatusCode esperado)
    {
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == esperado, cuerpo);
        return JsonDocument.Parse(cuerpo).RootElement.GetProperty("codigo").GetString()!;
    }

    private async Task<(decimal Stock, decimal? Costo, decimal Saldo)> EstadoAsync(Contexto ctx)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var idSucursal = await SucursalDePrueba.DeAsync(db, ctx.IdPuntoVenta);
        var stock = await db.Stock.Where(s => s.IdArticulo == ctx.IdArticulo && s.IdSucursal == idSucursal)
            .Select(s => (decimal?)s.Cantidad).FirstOrDefaultAsync() ?? 0m;
        var costo = await db.Articulos.Where(a => a.Id == ctx.IdArticulo).Select(a => a.CostoNominal).SingleAsync();
        var saldo = await db.Proveedores.Where(p => p.Id == ctx.IdProveedor).Select(p => p.Saldo).SingleAsync();
        return (stock, costo, saldo);
    }

    // ---- confirmar: stock, saldo y costos --------------------------------------------------------

    [Fact]
    public async Task UnaNotaDeCreditoConfirmadaSacaStockBajaElSaldoYNoTocaElCosto()
    {
        var ctx = await PrepararAsync(nameof(UnaNotaDeCreditoConfirmadaSacaStockBajaElSaldoYNoTocaElCosto));
        var factura = await FacturaConfirmadaAsync(ctx);
        var antes = await EstadoAsync(ctx);
        Assert.Equal(10m, antes.Stock);
        Assert.Equal(121m, antes.Costo);
        Assert.Equal(factura.Total, antes.Saldo);

        var borrador = await CrearBorradorAsync(ctx, Nota(ctx, factura.Id));
        Assert.Equal(factura.Id, borrador.IdComprobanteCompraAsociado);
        Assert.All(borrador.Items, i => Assert.False(i.ActualizaCosto));
        Assert.All(borrador.Items, i => Assert.Null(i.PrecioSugerido));

        var nota = await ConfirmarAsync(ctx, borrador.Id);

        Assert.Equal(EstadoCompra.Confirmada, nota.Estado);
        Assert.Equal(726m, nota.Total);
        Assert.Equal(factura.Id, nota.IdComprobanteCompraAsociado);
        Assert.Equal(0m, nota.SaldoPendiente);

        var despues = await EstadoAsync(ctx);
        Assert.Equal(6m, despues.Stock);
        Assert.Equal(factura.Total - nota.Total, despues.Saldo);
        Assert.Equal(121m, despues.Costo);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var movimientoDeStock = await db.MovimientosStock.SingleAsync(m => m.IdComprobanteCompra == nota.Id);
        Assert.Equal(MotivoStock.Compra, movimientoDeStock.Motivo);
        Assert.Equal(-4m, movimientoDeStock.Cantidad);

        var asiento = await db.MovimientosCuentaCorrienteProveedor.SingleAsync(m => m.IdComprobanteCompra == nota.Id);
        Assert.Equal(TipoMovimientoCcProveedor.Compra, asiento.Tipo);
        Assert.Equal(-nota.Total, asiento.Importe);
        Assert.Equal(despues.Saldo, asiento.SaldoResultante);
    }

    [Fact]
    public async Task ElCostoDeUnaNotaDeCreditoNoSeActualizaAunqueLaLineaLoPidaAlConfirmar()
    {
        // Cláusula bajo prueba: la guarda del paso 4 de EjecutarConfirmarAsync. Se baja DEBAJO de la guarda del guardado
        // (que fuerza actualiza_costo = false) escribiendo la línea con actualiza_costo verdadero por SQL: así solo la
        // guarda del confirmar puede impedir que el costo del artículo cambie.
        var ctx = await PrepararAsync(nameof(ElCostoDeUnaNotaDeCreditoNoSeActualizaAunqueLaLineaLoPidaAlConfirmar));
        var factura = await FacturaConfirmadaAsync(ctx);
        var borrador = await CrearBorradorAsync(ctx, Nota(ctx, factura.Id, costo: 999m));

        await using (var cruda = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant))
        {
            await using var comando = cruda.CreateCommand();
            comando.CommandText = "UPDATE items_comprobante_compra SET actualiza_costo = true WHERE id_comprobante_compra = $1";
            comando.Parameters.Add(new NpgsqlParameter { Value = borrador.Id });
            Assert.Equal(1, await comando.ExecuteNonQueryAsync());
        }

        await ConfirmarAsync(ctx, borrador.Id);

        Assert.Equal(121m, (await EstadoAsync(ctx)).Costo);
    }

    [Fact]
    public async Task UnaNotaDeCreditoNoAdmiteActualizaCostoVerdaderoNiOrdenDeCompra()
    {
        var ctx = await PrepararAsync(nameof(UnaNotaDeCreditoNoAdmiteActualizaCostoVerdaderoNiOrdenDeCompra));

        var conCosto = await PostBorradorAsync(ctx, Nota(ctx, null, actualizaCosto: true));
        Assert.Equal("nota_de_credito_no_actualiza_costo", await CodigoDeRechazoAsync(conCosto, HttpStatusCode.BadRequest));

        var conOrden = await PostBorradorAsync(ctx, Nota(ctx, null) with { IdOrdenCompra = 1 });
        Assert.Equal("nota_de_credito_sin_orden_de_compra", await CodigoDeRechazoAsync(conOrden, HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task UnaNotaDeCreditoSinFacturaAsociadaEsValida()
    {
        var ctx = await PrepararAsync(nameof(UnaNotaDeCreditoSinFacturaAsociadaEsValida));
        await FacturaConfirmadaAsync(ctx);

        var nota = await ConfirmarAsync(ctx, (await CrearBorradorAsync(ctx, Nota(ctx, null))).Id);

        Assert.Null(nota.IdComprobanteCompraAsociado);
        Assert.Equal(6m, (await EstadoAsync(ctx)).Stock);
    }

    [Fact]
    public async Task UnaNotaDeCreditoQueDejariaElStockNegativoEsRechazadaYNoCambiaNada()
    {
        var ctx = await PrepararAsync(nameof(UnaNotaDeCreditoQueDejariaElStockNegativoEsRechazadaYNoCambiaNada));
        var factura = await FacturaConfirmadaAsync(ctx);
        var borrador = await CrearBorradorAsync(ctx, Nota(ctx, factura.Id, unidades: 11m));

        var respuesta = await ctx.Admin.PostAsync($"/api/compras/{borrador.Id}/confirmar", null);

        Assert.Equal("nota_de_credito_stock_negativo", await CodigoDeRechazoAsync(respuesta, HttpStatusCode.Conflict));
        var estado = await EstadoAsync(ctx);
        Assert.Equal(10m, estado.Stock);
        Assert.Equal(factura.Total, estado.Saldo);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(EstadoCompra.Borrador, await db.ComprobantesCompra.Where(c => c.Id == borrador.Id).Select(c => c.Estado).SingleAsync());
    }

    // ---- factura asociada ------------------------------------------------------------------------

    [Fact]
    public async Task LaAsociacionAUnRemitoUnaNotaOUnaFacturaAjenaEsRechazada()
    {
        // Cláusula bajo prueba: ExigirFacturaAsociable al guardar el borrador (cada código es un conjunto propio).
        var ctx = await PrepararAsync(nameof(LaAsociacionAUnRemitoUnaNotaOUnaFacturaAjenaEsRechazada));

        var factura = await FacturaConfirmadaAsync(ctx);
        var remito = await ConfirmarAsync(ctx, (await CrearBorradorAsync(ctx, Factura(ctx, "R-1", idTipo: ctx.IdTipoRM))).Id);
        var otraNota = await ConfirmarAsync(ctx, (await CrearBorradorAsync(ctx, Nota(ctx, factura.Id, "0001-00000060", 1m))).Id);
        var facturaAjena = await ConfirmarAsync(
            ctx, (await CrearBorradorAsync(ctx, Factura(ctx, "0001-00000002", idProveedor: ctx.IdOtroProveedor))).Id);
        var facturaBorrador = await CrearBorradorAsync(ctx, Factura(ctx, "0001-00000003"));

        var facturaAnulada = await ConfirmarAsync(ctx, (await CrearBorradorAsync(ctx, Factura(ctx, "0001-00000004"))).Id);
        Assert.Equal(HttpStatusCode.OK, (await ctx.Admin.PostAsync($"/api/compras/{facturaAnulada.Id}/anular", null)).StatusCode);

        async Task<string> RechazoDeAsync(int idAsociada) =>
            await CodigoDeRechazoAsync(await PostBorradorAsync(ctx, Nota(ctx, idAsociada)), HttpStatusCode.BadRequest);

        Assert.Equal("compra_asociada_no_es_factura", await RechazoDeAsync(remito.Id));
        Assert.Equal("compra_asociada_no_es_factura", await RechazoDeAsync(otraNota.Id));
        Assert.Equal("compra_asociada_de_otro_proveedor", await RechazoDeAsync(facturaAjena.Id));
        Assert.Equal("compra_asociada_no_confirmada", await RechazoDeAsync(facturaBorrador.Id));
        Assert.Equal("compra_asociada_no_confirmada", await RechazoDeAsync(facturaAnulada.Id));
        Assert.Equal("referencia_invalida", await RechazoDeAsync(99_999_999));
    }

    [Fact]
    public async Task UnaFacturaNoLlevaAsociacion()
    {
        var ctx = await PrepararAsync(nameof(UnaFacturaNoLlevaAsociacion));
        var factura = await FacturaConfirmadaAsync(ctx);

        var respuesta = await PostBorradorAsync(ctx, Factura(ctx, "0001-00000009") with { IdComprobanteCompraAsociado = factura.Id });

        Assert.Equal("asociacion_solo_en_nota_de_credito", await CodigoDeRechazoAsync(respuesta, HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task ConfirmarUnaNotaRevalidaLaFacturaAsociadaBajoLock()
    {
        // Cláusula bajo prueba: la relectura FOR SHARE del paso 1.c de EjecutarConfirmarAsync. La factura se anula DESPUÉS
        // de guardar el borrador de la nota, así que el chequeo del guardado ya pasó y solo la relectura puede rechazarla.
        var ctx = await PrepararAsync(nameof(ConfirmarUnaNotaRevalidaLaFacturaAsociadaBajoLock));
        var factura = await FacturaConfirmadaAsync(ctx);
        var borrador = await CrearBorradorAsync(ctx, Nota(ctx, factura.Id));
        Assert.Equal(HttpStatusCode.OK, (await ctx.Admin.PostAsync($"/api/compras/{factura.Id}/anular", null)).StatusCode);

        var respuesta = await ctx.Admin.PostAsync($"/api/compras/{borrador.Id}/confirmar", null);

        Assert.Equal("compra_asociada_no_confirmada", await CodigoDeRechazoAsync(respuesta, HttpStatusCode.BadRequest));
        Assert.Equal(0m, (await EstadoAsync(ctx)).Stock);
    }

    [Fact]
    public async Task UnaFacturaInexistenteComoAsociadaViolaLaFkConSqlState23503()
    {
        var ctx = await PrepararAsync(nameof(UnaFacturaInexistenteComoAsociadaViolaLaFkConSqlState23503));
        var borrador = await CrearBorradorAsync(ctx, Nota(ctx, null));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "UPDATE comprobantes_compra SET id_comprobante_compra_asociado = 99999999 WHERE id_comprobante_compra = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = borrador.Id });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_comprobantes_compra_comprobante_asociado", excepcion.ConstraintName);
    }

    // ---- anular y editar -------------------------------------------------------------------------

    [Fact]
    public async Task AnularUnaNotaDeCreditoRestauraElStockYElSaldo()
    {
        var ctx = await PrepararAsync(nameof(AnularUnaNotaDeCreditoRestauraElStockYElSaldo));
        var factura = await FacturaConfirmadaAsync(ctx);
        var nota = await ConfirmarAsync(ctx, (await CrearBorradorAsync(ctx, Nota(ctx, factura.Id))).Id);

        var respuesta = await ctx.Admin.PostAsync($"/api/compras/{nota.Id}/anular", null);
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);

        var estado = await EstadoAsync(ctx);
        Assert.Equal(10m, estado.Stock);
        Assert.Equal(factura.Total, estado.Saldo);
        Assert.Equal(121m, estado.Costo);
    }

    [Fact]
    public async Task UnaFacturaConNotasConfirmadasNoSeAnulaHastaAnularLasNotas()
    {
        var ctx = await PrepararAsync(nameof(UnaFacturaConNotasConfirmadasNoSeAnulaHastaAnularLasNotas));
        var factura = await FacturaConfirmadaAsync(ctx);
        var nota = await ConfirmarAsync(ctx, (await CrearBorradorAsync(ctx, Nota(ctx, factura.Id))).Id);

        var rechazada = await ctx.Admin.PostAsync($"/api/compras/{factura.Id}/anular", null);
        Assert.Equal("compra_con_notas_de_credito", await CodigoDeRechazoAsync(rechazada, HttpStatusCode.Conflict));
        Assert.Equal(6m, (await EstadoAsync(ctx)).Stock);

        Assert.Equal(HttpStatusCode.OK, (await ctx.Admin.PostAsync($"/api/compras/{nota.Id}/anular", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ctx.Admin.PostAsync($"/api/compras/{factura.Id}/anular", null)).StatusCode);
    }

    [Fact]
    public async Task UnaNotaDeCreditoConfirmadaNoSeEditaNiUnaFacturaPasaASerNota()
    {
        var ctx = await PrepararAsync(nameof(UnaNotaDeCreditoConfirmadaNoSeEditaNiUnaFacturaPasaASerNota));
        var factura = await FacturaConfirmadaAsync(ctx);
        var nota = await ConfirmarAsync(ctx, (await CrearBorradorAsync(ctx, Nota(ctx, factura.Id))).Id);

        var editarNota = await ctx.Admin.PutAsJsonAsync($"/api/compras/{nota.Id}", Nota(ctx, factura.Id, unidades: 2m));
        Assert.Equal("nota_de_credito_no_editable", await CodigoDeRechazoAsync(editarNota, HttpStatusCode.Conflict));

        var facturaANota = await ctx.Admin.PutAsJsonAsync(
            $"/api/compras/{factura.Id}", Factura(ctx, "0001-00000001") with { IdTipoComprobante = ctx.IdTipoNCA });
        Assert.Equal(
            "compra_confirmada_no_puede_ser_nota_de_credito", await CodigoDeRechazoAsync(facturaANota, HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task UnaFacturaConNotasConfirmadasNoCambiaDeProveedorNiDeTipo()
    {
        var ctx = await PrepararAsync(nameof(UnaFacturaConNotasConfirmadasNoCambiaDeProveedorNiDeTipo));
        var factura = await FacturaConfirmadaAsync(ctx);
        await ConfirmarAsync(ctx, (await CrearBorradorAsync(ctx, Nota(ctx, factura.Id))).Id);

        var cambioDeProveedor = await ctx.Admin.PutAsJsonAsync(
            $"/api/compras/{factura.Id}", Factura(ctx, "0001-00000001", idProveedor: ctx.IdOtroProveedor));
        Assert.Equal("compra_con_notas_de_credito", await CodigoDeRechazoAsync(cambioDeProveedor, HttpStatusCode.Conflict));

        var cambioDeTipo = await ctx.Admin.PutAsJsonAsync(
            $"/api/compras/{factura.Id}", Factura(ctx, "0001-00000001", idTipo: ctx.IdTipoRM));
        Assert.Equal("compra_con_notas_de_credito", await CodigoDeRechazoAsync(cambioDeTipo, HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task UnaNotaDeCreditoNoAplicaPrecios()
    {
        var ctx = await PrepararAsync(nameof(UnaNotaDeCreditoNoAplicaPrecios));
        var factura = await FacturaConfirmadaAsync(ctx);
        var nota = await ConfirmarAsync(ctx, (await CrearBorradorAsync(ctx, Nota(ctx, factura.Id))).Id);

        var respuesta = await ctx.Admin.PostAsJsonAsync($"/api/compras/{nota.Id}/precios", new SolicitudDeAplicarPrecios(1));

        Assert.Equal("nota_de_credito_no_aplica_precios", await CodigoDeRechazoAsync(respuesta, HttpStatusCode.BadRequest));
    }

    // ---- consumidores ----------------------------------------------------------------------------

    [Fact]
    public async Task ElLibroIvaDeComprasListaLaNotaConImportesNegativos()
    {
        var ctx = await PrepararAsync(nameof(ElLibroIvaDeComprasListaLaNotaConImportesNegativos));
        var factura = await FacturaConfirmadaAsync(ctx);
        var nota = await ConfirmarAsync(ctx, (await CrearBorradorAsync(ctx, Nota(ctx, factura.Id))).Id);

        var hoy = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
        var respuesta = await ctx.Admin.GetAsync(
            $"/api/reportes/libro-iva-compras?desde={hoy:yyyy-MM-dd}&hasta={hoy:yyyy-MM-dd}&idEmpresa={ctx.IdEmpresa}");
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        var libro = JsonSerializer.Deserialize<LibroIva>(cuerpo, OpcionesJson)!;

        var filaDeLaNota = Assert.Single(libro.Filas, f => f.TipoComprobante == "C-NCA");
        Assert.Equal(-nota.Total, filaDeLaNota.Total);
        Assert.Equal(-600m, filaDeLaNota.NetoGravado);
        Assert.Equal(-126m, filaDeLaNota.IvaTotal);
        Assert.Equal(0m, filaDeLaNota.Diferencia);

        var filaDeLaFactura = Assert.Single(libro.Filas, f => f.TipoComprobante == "C-FA");
        Assert.Equal(factura.Total, filaDeLaFactura.Total);
        Assert.Equal(factura.Total - nota.Total, libro.Totales.Total);
    }

    [Fact]
    public async Task ElListadoYElSaldoNoTratanALaNotaComoUnaDeudaPagable()
    {
        var ctx = await PrepararAsync(nameof(ElListadoYElSaldoNoTratanALaNotaComoUnaDeudaPagable));
        var factura = await FacturaConfirmadaAsync(ctx);
        var nota = await ConfirmarAsync(ctx, (await CrearBorradorAsync(ctx, Nota(ctx, factura.Id))).Id);

        var listado = await ctx.Admin.GetFromJsonAsync<PaginaDeCompras>($"/api/compras?tamanio=50", OpcionesJson);
        var filaDeLaNota = Assert.Single(listado!.Items, c => c.Id == nota.Id);
        Assert.Equal(0m, filaDeLaNota.SaldoPendiente);
        Assert.Null(filaDeLaNota.EstadoPago);

        var pendientes = await ctx.Admin.GetFromJsonAsync<PaginaDeCompras>("/api/compras?tamanio=50&estadoPago=pendiente", OpcionesJson);
        Assert.DoesNotContain(pendientes!.Items, c => c.Id == nota.Id);
        Assert.Contains(pendientes.Items, c => c.Id == factura.Id);

        var saldo = await ctx.Admin.GetFromJsonAsync<SaldoDeProveedor>($"/api/proveedores/{ctx.IdProveedor}/saldo", OpcionesJson);
        Assert.Equal(factura.Total - nota.Total, saldo!.Saldo);
        Assert.DoesNotContain(saldo.Compras, c => c.IdComprobanteCompra == nota.Id);
    }

    [Fact]
    public async Task ElReporteDeComprasPorProveedorRestaLaNota()
    {
        var ctx = await PrepararAsync(nameof(ElReporteDeComprasPorProveedorRestaLaNota));
        var factura = await FacturaConfirmadaAsync(ctx);
        var nota = await ConfirmarAsync(ctx, (await CrearBorradorAsync(ctx, Nota(ctx, factura.Id))).Id);

        var hoy = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
        var respuesta = await ctx.Admin.GetAsync(
            $"/api/reportes/compras/por-proveedor?idEmpresa={ctx.IdEmpresa}&desde={hoy.AddDays(-1):yyyy-MM-dd}&hasta={hoy.AddDays(1):yyyy-MM-dd}");
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        var reporte = JsonSerializer.Deserialize<ComprasPorProveedor>(cuerpo, OpcionesJson)!;

        var fila = Assert.Single(reporte.PorProveedor, p => p.IdProveedor == ctx.IdProveedor);
        Assert.Equal(factura.Total - nota.Total, fila.Total);
        Assert.Equal(2, fila.CantidadCompras);
    }

    // ---- pagos y cuenta corriente ----------------------------------------------------------------

    private async Task<int> MedioEfectivoAsync(Contexto ctx)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        return await db.MediosPago.Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo).Select(m => m.Id).FirstAsync();
    }

    private async Task<(Contexto Ctx, CompraDetalle Nota, int IdMedio)> NotaConfirmadaAsync(string nombre)
    {
        var ctx = await PrepararAsync(nombre);
        var factura = await FacturaConfirmadaAsync(ctx);
        var nota = await ConfirmarAsync(ctx, (await CrearBorradorAsync(ctx, Nota(ctx, factura.Id))).Id);
        return (ctx, nota, await MedioEfectivoAsync(ctx));
    }

    [Fact]
    public async Task PagarUnaNotaDeCreditoEsRechazadoYNoCreaNingunPago()
    {
        // Cláusula bajo prueba: el rechazo de BloquearCompraAsync (único punto por el que pasan pagos, aplicaciones,
        // vínculos y redondeo contra una compra).
        var (ctx, nota, medio) = await NotaConfirmadaAsync(nameof(PagarUnaNotaDeCreditoEsRechazadoYNoCreaNingunPago));

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            $"/api/compras/{nota.Id}/pagos", new SolicitudDePagoDeCompra(DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), 100m, medio, null));

        Assert.Equal("compra_es_nota_de_credito", await CodigoDeRechazoAsync(respuesta, HttpStatusCode.BadRequest));
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.Pagos.CountAsync());
    }

    [Fact]
    public async Task UnPagoNoSeVinculaNiSeAplicaNiSeAltaContraUnaNotaDeCredito()
    {
        var (ctx, nota, medio) = await NotaConfirmadaAsync(nameof(UnPagoNoSeVinculaNiSeAplicaNiSeAltaContraUnaNotaDeCredito));

        SolicitudDePagoDeAdministracionConLineas Alta(int? idCompra) => PagoDeUnMedio.Administracion(
            DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), ctx.IdEmpresa, ctx.IdPuntoVenta, CategoriaPago.Proveedor, ctx.IdProveedor, null,
            "Pago de prueba", null, medio, null, 100m, idCompra);

        var altaLigada = await ctx.Admin.PostAsJsonAsync("/api/pagos/administracion", Alta(nota.Id));
        Assert.Equal("compra_es_nota_de_credito", await CodigoDeRechazoAsync(altaLigada, HttpStatusCode.BadRequest));

        var altaLibre = await ctx.Admin.PostAsJsonAsync("/api/pagos/administracion", Alta(null));
        Assert.Equal(HttpStatusCode.Created, altaLibre.StatusCode);
        var idPago = (await altaLibre.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var aplicar = await ctx.Admin.PostAsJsonAsync(
            $"/api/pagos/{idPago}/aplicaciones", new SolicitudDeAplicarPago([new AplicacionSolicitada(nota.Id, 50m)]));
        Assert.Equal("compra_es_nota_de_credito", await CodigoDeRechazoAsync(aplicar, HttpStatusCode.BadRequest));

        var vincular = await ctx.Admin.PostAsJsonAsync($"/api/pagos/{idPago}/vincular-compra", new SolicitudDeVincularCompra(nota.Id));
        Assert.Equal("compra_es_nota_de_credito", await CodigoDeRechazoAsync(vincular, HttpStatusCode.BadRequest));

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.AplicacionesPago.CountAsync());
    }

    [Fact]
    public async Task ElRedondeoDeUnaNotaDeCreditoEsRechazado()
    {
        var (ctx, nota, _) = await NotaConfirmadaAsync(nameof(ElRedondeoDeUnaNotaDeCreditoEsRechazado));

        var respuesta = await ctx.Admin.PostAsync($"/api/compras/{nota.Id}/ajuste-de-redondeo", null);

        Assert.Equal("compra_es_nota_de_credito", await CodigoDeRechazoAsync(respuesta, HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task ElEstadoDeCuentaMuestraLaNotaConSaldoPendienteCeroYLaFacturaConElSuyo()
    {
        var (ctx, nota, _) = await NotaConfirmadaAsync(nameof(ElEstadoDeCuentaMuestraLaNotaConSaldoPendienteCeroYLaFacturaConElSuyo));

        var respuesta = await ctx.Admin.GetAsync($"/api/proveedores/{ctx.IdProveedor}/cuenta-corriente");
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        var pagina = JsonSerializer.Deserialize<PaginaDeEstadoDeCuentaDeProveedor>(cuerpo, OpcionesJson)!;

        var filaDeLaNota = Assert.Single(pagina.Items, m => m.IdComprobanteCompra == nota.Id);
        Assert.Equal(-nota.Total, filaDeLaNota.Importe);
        Assert.Equal(0m, filaDeLaNota.SaldoPendienteDeLaCompra);

        var filaDeLaFactura = Assert.Single(pagina.Items, m => m.IdComprobanteCompra == nota.IdComprobanteCompraAsociado);
        Assert.True(filaDeLaFactura.SaldoPendienteDeLaCompra > 0m);
    }
}
