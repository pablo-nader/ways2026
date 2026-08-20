using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
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
using Ways.Application.Reportes;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-16-ordenes-de-compra, Slice 5 (tasks 5.6-5.17; design decisiones 12-15). Listado
/// paginado + detalle con cobertura por artículo y desvío de precio informativo — el read model
/// que cierra el ciclo de vida de la OC.
///
/// <c>mutation-proof-tests</c> regla 12 (la central en un read model): (a) <see
/// cref="DetalleLeeElEstadoDeLaColumnaSinRederivarloConUnaDesincronizacionCruda"/> desincroniza
/// <c>estado</c> con una escritura EF cruda (sin pasar por <see cref="EscriturasDeOrdenDeCompra"/>)
/// y prueba que el endpoint devuelve el sentinela, nunca el estado re-derivado; (b) toda proyección
/// devuelta se assertea con valores discriminantes por fila (nunca un fixture 1-línea/1-recepción
/// donde cualquier número en scope pasaría); (c) toda lectura siembra una OC hermana del mismo
/// tenant con sus propios items y assertea que queda intacta.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class OrdenesCompraLecturaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdPuntoVenta, int IdPuntoVenta2, HttpClient Admin, HttpClient Vendedor,
        int IdProveedor, int IdProveedor2, int IdArticulo, int IdArticulo2, string MailAdmin,
        string PasswordAdmin, int IdTipoCFB, int IdAlicuotaIva21);

    /// <summary>Decisión 13 (tasks.md): ids deliberadamente desincronizados — cada entidad nace en
    /// su propia tabla, nunca forzada a coincidir numéricamente con otra.</summary>
    private async Task<Contexto> PrepararAsync(string nombre, WebApplicationFactory<Program>? factory = null)
    {
        var host = factory ?? fixture;

        using var root = host.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var solicitud = new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin);
        var respuesta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = host.CreateClient();
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

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "OC-lectura-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva21 = await db.AlicuotasIva.Where(a => a.Nombre == "21%").Select(a => a.Id).FirstAsync();

        var condicionFiscal = new CondicionFiscal { Codigo = $"{nombre}-CF", Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.CondicionesFiscales.Add(condicionFiscal);
        await db.SaveChangesAsync();

        var proveedor1 = new Proveedor
        {
            IdTenant = resultado.IdTenant, RazonSocial = $"{nombre}-prov1", IdCondicionFiscal = condicionFiscal.Id,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        var proveedor2 = new Proveedor
        {
            IdTenant = resultado.IdTenant, RazonSocial = $"{nombre}-prov2", IdCondicionFiscal = condicionFiscal.Id,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Proveedores.AddRange(proveedor1, proveedor2);
        await db.SaveChangesAsync();

        var articulo1 = new Articulo
        {
            IdTenant = resultado.IdTenant, CodigoInterno = $"{nombre}-lec-1-{Guid.NewGuid():N}", Nombre = "Lectura Articulo 1",
            IdArea = area.Id, IdAlicuotaIva = idAlicuotaIva21, UnidadVenta = UnidadVenta.Unidad, EsProducto = true,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        var articulo2 = new Articulo
        {
            IdTenant = resultado.IdTenant, CodigoInterno = $"{nombre}-lec-2-{Guid.NewGuid():N}", Nombre = "Lectura Articulo 2",
            IdArea = area.Id, IdAlicuotaIva = idAlicuotaIva21, UnidadVenta = UnidadVenta.Unidad, EsProducto = true,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.AddRange(articulo1, articulo2);
        await db.SaveChangesAsync();

        // C-FB (DiscriminaIva = false) — elegido a propósito para mantener la aritmética de
        // CostoReal simple (costoEfectivo = total/cantidad, sin IVA de por medio) en las pruebas de
        // desvío de precio.
        var idTipoCFB = await db.TiposComprobante.Where(t => t.Codigo == "C-FB").Select(t => t.Id).SingleAsync();

        var vendedor = host.CreateClient();
        var mailVendedor = $"{nombre.ToLowerInvariant()}-vend@ways.test";
        var alta = await admin.PostAsJsonAsync(
            "/api/usuarios", new CrearUsuario("Vendedor lectura", mailVendedor, (int)RolConocido.Vendedor, "vendedor-password-larga"));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var loginVendedor = await vendedor.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailVendedor, "vendedor-password-larga"));
        Assert.Equal(HttpStatusCode.OK, loginVendedor.StatusCode);

        return new Contexto(
            resultado.IdTenant, resultado.IdPuntoVenta, puntoVenta2.Id, admin, vendedor,
            proveedor1.Id, proveedor2.Id, articulo1.Id, articulo2.Id, mailAdmin, resultado.PasswordTemporal,
            idTipoCFB, idAlicuotaIva21);
    }

    // ---- helpers: órdenes de compra ------------------------------------------------------------------

    private static async Task<OrdenDeCompraBorrador> CrearBorradorDeOrdenAsync(
        Contexto ctx, SolicitudDeOrdenDeCompra solicitud, HttpClient? cliente = null)
    {
        var respuesta = await (cliente ?? ctx.Admin).PostAsJsonAsync("/api/ordenes-compra", solicitud);
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

    private static async Task<OrdenDeCompraBorrador> CerrarOrdenAsync(Contexto ctx, int id, HttpClient? cliente = null)
    {
        var respuesta = await (cliente ?? ctx.Admin).PostAsync($"/api/ordenes-compra/{id}/cerrar", null);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<OrdenDeCompraBorrador>(cuerpo, OpcionesJson)!;
    }

    private static async Task<OrdenDeCompraBorrador> CrearYEnviarOrdenAsync(
        Contexto ctx, IReadOnlyList<LineaDeOrdenSolicitada> items, int? idProveedor = null,
        int? idPuntoVenta = null, DateOnly? fechaEsperada = null)
    {
        var solicitud = new SolicitudDeOrdenDeCompra(
            idProveedor ?? ctx.IdProveedor, idPuntoVenta ?? ctx.IdPuntoVenta, fechaEsperada, null, items);
        var creada = await CrearBorradorDeOrdenAsync(ctx, solicitud);
        return await EnviarOrdenAsync(ctx, creada.Id);
    }

    private static async Task<OrdenDeCompraDetalle> ObtenerDetalleAsync(Contexto ctx, int id, HttpClient? cliente = null)
    {
        var respuesta = await (cliente ?? ctx.Admin).GetAsync($"/api/ordenes-compra/{id}");
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<OrdenDeCompraDetalle>(cuerpo, OpcionesJson)!;
    }

    private static async Task<PaginaDeOrdenesDeCompra> ListarAsync(Contexto ctx, string query, HttpClient? cliente = null)
    {
        var respuesta = await (cliente ?? ctx.Admin).GetAsync($"/api/ordenes-compra{query}");
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<PaginaDeOrdenesDeCompra>(cuerpo, OpcionesJson)!;
    }

    // ---- helpers: comprobantes de compra (recepción) --------------------------------------------------

    private static async Task<CompraDetalle> CrearBorradorDeCompraAsync(Contexto ctx, SolicitudDeCompra solicitud)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/compras", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<CompraDetalle>(cuerpo, OpcionesJson)!;
    }

    private static async Task<CompraDetalle> ConfirmarCompraAsync(Contexto ctx, int id)
    {
        var respuesta = await ctx.Admin.PostAsync($"/api/compras/{id}/confirmar", null);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<CompraDetalle>(cuerpo, OpcionesJson)!;
    }

    /// <summary>Confirma una recepción completa de <paramref name="cantidad"/> unidades del
    /// artículo (por defecto <c>ctx.IdArticulo</c>) ligada a <paramref name="idOrdenCompra"/>, con
    /// costo unitario controlado (para que el desvío de precio salga exacto en las pruebas).</summary>
    private static async Task<CompraDetalle> RecibirYConfirmarAsync(
        Contexto ctx, int idOrdenCompra, decimal cantidad, decimal costoUnitario, int? idArticulo = null)
    {
        var creada = await CrearBorradorDeCompraAsync(
            ctx,
            new SolicitudDeCompra(
                ctx.IdProveedor, ctx.IdTipoCFB, ctx.IdPuntoVenta, $"0001-{Guid.NewGuid():N}"[..8],
                DateOnly.FromDateTime(DateTime.UtcNow), null,
                [new LineaDeCompraSolicitada(idArticulo ?? ctx.IdArticulo, "Item de recepción", cantidad, null, null, costoUnitario, 0m, ctx.IdAlicuotaIva21, false)],
                idOrdenCompra));
        return await ConfirmarCompraAsync(ctx, creada.Id);
    }

    private async Task<OrdenCompra> LeerOrdenAsync(Contexto ctx, int idOrdenCompra)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        return await db.OrdenesCompra.AsNoTracking().FirstAsync(o => o.Id == idOrdenCompra);
    }

    // ====================================================================================================
    // task 5.6/5.15 — paginación con fecha_emision empatada (RelojFijo); mutation target #34b parte 1
    // ====================================================================================================

    private sealed class RelojFijo(DateTimeOffset ahora) : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = ahora;
    }

    [Fact]
    public async Task PaginacionConFechaEmisionEmpatadaNoDuplicaNiSalteaFilas()
    {
        var instanteFijo = new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<IRelojDelSistema>(new RelojFijo(instanteFijo))));

        var ctx = await PrepararAsync(nameof(PaginacionConFechaEmisionEmpatadaNoDuplicaNiSalteaFilas), factory);

        // Tres OCs, misma fecha_emision (RelojFijo) — solo el id las distingue.
        var ordenA = await CrearYEnviarOrdenAsync(ctx, [new LineaDeOrdenSolicitada(ctx.IdArticulo, "L", 1m, 10m)]);
        var ordenB = await CrearYEnviarOrdenAsync(ctx, [new LineaDeOrdenSolicitada(ctx.IdArticulo, "L", 1m, 10m)]);
        var ordenC = await CrearYEnviarOrdenAsync(ctx, [new LineaDeOrdenSolicitada(ctx.IdArticulo, "L", 1m, 10m)]);

        var pagina1 = await ListarAsync(ctx, "?tamanio=2&pagina=1", ctx.Admin);
        var pagina2 = await ListarAsync(ctx, "?tamanio=2&pagina=2", ctx.Admin);

        Assert.Equal(3, pagina1.Total);
        Assert.Equal(2, pagina1.Items.Count);
        Assert.Single(pagina2.Items);

        // Orden esperado: id DESC como desempate — C, B en la página 1; A en la página 2. Sin
        // duplicados ni huecos entre las dos páginas.
        Assert.Equal([ordenC.Id, ordenB.Id], pagina1.Items.Select(i => i.Id).ToArray());
        Assert.Equal([ordenA.Id], pagina2.Items.Select(i => i.Id).ToArray());
    }

    // ====================================================================================================
    // task 5.7/5.16 — cada filtro con semillas asimétricas; mutation target #34b parte 2
    // ====================================================================================================

    [Fact]
    public async Task CadaFiltroIgnoradoDevolveriaDeMasConSemillasAsimetricas()
    {
        var ctx = await PrepararAsync(nameof(CadaFiltroIgnoradoDevolveriaDeMasConSemillasAsimetricas));

        var objetivo = await CrearYEnviarOrdenAsync(
            ctx, [new LineaDeOrdenSolicitada(ctx.IdArticulo, "L", 1m, 10m)],
            idProveedor: ctx.IdProveedor, idPuntoVenta: ctx.IdPuntoVenta);

        // Ruido: proveedor distinto, PV distinto, mismo request no debería aparecer bajo ningún filtro del objetivo.
        var ruidoProveedor = await CrearYEnviarOrdenAsync(
            ctx, [new LineaDeOrdenSolicitada(ctx.IdArticulo, "L", 1m, 10m)],
            idProveedor: ctx.IdProveedor2, idPuntoVenta: ctx.IdPuntoVenta);
        var ruidoPuntoVenta = await CrearYEnviarOrdenAsync(
            ctx, [new LineaDeOrdenSolicitada(ctx.IdArticulo, "L", 1m, 10m)],
            idProveedor: ctx.IdProveedor, idPuntoVenta: ctx.IdPuntoVenta2);

        // idProveedor
        var porProveedor = await ListarAsync(ctx, $"?idProveedor={ctx.IdProveedor}");
        Assert.DoesNotContain(porProveedor.Items, i => i.Id == ruidoProveedor.Id);
        Assert.Contains(porProveedor.Items, i => i.Id == objetivo.Id);

        // idPuntoVenta
        var porPuntoVenta = await ListarAsync(ctx, $"?idPuntoVenta={ctx.IdPuntoVenta}");
        Assert.DoesNotContain(porPuntoVenta.Items, i => i.Id == ruidoPuntoVenta.Id);
        Assert.Contains(porPuntoVenta.Items, i => i.Id == objetivo.Id);

        // estado — una orden borrador (nunca enviada) no debe aparecer bajo estado=Enviada
        var borrador = await CrearBorradorDeOrdenAsync(
            ctx, new SolicitudDeOrdenDeCompra(ctx.IdProveedor, ctx.IdPuntoVenta, null, null, [new LineaDeOrdenSolicitada(ctx.IdArticulo, "L", 1m, 10m)]));
        var porEstado = await ListarAsync(ctx, "?estado=Enviada");
        Assert.DoesNotContain(porEstado.Items, i => i.Id == borrador.Id);
        Assert.Contains(porEstado.Items, i => i.Id == objetivo.Id);

        // desde/hasta — ventana estrictamente futura no debe traer la orden objetivo
        var manana = DateTimeOffset.UtcNow.AddDays(1);
        var porDesde = await ListarAsync(ctx, $"?desde={Uri.EscapeDataString(manana.ToString("O"))}");
        Assert.DoesNotContain(porDesde.Items, i => i.Id == objetivo.Id);

        var haceUnDia = DateTimeOffset.UtcNow.AddDays(-1);
        var porHasta = await ListarAsync(ctx, $"?hasta={Uri.EscapeDataString(haceUnDia.ToString("O"))}");
        Assert.DoesNotContain(porHasta.Items, i => i.Id == objetivo.Id);
    }

    // ====================================================================================================
    // task 5.8/5.9 — regla 12(a): el detalle LEE la columna, nunca la re-deriva
    // ====================================================================================================

    [Fact]
    public async Task DetalleLeeElEstadoDeLaColumnaSinRederivarloConUnaDesincronizacionCruda()
    {
        var ctx = await PrepararAsync(nameof(DetalleLeeElEstadoDeLaColumnaSinRederivarloConUnaDesincronizacionCruda));

        // Enviada, sin ninguna recepción — la derivación honesta sería "enviada" para siempre.
        var enviada = await CrearYEnviarOrdenAsync(ctx, [new LineaDeOrdenSolicitada(ctx.IdArticulo, "L", 10m, 100m)]);

        // Regla 12c: hermana del mismo tenant, con sus propios items — no debe verse afectada.
        var hermana = await CrearYEnviarOrdenAsync(ctx, [new LineaDeOrdenSolicitada(ctx.IdArticulo2, "L2", 5m, 50m)]);

        // Desincronización cruda (EF, sin pasar por EscriturasDeOrdenDeCompra): fuerza la columna a
        // RecibidaParcial, un valor que la derivación real jamás produciría para esta orden (cero
        // comprobantes ligados). numero/fecha_envio ya satisfacen ck_ordenes_compra_envio_completo;
        // fecha_cierre/id_empleado_cierre siguen NULL, satisfaciendo ck_ordenes_compra_cierre.
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var orden = await db.OrdenesCompra.FirstAsync(o => o.Id == enviada.Id);
            orden.Estado = EstadoOrdenCompra.RecibidaParcial;
            await db.SaveChangesAsync();
        }

        var detalle = await ObtenerDetalleAsync(ctx, enviada.Id);
        Assert.Equal(EstadoOrdenCompra.RecibidaParcial, detalle.Estado);

        var detalleHermana = await ObtenerDetalleAsync(ctx, hermana.Id);
        Assert.Equal(EstadoOrdenCompra.Enviada, detalleHermana.Estado);
    }

    // ====================================================================================================
    // task 5.9 — cobertura por artículo + fidelidad de proyección (regla 11: fixture discriminante)
    // ====================================================================================================

    /// <summary>design: Testing Strategy — "derivation fidelity (rule 11)". Un solo fixture rico:
    /// dos líneas de OC del mismo artículo (3+4 ⇒ 7 pedidas), una recepción partida (2 luego 5,
    /// completando exactamente 7), un artículo recibido pero jamás pedido (Pedida = 0), una
    /// recepción soft-deleted (excluida), un comprobante ligado todavía en borrador (excluido —
    /// nunca confirmado), y una recepción de OTRA orden del mismo proveedor (excluida — scoping por
    /// id_orden_compra). Cierra con la prueba de projection fidelity: <c>ProyectorDeEstadoDeOrden.
    /// Proyectar</c> recomputado desde LOS NÚMEROS DE ESTA LECTURA coincide con la columna
    /// persistida (la escribió <see cref="EscriturasDeOrdenDeCompra"/> en <c>ConfirmarAsync</c>,
    /// una derivación totalmente separada — ver <see cref="ServicioDeOrdenesDeCompra.
    /// ObtenerCoberturaAsync"/>).</summary>
    [Fact]
    public async Task CoberturaPorArticuloDiscriminaCorrectamenteYLaProyeccionCoincideConLaColumna()
    {
        var ctx = await PrepararAsync(nameof(CoberturaPorArticuloDiscriminaCorrectamenteYLaProyeccionCoincideConLaColumna));

        // Regla 12c: OC hermana del mismo tenant, jamás tocada por este fixture.
        var hermana = await CrearYEnviarOrdenAsync(ctx, [new LineaDeOrdenSolicitada(ctx.IdArticulo2, "L2", 3m, 30m)]);

        // Otra OC del MISMO proveedor — su propia recepción NO debe contarse en la cobertura de la
        // orden objetivo (scoping por id_orden_compra, no por proveedor).
        var otraOrden = await CrearYEnviarOrdenAsync(ctx, [new LineaDeOrdenSolicitada(ctx.IdArticulo, "L-otra", 100m, 1m)]);
        await RecibirYConfirmarAsync(ctx, otraOrden.Id, cantidad: 100m, costoUnitario: 1m);

        // La orden objetivo: DOS líneas del mismo artículo, 3 + 4 = 7 pedidas, mismo costo
        // estimado (100) para que el promedio ponderado sea trivialmente 100.
        var objetivo = await CrearYEnviarOrdenAsync(
            ctx,
            [
                new LineaDeOrdenSolicitada(ctx.IdArticulo, "Linea A", 3m, 100m),
                new LineaDeOrdenSolicitada(ctx.IdArticulo, "Linea B", 4m, 100m)
            ]);

        // Recepción partida: 2 luego 5 (completa exactamente 7), costo real 112 (⇒ desvío +12%).
        var confirmadaPrimera = await RecibirYConfirmarAsync(ctx, objetivo.Id, cantidad: 2m, costoUnitario: 112m);
        var confirmadaFinal = await RecibirYConfirmarAsync(ctx, objetivo.Id, cantidad: 5m, costoUnitario: 112m);
        Assert.Equal(EstadoCompra.Confirmada, confirmadaFinal.Estado);

        // Recibido-no-pedido: articulo2, jamás en items_orden_compra de esta orden.
        var reciboExtra = await RecibirYConfirmarAsync(ctx, objetivo.Id, cantidad: 1m, costoUnitario: 50m, idArticulo: ctx.IdArticulo2);

        // Un comprobante ligado todavía en borrador — NO debe contar en la cobertura (solo
        // confirmados cuentan) pero SÍ debe aparecer en ComprobantesLigados (cualquier estado).
        var comprobanteBorrador = await CrearBorradorDeCompraAsync(
            ctx,
            new SolicitudDeCompra(
                ctx.IdProveedor, ctx.IdTipoCFB, ctx.IdPuntoVenta, $"0001-{Guid.NewGuid():N}"[..8],
                DateOnly.FromDateTime(DateTime.UtcNow), null,
                [new LineaDeCompraSolicitada(ctx.IdArticulo, "No confirmado", 9m, null, null, 999m, 0m, ctx.IdAlicuotaIva21, false)],
                objetivo.Id));

        // Una recepción soft-deleted del artículo 1 — excluida de la cobertura (defense-in-depth,
        // mismo criterio que EscriturasDeOrdenDeCompra.DerivarAsync).
        var recepcionSoftDeleted = await RecibirYConfirmarAsync(ctx, objetivo.Id, cantidad: 999m, costoUnitario: 1m);
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var itemsDeEsaCompra = await db.ItemsComprobanteCompra
                .Where(i => i.IdComprobanteCompra == recepcionSoftDeleted.Id)
                .ToListAsync();
            foreach (var item in itemsDeEsaCompra)
            {
                item.DeletedAt = DateTimeOffset.UtcNow;
            }
            await db.SaveChangesAsync();
        }

        var detalle = await ObtenerDetalleAsync(ctx, objetivo.Id);

        var coberturaArticulo1 = Assert.Single(detalle.Cobertura, c => c.IdArticulo == ctx.IdArticulo);
        Assert.Equal(7m, coberturaArticulo1.Pedida);
        Assert.Equal(7m, coberturaArticulo1.Recibida); // 2 + 5, jamás 999 (soft-deleted) ni 100 (otra orden)
        Assert.Equal(0m, coberturaArticulo1.Pendiente);
        Assert.Equal(100m, coberturaArticulo1.CostoEstimado);
        Assert.Equal(112m, coberturaArticulo1.CostoReal);
        Assert.Equal(12.00m, coberturaArticulo1.Desvio);

        var coberturaArticulo2 = Assert.Single(detalle.Cobertura, c => c.IdArticulo == ctx.IdArticulo2);
        Assert.Equal(0m, coberturaArticulo2.Pedida); // recibido-no-pedido
        Assert.Equal(1m, coberturaArticulo2.Recibida);
        Assert.Equal(0m, coberturaArticulo2.Pendiente); // Math.Max(0 - 1, 0), nunca negativo
        Assert.Null(coberturaArticulo2.CostoEstimado); // nunca pedido ⇒ nunca cotizado
        Assert.NotNull(coberturaArticulo2.CostoReal);
        Assert.Null(coberturaArticulo2.Desvio); // no comparable — un lado ausente, nunca 0

        // CRITICAL 2 (judgment-day, juez B): TotalEstimado/TotalReal/DesvioTotal del detalle,
        // calculados a mano desde el fixture de arriba. Solo articulo1 tiene CostoEstimado (100,
        // pedida 7) ⇒ TotalEstimado = 100*7 = 700. TotalReal suma AMBOS artículos con CostoReal
        // (articulo1: 112*7 = 784; articulo2: 50*1 = 50) ⇒ TotalReal = 834. DesvioTotal =
        // (834-700)/700*100 = 19.14. Los tres valores son pairwise-distintos entre sí.
        Assert.Equal(700m, detalle.TotalEstimado);
        Assert.Equal(834m, detalle.TotalReal);
        Assert.Equal(19.14m, detalle.DesvioTotal);

        // ComprobantesLigados: CONJUNTO EXACTO (judgment-day, juez A, ronda 2 — WARNING) — los
        // CINCO comprobantes ligados a esta orden, incluido el borrador (todo estado cuenta), nunca
        // solo un Count.Count >= N que un extra silencioso pasaría igual.
        Assert.Equal(
            new[]
            {
                confirmadaPrimera.Id, confirmadaFinal.Id, reciboExtra.Id, comprobanteBorrador.Id,
                recepcionSoftDeleted.Id
            }.OrderBy(x => x),
            detalle.ComprobantesLigados.OrderBy(x => x));

        // ---- fidelidad de proyección (task 5.9): recomputar Proyectar desde ESTA lectura ----------
        var completa = detalle.Cobertura.All(c => c.Pendiente <= 0m);
        var algoRecibido = detalle.Cobertura.Any(c => c.Recibida > 0m);
        var recomputado = ProyectorDeEstadoDeOrden.Proyectar(
            EstadoOrdenCompra.Enviada, cierreManual: false, completa: completa, algoRecibido: algoRecibido);

        var persistida = await LeerOrdenAsync(ctx, objetivo.Id);
        Assert.Equal(persistida.Estado, recomputado);
        Assert.Equal(persistida.Estado, detalle.Estado);

        // Hermana intacta.
        var detalleHermana = await ObtenerDetalleAsync(ctx, hermana.Id);
        Assert.Equal(EstadoOrdenCompra.Enviada, detalleHermana.Estado);
        Assert.Equal(3m, Assert.Single(detalleHermana.Cobertura).Pedida);
    }

    // ====================================================================================================
    // CRITICAL 1 (judgment-day, juez B, regla 12b): Pendiente solo se asserteaba en 0 (7-7 y
    // Math.Max(0-1,0)) — un `var pendiente = 0m;` fijo en producción sobrevivía. Fixture dedicado
    // con Pendiente POSITIVO discriminante.
    // ====================================================================================================

    [Fact]
    public async Task CoberturaPendienteEsPositivaCuandoLaRecepcionNoCompletaLoPedido()
    {
        var ctx = await PrepararAsync(nameof(CoberturaPendienteEsPositivaCuandoLaRecepcionNoCompletaLoPedido));

        var orden = await CrearYEnviarOrdenAsync(ctx, [new LineaDeOrdenSolicitada(ctx.IdArticulo, "L", 7m, 10m)]);
        await RecibirYConfirmarAsync(ctx, orden.Id, cantidad: 5m, costoUnitario: 10m);

        var detalle = await ObtenerDetalleAsync(ctx, orden.Id);
        var cobertura = Assert.Single(detalle.Cobertura);
        Assert.Equal(7m, cobertura.Pedida);
        Assert.Equal(5m, cobertura.Recibida);
        Assert.Equal(2m, cobertura.Pendiente); // Math.Max(7 - 5, 0) = 2, nunca 0 fijo
    }

    // ====================================================================================================
    // task 5.10 — un aumento de precio se muestra, jamás bloquea
    // ====================================================================================================

    [Fact]
    public async Task UnAumentoDePrecioEntreOrdenYFacturaSeSurfaceaNoSeBloquea()
    {
        var ctx = await PrepararAsync(nameof(UnAumentoDePrecioEntreOrdenYFacturaSeSurfaceaNoSeBloquea));

        var orden = await CrearYEnviarOrdenAsync(ctx, [new LineaDeOrdenSolicitada(ctx.IdArticulo, "L", 1m, 100m)]);
        var confirmada = await RecibirYConfirmarAsync(ctx, orden.Id, cantidad: 1m, costoUnitario: 112m);
        Assert.Equal(EstadoCompra.Confirmada, confirmada.Estado); // nunca bloqueada por el desvío

        var detalle = await ObtenerDetalleAsync(ctx, orden.Id);
        var cobertura = Assert.Single(detalle.Cobertura);
        Assert.Equal(100m, cobertura.CostoEstimado);
        Assert.Equal(112m, cobertura.CostoReal);
        Assert.Equal(12.00m, cobertura.Desvio);
    }

    // ====================================================================================================
    // task 5.11/5.17 — una línea nunca cotizada reporta "no comparable", jamás 0; mutation target #34b parte 3
    // ====================================================================================================

    [Fact]
    public async Task UnaLineaNuncaCotizadaReportaNoComparableNuncaCero()
    {
        var ctx = await PrepararAsync(nameof(UnaLineaNuncaCotizadaReportaNoComparableNuncaCero));

        var orden = await CrearYEnviarOrdenAsync(ctx, [new LineaDeOrdenSolicitada(ctx.IdArticulo, "L", 1m, null)]);
        await RecibirYConfirmarAsync(ctx, orden.Id, cantidad: 1m, costoUnitario: 55m);

        var detalle = await ObtenerDetalleAsync(ctx, orden.Id);
        var cobertura = Assert.Single(detalle.Cobertura);
        Assert.Null(cobertura.CostoEstimado);
        Assert.NotNull(cobertura.CostoReal);
        Assert.Null(cobertura.Desvio); // NUNCA 0m
        Assert.Null(detalle.TotalEstimado); // ningún artículo comparable del lado estimado
    }

    // ====================================================================================================
    // CRITICAL 1 (judgment-day, juez A, ronda 2): TotalEstimado fabricaba costo — agregaba desde
    // Cobertura (promedio ponderado por-artículo, que promedia SOLO las líneas cotizadas) ×
    // Pedida TOTAL del artículo (incluidas las líneas SIN cotizar), extrapolando en silencio.
    // Fixture dedicado: mismo artículo, una línea cotizada + una sin cotizar.
    // ====================================================================================================

    [Fact]
    public async Task TotalEstimadoSumaSoloLasLineasCotizadasSinExtrapolarAlPromedioDelArticulo()
    {
        var ctx = await PrepararAsync(nameof(TotalEstimadoSumaSoloLasLineasCotizadasSinExtrapolarAlPromedioDelArticulo));

        // Mismo artículo, dos líneas: 3 unidades cotizadas a 100 + 4 unidades SIN cotizar (null).
        // Cobertura.CostoEstimado (promedio ponderado, SOLO sobre la línea cotizada) = 100 — sigue
        // siendo un display por-artículo correcto. Pedida total del artículo = 7. El bug agregaba
        // TotalEstimado = 100 * 7 = 700 (extrapolando el costo a las 4 unidades nunca cotizadas);
        // el total honesto es la suma línea a línea SOLO de lo cotizado: 100 * 3 = 300.
        var orden = await CrearYEnviarOrdenAsync(
            ctx,
            [
                new LineaDeOrdenSolicitada(ctx.IdArticulo, "Cotizada", 3m, 100m),
                new LineaDeOrdenSolicitada(ctx.IdArticulo, "Sin cotizar", 4m, null)
            ]);

        var detalle = await ObtenerDetalleAsync(ctx, orden.Id);

        var cobertura = Assert.Single(detalle.Cobertura);
        Assert.Equal(7m, cobertura.Pedida);
        Assert.Equal(100m, cobertura.CostoEstimado); // el promedio por-artículo sigue siendo 100

        Assert.Equal(300m, detalle.TotalEstimado); // JAMÁS 700 (100*7) ni 233.33 (100*7/3)
    }

    // ====================================================================================================
    // task 5.13 — GET /api/reportes/stock/reposicion mantiene su shape y sus figuras (regresión stage 13)
    // ====================================================================================================

    [Fact]
    public async Task ReposicionMantieneSuShapeYSusFigurasSinCambios()
    {
        // Reloj pineado en el borde del día UTC: 01:30Z ya es "mañana" en UTC pero sigue siendo
        // 2026-08-19 22:30 en America/Argentina/Buenos_Aires (la zona sembrada del PV). El
        // contrato del endpoint resuelve "hoy" en la zona del PV, nunca en UTC (spec de la
        // etapa 13, vinculante) — assertar la fecha UTC del reloj de pared hacía fallar este
        // test todas las noches en la franja 21:00-00:00 locales.
        var instanteFijo = new DateTimeOffset(2026, 8, 20, 1, 30, 0, TimeSpan.Zero);

        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<IRelojDelSistema>(new RelojFijo(instanteFijo))));

        var ctx = await PrepararAsync(nameof(ReposicionMantieneSuShapeYSusFigurasSinCambios), factory);

        var respuesta = await ctx.Admin.GetAsync($"/api/reportes/stock/reposicion?idPuntoVenta={ctx.IdPuntoVenta}");
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        // Etapa 13 nunca vio una OC — el shape (IdPuntoVenta/Hoy/DiasDeRotacion/ZonaHoraria/Filas)
        // es el mismo antes y después de esta etapa: solo lectura, cero mutación de este endpoint
        // (ordenes-de-compra/spec.md: "Etapa 13 stays a read-only source").
        var reposicion = JsonSerializer.Deserialize<Reposicion>(cuerpo, OpcionesJson)!;
        Assert.Equal(ctx.IdPuntoVenta, reposicion.IdPuntoVenta);
        Assert.Equal("America/Argentina/Buenos_Aires", reposicion.ZonaHoraria);
        Assert.Equal(new DateOnly(2026, 8, 19), reposicion.Hoy);
        Assert.NotNull(reposicion.Filas);
    }

    // ====================================================================================================
    // task 5.14 — el límite del offset -03:00 (nunca Z) assertea filas Y período mostrado
    // ====================================================================================================

    [Fact]
    public async Task ListadoConOffsetMenosTresAsertaFilasYPeriodoMostrado()
    {
        var instanteFijo = new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
        var mismoInstanteConOffset = new DateTimeOffset(2026, 8, 19, 9, 0, 0, TimeSpan.FromHours(-3));
        Assert.Equal(instanteFijo, mismoInstanteConOffset);

        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<IRelojDelSistema>(new RelojFijo(instanteFijo))));

        var ctx = await PrepararAsync(nameof(ListadoConOffsetMenosTresAsertaFilasYPeriodoMostrado), factory);
        var orden = await CrearYEnviarOrdenAsync(ctx, [new LineaDeOrdenSolicitada(ctx.IdArticulo, "L", 1m, 10m)]);

        var desde = mismoInstanteConOffset.AddHours(-1);
        var hasta = mismoInstanteConOffset.AddHours(1);
        var pagina = await ListarAsync(
            ctx, $"?desde={Uri.EscapeDataString(desde.ToString("O"))}&hasta={Uri.EscapeDataString(hasta.ToString("O"))}");

        Assert.Contains(pagina.Items, i => i.Id == orden.Id);
        var fila = pagina.Items.First(i => i.Id == orden.Id);
        Assert.Equal(instanteFijo, fila.FechaEmision);
    }

    // ---- autorización — Vendedor lee, no escribe (matriz completa la cierra un test dedicado) --------

    [Fact]
    public async Task VendedorLeeAmbosEndpointsDeLectura()
    {
        var ctx = await PrepararAsync(nameof(VendedorLeeAmbosEndpointsDeLectura));
        var orden = await CrearYEnviarOrdenAsync(ctx, [new LineaDeOrdenSolicitada(ctx.IdArticulo, "L", 1m, 10m)]);

        var lista = await ctx.Vendedor.GetAsync("/api/ordenes-compra");
        Assert.Equal(HttpStatusCode.OK, lista.StatusCode);

        var detalle = await ctx.Vendedor.GetAsync($"/api/ordenes-compra/{orden.Id}");
        Assert.Equal(HttpStatusCode.OK, detalle.StatusCode);
    }

    // ---- cross-tenant — ADR-8: 404 uniforme ------------------------------------------------------------

    [Fact]
    public async Task UnaOrdenDeOtroTenantResponde404()
    {
        var ctxA = await PrepararAsync(nameof(UnaOrdenDeOtroTenantResponde404) + "A");
        var ctxB = await PrepararAsync(nameof(UnaOrdenDeOtroTenantResponde404) + "B");

        var ordenDeA = await CrearYEnviarOrdenAsync(ctxA, [new LineaDeOrdenSolicitada(ctxA.IdArticulo, "L", 1m, 10m)]);

        var respuesta = await ctxB.Admin.GetAsync($"/api/ordenes-compra/{ordenDeA.Id}");
        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    // ====================================================================================================
    // CRITICAL 3 (judgment-day, juez B, regla 12b): IdProveedor/IdPuntoVenta del detalle son dos
    // ints posicionales adyacentes en un record de 17 parámetros que jamás se leían de vuelta — el
    // SWAP de ambos en el constructor sobrevivía 197/197. Ya que la causa raíz es la misma (ningún
    // campo posicional del detalle/listado leído de vuelta), un solo test integral asserta CADA
    // campo posicional hoy sin cobertura (Numero, FechaEnvio, FechaCierre, CierreManual,
    // Observaciones, FechaEsperada, y el Estado de la fila del listado) con valores todos
    // distintos entre sí.
    // ====================================================================================================

    [Fact]
    public async Task DetalleDevuelveCadaCampoPosicionalConSuVerdad()
    {
        var ctx = await PrepararAsync(nameof(DetalleDevuelveCadaCampoPosicionalConSuVerdad));

        // Precondición (regla 11): IdProveedor2/IdPuntoVenta2 nacen en tablas distintas — si algún
        // día colisionaran numéricamente el swap quedaría indetectable. Falla acá primero, ruidosamente,
        // en vez de dar un verde falso más abajo.
        Assert.NotEqual(ctx.IdProveedor2, ctx.IdPuntoVenta2);

        var fechaEsperada = new DateOnly(2027, 3, 15);
        const string observaciones = "observacion-distintiva-critical-3";

        var solicitud = new SolicitudDeOrdenDeCompra(
            ctx.IdProveedor2, ctx.IdPuntoVenta2, fechaEsperada, observaciones,
            [new LineaDeOrdenSolicitada(ctx.IdArticulo, "Linea unica", 6m, 60m)]);
        var creada = await CrearBorradorDeOrdenAsync(ctx, solicitud);
        var enviada = await EnviarOrdenAsync(ctx, creada.Id);
        var cerrada = await CerrarOrdenAsync(ctx, creada.Id);

        var detalle = await ObtenerDetalleAsync(ctx, creada.Id);

        Assert.Equal(creada.Id, detalle.Id);
        Assert.Equal(ctx.IdProveedor2, detalle.IdProveedor);
        Assert.Equal(ctx.IdPuntoVenta2, detalle.IdPuntoVenta);
        Assert.NotNull(detalle.Numero);
        Assert.Equal(enviada.Numero, detalle.Numero);
        Assert.NotNull(detalle.FechaEnvio);
        Assert.Equal(enviada.FechaEnvio, detalle.FechaEnvio);
        Assert.Equal(fechaEsperada, detalle.FechaEsperada);
        Assert.NotNull(detalle.FechaCierre);
        Assert.Equal(cerrada.FechaCierre, detalle.FechaCierre);
        Assert.True(detalle.CierreManual);
        Assert.Equal(observaciones, detalle.Observaciones);
        Assert.Equal(EstadoOrdenCompra.Cerrada, detalle.Estado);

        // Estado de la fila del listado — nunca asserteado hasta ahora.
        var pagina = await ListarAsync(ctx, $"?idProveedor={ctx.IdProveedor2}");
        var filaListado = Assert.Single(pagina.Items, i => i.Id == creada.Id);
        Assert.Equal(EstadoOrdenCompra.Cerrada, filaListado.Estado);

        // CRITICAL 2 (judgment-day, juez A, ronda 2): IdProveedor/IdPuntoVenta de la fila del
        // listado tampoco se leían de vuelta — mismo hueco de causa raíz que el swap del detalle,
        // pero en el Select de ListarAsync. Ids desincronizados (IdProveedor2/IdPuntoVenta2)
        // discriminan un swap en ese Select.
        Assert.Equal(ctx.IdProveedor2, filaListado.IdProveedor);
        Assert.Equal(ctx.IdPuntoVenta2, filaListado.IdPuntoVenta);
    }

    // ---- gate guard (task 5.18) --------------------------------------------------------------------

    /// <summary>Gate guard (task 5.18, decisión 2 de tasks.md): esta slice no agrega DDL — el
    /// modelo EF sigue coincidiendo exactamente con la migración de slice 1.</summary>
    [Fact]
    public async Task NoHayCambiosPendientesDeModeloRespectoDeLaMigracionDeLaSlice1()
    {
        using var _ = fixture.CreateClient();
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var hayPendientes = db.Database.HasPendingModelChanges();
        Assert.False(hayPendientes);
    }
}
