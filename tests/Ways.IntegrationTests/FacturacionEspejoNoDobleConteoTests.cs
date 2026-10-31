using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Organizacion;
using Ways.Application.Reportes;
using Ways.Application.Usuarios;
using Ways.Application.Ventas;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Fiscal;
using Ways.Domain.Organizacion;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// Facturación por medio de pago, T3: un comprobante fiscal espejo (fila de <c>comprobantes_venta</c> con
/// <c>id_venta_origen</c>) representa la factura de una venta que ya está contada como ticket. Cada agregado
/// o listado de ventas la cuenta UNA sola vez; el libro IVA ventas, en cambio, tiene que seguir viendo la
/// factura. Se siembra directo (emitir una factura exige WSFE) y todas las fechas son fijas.
///
/// Mutation-proof-tests: la cláusula bajo prueba es <c>SinEspejosFiscales</c> en cada lector; sacarla de uno
/// pone en rojo la prueba de ese lector (evidencia en el reporte de la tarea).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class FacturacionEspejoNoDobleConteoTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static readonly DateOnly Desde = new(2026, 5, 1);
    private static readonly DateOnly Hasta = new(2026, 5, 31);
    private static readonly DateTimeOffset FechaVenta = new(2026, 5, 10, 12, 0, 0, TimeSpan.Zero);

    // El ticket con factura espejo y otro ticket común: con uno solo, sumar el espejo en lugar del ticket (un
    // predicado negado) daría los mismos totales que el correcto.
    private const decimal TotalVenta = 300m;
    private const decimal CantidadVendida = 3m;
    private const decimal TotalOtraVenta = 100m;
    private const decimal CantidadOtraVenta = 1m;
    private const decimal CostoUnitarioVenta = 40m;
    private const decimal TotalEsperado = TotalVenta + TotalOtraVenta;
    private const decimal CantidadEsperada = CantidadVendida + CantidadOtraVenta;

    private static long _numeroSecuencial = 1;

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, HttpClient Admin, int IdCliente, int IdEmpleado, int IdArea,
        int IdAlicuota, int IdListaPrecio, int IdMedioPago, int IdArticulo, int IdTipoTx, int IdTipoFa);

    private sealed record Sembrado(int IdVenta, int IdEspejo, int IdOtraVenta);

    private async Task<Contexto> PrepararAsync(string nombre)
    {
        var root = fixture.CreateClient();
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

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Area espejo", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        var lista = new ListaPrecio
        {
            IdTenant = resultado.IdTenant, Nombre = "Lista espejo", EsDefault = false, Modo = ModoLista.Fija,
            Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ListasPrecio.Add(lista);
        await db.SaveChangesAsync();

        var idAlicuota = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();
        var idCliente = await db.Clientes.Select(c => c.Id).FirstAsync();
        var idMedioPago = await db.MediosPago.OrderBy(m => m.Orden).Select(m => m.Id).FirstAsync();

        var articulo = new Articulo
        {
            IdTenant = resultado.IdTenant, CodigoInterno = $"espejo-{Guid.NewGuid():N}", Nombre = "Articulo espejo",
            IdArea = area.Id, IdAlicuotaIva = idAlicuota, UnidadVenta = UnidadVenta.Unidad, EsProducto = true,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        await using var dbPlataforma = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var idTipoTx = await dbPlataforma.TiposComprobante.Where(t => t.Codigo == "TX").Select(t => t.Id).SingleAsync();
        var idTipoFa = await dbPlataforma.TiposComprobante.Where(t => t.Codigo == "FA").Select(t => t.Id).SingleAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, admin, idCliente, resultado.IdUsuarioAdmin,
            area.Id, idAlicuota, lista.Id, idMedioPago, articulo.Id, idTipoTx, idTipoFa);
    }

    /// <summary>Un ticket TX con una línea y un pago, su factura espejo aprobada (línea idéntica; también un pago,
    /// que un espejo real no lleva, para que <c>por-medio-pago</c> distinga el predicado) y un segundo ticket común.</summary>
    private async Task<Sembrado> SembrarVentaConEspejoAsync(Contexto ctx)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        async Task<int> SembrarAsync(
            int idTipo, bool fiscal, int? idVentaOrigen, decimal total, decimal cantidad)
        {
            var comprobante = new ComprobanteVenta
            {
                IdTenant = ctx.IdTenant,
                IdTipoComprobante = idTipo,
                Numero = Interlocked.Increment(ref _numeroSecuencial),
                Fecha = FechaVenta,
                IdPuntoVenta = ctx.IdPuntoVenta,
                IdEmpleado = ctx.IdEmpleado,
                IdCliente = ctx.IdCliente,
                Subtotal = total,
                DescuentoTotal = 0m,
                Total = total,
                Estado = EstadoComprobante.Emitido,
                Cae = fiscal ? "12345678901234" : null,
                CaeVencimiento = fiscal ? new DateOnly(2026, 5, 20) : null,
                ResultadoFiscal = fiscal ? ResultadoFiscal.Aprobado : null,
                IdVentaOrigen = idVentaOrigen,
                CreatedAt = ahora,
                UpdatedAt = ahora
            };
            db.ComprobantesVenta.Add(comprobante);
            await db.SaveChangesAsync();

            db.ItemsComprobanteVenta.Add(new ItemComprobanteVenta
            {
                IdTenant = ctx.IdTenant, IdComprobanteVenta = comprobante.Id, Orden = 1, IdArticulo = ctx.IdArticulo,
                Descripcion = "Articulo espejo", IdArea = ctx.IdArea, IdListaPrecio = ctx.IdListaPrecio,
                IdAlicuotaIva = ctx.IdAlicuota, PorcentajeIva = 0m, Cantidad = cantidad,
                PrecioUnitario = total / cantidad, Descuento = 0m, Total = total,
                CostoUnitario = CostoUnitarioVenta, CostoEsEstimado = false, CreatedAt = ahora, UpdatedAt = ahora
            });
            db.Cobros.Add(new Cobro
            {
                IdTenant = ctx.IdTenant, IdComprobanteVenta = comprobante.Id, IdMedioPago = ctx.IdMedioPago,
                Importe = total, Vuelto = 0m, CreatedAt = ahora, UpdatedAt = ahora
            });
            await db.SaveChangesAsync();
            return comprobante.Id;
        }

        var idVenta = await SembrarAsync(ctx.IdTipoTx, fiscal: false, idVentaOrigen: null, TotalVenta, CantidadVendida);
        var idEspejo = await SembrarAsync(ctx.IdTipoFa, fiscal: true, idVentaOrigen: idVenta, TotalVenta, CantidadVendida);
        var idOtraVenta = await SembrarAsync(ctx.IdTipoTx, fiscal: false, idVentaOrigen: null, TotalOtraVenta, CantidadOtraVenta);
        return new Sembrado(idVenta, idEspejo, idOtraVenta);
    }

    private static async Task<T> LeerAsync<T>(HttpResponseMessage respuesta)
    {
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<T>(cuerpo, OpcionesJson)!;
    }

    private static string Rango => $"desde={Desde:yyyy-MM-dd}&hasta={Hasta:yyyy-MM-dd}";

    [Fact]
    public async Task ElResumenDeVentasCuentaLaVentaUnaSolaVezConSuEspejoFiscal()
    {
        var ctx = await PrepararAsync(nameof(ElResumenDeVentasCuentaLaVentaUnaSolaVezConSuEspejoFiscal));
        await SembrarVentaConEspejoAsync(ctx);

        var resumen = await LeerAsync<ResumenDeVentas>(await ctx.Admin.GetAsync(
            $"/api/reportes/ventas/resumen?idEmpresa={ctx.IdEmpresa}&{Rango}&granularidad=Dia"));

        Assert.Equal(TotalEsperado, resumen.NetoVendido);
        Assert.Equal(2, resumen.CantidadTx);
        Assert.Equal(TotalEsperado / 2, resumen.TicketPromedio);
    }

    [Fact]
    public async Task VentasPorPuntoDeVentaCuentaLaVentaUnaSolaVezConSuEspejoFiscal()
    {
        var ctx = await PrepararAsync(nameof(VentasPorPuntoDeVentaCuentaLaVentaUnaSolaVezConSuEspejoFiscal));
        await SembrarVentaConEspejoAsync(ctx);

        var ventas = await LeerAsync<VentasPorPuntoVenta>(await ctx.Admin.GetAsync(
            $"/api/reportes/ventas/por-punto-venta?idEmpresa={ctx.IdEmpresa}&{Rango}"));

        var fila = Assert.Single(ventas.Filas);
        Assert.Equal(TotalEsperado, fila.Neto);
        Assert.Equal(2, fila.CantidadTx);
    }

    [Fact]
    public async Task VentasPorVendedorCuentaLaVentaUnaSolaVezConSuEspejoFiscal()
    {
        var ctx = await PrepararAsync(nameof(VentasPorVendedorCuentaLaVentaUnaSolaVezConSuEspejoFiscal));
        await SembrarVentaConEspejoAsync(ctx);

        var ventas = await LeerAsync<VentasPorVendedor>(await ctx.Admin.GetAsync(
            $"/api/reportes/ventas/por-vendedor?idEmpresa={ctx.IdEmpresa}&{Rango}"));

        var fila = Assert.Single(ventas.Filas);
        Assert.Equal(TotalEsperado, fila.Neto);
        Assert.Equal(2, fila.CantidadTx);
    }

    [Fact]
    public async Task VentasPorMedioDePagoNoCuentaLosPagosDeUnEspejoFiscal()
    {
        var ctx = await PrepararAsync(nameof(VentasPorMedioDePagoNoCuentaLosPagosDeUnEspejoFiscal));
        await SembrarVentaConEspejoAsync(ctx);

        var ventas = await LeerAsync<VentasPorMedioPago>(await ctx.Admin.GetAsync(
            $"/api/reportes/ventas/por-medio-pago?idEmpresa={ctx.IdEmpresa}&{Rango}"));

        var fila = Assert.Single(ventas.Filas);
        Assert.Equal(TotalEsperado, fila.Neto);
        Assert.Equal(2, fila.CantidadPagos);
    }

    [Fact]
    public async Task ElTopDeArticulosNoDuplicaCantidadNiImporteConUnEspejoFiscal()
    {
        var ctx = await PrepararAsync(nameof(ElTopDeArticulosNoDuplicaCantidadNiImporteConUnEspejoFiscal));
        await SembrarVentaConEspejoAsync(ctx);

        var top = await LeerAsync<TopArticulos>(await ctx.Admin.GetAsync(
            $"/api/reportes/articulos/top?idEmpresa={ctx.IdEmpresa}&{Rango}"));

        var articulo = Assert.Single(top.Articulos);
        Assert.Equal(CantidadEsperada, articulo.Cantidad);
        Assert.Equal(TotalEsperado, articulo.Total);
    }

    [Fact]
    public async Task LaRentabilidadNoDuplicaVentaNiCostoConUnEspejoFiscal()
    {
        var ctx = await PrepararAsync(nameof(LaRentabilidadNoDuplicaVentaNiCostoConUnEspejoFiscal));
        await SembrarVentaConEspejoAsync(ctx);

        var rentabilidad = await LeerAsync<Rentabilidad>(await ctx.Admin.GetAsync(
            $"/api/reportes/rentabilidad?idEmpresa={ctx.IdEmpresa}&{Rango}"));

        Assert.Equal(TotalEsperado, rentabilidad.VentaConsiderada);
        Assert.Equal(CostoUnitarioVenta * CantidadEsperada, rentabilidad.CostoConsiderado);
        Assert.Equal(2, rentabilidad.Cobertura.LineasTotales);
    }

    [Fact]
    public async Task ElListadoDeVentasNoMuestraElEspejoFiscal()
    {
        var ctx = await PrepararAsync(nameof(ElListadoDeVentasNoMuestraElEspejoFiscal));
        var sembrado = await SembrarVentaConEspejoAsync(ctx);

        var listado = await LeerAsync<PaginaDe<ComprobanteListado>>(await ctx.Admin.GetAsync(
            $"/api/ventas?idPuntoVenta={ctx.IdPuntoVenta}&tamanio=50"));

        Assert.Equal(2, listado.Total);
        Assert.Equivalent(new[] { sembrado.IdVenta, sembrado.IdOtraVenta }, listado.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task ElLibroIvaDeVentasSigueViendoLaFacturaEspejo()
    {
        var ctx = await PrepararAsync(nameof(ElLibroIvaDeVentasSigueViendoLaFacturaEspejo));
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var puntoVenta = await db.PuntosVenta.SingleAsync(p => p.Id == ctx.IdPuntoVenta);
            puntoVenta.NumeroFiscal = 7;
            await db.SaveChangesAsync();
        }

        await SembrarVentaConEspejoAsync(ctx);

        var libro = await LeerAsync<LibroIva>(await ctx.Admin.GetAsync(
            $"/api/reportes/libro-iva-ventas?{Rango}&idEmpresa={ctx.IdEmpresa}"));

        var fila = Assert.Single(libro.Filas);
        Assert.Equal("FA", fila.TipoComprobante);
        Assert.Equal(TotalVenta, fila.Total);
    }

    [Fact]
    public async Task UnaDevolucionNoPuedeAsociarseAUnEspejoFiscal()
    {
        var ctx = await PrepararAsync(nameof(UnaDevolucionNoPuedeAsociarseAUnEspejoFiscal));
        var sembrado = await SembrarVentaConEspejoAsync(ctx);
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var ahora = DateTimeOffset.UtcNow;
            db.TurnosCaja.Add(new Ways.Domain.Caja.TurnoCaja
            {
                IdTenant = ctx.IdTenant, IdPuntoVenta = ctx.IdPuntoVenta, IdEmpleadoApertura = ctx.IdEmpleado,
                FechaApertura = ahora, FondoInicial = 0m, Estado = Ways.Domain.Caja.EstadoTurno.Abierto,
                CreatedAt = ahora, UpdatedAt = ahora
            });
            await db.SaveChangesAsync();
        }

        var solicitud = new SolicitudDeVenta(
            ctx.IdPuntoVenta, ctx.IdCliente, "NCX", sembrado.IdEspejo,
            [new LineaDeVenta(ctx.IdArticulo, 1m, null)], [], null, null);
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/ventas", solicitud);

        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.BadRequest, cuerpo);
        Assert.Equal(
            "comprobante_asociado_es_espejo_fiscal",
            JsonSerializer.Deserialize<JsonElement>(cuerpo).GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnEspejoFiscalRechazadoNoSeAnulaPorLaViaDeVentas()
    {
        var ctx = await PrepararAsync(nameof(UnEspejoFiscalRechazadoNoSeAnulaPorLaViaDeVentas));
        var sembrado = await SembrarVentaConEspejoAsync(ctx);
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var espejo = await db.ComprobantesVenta.SingleAsync(c => c.Id == sembrado.IdEspejo);
            espejo.ResultadoFiscal = ResultadoFiscal.Rechazado;
            espejo.Cae = null;
            espejo.CaeVencimiento = null;
            await db.SaveChangesAsync();
        }

        var respuesta = await ctx.Admin.PostAsync($"/api/ventas/{sembrado.IdEspejo}/anulacion", null);

        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Conflict, cuerpo);
        Assert.Equal(
            "comprobante_es_espejo_fiscal",
            JsonSerializer.Deserialize<JsonElement>(cuerpo).GetProperty("codigo").GetString());

        await using var verificacion = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(
            EstadoComprobante.Emitido,
            await verificacion.ComprobantesVenta.Where(c => c.Id == sembrado.IdEspejo).Select(c => c.Estado).SingleAsync());
    }

    [Fact]
    public async Task AnularUnComprobanteInexistenteSigueDandoNoEncontrado()
    {
        var ctx = await PrepararAsync(nameof(AnularUnComprobanteInexistenteSigueDandoNoEncontrado));

        var respuesta = await ctx.Admin.PostAsync("/api/ventas/2000000000/anulacion", null);

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }
}
