using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Ways.Application.Abstracciones;
using Ways.Application.Compras;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Compras;
using Ways.Domain.Organizacion;
using Ways.Domain.Proveedores;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-16-ordenes-de-compra, Slice 2 (tasks 2.8-2.24; design: Transactions — ENVIAR OC; tasks.md
/// decisión 4/conflict #1: los DOS shapes de concurrencia de <c>enviar</c>). Borrador CRUD
/// (replace-set) + <c>enviar</c> con numeración propia (serie <c>'OC'</c>), consumida ANTES de la
/// transacción de escritura (design decisión 6).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ServicioDeOrdenesDeCompraTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordVendedor = "vendedor-password-larga";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdPuntoVenta, int IdPuntoVenta2, HttpClient Admin, HttpClient Vendedor,
        int IdProveedor, int IdArticulo, int IdArticulo2, string MailAdmin, string PasswordAdmin);

    /// <summary>Decisión 13 (tasks.md): ids deliberadamente desincronizados — <c>PrepararAsync</c>
    /// nunca produce tenant/proveedor/PV/artículo numéricamente alineados entre sí (cada entidad
    /// nace en su propia tabla con su propia identidad autoincremental, nunca forzada a coincidir).</summary>
    private async Task<Contexto> PrepararAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var passwordAdmin = default(string);
        var solicitud = new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin);
        var respuesta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;
        passwordAdmin = resultado.PasswordTemporal;

        var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        // Un segundo punto de venta de la MISMA empresa — task 2.16 (el relink concurrente mueve
        // la orden acá).
        var idEmpresa = await db.PuntosVenta.Where(pv => pv.Id == resultado.IdPuntoVenta).Select(pv => pv.IdEmpresa).FirstAsync();
        var puntoVenta2 = new PuntoVenta
        {
            IdTenant = resultado.IdTenant, IdEmpresa = idEmpresa, Nombre = $"{nombre}-PV2", CreatedAt = ahora, UpdatedAt = ahora
        };
        db.PuntosVenta.Add(puntoVenta2);
        await db.SaveChangesAsync();

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "OC-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva21 = await db.AlicuotasIva.Where(a => a.Nombre == "21%").Select(a => a.Id).FirstAsync();

        var condicionFiscal = new CondicionFiscal { Codigo = $"{nombre}-CF", Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.CondicionesFiscales.Add(condicionFiscal);
        await db.SaveChangesAsync();

        var proveedor = new Proveedor
        {
            IdTenant = resultado.IdTenant, RazonSocial = nombre, IdCondicionFiscal = condicionFiscal.Id,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Proveedores.Add(proveedor);
        await db.SaveChangesAsync();

        var articulo1 = new Articulo
        {
            IdTenant = resultado.IdTenant, CodigoInterno = $"{nombre}-oc-1-{Guid.NewGuid():N}", Nombre = "OC Articulo 1",
            IdArea = area.Id, IdAlicuotaIva = idAlicuotaIva21, UnidadVenta = UnidadVenta.Unidad, EsProducto = true,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        var articulo2 = new Articulo
        {
            IdTenant = resultado.IdTenant, CodigoInterno = $"{nombre}-oc-2-{Guid.NewGuid():N}", Nombre = "OC Articulo 2",
            IdArea = area.Id, IdAlicuotaIva = idAlicuotaIva21, UnidadVenta = UnidadVenta.Unidad, EsProducto = true,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.AddRange(articulo1, articulo2);
        await db.SaveChangesAsync();

        var hasheador = new HasheadorPbkdf2();
        var mailVendedor = $"{nombre.ToLowerInvariant()}-vend@ways.test";
        db.Usuarios.Add(new Usuario
        {
            IdTenant = resultado.IdTenant, NombreUsuario = "oc-vendedor", Mail = mailVendedor, RolId = (int)RolConocido.Vendedor,
            PasswordHash = hasheador.Hashear(PasswordVendedor), PasswordAlgoritmo = hasheador.Algoritmo,
            PasswordActualizadoEl = ahora, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        var vendedor = fixture.CreateClient();
        var loginVendedor = await vendedor.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mailVendedor, PasswordVendedor));
        Assert.Equal(HttpStatusCode.OK, loginVendedor.StatusCode);

        return new Contexto(
            resultado.IdTenant, resultado.IdPuntoVenta, puntoVenta2.Id, admin, vendedor,
            proveedor.Id, articulo1.Id, articulo2.Id, mailAdmin, passwordAdmin!);
    }

    private static SolicitudDeOrdenDeCompra SolicitudSimple(
        Contexto ctx, decimal cantidad = 10m, decimal? costo = 100m, int? idPuntoVenta = null, int? idArticulo = null) =>
        new(
            ctx.IdProveedor, idPuntoVenta ?? ctx.IdPuntoVenta, null, null,
            [new LineaDeOrdenSolicitada(idArticulo ?? ctx.IdArticulo, "Item de prueba", cantidad, costo)]);

    private static SolicitudDeOrdenDeCompra SolicitudSinItems(Contexto ctx, int? idPuntoVenta = null) =>
        new(ctx.IdProveedor, idPuntoVenta ?? ctx.IdPuntoVenta, null, null, []);

    private static async Task<OrdenDeCompraBorrador> CrearBorradorAsync(
        HttpClient cliente, SolicitudDeOrdenDeCompra solicitud)
    {
        var respuesta = await cliente.PostAsJsonAsync("/api/ordenes-compra", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<OrdenDeCompraBorrador>(cuerpo, OpcionesJson)!;
    }

    // ---- task 2.8/2.9: replace-set del borrador ----------------------------------------------------

    [Fact]
    public async Task ItemsSeReemplazanCompletosEnUnRequestAgregandoYQuitando()
    {
        var ctx = await PrepararAsync(nameof(ItemsSeReemplazanCompletosEnUnRequestAgregandoYQuitando));
        var creada = await CrearBorradorAsync(ctx.Admin, SolicitudSimple(ctx));
        Assert.Single(creada.Items);

        // Regla 12c: una OC HERMANA del mismo tenant con items propios discriminantes — el
        // replace-set de "creada" nunca debe tocarla (el DELETE del replace-set debe seguir
        // scopeado a IdOrdenCompra == creada.Id, jamás ensanchado a toda la tabla).
        var hermana = await CrearBorradorAsync(
            ctx.Admin,
            new SolicitudDeOrdenDeCompra(
                ctx.IdProveedor, ctx.IdPuntoVenta, null, null,
                [
                    new LineaDeOrdenSolicitada(ctx.IdArticulo, "Hermana item 1", 77m, 770m),
                    new LineaDeOrdenSolicitada(ctx.IdArticulo2, "Hermana item 2", 88m, 880m)
                ]));
        Assert.Equal(2, hermana.Items.Count);

        var conDosItems = new SolicitudDeOrdenDeCompra(
            ctx.IdProveedor, ctx.IdPuntoVenta, null, null,
            [
                new LineaDeOrdenSolicitada(ctx.IdArticulo, "Item 1", 5m, 50m),
                new LineaDeOrdenSolicitada(ctx.IdArticulo2, "Item 2", 3m, 30m)
            ]);
        var respuestaPut = await ctx.Admin.PutAsJsonAsync($"/api/ordenes-compra/{creada.Id}", conDosItems);
        var cuerpoPut = await respuestaPut.Content.ReadAsStringAsync();
        Assert.True(respuestaPut.StatusCode == HttpStatusCode.OK, cuerpoPut);
        var actualizada = JsonSerializer.Deserialize<OrdenDeCompraBorrador>(cuerpoPut, OpcionesJson)!;
        Assert.Equal(2, actualizada.Items.Count);
        Assert.Equal([1, 2], actualizada.Items.Select(i => i.Orden));

        var conUnItemDistinto = new SolicitudDeOrdenDeCompra(
            ctx.IdProveedor, ctx.IdPuntoVenta, null, null,
            [new LineaDeOrdenSolicitada(ctx.IdArticulo2, "Solo item 2", 9m, 90m)]);
        var respuestaPut2 = await ctx.Admin.PutAsJsonAsync($"/api/ordenes-compra/{creada.Id}", conUnItemDistinto);
        Assert.Equal(HttpStatusCode.OK, respuestaPut2.StatusCode);
        var final = (await respuestaPut2.Content.ReadFromJsonAsync<OrdenDeCompraBorrador>(OpcionesJson))!;
        Assert.Single(final.Items);
        Assert.Equal(ctx.IdArticulo2, final.Items[0].IdArticulo);
        Assert.Equal(1, final.Items[0].Orden); // server-reasignado, nunca hereda el 2 del request anterior

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(1, await db.ItemsOrdenCompra.CountAsync(i => i.IdOrdenCompra == creada.Id));

        // Regla 12c: los DOS replace-sets de arriba sobre "creada" (agregar+quitar) no tocaron a
        // la hermana — sus items siguen siendo exactamente los dos originales, con las mismas
        // cantidades discriminantes.
        var itemsHermana = await db.ItemsOrdenCompra
            .Where(i => i.IdOrdenCompra == hermana.Id)
            .OrderBy(i => i.Orden)
            .ToListAsync();
        Assert.Equal(2, itemsHermana.Count);
        Assert.Equal(ctx.IdArticulo, itemsHermana[0].IdArticulo);
        Assert.Equal(77m, itemsHermana[0].CantidadPedida);
        Assert.Equal(ctx.IdArticulo2, itemsHermana[1].IdArticulo);
        Assert.Equal(88m, itemsHermana[1].CantidadPedida);
    }

    // ---- task 2.8/2.9: FechaEsperada/Observaciones sobreviven el ciclo completo (dto-contract-honesty) ----

    /// <summary>dto-contract-honesty regla 1: <c>FechaEsperada</c>/<c>Observaciones</c> no son
    /// relleno — se persisten y se leen de vuelta con el valor exacto enviado, y el replace-set
    /// del PUT los CAMBIA (incluida la limpieza explícita a <c>null</c>), nunca los ignora ni los
    /// fuerza a <c>null</c> por default.</summary>
    [Fact]
    public async Task FechaEsperadaYObservacionesSePersistenYSeActualizanEnElReplaceSet()
    {
        var ctx = await PrepararAsync(nameof(FechaEsperadaYObservacionesSePersistenYSeActualizanEnElReplaceSet));
        var fechaEsperadaInicial = new DateOnly(2026, 9, 15);
        var solicitudInicial = new SolicitudDeOrdenDeCompra(
            ctx.IdProveedor, ctx.IdPuntoVenta, fechaEsperadaInicial, "Observación inicial discriminante",
            [new LineaDeOrdenSolicitada(ctx.IdArticulo, "Item de prueba", 10m, 100m)]);

        var creada = await CrearBorradorAsync(ctx.Admin, solicitudInicial);
        Assert.Equal(fechaEsperadaInicial, creada.FechaEsperada);
        Assert.Equal("Observación inicial discriminante", creada.Observaciones);

        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var persistida = await db.OrdenesCompra.AsNoTracking().FirstAsync(o => o.Id == creada.Id);
            Assert.Equal(fechaEsperadaInicial, persistida.FechaEsperada);
            Assert.Equal("Observación inicial discriminante", persistida.Observaciones);
        }

        var fechaEsperadaNueva = new DateOnly(2026, 12, 24);
        var solicitudActualizada = new SolicitudDeOrdenDeCompra(
            ctx.IdProveedor, ctx.IdPuntoVenta, fechaEsperadaNueva, "Observación actualizada distinta",
            [new LineaDeOrdenSolicitada(ctx.IdArticulo, "Item de prueba", 10m, 100m)]);
        var respuestaPut = await ctx.Admin.PutAsJsonAsync($"/api/ordenes-compra/{creada.Id}", solicitudActualizada);
        var cuerpoPut = await respuestaPut.Content.ReadAsStringAsync();
        Assert.True(respuestaPut.StatusCode == HttpStatusCode.OK, cuerpoPut);
        var actualizada = JsonSerializer.Deserialize<OrdenDeCompraBorrador>(cuerpoPut, OpcionesJson)!;
        Assert.Equal(fechaEsperadaNueva, actualizada.FechaEsperada);
        Assert.Equal("Observación actualizada distinta", actualizada.Observaciones);

        var solicitudLimpiando = new SolicitudDeOrdenDeCompra(
            ctx.IdProveedor, ctx.IdPuntoVenta, null, null,
            [new LineaDeOrdenSolicitada(ctx.IdArticulo, "Item de prueba", 10m, 100m)]);
        var respuestaPutLimpiando = await ctx.Admin.PutAsJsonAsync($"/api/ordenes-compra/{creada.Id}", solicitudLimpiando);
        var cuerpoPutLimpiando = await respuestaPutLimpiando.Content.ReadAsStringAsync();
        Assert.True(respuestaPutLimpiando.StatusCode == HttpStatusCode.OK, cuerpoPutLimpiando);
        var limpia = JsonSerializer.Deserialize<OrdenDeCompraBorrador>(cuerpoPutLimpiando, OpcionesJson)!;
        Assert.Null(limpia.FechaEsperada);
        Assert.Null(limpia.Observaciones);

        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var final = await db.OrdenesCompra.AsNoTracking().FirstAsync(o => o.Id == creada.Id);
            Assert.Null(final.FechaEsperada);
            Assert.Null(final.Observaciones);
        }
    }

    [Fact]
    public async Task EditarUnaOrdenNoBorradorEsRechazada409()
    {
        var ctx = await PrepararAsync(nameof(EditarUnaOrdenNoBorradorEsRechazada409));
        var creada = await CrearBorradorAsync(ctx.Admin, SolicitudSimple(ctx));

        var enviar = await ctx.Admin.PostAsync($"/api/ordenes-compra/{creada.Id}/enviar", null);
        Assert.Equal(HttpStatusCode.OK, enviar.StatusCode);

        var respuesta = await ctx.Admin.PutAsJsonAsync($"/api/ordenes-compra/{creada.Id}", SolicitudSimple(ctx));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("orden_compra_no_editable", problema.GetProperty("codigo").GetString());
    }

    // ---- task 2.10/2.11: enviar asigna numero propio, per PV ---------------------------------------

    [Fact]
    public async Task EnviarAsignaElPrimerNumeroParaUnPuntoDeVentaFresco()
    {
        var ctx = await PrepararAsync(nameof(EnviarAsignaElPrimerNumeroParaUnPuntoDeVentaFresco));
        var creada = await CrearBorradorAsync(ctx.Admin, SolicitudSimple(ctx));
        Assert.Null(creada.Numero);

        var respuesta = await ctx.Admin.PostAsync($"/api/ordenes-compra/{creada.Id}/enviar", null);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        var enviada = JsonSerializer.Deserialize<OrdenDeCompraBorrador>(cuerpo, OpcionesJson)!;

        Assert.Equal(1, enviada.Numero);
        Assert.NotNull(enviada.FechaEnvio);
        Assert.Equal(EstadoOrdenCompra.Enviada, enviada.Estado);
    }

    [Fact]
    public async Task ReenviarUnaOrdenYaEnviadaEsRechazada409SinReasignarNumero()
    {
        var ctx = await PrepararAsync(nameof(ReenviarUnaOrdenYaEnviadaEsRechazada409SinReasignarNumero));
        var creada = await CrearBorradorAsync(ctx.Admin, SolicitudSimple(ctx));
        var primerEnvio = await ctx.Admin.PostAsync($"/api/ordenes-compra/{creada.Id}/enviar", null);
        Assert.Equal(HttpStatusCode.OK, primerEnvio.StatusCode);
        var enviada = (await primerEnvio.Content.ReadFromJsonAsync<OrdenDeCompraBorrador>(OpcionesJson))!;

        var segundoEnvio = await ctx.Admin.PostAsync($"/api/ordenes-compra/{creada.Id}/enviar", null);

        Assert.Equal(HttpStatusCode.Conflict, segundoEnvio.StatusCode);
        var problema = await segundoEnvio.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("orden_compra_no_enviable", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var actual = await db.OrdenesCompra.FirstAsync(o => o.Id == creada.Id);
        Assert.Equal(enviada.Numero, actual.Numero);
    }

    // ---- task 2.12: conflict #3 — OC vacía ----------------------------------------------------------

    [Fact]
    public async Task EnviarUnaOrdenSinItemsEsRechazadaConOrdenCompraSinItems400()
    {
        var ctx = await PrepararAsync(nameof(EnviarUnaOrdenSinItemsEsRechazadaConOrdenCompraSinItems400));
        var creada = await CrearBorradorAsync(ctx.Admin, SolicitudSinItems(ctx));

        var respuesta = await ctx.Admin.PostAsync($"/api/ordenes-compra/{creada.Id}/enviar", null);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("orden_compra_sin_items", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var actual = await db.OrdenesCompra.FirstAsync(o => o.Id == creada.Id);
        Assert.Equal(EstadoOrdenCompra.Borrador, actual.Estado);
        Assert.Null(actual.Numero);
    }

    // ---- task 2.13: mutation target #16 — el offset -03:00 sobrevive la normalización -------------

    private sealed class RelojFijo(DateTimeOffset ahora) : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = ahora;
    }

    /// <summary>Decisión 13 (tasks.md): <c>RelojFijo</c> devuelve el mismo instante que
    /// <c>2026-08-19T12:00:00Z</c> pero expresado con offset real <c>-03:00</c> — si
    /// <c>EnviarHeaderAsync</c> alguna vez construyera el parámetro de <c>fecha_envio</c> a mano
    /// (sin pasar por <see cref="ParametrosDeComando"/>, mutation target #16), Npgsql rechaza
    /// escribir un <c>timestamptz</c> con offset != 0 y la respuesta pasaría de <c>200</c> a un
    /// <c>500</c> silencioso.</summary>
    [Fact]
    public async Task EnviarConOffsetMenosTresPersisteElInstanteFijoExacto()
    {
        var instanteFijo = new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
        var mismoInstanteConOffset = new DateTimeOffset(2026, 8, 19, 9, 0, 0, TimeSpan.FromHours(-3));
        Assert.Equal(instanteFijo, mismoInstanteConOffset);

        using var factoryConRelojFijo = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddSingleton<IRelojDelSistema>(new RelojFijo(mismoInstanteConOffset))));

        var ctx = await PrepararAsyncConFactory(
            nameof(EnviarConOffsetMenosTresPersisteElInstanteFijoExacto), factoryConRelojFijo);
        var creada = await CrearBorradorAsync(ctx.Admin, SolicitudSimple(ctx));

        var respuesta = await ctx.Admin.PostAsync($"/api/ordenes-compra/{creada.Id}/enviar", null);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var actual = await db.OrdenesCompra.FirstAsync(o => o.Id == creada.Id);
        Assert.Equal(instanteFijo, actual.FechaEnvio);
    }

    // ---- task 2.14/2.15: los DOS binding gate tests de concurrencia (conflict #1) -------------------

    /// <summary>Binding gate test (b), parte 1 (tasks.md decisión 4, spec: "Two concurrent enviar
    /// calls at the same punto de venta never collide"): dos OCs DISTINTAS del mismo punto de
    /// venta, enviadas en simultáneo, sacan números distintos — NINGUNA responde 409. Mutation
    /// target #12: si <c>AsignarComprometidoAsync</c> se reemplazara por <c>MAX(numero)+1</c>,
    /// ambas leerían el mismo máximo y colisionarían.</summary>
    [Fact]
    public async Task DosEnviarConcurrentesDeOrdenesDistintasEnElMismoPuntoDeVentaDanNumerosDistintosSin409()
    {
        var ctx = await PrepararAsync(nameof(DosEnviarConcurrentesDeOrdenesDistintasEnElMismoPuntoDeVentaDanNumerosDistintosSin409));
        var ordenA = await CrearBorradorAsync(ctx.Admin, SolicitudSimple(ctx));
        var ordenB = await CrearBorradorAsync(ctx.Admin, SolicitudSimple(ctx));

        var tareaA = ctx.Admin.PostAsync($"/api/ordenes-compra/{ordenA.Id}/enviar", null);
        var tareaB = ctx.Admin.PostAsync($"/api/ordenes-compra/{ordenB.Id}/enviar", null);
        var respuestas = await Task.WhenAll(tareaA, tareaB);

        foreach (var respuesta in respuestas)
        {
            Assert.NotEqual(HttpStatusCode.Conflict, respuesta.StatusCode);
            Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        }

        var enviadaA = await respuestas[0].Content.ReadFromJsonAsync<OrdenDeCompraBorrador>(OpcionesJson);
        var enviadaB = await respuestas[1].Content.ReadFromJsonAsync<OrdenDeCompraBorrador>(OpcionesJson);

        Assert.NotNull(enviadaA!.Numero);
        Assert.NotNull(enviadaB!.Numero);
        Assert.NotEqual(enviadaA.Numero, enviadaB.Numero);
    }

    /// <summary>Binding gate test (b), parte 2 (tasks.md decisión 4, design.md T1, conflict #1):
    /// dos <c>enviar</c> concurrentes de la MISMA OC producen un <c>200</c> + un <c>409</c> — nunca
    /// dos <c>200</c>. El número del perdedor queda quemado (design decisión 6, residuo aceptado):
    /// ambas tareas ya dibujaron un número ANTES del <c>UPDATE</c> final (el asignador comitea su
    /// propia transacción incondicionalmente), así que el conteo total de números consumidos para
    /// este PV es 2 aunque solo una OC haya quedado <c>enviada</c>.</summary>
    [Fact]
    public async Task DosEnviarConcurrentesDeLaMismaOrdenDanUn200YUn409ConNumeroQuemado()
    {
        var ctx = await PrepararAsync(nameof(DosEnviarConcurrentesDeLaMismaOrdenDanUn200YUn409ConNumeroQuemado));
        var creada = await CrearBorradorAsync(ctx.Admin, SolicitudSimple(ctx));

        var tareaA = ctx.Admin.PostAsync($"/api/ordenes-compra/{creada.Id}/enviar", null);
        var tareaB = ctx.Admin.PostAsync($"/api/ordenes-compra/{creada.Id}/enviar", null);
        var respuestas = await Task.WhenAll(tareaA, tareaB);

        var ganadores = respuestas.Count(r => r.StatusCode == HttpStatusCode.OK);
        var perdedores = respuestas.Count(r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(1, ganadores);
        Assert.Equal(1, perdedores);

        var perdedor = respuestas.First(r => r.StatusCode == HttpStatusCode.Conflict);
        var problema = await perdedor.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("orden_compra_no_enviable", problema.GetProperty("codigo").GetString());

        // Una siguiente OC del MISMO punto de venta prueba el hueco: el perdedor quemó un número
        // que nadie más va a ver, así que el próximo enviar salta a 3 (1=ganador, 2=quemado por el
        // perdedor, 3=el siguiente).
        var siguienteOrden = await CrearBorradorAsync(ctx.Admin, SolicitudSimple(ctx));
        var siguienteEnvio = await ctx.Admin.PostAsync($"/api/ordenes-compra/{siguienteOrden.Id}/enviar", null);
        Assert.Equal(HttpStatusCode.OK, siguienteEnvio.StatusCode);
        var siguienteEnviada = (await siguienteEnvio.Content.ReadFromJsonAsync<OrdenDeCompraBorrador>(OpcionesJson))!;
        Assert.Equal(3, siguienteEnviada.Numero);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(EstadoOrdenCompra.Enviada, (await db.OrdenesCompra.FirstAsync(o => o.Id == creada.Id)).Estado);
    }

    // ---- task 2.16: mutation target #11 — la carrera del relink de PV -------------------------------

    private sealed class InterceptorDePausaTrasIniciarLaTransaccion(
        TaskCompletionSource transaccionIniciada, TaskCompletionSource puedeContinuar) : DbTransactionInterceptor
    {
        public override async ValueTask<DbTransaction> TransactionStartedAsync(
            DbConnection connection, TransactionEndEventData eventData, DbTransaction transaction,
            CancellationToken cancellationToken = default)
        {
            transaccionIniciada.TrySetResult();
            await puedeContinuar.Task;
            return await base.TransactionStartedAsync(connection, eventData, transaction, cancellationToken);
        }
    }

    /// <summary>Mutation target #11 (task 2.16, design decisión 6): un <c>PUT</c> que mueve la OC
    /// del punto de venta 1 al punto de venta 2 gana la carrera y COMMITEA DESPUÉS de que el número
    /// ya fue dibujado (serie del PV 1) pero ANTES de que el <c>UPDATE</c> final de <c>enviar</c>
    /// corra — pausado justo tras <c>BeginTransactionAsync</c> de <c>EjecutarEnvioAsync</c>, mismo
    /// patrón que <c>ComprasAnulacionYConcurrenciaTests.InterceptorDePausaTrasIniciarLaTransaccion</c>.
    /// El <c>WHERE id_punto_venta = $pv</c> (pineado al PV 1, capturado en la pre-lectura) no
    /// matchea la fila ya movida al PV 2 ⇒ 0 filas ⇒ <c>409</c>, el número dibujado para el PV 1
    /// queda quemado SIN aparecer en ninguna orden — nunca aterriza en la serie del PV 2.</summary>
    [Fact]
    public async Task UnPutQueMuevePuntoDeVentaConcurrenteConEnviarReclasificaA409YElNumeroQuedaEnLaSerieVieja()
    {
        var ctx = await PrepararAsync(nameof(UnPutQueMuevePuntoDeVentaConcurrenteConEnviarReclasificaA409YElNumeroQuedaEnLaSerieVieja));
        var creada = await CrearBorradorAsync(ctx.Admin, SolicitudSimple(ctx, idPuntoVenta: ctx.IdPuntoVenta));

        var transaccionIniciada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var puedeContinuar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interceptor = new InterceptorDePausaTrasIniciarLaTransaccion(transaccionIniciada, puedeContinuar);

        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddDbContext<WaysDbContext>((_, options) => options.AddInterceptors(interceptor))));

        using var clienteEnviar = factory.CreateClient();
        var login = await clienteEnviar.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(ctx.MailAdmin, ctx.PasswordAdmin));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var tareaEnviar = clienteEnviar.PostAsync($"/api/ordenes-compra/{creada.Id}/enviar", null);

        await transaccionIniciada.Task;

        // El PUT gana la carrera: mueve la OC al PV 2 y COMMITEA antes de que el UPDATE final de
        // enviar corra.
        var solicitudRelink = new SolicitudDeOrdenDeCompra(
            ctx.IdProveedor, ctx.IdPuntoVenta2, null, null,
            [new LineaDeOrdenSolicitada(ctx.IdArticulo, "Item de prueba", 10m, 100m)]);
        var respuestaPut = await ctx.Admin.PutAsJsonAsync($"/api/ordenes-compra/{creada.Id}", solicitudRelink);
        var cuerpoPut = await respuestaPut.Content.ReadAsStringAsync();
        Assert.True(respuestaPut.StatusCode == HttpStatusCode.OK, cuerpoPut);

        puedeContinuar.TrySetResult();

        var respuestaEnviar = await tareaEnviar;
        var cuerpoEnviar = await respuestaEnviar.Content.ReadAsStringAsync();
        Assert.True(respuestaEnviar.StatusCode == HttpStatusCode.Conflict, cuerpoEnviar);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpoEnviar, OpcionesJson);
        Assert.Equal("orden_compra_no_enviable", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var actual = await db.OrdenesCompra.FirstAsync(o => o.Id == creada.Id);
        Assert.Equal(EstadoOrdenCompra.Borrador, actual.Estado);
        Assert.Null(actual.Numero);
        Assert.Equal(ctx.IdPuntoVenta2, actual.IdPuntoVenta);

        // El número quemado (serie del PV 1) nunca aparece en ninguna orden de ese punto de venta:
        // un envío siguiente del PV 1 salta directo al 2.
        var siguienteOrdenPv1 = await CrearBorradorAsync(ctx.Admin, SolicitudSimple(ctx, idPuntoVenta: ctx.IdPuntoVenta));
        var siguienteEnvioPv1 = await ctx.Admin.PostAsync($"/api/ordenes-compra/{siguienteOrdenPv1.Id}/enviar", null);
        Assert.Equal(HttpStatusCode.OK, siguienteEnvioPv1.StatusCode);
        var siguienteEnviadaPv1 = (await siguienteEnvioPv1.Content.ReadFromJsonAsync<OrdenDeCompraBorrador>(OpcionesJson))!;
        Assert.Equal(2, siguienteEnviadaPv1.Numero);
    }

    // ---- autorización — spot-check (la matriz completa la cierra la slice 4) ------------------------

    [Fact]
    public async Task VendedorEsBloqueadoEnElCicloDeEscrituraDeOrdenesDeCompra()
    {
        var ctx = await PrepararAsync(nameof(VendedorEsBloqueadoEnElCicloDeEscrituraDeOrdenesDeCompra));
        var creada = await CrearBorradorAsync(ctx.Admin, SolicitudSimple(ctx));

        var crear = await ctx.Vendedor.PostAsJsonAsync("/api/ordenes-compra", SolicitudSimple(ctx));
        Assert.Equal(HttpStatusCode.Forbidden, crear.StatusCode);

        var editar = await ctx.Vendedor.PutAsJsonAsync($"/api/ordenes-compra/{creada.Id}", SolicitudSimple(ctx));
        Assert.Equal(HttpStatusCode.Forbidden, editar.StatusCode);

        var enviar = await ctx.Vendedor.PostAsync($"/api/ordenes-compra/{creada.Id}/enviar", null);
        Assert.Equal(HttpStatusCode.Forbidden, enviar.StatusCode);
    }

    // ---- gate guard (task 2.25) ----------------------------------------------------------------------

    /// <summary>Gate guard (task 2.25, decisión 2 de tasks.md): esta slice no agrega DDL — el
    /// modelo EF sigue coincidiendo exactamente con la migración de slice 1.</summary>
    [Fact]
    public async Task NoHayCambiosPendientesDeModeloRespectoDeLaMigracionDeLaSlice1()
    {
        using var _ = fixture.CreateClient();
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var hayPendientes = db.Database.HasPendingModelChanges();
        Assert.False(hayPendientes);
    }

    /// <summary>Variante de <see cref="PrepararAsync"/> que arranca el host desde un
    /// <c>WebApplicationFactory</c> ya configurado (p. ej. con <see cref="RelojFijo"/> inyectado) en
    /// vez del <see cref="fixture"/> por defecto — mismo criterio que
    /// <c>VencimientosReporteTests.PrepararAsync</c>.</summary>
    private async Task<Contexto> PrepararAsyncConFactory(
        string nombre, WebApplicationFactory<Program> factory)
    {
        using var root = factory.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var solicitud = new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin);
        var respuesta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = factory.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var idEmpresa = await db.PuntosVenta.Where(pv => pv.Id == resultado.IdPuntoVenta).Select(pv => pv.IdEmpresa).FirstAsync();
        var puntoVenta2 = new PuntoVenta
        {
            IdTenant = resultado.IdTenant, IdEmpresa = idEmpresa, Nombre = $"{nombre}-PV2", CreatedAt = ahora, UpdatedAt = ahora
        };
        db.PuntosVenta.Add(puntoVenta2);
        await db.SaveChangesAsync();

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "OC-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva21 = await db.AlicuotasIva.Where(a => a.Nombre == "21%").Select(a => a.Id).FirstAsync();

        var condicionFiscal = new CondicionFiscal { Codigo = $"{nombre}-CF", Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.CondicionesFiscales.Add(condicionFiscal);
        await db.SaveChangesAsync();

        var proveedor = new Proveedor
        {
            IdTenant = resultado.IdTenant, RazonSocial = nombre, IdCondicionFiscal = condicionFiscal.Id,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Proveedores.Add(proveedor);
        await db.SaveChangesAsync();

        var articulo1 = new Articulo
        {
            IdTenant = resultado.IdTenant, CodigoInterno = $"{nombre}-oc-1-{Guid.NewGuid():N}", Nombre = "OC Articulo 1",
            IdArea = area.Id, IdAlicuotaIva = idAlicuotaIva21, UnidadVenta = UnidadVenta.Unidad, EsProducto = true,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.Add(articulo1);
        await db.SaveChangesAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdPuntoVenta, puntoVenta2.Id, admin, admin,
            proveedor.Id, articulo1.Id, articulo1.Id, mailAdmin, resultado.PasswordTemporal);
    }
}
