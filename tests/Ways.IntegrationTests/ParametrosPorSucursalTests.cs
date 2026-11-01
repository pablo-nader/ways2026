using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Organizacion;
using Ways.Application.Parametros;
using Ways.Application.Reportes;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Stock;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// Parámetros por sucursal (T6) contra Postgres real como <c>ways_app</c>. Escenario: la sucursal A tiene las cajas 1 y 2, la
/// sucursal B tiene la caja 3. Las claves de sucursal (zona horaria, lotes, días de alerta) se escriben y se resuelven por
/// la sucursal, las de caja (tolerancia de pago, importe de envío) siguen por punto de venta.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ParametrosPorSucursalTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string ZonaPorDefecto = "America/Argentina/Buenos_Aires";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, HttpClient Admin, int IdCaja1, int IdCaja2, int IdCaja3, int IdSucursalA, int IdSucursalB,
        string Mail, string Password);

    private async Task<Contexto> PrepararAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var respuesta = await root.PostAsJsonAsync(
            "/api/plataforma/tenants",
            new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Caja 1", mailAdmin, ModoPuntoVenta.Web));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var idSucursalA = await SucursalDePrueba.DeAsync(db, resultado.IdPuntoVenta);

        var caja2 = await LeerAsync<PuntoVentaListado>(
            await admin.PostAsJsonAsync(
                "/api/puntos-venta",
                new PuntoVentaAlta(resultado.IdEmpresa, "Caja 2", ModoPuntoVenta.Web, IdSucursal: idSucursalA)),
            HttpStatusCode.Created);
        var sucursalB = await LeerAsync<SucursalListado>(
            await admin.PostAsJsonAsync("/api/sucursales", new SucursalAlta(resultado.IdEmpresa, "Sucursal B")),
            HttpStatusCode.Created);
        var caja3 = await LeerAsync<PuntoVentaListado>(
            await admin.PostAsJsonAsync(
                "/api/puntos-venta",
                new PuntoVentaAlta(resultado.IdEmpresa, "Caja 3", ModoPuntoVenta.Web, IdSucursal: sucursalB.Id)),
            HttpStatusCode.Created);

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, admin, resultado.IdPuntoVenta, caja2.Id, caja3.Id, idSucursalA, sucursalB.Id,
            mailAdmin, resultado.PasswordTemporal);
    }

    private static async Task<T> LeerAsync<T>(HttpResponseMessage respuesta, HttpStatusCode esperado)
    {
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == esperado, $"{respuesta.StatusCode}: {cuerpo}");
        return JsonSerializer.Deserialize<T>(cuerpo, OpcionesJson)!;
    }

    private static async Task AssertCodigoAsync(HttpResponseMessage respuesta, string codigo)
    {
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.BadRequest, $"{respuesta.StatusCode}: {cuerpo}");
        Assert.Equal(codigo, JsonDocument.Parse(cuerpo).RootElement.GetProperty("codigo").GetString());
    }

    private static Task<HttpResponseMessage> EstablecerAsync(Contexto ctx, ParametroAlta datos) =>
        ctx.Admin.PutAsJsonAsync($"/api/parametros?idEmpresa={ctx.IdEmpresa}", datos);

    private static async Task EstablecerOkAsync(Contexto ctx, ParametroAlta datos)
    {
        var respuesta = await EstablecerAsync(ctx, datos);
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, await respuesta.Content.ReadAsStringAsync());
    }

    private static async Task<string> ResolverAsync(Contexto ctx, string clave, string consulta)
    {
        var respuesta = await ctx.Admin.GetAsync($"/api/parametros/{clave}?idEmpresa={ctx.IdEmpresa}&{consulta}");
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonDocument.Parse(cuerpo).RootElement.GetProperty("valor").GetString()!;
    }

    private async Task<int> SembrarSucursalDeOtraEmpresaAsync(Contexto ctx)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var empresa = new Empresa { IdTenant = ctx.IdTenant, RazonSocial = "Otra empresa", CreatedAt = ahora, UpdatedAt = ahora };
        db.Empresas.Add(empresa);
        await db.SaveChangesAsync();

        var sucursal = new Sucursal { IdTenant = ctx.IdTenant, IdEmpresa = empresa.Id, Nombre = "Ajena", CreatedAt = ahora, UpdatedAt = ahora };
        db.Sucursales.Add(sucursal);
        await db.SaveChangesAsync();
        return sucursal.Id;
    }

    // ---- escritura: cada clave admite un solo nivel ---------------------------------------------

    /// <summary>
    /// LAS GUARDAS (mutation-proof-tests): cada aserción nombra una cláusula distinta de <c>ExigirAlcance</c> /
    /// <c>ValidarSucursalDeLaEmpresaAsync</c>; sacar cualquiera de las cuatro cambia el código de la línea correspondiente.
    /// </summary>
    [Fact]
    public async Task UnaClaveSoloAdmiteElNivelQueLeCorrespondeYLaSucursalTieneQueSerDeLaEmpresa()
    {
        var ctx = await PrepararAsync(nameof(UnaClaveSoloAdmiteElNivelQueLeCorrespondeYLaSucursalTieneQueSerDeLaEmpresa));
        var ajena = await SembrarSucursalDeOtraEmpresaAsync(ctx);

        await AssertCodigoAsync(
            await EstablecerAsync(ctx, new ParametroAlta("zona_horaria", "\"America/Lima\"", ctx.IdCaja1)),
            "parametro_de_sucursal");
        await AssertCodigoAsync(
            await EstablecerAsync(ctx, new ParametroAlta("tolerancia_pago", "15", null, ctx.IdSucursalA)),
            "parametro_de_caja");
        await AssertCodigoAsync(
            await EstablecerAsync(ctx, new ParametroAlta("zona_horaria", "\"America/Lima\"", ctx.IdCaja1, ctx.IdSucursalA)),
            "parametro_alcance_ambiguo");
        await AssertCodigoAsync(
            await EstablecerAsync(ctx, new ParametroAlta("zona_horaria", "\"America/Lima\"", null, ajena)),
            "sucursal_no_pertenece_a_la_empresa");
        await AssertCodigoAsync(
            await EstablecerAsync(ctx, new ParametroAlta("zona_horaria", "\"America/Lima\"", null, 987654)),
            "sucursal_no_pertenece_a_la_empresa");

        Assert.Empty(await LeerAsync<List<ParametroListado>>(
            await ctx.Admin.GetAsync($"/api/parametros?idEmpresa={ctx.IdEmpresa}"), HttpStatusCode.OK));
    }

    // ---- resolución -----------------------------------------------------------------------------

    [Fact]
    public async Task DosCajasDeLaMismaSucursalCompartenLaZonaHorariaYLosLotesYLaDeOtraSucursalNo()
    {
        var ctx = await PrepararAsync(nameof(DosCajasDeLaMismaSucursalCompartenLaZonaHorariaYLosLotesYLaDeOtraSucursalNo));

        await EstablecerOkAsync(ctx, new ParametroAlta("zona_horaria", "\"America/Lima\"", null, ctx.IdSucursalA));

        Assert.Equal("\"America/Lima\"", await ResolverAsync(ctx, "zona_horaria", $"idPuntoVenta={ctx.IdCaja1}"));
        Assert.Equal("\"America/Lima\"", await ResolverAsync(ctx, "zona_horaria", $"idPuntoVenta={ctx.IdCaja2}"));
        Assert.Equal("\"America/Lima\"", await ResolverAsync(ctx, "zona_horaria", $"idSucursal={ctx.IdSucursalA}"));
        Assert.Equal($"\"{ZonaPorDefecto}\"", await ResolverAsync(ctx, "zona_horaria", $"idPuntoVenta={ctx.IdCaja3}"));
        Assert.Equal($"\"{ZonaPorDefecto}\"", await ResolverAsync(ctx, "zona_horaria", $"idSucursal={ctx.IdSucursalB}"));
        Assert.Equal($"\"{ZonaPorDefecto}\"", await ResolverAsync(ctx, "zona_horaria", string.Empty));

        await EstablecerOkAsync(ctx, new ParametroAlta("lotes_habilitado", "true", null, ctx.IdSucursalA));

        Assert.Equal("true", await ResolverAsync(ctx, "lotes_habilitado", $"idPuntoVenta={ctx.IdCaja1}"));
        Assert.Equal("true", await ResolverAsync(ctx, "lotes_habilitado", $"idPuntoVenta={ctx.IdCaja2}"));
        Assert.Equal("false", await ResolverAsync(ctx, "lotes_habilitado", $"idPuntoVenta={ctx.IdCaja3}"));
    }

    [Fact]
    public async Task UnaClaveDeCajaSigueResolviendoPorPuntoDeVentaYElDefaultDeLaEmpresaEsElDeLaSucursalSinFilaPropia()
    {
        var ctx = await PrepararAsync(
            nameof(UnaClaveDeCajaSigueResolviendoPorPuntoDeVentaYElDefaultDeLaEmpresaEsElDeLaSucursalSinFilaPropia));

        await EstablecerOkAsync(ctx, new ParametroAlta("tolerancia_pago", "25", ctx.IdCaja1));
        await EstablecerOkAsync(ctx, new ParametroAlta("dias_alerta_vencimiento", "40", null));
        await EstablecerOkAsync(ctx, new ParametroAlta("dias_alerta_vencimiento", "7", null, ctx.IdSucursalB));

        Assert.Equal("25", await ResolverAsync(ctx, "tolerancia_pago", $"idPuntoVenta={ctx.IdCaja1}"));
        Assert.Equal("10", await ResolverAsync(ctx, "tolerancia_pago", $"idPuntoVenta={ctx.IdCaja2}"));
        Assert.Equal("40", await ResolverAsync(ctx, "dias_alerta_vencimiento", $"idPuntoVenta={ctx.IdCaja2}"));
        Assert.Equal("7", await ResolverAsync(ctx, "dias_alerta_vencimiento", $"idPuntoVenta={ctx.IdCaja3}"));
    }

    [Fact]
    public async Task LeerConLosDosIdsOUnaClaveDeCajaPorSucursalSeRechaza()
    {
        var ctx = await PrepararAsync(nameof(LeerConLosDosIdsOUnaClaveDeCajaPorSucursalSeRechaza));

        await AssertCodigoAsync(
            await ctx.Admin.GetAsync(
                $"/api/parametros/zona_horaria?idEmpresa={ctx.IdEmpresa}&idPuntoVenta={ctx.IdCaja1}&idSucursal={ctx.IdSucursalA}"),
            "parametro_alcance_ambiguo");
        await AssertCodigoAsync(
            await ctx.Admin.GetAsync($"/api/parametros/tolerancia_pago?idEmpresa={ctx.IdEmpresa}&idSucursal={ctx.IdSucursalA}"),
            "parametro_de_caja");
    }

    // ---- reportes -------------------------------------------------------------------------------

    /// <summary>
    /// LA CLÁUSULA: el reporte de una sucursal lee la fila de la SUCURSAL, no la de la caja de menor id ni la de la empresa.
    /// Los tres niveles valen distinto (caja 1 = 99, empresa = 40, sucursal = 7) y la zona también: ninguno de los otros dos
    /// produce el 7 / Lima. La fila por caja se siembra por SQL directo porque la API ya no la acepta.
    /// </summary>
    [Fact]
    public async Task ElReporteDeUnaSucursalLeeLaFilaDeLaSucursalYNoLaDeSuCajaDeMenorId()
    {
        var ctx = await PrepararAsync(nameof(ElReporteDeUnaSucursalLeeLaFilaDeLaSucursalYNoLaDeSuCajaDeMenorId));

        await EstablecerOkAsync(ctx, new ParametroAlta("dias_alerta_vencimiento", "40", null));
        await EstablecerOkAsync(ctx, new ParametroAlta("dias_alerta_vencimiento", "7", null, ctx.IdSucursalA));
        await EstablecerOkAsync(ctx, new ParametroAlta("zona_horaria", "\"America/Lima\"", null, ctx.IdSucursalA));
        await SembrarFilaPorCajaAsync(ctx, ctx.IdCaja1, "dias_alerta_vencimiento", "99");
        await SembrarFilaPorCajaAsync(ctx, ctx.IdCaja1, "zona_horaria", "\"Asia/Kolkata\"");

        var porSucursal = await LeerAsync<Vencimientos>(
            await ctx.Admin.GetAsync($"/api/reportes/stock/vencimientos?idSucursal={ctx.IdSucursalA}"), HttpStatusCode.OK);
        var porCajaDeLaSucursal = await LeerAsync<Vencimientos>(
            await ctx.Admin.GetAsync($"/api/reportes/stock/vencimientos?idPuntoVenta={ctx.IdCaja2}"), HttpStatusCode.OK);
        var deOtraSucursal = await LeerAsync<Vencimientos>(
            await ctx.Admin.GetAsync($"/api/reportes/stock/vencimientos?idSucursal={ctx.IdSucursalB}"), HttpStatusCode.OK);

        Assert.Equal((7, "America/Lima"), (porSucursal.DiasDeAlerta, porSucursal.ZonaHoraria));
        Assert.Equal((7, "America/Lima"), (porCajaDeLaSucursal.DiasDeAlerta, porCajaDeLaSucursal.ZonaHoraria));
        Assert.Equal((40, ZonaPorDefecto), (deOtraSucursal.DiasDeAlerta, deOtraSucursal.ZonaHoraria));
    }

    private async Task SembrarFilaPorCajaAsync(Contexto ctx, int idCaja, string clave, string valorJson)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        db.Parametros.Add(new Parametro
        {
            IdTenant = ctx.IdTenant, IdEmpresa = ctx.IdEmpresa, IdPuntoVenta = idCaja, Clave = clave, Valor = valorJson,
            CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    // ---- constraints ----------------------------------------------------------------------------

    [Fact]
    public async Task LaBaseRechazaUnaSegundaFilaDeLaSucursalUnaFilaConLosDosNivelesYUnaSucursalDeOtraEmpresa()
    {
        var ctx = await PrepararAsync(nameof(LaBaseRechazaUnaSegundaFilaDeLaSucursalUnaFilaConLosDosNivelesYUnaSucursalDeOtraEmpresa));
        var ajena = await SembrarSucursalDeOtraEmpresaAsync(ctx);
        await using var owner = new NpgsqlConnection(fixture.OwnerConnectionString);
        await owner.OpenAsync();

        const string insertar =
            "INSERT INTO parametros (id_tenant, id_empresa, id_punto_venta, id_sucursal, clave, valor, created_at, updated_at) " +
            "VALUES ($1, $2, $3, $4, 'zona_horaria', '\"UTC\"'::jsonb, now(), now())";

        async Task<PostgresException> FallaAsync(int? idPuntoVenta, int? idSucursal)
        {
            await using var comando = owner.CreateCommand();
            comando.CommandText = insertar;
            comando.Parameters.Add(new NpgsqlParameter { Value = ctx.IdTenant });
            comando.Parameters.Add(new NpgsqlParameter { Value = ctx.IdEmpresa });
            comando.Parameters.Add(new NpgsqlParameter { Value = (object?)idPuntoVenta ?? DBNull.Value });
            comando.Parameters.Add(new NpgsqlParameter { Value = (object?)idSucursal ?? DBNull.Value });
            return await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        }

        await using (var primera = owner.CreateCommand())
        {
            primera.CommandText = insertar;
            primera.Parameters.Add(new NpgsqlParameter { Value = ctx.IdTenant });
            primera.Parameters.Add(new NpgsqlParameter { Value = ctx.IdEmpresa });
            primera.Parameters.Add(new NpgsqlParameter { Value = DBNull.Value });
            primera.Parameters.Add(new NpgsqlParameter { Value = ctx.IdSucursalA });
            Assert.Equal(1, await primera.ExecuteNonQueryAsync());
        }

        var duplicada = await FallaAsync(null, ctx.IdSucursalA);
        Assert.Equal("23505", duplicada.SqlState);
        Assert.Equal("ux_parametros_sucursal", duplicada.ConstraintName);

        var dosNiveles = await FallaAsync(ctx.IdCaja1, ctx.IdSucursalB);
        Assert.Equal("23514", dosNiveles.SqlState);
        Assert.Equal("ck_parametros_un_solo_alcance", dosNiveles.ConstraintName);

        var deOtraEmpresa = await FallaAsync(null, ajena);
        Assert.Equal("23503", deOtraEmpresa.SqlState);
        Assert.Equal("fk_parametros_sucursal", deOtraEmpresa.ConstraintName);
    }

    /// <summary>La fila de empresa y la de sucursal conviven con la misma clave: el índice de empresa excluye las filas de
    /// sucursal (<c>ux_parametros_empresa</c> pide <c>id_sucursal IS NULL</c>).</summary>
    [Fact]
    public async Task ElDefaultDeLaEmpresaYElDeUnaSucursalConviven()
    {
        var ctx = await PrepararAsync(nameof(ElDefaultDeLaEmpresaYElDeUnaSucursalConviven));

        await EstablecerOkAsync(ctx, new ParametroAlta("dias_rotacion", "30", null));
        await EstablecerOkAsync(ctx, new ParametroAlta("dias_rotacion", "14", null, ctx.IdSucursalA));
        await EstablecerOkAsync(ctx, new ParametroAlta("dias_rotacion", "21", null, ctx.IdSucursalB));
        await EstablecerOkAsync(ctx, new ParametroAlta("dias_rotacion", "15", null, ctx.IdSucursalA));

        var filas = await LeerAsync<List<ParametroListado>>(
            await ctx.Admin.GetAsync($"/api/parametros?idEmpresa={ctx.IdEmpresa}"), HttpStatusCode.OK);
        Assert.Equal(
            new List<(int?, string)> { (null, "30"), (ctx.IdSucursalA, "15"), (ctx.IdSucursalB, "21") },
            filas.OrderBy(f => f.IdSucursal).Select(f => (f.IdSucursal, f.Valor)).ToList());
    }

    /// <summary>Retiene las dos primeras lecturas de <c>parametros</c> (la "existente" de cada PUT) ya ejecutadas hasta que
    /// ambas volvieron: las dos ven "no existe" y las dos insertan (misma técnica que <c>ParametrosTests</c>).</summary>
    private sealed class InterceptorDeRendezVous(CountdownEvent gate) : DbCommandInterceptor
    {
        private int _participantes;

        public int Participantes => _participantes;

        public override DbDataReader ReaderExecuted(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
        {
            EsperarSiCorresponde(command);
            return base.ReaderExecuted(command, eventData, result);
        }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            EsperarSiCorresponde(command);
            return await base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }

        private void EsperarSiCorresponde(DbCommand command)
        {
            if (!command.CommandText.Contains("parametros", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (Interlocked.Increment(ref _participantes) > 2)
            {
                return;
            }

            gate.Signal();
            gate.Wait(TimeSpan.FromSeconds(30));
        }
    }

    [Fact]
    public async Task DosEstablecimientosConcurrentesDeLaMismaClaveDeSucursalDisparanElBackstopYSeTraducenA409()
    {
        var ctx = await PrepararAsync(nameof(DosEstablecimientosConcurrentesDeLaMismaClaveDeSucursalDisparanElBackstopYSeTraducenA409));

        using var gate = new CountdownEvent(2);
        var interceptor = new InterceptorDeRendezVous(gate);
        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddDbContext<WaysDbContext>((_, options) => options.AddInterceptors(interceptor))));

        using var cliente = factory.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(ctx.Mail, ctx.Password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var url = $"/api/parametros?idEmpresa={ctx.IdEmpresa}";
        var tareaA = cliente.PutAsJsonAsync(url, new ParametroAlta("zona_horaria", "\"America/Lima\"", null, ctx.IdSucursalA));
        var tareaB = cliente.PutAsJsonAsync(url, new ParametroAlta("zona_horaria", "\"America/Bogota\"", null, ctx.IdSucursalA));

        var respuestas = await Task.WhenAll(tareaA, tareaB);
        var estados = respuestas.Select(r => r.StatusCode).ToList();

        Assert.True(interceptor.Participantes >= 2, $"participantes={interceptor.Participantes}");
        Assert.Contains(HttpStatusCode.OK, estados);
        Assert.Contains(HttpStatusCode.Conflict, estados);

        var problema = await respuestas.Single(r => r.StatusCode == HttpStatusCode.Conflict)
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("parametro_duplicado", problema.GetProperty("codigo").GetString());
    }

    // ---- lotes: el flip por sucursal reconcilia esa sucursal ------------------------------------

    private async Task<int> SembrarArticuloConStockAsync(Contexto ctx, string nombre, int idCaja, decimal cantidad)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var area = new Area { IdTenant = ctx.IdTenant, Nombre = $"Area {nombre}", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var articulo = new Articulo
        {
            IdTenant = ctx.IdTenant, CodigoInterno = $"{nombre}-{Guid.NewGuid():N}", Nombre = nombre, IdArea = area.Id,
            IdAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync(), UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true, ControlaLote = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        db.Stock.Add(new Stock
        {
            IdArticulo = articulo.Id, IdSucursal = await SucursalDePrueba.DeAsync(db, idCaja), IdTenant = ctx.IdTenant,
            Cantidad = cantidad
        });
        await db.SaveChangesAsync();
        return articulo.Id;
    }

    private async Task<int> ReclasificacionesAsync(Contexto ctx, int idArticulo)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        return await db.MovimientosStock.CountAsync(m => m.IdArticulo == idArticulo && m.Motivo == MotivoStock.Reclasificacion);
    }

    [Fact]
    public async Task ActivarLotesEnUnaSucursalReconciliaSoloEsaSucursalYElDefaultDeLaEmpresaRespetaLaFilaPropiaEnFalse()
    {
        var ctx = await PrepararAsync(
            nameof(ActivarLotesEnUnaSucursalReconciliaSoloEsaSucursalYElDefaultDeLaEmpresaRespetaLaFilaPropiaEnFalse));
        var articuloA = await SembrarArticuloConStockAsync(ctx, "Yogur A", ctx.IdCaja2, 17m);
        var articuloB = await SembrarArticuloConStockAsync(ctx, "Yogur B", ctx.IdCaja3, 5m);

        // B declara explícitamente que no usa lotes; A todavía hereda el default (false).
        await EstablecerOkAsync(ctx, new ParametroAlta("lotes_habilitado", "false", null, ctx.IdSucursalB));
        Assert.Equal(0, await ReclasificacionesAsync(ctx, articuloA));

        await EstablecerOkAsync(ctx, new ParametroAlta("lotes_habilitado", "true", null, ctx.IdSucursalA));

        Assert.Equal(2, await ReclasificacionesAsync(ctx, articuloA));
        Assert.Equal(0, await ReclasificacionesAsync(ctx, articuloB));

        // El default de la empresa pasa a true: B conserva su fila propia en false y no se reconcilia.
        await EstablecerOkAsync(ctx, new ParametroAlta("lotes_habilitado", "true", null));

        Assert.Equal(2, await ReclasificacionesAsync(ctx, articuloA));
        Assert.Equal(0, await ReclasificacionesAsync(ctx, articuloB));

        // Y al sacarle el false a B (true), recién ahí se reconcilia.
        await EstablecerOkAsync(ctx, new ParametroAlta("lotes_habilitado", "true", null, ctx.IdSucursalB));

        Assert.Equal(2, await ReclasificacionesAsync(ctx, articuloB));
    }

    // ---- fusión ---------------------------------------------------------------------------------

    /// <summary>
    /// LA REGLA DE LA FUSIÓN: el destino conserva sus filas, toma las del origen solo donde no tiene la clave y las del
    /// origen que el destino ya tenía se dan de baja. Cada rama tiene una clave propia: <c>zona_horaria</c> está en las dos
    /// (gana el destino), <c>dias_rotacion</c> solo en el origen (pasa al destino), <c>dias_alerta_vencimiento</c> solo en
    /// el destino (queda).
    /// </summary>
    [Fact]
    public async Task LaFusionConservaLasFilasDelDestinoYTomaLasDelOrigenDondeNoTieneNinguna()
    {
        var ctx = await PrepararAsync(nameof(LaFusionConservaLasFilasDelDestinoYTomaLasDelOrigenDondeNoTieneNinguna));

        await EstablecerOkAsync(ctx, new ParametroAlta("zona_horaria", "\"America/Lima\"", null, ctx.IdSucursalA));
        await EstablecerOkAsync(ctx, new ParametroAlta("dias_alerta_vencimiento", "9", null, ctx.IdSucursalA));
        await EstablecerOkAsync(ctx, new ParametroAlta("zona_horaria", "\"America/Bogota\"", null, ctx.IdSucursalB));
        await EstablecerOkAsync(ctx, new ParametroAlta("dias_rotacion", "11", null, ctx.IdSucursalB));

        var fusion = await ctx.Admin.PostAsJsonAsync(
            $"/api/sucursales/{ctx.IdSucursalB}/fusionar", new SucursalFusion(ctx.IdSucursalA));
        Assert.True(fusion.StatusCode == HttpStatusCode.OK, await fusion.Content.ReadAsStringAsync());

        var filas = await LeerAsync<List<ParametroListado>>(
            await ctx.Admin.GetAsync($"/api/parametros?idEmpresa={ctx.IdEmpresa}"), HttpStatusCode.OK);
        Assert.Equal(
            [("dias_alerta_vencimiento", ctx.IdSucursalA, "9"), ("dias_rotacion", ctx.IdSucursalA, "11"),
             ("zona_horaria", ctx.IdSucursalA, "\"America/Lima\"")],
            filas.OrderBy(f => f.Clave).Select(f => (f.Clave, f.IdSucursal ?? 0, f.Valor)).ToList());

        Assert.Equal("\"America/Lima\"", await ResolverAsync(ctx, "zona_horaria", $"idPuntoVenta={ctx.IdCaja3}"));
        Assert.Equal("11", await ResolverAsync(ctx, "dias_rotacion", $"idPuntoVenta={ctx.IdCaja3}"));
    }

    /// <summary>Mover la última caja de una sucursal que solo tiene parámetros no se bloquea: la sucursal cae y sus
    /// valores pasan a la nueva con la regla de la fusión (el destino conserva los suyos y toma los que le faltan).</summary>
    [Fact]
    public async Task MoverLaUltimaCajaDeUnaSucursalConSoloParametrosLosLlevaALaNuevaDondeNoTieneLosSuyos()
    {
        var ctx = await PrepararAsync(nameof(MoverLaUltimaCajaDeUnaSucursalConSoloParametrosLosLlevaALaNuevaDondeNoTieneLosSuyos));

        await EstablecerOkAsync(ctx, new ParametroAlta("zona_horaria", "\"America/Lima\"", null, ctx.IdSucursalA));
        await EstablecerOkAsync(ctx, new ParametroAlta("zona_horaria", "\"America/Bogota\"", null, ctx.IdSucursalB));
        await EstablecerOkAsync(ctx, new ParametroAlta("dias_rotacion", "11", null, ctx.IdSucursalB));

        var mover = await ctx.Admin.PostAsJsonAsync(
            $"/api/puntos-venta/{ctx.IdCaja3}/sucursal", new PuntoVentaSucursalEdicion(ctx.IdSucursalA));
        Assert.True(mover.StatusCode == HttpStatusCode.OK, await mover.Content.ReadAsStringAsync());

        var filas = await LeerAsync<List<ParametroListado>>(
            await ctx.Admin.GetAsync($"/api/parametros?idEmpresa={ctx.IdEmpresa}"), HttpStatusCode.OK);
        Assert.Equal(
            [("dias_rotacion", ctx.IdSucursalA, "11"), ("zona_horaria", ctx.IdSucursalA, "\"America/Lima\"")],
            filas.OrderBy(f => f.Clave).Select(f => (f.Clave, f.IdSucursal ?? 0, f.Valor)).ToList());

        Assert.Equal("\"America/Lima\"", await ResolverAsync(ctx, "zona_horaria", $"idPuntoVenta={ctx.IdCaja3}"));
        Assert.Equal("11", await ResolverAsync(ctx, "dias_rotacion", $"idPuntoVenta={ctx.IdCaja3}"));
    }
}
