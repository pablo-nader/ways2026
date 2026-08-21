using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Ways.Application.Abstracciones;
using Ways.Application.Ofertas;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Application.Ventas;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-17-presupuestos-y-remitos, Slice 2 (tasks 2.1-2.23; design: Transactions — ENVIAR
/// PRESUPUESTO; API Surface). Borrador CRUD (replace-set) + <c>enviar</c> con numeración propia
/// (serie <c>'PRES'</c>, consumida ANTES de la transacción de escritura — mismo patrón que
/// <see cref="ServicioDeOrdenesDeCompraTests"/>) + <c>anular</c> + el listado/detalle con
/// <c>Vencido</c>/<c>Convertible</c> derivados en la zona horaria del punto de venta.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ServicioDePresupuestosTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    // Literal fijo del pasado, jamas derivado del reloj real: los tests que pinean el reloj en
    // instantes historicos necesitan un precio ya vigente bajo AMBOS relojes, hoy y siempre —
    // un seed relativo a UtcNow se vuelve una bomba de calendario apenas la fecha real avanza.
    private static readonly DateTimeOffset InicioDeVigenciaFijo = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordVendedor = "vendedor-password-larga";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, int IdPuntoVenta2, HttpClient Root, HttpClient Admin,
        HttpClient Vendedor, int IdArea, int IdAlicuotaIva, int IdListaPrecio, int IdCliente, int IdArticulo,
        int IdArticulo2, string MailAdmin, string PasswordAdmin);

    /// <summary>Decisión 13 (tasks.md): ids deliberadamente desincronizados — cada entidad nace en
    /// su propia tabla con su propia identidad autoincremental, nunca forzada a coincidir.</summary>
    private async Task<Contexto> PrepararAsync(string nombre) => await PrepararConFactoryAsync(nombre, null);

    private async Task<Contexto> PrepararConFactoryAsync(string nombre, WebApplicationFactory<Program>? factory)
    {
        var root = factory is null ? fixture.CreateClient() : factory.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var solicitud = new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin);
        var respuesta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = factory is null ? fixture.CreateClient() : factory.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var puntoVenta2 = new PuntoVenta
        {
            IdTenant = resultado.IdTenant, IdEmpresa = resultado.IdEmpresa, Nombre = $"{nombre}-PV2",
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.PuntosVenta.Add(puntoVenta2);
        await db.SaveChangesAsync();

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Pres-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Where(a => a.Nombre == "21%").Select(a => a.Id).FirstAsync();

        var lista = new ListaPrecio
        {
            IdTenant = resultado.IdTenant, Nombre = "Lista de Presupuestos", EsDefault = false, Modo = ModoLista.Fija,
            Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ListasPrecio.Add(lista);
        await db.SaveChangesAsync();

        var idCondicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync();
        var cliente = new Cliente
        {
            IdTenant = resultado.IdTenant, Numero = 1000 + Random.Shared.Next(1, 100_000), Nombre = $"{nombre}-cliente",
            IdCondicionFiscal = idCondicionFiscal, IdListaPrecio = lista.Id, LimiteCredito = 0,
            CreditoIlimitado = true, Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Clientes.Add(cliente);
        await db.SaveChangesAsync();

        var articulo1 = new Articulo
        {
            IdTenant = resultado.IdTenant, CodigoInterno = $"{nombre}-pres-1-{Guid.NewGuid():N}", Nombre = "Pres Articulo 1",
            IdArea = area.Id, IdAlicuotaIva = idAlicuotaIva, UnidadVenta = UnidadVenta.Unidad, EsProducto = true,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        var articulo2 = new Articulo
        {
            IdTenant = resultado.IdTenant, CodigoInterno = $"{nombre}-pres-2-{Guid.NewGuid():N}", Nombre = "Pres Articulo 2",
            IdArea = area.Id, IdAlicuotaIva = idAlicuotaIva, UnidadVenta = UnidadVenta.Unidad, EsProducto = true,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.AddRange(articulo1, articulo2);
        await db.SaveChangesAsync();

        db.Precios.AddRange(
            new Precio
            {
                IdTenant = resultado.IdTenant, IdArticulo = articulo1.Id, IdListaPrecio = lista.Id, Monto = 100m,
                VigenteDesde = InicioDeVigenciaFijo, VigenteHasta = null, CreatedAt = ahora, UpdatedAt = ahora
            },
            new Precio
            {
                IdTenant = resultado.IdTenant, IdArticulo = articulo2.Id, IdListaPrecio = lista.Id, Monto = 250m,
                VigenteDesde = InicioDeVigenciaFijo, VigenteHasta = null, CreatedAt = ahora, UpdatedAt = ahora
            });
        await db.SaveChangesAsync();

        var hasheador = new HasheadorPbkdf2();
        var mailVendedor = $"{nombre.ToLowerInvariant()}-vend@ways.test";
        db.Usuarios.Add(new Usuario
        {
            IdTenant = resultado.IdTenant, NombreUsuario = "pres-vendedor", Mail = mailVendedor, RolId = (int)RolConocido.Vendedor,
            PasswordHash = hasheador.Hashear(PasswordVendedor), PasswordAlgoritmo = hasheador.Algoritmo,
            PasswordActualizadoEl = ahora, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        var vendedor = factory is null ? fixture.CreateClient() : factory.CreateClient();
        var loginVendedor = await vendedor.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mailVendedor, PasswordVendedor));
        Assert.Equal(HttpStatusCode.OK, loginVendedor.StatusCode);

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, puntoVenta2.Id, root, admin, vendedor,
            area.Id, idAlicuotaIva, lista.Id, cliente.Id, articulo1.Id, articulo2.Id, mailAdmin, resultado.PasswordTemporal);
    }

    private static SolicitudDePresupuesto SolicitudSimple(Contexto ctx, decimal cantidad = 2m, int? idPuntoVenta = null) =>
        new(idPuntoVenta ?? ctx.IdPuntoVenta, ctx.IdCliente, "obs", [new LineaDePresupuesto(ctx.IdArticulo, cantidad)]);

    private static SolicitudDePresupuesto SolicitudSinItems(Contexto ctx, int? idPuntoVenta = null) =>
        new(idPuntoVenta ?? ctx.IdPuntoVenta, ctx.IdCliente, null, []);

    private static async Task<PresupuestoDetalle> CrearBorradorAsync(HttpClient cliente, SolicitudDePresupuesto solicitud)
    {
        var respuesta = await cliente.PostAsJsonAsync("/api/presupuestos", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<PresupuestoDetalle>(cuerpo, OpcionesJson)!;
    }

    /// <summary>Mismo shape que <c>OfertasResolucionTests.OfertaDeArticulo</c> — oferta directa
    /// (sin <c>cantidad_minima</c>), sin ventana de vigencia, alcance a un único artículo.</summary>
    private static AltaOferta OfertaDeArticulo(int idArticulo, decimal porcentaje) => new(
        Nombre: "oferta de prueba", IdEmpresa: null, IdArticulo: idArticulo, IdGrupo: null, IdCategoria: null,
        FechaDesde: null, FechaHasta: null, HoraDesde: null, HoraHasta: null, DiasSemana: null,
        CantidadMinima: null, PrecioUnitario: null, Porcentaje: porcentaje, ImporteFijo: null,
        Prioridad: 0, Acumulable: false);

    private static async Task CrearOfertaAsync(HttpClient cliente, AltaOferta datos)
    {
        var respuesta = await cliente.PostAsJsonAsync("/api/ofertas", datos);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
    }

    // ---- task 2.2: crear borrador, precio resuelto al guardar --------------------------------------

    [Fact]
    public async Task UnBorradorSinItemsPersisteConNumeroYVencimientoNulos()
    {
        var ctx = await PrepararAsync(nameof(UnBorradorSinItemsPersisteConNumeroYVencimientoNulos));
        var creado = await CrearBorradorAsync(ctx.Admin, SolicitudSinItems(ctx));

        Assert.Null(creado.Numero);
        Assert.Null(creado.NumeroFormateado);
        Assert.Null(creado.Vencimiento);
        Assert.Empty(creado.Items);
        Assert.Equal(EstadoPresupuesto.Borrador, creado.Estado);
    }

    [Fact]
    public async Task UnBorradorConItemsResuelveElPrecioVigenteAlGuardar()
    {
        var ctx = await PrepararAsync(nameof(UnBorradorConItemsResuelveElPrecioVigenteAlGuardar));
        var creado = await CrearBorradorAsync(
            ctx.Admin,
            new SolicitudDePresupuesto(
                ctx.IdPuntoVenta, ctx.IdCliente, null,
                [new LineaDePresupuesto(ctx.IdArticulo, 3m), new LineaDePresupuesto(ctx.IdArticulo2, 2m)]));

        Assert.Equal(2, creado.Items.Count);
        var item1 = creado.Items.Single(i => i.IdArticulo == ctx.IdArticulo);
        var item2 = creado.Items.Single(i => i.IdArticulo == ctx.IdArticulo2);
        Assert.Equal(100m, item1.PrecioUnitario);
        Assert.Equal(300m, item1.Total);
        Assert.Equal(250m, item2.PrecioUnitario);
        Assert.Equal(500m, item2.Total);
        Assert.Equal(800m, creado.Total);
        Assert.Equal(1, item1.Orden);
        Assert.Equal(2, item2.Orden);
    }

    // ---- task 2.3/2.17: replace-set completo, hermana intacta (mutation targets #12/#13) -----------

    [Fact]
    public async Task ElReplaceSetReemplazaLosItemsCompletosSinTocarUnPresupuestoHermano()
    {
        var ctx = await PrepararAsync(nameof(ElReplaceSetReemplazaLosItemsCompletosSinTocarUnPresupuestoHermano));
        var creado = await CrearBorradorAsync(ctx.Admin, SolicitudSimple(ctx, cantidad: 1m));
        Assert.Single(creado.Items);

        // Regla 12c: un presupuesto HERMANO del mismo tenant, con sus propios items — el
        // replace-set de "creado" nunca debe tocarlo (mutation target #13: el RemoveRange tiene
        // que quedar scopeado a IdPresupuesto, jamás ensanchado a toda la tabla).
        var hermano = await CrearBorradorAsync(
            ctx.Admin,
            new SolicitudDePresupuesto(
                ctx.IdPuntoVenta, ctx.IdCliente, null,
                [new LineaDePresupuesto(ctx.IdArticulo, 7m), new LineaDePresupuesto(ctx.IdArticulo2, 8m)]));
        Assert.Equal(2, hermano.Items.Count);

        var conDosItems = new SolicitudDePresupuesto(
            ctx.IdPuntoVenta, ctx.IdCliente, "editado",
            [new LineaDePresupuesto(ctx.IdArticulo, 5m), new LineaDePresupuesto(ctx.IdArticulo2, 4m)]);
        var respuesta = await ctx.Admin.PutAsJsonAsync($"/api/presupuestos/{creado.Id}", conDosItems);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        var editado = JsonSerializer.Deserialize<PresupuestoDetalle>(cuerpo, OpcionesJson)!;

        Assert.Equal(2, editado.Items.Count);
        Assert.Equal("editado", editado.Observaciones);
        Assert.Equal(1, editado.Items.Single(i => i.IdArticulo == ctx.IdArticulo).Orden);
        Assert.Equal(2, editado.Items.Single(i => i.IdArticulo == ctx.IdArticulo2).Orden);

        // El hermano sigue intacto: mismo count, mismos items, por identidad.
        var hermanoActual = await ctx.Admin.GetFromJsonAsync<PresupuestoDetalle>(
            $"/api/presupuestos/{hermano.Id}", OpcionesJson);
        Assert.Equal(2, hermanoActual!.Items.Count);
        Assert.Equal(7m, hermanoActual.Items.Single(i => i.IdArticulo == ctx.IdArticulo).Cantidad);
        Assert.Equal(8m, hermanoActual.Items.Single(i => i.IdArticulo == ctx.IdArticulo2).Cantidad);
    }

    // ---- task 2.18: mutation target #12 — editar un no-borrador es rechazado -----------------------

    [Fact]
    public async Task EditarUnPresupuestoEnviadoEsRechazado409PresupuestoNoEditable()
    {
        var ctx = await PrepararAsync(nameof(EditarUnPresupuestoEnviadoEsRechazado409PresupuestoNoEditable));
        var creado = await CrearBorradorAsync(ctx.Admin, SolicitudSimple(ctx));

        var enviar = await ctx.Admin.PostAsJsonAsync($"/api/presupuestos/{creado.Id}/enviar", new SolicitudDeEnvio(FechaFutura()));
        Assert.Equal(HttpStatusCode.OK, enviar.StatusCode);

        var respuesta = await ctx.Admin.PutAsJsonAsync($"/api/presupuestos/{creado.Id}", SolicitudSimple(ctx));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("presupuesto_no_editable", problema.GetProperty("codigo").GetString());
    }

    // ---- task 2.6/2.15: enviar un presupuesto sin items --------------------------------------------

    [Fact]
    public async Task EnviarUnPresupuestoSinItemsEsRechazado400PresupuestoSinItemsSinConsumirNumero()
    {
        var ctx = await PrepararAsync(nameof(EnviarUnPresupuestoSinItemsEsRechazado400PresupuestoSinItemsSinConsumirNumero));
        var creado = await CrearBorradorAsync(ctx.Admin, SolicitudSinItems(ctx));

        var respuesta = await ctx.Admin.PostAsJsonAsync($"/api/presupuestos/{creado.Id}/enviar", new SolicitudDeEnvio(FechaFutura()));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("presupuesto_sin_items", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var actual = await db.Presupuestos.FirstAsync(p => p.Id == creado.Id);
        Assert.Equal(EstadoPresupuesto.Borrador, actual.Estado);
        Assert.Null(actual.Numero);
    }

    // ---- task 2.5: enviar con un vencimiento en el pasado ------------------------------------------

    [Fact]
    public async Task EnviarConUnVencimientoEnElPasadoEsRechazado400VencimientoInvalidoSinConsumirNumero()
    {
        var ctx = await PrepararAsync(nameof(EnviarConUnVencimientoEnElPasadoEsRechazado400VencimientoInvalidoSinConsumirNumero));
        var creado = await CrearBorradorAsync(ctx.Admin, SolicitudSimple(ctx));

        var vencimientoPasado = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-5);
        var respuesta = await ctx.Admin.PostAsJsonAsync($"/api/presupuestos/{creado.Id}/enviar", new SolicitudDeEnvio(vencimientoPasado));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("vencimiento_invalido", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var actual = await db.Presupuestos.FirstAsync(p => p.Id == creado.Id);
        Assert.Null(actual.Numero);
    }

    // ---- task 2.14: borde del día de vencimiento — convertible EN el día exacto --------------------

    [Fact]
    public async Task EnviarConVencimientoIgualAHoyEsAceptado()
    {
        var ctx = await PrepararAsync(nameof(EnviarConVencimientoIgualAHoyEsAceptado));
        var creado = await CrearBorradorAsync(ctx.Admin, SolicitudSimple(ctx));

        // Default zona_horaria: America/Argentina/Buenos_Aires (-03:00) — "hoy" local, sin
        // seedear ningún parametro.
        var hoyLocal = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(
            DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires")).DateTime);

        var respuesta = await ctx.Admin.PostAsJsonAsync($"/api/presupuestos/{creado.Id}/enviar", new SolicitudDeEnvio(hoyLocal));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
    }

    // ---- task 2.13/2.9: el borde -03:00 vs +05:30 (mutation-proof-tests rule 10) -------------------

    private sealed class RelojFijo(DateTimeOffset ahora) : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = ahora;
    }

    /// <summary>Mutation target #19 (design: decisión 10/11, mutation-proof-tests rule 10):
    /// <c>RelojFijo</c> en <c>2026-09-30T02:00:00Z</c>. En Buenos Aires (-03:00, zona DEFAULT sin
    /// seedear ningún parametro) el día local es el 29 — un presupuesto con <c>vencimiento =
    /// 2026-09-29</c> es aceptado (>= hoy local) y uno con el 28 es rechazado. Si <c>hoy</c> se
    /// resolviera con <c>reloj.Ahora.UtcDateTime</c> en vez de la zona del PV, el día "visto" sería
    /// el 30 y el 29 se rechazaría — el test discrimina exactamente esa mutación.</summary>
    [Fact]
    public async Task EnviarEnLaZonaMenosTresElVencimientoDelDiaLocalEsAceptadoYElDiaAnteriorRechazado()
    {
        var instanteFijo = new DateTimeOffset(2026, 9, 30, 2, 0, 0, TimeSpan.Zero);

        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddSingleton<IRelojDelSistema>(new RelojFijo(instanteFijo))));

        var ctx = await PrepararConFactoryAsync(
            nameof(EnviarEnLaZonaMenosTresElVencimientoDelDiaLocalEsAceptadoYElDiaAnteriorRechazado), factory);

        var aceptado = await CrearBorradorAsync(ctx.Admin, SolicitudSimple(ctx));
        var respuestaAceptada = await ctx.Admin.PostAsJsonAsync(
            $"/api/presupuestos/{aceptado.Id}/enviar", new SolicitudDeEnvio(new DateOnly(2026, 9, 29)));
        var cuerpoAceptada = await respuestaAceptada.Content.ReadAsStringAsync();
        Assert.True(respuestaAceptada.StatusCode == HttpStatusCode.OK, cuerpoAceptada);

        var rechazado = await CrearBorradorAsync(ctx.Admin, SolicitudSimple(ctx));
        var respuestaRechazada = await ctx.Admin.PostAsJsonAsync(
            $"/api/presupuestos/{rechazado.Id}/enviar", new SolicitudDeEnvio(new DateOnly(2026, 9, 28)));
        Assert.Equal(HttpStatusCode.BadRequest, respuestaRechazada.StatusCode);
    }

    /// <summary>El espejo a <c>+05:30</c> (<c>Asia/Kolkata</c>, sin horario de verano): al MISMO
    /// instante UTC, el día local es el 30 — la conclusión se INVIERTE respecto del test anterior.
    /// Solo un fixture con offset real (nunca <c>Z</c>) puede ver esta clase de regresión.</summary>
    [Fact]
    public async Task EnviarEnLaZonaMasCincoYMediaElVencimientoDelDiaLocalEsAceptadoYElDiaAnteriorRechazado()
    {
        var instanteFijo = new DateTimeOffset(2026, 9, 30, 2, 0, 0, TimeSpan.Zero);

        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddSingleton<IRelojDelSistema>(new RelojFijo(instanteFijo))));

        var ctx = await PrepararConFactoryAsync(
            nameof(EnviarEnLaZonaMasCincoYMediaElVencimientoDelDiaLocalEsAceptadoYElDiaAnteriorRechazado), factory);

        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            db.Parametros.Add(new Parametro
            {
                IdTenant = ctx.IdTenant, IdEmpresa = ctx.IdEmpresa, IdPuntoVenta = ctx.IdPuntoVenta,
                Clave = ParametroConocido.ZonaHoraria.Clave, Valor = JsonSerializer.Serialize("Asia/Kolkata"),
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var aceptado = await CrearBorradorAsync(ctx.Admin, SolicitudSimple(ctx));
        var respuestaAceptada = await ctx.Admin.PostAsJsonAsync(
            $"/api/presupuestos/{aceptado.Id}/enviar", new SolicitudDeEnvio(new DateOnly(2026, 9, 30)));
        var cuerpoAceptada = await respuestaAceptada.Content.ReadAsStringAsync();
        Assert.True(respuestaAceptada.StatusCode == HttpStatusCode.OK, cuerpoAceptada);

        var rechazado = await CrearBorradorAsync(ctx.Admin, SolicitudSimple(ctx));
        var respuestaRechazada = await ctx.Admin.PostAsJsonAsync(
            $"/api/presupuestos/{rechazado.Id}/enviar", new SolicitudDeEnvio(new DateOnly(2026, 9, 29)));
        Assert.Equal(HttpStatusCode.BadRequest, respuestaRechazada.StatusCode);
    }

    // ---- task 2.10/2.11: los DOS binding gate tests de concurrencia del enviar ---------------------

    /// <summary>Binding gate test, parte 1 (spec: "Two concurrent enviar calls at the same punto de
    /// venta never collide"; mutation target #15): dos presupuestos DISTINTOS del mismo PV,
    /// enviados en simultáneo, sacan números distintos — NINGUNO responde 409. Si
    /// <c>AsignarComprometidoAsync</c> se reemplazara por <c>MAX(numero)+1</c>, ambos leerían el
    /// mismo máximo y colisionarían.</summary>
    [Fact]
    public async Task DosEnviarConcurrentesDePresupuestosDistintosEnElMismoPuntoDeVentaDanNumerosDistintosSin409()
    {
        var ctx = await PrepararAsync(nameof(DosEnviarConcurrentesDePresupuestosDistintosEnElMismoPuntoDeVentaDanNumerosDistintosSin409));
        var presupuestoA = await CrearBorradorAsync(ctx.Admin, SolicitudSimple(ctx));
        var presupuestoB = await CrearBorradorAsync(ctx.Admin, SolicitudSimple(ctx));

        var vencimiento = FechaFutura();
        var tareaA = ctx.Admin.PostAsJsonAsync($"/api/presupuestos/{presupuestoA.Id}/enviar", new SolicitudDeEnvio(vencimiento));
        var tareaB = ctx.Admin.PostAsJsonAsync($"/api/presupuestos/{presupuestoB.Id}/enviar", new SolicitudDeEnvio(vencimiento));
        var respuestas = await Task.WhenAll(tareaA, tareaB);

        foreach (var respuesta in respuestas)
        {
            Assert.NotEqual(HttpStatusCode.Conflict, respuesta.StatusCode);
            Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        }

        var enviadoA = await respuestas[0].Content.ReadFromJsonAsync<PresupuestoDetalle>(OpcionesJson);
        var enviadoB = await respuestas[1].Content.ReadFromJsonAsync<PresupuestoDetalle>(OpcionesJson);

        Assert.NotNull(enviadoA!.Numero);
        Assert.NotNull(enviadoB!.Numero);
        Assert.NotEqual(enviadoA.Numero, enviadoB.Numero);
    }

    /// <summary>Interceptor de rendezvous DETERMINÍSTICO (mutation-proof-tests regla 2 — un
    /// <c>Task.WhenAll</c> desnudo demostró, corrido repetidas veces, que la ventana de carrera
    /// entre el pre-chequeo de <c>EnviarAsync</c> y el <c>UPDATE</c> guardado de
    /// <c>EjecutarEnvioAsync</c> puede resolverse ANTES de que el perdedor llegue a dibujar un
    /// número — un resultado igualmente válido del diseño (ambas ramas devuelven el mismo
    /// <c>presupuesto_ya_enviado</c> a propósito), pero que no prueba el hueco. Este interceptor
    /// distingue, por identidad del <see cref="DbContext"/> de cada request (uno por HTTP request,
    /// scoped), la SEGUNDA transacción que abre — la de <c>EjecutarEnvioAsync</c>, nunca la
    /// primera (la mini-transacción de <c>AsignadorDeNumeroComprobante</c>, que comitea sola y no
    /// se pausa) — y hace que la primera petición en llegar a esa segunda transacción ESPERE a que
    /// la segunda también llegue: garantiza que AMBAS ya dibujaron su número antes de que
    /// cualquiera intente el <c>UPDATE</c> final.</summary>
    private sealed class InterceptorDeRendezvousEnLaSegundaTransaccion : DbTransactionInterceptor
    {
        private readonly HashSet<object> _contextosVistos = [];
        private readonly TaskCompletionSource _primeraLlego = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _segundaLlego = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<DbTransaction> TransactionStartedAsync(
            DbConnection connection, TransactionEndEventData eventData, DbTransaction transaction,
            CancellationToken cancellationToken = default)
        {
            bool esLaSegundaTransaccionDeEsteContexto;
            lock (_contextosVistos)
            {
                esLaSegundaTransaccionDeEsteContexto = eventData.Context is not null
                    && !_contextosVistos.Add(eventData.Context);
            }

            if (esLaSegundaTransaccionDeEsteContexto)
            {
                if (!_primeraLlego.TrySetResult())
                {
                    // Ya había una esperando: esta es la segunda — libera a ambas.
                    _segundaLlego.TrySetResult();
                }
                else
                {
                    await _segundaLlego.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                }
            }

            return await base.TransactionStartedAsync(connection, eventData, transaction, cancellationToken);
        }
    }

    /// <summary>Binding gate test, parte 2 (design: Failure Semantics, "el número queda quemado —
    /// residuo aceptado"): dos <c>enviar</c> concurrentes del MISMO presupuesto producen un
    /// <c>200</c> + un <c>409</c> — nunca dos <c>200</c>. El interceptor de rendezvous fuerza que
    /// AMBAS peticiones ya hayan dibujado su número antes de que cualquiera corra el <c>UPDATE</c>
    /// final, así que el número del perdedor queda quemado por construcción: el siguiente
    /// presupuesto enviado del mismo PV salta el hueco exacto.</summary>
    [Fact]
    public async Task DosEnviarConcurrentesDelMismoPresupuestoDanUn200YUn409ConNumeroQuemado()
    {
        var interceptor = new InterceptorDeRendezvousEnLaSegundaTransaccion();

        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddDbContext<WaysDbContext>((_, options) => options.AddInterceptors(interceptor))));

        var ctx = await PrepararConFactoryAsync(nameof(DosEnviarConcurrentesDelMismoPresupuestoDanUn200YUn409ConNumeroQuemado), factory);
        var creado = await CrearBorradorAsync(ctx.Admin, SolicitudSimple(ctx));

        var vencimiento = FechaFutura();
        var tareaA = ctx.Admin.PostAsJsonAsync($"/api/presupuestos/{creado.Id}/enviar", new SolicitudDeEnvio(vencimiento));
        var tareaB = ctx.Admin.PostAsJsonAsync($"/api/presupuestos/{creado.Id}/enviar", new SolicitudDeEnvio(vencimiento));
        var respuestas = await Task.WhenAll(tareaA, tareaB);

        var ganadores = respuestas.Count(r => r.StatusCode == HttpStatusCode.OK);
        var perdedores = respuestas.Count(r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(1, ganadores);
        Assert.Equal(1, perdedores);

        var perdedor = respuestas.First(r => r.StatusCode == HttpStatusCode.Conflict);
        var problema = await perdedor.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("presupuesto_ya_enviado", problema.GetProperty("codigo").GetString());

        // El hueco: el ganador se llevó 1, el perdedor quemó 2 (comitea igual, incondicional), el
        // SIGUIENTE presupuesto del mismo PV salta directo a 3.
        var siguiente = await CrearBorradorAsync(ctx.Admin, SolicitudSimple(ctx));
        var siguienteEnvio = await ctx.Admin.PostAsJsonAsync($"/api/presupuestos/{siguiente.Id}/enviar", new SolicitudDeEnvio(vencimiento));
        Assert.Equal(HttpStatusCode.OK, siguienteEnvio.StatusCode);
        var siguienteEnviado = (await siguienteEnvio.Content.ReadFromJsonAsync<PresupuestoDetalle>(OpcionesJson))!;
        Assert.Equal(3, siguienteEnviado.Numero);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(EstadoPresupuesto.Enviado, (await db.Presupuestos.FirstAsync(p => p.Id == creado.Id)).Estado);
    }

    // ---- task 2.12: mutation target #17 — la carrera del relink de PV ------------------------------

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

    /// <summary>Mutation target #17 (task 2.12, mismo patrón que
    /// <c>ServicioDeOrdenesDeCompraTests.UnPutQueMuevePuntoDeVentaConcurrenteConEnviarReclasificaA409YElNumeroQuedaEnLaSerieVieja</c>):
    /// un <c>PUT</c> que mueve el presupuesto del PV 1 al PV 2 gana la carrera y COMMITEA DESPUÉS
    /// de que el número ya fue dibujado (serie del PV 1) pero ANTES de que el <c>UPDATE</c> final
    /// de <c>enviar</c> corra — pausado justo tras <c>BeginTransactionAsync</c> de
    /// <c>EjecutarEnvioAsync</c>. El <c>WHERE id_punto_venta = $pv</c> (pineado al PV 1, capturado
    /// en la pre-lectura) no matchea la fila ya movida al PV 2 ⇒ 0 filas ⇒ <c>409</c>, el número
    /// dibujado para el PV 1 queda quemado SIN aparecer en ninguna orden — nunca aterriza en la
    /// serie del PV 2.</summary>
    [Fact]
    public async Task UnPutQueMuevePuntoDeVentaConcurrenteConEnviarReclasificaA409YElNumeroQuedaEnLaSerieVieja()
    {
        var ctx = await PrepararAsync(nameof(UnPutQueMuevePuntoDeVentaConcurrenteConEnviarReclasificaA409YElNumeroQuedaEnLaSerieVieja));
        var creado = await CrearBorradorAsync(ctx.Admin, SolicitudSimple(ctx, idPuntoVenta: ctx.IdPuntoVenta));

        var transaccionIniciada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var puedeContinuar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interceptor = new InterceptorDePausaTrasIniciarLaTransaccion(transaccionIniciada, puedeContinuar);

        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddDbContext<WaysDbContext>((_, options) => options.AddInterceptors(interceptor))));

        using var clienteEnviar = factory.CreateClient();
        var login = await clienteEnviar.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(ctx.MailAdmin, ctx.PasswordAdmin));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var vencimiento = FechaFutura();
        var tareaEnviar = clienteEnviar.PostAsJsonAsync($"/api/presupuestos/{creado.Id}/enviar", new SolicitudDeEnvio(vencimiento));

        await transaccionIniciada.Task;

        // El PUT gana la carrera: mueve el presupuesto al PV 2 y COMMITEA antes de que el UPDATE
        // final de enviar corra.
        var solicitudRelink = new SolicitudDePresupuesto(
            ctx.IdPuntoVenta2, ctx.IdCliente, null, [new LineaDePresupuesto(ctx.IdArticulo, 2m)]);
        var respuestaPut = await ctx.Admin.PutAsJsonAsync($"/api/presupuestos/{creado.Id}", solicitudRelink);
        var cuerpoPut = await respuestaPut.Content.ReadAsStringAsync();
        Assert.True(respuestaPut.StatusCode == HttpStatusCode.OK, cuerpoPut);

        puedeContinuar.TrySetResult();

        var respuestaEnviar = await tareaEnviar;
        var cuerpoEnviar = await respuestaEnviar.Content.ReadAsStringAsync();
        Assert.True(respuestaEnviar.StatusCode == HttpStatusCode.Conflict, cuerpoEnviar);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpoEnviar, OpcionesJson);
        Assert.Equal("presupuesto_ya_enviado", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var actual = await db.Presupuestos.FirstAsync(p => p.Id == creado.Id);
        Assert.Equal(EstadoPresupuesto.Borrador, actual.Estado);
        Assert.Null(actual.Numero);
        Assert.Equal(ctx.IdPuntoVenta2, actual.IdPuntoVenta);

        // El número quemado (serie del PV 1) nunca aparece en ningún presupuesto de ese punto de
        // venta: un envío siguiente del PV 1 salta directo al 2.
        var siguientePv1 = await CrearBorradorAsync(ctx.Admin, SolicitudSimple(ctx, idPuntoVenta: ctx.IdPuntoVenta));
        var siguienteEnvioPv1 = await ctx.Admin.PostAsJsonAsync($"/api/presupuestos/{siguientePv1.Id}/enviar", new SolicitudDeEnvio(vencimiento));
        Assert.Equal(HttpStatusCode.OK, siguienteEnvioPv1.StatusCode);
        var siguienteEnviadaPv1 = (await siguienteEnvioPv1.Content.ReadFromJsonAsync<PresupuestoDetalle>(OpcionesJson))!;
        Assert.Equal(2, siguienteEnviadaPv1.Numero);
    }

    // ---- task 2.7: anular ---------------------------------------------------------------------------

    [Fact]
    public async Task AnularUnPresupuestoEnviadoLoPasaAAnulado()
    {
        var ctx = await PrepararAsync(nameof(AnularUnPresupuestoEnviadoLoPasaAAnulado));
        var creado = await CrearBorradorAsync(ctx.Admin, SolicitudSimple(ctx));
        var enviar = await ctx.Admin.PostAsJsonAsync($"/api/presupuestos/{creado.Id}/enviar", new SolicitudDeEnvio(FechaFutura()));
        Assert.Equal(HttpStatusCode.OK, enviar.StatusCode);

        var anular = await ctx.Admin.PostAsync($"/api/presupuestos/{creado.Id}/anular", null);
        var cuerpo = await anular.Content.ReadAsStringAsync();
        Assert.True(anular.StatusCode == HttpStatusCode.OK, cuerpo);
        var anulado = JsonSerializer.Deserialize<PresupuestoDetalle>(cuerpo, OpcionesJson)!;
        Assert.Equal(EstadoPresupuesto.Anulado, anulado.Estado);
    }

    /// <summary>Una segunda anulación sobre un presupuesto ya <c>anulado</c> es rechazada — el
    /// <c>WHERE estado IN ('borrador','enviado')</c> del <c>UPDATE</c> no matchea una fila ya
    /// <c>anulado</c>, mismo criterio de "OD8/T1" (convertido/anulado ambos quedan fuera del
    /// IN).</summary>
    [Fact]
    public async Task AnularDosVecesElMismoPresupuestoEsRechazadoLaSegundaVez409PresupuestoNoAnulable()
    {
        var ctx = await PrepararAsync(nameof(AnularDosVecesElMismoPresupuestoEsRechazadoLaSegundaVez409PresupuestoNoAnulable));
        var creado = await CrearBorradorAsync(ctx.Admin, SolicitudSinItems(ctx));

        var primeraAnulacion = await ctx.Admin.PostAsync($"/api/presupuestos/{creado.Id}/anular", null);
        Assert.Equal(HttpStatusCode.OK, primeraAnulacion.StatusCode);

        var segundaAnulacion = await ctx.Admin.PostAsync($"/api/presupuestos/{creado.Id}/anular", null);
        Assert.Equal(HttpStatusCode.Conflict, segundaAnulacion.StatusCode);
        var problema = await segundaAnulacion.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("presupuesto_no_anulable", problema.GetProperty("codigo").GetString());
    }

    // ---- task 2.9: el filtro vencido exige idPuntoVenta ---------------------------------------------

    [Fact]
    public async Task ListarConVencidoSinIdPuntoVentaEsRechazado400PuntoVentaRequerido()
    {
        var ctx = await PrepararAsync(nameof(ListarConVencidoSinIdPuntoVentaEsRechazado400PuntoVentaRequerido));

        var respuesta = await ctx.Admin.GetAsync("/api/presupuestos?vencido=true");

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("punto_venta_requerido", problema.GetProperty("codigo").GetString());
    }

    /// <summary>task 2.8/2.9: un presupuesto <c>enviado</c> con <c>vencimiento</c> pasado reporta
    /// <c>Vencido = true</c> en la lectura, y el filtro <c>vencido=true</c> lo incluye mientras que
    /// <c>vencido=false</c> lo excluye — ambos resueltos en la zona DEFAULT del punto de venta
    /// (Buenos Aires), sin seedear ningún parametro.</summary>
    [Fact]
    public async Task UnPresupuestoConVencimientoPasadoSeReportaVencidoYElFiltroLoDiscrimina()
    {
        var instanteFijo = new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddSingleton<IRelojDelSistema>(new RelojFijo(instanteFijo))));

        var ctx = await PrepararConFactoryAsync(
            nameof(UnPresupuestoConVencimientoPasadoSeReportaVencidoYElFiltroLoDiscrimina), factory);

        // Enviado con vencimiento HOY (no vencido todavía) — luego lo "vencemos" escribiendo
        // directo la columna, porque enviar exige vencimiento >= hoy.
        var vencido = await CrearBorradorAsync(ctx.Admin, SolicitudSimple(ctx));
        var enviarVencido = await ctx.Admin.PostAsJsonAsync(
            $"/api/presupuestos/{vencido.Id}/enviar", new SolicitudDeEnvio(new DateOnly(2026, 8, 19)));
        Assert.Equal(HttpStatusCode.OK, enviarVencido.StatusCode);

        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var fila = await db.Presupuestos.FirstAsync(p => p.Id == vencido.Id);
            fila.Vencimiento = new DateOnly(2026, 8, 10);
            await db.SaveChangesAsync();
        }

        var vigente = await CrearBorradorAsync(ctx.Admin, SolicitudSimple(ctx));
        var enviarVigente = await ctx.Admin.PostAsJsonAsync(
            $"/api/presupuestos/{vigente.Id}/enviar", new SolicitudDeEnvio(new DateOnly(2026, 12, 31)));
        Assert.Equal(HttpStatusCode.OK, enviarVigente.StatusCode);

        var detalleVencido = await ctx.Admin.GetFromJsonAsync<PresupuestoDetalle>($"/api/presupuestos/{vencido.Id}", OpcionesJson);
        Assert.True(detalleVencido!.Vencido);
        Assert.False(detalleVencido.Convertible);

        var detalleVigente = await ctx.Admin.GetFromJsonAsync<PresupuestoDetalle>($"/api/presupuestos/{vigente.Id}", OpcionesJson);
        Assert.False(detalleVigente!.Vencido);
        Assert.True(detalleVigente.Convertible);

        var pageVencidos = await ctx.Admin.GetFromJsonAsync<PaginaDePresupuestos>(
            $"/api/presupuestos?idPuntoVenta={ctx.IdPuntoVenta}&vencido=true", OpcionesJson);
        Assert.Contains(pageVencidos!.Items, p => p.Id == vencido.Id);
        Assert.DoesNotContain(pageVencidos.Items, p => p.Id == vigente.Id);

        var pageVigentes = await ctx.Admin.GetFromJsonAsync<PaginaDePresupuestos>(
            $"/api/presupuestos?idPuntoVenta={ctx.IdPuntoVenta}&vencido=false", OpcionesJson);
        Assert.Contains(pageVigentes!.Items, p => p.Id == vigente.Id);
        Assert.DoesNotContain(pageVigentes.Items, p => p.Id == vencido.Id);
    }

    // ---- task 2.20: paginación con fecha_emision empatada + campos posicionales --------------------

    /// <summary>Regla 12b (mutation-proof-tests): con <c>fecha_emision</c> EMPATADA
    /// (<c>RelojFijo</c>) en toda la tanda, el desempate por <c>Id DESC</c> hace que la página 2 no
    /// repita ni saltee filas — sin él, un orden solo por <c>fecha_emision</c> es indeterminado
    /// entre filas empatadas.</summary>
    [Fact]
    public async Task PaginacionConFechaEmisionEmpatadaNoRepiteNiSalteaFilas()
    {
        var instanteFijo = new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddSingleton<IRelojDelSistema>(new RelojFijo(instanteFijo))));

        var ctx = await PrepararConFactoryAsync(nameof(PaginacionConFechaEmisionEmpatadaNoRepiteNiSalteaFilas), factory);

        var ids = new List<int>();
        for (var i = 0; i < 5; i++)
        {
            var creado = await CrearBorradorAsync(ctx.Admin, SolicitudSinItems(ctx));
            ids.Add(creado.Id);
        }

        var pagina1 = await ctx.Admin.GetFromJsonAsync<PaginaDePresupuestos>(
            $"/api/presupuestos?idPuntoVenta={ctx.IdPuntoVenta}&pagina=1&tamanio=3", OpcionesJson);
        var pagina2 = await ctx.Admin.GetFromJsonAsync<PaginaDePresupuestos>(
            $"/api/presupuestos?idPuntoVenta={ctx.IdPuntoVenta}&pagina=2&tamanio=3", OpcionesJson);

        Assert.Equal(3, pagina1!.Items.Count);
        Assert.Equal(2, pagina2!.Items.Count);
        Assert.Equal(5, pagina1.Total);

        var idsDePaginas = pagina1.Items.Select(p => p.Id).Concat(pagina2.Items.Select(p => p.Id)).ToList();
        Assert.Equal(ids.OrderByDescending(x => x), idsDePaginas);
    }

    /// <summary>Regla 12b: cada campo posicional de <see cref="PresupuestoDetalle"/>/
    /// <see cref="ItemDePresupuesto"/> se lee de vuelta con un valor DISTINTO al de sus vecinos —
    /// un test que solo comparara con <c>null</c>/default no discriminaría un mapeo de campos
    /// desordenado (p.ej. <c>Subtotal</c>/<c>Total</c> swappeados). Variante por descuento-cero
    /// (judgment-day, mismo mecanismo pero con un valor no-null coincidentemente igual): sin una
    /// oferta real sembrada, <c>DescuentoTotal</c> es siempre <c>0m</c> y <c>Subtotal == Total</c>
    /// — un swap de esos dos campos en el sitio de construcción (<c>ServicioDePresupuestos.cs</c>,
    /// tanto el <see cref="PresupuestoDetalle"/> como su espejo en <c>ProyectarListado</c>)
    /// pasaría verde. Se siembra una oferta del 20% sobre <c>ctx.IdArticulo2</c> para que los tres
    /// valores del header sean pairwise-distintos.</summary>
    [Fact]
    public async Task TodoCampoPosicionalDelDetalleSeLeeDeVueltaConValoresDistinguibles()
    {
        var ctx = await PrepararAsync(nameof(TodoCampoPosicionalDelDetalleSeLeeDeVueltaConValoresDistinguibles));
        await CrearOfertaAsync(ctx.Admin, OfertaDeArticulo(ctx.IdArticulo2, porcentaje: 20m));

        var creado = await CrearBorradorAsync(
            ctx.Admin,
            new SolicitudDePresupuesto(
                ctx.IdPuntoVenta, ctx.IdCliente, "observacion distinguible",
                [new LineaDePresupuesto(ctx.IdArticulo, 3m), new LineaDePresupuesto(ctx.IdArticulo2, 1m)]));

        Assert.Equal(ctx.IdPuntoVenta, creado.IdPuntoVenta);
        Assert.Equal(ctx.IdCliente, creado.IdCliente);
        Assert.True(creado.IdEmpleado > 0);
        Assert.Null(creado.Numero);
        Assert.Null(creado.NumeroFormateado);
        Assert.Null(creado.FechaEnvio);
        Assert.Null(creado.Vencimiento);
        Assert.False(creado.Vencido);
        Assert.False(creado.Convertible);
        Assert.Equal("America/Argentina/Buenos_Aires", creado.ZonaId);
        Assert.Equal("observacion distinguible", creado.Observaciones);
        // item1: 3 * 100 = 300 (sin oferta); item2: 1 * 250 con oferta 20% = 250 - 50 = 200;
        // header Subtotal = 550, DescuentoTotal = 50, Total = 500 — los tres pairwise-distintos.
        Assert.Equal(550m, creado.Subtotal);
        Assert.Equal(50m, creado.DescuentoTotal);
        Assert.Equal(500m, creado.Total);
        Assert.Equal(EstadoPresupuesto.Borrador, creado.Estado);
        Assert.Null(creado.IdComprobanteVenta);

        var item1 = creado.Items.Single(i => i.IdArticulo == ctx.IdArticulo);
        Assert.Equal(1, item1.Orden);
        Assert.Equal("Pres Articulo 1", item1.Descripcion);
        Assert.Equal(3m, item1.Cantidad);
        Assert.Equal(100m, item1.PrecioUnitario);
        Assert.Equal(0m, item1.Descuento);
        Assert.Equal(300m, item1.Total);
        Assert.Equal(ctx.IdListaPrecio, item1.IdListaPrecio);
        Assert.Null(item1.IdOferta);
        Assert.Equal(ctx.IdAlicuotaIva, item1.IdAlicuotaIva);
        Assert.Equal(21m, item1.PorcentajeIva);

        // Mismo swap Subtotal/Total en el sitio de construcción también pasaría verde en el
        // listado (ProyectarListado lee presupuesto.Total) — se confirma la fila.
        var listado = await ctx.Admin.GetFromJsonAsync<PaginaDePresupuestos>(
            $"/api/presupuestos?idPuntoVenta={ctx.IdPuntoVenta}&pagina=1&tamanio=10", OpcionesJson);
        var filaListado = listado!.Items.Single(p => p.Id == creado.Id);
        Assert.Equal(500m, filaListado.Total);

        var enviar = await ctx.Admin.PostAsJsonAsync($"/api/presupuestos/{creado.Id}/enviar", new SolicitudDeEnvio(FechaFutura()));
        var enviado = (await enviar.Content.ReadFromJsonAsync<PresupuestoDetalle>(OpcionesJson))!;
        Assert.Equal(1, enviado.Numero);
        Assert.Equal($"{ctx.IdPuntoVenta:D4}-00000001", enviado.NumeroFormateado);
        Assert.NotNull(enviado.FechaEnvio);
        Assert.Equal(EstadoPresupuesto.Enviado, enviado.Estado);
    }

    // ---- autorización — matriz explícita (Vendedor SÍ escribe; Root 403; sin token 401) ------------

    [Fact]
    public async Task LaMatrizDeAutorizacionEsOperacionDePosVendedorEscribeRootBloqueadoSinTokenNoAutenticado()
    {
        var ctx = await PrepararAsync(nameof(LaMatrizDeAutorizacionEsOperacionDePosVendedorEscribeRootBloqueadoSinTokenNoAutenticado));

        // Vendedor: ciclo de vida completo, todo 2xx.
        var crear = await ctx.Vendedor.PostAsJsonAsync("/api/presupuestos", SolicitudSimple(ctx));
        Assert.Equal(HttpStatusCode.Created, crear.StatusCode);
        var creado = (await crear.Content.ReadFromJsonAsync<PresupuestoDetalle>(OpcionesJson))!;

        var editar = await ctx.Vendedor.PutAsJsonAsync($"/api/presupuestos/{creado.Id}", SolicitudSimple(ctx));
        Assert.Equal(HttpStatusCode.OK, editar.StatusCode);

        var enviar = await ctx.Vendedor.PostAsJsonAsync($"/api/presupuestos/{creado.Id}/enviar", new SolicitudDeEnvio(FechaFutura()));
        Assert.Equal(HttpStatusCode.OK, enviar.StatusCode);

        var anular = await ctx.Vendedor.PostAsync($"/api/presupuestos/{creado.Id}/anular", null);
        Assert.Equal(HttpStatusCode.OK, anular.StatusCode);

        var listar = await ctx.Vendedor.GetAsync("/api/presupuestos");
        Assert.Equal(HttpStatusCode.OK, listar.StatusCode);

        // Root: bloqueado — OperacionDePos no admite plataforma.
        var rootCrear = await ctx.Root.PostAsJsonAsync("/api/presupuestos", SolicitudSimple(ctx));
        Assert.Equal(HttpStatusCode.Forbidden, rootCrear.StatusCode);

        // Sin token: no autenticado.
        using var sinToken = fixture.CreateClient();
        var sinTokenRespuesta = await sinToken.PostAsJsonAsync("/api/presupuestos", SolicitudSimple(ctx));
        Assert.Equal(HttpStatusCode.Unauthorized, sinTokenRespuesta.StatusCode);
    }

    // ---- gate guard (task 2.21) ----------------------------------------------------------------------

    /// <summary>Gate guard (task 2.21, decisión 2 de tasks.md): esta slice no agrega DDL — el
    /// modelo EF sigue coincidiendo exactamente con la migración de Slice 1.</summary>
    [Fact]
    public async Task NoHayCambiosPendientesDeModeloRespectoDeLaMigracionDeLaSlice1()
    {
        using var _ = fixture.CreateClient();
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var hayPendientes = db.Database.HasPendingModelChanges();
        Assert.False(hayPendientes);
    }

    private static DateOnly FechaFutura() => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30);
}
