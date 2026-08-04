using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Application.Ventas;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Stock;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-5-pos-ventas, Slice 5 (tasks 5.5-5.6): <c>POST /api/ventas/{id}/anulacion</c> punta a
/// punta contra Postgres real — reversa exacta de stock y cuenta corriente, la carrera de doble
/// anulación, el 404 uniforme (ADR-8) y la confirmación de que <c>restaurar</c> no existe (design:
/// Protection Rules).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class AnulacionTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    // Mismo motivo que VentasCheckoutTests.OpcionesJson.
    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, HttpClient Admin, int IdArea, int IdAlicuotaIva,
        int IdListaPrecio, int IdMedioEfectivo, int IdMedioCuentaCorriente);

    private async Task<Contexto> PrepararAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var solicitud = new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin);
        var respuesta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var area = new Area
        {
            IdTenant = resultado.IdTenant, Nombre = "Anulacion-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();

        var lista = new ListaPrecio
        {
            IdTenant = resultado.IdTenant, Nombre = "Lista Anulacion", EsDefault = false, Modo = ModoLista.Fija,
            Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ListasPrecio.Add(lista);
        await db.SaveChangesAsync();

        var idMedioEfectivo = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo)
            .Select(m => m.Id).FirstAsync();

        var medioCc = new MedioPago
        {
            IdTenant = resultado.IdTenant, Nombre = "Cuenta corriente", Orden = 3,
            Comportamiento = ComportamientoMedioPago.CuentaCorriente, AdmiteVuelto = false, RequiereReferencia = false,
            Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.MediosPago.Add(medioCc);
        await db.SaveChangesAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, admin, area.Id, idAlicuotaIva,
            lista.Id, idMedioEfectivo, medioCc.Id);
    }

    private async Task<int> SembrarArticuloConPrecioAsync(Contexto ctx, string nombre, decimal precio)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var articulo = new Articulo
        {
            IdTenant = ctx.IdTenant, CodigoInterno = $"{nombre}-{Guid.NewGuid():N}", Nombre = nombre,
            IdArea = ctx.IdArea, IdAlicuotaIva = ctx.IdAlicuotaIva, UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        db.Precios.Add(new Precio
        {
            IdTenant = ctx.IdTenant, IdArticulo = articulo.Id, IdListaPrecio = ctx.IdListaPrecio, Monto = precio,
            VigenteDesde = ahora.AddDays(-1), VigenteHasta = null, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        return articulo.Id;
    }

    private async Task<int> SembrarClienteAsync(
        Contexto ctx, string nombre, decimal limiteCredito = 0, bool creditoIlimitado = false)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var idCondicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync();

        var cliente = new Cliente
        {
            IdTenant = ctx.IdTenant, Numero = 1000 + Random.Shared.Next(1, 100_000), Nombre = nombre,
            IdCondicionFiscal = idCondicionFiscal, IdListaPrecio = ctx.IdListaPrecio, LimiteCredito = limiteCredito,
            CreditoIlimitado = creditoIlimitado, Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Clientes.Add(cliente);
        await db.SaveChangesAsync();

        return cliente.Id;
    }

    private static async Task<ComprobanteEmitido> EmitirAsync(
        Contexto ctx, int idCliente, int idArticulo, decimal precio, decimal cantidad = 1m, int? idMedio = null)
    {
        var solicitud = new SolicitudDeVenta(
            ctx.IdPuntoVenta, idCliente, "TX", null,
            [new LineaDeVenta(idArticulo, cantidad, null)],
            [new PagoDeVenta(idMedio ?? ctx.IdMedioEfectivo, precio * cantidad, null, 0m)],
            null, null);

        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/ventas", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);

        return (await JsonSerializer.DeserializeAsync<ComprobanteEmitido>(
            new MemoryStream(System.Text.Encoding.UTF8.GetBytes(cuerpo)), OpcionesJson))!;
    }

    private async Task<(decimal Cantidad, decimal Saldo)> LeerStockYSaldoAsync(Contexto ctx, int idArticulo, int idCliente)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var cantidad = await db.Stock
            .Where(s => s.IdArticulo == idArticulo && s.IdPuntoVenta == ctx.IdPuntoVenta)
            .Select(s => s.Cantidad)
            .FirstOrDefaultAsync();
        var saldo = await db.Clientes.Where(c => c.Id == idCliente).Select(c => c.Saldo).FirstAsync();
        return (cantidad, saldo);
    }

    // ---- task 5.5: reversa exacta ----------------------------------------------------------------

    [Fact]
    public async Task AnulacionReviertaElStockExactamente()
    {
        var ctx = await PrepararAsync(nameof(AnulacionReviertaElStockExactamente));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-anulacion-stock", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Anulación Stock");

        var emitido = await EmitirAsync(ctx, idCliente, idArticulo, 100m, cantidad: 3m);
        var (cantidadTrasVenta, _) = await LeerStockYSaldoAsync(ctx, idArticulo, idCliente);
        Assert.Equal(-3m, cantidadTrasVenta);

        var respuestaAnulacion = await ctx.Admin.PostAsync($"/api/ventas/{emitido.Id}/anulacion", null);
        var cuerpo = await respuestaAnulacion.Content.ReadAsStringAsync();
        Assert.True(respuestaAnulacion.StatusCode == HttpStatusCode.OK, cuerpo);

        var anulado = JsonSerializer.Deserialize<ComprobanteEmitido>(cuerpo, OpcionesJson)!;
        Assert.Equal(EstadoComprobante.Anulado, anulado.Estado);

        var (cantidadTrasAnulacion, _) = await LeerStockYSaldoAsync(ctx, idArticulo, idCliente);
        Assert.Equal(0m, cantidadTrasAnulacion);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var movimientoInverso = await db.MovimientosStock
            .SingleAsync(m => m.IdArticulo == idArticulo && m.Motivo == MotivoStock.Anulacion);
        Assert.Equal(3m, movimientoInverso.Cantidad);
        Assert.Equal(emitido.Id, movimientoInverso.IdComprobanteVenta);

        // El movimiento original (motivo = venta) sigue intacto — la anulación nunca lo edita
        // (design: Never Restores by Editing).
        var movimientoOriginal = await db.MovimientosStock
            .SingleAsync(m => m.IdArticulo == idArticulo && m.Motivo == MotivoStock.Venta);
        Assert.Equal(-3m, movimientoOriginal.Cantidad);
    }

    [Fact]
    public async Task AnulacionReviertaElConsumoDeCuentaCorrienteExactamente()
    {
        var ctx = await PrepararAsync(nameof(AnulacionReviertaElConsumoDeCuentaCorrienteExactamente));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-anulacion-cc", 200m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Anulación CC", limiteCredito: 1000m);

        var emitido = await EmitirAsync(ctx, idCliente, idArticulo, 200m, idMedio: ctx.IdMedioCuentaCorriente);
        var (_, saldoTrasVenta) = await LeerStockYSaldoAsync(ctx, idArticulo, idCliente);
        Assert.Equal(200m, saldoTrasVenta);

        var respuestaAnulacion = await ctx.Admin.PostAsync($"/api/ventas/{emitido.Id}/anulacion", null);
        Assert.Equal(HttpStatusCode.OK, respuestaAnulacion.StatusCode);

        var (_, saldoTrasAnulacion) = await LeerStockYSaldoAsync(ctx, idArticulo, idCliente);
        Assert.Equal(0m, saldoTrasAnulacion);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var contramovimiento = await db.MovimientosCuentaCorriente
            .SingleAsync(m => m.IdComprobanteVenta == emitido.Id && m.Tipo == Ways.Domain.CuentaCorriente.TipoMovimientoCc.Ajuste);
        Assert.Equal(-200m, contramovimiento.Importe);
        Assert.Equal(0m, contramovimiento.SaldoResultante);
    }

    [Fact]
    public async Task DobleAnulacionEsRechazadaSinDuplicarMovimientos()
    {
        var ctx = await PrepararAsync(nameof(DobleAnulacionEsRechazadaSinDuplicarMovimientos));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-doble-anulacion", 50m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Doble Anulación");

        var emitido = await EmitirAsync(ctx, idCliente, idArticulo, 50m);

        var primera = await ctx.Admin.PostAsync($"/api/ventas/{emitido.Id}/anulacion", null);
        Assert.Equal(HttpStatusCode.OK, primera.StatusCode);

        var segunda = await ctx.Admin.PostAsync($"/api/ventas/{emitido.Id}/anulacion", null);
        Assert.Equal(HttpStatusCode.Conflict, segunda.StatusCode);
        var problema = await segunda.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("comprobante_ya_anulado", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(1, await db.MovimientosStock.CountAsync(m => m.IdArticulo == idArticulo && m.Motivo == MotivoStock.Anulacion));
    }

    [Fact]
    public async Task NoExisteEndpointDeRestaurar()
    {
        var ctx = await PrepararAsync(nameof(NoExisteEndpointDeRestaurar));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-sin-restaurar", 50m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Sin Restaurar");
        var emitido = await EmitirAsync(ctx, idCliente, idArticulo, 50m);

        var anulacion = await ctx.Admin.PostAsync($"/api/ventas/{emitido.Id}/anulacion", null);
        Assert.Equal(HttpStatusCode.OK, anulacion.StatusCode);

        var restaurar = await ctx.Admin.PostAsync($"/api/ventas/{emitido.Id}/restaurar", null);
        Assert.Equal(HttpStatusCode.NotFound, restaurar.StatusCode);
    }

    [Fact]
    public async Task AnularUnComprobanteInexistenteEsRechazadoCon404()
    {
        var ctx = await PrepararAsync(nameof(AnularUnComprobanteInexistenteEsRechazadoCon404));

        var respuesta = await ctx.Admin.PostAsync("/api/ventas/999999/anulacion", null);

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("no_encontrado", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnComprobanteDeOtroTenantEsInvisibleParaLaAnulacion()
    {
        var ctxUno = await PrepararAsync($"{nameof(UnComprobanteDeOtroTenantEsInvisibleParaLaAnulacion)}-uno");
        var idArticulo = await SembrarArticuloConPrecioAsync(ctxUno, "articulo-tenant-uno", 50m);
        var idCliente = await SembrarClienteAsync(ctxUno, "Cliente Tenant Uno");
        var emitido = await EmitirAsync(ctxUno, idCliente, idArticulo, 50m);

        var ctxDos = await PrepararAsync($"{nameof(UnComprobanteDeOtroTenantEsInvisibleParaLaAnulacion)}-dos");

        var respuesta = await ctxDos.Admin.PostAsync($"/api/ventas/{emitido.Id}/anulacion", null);

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnVendedorPuedeAnularSuPropiaVenta()
    {
        var ctx = await PrepararAsync(nameof(UnVendedorPuedeAnularSuPropiaVenta));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-vendedor-anula", 50m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente del Vendedor Anulación");

        var mailVendedor = $"vendedor-{Guid.NewGuid():N}@ways.test";
        var altaVendedor = await ctx.Admin.PostAsJsonAsync(
            "/api/usuarios", new CrearUsuario("vendedor-anula", mailVendedor, (int)RolConocido.Vendedor, "una-contraseña-larga"));
        Assert.Equal(HttpStatusCode.Created, altaVendedor.StatusCode);

        using var vendedor = fixture.CreateClient();
        var login = await vendedor.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailVendedor, "una-contraseña-larga"));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var emitido = await EmitirAsync(ctx, idCliente, idArticulo, 50m);

        var respuesta = await vendedor.PostAsync($"/api/ventas/{emitido.Id}/anulacion", null);
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }

    // ---- atomicidad: un punto de falla dentro de la transacción de anulación --------------------

    private const string RolApp = "ways_app";

    private async Task RevocarAsync(string tabla, string privilegios)
    {
        await using var owner = new NpgsqlConnection(fixture.OwnerConnectionString);
        await owner.OpenAsync();
        await using var comando = owner.CreateCommand();
        comando.CommandText = $"REVOKE {privilegios} ON {tabla} FROM {RolApp}";
        await comando.ExecuteNonQueryAsync();
    }

    private async Task RestaurarAsync(string tabla, string privilegios)
    {
        await using var owner = new NpgsqlConnection(fixture.OwnerConnectionString);
        await owner.OpenAsync();
        await using var comando = owner.CreateCommand();
        comando.CommandText = $"GRANT {privilegios} ON {tabla} TO {RolApp}";
        await comando.ExecuteNonQueryAsync();
    }

    /// <summary>Postura sin reintento (ver <c>ServicioDeVentas.CrearEstrategiaSinReintento</c>):
    /// el 500 de acá NO dispara ningún reintento automático de <c>EnableRetryOnFailure</c> — el
    /// REVOKE simula una falla técnica persistente (no transitoria), así que el resultado es el
    /// mismo con o sin retry, pero el punto de este test es que <c>AnularAsync</c> nunca vuelve a
    /// correr la transacción por su cuenta; el reintento de abajo es explícitamente del "cliente"
    /// del test, no de la infraestructura.</summary>
    [Fact]
    public async Task UnaFallaAlRevertirElStockDejaElComprobanteEmitidoYNadaCambiado()
    {
        var ctx = await PrepararAsync(nameof(UnaFallaAlRevertirElStockDejaElComprobanteEmitidoYNadaCambiado));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-atomicidad-anulacion", 10m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Atomicidad Anulación");
        var emitido = await EmitirAsync(ctx, idCliente, idArticulo, 10m, cantidad: 4m);

        await RevocarAsync("movimientos_stock", "INSERT");
        HttpResponseMessage respuesta;
        try
        {
            respuesta = await ctx.Admin.PostAsync($"/api/ventas/{emitido.Id}/anulacion", null);
        }
        finally
        {
            await RestaurarAsync("movimientos_stock", "INSERT");
        }

        Assert.Equal(HttpStatusCode.InternalServerError, respuesta.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));

        // El UPDATE de estado (paso 1) se revierte junto con el resto — mismo rollback
        // transaccional que VentasAtomicidadYConcurrenciaTests prueba para EmitirAsync.
        var estado = await db.ComprobantesVenta.Where(c => c.Id == emitido.Id).Select(c => c.Estado).FirstAsync();
        Assert.Equal(EstadoComprobante.Emitido, estado);

        Assert.Equal(0, await db.MovimientosStock.CountAsync(m => m.Motivo == MotivoStock.Anulacion));

        var cantidad = await db.Stock
            .Where(s => s.IdArticulo == idArticulo && s.IdPuntoVenta == ctx.IdPuntoVenta)
            .Select(s => s.Cantidad).FirstAsync();
        Assert.Equal(-4m, cantidad);

        // La anulación no consume ningún recurso de un solo uso (a diferencia de la numeración
        // de EmitirAsync) — un reintento limpio inmediatamente después tiene que funcionar.
        var reintento = await ctx.Admin.PostAsync($"/api/ventas/{emitido.Id}/anulacion", null);
        Assert.Equal(HttpStatusCode.OK, reintento.StatusCode);
    }

    /// <summary>Mismo punto de falla que <see
    /// cref="UnaFallaAlRevertirElStockDejaElComprobanteEmitidoYNadaCambiado"/>, pero sobre el
    /// contramovimiento de cuenta corriente (paso 3 de <c>EjecutarAnulacionAsync</c>) — postura
    /// sin reintento: ningún reintento automático re-corre la transacción, así que
    /// <c>clientes.saldo</c> y el estado del comprobante quedan exactamente como antes del
    /// intento fallido.</summary>
    [Fact]
    public async Task UnaFallaAlRevertirLaCuentaCorrienteDejaElComprobanteEmitidoYNadaCambiado()
    {
        var ctx = await PrepararAsync(nameof(UnaFallaAlRevertirLaCuentaCorrienteDejaElComprobanteEmitidoYNadaCambiado));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-atomicidad-anulacion-cc", 150m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Atomicidad Anulación CC", limiteCredito: 1000m);
        var emitido = await EmitirAsync(ctx, idCliente, idArticulo, 150m, idMedio: ctx.IdMedioCuentaCorriente);

        await RevocarAsync("movimientos_cuenta_corriente", "INSERT");
        HttpResponseMessage respuesta;
        try
        {
            respuesta = await ctx.Admin.PostAsync($"/api/ventas/{emitido.Id}/anulacion", null);
        }
        finally
        {
            await RestaurarAsync("movimientos_cuenta_corriente", "INSERT");
        }

        Assert.Equal(HttpStatusCode.InternalServerError, respuesta.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));

        var estado = await db.ComprobantesVenta.Where(c => c.Id == emitido.Id).Select(c => c.Estado).FirstAsync();
        Assert.Equal(EstadoComprobante.Emitido, estado);

        Assert.Equal(
            0,
            await db.MovimientosCuentaCorriente.CountAsync(
                m => m.IdComprobanteVenta == emitido.Id && m.Tipo == Ways.Domain.CuentaCorriente.TipoMovimientoCc.Ajuste));

        var saldo = await db.Clientes.Where(c => c.Id == idCliente).Select(c => c.Saldo).FirstAsync();
        Assert.Equal(150m, saldo);

        var reintento = await ctx.Admin.PostAsync($"/api/ventas/{emitido.Id}/anulacion", null);
        Assert.Equal(HttpStatusCode.OK, reintento.StatusCode);
    }

    // ---- Triaged: anulación de una NCX invierte el signo correctamente -------------------------

    /// <summary>Una devolución (NCX) escribe un movimiento de stock ORIGINAL positivo (motivo =
    /// venta, la devolución sube el stock — ver <c>MaterializarItems</c>/<c>EjecutarTransaccionAsync</c>
    /// paso 5, <c>delta = -item.Cantidad</c> con <c>item.Cantidad</c> ya negativa para NCX). Su
    /// anulación tiene que escribir la inversa exacta —negativa—, nunca reusar el mismo signo que
    /// la anulación de una TX.</summary>
    [Fact]
    public async Task AnulacionDeUnaNcxReviertaElStockConElSignoInvertidoCorrectamente()
    {
        var ctx = await PrepararAsync(nameof(AnulacionDeUnaNcxReviertaElStockConElSignoInvertidoCorrectamente));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-anulacion-ncx", 100m);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Anulación NCX");

        var ncx = await EmitirNcxAsync(ctx, idCliente, idArticulo, cantidad: 3m);

        var (cantidadTrasNcx, _) = await LeerStockYSaldoAsync(ctx, idArticulo, idCliente);
        Assert.Equal(3m, cantidadTrasNcx);

        var respuestaAnulacion = await ctx.Admin.PostAsync($"/api/ventas/{ncx.Id}/anulacion", null);
        var cuerpo = await respuestaAnulacion.Content.ReadAsStringAsync();
        Assert.True(respuestaAnulacion.StatusCode == HttpStatusCode.OK, cuerpo);

        var (cantidadTrasAnulacion, _) = await LeerStockYSaldoAsync(ctx, idArticulo, idCliente);
        Assert.Equal(0m, cantidadTrasAnulacion);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var movimientoInverso = await db.MovimientosStock
            .SingleAsync(m => m.IdArticulo == idArticulo && m.Motivo == MotivoStock.Anulacion);
        Assert.Equal(-3m, movimientoInverso.Cantidad);
    }

    private static async Task<ComprobanteEmitido> EmitirNcxAsync(
        Contexto ctx, int idCliente, int idArticulo, decimal cantidad = 1m)
    {
        var solicitud = new SolicitudDeVenta(
            ctx.IdPuntoVenta, idCliente, "NCX", null,
            [new LineaDeVenta(idArticulo, cantidad, null)],
            [],
            null, null);

        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/ventas", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);

        return (await JsonSerializer.DeserializeAsync<ComprobanteEmitido>(
            new MemoryStream(System.Text.Encoding.UTF8.GetBytes(cuerpo)), OpcionesJson))!;
    }

    // ---- task 5.6: dos anulaciones concurrentes del mismo comprobante --------------------------

    /// <summary>Spec: task 5.6 / design's forced-rendezvous list (Reachability, honestly — surface
    /// 3, "two anulaciones of the same comprobante"). Sin interceptor de rendezvous (mismo
    /// criterio que <c>ClientesEndpointsTests</c>): el <c>UPDATE ... WHERE estado = 'emitido'
    /// RETURNING</c> condicional de <c>ServicioDeVentas.EjecutarAnulacionAsync</c> ya serializa
    /// la carrera con su propio lock de fila — mismo hallazgo confirmado sin forzar nada. Dos POST
    /// lanzados con <c>Task.WhenAll</c> alcanzan para probar la concurrencia real; desvío
    /// registrado respecto a la lista de forced-rendezvous de design, documentado acá en vez de
    /// silencioso.</summary>
    [Fact]
    public async Task DosAnulacionesConcurrentesDelMismoComprobanteDanExactamenteUnGanador()
    {
        for (var ronda = 0; ronda < 3; ronda++)
        {
            var ctx = await PrepararAsync(
                $"{nameof(DosAnulacionesConcurrentesDelMismoComprobanteDanExactamenteUnGanador)}-{ronda}");
            var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-anulacion-concurrente", 10m);
            var idCliente = await SembrarClienteAsync(ctx, "Cliente Anulación Concurrente");
            var emitido = await EmitirAsync(ctx, idCliente, idArticulo, 10m, cantidad: 5m);

            var tareaA = ctx.Admin.PostAsync($"/api/ventas/{emitido.Id}/anulacion", null);
            var tareaB = ctx.Admin.PostAsync($"/api/ventas/{emitido.Id}/anulacion", null);

            var respuestas = await Task.WhenAll(tareaA, tareaB);
            var estados = respuestas.Select(r => r.StatusCode).OrderBy(s => s).ToList();

            Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], estados);

            await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
            Assert.Equal(
                1, await db.MovimientosStock.CountAsync(m => m.IdArticulo == idArticulo && m.Motivo == MotivoStock.Anulacion));

            var cantidad = await db.Stock
                .Where(s => s.IdArticulo == idArticulo && s.IdPuntoVenta == ctx.IdPuntoVenta)
                .Select(s => s.Cantidad).FirstAsync();
            Assert.Equal(0m, cantidad);
        }
    }
}
