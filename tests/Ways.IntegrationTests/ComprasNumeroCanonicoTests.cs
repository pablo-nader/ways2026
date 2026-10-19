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
/// Número de las facturas de compra (C-FA, C-FB, C-FC): se guarda como <c>PPPPP-NNNNNNNN</c> y el chequeo de
/// duplicado (<c>GET /api/compras/duplicados</c>) compara el valor numérico de cada segmento, así que también
/// encuentra filas viejas con otro formato. Las filas viejas se siembran directo en la base: las escrituras ya
/// normalizan y no pueden producirlas.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ComprasNumeroCanonicoTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordSecundario = "password-secundaria-larga";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdPuntoVenta, int IdAdmin, HttpClient Admin, HttpClient Vendedor,
        int IdProveedor, int IdOtroProveedor, int IdArticulo, int IdAlicuotaIva21, int IdTipoCFA, int IdTipoCFB, int IdTipoRemito);

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

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Numero-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva21 = await db.AlicuotasIva.Where(a => a.Nombre == "21%").Select(a => a.Id).FirstAsync();

        var condicionFiscal = new CondicionFiscal { Codigo = $"{nombre}-CF", Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.CondicionesFiscales.Add(condicionFiscal);
        await db.SaveChangesAsync();

        var proveedor = NuevoProveedor(resultado.IdTenant, nombre, condicionFiscal.Id, ahora);
        var otroProveedor = NuevoProveedor(resultado.IdTenant, $"{nombre}-otro", condicionFiscal.Id, ahora);
        db.Proveedores.AddRange(proveedor, otroProveedor);
        await db.SaveChangesAsync();

        var articulo = new Articulo
        {
            IdTenant = resultado.IdTenant, CodigoInterno = $"{nombre}-1-{Guid.NewGuid():N}", Nombre = "Articulo 1",
            IdArea = area.Id, IdAlicuotaIva = idAlicuotaIva21, UnidadVenta = UnidadVenta.Unidad, EsProducto = true,
            IdProveedorHabitual = proveedor.Id, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        async Task<int> TipoAsync(string codigo) =>
            await db.TiposComprobante.Where(t => t.Codigo == codigo).Select(t => t.Id).SingleAsync();

        var vendedor = await CrearVendedorAsync(db, resultado.IdTenant, nombre, ahora);

        return new Contexto(
            resultado.IdTenant, resultado.IdPuntoVenta, resultado.IdUsuarioAdmin, admin, vendedor,
            proveedor.Id, otroProveedor.Id, articulo.Id, idAlicuotaIva21,
            await TipoAsync("C-FA"), await TipoAsync("C-FB"), await TipoAsync("C-RM"));
    }

    private static Proveedor NuevoProveedor(int idTenant, string nombre, int idCondicionFiscal, DateTimeOffset ahora) =>
        new()
        {
            IdTenant = idTenant, RazonSocial = nombre, IdCondicionFiscal = idCondicionFiscal,
            Margen = 50m, CreatedAt = ahora, UpdatedAt = ahora
        };

    private async Task<HttpClient> CrearVendedorAsync(
        Ways.Infrastructure.Persistencia.WaysDbContext db, int idTenant, string nombre, DateTimeOffset ahora)
    {
        var hasheador = new HasheadorPbkdf2();
        var mail = $"{nombre.ToLowerInvariant()}-vendedor@ways.test";
        db.Usuarios.Add(new Usuario
        {
            IdTenant = idTenant, NombreUsuario = "vendedor", Mail = mail, RolId = (int)RolConocido.Vendedor,
            PasswordHash = hasheador.Hashear(PasswordSecundario), PasswordAlgoritmo = hasheador.Algoritmo,
            PasswordActualizadoEl = ahora, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, PasswordSecundario));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    private static SolicitudDeCompra Solicitud(Contexto ctx, string? numeroExterno, int? idTipo = null) =>
        new(
            ctx.IdProveedor, idTipo ?? ctx.IdTipoCFA, ctx.IdPuntoVenta, numeroExterno, DateOnly.FromDateTime(DateTime.UtcNow), null,
            [new LineaDeCompraSolicitada(ctx.IdArticulo, "Item 1", 10m, null, null, 100m, 0m, ctx.IdAlicuotaIva21, true)]);

    private static async Task<HttpResponseMessage> PostAsync(Contexto ctx, string? numeroExterno, int? idTipo = null) =>
        await ctx.Admin.PostAsJsonAsync("/api/compras", Solicitud(ctx, numeroExterno, idTipo));

    private static async Task<CompraDetalle> CrearAsync(Contexto ctx, string? numeroExterno, int? idTipo = null)
    {
        var respuesta = await PostAsync(ctx, numeroExterno, idTipo);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<CompraDetalle>(cuerpo, OpcionesJson)!;
    }

    private async Task<string?> NumeroEnBaseAsync(int idTenant, int idCompra)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        return await db.ComprobantesCompra.Where(c => c.Id == idCompra).Select(c => c.NumeroExterno).SingleAsync();
    }

    private async Task<int> SembrarAsync(
        Contexto ctx, string? numeroExterno, EstadoCompra estado = EstadoCompra.Borrador, int? idProveedor = null,
        int? idTipo = null, DateOnly? fechaComprobante = null, OrigenCompra origen = OrigenCompra.Manual)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var compra = new ComprobanteCompra
        {
            IdTenant = ctx.IdTenant,
            IdProveedor = idProveedor ?? ctx.IdProveedor,
            IdTipoComprobante = idTipo ?? ctx.IdTipoCFA,
            NumeroExterno = numeroExterno,
            FechaComprobante = fechaComprobante ?? DateOnly.FromDateTime(DateTime.UtcNow),
            FechaRecepcion = estado == EstadoCompra.Borrador ? null : ahora,
            IdPuntoVenta = ctx.IdPuntoVenta,
            IdEmpleado = ctx.IdAdmin,
            Estado = estado,
            Origen = origen,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.ComprobantesCompra.Add(compra);
        await db.SaveChangesAsync();
        return compra.Id;
    }

    private static string UrlDeDuplicados(int idProveedor, int idTipo, string? numero, int? excluirId = null) =>
        $"/api/compras/duplicados?idProveedor={idProveedor}&idTipoComprobante={idTipo}"
        + (numero is null ? string.Empty : $"&numeroExterno={Uri.EscapeDataString(numero)}")
        + (excluirId is null ? string.Empty : $"&excluirId={excluirId}");

    private static async Task<JsonElement> DuplicadosAsync(
        HttpClient cliente, int idProveedor, int idTipo, string? numero, int? excluirId = null)
    {
        var respuesta = await cliente.GetAsync(UrlDeDuplicados(idProveedor, idTipo, numero, excluirId));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<JsonElement>(cuerpo);
    }

    private static async Task<List<int>> IdsDuplicadosAsync(
        HttpClient cliente, int idProveedor, int idTipo, string? numero, int? excluirId = null) =>
        (await DuplicadosAsync(cliente, idProveedor, idTipo, numero, excluirId))
            .EnumerateArray().Select(d => d.GetProperty("id").GetInt32()).ToList();

    // ---- normalización al escribir ----------------------------------------------------------------------

    [Theory]
    [InlineData("3-12345")]
    [InlineData("0003-00012345")]
    [InlineData("00003-00012345")]
    [InlineData("  3 - 12345  ")]
    [InlineData("000300012345")]
    public async Task UnaFacturaGuardaSuNumeroEnFormaCanonicaAlCrear(string entrada)
    {
        var ctx = await PrepararAsync(nameof(UnaFacturaGuardaSuNumeroEnFormaCanonicaAlCrear) + Math.Abs(entrada.GetHashCode()));

        var creada = await CrearAsync(ctx, entrada);

        Assert.Equal("00003-00012345", creada.NumeroExterno);
        Assert.Equal("00003-00012345", await NumeroEnBaseAsync(ctx.IdTenant, creada.Id));
    }

    [Fact]
    public async Task UnaFacturaBTambienSeNormaliza()
    {
        var ctx = await PrepararAsync(nameof(UnaFacturaBTambienSeNormaliza));

        var facturaB = await CrearAsync(ctx, "7-9", ctx.IdTipoCFB);

        Assert.Equal("00007-00000009", facturaB.NumeroExterno);
    }

    [Fact]
    public async Task ElPutNormalizaElNumeroDelBorrador()
    {
        var ctx = await PrepararAsync(nameof(ElPutNormalizaElNumeroDelBorrador));
        var creada = await CrearAsync(ctx, "1-1");

        var respuesta = await ctx.Admin.PutAsJsonAsync($"/api/compras/{creada.Id}", Solicitud(ctx, "0003-00012345"));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        Assert.Equal("00003-00012345", JsonSerializer.Deserialize<CompraDetalle>(cuerpo, OpcionesJson)!.NumeroExterno);
        Assert.Equal("00003-00012345", await NumeroEnBaseAsync(ctx.IdTenant, creada.Id));
    }

    [Fact]
    public async Task DosFormatosDelMismoNumeroChocanContraElIndiceUnicoCuandoSeEscribenNormalizados()
    {
        var ctx = await PrepararAsync(nameof(DosFormatosDelMismoNumeroChocanContraElIndiceUnicoCuandoSeEscribenNormalizados));
        await CrearAsync(ctx, "3-12345");

        var respuesta = await PostAsync(ctx, "0003-00012345");

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("compra_duplicada", problema.GetProperty("codigo").GetString());
    }

    [Theory]
    [InlineData("FA-0003-1")]
    [InlineData("3-12-345")]
    [InlineData("0-1")]
    [InlineData("123456-1")]
    [InlineData("1-123456789")]
    [InlineData("12345")]
    public async Task UnNumeroFiscalIninterpretableEsUn400EstableYNoCreaLaCompra(string entrada)
    {
        var ctx = await PrepararAsync(nameof(UnNumeroFiscalIninterpretableEsUn400EstableYNoCreaLaCompra) + Math.Abs(entrada.GetHashCode()));

        var respuesta = await PostAsync(ctx, entrada);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("numero_comprobante_invalido", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Empty(await db.ComprobantesCompra.ToListAsync());
    }

    [Fact]
    public async Task ElPutConUnNumeroIninterpretableEsUn400YDejaElBorradorIntacto()
    {
        var ctx = await PrepararAsync(nameof(ElPutConUnNumeroIninterpretableEsUn400YDejaElBorradorIntacto));
        var creada = await CrearAsync(ctx, "3-12345");

        var respuesta = await ctx.Admin.PutAsJsonAsync($"/api/compras/{creada.Id}", Solicitud(ctx, "FA-1"));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("numero_comprobante_invalido", problema.GetProperty("codigo").GetString());
        Assert.Equal("00003-00012345", await NumeroEnBaseAsync(ctx.IdTenant, creada.Id));
    }

    /// <summary>update-404-first: la cláusula bajo prueba es el <c>AnyAsync</c> de existencia que corre antes de
    /// normalizar el número. Una compra que no existe en este tenant responde 404 aunque el número sea
    /// inválido; las compras no tienen baja lógica, así que los casos son id inexistente y compra de otro tenant.</summary>
    [Fact]
    public async Task UnPutConNumeroInvalidoSobreUnaCompraQueNoExisteEsUn404()
    {
        var ctx = await PrepararAsync(nameof(UnPutConNumeroInvalidoSobreUnaCompraQueNoExisteEsUn404) + "-A");
        var otro = await PrepararAsync(nameof(UnPutConNumeroInvalidoSobreUnaCompraQueNoExisteEsUn404) + "-B");
        var deOtroTenant = await CrearAsync(otro, "3-12345");

        foreach (var id in new[] { int.MaxValue, deOtroTenant.Id })
        {
            var respuesta = await ctx.Admin.PutAsJsonAsync($"/api/compras/{id}", Solicitud(ctx, "FA-1"));

            Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        }

        Assert.Equal("00003-00012345", await NumeroEnBaseAsync(otro.IdTenant, deOtroTenant.Id));
    }

    [Fact]
    public async Task UnRemitoConservaSuNumeroComoTextoLibreRecortado()
    {
        var ctx = await PrepararAsync(nameof(UnRemitoConservaSuNumeroComoTextoLibreRecortado));

        var creada = await CrearAsync(ctx, "  Rem 0003/123  ", ctx.IdTipoRemito);

        Assert.Equal("Rem 0003/123", creada.NumeroExterno);
        Assert.Equal("Rem 0003/123", await NumeroEnBaseAsync(ctx.IdTenant, creada.Id));
    }

    [Fact]
    public async Task UnNumeroEnBlancoEnUnaFacturaEsSinNumeroYNoUnError()
    {
        var ctx = await PrepararAsync(nameof(UnNumeroEnBlancoEnUnaFacturaEsSinNumeroYNoUnError));

        var creada = await CrearAsync(ctx, "   ");

        Assert.Null(creada.NumeroExterno);
        Assert.Null(await NumeroEnBaseAsync(ctx.IdTenant, creada.Id));
    }

    // ---- GET /api/compras/duplicados --------------------------------------------------------------------

    [Fact]
    public async Task ElChequeoEncuentraLasFilasViejasGuardadasConOtroFormato()
    {
        var ctx = await PrepararAsync(nameof(ElChequeoEncuentraLasFilasViejasGuardadasConOtroFormato));
        var viejaConCeros = await SembrarAsync(ctx, "0003-00012345");
        var viejaSinCeros = await SembrarAsync(ctx, "3-12345");
        var viejaConTexto = await SembrarAsync(ctx, "FA 0003-00012345");
        await SembrarAsync(ctx, "0003-00012346");
        await SembrarAsync(ctx, "0004-00012345");
        await SembrarAsync(ctx, "000300012345");
        await SembrarAsync(ctx, "FA");

        foreach (var pedido in new[] { "3-12345", "0003-00012345", "00003-00012345", "  3 - 12345 ", "000300012345" })
        {
            Assert.Equal(
                [viejaConCeros, viejaSinCeros, viejaConTexto],
                await IdsDuplicadosAsync(ctx.Admin, ctx.IdProveedor, ctx.IdTipoCFA, pedido));
        }
    }

    [Fact]
    public async Task ElChequeoDevuelveCadaCampoDeCadaDuplicadoConSusPropiosValores()
    {
        var ctx = await PrepararAsync(nameof(ElChequeoDevuelveCadaCampoDeCadaDuplicadoConSusPropiosValores));
        var borrador = await SembrarAsync(
            ctx, "0003-00012345", EstadoCompra.Borrador, fechaComprobante: new DateOnly(2026, 3, 1), origen: OrigenCompra.Asistente);
        var confirmada = await SembrarAsync(
            ctx, "3-12345", EstadoCompra.Confirmada, fechaComprobante: new DateOnly(2026, 3, 2), origen: OrigenCompra.Manual);

        var duplicados = (await DuplicadosAsync(ctx.Admin, ctx.IdProveedor, ctx.IdTipoCFA, "3-12345")).EnumerateArray().ToList();

        Assert.Equal(2, duplicados.Count);
        Assert.Equal(borrador, duplicados[0].GetProperty("id").GetInt32());
        Assert.Equal("Borrador", duplicados[0].GetProperty("estado").GetString());
        Assert.Equal("0003-00012345", duplicados[0].GetProperty("numeroExterno").GetString());
        Assert.Equal("2026-03-01", duplicados[0].GetProperty("fechaComprobante").GetString());
        Assert.Equal("Asistente", duplicados[0].GetProperty("origen").GetString());
        Assert.Equal(confirmada, duplicados[1].GetProperty("id").GetInt32());
        Assert.Equal("Confirmada", duplicados[1].GetProperty("estado").GetString());
        Assert.Equal("3-12345", duplicados[1].GetProperty("numeroExterno").GetString());
        Assert.Equal("2026-03-02", duplicados[1].GetProperty("fechaComprobante").GetString());
        Assert.Equal("Manual", duplicados[1].GetProperty("origen").GetString());
    }

    /// <summary>Cláusula bajo prueba: <c>c.Estado != EstadoCompra.Anulada</c>. La fila anulada tiene el mismo número
    /// que una viva, así que solo ese filtro la separa.</summary>
    [Fact]
    public async Task UnaCompraAnuladaNoCuentaComoDuplicado()
    {
        var ctx = await PrepararAsync(nameof(UnaCompraAnuladaNoCuentaComoDuplicado));
        await SembrarAsync(ctx, "0003-00012345", EstadoCompra.Anulada);
        var viva = await SembrarAsync(ctx, "3-12345", EstadoCompra.Confirmada);

        Assert.Equal([viva], await IdsDuplicadosAsync(ctx.Admin, ctx.IdProveedor, ctx.IdTipoCFA, "3-12345"));
    }

    /// <summary>Cláusulas bajo prueba: <c>c.IdProveedor == idProveedor</c> y <c>c.IdTipoComprobante == idTipoComprobante</c>,
    /// una fila por cada una que difiere solo en ese campo.</summary>
    [Fact]
    public async Task ElChequeoSeLimitaAlProveedorYAlTipoPedidos()
    {
        var ctx = await PrepararAsync(nameof(ElChequeoSeLimitaAlProveedorYAlTipoPedidos));
        var propia = await SembrarAsync(ctx, "0003-00012345");
        var deOtroProveedor = await SembrarAsync(ctx, "0003-00012345", idProveedor: ctx.IdOtroProveedor);
        var deOtroTipo = await SembrarAsync(ctx, "0003-00012345", idTipo: ctx.IdTipoCFB);

        Assert.Equal([propia], await IdsDuplicadosAsync(ctx.Admin, ctx.IdProveedor, ctx.IdTipoCFA, "3-12345"));
        Assert.Equal([deOtroProveedor], await IdsDuplicadosAsync(ctx.Admin, ctx.IdOtroProveedor, ctx.IdTipoCFA, "3-12345"));
        Assert.Equal([deOtroTipo], await IdsDuplicadosAsync(ctx.Admin, ctx.IdProveedor, ctx.IdTipoCFB, "3-12345"));
    }

    /// <summary>Cláusula bajo prueba: <c>c.Id != excluirIdCompra</c>. Hay un segundo duplicado vivo para que
    /// la respuesta no quede vacía por otra razón.</summary>
    [Fact]
    public async Task ElChequeoDejaAfueraLaCompraQueSeEstaEditando()
    {
        var ctx = await PrepararAsync(nameof(ElChequeoDejaAfueraLaCompraQueSeEstaEditando));
        var enEdicion = await SembrarAsync(ctx, "0003-00012345");
        var otra = await SembrarAsync(ctx, "3-12345");

        Assert.Equal([enEdicion, otra], await IdsDuplicadosAsync(ctx.Admin, ctx.IdProveedor, ctx.IdTipoCFA, "3-12345"));
        Assert.Equal([otra], await IdsDuplicadosAsync(ctx.Admin, ctx.IdProveedor, ctx.IdTipoCFA, "3-12345", excluirId: enEdicion));
        Assert.Equal([enEdicion], await IdsDuplicadosAsync(ctx.Admin, ctx.IdProveedor, ctx.IdTipoCFA, "3-12345", excluirId: otra));
    }

    [Fact]
    public async Task LasComprasDeOtroTenantNuncaApareceNiConSuProveedor()
    {
        var ctx = await PrepararAsync(nameof(LasComprasDeOtroTenantNuncaApareceNiConSuProveedor) + "-A");
        var otro = await PrepararAsync(nameof(LasComprasDeOtroTenantNuncaApareceNiConSuProveedor) + "-B");
        await SembrarAsync(otro, "0003-00012345");
        var propia = await SembrarAsync(ctx, "3-12345");

        Assert.Equal([propia], await IdsDuplicadosAsync(ctx.Admin, ctx.IdProveedor, ctx.IdTipoCFA, "3-12345"));
        Assert.Empty(await IdsDuplicadosAsync(ctx.Admin, otro.IdProveedor, otro.IdTipoCFA, "3-12345"));
    }

    [Fact]
    public async Task EnUnRemitoElChequeoComparaElTextoRecortadoSinDistinguirMayusculas()
    {
        var ctx = await PrepararAsync(nameof(EnUnRemitoElChequeoComparaElTextoRecortadoSinDistinguirMayusculas));
        var remito = await SembrarAsync(ctx, "Rem-0003/123", idTipo: ctx.IdTipoRemito);
        await SembrarAsync(ctx, "Rem-0003/124", idTipo: ctx.IdTipoRemito);
        await SembrarAsync(ctx, "Rem-0003/123", idTipo: ctx.IdTipoCFA);

        Assert.Equal([remito], await IdsDuplicadosAsync(ctx.Admin, ctx.IdProveedor, ctx.IdTipoRemito, "  rem-0003/123 "));
    }

    [Theory]
    [InlineData("FA-1")]
    [InlineData("0-1")]
    [InlineData("1-123456789")]
    public async Task UnNumeroFiscalIninterpretableEnElChequeoEsUn400Estable(string pedido)
    {
        var ctx = await PrepararAsync(nameof(UnNumeroFiscalIninterpretableEnElChequeoEsUn400Estable) + Math.Abs(pedido.GetHashCode()));

        var respuesta = await ctx.Admin.GetAsync(UrlDeDuplicados(ctx.IdProveedor, ctx.IdTipoCFA, pedido));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("numero_comprobante_invalido", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task SinNumeroNoHayDuplicadosYUnTipoQueNoEsDeCompraEs400()
    {
        var ctx = await PrepararAsync(nameof(SinNumeroNoHayDuplicadosYUnTipoQueNoEsDeCompraEs400));
        await SembrarAsync(ctx, "0003-00012345");

        Assert.Empty(await IdsDuplicadosAsync(ctx.Admin, ctx.IdProveedor, ctx.IdTipoCFA, null));
        Assert.Empty(await IdsDuplicadosAsync(ctx.Admin, ctx.IdProveedor, ctx.IdTipoCFA, "   "));

        var respuesta = await ctx.Admin.GetAsync(UrlDeDuplicados(ctx.IdProveedor, int.MaxValue, "3-12345"));
        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("tipo_de_compra_invalido", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnVendedorPuedeConsultarLosDuplicadosPorqueEsUnaLectura()
    {
        var ctx = await PrepararAsync(nameof(UnVendedorPuedeConsultarLosDuplicadosPorqueEsUnaLectura));
        var existente = await SembrarAsync(ctx, "0003-00012345");

        Assert.Equal([existente], await IdsDuplicadosAsync(ctx.Vendedor, ctx.IdProveedor, ctx.IdTipoCFA, "3-12345"));
    }
}
