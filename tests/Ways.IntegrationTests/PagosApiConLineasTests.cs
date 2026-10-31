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
/// Contrato HTTP <c>/api/pagos</c> con líneas por medio: cada ruta se ejerce por HTTP con el rol que su política exige.
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
    public async Task ElHistorialDelPosTraeLasLineasYElPutEditaUnPagoDeDosMedios()
    {
        var ctx = await PrepararAsync(nameof(ElHistorialDelPosTraeLasLineasYElPutEditaUnPagoDeDosMedios));
        var vendedor = await CrearVendedorAsync(ctx);
        var idTurno = await AbrirTurnoAsync(ctx, 1000m);

        var creado = await LeerAsync<PagoConLineas>(
            await vendedor.PostAsJsonAsync("/api/pagos", AltaPos(
                ctx, new LineaDePago(ctx.IdEfectivo, 60m, null), new LineaDePago(ctx.IdElectronico, 40m, null))),
            HttpStatusCode.Created);

        var pagina = await vendedor.GetFromJsonAsync<PaginaDePagos>($"/api/pagos?idPuntoVenta={ctx.IdPuntoVenta}", OpcionesJson);
        Assert.Equal(2, pagina!.Items.Single(p => p.Id == creado.Id).Lineas.Count);

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
        Assert.Contains("No existe el gasto 999999", cuerpo.GetProperty("title").GetString());
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
        Assert.Equal(2, resultado.Pago!.Lineas.Count);
        Assert.Empty(resultado.Aplicaciones.Where(a => a.IdPago != resultado.Pago.Id));
        Assert.Equal(cuarto * 2, resultado.Aplicaciones.Sum(a => a.Importe));
        Assert.Equal(0m, resultado.DisponibleDelPago);

        var sinLineas = await ctx.Admin.PostAsJsonAsync(
            $"/api/pagos/de-compra/{idCompra}", new SolicitudDePagoDeCompraConLineas(fecha, [], null));
        Assert.Equal("pago_sin_lineas", await CodigoAsync(sinLineas));
    }

    // ---- de compra: pagos a cuenta y exceso (E3-S4) -------------------------------------------

    private static async Task<decimal> SaldoDelProveedorAsync(Contexto ctx) =>
        (await ctx.Admin.GetFromJsonAsync<SaldoDeProveedor>($"/api/proveedores/{ctx.IdProveedor}/saldo", OpcionesJson))!.Saldo;

    private static DateOnly Ayer() => DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));

    private static async Task<PagoConLineas> CrearPagoDeProveedorAsync(Contexto ctx, decimal importe) =>
        await LeerAsync<PagoConLineas>(
            await ctx.Admin.PostAsJsonAsync(
                "/api/pagos/administracion",
                AltaAdmin(ctx, CategoriaPago.Proveedor, ctx.IdProveedor, new LineaDePago(ctx.IdEfectivo, importe, null))),
            HttpStatusCode.Created);

    [Fact]
    public async Task PagarDeMasDejaElExcedenteADisponibleDelPagoYElSaldoDelProveedorAFavor()
    {
        var ctx = await PrepararAsync(nameof(PagarDeMasDejaElExcedenteADisponibleDelPagoYElSaldoDelProveedorAFavor));
        var (idCompra, total) = await CrearYConfirmarCompraAsync(ctx);
        var saldoAntes = await SaldoDelProveedorAsync(ctx);

        var resultado = await LeerAsync<ResultadoDePagoDeCompraConLineas>(
            await ctx.Admin.PostAsJsonAsync(
                $"/api/pagos/de-compra/{idCompra}",
                new SolicitudDePagoDeCompraConLineas(Ayer(), [new LineaDePago(ctx.IdEfectivo, total + 300m, null)], null)),
            HttpStatusCode.Created);

        Assert.Equal(total, resultado.Pagado);
        Assert.Equal(0m, resultado.SaldoPendiente);
        Assert.Equal(300m, resultado.DisponibleDelPago);
        Assert.Equal(total + 300m, resultado.Pago!.Importe);
        var aplicacion = Assert.Single(resultado.Aplicaciones);
        Assert.Equal((resultado.Pago.Id, total), (aplicacion.IdPago, aplicacion.Importe));
        Assert.Equal(saldoAntes - total - 300m, await SaldoDelProveedorAsync(ctx));

        var detalle = await ctx.Admin.GetFromJsonAsync<PagoDeAdministracionListado>(
            $"/api/pagos/administracion/{resultado.Pago.Id}", OpcionesJson);
        Assert.Equal(300m, detalle!.Disponible);
    }

    [Fact]
    public async Task SoloAplicacionesSinLineasPagaLaCompraYNoCreaPagoNuevo()
    {
        var ctx = await PrepararAsync(nameof(SoloAplicacionesSinLineasPagaLaCompraYNoCreaPagoNuevo));
        var (idCompra, total) = await CrearYConfirmarCompraAsync(ctx);
        var credito = await CrearPagoDeProveedorAsync(ctx, 500m);

        var resultado = await LeerAsync<ResultadoDePagoDeCompraConLineas>(
            await ctx.Admin.PostAsJsonAsync(
                $"/api/pagos/de-compra/{idCompra}",
                new SolicitudDePagoDeCompraConLineas(Ayer(), null, null, [new AplicacionDePagoExistente(credito.Id, 200m)])),
            HttpStatusCode.OK);

        Assert.Null(resultado.Pago);
        Assert.Null(resultado.DisponibleDelPago);
        Assert.Equal(200m, resultado.Pagado);
        Assert.Equal(total - 200m, resultado.SaldoPendiente);
        var aplicacion = Assert.Single(resultado.Aplicaciones);
        Assert.Equal((credito.Id, 200m), (aplicacion.IdPago, aplicacion.Importe));

        var detalle = await ctx.Admin.GetFromJsonAsync<PagoDeAdministracionListado>(
            $"/api/pagos/administracion/{credito.Id}", OpcionesJson);
        Assert.Equal(300m, detalle!.Disponible);
    }

    [Fact]
    public async Task AplicacionesMasLineasConsumenPrimeroElCreditoYElExcedenteQuedaEnElPagoNuevo()
    {
        var ctx = await PrepararAsync(nameof(AplicacionesMasLineasConsumenPrimeroElCreditoYElExcedenteQuedaEnElPagoNuevo));
        var (idCompra, total) = await CrearYConfirmarCompraAsync(ctx);
        var credito = await CrearPagoDeProveedorAsync(ctx, 1000m);

        // El crédito deja total - 1000 por pagar; las líneas pagan 400, así que se aplica ese resto y lo demás queda a favor.
        var restante = total - 1000m;
        Assert.InRange(restante, 1m, 399m);
        var resultado = await LeerAsync<ResultadoDePagoDeCompraConLineas>(
            await ctx.Admin.PostAsJsonAsync(
                $"/api/pagos/de-compra/{idCompra}",
                new SolicitudDePagoDeCompraConLineas(
                    Ayer(), [new LineaDePago(ctx.IdEfectivo, 400m, null)], null,
                    [new AplicacionDePagoExistente(credito.Id, 1000m)])),
            HttpStatusCode.Created);

        Assert.Equal(total, resultado.Pagado);
        Assert.Equal(0m, resultado.SaldoPendiente);
        Assert.Equal(400m - restante, resultado.DisponibleDelPago);
        Assert.Equal(
            [(credito.Id, 1000m), (resultado.Pago!.Id, restante)],
            resultado.Aplicaciones.Select(a => (a.IdPago, a.Importe)));
    }

    private async Task<int> CrearOtroProveedorAsync(Contexto ctx)
    {
        await using var plataforma = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;
        var idCondicion = await plataforma.CondicionesFiscales.Select(c => c.Id).FirstAsync();
        var otro = new Proveedor
        {
            IdTenant = ctx.IdTenant, RazonSocial = $"{ctx.Nombre} Otro", IdCondicionFiscal = idCondicion,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        plataforma.Proveedores.Add(otro);
        await plataforma.SaveChangesAsync();
        return otro.Id;
    }

    [Fact]
    public async Task UnPagoSinProveedorSeAdoptaAdentroDePagarLaCompraYElLibroRecibeSuImporteCompletoMasElPagoNuevo()
    {
        var ctx = await PrepararAsync(nameof(UnPagoSinProveedorSeAdoptaAdentroDePagarLaCompraYElLibroRecibeSuImporteCompletoMasElPagoNuevo));
        var (idCompra, total) = await CrearYConfirmarCompraAsync(ctx);
        var sinProveedor = await LeerAsync<PagoConLineas>(
            await ctx.Admin.PostAsJsonAsync(
                "/api/pagos/administracion", AltaAdmin(ctx, CategoriaPago.Otros, null, new LineaDePago(ctx.IdEfectivo, 500m, null))),
            HttpStatusCode.Created);
        var saldoAntes = await SaldoDelProveedorAsync(ctx);

        var resultado = await LeerAsync<ResultadoDePagoDeCompraConLineas>(
            await ctx.Admin.PostAsJsonAsync(
                $"/api/pagos/de-compra/{idCompra}",
                new SolicitudDePagoDeCompraConLineas(
                    Ayer(), [new LineaDePago(ctx.IdEfectivo, total, null)], null,
                    [new AplicacionDePagoExistente(sinProveedor.Id, 500m)])),
            HttpStatusCode.Created);

        Assert.Equal(total, resultado.Pagado);
        Assert.Equal(500m, resultado.DisponibleDelPago);

        var adoptado = await ctx.Admin.GetFromJsonAsync<PagoDeAdministracionListado>(
            $"/api/pagos/administracion/{sinProveedor.Id}", OpcionesJson);
        Assert.Equal((CategoriaPago.Proveedor, (int?)ctx.IdProveedor), (adoptado!.Categoria, adoptado.IdProveedor));

        // El libro recibe el pago adoptado por sus 500 completos y el pago nuevo por todo su importe: saldo = Σ ledger.
        var saldo = await SaldoDelProveedorAsync(ctx);
        Assert.Equal(saldoAntes - 500m - total, saldo);
        await using var db = Db(ctx);
        var movimientoAdoptado = await db.MovimientosCuentaCorrienteProveedor
            .SingleAsync(m => m.IdPago == sinProveedor.Id && m.IdProveedor == ctx.IdProveedor);
        Assert.Equal(-500m, movimientoAdoptado.Importe);
        Assert.Equal(
            saldo,
            await db.MovimientosCuentaCorrienteProveedor.Where(m => m.IdProveedor == ctx.IdProveedor).SumAsync(m => m.Importe));
    }

    [Fact]
    public async Task PagarDeMasEgresaDeTesoreriaElImporteCompletoDelPagoNuevo()
    {
        var ctx = await PrepararAsync(nameof(PagarDeMasEgresaDeTesoreriaElImporteCompletoDelPagoNuevo));
        var (idCompra, total) = await CrearYConfirmarCompraAsync(ctx);

        var resultado = await LeerAsync<ResultadoDePagoDeCompraConLineas>(
            await ctx.Admin.PostAsJsonAsync(
                $"/api/pagos/de-compra/{idCompra}",
                new SolicitudDePagoDeCompraConLineas(Ayer(), [new LineaDePago(ctx.IdEfectivo, total + 300m, null)], null)),
            HttpStatusCode.Created);

        await using var db = Db(ctx);
        var movimiento = await db.MovimientosTesoreria.SingleAsync(m => m.IdPago == resultado.Pago!.Id);
        Assert.Equal((0m, total + 300m), (movimiento.Ingreso, movimiento.Egreso));
    }

    [Fact]
    public async Task UnPagoDeOtroProveedorOUnoDeSueldosNoSeAplicanPorEstaRuta()
    {
        var ctx = await PrepararAsync(nameof(UnPagoDeOtroProveedorOUnoDeSueldosNoSeAplicanPorEstaRuta));
        var (idCompra, _) = await CrearYConfirmarCompraAsync(ctx);
        var idOtro = await CrearOtroProveedorAsync(ctx);
        var deOtro = await LeerAsync<PagoConLineas>(
            await ctx.Admin.PostAsJsonAsync(
                "/api/pagos/administracion",
                AltaAdmin(ctx, CategoriaPago.Proveedor, idOtro, new LineaDePago(ctx.IdEfectivo, 100m, null))),
            HttpStatusCode.Created);
        var deSueldos = await LeerAsync<PagoConLineas>(
            await ctx.Admin.PostAsJsonAsync(
                "/api/pagos/administracion", AltaAdmin(ctx, CategoriaPago.Sueldos, null, new LineaDePago(ctx.IdEfectivo, 100m, null))),
            HttpStatusCode.Created);
        var ruta = $"/api/pagos/de-compra/{idCompra}";

        var otroProveedor = await ctx.Admin.PostAsJsonAsync(
            ruta, new SolicitudDePagoDeCompraConLineas(Ayer(), null, null, [new AplicacionDePagoExistente(deOtro.Id, 50m)]));
        Assert.Equal(HttpStatusCode.Conflict, otroProveedor.StatusCode);
        Assert.Equal("compra_de_otro_proveedor", await CodigoAsync(otroProveedor));

        var sueldos = await ctx.Admin.PostAsJsonAsync(
            ruta, new SolicitudDePagoDeCompraConLineas(Ayer(), null, null, [new AplicacionDePagoExistente(deSueldos.Id, 50m)]));
        Assert.Equal(HttpStatusCode.Conflict, sueldos.StatusCode);
        Assert.Equal("pago_no_aplicable", await CodigoAsync(sueldos));
    }

    [Fact]
    public async Task UnaCompraYaPagadaOSaldadaPorElCreditoNoAceptaLineasNuevasYNoEscribeNada()
    {
        var ctx = await PrepararAsync(nameof(UnaCompraYaPagadaOSaldadaPorElCreditoNoAceptaLineasNuevasYNoEscribeNada));
        var (idCompra, total) = await CrearYConfirmarCompraAsync(ctx);
        var ruta = $"/api/pagos/de-compra/{idCompra}";
        var credito = await CrearPagoDeProveedorAsync(ctx, total);

        // El crédito salda la compra justo: hay líneas pero no queda dónde aplicarlas.
        var saldadaPorElCredito = await ctx.Admin.PostAsJsonAsync(
            ruta,
            new SolicitudDePagoDeCompraConLineas(
                Ayer(), [new LineaDePago(ctx.IdEfectivo, 50m, null)], null, [new AplicacionDePagoExistente(credito.Id, total)]));
        Assert.Equal(HttpStatusCode.Conflict, saldadaPorElCredito.StatusCode);
        Assert.Equal("compra_sin_saldo_pendiente", await CodigoAsync(saldadaPorElCredito));
        await using (var db = Db(ctx))
        {
            Assert.Empty(await db.AplicacionesPago.Where(a => a.IdComprobanteCompra == idCompra).ToListAsync());
            Assert.Single(await db.Pagos.ToListAsync());
        }

        // Ya pagada: aplicar el crédito y pagar de nuevo.
        await LeerAsync<ResultadoDePagoDeCompraConLineas>(
            await ctx.Admin.PostAsJsonAsync(
                ruta, new SolicitudDePagoDeCompraConLineas(Ayer(), null, null, [new AplicacionDePagoExistente(credito.Id, total)])),
            HttpStatusCode.OK);
        var yaPagada = await ctx.Admin.PostAsJsonAsync(
            ruta, new SolicitudDePagoDeCompraConLineas(Ayer(), [new LineaDePago(ctx.IdEfectivo, 10m, null)], null));
        Assert.Equal(HttpStatusCode.Conflict, yaPagada.StatusCode);
        Assert.Equal("compra_sin_saldo_pendiente", await CodigoAsync(yaPagada));
    }

    [Fact]
    public async Task ElConceptoSinLineasSeRechazaLaFechaSiempreSeValidaYDataLasAplicacionesNuevas()
    {
        var ctx = await PrepararAsync(nameof(ElConceptoSinLineasSeRechazaLaFechaSiempreSeValidaYDataLasAplicacionesNuevas));
        var (idCompra, _) = await CrearYConfirmarCompraAsync(ctx);
        var credito = await CrearPagoDeProveedorAsync(ctx, 500m);
        var ruta = $"/api/pagos/de-compra/{idCompra}";
        var aplicar = new[] { new AplicacionDePagoExistente(credito.Id, 100m) };

        var conConcepto = await ctx.Admin.PostAsJsonAsync(ruta, new SolicitudDePagoDeCompraConLineas(Ayer(), null, "Se perdería", aplicar));
        Assert.Equal(HttpStatusCode.BadRequest, conConcepto.StatusCode);
        Assert.Equal("concepto_sin_pago_nuevo", await CodigoAsync(conConcepto));

        var futura = await ctx.Admin.PostAsJsonAsync(
            ruta, new SolicitudDePagoDeCompraConLineas(DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)), null, null, aplicar));
        Assert.Equal(HttpStatusCode.BadRequest, futura.StatusCode);
        Assert.Equal("gasto_fecha_futura", await CodigoAsync(futura));

        var resultado = await LeerAsync<ResultadoDePagoDeCompraConLineas>(
            await ctx.Admin.PostAsJsonAsync(ruta, new SolicitudDePagoDeCompraConLineas(Ayer(), null, null, aplicar)),
            HttpStatusCode.OK);
        var aplicacion = Assert.Single(resultado.Aplicaciones);
        Assert.Equal(Ayer().ToDateTime(TimeOnly.MinValue).Date, aplicacion.Fecha.UtcDateTime.Date);
    }

    [Fact]
    public async Task UnPagoInexistenteDa404AunqueElImporteDeLaAplicacionSeaInvalido()
    {
        var ctx = await PrepararAsync(nameof(UnPagoInexistenteDa404AunqueElImporteDeLaAplicacionSeaInvalido));
        var (idCompra, _) = await CrearYConfirmarCompraAsync(ctx);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            $"/api/pagos/de-compra/{idCompra}",
            new SolicitudDePagoDeCompraConLineas(Ayer(), null, null, [new AplicacionDePagoExistente(999_999, 0m)]));

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    [Fact]
    public async Task AplicarMasQueElDisponibleOQueElSaldoEsConflictoYUnPagoRepetidoEs400()
    {
        var ctx = await PrepararAsync(nameof(AplicarMasQueElDisponibleOQueElSaldoEsConflictoYUnPagoRepetidoEs400));
        var (idCompra, total) = await CrearYConfirmarCompraAsync(ctx);
        var chico = await CrearPagoDeProveedorAsync(ctx, 100m);
        var grande = await CrearPagoDeProveedorAsync(ctx, total + 500m);
        var ruta = $"/api/pagos/de-compra/{idCompra}";

        var masQueElDisponible = await ctx.Admin.PostAsJsonAsync(
            ruta, new SolicitudDePagoDeCompraConLineas(Ayer(), null, null, [new AplicacionDePagoExistente(chico.Id, 150m)]));
        Assert.Equal(HttpStatusCode.Conflict, masQueElDisponible.StatusCode);
        Assert.Equal("aplicacion_excede_disponible", await CodigoAsync(masQueElDisponible));

        var masQueElSaldo = await ctx.Admin.PostAsJsonAsync(
            ruta, new SolicitudDePagoDeCompraConLineas(Ayer(), null, null, [new AplicacionDePagoExistente(grande.Id, total + 1m)]));
        Assert.Equal(HttpStatusCode.Conflict, masQueElSaldo.StatusCode);
        Assert.Equal("aplicacion_excede_saldo", await CodigoAsync(masQueElSaldo));

        var repetido = await ctx.Admin.PostAsJsonAsync(
            ruta,
            new SolicitudDePagoDeCompraConLineas(
                Ayer(), null, null, [new AplicacionDePagoExistente(chico.Id, 10m), new AplicacionDePagoExistente(chico.Id, 20m)]));
        Assert.Equal(HttpStatusCode.BadRequest, repetido.StatusCode);
        Assert.Equal("aplicacion_pago_repetido", await CodigoAsync(repetido));

        var importeInvalido = await ctx.Admin.PostAsJsonAsync(
            ruta, new SolicitudDePagoDeCompraConLineas(Ayer(), null, null, [new AplicacionDePagoExistente(chico.Id, 0m)]));
        Assert.Equal("aplicacion_importe_invalido", await CodigoAsync(importeInvalido));

        var inexistente = await ctx.Admin.PostAsJsonAsync(
            ruta, new SolicitudDePagoDeCompraConLineas(Ayer(), null, null, [new AplicacionDePagoExistente(999_999, 10m)]));
        Assert.Equal(HttpStatusCode.NotFound, inexistente.StatusCode);

        var sinNada = await ctx.Admin.PostAsJsonAsync(ruta, new SolicitudDePagoDeCompraConLineas(Ayer(), null, null, []));
        Assert.Equal("pago_sin_lineas", await CodigoAsync(sinNada));

        // Nada de lo rechazado dejó rastro: la compra sigue sin pagar.
        var estado = await ctx.Admin.GetFromJsonAsync<SaldoDeProveedor>($"/api/proveedores/{ctx.IdProveedor}/saldo", OpcionesJson);
        Assert.Equal(0m, estado!.Compras.Single(c => c.IdComprobanteCompra == idCompra).Pagado);
    }

    [Fact]
    public async Task LasRutasLegacyDePagarUnaCompraSiguenRechazandoElExcedente()
    {
        var ctx = await PrepararAsync(nameof(LasRutasLegacyDePagarUnaCompraSiguenRechazandoElExcedente));
        var (idCompra, total) = await CrearYConfirmarCompraAsync(ctx);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            $"/api/compras/{idCompra}/pagos",
            new SolicitudDePagoDeCompra(Ayer(), total + 1m, ctx.IdEfectivo, null));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal("pago_excede_saldo_pendiente", await CodigoAsync(respuesta));
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
