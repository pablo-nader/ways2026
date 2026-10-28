using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.Compras;
using Ways.Application.Organizacion;
using Ways.Application.Pagos;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Pagos;
using Ways.Domain.Proveedores;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// E2-S4: contrato HTTP <c>/api/pagos</c> con líneas por medio, a la par de <c>/api/gastos</c> (que sigue de un solo medio).
/// Cada ruta nueva se ejerce por HTTP con el rol que su gemela de gastos exige.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class PagosApiConLineasTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordVendedor = "una-contraseña-larga";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, HttpClient Admin, string Nombre, int IdProveedor, int IdArea,
        int IdEfectivo, int IdElectronico, int IdTercero, int IdCuentaCorriente);

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

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var idEfectivo = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo).Select(m => m.Id).FirstAsync();
        var idElectronico = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Electronico).Select(m => m.Id).FirstAsync();
        var tercero = NuevoMedio(resultado.IdTenant, $"{nombre}-tercero", 50, ComportamientoMedioPago.Electronico, ahora);
        var cuentaCorriente = NuevoMedio(resultado.IdTenant, $"{nombre}-cc", 60, ComportamientoMedioPago.CuentaCorriente, ahora);
        db.MediosPago.AddRange(tercero, cuentaCorriente);

        await using var plataforma = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var area = new Area { IdTenant = resultado.IdTenant, Nombre = $"{nombre}-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        var condicion = new CondicionFiscal { Codigo = $"{nombre}-CF", Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        plataforma.Areas.Add(area);
        plataforma.CondicionesFiscales.Add(condicion);
        await plataforma.SaveChangesAsync();
        var proveedor = new Proveedor
        {
            IdTenant = resultado.IdTenant, RazonSocial = nombre, IdCondicionFiscal = condicion.Id,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        plataforma.Proveedores.Add(proveedor);
        await plataforma.SaveChangesAsync();
        await db.SaveChangesAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, admin, nombre, proveedor.Id, area.Id,
            idEfectivo, idElectronico, tercero.Id, cuentaCorriente.Id);
    }

    private static MedioPago NuevoMedio(int idTenant, string nombre, int orden, ComportamientoMedioPago comportamiento, DateTimeOffset ahora) =>
        new()
        {
            IdTenant = idTenant, Nombre = nombre, Orden = orden, Comportamiento = comportamiento, AdmiteVuelto = false,
            RequiereReferencia = false, CreatedAt = ahora, UpdatedAt = ahora
        };

    private WaysDbContext Db(Contexto ctx) =>
        fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));

    private async Task<HttpClient> CrearVendedorAsync(Contexto ctx)
    {
        var mail = $"{ctx.Nombre.ToLowerInvariant()}-vendedor@ways.test";
        var alta = await ctx.Admin.PostAsJsonAsync(
            "/api/usuarios", new CrearUsuario("vendedor-pagos", mail, (int)RolConocido.Vendedor, PasswordVendedor));
        Assert.True(alta.StatusCode == HttpStatusCode.Created, await alta.Content.ReadAsStringAsync());

        var vendedor = fixture.CreateClient();
        var login = await vendedor.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, PasswordVendedor));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return vendedor;
    }

    private static async Task<int> AbrirTurnoAsync(Contexto ctx, decimal fondoInicial)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/caja/turnos", new SolicitudDeApertura(ctx.IdPuntoVenta, fondoInicial, null));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<TurnoResumen>(cuerpo, OpcionesJson)!.Id;
    }

    private static async Task CerrarTurnoAsync(Contexto ctx, int idTurno, params ConteoDeclarado[] conteos)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync(
            $"/api/caja/turnos/{idTurno}/cierre", new SolicitudDeCierre(conteos, null));
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, await respuesta.Content.ReadAsStringAsync());
    }

    private static SolicitudDePagoConLineas AltaPos(Contexto ctx, params LineaDePago[] lineas) =>
        new(ctx.IdPuntoVenta, CategoriaPago.Otros, null, null, "Pago con líneas", null, null, lineas);

    private static SolicitudDePagoDeAdministracionConLineas AltaAdmin(
        Contexto ctx, CategoriaPago categoria, int? idProveedor, params LineaDePago[] lineas) =>
        new(DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), ctx.IdEmpresa, ctx.IdPuntoVenta, categoria, idProveedor,
            null, "Pago admin con líneas", null, null, lineas);

    private static SolicitudDeEdicionDePagoConLineas Edicion(params LineaDePago[] lineas) =>
        new(CategoriaPago.Otros, null, null, "Concepto editado", null, null, lineas);

    private static async Task<T> LeerAsync<T>(HttpResponseMessage respuesta, HttpStatusCode esperado)
    {
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == esperado, cuerpo);
        return JsonSerializer.Deserialize<T>(cuerpo, OpcionesJson)!;
    }

    private static async Task<string> CodigoAsync(HttpResponseMessage respuesta) =>
        JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync()).RootElement.GetProperty("codigo").GetString()!;

    private async Task<(int IdCompra, decimal Total)> CrearYConfirmarCompraAsync(Contexto ctx)
    {
        await using var plataforma = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;
        var idAlicuota = await plataforma.AlicuotasIva.Where(a => a.Nombre == "21%").Select(a => a.Id).FirstAsync();
        var idTipo = await plataforma.TiposComprobante.Where(t => t.Codigo == "C-FA").Select(t => t.Id).SingleAsync();
        var articulo = new Articulo
        {
            IdTenant = ctx.IdTenant, CodigoInterno = $"api-lineas-{Guid.NewGuid():N}", Nombre = "Articulo",
            IdArea = ctx.IdArea, IdAlicuotaIva = idAlicuota, UnidadVenta = UnidadVenta.Unidad, EsProducto = true,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        plataforma.Articulos.Add(articulo);
        await plataforma.SaveChangesAsync();

        var alta = await ctx.Admin.PostAsJsonAsync("/api/compras", new SolicitudDeCompra(
            ctx.IdProveedor, idTipo, ctx.IdPuntoVenta, "0001-00000001", DateOnly.FromDateTime(DateTime.UtcNow), null,
            [new LineaDeCompraSolicitada(articulo.Id, "Item", 10m, null, null, 100m, 0m, idAlicuota, true)]));
        var idCompra = (await LeerAsync<CompraDetalle>(alta, HttpStatusCode.Created)).Id;
        Assert.Equal(HttpStatusCode.OK, (await ctx.Admin.PostAsync($"/api/compras/{idCompra}/confirmar", null)).StatusCode);

        await using var db = Db(ctx);
        return (idCompra, await db.ComprobantesCompra.Where(c => c.Id == idCompra).Select(c => c.Total).SingleAsync());
    }

    // ---- POS --------------------------------------------------------------------------------

    /// <summary>Un Vendedor registra un pago de dos medios: 201 con las líneas, sin el medio de cabecera, y el cierre reparte
    /// el arqueo por medio.</summary>
    [Fact]
    public async Task UnVendedorRegistraUnPagoConDosLineasYElArqueoSeReparteConLasLineas()
    {
        var ctx = await PrepararAsync(nameof(UnVendedorRegistraUnPagoConDosLineasYElArqueoSeReparteConLasLineas));
        var vendedor = await CrearVendedorAsync(ctx);
        var idTurno = await AbrirTurnoAsync(ctx, 1000m);

        var respuesta = await vendedor.PostAsJsonAsync("/api/pagos", AltaPos(
            ctx, new LineaDePago(ctx.IdEfectivo, 60m, null), new LineaDePago(ctx.IdElectronico, 40m, " cupón 3 ")));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        var pago = JsonSerializer.Deserialize<PagoConLineas>(cuerpo, OpcionesJson)!;
        Assert.Equal($"/api/pagos/{pago.Id}", respuesta.Headers.Location!.OriginalString);
        Assert.Equal(100m, pago.Importe);
        Assert.Equal(
            [(ctx.IdEfectivo, 60m, (string?)null), (ctx.IdElectronico, 40m, "cupón 3")],
            pago.Lineas.Select(l => (l.IdMedioPago, l.Importe, l.Referencia)));
        Assert.False(JsonDocument.Parse(cuerpo).RootElement.TryGetProperty("idMedioPago", out _));

        await CerrarTurnoAsync(
            ctx, idTurno, new ConteoDeclarado(ctx.IdEfectivo, 940m), new ConteoDeclarado(ctx.IdElectronico, 0m));
        await using var db = Db(ctx);
        var filas = await db.ArqueosTurno.Where(a => a.IdTurnoCaja == idTurno).ToListAsync();
        Assert.Equal(940m, filas.Single(a => a.IdMedioPago == ctx.IdEfectivo).ImporteEsperado);
        Assert.Equal(-40m, filas.Single(a => a.IdMedioPago == ctx.IdElectronico).ImporteEsperado);
    }

    [Fact]
    public async Task ElAltaDelPosRechazaLineasInvalidasConSusCodigosYUnMedioDeCuentaCorrienteOInexistente()
    {
        var ctx = await PrepararAsync(nameof(ElAltaDelPosRechazaLineasInvalidasConSusCodigosYUnMedioDeCuentaCorrienteOInexistente));
        await AbrirTurnoAsync(ctx, 100m);

        async Task<(HttpStatusCode, string)> Probar(params LineaDePago[] lineas)
        {
            var r = await ctx.Admin.PostAsJsonAsync("/api/pagos", AltaPos(ctx, lineas));
            return (r.StatusCode, await CodigoAsync(r));
        }

        Assert.Equal((HttpStatusCode.BadRequest, "pago_sin_lineas"), await Probar());
        Assert.Equal(
            (HttpStatusCode.BadRequest, "pago_demasiadas_lineas"),
            await Probar(Enumerable.Repeat(new LineaDePago(ctx.IdEfectivo, 1m, null), 21).ToArray()));
        Assert.Equal(
            (HttpStatusCode.BadRequest, "pago_linea_importe_invalido"), await Probar(new LineaDePago(ctx.IdEfectivo, 0m, null)));
        Assert.Equal(
            (HttpStatusCode.BadRequest, "pago_linea_referencia_muy_larga"),
            await Probar(new LineaDePago(ctx.IdEfectivo, 5m, new string('x', 101))));
        Assert.Equal(
            (HttpStatusCode.BadRequest, "gasto_medio_pago_cuenta_corriente_invalido"),
            await Probar(new LineaDePago(ctx.IdEfectivo, 5m, null), new LineaDePago(ctx.IdCuentaCorriente, 5m, null)));

        var inexistente = await ctx.Admin.PostAsJsonAsync("/api/pagos", AltaPos(ctx, new LineaDePago(int.MaxValue, 5m, null)));
        Assert.Equal(HttpStatusCode.NotFound, inexistente.StatusCode);

        await using var db = Db(ctx);
        Assert.Empty(await db.Pagos.ToListAsync());
    }

    [Fact]
    public async Task ElHistorialDelPosTraeLasLineasYElPutEditaUnPagoDeDosMediosMientrasElPutViejoDa409()
    {
        var ctx = await PrepararAsync(nameof(ElHistorialDelPosTraeLasLineasYElPutEditaUnPagoDeDosMediosMientrasElPutViejoDa409));
        var vendedor = await CrearVendedorAsync(ctx);
        var idTurno = await AbrirTurnoAsync(ctx, 1000m);

        var creado = await LeerAsync<PagoConLineas>(
            await vendedor.PostAsJsonAsync("/api/pagos", AltaPos(
                ctx, new LineaDePago(ctx.IdEfectivo, 60m, null), new LineaDePago(ctx.IdElectronico, 40m, null))),
            HttpStatusCode.Created);

        var pagina = await vendedor.GetFromJsonAsync<PaginaDePagos>($"/api/pagos?idPuntoVenta={ctx.IdPuntoVenta}", OpcionesJson);
        Assert.Equal(2, pagina!.Items.Single(p => p.Id == creado.Id).Lineas.Count);

        var viejo = await vendedor.PutAsJsonAsync(
            $"/api/gastos/{creado.Id}",
            new SolicitudDeEdicionDePago(CategoriaPago.Otros, null, null, "x", null, ctx.IdEfectivo, null, 100m));
        Assert.Equal(HttpStatusCode.Conflict, viejo.StatusCode);
        Assert.Equal("gasto_con_varios_medios", await CodigoAsync(viejo));

        var editado = await LeerAsync<PagoConLineas>(
            await vendedor.PutAsJsonAsync($"/api/pagos/{creado.Id}", Edicion(
                new LineaDePago(ctx.IdEfectivo, 50m, null), new LineaDePago(ctx.IdElectronico, 30m, null),
                new LineaDePago(ctx.IdTercero, 20m, "op 1"))),
            HttpStatusCode.OK);
        Assert.Equal(100m, editado.Importe);
        Assert.Equal(3, editado.Lineas.Count);

        await CerrarTurnoAsync(
            ctx, idTurno, new ConteoDeclarado(ctx.IdEfectivo, 950m), new ConteoDeclarado(ctx.IdElectronico, 0m),
            new ConteoDeclarado(ctx.IdTercero, 0m));
        await using var db = Db(ctx);
        var filas = await db.ArqueosTurno.Where(a => a.IdTurnoCaja == idTurno).ToListAsync();
        Assert.Equal(-20m, filas.Single(a => a.IdMedioPago == ctx.IdTercero).ImporteEsperado);
    }

    [Fact]
    public async Task EditarOBorrarUnPagoInexistenteDa404AunqueElCuerpoSeaInvalido()
    {
        var ctx = await PrepararAsync(nameof(EditarOBorrarUnPagoInexistenteDa404AunqueElCuerpoSeaInvalido));
        var sinLineas = Edicion();

        Assert.Equal(HttpStatusCode.NotFound, (await ctx.Admin.PutAsJsonAsync("/api/pagos/999999", sinLineas)).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound, (await ctx.Admin.PutAsJsonAsync("/api/pagos/administracion/999999", sinLineas)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ctx.Admin.DeleteAsync("/api/pagos/administracion/999999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ctx.Admin.GetAsync("/api/pagos/administracion/999999")).StatusCode);
    }

    /// <summary>La compra existe y está confirmada: el 404 solo puede venir del pago (la compra se bloquea primero).</summary>
    [Fact]
    public async Task VincularUnPagoInexistenteDa404PorElPagoYNoPorLaCompra()
    {
        var ctx = await PrepararAsync(nameof(VincularUnPagoInexistenteDa404PorElPagoYNoPorLaCompra));
        var (idCompra, _) = await CrearYConfirmarCompraAsync(ctx);

        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/pagos/999999/vincular-compra", new SolicitudDeVincularCompra(idCompra));

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        var cuerpo = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("no_encontrado", cuerpo.GetProperty("codigo").GetString());
        Assert.Contains("No existe el gasto 999999", cuerpo.GetProperty("mensaje").GetString());
    }

    /// <summary>El Location del alta resuelve: mismas líneas que la respuesta del POST, mismo alcance que el listado.</summary>
    [Fact]
    public async Task ElLocationDelAltaDelPosResuelveConLasMismasLineasYUnIdAjenoODesconocidoDa404()
    {
        var ctx = await PrepararAsync(nameof(ElLocationDelAltaDelPosResuelveConLasMismasLineasYUnIdAjenoODesconocidoDa404));
        var otro = await PrepararAsync(nameof(ElLocationDelAltaDelPosResuelveConLasMismasLineasYUnIdAjenoODesconocidoDa404) + "Otro");
        var vendedor = await CrearVendedorAsync(ctx);
        await AbrirTurnoAsync(ctx, 1000m);
        await AbrirTurnoAsync(otro, 1000m);

        var alta = await vendedor.PostAsJsonAsync("/api/pagos", AltaPos(
            ctx, new LineaDePago(ctx.IdEfectivo, 60m, null), new LineaDePago(ctx.IdElectronico, 40m, "cupón")));
        var location = alta.Headers.Location!.OriginalString;
        var creado = await LeerAsync<PagoConLineas>(alta, HttpStatusCode.Created);

        var leido = await LeerAsync<PagoListado>(await vendedor.GetAsync(location), HttpStatusCode.OK);
        Assert.Equal(creado.Id, leido.Id);
        Assert.Equal(
            creado.Lineas.Select(l => (l.IdMedioPago, l.Importe, l.Referencia)),
            leido.Lineas.Select(l => (l.IdMedioPago, l.Importe, l.Referencia)));

        Assert.Equal(HttpStatusCode.NotFound, (await vendedor.GetAsync("/api/pagos/999999")).StatusCode);

        var ajeno = await LeerAsync<PagoConLineas>(
            await otro.Admin.PostAsJsonAsync("/api/pagos", AltaPos(otro, new LineaDePago(otro.IdEfectivo, 5m, null))),
            HttpStatusCode.Created);
        var propios = await vendedor.GetFromJsonAsync<PaginaDePagos>("/api/pagos?tamanio=200", OpcionesJson);
        Assert.DoesNotContain(propios!.Items, p => p.Id == ajeno.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await vendedor.GetAsync($"/api/pagos/{ajeno.Id}")).StatusCode);
    }

    // ---- administración -----------------------------------------------------------------------

    [Fact]
    public async Task ElPagoAdministrativoConDosLineasEgresaElTotalUnaVezBajaElProveedorYSePuedeEditarYBorrar()
    {
        var ctx = await PrepararAsync(nameof(ElPagoAdministrativoConDosLineasEgresaElTotalUnaVezBajaElProveedorYSePuedeEditarYBorrar));

        var creado = await LeerAsync<PagoConLineas>(
            await ctx.Admin.PostAsJsonAsync("/api/pagos/administracion", AltaAdmin(
                ctx, CategoriaPago.Proveedor, ctx.IdProveedor,
                new LineaDePago(ctx.IdEfectivo, 70m, null), new LineaDePago(ctx.IdElectronico, 55m, null))),
            HttpStatusCode.Created);
        Assert.Equal(125m, creado.Importe);
        Assert.Equal(2, creado.Lineas.Count);

        await using (var db = Db(ctx))
        {
            var movimiento = await db.MovimientosTesoreria.SingleAsync(m => m.IdPago == creado.Id);
            Assert.Equal((0m, 125m), (movimiento.Ingreso, movimiento.Egreso));
            Assert.Equal(-125m, await db.Proveedores.Where(p => p.Id == ctx.IdProveedor).Select(p => p.Saldo).SingleAsync());
        }

        var detalle = await ctx.Admin.GetFromJsonAsync<PagoDeAdministracionListado>(
            $"/api/pagos/administracion/{creado.Id}", OpcionesJson);
        Assert.Equal(2, detalle!.Lineas.Count);

        var editado = await LeerAsync<PagoDeAdministracionListado>(
            await ctx.Admin.PutAsJsonAsync(
                $"/api/pagos/administracion/{creado.Id}",
                new SolicitudDeEdicionDePagoConLineas(
                    CategoriaPago.Proveedor, ctx.IdProveedor, null, "Editado", null, null,
                    [new LineaDePago(ctx.IdEfectivo, 40m, null), new LineaDePago(ctx.IdElectronico, 30m, null),
                     new LineaDePago(ctx.IdTercero, 10m, null)])),
            HttpStatusCode.OK);
        Assert.Equal((80m, 3), (editado.Importe, editado.Lineas.Count));

        var listado = await ctx.Admin.GetFromJsonAsync<PaginaDePagosDeAdministracion>(
            "/api/pagos/administracion?tamanio=50", OpcionesJson);
        Assert.Contains(listado!.Items, p => p.Id == creado.Id && p.Lineas.Count == 3);

        Assert.Equal(HttpStatusCode.NoContent, (await ctx.Admin.DeleteAsync($"/api/pagos/administracion/{creado.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ctx.Admin.GetAsync($"/api/pagos/administracion/{creado.Id}")).StatusCode);
    }

    [Fact]
    public async Task ElAltaAdministrativaRechazaLineasInvalidasYUnMedioDeCuentaCorriente()
    {
        var ctx = await PrepararAsync(nameof(ElAltaAdministrativaRechazaLineasInvalidasYUnMedioDeCuentaCorriente));

        var sinLineas = await ctx.Admin.PostAsJsonAsync("/api/pagos/administracion", AltaAdmin(ctx, CategoriaPago.Otros, null));
        Assert.Equal("pago_sin_lineas", await CodigoAsync(sinLineas));
        var invalida = await ctx.Admin.PostAsJsonAsync(
            "/api/pagos/administracion", AltaAdmin(ctx, CategoriaPago.Otros, null, new LineaDePago(ctx.IdEfectivo, -1m, null)));
        Assert.Equal("pago_linea_importe_invalido", await CodigoAsync(invalida));
        var cuentaCorriente = await ctx.Admin.PostAsJsonAsync(
            "/api/pagos/administracion",
            AltaAdmin(ctx, CategoriaPago.Otros, null, new LineaDePago(ctx.IdCuentaCorriente, 5m, null)));
        Assert.Equal("gasto_medio_pago_cuenta_corriente_invalido", await CodigoAsync(cuentaCorriente));
    }

    // ---- de compra ----------------------------------------------------------------------------

    [Fact]
    public async Task PagarUnaCompraConDosLineasDejaElSaldoPendienteYRespondeConLaClavePago()
    {
        var ctx = await PrepararAsync(nameof(PagarUnaCompraConDosLineasDejaElSaldoPendienteYRespondeConLaClavePago));
        var (idCompra, total) = await CrearYConfirmarCompraAsync(ctx);
        var fecha = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
        var cuarto = Math.Round(total / 4m, 2);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            $"/api/pagos/de-compra/{idCompra}",
            new SolicitudDePagoDeCompraConLineas(
                fecha, [new LineaDePago(ctx.IdEfectivo, cuarto, null), new LineaDePago(ctx.IdElectronico, cuarto, null)], null));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        var raiz = JsonDocument.Parse(cuerpo).RootElement;
        Assert.True(raiz.TryGetProperty("pago", out _));
        Assert.False(raiz.TryGetProperty("gasto", out _));
        var resultado = JsonSerializer.Deserialize<ResultadoDePagoDeCompraConLineas>(cuerpo, OpcionesJson)!;
        Assert.Equal(cuarto * 2, resultado.Pagado);
        Assert.Equal(total - cuarto * 2, resultado.SaldoPendiente);
        Assert.Equal(2, resultado.Pago.Lineas.Count);

        var excede = await ctx.Admin.PostAsJsonAsync(
            $"/api/pagos/de-compra/{idCompra}",
            new SolicitudDePagoDeCompraConLineas(fecha, [new LineaDePago(ctx.IdEfectivo, total, null)], null));
        Assert.Equal("pago_excede_saldo_pendiente", await CodigoAsync(excede));

        var sinLineas = await ctx.Admin.PostAsJsonAsync(
            $"/api/pagos/de-compra/{idCompra}", new SolicitudDePagoDeCompraConLineas(fecha, [], null));
        Assert.Equal("pago_sin_lineas", await CodigoAsync(sinLineas));
    }

    [Fact]
    public async Task VincularUnPagoACompraPorLaRutaNuevaDevuelveElPagoConLineas()
    {
        var ctx = await PrepararAsync(nameof(VincularUnPagoACompraPorLaRutaNuevaDevuelveElPagoConLineas));
        var (idCompra, _) = await CrearYConfirmarCompraAsync(ctx);
        var creado = await LeerAsync<PagoConLineas>(
            await ctx.Admin.PostAsJsonAsync("/api/pagos/administracion", AltaAdmin(
                ctx, CategoriaPago.Proveedor, ctx.IdProveedor,
                new LineaDePago(ctx.IdEfectivo, 10m, null), new LineaDePago(ctx.IdElectronico, 5m, null))),
            HttpStatusCode.Created);

        var vinculado = await LeerAsync<PagoConLineas>(
            await ctx.Admin.PostAsJsonAsync($"/api/pagos/{creado.Id}/vincular-compra", new SolicitudDeVincularCompra(idCompra)),
            HttpStatusCode.OK);

        Assert.Equal(idCompra, vinculado.IdComprobanteCompra);
        Assert.Equal(2, vinculado.Lineas.Count);
    }

    // ---- autorización -------------------------------------------------------------------------

    [Fact]
    public async Task UnVendedorNoUsaLasRutasDeAdministracionNiPagaComprasNiVincula()
    {
        var ctx = await PrepararAsync(nameof(UnVendedorNoUsaLasRutasDeAdministracionNiPagaComprasNiVincula));
        var vendedor = await CrearVendedorAsync(ctx);
        var linea = new LineaDePago(ctx.IdEfectivo, 5m, null);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await vendedor.PostAsJsonAsync("/api/pagos/administracion", AltaAdmin(ctx, CategoriaPago.Otros, null, linea))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await vendedor.GetAsync("/api/pagos/administracion")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await vendedor.GetAsync("/api/pagos/administracion/1")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden, (await vendedor.PutAsJsonAsync("/api/pagos/administracion/1", Edicion(linea))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await vendedor.DeleteAsync("/api/pagos/administracion/1")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await vendedor.PostAsJsonAsync(
                "/api/pagos/de-compra/1", new SolicitudDePagoDeCompraConLineas(DateOnly.FromDateTime(DateTime.UtcNow), [linea], null)))
            .StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await vendedor.PostAsJsonAsync("/api/pagos/1/vincular-compra", new SolicitudDeVincularCompra(1))).StatusCode);
    }

    [Fact]
    public async Task SinSesionLasRutasNuevasDan401()
    {
        using var anonimo = fixture.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.GetAsync("/api/pagos")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.GetAsync("/api/pagos/administracion")).StatusCode);
    }
}
