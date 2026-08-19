using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-16-ordenes-de-compra, Slice 3 (tasks 3.12-3.38; design: Transactions — CONFIRMAR
/// COMPRA/ANULAR COMPRA; decisiones 1, 2, 3, 5, 8, 9). La ligadura OC↔comprobante +
/// <c>EscriturasDeOrdenDeCompra.ProyectarEstadoAsync</c>/<c>BloquearYExigirNoAnuladaAsync</c>
/// llamadas desde <c>ServicioDeCompras.EjecutarConfirmarAsync</c>/<c>EjecutarAnulacionAsync</c>.
///
/// DEVIATION registrada (decisión 15 de tasks.md): la mitad "anular OC × confirmar reception" del
/// binding gate test (c) (task 3.22) NO puede implementarse en esta slice — <c>POST
/// /api/ordenes-compra/{id}/anular</c> y <c>ServicioDeOrdenesDeCompra.AnularAsync</c> son tareas de
/// la SLICE 4 (tasks.md, Slice 4, task 4.2/4.3); no existe ningún camino de escritura de anulación
/// de OC en el árbol de esta slice para correr esa carrera. La mitad "confirm × confirm" SÍ se
/// implementa acá (es la prueba central de decisión 2: dos statements separados bajo
/// <c>READ COMMITTED</c>). Los escenarios que necesitan una OC ya <c>anulada</c> (tasks 3.15, 3.19)
/// se siembran directo por EF — el estado de la fila, no el camino que la produjo, es lo que
/// <c>ExigirOrdenLigableAsync</c>/<c>BloquearYExigirNoAnuladaAsync</c> interpretan.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ServicioDeComprasLigaduraTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdPuntoVenta, HttpClient Admin, int IdProveedor, int IdProveedor2, int IdArticulo,
        int IdArticulo2, int IdAlicuotaIva21, int IdTipoCFA, int IdEmpleadoAdmin);

    /// <summary>Decisión 13 (tasks.md): ids deliberadamente desincronizados — cada entidad nace en
    /// su propia tabla, ninguna forzada a alinearse con otra.</summary>
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

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Ligadura-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
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
        var proveedor2 = new Proveedor
        {
            IdTenant = resultado.IdTenant, RazonSocial = $"{nombre}-otro", IdCondicionFiscal = condicionFiscal.Id,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Proveedores.AddRange(proveedor, proveedor2);
        await db.SaveChangesAsync();

        var articulo1 = new Articulo
        {
            IdTenant = resultado.IdTenant, CodigoInterno = $"{nombre}-lig-1-{Guid.NewGuid():N}", Nombre = "Ligadura Articulo 1",
            IdArea = area.Id, IdAlicuotaIva = idAlicuotaIva21, UnidadVenta = UnidadVenta.Unidad, EsProducto = true,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        var articulo2 = new Articulo
        {
            IdTenant = resultado.IdTenant, CodigoInterno = $"{nombre}-lig-2-{Guid.NewGuid():N}", Nombre = "Ligadura Articulo 2",
            IdArea = area.Id, IdAlicuotaIva = idAlicuotaIva21, UnidadVenta = UnidadVenta.Unidad, EsProducto = true,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.AddRange(articulo1, articulo2);
        await db.SaveChangesAsync();

        var idTipoCFA = await db.TiposComprobante.Where(t => t.Codigo == "C-FA").Select(t => t.Id).SingleAsync();
        var idEmpleadoAdmin = await db.Usuarios.Where(u => u.Mail == mailAdmin).Select(u => u.Id).FirstAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdPuntoVenta, admin, proveedor.Id, proveedor2.Id, articulo1.Id, articulo2.Id,
            idAlicuotaIva21, idTipoCFA, idEmpleadoAdmin);
    }

    // ---- helpers: órdenes de compra (slice 2, vía HTTP) ------------------------------------------

    private static SolicitudDeOrdenDeCompra SolicitudDeOrdenSimple(
        Contexto ctx, decimal cantidad = 10m, decimal? costo = 100m, int? idArticulo = null, int? idProveedor = null) =>
        new(
            idProveedor ?? ctx.IdProveedor, ctx.IdPuntoVenta, null, null,
            [new LineaDeOrdenSolicitada(idArticulo ?? ctx.IdArticulo, "Item de orden", cantidad, costo)]);

    private static async Task<OrdenDeCompraBorrador> CrearBorradorDeOrdenAsync(Contexto ctx, SolicitudDeOrdenDeCompra solicitud)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/ordenes-compra", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<OrdenDeCompraBorrador>(cuerpo, OpcionesJson)!;
    }

    private static async Task<OrdenDeCompraBorrador> EnviarOrdenAsync(Contexto ctx, int id)
    {
        var respuesta = await ctx.Admin.PostAsync($"/api/ordenes-compra/{id}/enviar", null);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<OrdenDeCompraBorrador>(cuerpo, OpcionesJson)!;
    }

    private static async Task<OrdenDeCompraBorrador> CrearYEnviarOrdenAsync(
        Contexto ctx, decimal cantidad = 10m, decimal? costo = 100m, int? idArticulo = null, int? idProveedor = null)
    {
        var creada = await CrearBorradorDeOrdenAsync(ctx, SolicitudDeOrdenSimple(ctx, cantidad, costo, idArticulo, idProveedor));
        return await EnviarOrdenAsync(ctx, creada.Id);
    }

    /// <summary>Siembra directa por EF — el único camino disponible en esta slice para una OC
    /// <c>anulada</c> (<c>POST /{id}/anular</c> es slice 4, ver el doc-comment de la clase). El
    /// estado de la fila es lo que el guard interpreta, no el camino que la produjo.</summary>
    private async Task<int> SembrarOrdenAnuladaAsync(Contexto ctx)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var orden = new OrdenCompra
        {
            IdTenant = ctx.IdTenant, IdPuntoVenta = ctx.IdPuntoVenta, IdProveedor = ctx.IdProveedor,
            IdEmpleado = ctx.IdEmpleadoAdmin, Numero = 999, FechaEmision = ahora, FechaEnvio = ahora,
            Estado = EstadoOrdenCompra.Anulada, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.OrdenesCompra.Add(orden);
        await db.SaveChangesAsync();
        return orden.Id;
    }

    // ---- helpers: comprobantes de compra (slices 1-2, ya existentes; el link es esta slice) ------

    private static SolicitudDeCompra SolicitudDeCompraSimple(
        Contexto ctx, decimal unidades = 10m, decimal costoUnitario = 100m, int? idArticulo = null,
        int? idOrdenCompra = null, string? numeroExterno = null) =>
        new(
            ctx.IdProveedor, ctx.IdTipoCFA, ctx.IdPuntoVenta, numeroExterno ?? $"0001-{Guid.NewGuid():N}"[..8], DateOnly.FromDateTime(DateTime.UtcNow), null,
            [new LineaDeCompraSolicitada(idArticulo ?? ctx.IdArticulo, "Item de recepción", unidades, null, null, costoUnitario, 0m, ctx.IdAlicuotaIva21, false)],
            idOrdenCompra);

    private static async Task<(HttpStatusCode Estado, CompraDetalle? Compra, JsonElement? Problema)> CrearBorradorDeCompraAsync(
        Contexto ctx, SolicitudDeCompra solicitud)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/compras", solicitud);
        if (respuesta.StatusCode != HttpStatusCode.Created)
        {
            return (respuesta.StatusCode, null, await respuesta.Content.ReadFromJsonAsync<JsonElement>());
        }

        return (respuesta.StatusCode, await respuesta.Content.ReadFromJsonAsync<CompraDetalle>(OpcionesJson), null);
    }

    private static async Task<HttpResponseMessage> ConfirmarCompraHttpAsync(Contexto ctx, int id) =>
        await ctx.Admin.PostAsync($"/api/compras/{id}/confirmar", null);

    private static async Task<CompraDetalle> ConfirmarCompraAsync(Contexto ctx, int id)
    {
        var respuesta = await ConfirmarCompraHttpAsync(ctx, id);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<CompraDetalle>(cuerpo, OpcionesJson)!;
    }

    private static async Task<HttpResponseMessage> AnularCompraHttpAsync(Contexto ctx, int id) =>
        await ctx.Admin.PostAsync($"/api/compras/{id}/anular", null);

    /// <summary>Crea + confirma una recepción ligada a <paramref name="idOrdenCompra"/> en un solo
    /// paso — la forma que casi todos los tests de proyección necesitan.</summary>
    private static async Task<CompraDetalle> CrearYConfirmarRecepcionAsync(
        Contexto ctx, int idOrdenCompra, decimal unidades, int? idArticulo = null)
    {
        var (estado, creada, problema) = await CrearBorradorDeCompraAsync(
            ctx, SolicitudDeCompraSimple(ctx, unidades, idArticulo: idArticulo, idOrdenCompra: idOrdenCompra));
        Assert.True(estado == HttpStatusCode.Created, problema?.ToString() ?? estado.ToString());
        return await ConfirmarCompraAsync(ctx, creada!.Id);
    }

    private async Task<EstadoOrdenCompra> LeerEstadoDeOrdenAsync(Contexto ctx, int idOrdenCompra)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        return (await db.OrdenesCompra.AsNoTracking().FirstAsync(o => o.Id == idOrdenCompra)).Estado;
    }

    private async Task<OrdenCompra> LeerOrdenAsync(Contexto ctx, int idOrdenCompra)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        return await db.OrdenesCompra.AsNoTracking().FirstAsync(o => o.Id == idOrdenCompra);
    }

    // ================================================================================================
    // task 3.13-3.16: ligadura happy/blocked paths + state-gating + freeze + round-trip
    // ================================================================================================

    [Fact]
    public async Task UnBorradorLigaAUnaOrdenEnviadaYPersiste()
    {
        var ctx = await PrepararAsync(nameof(UnBorradorLigaAUnaOrdenEnviadaYPersiste));
        var orden = await CrearYEnviarOrdenAsync(ctx);

        var (estado, creada, problema) = await CrearBorradorDeCompraAsync(
            ctx, SolicitudDeCompraSimple(ctx, idOrdenCompra: orden.Id));
        Assert.True(estado == HttpStatusCode.Created, problema?.ToString());
        Assert.Equal(orden.Id, creada!.IdOrdenCompra);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var persistido = await db.ComprobantesCompra.AsNoTracking().FirstAsync(c => c.Id == creada.Id);
        Assert.Equal(orden.Id, persistido.IdOrdenCompra);
    }

    [Fact]
    public async Task UnProveedorNoCoincidenteNoPuedeLigar()
    {
        var ctx = await PrepararAsync(nameof(UnProveedorNoCoincidenteNoPuedeLigar));
        var orden = await CrearYEnviarOrdenAsync(ctx); // dueña de ctx.IdProveedor

        var (estado, creada, problema) = await CrearBorradorDeCompraAsync(
            ctx,
            SolicitudDeCompraSimple(ctx, idOrdenCompra: orden.Id) with { IdProveedor = ctx.IdProveedor2 });

        Assert.Equal(HttpStatusCode.BadRequest, estado);
        Assert.Null(creada);
        Assert.Equal("proveedor_no_coincide_con_la_orden", problema!.Value.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(0, await db.ComprobantesCompra.CountAsync());
    }

    [Fact]
    public async Task UnPuntoDeVentaNoCoincidenteNoPuedeLigar()
    {
        var ctx = await PrepararAsync(nameof(UnPuntoDeVentaNoCoincidenteNoPuedeLigar));
        var orden = await CrearYEnviarOrdenAsync(ctx);

        // Segundo PV de la misma empresa — la fila de la OC quedó en ctx.IdPuntoVenta.
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var idEmpresa = await db.PuntosVenta.Where(pv => pv.Id == ctx.IdPuntoVenta).Select(pv => pv.IdEmpresa).FirstAsync();
        var ahora = DateTimeOffset.UtcNow;
        var pv2 = new PuntoVenta { IdTenant = ctx.IdTenant, IdEmpresa = idEmpresa, Nombre = "PV2", CreatedAt = ahora, UpdatedAt = ahora };
        db.PuntosVenta.Add(pv2);
        await db.SaveChangesAsync();

        var (estado, creada, problema) = await CrearBorradorDeCompraAsync(
            ctx,
            SolicitudDeCompraSimple(ctx, idOrdenCompra: orden.Id) with { IdPuntoVenta = pv2.Id });

        Assert.Equal(HttpStatusCode.BadRequest, estado);
        Assert.Null(creada);
        Assert.Equal("punto_de_venta_no_coincide_con_la_orden", problema!.Value.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task LigarAUnaOrdenBorradorEsRechazada409()
    {
        var ctx = await PrepararAsync(nameof(LigarAUnaOrdenBorradorEsRechazada409));
        var borrador = await CrearBorradorDeOrdenAsync(ctx, SolicitudDeOrdenSimple(ctx)); // nunca enviada

        var (estado, creada, problema) = await CrearBorradorDeCompraAsync(
            ctx, SolicitudDeCompraSimple(ctx, idOrdenCompra: borrador.Id));

        Assert.Equal(HttpStatusCode.Conflict, estado);
        Assert.Null(creada);
        Assert.Equal("orden_compra_no_enviada", problema!.Value.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task LigarAUnaOrdenAnuladaEsRechazada409()
    {
        var ctx = await PrepararAsync(nameof(LigarAUnaOrdenAnuladaEsRechazada409));
        var idOrdenAnulada = await SembrarOrdenAnuladaAsync(ctx);

        var (estado, creada, problema) = await CrearBorradorDeCompraAsync(
            ctx, SolicitudDeCompraSimple(ctx, idOrdenCompra: idOrdenAnulada));

        Assert.Equal(HttpStatusCode.Conflict, estado);
        Assert.Null(creada);
        Assert.Equal("orden_compra_anulada", problema!.Value.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task LigarAUnaOrdenCerradaEsPermitido()
    {
        var ctx = await PrepararAsync(nameof(LigarAUnaOrdenCerradaEsPermitido));
        var orden = await CrearYEnviarOrdenAsync(ctx, cantidad: 5m);
        await CrearYConfirmarRecepcionAsync(ctx, orden.Id, unidades: 5m); // cubre todo -> cierre automático
        Assert.Equal(EstadoOrdenCompra.Cerrada, await LeerEstadoDeOrdenAsync(ctx, orden.Id));

        var (estado, creada, problema) = await CrearBorradorDeCompraAsync(
            ctx, SolicitudDeCompraSimple(ctx, idOrdenCompra: orden.Id));

        Assert.True(estado == HttpStatusCode.Created, problema?.ToString());
        Assert.Equal(orden.Id, creada!.IdOrdenCompra);
    }

    [Fact]
    public async Task ElLinkSeCongelaAlConfirmarYCompraDetalleHaceRoundTrip()
    {
        var ctx = await PrepararAsync(nameof(ElLinkSeCongelaAlConfirmarYCompraDetalleHaceRoundTrip));
        var orden = await CrearYEnviarOrdenAsync(ctx);

        var (_, creada, _) = await CrearBorradorDeCompraAsync(ctx, SolicitudDeCompraSimple(ctx, idOrdenCompra: orden.Id));
        Assert.Equal(orden.Id, creada!.IdOrdenCompra);

        var confirmada = await ConfirmarCompraAsync(ctx, creada.Id);
        Assert.Equal(orden.Id, confirmada.IdOrdenCompra); // round-trip exacto (conflicto #4, dto-contract-honesty)

        // Congelado: ningún endpoint permite editar una compra que ya no es borrador.
        var intentoDeEdicion = await ctx.Admin.PutAsJsonAsync(
            $"/api/compras/{creada.Id}", SolicitudDeCompraSimple(ctx, idOrdenCompra: null));
        Assert.Equal(HttpStatusCode.Conflict, intentoDeEdicion.StatusCode);
    }

    // ================================================================================================
    // task 3.17-3.20: los escenarios de proyección del propio spec
    // ================================================================================================

    [Fact]
    public async Task ConfirmarUnaRecepcionLigadaMueveLaOrdenARecibidaParcial()
    {
        var ctx = await PrepararAsync(nameof(ConfirmarUnaRecepcionLigadaMueveLaOrdenARecibidaParcial));
        var orden = await CrearYEnviarOrdenAsync(ctx, cantidad: 100m);

        await CrearYConfirmarRecepcionAsync(ctx, orden.Id, unidades: 40m);

        Assert.Equal(EstadoOrdenCompra.RecibidaParcial, await LeerEstadoDeOrdenAsync(ctx, orden.Id));
    }

    [Fact]
    public async Task ConfirmarElRemanenteCierraLaOrdenAutomaticamente()
    {
        var ctx = await PrepararAsync(nameof(ConfirmarElRemanenteCierraLaOrdenAutomaticamente));
        var orden = await CrearYEnviarOrdenAsync(ctx, cantidad: 100m);

        await CrearYConfirmarRecepcionAsync(ctx, orden.Id, unidades: 40m);
        await CrearYConfirmarRecepcionAsync(ctx, orden.Id, unidades: 60m);

        var final = await LeerOrdenAsync(ctx, orden.Id);
        Assert.Equal(EstadoOrdenCompra.Cerrada, final.Estado);
        Assert.Null(final.IdEmpleadoCierre);
        Assert.NotNull(final.FechaCierre);
    }

    [Fact]
    public async Task ConfirmarContraUnaOrdenAnuladaEsRechazada409SinEscribirNada()
    {
        var ctx = await PrepararAsync(nameof(ConfirmarContraUnaOrdenAnuladaEsRechazada409SinEscribirNada));
        var idOrdenAnulada = await SembrarOrdenAnuladaAsync(ctx);

        // El link en sí se rechaza al crear el borrador (LigarAUnaOrdenAnuladaEsRechazada409) —
        // para llegar al guard de CONFIRMAR hace falta que la orden se anule DESPUÉS de que el
        // borrador ya está ligado: se liga a una orden viva, se anula por EF, y recién ahí se
        // confirma.
        var orden = await CrearYEnviarOrdenAsync(ctx);
        var (_, creada, _) = await CrearBorradorDeCompraAsync(ctx, SolicitudDeCompraSimple(ctx, idOrdenCompra: orden.Id));

        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var fila = await db.OrdenesCompra.FirstAsync(o => o.Id == orden.Id);
            fila.Estado = EstadoOrdenCompra.Anulada;
            await db.SaveChangesAsync();
        }

        var respuesta = await ConfirmarCompraHttpAsync(ctx, creada!.Id);
        var cuerpo = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal("orden_compra_anulada", cuerpo.GetProperty("codigo").GetString());

        await using var dbVerif = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var compraTrasElIntento = await dbVerif.ComprobantesCompra.AsNoTracking().FirstAsync(c => c.Id == creada.Id);
        Assert.Equal(EstadoCompra.Borrador, compraTrasElIntento.Estado); // no write: sigue borrador
        Assert.Equal(0, await dbVerif.MovimientosStock.CountAsync(m => m.IdComprobanteCompra == creada.Id));
    }

    [Fact]
    public async Task AnularLaUnicaRecepcionDeUnaOrdenCerradaAutomaticamenteLaDevuelveAEnviada()
    {
        var ctx = await PrepararAsync(nameof(AnularLaUnicaRecepcionDeUnaOrdenCerradaAutomaticamenteLaDevuelveAEnviada));
        var orden = await CrearYEnviarOrdenAsync(ctx, cantidad: 30m);
        var recepcion = await CrearYConfirmarRecepcionAsync(ctx, orden.Id, unidades: 30m);
        Assert.Equal(EstadoOrdenCompra.Cerrada, await LeerEstadoDeOrdenAsync(ctx, orden.Id));

        var anulacion = await AnularCompraHttpAsync(ctx, recepcion.Id);
        Assert.Equal(HttpStatusCode.OK, anulacion.StatusCode);

        Assert.Equal(EstadoOrdenCompra.Enviada, await LeerEstadoDeOrdenAsync(ctx, orden.Id));
    }

    // ================================================================================================
    // task 3.21: fidelidad de la derivación (rule 11 — cantidades/fixtures discriminantes)
    // ================================================================================================

    [Fact]
    public async Task DosLineasDelMismoArticuloSeAgrupanYUnaSobreEntregaNoBloqueaNiErrorea()
    {
        var ctx = await PrepararAsync(nameof(DosLineasDelMismoArticuloSeAgrupanYUnaSobreEntregaNoBloqueaNiErrorea));

        // Server-asigna `orden` 1/2 dentro del mismo replace-set: dos líneas de UN artículo, 3+4=7.
        var creadaOrden = await CrearBorradorDeOrdenAsync(
            ctx,
            new SolicitudDeOrdenDeCompra(
                ctx.IdProveedor, ctx.IdPuntoVenta, null, null,
                [
                    new LineaDeOrdenSolicitada(ctx.IdArticulo, "Linea A", 3m, 100m),
                    new LineaDeOrdenSolicitada(ctx.IdArticulo, "Linea B", 4m, 100m)
                ]));
        var orden = await EnviarOrdenAsync(ctx, creadaOrden.Id);

        // Una recepción de 8 unidades — sobre-entrega contra las 7 pedidas (grupo por artículo).
        await CrearYConfirmarRecepcionAsync(ctx, orden.Id, unidades: 8m);

        Assert.Equal(EstadoOrdenCompra.Cerrada, await LeerEstadoDeOrdenAsync(ctx, orden.Id));
    }

    /// <summary>Mutation target #25: <c>algoRecibido</c> tiene que salir del lado RECEPCIÓN. Un
    /// artículo NUNCA pedido, recibido por sustitución, no cubre lo pedido (sigue
    /// <c>recibida_parcial</c>, nunca <c>enviada</c>) — si <c>algoRecibido</c> se derivara (mal) del
    /// lado pedido, este fixture leería 0 recibido y la orden quedaría stale en <c>enviada</c>.</summary>
    [Fact]
    public async Task UnaEntregaPorSustitucionNuncaPedidaMuevaAOrdenARecibidaParcial()
    {
        var ctx = await PrepararAsync(nameof(UnaEntregaPorSustitucionNuncaPedidaMuevaAOrdenARecibidaParcial));
        var orden = await CrearYEnviarOrdenAsync(ctx, cantidad: 10m, idArticulo: ctx.IdArticulo);

        // La recepción trae el OTRO artículo — nada del pedido original llega.
        await CrearYConfirmarRecepcionAsync(ctx, orden.Id, unidades: 5m, idArticulo: ctx.IdArticulo2);

        Assert.Equal(EstadoOrdenCompra.RecibidaParcial, await LeerEstadoDeOrdenAsync(ctx, orden.Id));
    }

    /// <summary>Mutation target #22: solo comprobantes <c>confirmada</c> cuentan. Un borrador
    /// ligado con cantidad suficiente para cubrir todo el pedido NO debe cerrarlo — la orden se
    /// mantiene <c>recibida_parcial</c> tras una recepción chica de OTRO artículo (que solo sirve
    /// para disparar una re-proyección observable).</summary>
    [Fact]
    public async Task UnaRecepcionEnBorradorLigadaNoCuentaParaLaDerivacion()
    {
        var ctx = await PrepararAsync(nameof(UnaRecepcionEnBorradorLigadaNoCuentaParaLaDerivacion));
        var orden = await CrearYEnviarOrdenAsync(ctx, cantidad: 10m, idArticulo: ctx.IdArticulo);

        // Ligada, pero JAMÁS confirmada.
        await CrearBorradorDeCompraAsync(ctx, SolicitudDeCompraSimple(ctx, unidades: 10m, idOrdenCompra: orden.Id));

        // Dispara una re-proyección real con una recepción chica de otro artículo.
        await CrearYConfirmarRecepcionAsync(ctx, orden.Id, unidades: 1m, idArticulo: ctx.IdArticulo2);

        Assert.Equal(EstadoOrdenCompra.RecibidaParcial, await LeerEstadoDeOrdenAsync(ctx, orden.Id));
    }

    /// <summary>Mutation target #23: filas con <c>deleted_at</c> (en cualquiera de las dos tablas
    /// del join) se excluyen de la derivación. Cierra automático con la primera recepción; tras
    /// soft-deletar su item, una segunda recepción (de otro artículo, para re-disparar la
    /// proyección) debe REGRESAR la orden a <c>recibida_parcial</c> — probando que la cantidad
    /// soft-deleteada dejó de contar.</summary>
    [Fact]
    public async Task UnItemDeRecepcionSoftDeleteadoDejaDeContarEnLaDerivacion()
    {
        var ctx = await PrepararAsync(nameof(UnItemDeRecepcionSoftDeleteadoDejaDeContarEnLaDerivacion));
        var orden = await CrearYEnviarOrdenAsync(ctx, cantidad: 10m, idArticulo: ctx.IdArticulo);

        var recepcion = await CrearYConfirmarRecepcionAsync(ctx, orden.Id, unidades: 10m, idArticulo: ctx.IdArticulo);
        Assert.Equal(EstadoOrdenCompra.Cerrada, await LeerEstadoDeOrdenAsync(ctx, orden.Id));

        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var item = await db.ItemsComprobanteCompra.FirstAsync(i => i.IdComprobanteCompra == recepcion.Id);
            item.DeletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        // Re-dispara la proyección con una recepción chica de otro artículo.
        await CrearYConfirmarRecepcionAsync(ctx, orden.Id, unidades: 1m, idArticulo: ctx.IdArticulo2);

        Assert.Equal(EstadoOrdenCompra.RecibidaParcial, await LeerEstadoDeOrdenAsync(ctx, orden.Id));
    }

    /// <summary>Regla 12c aplicada a la derivación: una recepción de OTRA orden del MISMO proveedor
    /// no debe filtrarse hacia esta — cada OC solo ve su propio libro.</summary>
    [Fact]
    public async Task UnaRecepcionDeOtraOrdenDelMismoProveedorNoCuenta()
    {
        var ctx = await PrepararAsync(nameof(UnaRecepcionDeOtraOrdenDelMismoProveedorNoCuenta));
        var ordenA = await CrearYEnviarOrdenAsync(ctx, cantidad: 10m, idArticulo: ctx.IdArticulo);
        var ordenB = await CrearYEnviarOrdenAsync(ctx, cantidad: 10m, idArticulo: ctx.IdArticulo);

        await CrearYConfirmarRecepcionAsync(ctx, ordenB.Id, unidades: 10m, idArticulo: ctx.IdArticulo);

        Assert.Equal(EstadoOrdenCompra.Cerrada, await LeerEstadoDeOrdenAsync(ctx, ordenB.Id));
        Assert.Equal(EstadoOrdenCompra.Enviada, await LeerEstadoDeOrdenAsync(ctx, ordenA.Id)); // intacta
    }

    // ================================================================================================
    // task 3.22: la carrera confirm × confirm de dos recepciones de UNA orden (binding gate test c)
    // ================================================================================================

    [Fact]
    public async Task DosConfirmacionesConcurrentesDeDosRecepcionesDeUnaOrdenNuncaSeSobreescriben()
    {
        var ctx = await PrepararAsync(nameof(DosConfirmacionesConcurrentesDeDosRecepcionesDeUnaOrdenNuncaSeSobreescriben));
        var orden = await CrearBorradorDeOrdenAsync(
            ctx,
            new SolicitudDeOrdenDeCompra(
                ctx.IdProveedor, ctx.IdPuntoVenta, null, null,
                [
                    new LineaDeOrdenSolicitada(ctx.IdArticulo, "Linea A", 10m, 100m),
                    new LineaDeOrdenSolicitada(ctx.IdArticulo2, "Linea B", 10m, 100m)
                ]));
        var enviada = await EnviarOrdenAsync(ctx, orden.Id);

        var (_, compraA, _) = await CrearBorradorDeCompraAsync(
            ctx, SolicitudDeCompraSimple(ctx, unidades: 10m, idArticulo: ctx.IdArticulo, idOrdenCompra: enviada.Id));
        var (_, compraB, _) = await CrearBorradorDeCompraAsync(
            ctx, SolicitudDeCompraSimple(ctx, unidades: 10m, idArticulo: ctx.IdArticulo2, idOrdenCompra: enviada.Id));

        var tareaA = ConfirmarCompraHttpAsync(ctx, compraA!.Id);
        var tareaB = ConfirmarCompraHttpAsync(ctx, compraB!.Id);
        var respuestas = await Task.WhenAll(tareaA, tareaB);

        foreach (var respuesta in respuestas)
        {
            var cuerpo = await respuesta.Content.ReadAsStringAsync();
            Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        }

        // Ambas confirmaciones cubren TODO lo pedido (10+10 de dos artículos distintos) — el
        // resultado tiene que ser la SUMA de las dos, nunca solo una (design: Concurrency
        // guarantees, "never only one of them"). Si el lock/derivación de statement 1/2 fallaran,
        // una de las dos pisaría a la otra y la orden quedaría en recibida_parcial en vez de cerrada.
        Assert.Equal(EstadoOrdenCompra.Cerrada, await LeerEstadoDeOrdenAsync(ctx, enviada.Id));
    }

    // ================================================================================================
    // task 3.23: fault-point — una falla DESPUÉS de la proyección deja la orden intacta
    // ================================================================================================

    [Fact]
    public async Task UnaFallaDespuesDeLaProyeccionEnConfirmarDejaLaOrdenSinCambios()
    {
        var ctx = await PrepararAsync(nameof(UnaFallaDespuesDeLaProyeccionEnConfirmarDejaLaOrdenSinCambios));
        var orden = await CrearYEnviarOrdenAsync(ctx, cantidad: 10m);

        // Artículo con control de lote efectivo (controla_lote AND lotes_habilitado) — una línea
        // SIN codigo_lote/fecha_vencimiento pasa la validación de borrador (ValidarVencimientosDe
        // Recepcion solo rechaza un codigo sin fecha, o una fecha pasada; ninguno de los dos aplica
        // acá) pero dispara `lote_requerido` (400) en el paso 2.b de EjecutarConfirmarAsync —
        // DESPUÉS de que el paso 1.b (proyección de la OC, cantidad real, transición real
        // enviada→recibida_parcial) ya corrió sus tres statements DENTRO de la misma transacción.
        // El rollback tiene que deshacer también esa proyección ya ejecutada.
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var articulo = await db.Articulos.FirstAsync(a => a.Id == ctx.IdArticulo);
            articulo.ControlaLote = true;
            var idEmpresa = await db.PuntosVenta.Where(pv => pv.Id == ctx.IdPuntoVenta).Select(pv => pv.IdEmpresa).FirstAsync();
            db.Parametros.Add(new Parametro
            {
                IdTenant = ctx.IdTenant, IdEmpresa = idEmpresa, IdPuntoVenta = null,
                Clave = "lotes_habilitado", Valor = "true", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var solicitud = new SolicitudDeCompra(
            ctx.IdProveedor, ctx.IdTipoCFA, ctx.IdPuntoVenta, "0001-99999999", DateOnly.FromDateTime(DateTime.UtcNow), null,
            [new LineaDeCompraSolicitada(ctx.IdArticulo, "Item sin lote", 10m, null, null, 100m, 0m, ctx.IdAlicuotaIva21, false)],
            orden.Id);

        var (estadoCreacion, creada, _) = await CrearBorradorDeCompraAsync(ctx, solicitud);
        Assert.Equal(HttpStatusCode.Created, estadoCreacion);

        var respuesta = await ConfirmarCompraHttpAsync(ctx, creada!.Id);
        var cuerpoFalla = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(respuesta.StatusCode == HttpStatusCode.BadRequest, cuerpoFalla.ToString());
        Assert.Equal("lote_requerido", cuerpoFalla.GetProperty("codigo").GetString());

        // La orden nunca se movió de `enviada` — el rollback deshizo la proyección junto con todo
        // lo demás, aunque el UPDATE de 1.b ya había corrido dentro de la transacción abortada.
        Assert.Equal(EstadoOrdenCompra.Enviada, await LeerEstadoDeOrdenAsync(ctx, orden.Id));
    }

    // ================================================================================================
    // task 3.12: binding gate test (a) — zero-extra-statements (proxy comportamental, ver
    // EscriturasDeOrdenDeCompraLockOrderTests para la prueba de texto fuente complementaria)
    // ================================================================================================

    [Fact]
    public async Task UnConfirmSinOrdenLigadaNoTocaNingunaOrdenDeCompraExistente()
    {
        var ctx = await PrepararAsync(nameof(UnConfirmSinOrdenLigadaNoTocaNingunaOrdenDeCompraExistente));

        // Regla 12c: una OC hermana, completamente ajena a la compra que se va a confirmar.
        var hermana = await CrearYEnviarOrdenAsync(ctx);
        var hermanaAntes = await LeerOrdenAsync(ctx, hermana.Id);

        var (_, creada, _) = await CrearBorradorDeCompraAsync(ctx, SolicitudDeCompraSimple(ctx, idOrdenCompra: null));
        Assert.Null(creada!.IdOrdenCompra);
        await ConfirmarCompraAsync(ctx, creada.Id);

        var hermanaDespues = await LeerOrdenAsync(ctx, hermana.Id);
        Assert.Equal(hermanaAntes.Estado, hermanaDespues.Estado);
        Assert.Equal(hermanaAntes.UpdatedAt, hermanaDespues.UpdatedAt); // ni un statement la tocó
    }

    // ================================================================================================
    // task 3.38: FK 9 (fk_comprobantes_compra_orden_compra) client-reachable — backstop genérico
    // ================================================================================================

    [Fact]
    public async Task LigarAUnaOrdenInexistenteEsRechazadaComo404()
    {
        var ctx = await PrepararAsync(nameof(LigarAUnaOrdenInexistenteEsRechazadaComo404));

        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/compras", SolicitudDeCompraSimple(ctx, idOrdenCompra: 999_999_999));
        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }
}
