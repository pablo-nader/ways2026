using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Articulos;
using Ways.Application.Compras;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Compras;
using Ways.Domain.Organizacion;
using Ways.Domain.Proveedores;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// Código de proveedor de la línea de compra: se guarda en el borrador (con o sin artículo), sobrevive al
/// replace-set y, al confirmar, las líneas con artículo lo asocian en <c>codigos_proveedor</c> sin que un código
/// ya ocupado por otro artículo frene la confirmación. Todo a través de la API real y la base real.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ComprasCodigoProveedorEnLineaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdPuntoVenta, HttpClient Admin, int IdProveedor, int IdOtroProveedor, int IdArticulo1,
        int IdArticulo2, int IdAlicuotaIva21, int IdTipoCFB);

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

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Codigo-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva21 = await db.AlicuotasIva.Where(a => a.Nombre == "21%").Select(a => a.Id).FirstAsync();

        var condicionFiscal = new CondicionFiscal { Codigo = $"{nombre}-CF", Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.CondicionesFiscales.Add(condicionFiscal);
        await db.SaveChangesAsync();

        Proveedor NuevoProveedor(string razon) => new()
        {
            IdTenant = resultado.IdTenant, RazonSocial = razon, IdCondicionFiscal = condicionFiscal.Id,
            Margen = 50m, CreatedAt = ahora, UpdatedAt = ahora
        };

        var proveedor = NuevoProveedor(nombre);
        var otroProveedor = NuevoProveedor(nombre + "-otro");
        db.Proveedores.AddRange(proveedor, otroProveedor);
        await db.SaveChangesAsync();

        Articulo NuevoArticulo(string sufijo) => new()
        {
            IdTenant = resultado.IdTenant, CodigoInterno = $"{nombre}-{sufijo}-{Guid.NewGuid():N}", Nombre = $"Articulo {sufijo}",
            IdArea = area.Id, IdAlicuotaIva = idAlicuotaIva21, UnidadVenta = UnidadVenta.Unidad, EsProducto = true,
            IdProveedorHabitual = proveedor.Id, CreatedAt = ahora, UpdatedAt = ahora
        };

        var articulo1 = NuevoArticulo("1");
        var articulo2 = NuevoArticulo("2");
        db.Articulos.AddRange(articulo1, articulo2);
        await db.SaveChangesAsync();

        var idTipoCFB = await db.TiposComprobante.Where(t => t.Codigo == "C-FB").Select(t => t.Id).SingleAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdPuntoVenta, admin, proveedor.Id, otroProveedor.Id, articulo1.Id, articulo2.Id,
            idAlicuotaIva21, idTipoCFB);
    }

    private static LineaDeCompraSolicitada DeArticulo(Contexto ctx, int idArticulo, string? codigo) =>
        new(idArticulo, "Artículo", 10m, null, null, 100m, 0m, ctx.IdAlicuotaIva21, CodigoProveedor: codigo);

    private static LineaDeCompraSolicitada Concepto(Contexto ctx, string? codigo) =>
        new(null, "Flete", 1m, null, null, 500m, 0m, ctx.IdAlicuotaIva21, CodigoProveedor: codigo);

    private static SolicitudDeCompra Solicitud(
        Contexto ctx, IReadOnlyList<LineaDeCompraSolicitada> items, int? idProveedor = null) =>
        new(idProveedor ?? ctx.IdProveedor, ctx.IdTipoCFB, ctx.IdPuntoVenta, DatosDePrueba.NumeroExternoUnico(),
            FechaDelNegocio.Hoy(), null, items);

    private static async Task<CompraDetalle> CrearBorradorAsync(Contexto ctx, SolicitudDeCompra solicitud)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/compras", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<CompraDetalle>(cuerpo, OpcionesJson)!;
    }

    private static async Task<CompraDetalle> ActualizarAsync(Contexto ctx, int id, SolicitudDeCompra solicitud)
    {
        var respuesta = await ctx.Admin.PutAsJsonAsync($"/api/compras/{id}", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<CompraDetalle>(cuerpo, OpcionesJson)!;
    }

    private static Task<HttpResponseMessage> ConfirmarCrudoAsync(Contexto ctx, int id) =>
        ctx.Admin.PostAsync($"/api/compras/{id}/confirmar", null);

    private static async Task<CompraDetalle> ConfirmarAsync(Contexto ctx, int id)
    {
        var respuesta = await ConfirmarCrudoAsync(ctx, id);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<CompraDetalle>(cuerpo, OpcionesJson)!;
    }

    private static async Task<CompraDetalle> ObtenerAsync(Contexto ctx, int id) =>
        (await ctx.Admin.GetFromJsonAsync<CompraDetalle>($"/api/compras/{id}", OpcionesJson))!;

    private static async Task AssertRechazoAsync(HttpResponseMessage respuesta, string codigoEsperado)
    {
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.BadRequest, cuerpo);
        Assert.Equal(codigoEsperado, JsonDocument.Parse(cuerpo).RootElement.GetProperty("codigo").GetString());
    }

    private WaysDbContext Db(Contexto ctx) =>
        fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));

    private async Task<List<CodigoProveedor>> AsociacionesAsync(Contexto ctx)
    {
        await using var db = Db(ctx);
        return await db.CodigosProveedor.AsNoTracking().OrderBy(c => c.Id).ToListAsync();
    }

    private static async Task AsociarPreviamenteAsync(Contexto ctx, int idArticulo, int idProveedor, string codigo)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync(
            $"/api/articulos/{idArticulo}/codigos-proveedor", new AltaCodigoProveedor(idProveedor, codigo));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
    }

    // ---- borrador: guardar, leer, reemplazar -----------------------------------------------------------------

    [Fact]
    public async Task ElCodigoDeUnaLineaDeArticuloYDeUnConceptoViajaEnElBorradorYSeNormaliza()
    {
        var ctx = await PrepararAsync(nameof(ElCodigoDeUnaLineaDeArticuloYDeUnConceptoViajaEnElBorradorYSeNormaliza));
        using var _ = ctx.Admin;

        var creada = await CrearBorradorAsync(ctx, Solicitud(ctx,
        [
            DeArticulo(ctx, ctx.IdArticulo1, "  ab-12 "),
            Concepto(ctx, "FLETE-9"),
            DeArticulo(ctx, ctx.IdArticulo2, "   "),
            DeArticulo(ctx, ctx.IdArticulo2, null)
        ]));

        Assert.Equal(["ab-12", "FLETE-9", null, null], creada.Items.OrderBy(i => i.Orden).Select(i => i.CodigoProveedor));

        var releida = await ObtenerAsync(ctx, creada.Id);
        Assert.Equal(["ab-12", "FLETE-9", null, null], releida.Items.OrderBy(i => i.Orden).Select(i => i.CodigoProveedor));

        await using var db = Db(ctx);
        var guardado = await db.ItemsComprobanteCompra.AsNoTracking()
            .Where(i => i.IdComprobanteCompra == creada.Id).OrderBy(i => i.Orden).Select(i => i.CodigoProveedor).ToListAsync();
        Assert.Equal(["ab-12", "FLETE-9", null, null], guardado);
    }

    [Fact]
    public async Task ElReemplazoDelBorradorConservaCambiaOLimpiaElCodigoSegunLoQueLlega()
    {
        var ctx = await PrepararAsync(nameof(ElReemplazoDelBorradorConservaCambiaOLimpiaElCodigoSegunLoQueLlega));
        using var _ = ctx.Admin;
        var creada = await CrearBorradorAsync(ctx, Solicitud(ctx,
        [
            DeArticulo(ctx, ctx.IdArticulo1, "UNO"),
            DeArticulo(ctx, ctx.IdArticulo2, "DOS"),
            Concepto(ctx, "TRES")
        ]));

        // Reenvío del mismo contenido tal como lo devolvió el GET: nada se pierde.
        var reenviada = await ActualizarAsync(ctx, creada.Id, Solicitud(ctx,
            creada.Items.OrderBy(i => i.Orden)
                .Select(i => i.IdArticulo is { } id ? DeArticulo(ctx, id, i.CodigoProveedor) : Concepto(ctx, i.CodigoProveedor))
                .ToList()));
        Assert.Equal(["UNO", "DOS", "TRES"], reenviada.Items.OrderBy(i => i.Orden).Select(i => i.CodigoProveedor));

        // Cambia una, limpia otra (null) y la tercera llega sin el campo: queda sin código.
        var cambiada = await ActualizarAsync(ctx, creada.Id, Solicitud(ctx,
        [
            DeArticulo(ctx, ctx.IdArticulo1, "UNO-B"),
            DeArticulo(ctx, ctx.IdArticulo2, null),
            new LineaDeCompraSolicitada(null, "Flete", 1m, null, null, 500m, 0m, ctx.IdAlicuotaIva21)
        ]));
        Assert.Equal(["UNO-B", null, null], cambiada.Items.OrderBy(i => i.Orden).Select(i => i.CodigoProveedor));
        Assert.Equal(
            ["UNO-B", null, null], (await ObtenerAsync(ctx, creada.Id)).Items.OrderBy(i => i.Orden).Select(i => i.CodigoProveedor));
    }

    [Fact]
    public async Task UnCodigoDeExactamenteCincuentaCaracteresSeAcepta()
    {
        var ctx = await PrepararAsync(nameof(UnCodigoDeExactamenteCincuentaCaracteresSeAcepta));
        using var _ = ctx.Admin;
        var codigo = new string('K', ReglaDeCodigoProveedor.LongitudMaxima);

        var creada = await CrearBorradorAsync(ctx, Solicitud(ctx, [DeArticulo(ctx, ctx.IdArticulo1, codigo)]));

        Assert.Equal(codigo, Assert.Single(creada.Items).CodigoProveedor);
    }

    [Fact]
    public async Task UnCodigoDeMasDeCincuentaCaracteresEsUn400AlCrearYNoDejaUnEncabezadoHuerfano()
    {
        var ctx = await PrepararAsync(nameof(UnCodigoDeMasDeCincuentaCaracteresEsUn400AlCrearYNoDejaUnEncabezadoHuerfano));
        using var _ = ctx.Admin;
        var largo = new string('K', ReglaDeCodigoProveedor.LongitudMaxima + 1);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/compras", Solicitud(ctx, [DeArticulo(ctx, ctx.IdArticulo1, "BIEN"), Concepto(ctx, largo)]));

        await AssertRechazoAsync(respuesta, "codigo_proveedor_muy_largo");
        await using var db = Db(ctx);
        Assert.Equal(0, await db.ComprobantesCompra.CountAsync());
    }

    [Fact]
    public async Task UnCodigoDeMasDeCincuentaCaracteresEsUn400AlActualizarYDejaElBorradorIntacto()
    {
        var ctx = await PrepararAsync(nameof(UnCodigoDeMasDeCincuentaCaracteresEsUn400AlActualizarYDejaElBorradorIntacto));
        using var _ = ctx.Admin;
        var creada = await CrearBorradorAsync(ctx, Solicitud(ctx, [DeArticulo(ctx, ctx.IdArticulo1, "ORIGINAL")]));
        var largo = new string('K', ReglaDeCodigoProveedor.LongitudMaxima + 1);

        var respuesta = await ctx.Admin.PutAsJsonAsync(
            $"/api/compras/{creada.Id}", Solicitud(ctx, [DeArticulo(ctx, ctx.IdArticulo1, largo)]));

        await AssertRechazoAsync(respuesta, "codigo_proveedor_muy_largo");
        Assert.Equal("ORIGINAL", Assert.Single((await ObtenerAsync(ctx, creada.Id)).Items).CodigoProveedor);
    }

    /// <summary>Cláusula bajo prueba: el guardado del borrador (alta y reemplazo) no escribe
    /// <c>codigos_proveedor</c>; la asociación es solo de la confirmación.</summary>
    [Fact]
    public async Task GuardarUnBorradorNuncaAsociaElCodigo()
    {
        var ctx = await PrepararAsync(nameof(GuardarUnBorradorNuncaAsociaElCodigo));
        using var _ = ctx.Admin;

        var creada = await CrearBorradorAsync(ctx, Solicitud(ctx, [DeArticulo(ctx, ctx.IdArticulo1, "BORR-1")]));
        Assert.Empty(await AsociacionesAsync(ctx));

        await ActualizarAsync(ctx, creada.Id, Solicitud(ctx, [DeArticulo(ctx, ctx.IdArticulo1, "BORR-2")]));
        Assert.Empty(await AsociacionesAsync(ctx));
    }

    // ---- confirmar: asociación ------------------------------------------------------------------------

    [Fact]
    public async Task ConfirmarAsociaElCodigoDeLaLineaConArticuloAlArticuloYAlProveedorDeLaCompra()
    {
        var ctx = await PrepararAsync(nameof(ConfirmarAsociaElCodigoDeLaLineaConArticuloAlArticuloYAlProveedorDeLaCompra));
        using var _ = ctx.Admin;
        var creada = await CrearBorradorAsync(ctx, Solicitud(ctx, [DeArticulo(ctx, ctx.IdArticulo1, " Ab-1 ")]));

        var confirmada = await ConfirmarAsync(ctx, creada.Id);

        var asociacion = Assert.Single(await AsociacionesAsync(ctx));
        Assert.Equal(ctx.IdArticulo1, asociacion.IdArticulo);
        Assert.Equal(ctx.IdProveedor, asociacion.IdProveedor);
        Assert.Equal("Ab-1", asociacion.Codigo);
        Assert.Equal(ctx.IdTenant, asociacion.IdTenant);
        Assert.Equal("Ab-1", Assert.Single(confirmada.Items).CodigoProveedor);
    }

    /// <summary>Cláusula bajo prueba: solo las líneas con artículo asocian. El código del concepto
    /// queda en la línea y no aparece en <c>codigos_proveedor</c>.</summary>
    [Fact]
    public async Task UnConceptoConCodigoLoConservaPeroNuncaAsocia()
    {
        var ctx = await PrepararAsync(nameof(UnConceptoConCodigoLoConservaPeroNuncaAsocia));
        using var _ = ctx.Admin;
        var creada = await CrearBorradorAsync(ctx, Solicitud(ctx,
            [Concepto(ctx, "SOLO-LINEA"), DeArticulo(ctx, ctx.IdArticulo1, "CON-ART")]));

        var confirmada = await ConfirmarAsync(ctx, creada.Id);

        Assert.Equal("SOLO-LINEA", confirmada.Items.Single(i => i.IdArticulo is null).CodigoProveedor);
        Assert.Equal(["CON-ART"], (await AsociacionesAsync(ctx)).Select(a => a.Codigo));
    }

    [Fact]
    public async Task ConfirmarOtraCompraConElMismoCodigoYArticuloNoDuplicaLaAsociacion()
    {
        var ctx = await PrepararAsync(nameof(ConfirmarOtraCompraConElMismoCodigoYArticuloNoDuplicaLaAsociacion));
        using var _ = ctx.Admin;
        await ConfirmarAsync(ctx, (await CrearBorradorAsync(ctx, Solicitud(ctx, [DeArticulo(ctx, ctx.IdArticulo1, "REP-1")]))).Id);
        var primera = Assert.Single(await AsociacionesAsync(ctx));

        await ConfirmarAsync(ctx, (await CrearBorradorAsync(ctx, Solicitud(ctx, [DeArticulo(ctx, ctx.IdArticulo1, "rep-1")]))).Id);

        var despues = Assert.Single(await AsociacionesAsync(ctx));
        Assert.Equal(primera.Id, despues.Id);
        Assert.Equal(primera.UpdatedAt, despues.UpdatedAt);
    }

    /// <summary>Cláusula bajo prueba: un código ya asignado a OTRO artículo del proveedor no frena la
    /// confirmación y no se reasigna; queda solo en la línea.</summary>
    [Fact]
    public async Task UnCodigoDeOtroArticuloDelProveedorConfirmaIgualYNoSeReasigna()
    {
        var ctx = await PrepararAsync(nameof(UnCodigoDeOtroArticuloDelProveedorConfirmaIgualYNoSeReasigna));
        using var _ = ctx.Admin;
        await AsociarPreviamenteAsync(ctx, ctx.IdArticulo2, ctx.IdProveedor, "OCUPADO");
        var creada = await CrearBorradorAsync(ctx, Solicitud(ctx,
            [DeArticulo(ctx, ctx.IdArticulo1, "ocupado"), DeArticulo(ctx, ctx.IdArticulo1, "LIBRE")]));

        var confirmada = await ConfirmarAsync(ctx, creada.Id);

        Assert.Equal(EstadoCompra.Confirmada, confirmada.Estado);
        Assert.Equal(["ocupado", "LIBRE"], confirmada.Items.OrderBy(i => i.Orden).Select(i => i.CodigoProveedor));
        var asociaciones = await AsociacionesAsync(ctx);
        Assert.Equal(2, asociaciones.Count);
        Assert.Equal(ctx.IdArticulo2, asociaciones.Single(a => a.Codigo == "OCUPADO").IdArticulo);
        Assert.Equal(ctx.IdArticulo1, asociaciones.Single(a => a.Codigo == "LIBRE").IdArticulo);
    }

    /// <summary>Cláusula bajo prueba: con el mismo código nuevo en dos artículos de una misma compra
    /// gana la línea de menor orden; la otra confirma igual y queda sin asociar.</summary>
    [Fact]
    public async Task ConElMismoCodigoNuevoEnDosArticulosGanaLaPrimeraLineaYLaOtraQuedaSinAsociar()
    {
        var ctx = await PrepararAsync(nameof(ConElMismoCodigoNuevoEnDosArticulosGanaLaPrimeraLineaYLaOtraQuedaSinAsociar));
        using var _ = ctx.Admin;
        var creada = await CrearBorradorAsync(ctx, Solicitud(ctx,
            [DeArticulo(ctx, ctx.IdArticulo2, "DUP-1"), DeArticulo(ctx, ctx.IdArticulo1, "DUP-1")]));

        var confirmada = await ConfirmarAsync(ctx, creada.Id);

        Assert.Equal(["DUP-1", "DUP-1"], confirmada.Items.OrderBy(i => i.Orden).Select(i => i.CodigoProveedor));
        var asociacion = Assert.Single(await AsociacionesAsync(ctx));
        Assert.Equal(ctx.IdArticulo2, asociacion.IdArticulo);
    }

    [Fact]
    public async Task ElMismoCodigoEnOtroProveedorEsIndependienteYSeAsociaAEseProveedor()
    {
        var ctx = await PrepararAsync(nameof(ElMismoCodigoEnOtroProveedorEsIndependienteYSeAsociaAEseProveedor));
        using var _ = ctx.Admin;
        await AsociarPreviamenteAsync(ctx, ctx.IdArticulo2, ctx.IdProveedor, "COMUN");
        var creada = await CrearBorradorAsync(ctx,
            Solicitud(ctx, [DeArticulo(ctx, ctx.IdArticulo1, "COMUN")], ctx.IdOtroProveedor));

        await ConfirmarAsync(ctx, creada.Id);

        var asociaciones = await AsociacionesAsync(ctx);
        Assert.Equal(2, asociaciones.Count);
        var delOtro = asociaciones.Single(a => a.IdProveedor == ctx.IdOtroProveedor);
        Assert.Equal(ctx.IdArticulo1, delOtro.IdArticulo);
        Assert.Equal(ctx.IdArticulo2, asociaciones.Single(a => a.IdProveedor == ctx.IdProveedor).IdArticulo);
    }

    [Fact]
    public async Task UnaAsociacionDadaDeBajaNoImpideAsociarElCodigoDeNuevo()
    {
        var ctx = await PrepararAsync(nameof(UnaAsociacionDadaDeBajaNoImpideAsociarElCodigoDeNuevo));
        using var _ = ctx.Admin;
        await AsociarPreviamenteAsync(ctx, ctx.IdArticulo2, ctx.IdProveedor, "BAJA-1");
        await using (var db = Db(ctx))
        {
            Assert.Equal(1, await db.CodigosProveedor.ExecuteUpdateAsync(
                s => s.SetProperty(c => c.DeletedAt, DateTimeOffset.UtcNow)));
        }

        var creada = await CrearBorradorAsync(ctx, Solicitud(ctx, [DeArticulo(ctx, ctx.IdArticulo1, "BAJA-1")]));
        await ConfirmarAsync(ctx, creada.Id);

        await using var verificacion = Db(ctx);
        var vivas = await verificacion.CodigosProveedor.AsNoTracking().IgnoreQueryFilters().ToListAsync();
        Assert.Equal(2, vivas.Count);
        Assert.Equal(ctx.IdArticulo1, vivas.Single(c => c.DeletedAt == null).IdArticulo);
    }

    /// <summary>Cláusula bajo prueba: el artículo se exige vivo en el mismo statement de la asociación.</summary>
    [Fact]
    public async Task UnArticuloDadoDeBajaDespuesDelBorradorNoRecibeLaAsociacionPeroLaCompraConfirma()
    {
        var ctx = await PrepararAsync(nameof(UnArticuloDadoDeBajaDespuesDelBorradorNoRecibeLaAsociacionPeroLaCompraConfirma));
        using var _ = ctx.Admin;
        var creada = await CrearBorradorAsync(ctx, Solicitud(ctx,
            [DeArticulo(ctx, ctx.IdArticulo1, "VIVO"), DeArticulo(ctx, ctx.IdArticulo2, "MUERTO")]));
        await using (var db = Db(ctx))
        {
            Assert.Equal(1, await db.Articulos.Where(a => a.Id == ctx.IdArticulo2)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.DeletedAt, DateTimeOffset.UtcNow)));
        }

        await ConfirmarAsync(ctx, creada.Id);

        Assert.Equal(["VIVO"], (await AsociacionesAsync(ctx)).Select(a => a.Codigo));
    }

    // ---- confirmar: tenant -------------------------------------------------------------------------------

    [Fact]
    public async Task LaAsociacionQuedaEnElTenantDeLaCompraYOtroTenantNoLaVeNiChocaConElMismoCodigo()
    {
        var a = await PrepararAsync(nameof(LaAsociacionQuedaEnElTenantDeLaCompraYOtroTenantNoLaVeNiChocaConElMismoCodigo) + "A");
        var b = await PrepararAsync(nameof(LaAsociacionQuedaEnElTenantDeLaCompraYOtroTenantNoLaVeNiChocaConElMismoCodigo) + "B");
        using var _a = a.Admin;
        using var _b = b.Admin;

        await ConfirmarAsync(a, (await CrearBorradorAsync(a, Solicitud(a, [DeArticulo(a, a.IdArticulo1, "MISMO")]))).Id);
        await ConfirmarAsync(b, (await CrearBorradorAsync(b, Solicitud(b, [DeArticulo(b, b.IdArticulo1, "MISMO")]))).Id);

        Assert.Equal(a.IdTenant, Assert.Single(await AsociacionesAsync(a)).IdTenant);
        Assert.Equal(b.IdTenant, Assert.Single(await AsociacionesAsync(b)).IdTenant);

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", b.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText = "SELECT count(*) FROM codigos_proveedor WHERE id_tenant = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = a.IdTenant });
        Assert.Equal(0L, await comando.ExecuteScalarAsync());
    }

    // ---- confirmar: carreras ---------------------------------------------------------------------------

    /// <summary>Carrera real: otra transacción tiene un INSERT del mismo código sin commitear. La confirmación
    /// llega al índice único, espera y, según cómo termine la otra, no asocia (commit) o asocia (rollback); en
    /// ningún caso falla ni queda abortada. Mata el <c>ON CONFLICT DO NOTHING</c>: sin él, el commit del otro
    /// produce 23505 y la confirmación se cae.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnaAsociacionConcurrenteDelMismoCodigoNoHaceFallarNiAbortaLaConfirmacion(bool comitea)
    {
        var ctx = await PrepararAsync(nameof(UnaAsociacionConcurrenteDelMismoCodigoNoHaceFallarNiAbortaLaConfirmacion) + comitea);
        using var _ = ctx.Admin;
        var creada = await CrearBorradorAsync(ctx, Solicitud(ctx,
            [DeArticulo(ctx, ctx.IdArticulo1, "CARRERA-1"), DeArticulo(ctx, ctx.IdArticulo1, "OTRO-LIBRE")]));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);
        await using var transaccion = await cruda.BeginTransactionAsync();
        await using (var insert = cruda.CreateCommand())
        {
            insert.CommandText =
                "INSERT INTO codigos_proveedor (id_tenant, id_articulo, id_proveedor, codigo, created_at, updated_at) " +
                "VALUES ($1, $2, $3, 'CARRERA-1', now(), now())";
            insert.Parameters.Add(new NpgsqlParameter { Value = ctx.IdTenant });
            insert.Parameters.Add(new NpgsqlParameter { Value = ctx.IdArticulo2 });
            insert.Parameters.Add(new NpgsqlParameter { Value = ctx.IdProveedor });
            await insert.ExecuteNonQueryAsync();
        }

        var confirmacion = ConfirmarCrudoAsync(ctx, creada.Id);
        await Task.Delay(1500);
        Assert.False(confirmacion.IsCompleted, "la confirmación debía quedar esperando el índice único");

        if (comitea)
        {
            await transaccion.CommitAsync();
        }
        else
        {
            await transaccion.RollbackAsync();
        }

        var respuesta = await confirmacion;
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, await respuesta.Content.ReadAsStringAsync());

        var asociaciones = await AsociacionesAsync(ctx);
        var carrera = asociaciones.Single(a => a.Codigo == "CARRERA-1");
        Assert.Equal(comitea ? ctx.IdArticulo2 : ctx.IdArticulo1, carrera.IdArticulo);
        Assert.Equal(ctx.IdArticulo1, asociaciones.Single(a => a.Codigo == "OTRO-LIBRE").IdArticulo);
        Assert.Equal(EstadoCompra.Confirmada, (await ObtenerAsync(ctx, creada.Id)).Estado);
    }

    [Fact]
    public async Task DosConfirmacionesSimultaneasDelMismoCodigoNuevoConfirmanAmbasYAsocianUnaSolaVez()
    {
        var ctx = await PrepararAsync(nameof(DosConfirmacionesSimultaneasDelMismoCodigoNuevoConfirmanAmbasYAsocianUnaSolaVez));
        using var _ = ctx.Admin;
        var uno = await CrearBorradorAsync(ctx, Solicitud(ctx, [DeArticulo(ctx, ctx.IdArticulo1, "SIMULTANEO")]));
        var dos = await CrearBorradorAsync(ctx, Solicitud(ctx, [DeArticulo(ctx, ctx.IdArticulo2, "SIMULTANEO")]));

        var respuestas = await Task.WhenAll(ConfirmarCrudoAsync(ctx, uno.Id), ConfirmarCrudoAsync(ctx, dos.Id));

        foreach (var respuesta in respuestas)
        {
            Assert.True(respuesta.StatusCode == HttpStatusCode.OK, await respuesta.Content.ReadAsStringAsync());
        }

        Assert.Single(await AsociacionesAsync(ctx));
        Assert.Equal(EstadoCompra.Confirmada, (await ObtenerAsync(ctx, uno.Id)).Estado);
        Assert.Equal(EstadoCompra.Confirmada, (await ObtenerAsync(ctx, dos.Id)).Estado);
    }
}
