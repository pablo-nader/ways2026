using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
/// stage-16-ordenes-de-compra, Slice 4 (tasks 4.4-4.17; design: Transactions — CERRAR OC/ANULAR
/// OC, decisiones 5/9). Cierre manual (<c>POST /{id}/cerrar</c>) + anulación gobernada por el
/// libro (<c>POST /{id}/anular</c>, guard lock-free) + la matriz 409/autorización.
///
/// OD (orquestador, decisión 20.2 de tasks.md, launch prompt de este slice): las DOS carreras
/// diferidas de slices anteriores SE PAGAN ACÁ.
/// - Race 1 (deferred de task 3.22 / design Testing Strategy "the two races" / mutation target
///   #33's "anular × confirmar rendezvous", task 4.10): <c>anular</c> vs <c>confirmar</c> de un
///   comprobante YA ligado (todavía borrador). Probada en AMBOS órdenes con
///   <see cref="InterceptorDePausaTrasIniciarLaTransaccion"/>. **FINDING** (mutation-proof-tests
///   regla 2/3, mismo criterio que el hallazgo de target #21 en slice 3): con el guard lock-free
///   real (statement 3 de <c>AnularAsync</c>, SIN <c>FOR SHARE</c>), el resultado es DETERMINISTA
///   en AMBOS órdenes — <c>anular</c> SIEMPRE pierde mientras el comprobante exista ligado en
///   borrador (si <c>anular</c> toma el lock de la OC primero, su propio statement 3 encuentra el
///   comprobante todavía 'borrador' — el UPDATE de confirmar está sin comitear, invisible bajo
///   READ COMMITTED — y lo rechaza; si <c>confirmar</c> toma el lock primero, comitea, y el
///   statement 1 de <c>anular</c> ve el estado ya proyectado, fuera de
///   <c>('borrador','enviada')</c>). El texto de design ("el guard del confirm... rechaza si
///   anulada") describe el mecanismo GENERAL de defensa en profundidad, no necesariamente
///   alcanzable con ESTE fixture específico — la propia task 4.9 (un borrador ligado bloquea SIEMPRE
///   la anulación) domina. Ambos órdenes se prueban igual: verifican que NUNCA hay deadlock y que
///   el resultado es <c>SIEMPRE</c> un 200 (confirmar) + un 409 (anular), nunca los dos 200, nunca
///   los dos 409.
/// - Race 2 (deferred de task 3.38, FK 9 / design Backstop Map "linking to an OC being annulled
///   concurrently"): <c>ExigirOrdenLigableAsync</c> (<c>FOR SHARE</c>, bajo la transacción de
///   <c>ActualizarBorradorAsync</c>) vs el <c>FOR UPDATE</c> de statement 1 de <c>AnularAsync</c> —
///   ESTA sí es genuinamente bidireccional: quien tome el lock de la fila de la OC primero decide
///   el resultado (el otro pierde), nunca deadlock (SHARE vs UPDATE sobre la MISMA fila, sin
///   ciclo — un solo recurso en juego).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class OrdenesCompraCierreYAnulacionTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordUsuario = "una-contraseña-larga";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdPuntoVenta, HttpClient Admin, string MailAdmin, string PasswordAdmin,
        int IdProveedor, int IdArticulo, int IdArticulo2, int IdAlicuotaIva21, int IdTipoCFA, int IdEmpleadoAdmin);

    /// <summary>Decisión 13 (tasks.md): ids deliberadamente desincronizados.</summary>
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

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Cierre-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
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
            IdTenant = resultado.IdTenant, CodigoInterno = $"{nombre}-cie-1-{Guid.NewGuid():N}", Nombre = "Cierre Articulo 1",
            IdArea = area.Id, IdAlicuotaIva = idAlicuotaIva21, UnidadVenta = UnidadVenta.Unidad, EsProducto = true,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        var articulo2 = new Articulo
        {
            IdTenant = resultado.IdTenant, CodigoInterno = $"{nombre}-cie-2-{Guid.NewGuid():N}", Nombre = "Cierre Articulo 2",
            IdArea = area.Id, IdAlicuotaIva = idAlicuotaIva21, UnidadVenta = UnidadVenta.Unidad, EsProducto = true,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.AddRange(articulo1, articulo2);
        await db.SaveChangesAsync();

        var idTipoCFA = await db.TiposComprobante.Where(t => t.Codigo == "C-FA").Select(t => t.Id).SingleAsync();
        var idEmpleadoAdmin = await db.Usuarios.Where(u => u.Mail == mailAdmin).Select(u => u.Id).FirstAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdPuntoVenta, admin, mailAdmin, resultado.PasswordTemporal,
            proveedor.Id, articulo1.Id, articulo2.Id, idAlicuotaIva21, idTipoCFA, idEmpleadoAdmin);
    }

    private async Task<HttpClient> CrearUsuarioConRolAsync(Contexto ctx, string nombre, RolConocido rol)
    {
        var mail = $"{nombre.ToLowerInvariant()}@ways.test";
        var alta = await ctx.Admin.PostAsJsonAsync(
            "/api/usuarios", new CrearUsuario(nombre, mail, (int)rol, PasswordUsuario));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);

        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, PasswordUsuario));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    // ---- helpers: órdenes de compra ----------------------------------------------------------------

    private static SolicitudDeOrdenDeCompra SolicitudDeOrdenSimple(
        Contexto ctx, decimal cantidad = 10m, decimal? costo = 100m, int? idArticulo = null) =>
        new(
            ctx.IdProveedor, ctx.IdPuntoVenta, null, null,
            [new LineaDeOrdenSolicitada(idArticulo ?? ctx.IdArticulo, "Item de orden", cantidad, costo)]);

    private static async Task<OrdenDeCompraBorrador> CrearBorradorDeOrdenAsync(
        Contexto ctx, SolicitudDeOrdenDeCompra? solicitud = null, HttpClient? cliente = null)
    {
        var respuesta = await (cliente ?? ctx.Admin).PostAsJsonAsync("/api/ordenes-compra", solicitud ?? SolicitudDeOrdenSimple(ctx));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<OrdenDeCompraBorrador>(cuerpo, OpcionesJson)!;
    }

    private static async Task<OrdenDeCompraBorrador> EnviarOrdenAsync(Contexto ctx, int id, HttpClient? cliente = null)
    {
        var respuesta = await (cliente ?? ctx.Admin).PostAsync($"/api/ordenes-compra/{id}/enviar", null);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<OrdenDeCompraBorrador>(cuerpo, OpcionesJson)!;
    }

    private static async Task<OrdenDeCompraBorrador> CrearYEnviarOrdenAsync(
        Contexto ctx, decimal cantidad = 10m, decimal? costo = 100m, int? idArticulo = null)
    {
        var creada = await CrearBorradorDeOrdenAsync(ctx, SolicitudDeOrdenSimple(ctx, cantidad, costo, idArticulo));
        return await EnviarOrdenAsync(ctx, creada.Id);
    }

    private static async Task<HttpResponseMessage> CerrarHttpAsync(Contexto ctx, int id, HttpClient? cliente = null) =>
        await (cliente ?? ctx.Admin).PostAsync($"/api/ordenes-compra/{id}/cerrar", null);

    private static async Task<HttpResponseMessage> AnularHttpAsync(Contexto ctx, int id, HttpClient? cliente = null) =>
        await (cliente ?? ctx.Admin).PostAsync($"/api/ordenes-compra/{id}/anular", null);

    // ---- helpers: comprobantes de compra (ligadura, slices 1-3) --------------------------------------

    private static SolicitudDeCompra SolicitudDeCompraSimple(
        Contexto ctx, decimal unidades, int? idOrdenCompra, int? idArticulo = null) =>
        new(
            ctx.IdProveedor, ctx.IdTipoCFA, ctx.IdPuntoVenta, $"0001-{Guid.NewGuid():N}"[..8], DateOnly.FromDateTime(DateTime.UtcNow), null,
            [new LineaDeCompraSolicitada(idArticulo ?? ctx.IdArticulo, "Item de recepción", unidades, null, null, 100m, 0m, ctx.IdAlicuotaIva21, false)],
            idOrdenCompra);

    private static async Task<(HttpStatusCode Estado, CompraDetalle? Compra)> CrearBorradorDeCompraAsync(
        Contexto ctx, SolicitudDeCompra solicitud)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/compras", solicitud);
        if (respuesta.StatusCode != HttpStatusCode.Created)
        {
            return (respuesta.StatusCode, null);
        }

        return (respuesta.StatusCode, await respuesta.Content.ReadFromJsonAsync<CompraDetalle>(OpcionesJson));
    }

    private static async Task<HttpResponseMessage> ConfirmarCompraHttpAsync(Contexto ctx, int id, HttpClient? cliente = null) =>
        await (cliente ?? ctx.Admin).PostAsync($"/api/compras/{id}/confirmar", null);

    private static async Task<CompraDetalle> ConfirmarCompraAsync(Contexto ctx, int id)
    {
        var respuesta = await ConfirmarCompraHttpAsync(ctx, id);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<CompraDetalle>(cuerpo, OpcionesJson)!;
    }

    private static async Task<HttpResponseMessage> AnularCompraHttpAsync(Contexto ctx, int id) =>
        await ctx.Admin.PostAsync($"/api/compras/{id}/anular", null);

    private async Task<OrdenCompra> LeerOrdenAsync(Contexto ctx, int idOrdenCompra)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        return await db.OrdenesCompra.AsNoTracking().FirstAsync(o => o.Id == idOrdenCompra);
    }

    // ================================================================================================
    // task 4.4/4.15: cerrar manualmente estampa fecha_cierre + id_empleado_cierre en UN UPDATE
    // ================================================================================================

    [Fact]
    public async Task CerrarManualmenteEstampaFechaCierreYEmpleado()
    {
        var ctx = await PrepararAsync(nameof(CerrarManualmenteEstampaFechaCierreYEmpleado));
        var enviada = await CrearYEnviarOrdenAsync(ctx);

        // Regla 12c: una OC hermana del mismo tenant — el cierre de la primera no debe tocarla.
        var hermana = await CrearYEnviarOrdenAsync(ctx, idArticulo: ctx.IdArticulo2);

        var respuesta = await CerrarHttpAsync(ctx, enviada.Id);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        var cerrada = JsonSerializer.Deserialize<OrdenDeCompraBorrador>(cuerpo, OpcionesJson)!;

        Assert.Equal(EstadoOrdenCompra.Cerrada, cerrada.Estado);
        Assert.NotNull(cerrada.FechaCierre);
        Assert.Equal(ctx.IdEmpleadoAdmin, cerrada.IdEmpleadoCierre);

        var persistida = await LeerOrdenAsync(ctx, enviada.Id);
        Assert.Equal(EstadoOrdenCompra.Cerrada, persistida.Estado);
        Assert.NotNull(persistida.FechaCierre);
        Assert.Equal(ctx.IdEmpleadoAdmin, persistida.IdEmpleadoCierre);

        // Hermana intacta (regla 12c).
        var hermanaDespues = await LeerOrdenAsync(ctx, hermana.Id);
        Assert.Equal(EstadoOrdenCompra.Enviada, hermanaDespues.Estado);
        Assert.Null(hermanaDespues.FechaCierre);
        Assert.Null(hermanaDespues.IdEmpleadoCierre);
    }

    // ================================================================================================
    // task 4.5/4.15 (mutation target #26 ya cubierto en slice 3; acá el camino HTTP real de cierre):
    // una OC cerrada manualmente no se reabre al anular su recepción
    // ================================================================================================

    [Fact]
    public async Task UnaOrdenCerradaManualmenteNoSeReabreAlAnularSuRecepcion()
    {
        var ctx = await PrepararAsync(nameof(UnaOrdenCerradaManualmenteNoSeReabreAlAnularSuRecepcion));
        var enviada = await CrearYEnviarOrdenAsync(ctx, cantidad: 10m);

        var (estadoCreacion, creada) = await CrearBorradorDeCompraAsync(
            ctx, SolicitudDeCompraSimple(ctx, unidades: 4m, idOrdenCompra: enviada.Id));
        Assert.Equal(HttpStatusCode.Created, estadoCreacion);
        var confirmada = await ConfirmarCompraAsync(ctx, creada!.Id);

        var antesDelCierre = await LeerOrdenAsync(ctx, enviada.Id);
        Assert.Equal(EstadoOrdenCompra.RecibidaParcial, antesDelCierre.Estado);

        var cierre = await CerrarHttpAsync(ctx, enviada.Id);
        Assert.Equal(HttpStatusCode.OK, cierre.StatusCode);

        var anulacion = await AnularCompraHttpAsync(ctx, confirmada.Id);
        Assert.Equal(HttpStatusCode.OK, anulacion.StatusCode);

        var despues = await LeerOrdenAsync(ctx, enviada.Id);
        Assert.Equal(EstadoOrdenCompra.Cerrada, despues.Estado);
        Assert.NotNull(despues.FechaCierre);
        Assert.Equal(ctx.IdEmpleadoAdmin, despues.IdEmpleadoCierre);
    }

    // ================================================================================================
    // task 4.6/4.14: mutation target #31 — cerrar una orden que no está en enviada/recibida_parcial
    // ================================================================================================

    [Theory]
    [InlineData("borrador")]
    [InlineData("cerrada")]
    [InlineData("anulada")]
    public async Task CerrarUnaOrdenFueraDeEnviadaORecibidaParcialEsRechazada409(string estadoInicial)
    {
        var ctx = await PrepararAsync(nameof(CerrarUnaOrdenFueraDeEnviadaORecibidaParcialEsRechazada409) + estadoInicial);
        var id = await SembrarOrdenEnEstadoAsync(ctx, estadoInicial);

        var respuesta = await CerrarHttpAsync(ctx, id);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Conflict, cuerpo);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpo, OpcionesJson);
        Assert.Equal("orden_compra_no_cerrable", problema.GetProperty("codigo").GetString());
    }

    /// <summary>Siembra directa por EF en el estado pedido — mismo criterio que
    /// <c>ServicioDeComprasLigaduraTests.SembrarOrdenAnuladaAsync</c>: el estado de la fila es lo
    /// que el guard interpreta, no el camino que la produjo.</summary>
    private async Task<int> SembrarOrdenEnEstadoAsync(Contexto ctx, string estado)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var (estadoEnum, numero, fechaEnvio, fechaCierre, idEmpleadoCierre) = estado switch
        {
            "borrador" => (EstadoOrdenCompra.Borrador, (long?)null, (DateTimeOffset?)null, (DateTimeOffset?)null, (int?)null),
            "cerrada" => (EstadoOrdenCompra.Cerrada, 900L, ahora, ahora, ctx.IdEmpleadoAdmin),
            "anulada" => (EstadoOrdenCompra.Anulada, 901L, ahora, (DateTimeOffset?)null, (int?)null),
            _ => throw new ArgumentOutOfRangeException(nameof(estado))
        };

        var orden = new OrdenCompra
        {
            IdTenant = ctx.IdTenant, IdPuntoVenta = ctx.IdPuntoVenta, IdProveedor = ctx.IdProveedor,
            IdEmpleado = ctx.IdEmpleadoAdmin, Numero = numero, FechaEmision = ahora, FechaEnvio = fechaEnvio,
            FechaCierre = fechaCierre, IdEmpleadoCierre = idEmpleadoCierre,
            Estado = estadoEnum, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.OrdenesCompra.Add(orden);
        await db.SaveChangesAsync();
        return orden.Id;
    }

    // ================================================================================================
    // task 4.7/4.9/4.16: anulación gobernada por el libro
    // ================================================================================================

    [Fact]
    public async Task UnaOrdenCuyaUnicaRecepcionFueAnuladaPuedeAnularseElla()
    {
        var ctx = await PrepararAsync(nameof(UnaOrdenCuyaUnicaRecepcionFueAnuladaPuedeAnularseElla));
        var enviada = await CrearYEnviarOrdenAsync(ctx, cantidad: 10m);

        var (estadoCreacion, creada) = await CrearBorradorDeCompraAsync(
            ctx, SolicitudDeCompraSimple(ctx, unidades: 10m, idOrdenCompra: enviada.Id));
        Assert.Equal(HttpStatusCode.Created, estadoCreacion);
        var confirmada = await ConfirmarCompraAsync(ctx, creada!.Id);
        Assert.Equal(EstadoOrdenCompra.Cerrada, (await LeerOrdenAsync(ctx, enviada.Id)).Estado);

        var anulacionCompra = await AnularCompraHttpAsync(ctx, confirmada.Id);
        Assert.Equal(HttpStatusCode.OK, anulacionCompra.StatusCode);
        // El cierre fue AUTOMÁTICO (no manual) — la proyección la devuelve a `enviada`.
        Assert.Equal(EstadoOrdenCompra.Enviada, (await LeerOrdenAsync(ctx, enviada.Id)).Estado);

        var respuesta = await AnularHttpAsync(ctx, enviada.Id);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        var anulada = JsonSerializer.Deserialize<OrdenDeCompraBorrador>(cuerpo, OpcionesJson)!;
        Assert.Equal(EstadoOrdenCompra.Anulada, anulada.Estado);

        Assert.Equal(EstadoOrdenCompra.Anulada, (await LeerOrdenAsync(ctx, enviada.Id)).Estado);
    }

    [Fact]
    public async Task UnaOrdenConRecepcionEfectivaNoPuedeAnularse409()
    {
        var ctx = await PrepararAsync(nameof(UnaOrdenConRecepcionEfectivaNoPuedeAnularse409));
        var enviada = await CrearYEnviarOrdenAsync(ctx, cantidad: 10m);

        var (estadoCreacion, creada) = await CrearBorradorDeCompraAsync(
            ctx, SolicitudDeCompraSimple(ctx, unidades: 4m, idOrdenCompra: enviada.Id));
        Assert.Equal(HttpStatusCode.Created, estadoCreacion);
        await ConfirmarCompraAsync(ctx, creada!.Id);
        Assert.Equal(EstadoOrdenCompra.RecibidaParcial, (await LeerOrdenAsync(ctx, enviada.Id)).Estado);

        var respuesta = await AnularHttpAsync(ctx, enviada.Id);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Conflict, cuerpo);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpo, OpcionesJson);
        Assert.Equal("orden_compra_con_recepciones", problema.GetProperty("codigo").GetString());

        Assert.Equal(EstadoOrdenCompra.RecibidaParcial, (await LeerOrdenAsync(ctx, enviada.Id)).Estado);
    }

    [Fact]
    public async Task UnaOrdenConBorradorLigadoConfirmableNoPuedeAnularse409()
    {
        var ctx = await PrepararAsync(nameof(UnaOrdenConBorradorLigadoConfirmableNoPuedeAnularse409));
        var enviada = await CrearYEnviarOrdenAsync(ctx, cantidad: 10m);

        var (estadoCreacion, creada) = await CrearBorradorDeCompraAsync(
            ctx, SolicitudDeCompraSimple(ctx, unidades: 4m, idOrdenCompra: enviada.Id));
        Assert.Equal(HttpStatusCode.Created, estadoCreacion);

        var respuesta = await AnularHttpAsync(ctx, enviada.Id);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Conflict, cuerpo);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpo, OpcionesJson);
        Assert.Equal("orden_compra_con_recepciones", problema.GetProperty("codigo").GetString());

        Assert.Equal(EstadoOrdenCompra.Enviada, (await LeerOrdenAsync(ctx, enviada.Id)).Estado);

        // El borrador sigue confirmable — anular no lo tocó.
        var confirmar = await ConfirmarCompraHttpAsync(ctx, creada!.Id);
        Assert.Equal(HttpStatusCode.OK, confirmar.StatusCode);
    }

    [Fact]
    public async Task AnularUnaOrdenYaAnuladaEsRechazada409()
    {
        var ctx = await PrepararAsync(nameof(AnularUnaOrdenYaAnuladaEsRechazada409));
        var id = await SembrarOrdenEnEstadoAsync(ctx, "anulada");

        var respuesta = await AnularHttpAsync(ctx, id);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Conflict, cuerpo);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpo, OpcionesJson);
        Assert.Equal("orden_compra_con_recepciones", problema.GetProperty("codigo").GetString());
    }

    // ================================================================================================
    // task 4.4/4.404 desvío-friendly: anular un borrador SIN número (CHECK 1 lo admite)
    // ================================================================================================

    [Fact]
    public async Task AnularUnBorradorSinNumeroEsPermitido()
    {
        var ctx = await PrepararAsync(nameof(AnularUnBorradorSinNumeroEsPermitido));
        var creada = await CrearBorradorDeOrdenAsync(ctx);
        Assert.Null(creada.Numero);

        var respuesta = await AnularHttpAsync(ctx, creada.Id);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        var persistida = await LeerOrdenAsync(ctx, creada.Id);
        Assert.Equal(EstadoOrdenCompra.Anulada, persistida.Estado);
        Assert.Null(persistida.Numero);
    }

    // ================================================================================================
    // task 4.10 / mutation target #33 (segunda cláusula) / decisión 20.2: RACE 1 — anular OC ×
    // confirmar el comprobante ligado, en AMBOS órdenes (interceptor). Ver doc-comment de la clase.
    // ================================================================================================

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

    /// <summary>Orden 1: <c>anular</c> pausada justo tras abrir su transacción — <c>confirmar</c>
    /// corre y comitea PRIMERO (mueve la OC vía <see cref="EscriturasDeOrdenDeCompra"/>). Al
    /// reanudar, el statement 1 de <c>anular</c> ve el estado YA proyectado (fuera de
    /// <c>('borrador','enviada')</c>) y rechaza.</summary>
    [Fact]
    public async Task AnularPierdeCuandoConfirmarComitePrimeroMientrasAnularEstaPausada()
    {
        var ctx = await PrepararAsync(nameof(AnularPierdeCuandoConfirmarComitePrimeroMientrasAnularEstaPausada));
        var enviada = await CrearYEnviarOrdenAsync(ctx, cantidad: 10m);
        var (estadoCreacion, creada) = await CrearBorradorDeCompraAsync(
            ctx, SolicitudDeCompraSimple(ctx, unidades: 10m, idOrdenCompra: enviada.Id));
        Assert.Equal(HttpStatusCode.Created, estadoCreacion);

        var transaccionIniciada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var puedeContinuar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interceptor = new InterceptorDePausaTrasIniciarLaTransaccion(transaccionIniciada, puedeContinuar);

        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddDbContext<WaysDbContext>((_, options) => options.AddInterceptors(interceptor))));

        using var clienteAnular = factory.CreateClient();
        var login = await clienteAnular.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(ctx.MailAdmin, ctx.PasswordAdmin));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var tareaAnular = clienteAnular.PostAsync($"/api/ordenes-compra/{enviada.Id}/anular", null);
        await transaccionIniciada.Task;

        var respuestaConfirmar = await ConfirmarCompraHttpAsync(ctx, creada!.Id);
        var cuerpoConfirmar = await respuestaConfirmar.Content.ReadAsStringAsync();
        Assert.True(respuestaConfirmar.StatusCode == HttpStatusCode.OK, cuerpoConfirmar);

        puedeContinuar.TrySetResult();

        var respuestaAnular = await tareaAnular;
        var cuerpoAnular = await respuestaAnular.Content.ReadAsStringAsync();
        Assert.True(respuestaAnular.StatusCode == HttpStatusCode.Conflict, cuerpoAnular);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpoAnular, OpcionesJson);
        Assert.Equal("orden_compra_con_recepciones", problema.GetProperty("codigo").GetString());

        Assert.Equal(EstadoOrdenCompra.Cerrada, (await LeerOrdenAsync(ctx, enviada.Id)).Estado);
    }

    /// <summary>Orden 2: <c>confirmar</c> pausada justo tras abrir su transacción —
    /// <c>anular</c> corre PRIMERO, completa sus 4 statements. Su statement 3 (EXISTS lock-free)
    /// encuentra el comprobante TODAVÍA 'borrador' (el UPDATE de confirmar sigue sin comitear,
    /// invisible bajo READ COMMITTED) y rechaza — <c>anular</c> hace rollback, libera el lock. Al
    /// reanudar, <c>confirmar</c> ve el estado SIN CAMBIOS (todavía enviada, nunca anulada) y
    /// completa con éxito.</summary>
    [Fact]
    public async Task AnularPierdeCuandoIntentaPrimeroMientrasConfirmarEstaPausada()
    {
        var ctx = await PrepararAsync(nameof(AnularPierdeCuandoIntentaPrimeroMientrasConfirmarEstaPausada));
        var enviada = await CrearYEnviarOrdenAsync(ctx, cantidad: 10m);
        var (estadoCreacion, creada) = await CrearBorradorDeCompraAsync(
            ctx, SolicitudDeCompraSimple(ctx, unidades: 10m, idOrdenCompra: enviada.Id));
        Assert.Equal(HttpStatusCode.Created, estadoCreacion);

        var transaccionIniciada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var puedeContinuar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interceptor = new InterceptorDePausaTrasIniciarLaTransaccion(transaccionIniciada, puedeContinuar);

        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddDbContext<WaysDbContext>((_, options) => options.AddInterceptors(interceptor))));

        using var clienteConfirmar = factory.CreateClient();
        var login = await clienteConfirmar.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(ctx.MailAdmin, ctx.PasswordAdmin));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var tareaConfirmar = clienteConfirmar.PostAsync($"/api/compras/{creada!.Id}/confirmar", null);
        await transaccionIniciada.Task;

        var respuestaAnular = await AnularHttpAsync(ctx, enviada.Id);
        var cuerpoAnular = await respuestaAnular.Content.ReadAsStringAsync();
        Assert.True(respuestaAnular.StatusCode == HttpStatusCode.Conflict, cuerpoAnular);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpoAnular, OpcionesJson);
        Assert.Equal("orden_compra_con_recepciones", problema.GetProperty("codigo").GetString());

        puedeContinuar.TrySetResult();

        var respuestaConfirmar = await tareaConfirmar;
        var cuerpoConfirmar = await respuestaConfirmar.Content.ReadAsStringAsync();
        Assert.True(respuestaConfirmar.StatusCode == HttpStatusCode.OK, cuerpoConfirmar);

        Assert.Equal(EstadoOrdenCompra.Cerrada, (await LeerOrdenAsync(ctx, enviada.Id)).Estado);
    }

    // ================================================================================================
    // decisión 20.2: RACE 2 — ligar un borrador (PUT, ExigirOrdenLigableAsync FOR SHARE) × anular
    // la OC (FOR UPDATE, statement 1). Genuinamente bidireccional: quien tome el lock de la fila
    // primero decide, nunca deadlock (SHARE/UPDATE sobre la MISMA fila — un solo recurso, sin ciclo).
    // ================================================================================================

    /// <summary>Orden A: el PUT que liga (bajo <c>ActualizarBorradorAsync</c>'s transacción, real
    /// TOCTOU guard — design T3) pausado justo tras abrir su transacción. <c>anular</c> corre
    /// PRIMERO y comitea sin contención (nada ligado todavía) — luego el PUT reanuda, su
    /// <c>ExigirOrdenLigableAsync</c> ve <c>estado='anulada'</c> fresco y rechaza.</summary>
    [Fact]
    public async Task AnularGanaLaCarreraCuandoElPutQueLigaEstaPausado()
    {
        var ctx = await PrepararAsync(nameof(AnularGanaLaCarreraCuandoElPutQueLigaEstaPausado));
        var enviada = await CrearYEnviarOrdenAsync(ctx, cantidad: 10m);
        var (estadoCreacion, borrador) = await CrearBorradorDeCompraAsync(ctx, SolicitudDeCompraSimple(ctx, unidades: 5m, idOrdenCompra: null));
        Assert.Equal(HttpStatusCode.Created, estadoCreacion);

        var transaccionIniciada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var puedeContinuar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interceptor = new InterceptorDePausaTrasIniciarLaTransaccion(transaccionIniciada, puedeContinuar);

        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddDbContext<WaysDbContext>((_, options) => options.AddInterceptors(interceptor))));

        using var clientePut = factory.CreateClient();
        var login = await clientePut.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(ctx.MailAdmin, ctx.PasswordAdmin));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var solicitudRelink = SolicitudDeCompraSimple(ctx, unidades: 5m, idOrdenCompra: enviada.Id);
        var tareaPut = clientePut.PutAsJsonAsync($"/api/compras/{borrador!.Id}", solicitudRelink);
        await transaccionIniciada.Task;

        var respuestaAnular = await AnularHttpAsync(ctx, enviada.Id);
        var cuerpoAnular = await respuestaAnular.Content.ReadAsStringAsync();
        Assert.True(respuestaAnular.StatusCode == HttpStatusCode.OK, cuerpoAnular);

        puedeContinuar.TrySetResult();

        var respuestaPut = await tareaPut;
        var cuerpoPut = await respuestaPut.Content.ReadAsStringAsync();
        Assert.True(respuestaPut.StatusCode == HttpStatusCode.Conflict, cuerpoPut);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpoPut, OpcionesJson);
        Assert.Equal("orden_compra_anulada", problema.GetProperty("codigo").GetString());

        Assert.Equal(EstadoOrdenCompra.Anulada, (await LeerOrdenAsync(ctx, enviada.Id)).Estado);
    }

    /// <summary>Orden B: <c>anular</c> pausada justo tras abrir su transacción. El PUT que liga
    /// corre PRIMERO — su <c>FOR SHARE</c> ve <c>estado='enviada'</c> (todavía), liga, comitea,
    /// libera el <c>FOR SHARE</c>. Al reanudar, <c>anular</c> toma el <c>FOR UPDATE</c> (ahora
    /// libre) y su statement 3 encuentra el comprobante recién ligado, todavía 'borrador' →
    /// rechaza.</summary>
    [Fact]
    public async Task ElPutQueLigaGanaLaCarreraCuandoAnularEstaPausada()
    {
        var ctx = await PrepararAsync(nameof(ElPutQueLigaGanaLaCarreraCuandoAnularEstaPausada));
        var enviada = await CrearYEnviarOrdenAsync(ctx, cantidad: 10m);
        var (estadoCreacion, borrador) = await CrearBorradorDeCompraAsync(ctx, SolicitudDeCompraSimple(ctx, unidades: 5m, idOrdenCompra: null));
        Assert.Equal(HttpStatusCode.Created, estadoCreacion);

        var transaccionIniciada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var puedeContinuar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interceptor = new InterceptorDePausaTrasIniciarLaTransaccion(transaccionIniciada, puedeContinuar);

        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddDbContext<WaysDbContext>((_, options) => options.AddInterceptors(interceptor))));

        using var clienteAnular = factory.CreateClient();
        var login = await clienteAnular.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(ctx.MailAdmin, ctx.PasswordAdmin));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var tareaAnular = clienteAnular.PostAsync($"/api/ordenes-compra/{enviada.Id}/anular", null);
        await transaccionIniciada.Task;

        var solicitudRelink = SolicitudDeCompraSimple(ctx, unidades: 5m, idOrdenCompra: enviada.Id);
        var respuestaPut = await ctx.Admin.PutAsJsonAsync($"/api/compras/{borrador!.Id}", solicitudRelink);
        var cuerpoPut = await respuestaPut.Content.ReadAsStringAsync();
        Assert.True(respuestaPut.StatusCode == HttpStatusCode.OK, cuerpoPut);

        puedeContinuar.TrySetResult();

        var respuestaAnular = await tareaAnular;
        var cuerpoAnular = await respuestaAnular.Content.ReadAsStringAsync();
        Assert.True(respuestaAnular.StatusCode == HttpStatusCode.Conflict, cuerpoAnular);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpoAnular, OpcionesJson);
        Assert.Equal("orden_compra_con_recepciones", problema.GetProperty("codigo").GetString());

        Assert.Equal(EstadoOrdenCompra.Enviada, (await LeerOrdenAsync(ctx, enviada.Id)).Estado);
    }

    // ================================================================================================
    // task 4.11 / mutation target #34a: matriz de autorización — las CINCO rutas de escritura
    // ================================================================================================

    [Fact]
    public async Task VendedorEsRechazadoEnLasCincoRutasDeEscrituraDeOrdenesDeCompra()
    {
        var ctx = await PrepararAsync(nameof(VendedorEsRechazadoEnLasCincoRutasDeEscrituraDeOrdenesDeCompra));
        using var vendedor = await CrearUsuarioConRolAsync(ctx, "vendedor-matriz-oc", RolConocido.Vendedor);
        var creada = await CrearBorradorDeOrdenAsync(ctx);
        var enviada = await CrearYEnviarOrdenAsync(ctx, idArticulo: ctx.IdArticulo2);

        var crear = await vendedor.PostAsJsonAsync("/api/ordenes-compra", SolicitudDeOrdenSimple(ctx));
        Assert.Equal(HttpStatusCode.Forbidden, crear.StatusCode);

        var editar = await vendedor.PutAsJsonAsync($"/api/ordenes-compra/{creada.Id}", SolicitudDeOrdenSimple(ctx));
        Assert.Equal(HttpStatusCode.Forbidden, editar.StatusCode);

        var enviar = await vendedor.PostAsync($"/api/ordenes-compra/{creada.Id}/enviar", null);
        Assert.Equal(HttpStatusCode.Forbidden, enviar.StatusCode);

        var cerrar = await vendedor.PostAsync($"/api/ordenes-compra/{enviada.Id}/cerrar", null);
        Assert.Equal(HttpStatusCode.Forbidden, cerrar.StatusCode);

        var anular = await vendedor.PostAsync($"/api/ordenes-compra/{enviada.Id}/anular", null);
        Assert.Equal(HttpStatusCode.Forbidden, anular.StatusCode);
    }

    [Fact]
    public async Task SupervisorEsRechazadoEnLasCincoRutasDeEscrituraDeOrdenesDeCompra()
    {
        var ctx = await PrepararAsync(nameof(SupervisorEsRechazadoEnLasCincoRutasDeEscrituraDeOrdenesDeCompra));
        using var supervisor = await CrearUsuarioConRolAsync(ctx, "supervisor-matriz-oc", RolConocido.Supervisor);
        var creada = await CrearBorradorDeOrdenAsync(ctx);
        var enviada = await CrearYEnviarOrdenAsync(ctx, idArticulo: ctx.IdArticulo2);

        var crear = await supervisor.PostAsJsonAsync("/api/ordenes-compra", SolicitudDeOrdenSimple(ctx));
        Assert.Equal(HttpStatusCode.Forbidden, crear.StatusCode);

        var editar = await supervisor.PutAsJsonAsync($"/api/ordenes-compra/{creada.Id}", SolicitudDeOrdenSimple(ctx));
        Assert.Equal(HttpStatusCode.Forbidden, editar.StatusCode);

        var enviar = await supervisor.PostAsync($"/api/ordenes-compra/{creada.Id}/enviar", null);
        Assert.Equal(HttpStatusCode.Forbidden, enviar.StatusCode);

        var cerrar = await supervisor.PostAsync($"/api/ordenes-compra/{enviada.Id}/cerrar", null);
        Assert.Equal(HttpStatusCode.Forbidden, cerrar.StatusCode);

        var anular = await supervisor.PostAsync($"/api/ordenes-compra/{enviada.Id}/anular", null);
        Assert.Equal(HttpStatusCode.Forbidden, anular.StatusCode);
    }

    [Fact]
    public async Task AdminEjerceElCicloCompletoDeEscrituraDeOrdenesDeCompra()
    {
        var ctx = await PrepararAsync(nameof(AdminEjerceElCicloCompletoDeEscrituraDeOrdenesDeCompra));

        var creada = await CrearBorradorDeOrdenAsync(ctx);

        var editar = await ctx.Admin.PutAsJsonAsync($"/api/ordenes-compra/{creada.Id}", SolicitudDeOrdenSimple(ctx));
        Assert.Equal(HttpStatusCode.OK, editar.StatusCode);

        var enviar = await ctx.Admin.PostAsync($"/api/ordenes-compra/{creada.Id}/enviar", null);
        Assert.Equal(HttpStatusCode.OK, enviar.StatusCode);

        var cerrar = await ctx.Admin.PostAsync($"/api/ordenes-compra/{creada.Id}/cerrar", null);
        Assert.Equal(HttpStatusCode.OK, cerrar.StatusCode);

        // Segunda OC, sin enviar — ejerce anular desde borrador (número nunca quemado).
        var segunda = await CrearBorradorDeOrdenAsync(ctx, idArticulo: null);
        var anular = await ctx.Admin.PostAsync($"/api/ordenes-compra/{segunda.Id}/anular", null);
        Assert.Equal(HttpStatusCode.OK, anular.StatusCode);
    }

    private static async Task<OrdenDeCompraBorrador> CrearBorradorDeOrdenAsync(Contexto ctx, int? idArticulo) =>
        await CrearBorradorDeOrdenAsync(ctx, SolicitudDeOrdenSimple(ctx, idArticulo: idArticulo));

    // ================================================================================================
    // task 4.11: aislamiento entre tenants — cerrar/anular una OC de OTRO tenant es 404 (ADR-8)
    // ================================================================================================

    [Fact]
    public async Task CerrarYAnularUnaOrdenDeOtroTenantEsRechazadaComo404()
    {
        var tenantA = await PrepararAsync(nameof(CerrarYAnularUnaOrdenDeOtroTenantEsRechazadaComo404) + "-A");
        var tenantB = await PrepararAsync(nameof(CerrarYAnularUnaOrdenDeOtroTenantEsRechazadaComo404) + "-B");
        var ordenDeA = await CrearYEnviarOrdenAsync(tenantA);

        var cerrar = await tenantB.Admin.PostAsync($"/api/ordenes-compra/{ordenDeA.Id}/cerrar", null);
        Assert.Equal(HttpStatusCode.NotFound, cerrar.StatusCode);

        var anular = await tenantB.Admin.PostAsync($"/api/ordenes-compra/{ordenDeA.Id}/anular", null);
        Assert.Equal(HttpStatusCode.NotFound, anular.StatusCode);
    }

    // ================================================================================================
    // task 4.12: no-regresión — ComprasLifecycleTests/ComprasAnulacionYConcurrenciaTests intactos.
    // Verificado por git diff --stat, no por un assert acá (ver PR body).
    // ================================================================================================

    // task 4.13: cuenta-corriente-de-proveedores untouched — verificado por git diff --stat.

    // ================================================================================================
    // task 4.18: gate guard — ninguna DDL nueva
    // ================================================================================================

    [Fact]
    public async Task NoHayCambiosPendientesDeModeloEnLaSlice4()
    {
        using var _ = fixture.CreateClient();
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        Assert.False(db.Database.HasPendingModelChanges());
    }
}
