using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Organizacion;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// El modo de acceso <c>publico</c> y la tabla <c>vitrinas</c> contra Postgres real, con el rol
/// <c>ways_app</c> (sin <c>BYPASSRLS</c>) y SIN el filtro de EF de por medio: conexión cruda, o EF con
/// <c>IgnoreQueryFilters</c>. Un test que pasara por el endpoint no probaría la policy, porque el filtro
/// de EF y el <c>JOIN</c> del lector dan el mismo resultado aunque la policy no existiera.
///
/// Cada test nombra la cláusula que mata: <c>id_tenant = app_tenant_publico()</c>, <c>publicada</c>,
/// <c>deleted_at IS NULL</c>, <c>app_modo() = 'publico'</c>, que el tenant viaje en el GUC público y no
/// en el estándar, y que sean exactamente tres tablas, solo de lectura.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class VitrinaAislamientoTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private readonly ApoyoDeVitrinas apoyo = new(fixture);

    /// <summary>Dos comercios, cada uno con filas que ESTA prueba tiene que ver o no ver. El tenant A tiene
    /// una vitrina publicada y viva, otra sin publicar y otra publicada pero dada de baja, todas en empresas
    /// distintas, y puntos de venta en dos de ellas; el B tiene la suya publicada. Lo que no se ve es lo que
    /// prueba cada cláusula.</summary>
    private sealed record Escenario(
        ApoyoDeVitrinas.Comercio A,
        ApoyoDeVitrinas.Comercio B,
        int VitrinaPublicadaDeA,
        int VitrinaSinPublicarDeA,
        int VitrinaPublicadaYEliminadaDeA,
        int VitrinaPublicadaDeB,
        int[] EmpresasDeA,
        int[] PuntosVentaDeA,
        int[] PuntosVentaDeB);

    private async Task<Escenario> SembrarEscenarioAsync(string prefijo)
    {
        var a = await apoyo.SembrarComercioAsync($"{prefijo}-a");
        var b = await apoyo.SembrarComercioAsync($"{prefijo}-b");

        var sinPublicar = await apoyo.SembrarEmpresaAsync(a.IdTenant, "A sin publicar SA");
        var eliminada = await apoyo.SembrarEmpresaAsync(a.IdTenant, "A con vitrina eliminada SA");

        var publicadaDeA = await apoyo.SembrarVitrinaAsync(a.IdTenant, a.IdEmpresa, publicada: true, tagline: "A publicada");
        var sinPublicarDeA = await apoyo.SembrarVitrinaAsync(a.IdTenant, sinPublicar, publicada: false, tagline: "A sin publicar");
        var eliminadaDeA = await apoyo.SembrarVitrinaAsync(
            a.IdTenant, eliminada, publicada: true, tagline: "A eliminada", eliminada: true);
        var publicadaDeB = await apoyo.SembrarVitrinaAsync(b.IdTenant, b.IdEmpresa, publicada: true, tagline: "B publicada");

        var pvA1 = await apoyo.SembrarPuntoVentaAsync(a.IdTenant, a.IdEmpresa, "A1");
        var pvA2 = await apoyo.SembrarPuntoVentaAsync(a.IdTenant, sinPublicar, "A2");
        var pvB = await apoyo.SembrarPuntoVentaAsync(b.IdTenant, b.IdEmpresa, "B1");

        return new Escenario(
            a, b, publicadaDeA, sinPublicarDeA, eliminadaDeA, publicadaDeB,
            [a.IdEmpresa, sinPublicar, eliminada], [pvA1, pvA2], [pvB]);
    }

    private static async Task<int[]> IdsAsync(NpgsqlConnection conexion, string sql)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;

        var ids = new List<int>();
        await using var lector = await comando.ExecuteReaderAsync();
        while (await lector.ReadAsync())
        {
            ids.Add(lector.GetInt32(0));
        }

        return [.. ids.Order()];
    }

    private static async Task<long> ContarAsync(NpgsqlConnection conexion, string tabla)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = $"SELECT count(*) FROM {tabla}";

        return (long)(await comando.ExecuteScalarAsync())!;
    }

    // ---- qué se ve en modo publico ----------------------------------------------------------------

    /// <summary>La lista completa, sin WHERE: cada fila de más rompe la igualdad. La de B mata
    /// <c>id_tenant = app_tenant_publico()</c>; la sin publicar, <c>publicada</c>; la publicada y dada de
    /// baja, <c>deleted_at IS NULL</c>.</summary>
    [Fact]
    public async Task EnModoPublicoLaPolicyDeVitrinasDejaVerSoloLaPublicadaYVivaDelTenantDelGuc()
    {
        var e = await SembrarEscenarioAsync("rls-vitrinas");

        int[] soloLaDeA = [e.VitrinaPublicadaDeA];
        int[] soloLaDeB = [e.VitrinaPublicadaDeB];

        await using var comoA = await fixture.AbrirConexionCrudaPublicaAsync(e.A.IdTenant);
        Assert.Equal(soloLaDeA, await IdsAsync(comoA, "SELECT id_vitrina FROM vitrinas"));

        await using var comoB = await fixture.AbrirConexionCrudaPublicaAsync(e.B.IdTenant);
        Assert.Equal(soloLaDeB, await IdsAsync(comoB, "SELECT id_vitrina FROM vitrinas"));
    }

    [Fact]
    public async Task EnModoPublicoLasPoliciesDeEmpresasYPuntosVentaDejanVerSoloLasDelTenantDelGuc()
    {
        var e = await SembrarEscenarioAsync("rls-empresas");

        int[] empresasDeA = [.. e.EmpresasDeA.Order()];
        int[] puntosVentaDeA = [.. e.PuntosVentaDeA.Order()];
        int[] empresaDeB = [e.B.IdEmpresa];

        await using var comoA = await fixture.AbrirConexionCrudaPublicaAsync(e.A.IdTenant);
        Assert.Equal(empresasDeA, await IdsAsync(comoA, "SELECT id_empresa FROM empresas"));
        Assert.Equal(puntosVentaDeA, await IdsAsync(comoA, "SELECT id_punto_venta FROM puntos_venta"));

        await using var comoB = await fixture.AbrirConexionCrudaPublicaAsync(e.B.IdTenant);
        Assert.Equal(empresaDeB, await IdsAsync(comoB, "SELECT id_empresa FROM empresas"));
        Assert.Equal(e.PuntosVentaDeB, await IdsAsync(comoB, "SELECT id_punto_venta FROM puntos_venta"));
    }

    /// <summary>La policy lee <c>app_modo() = 'publico'</c> y no solo el GUC del tenant: con el GUC público
    /// puesto pero cualquier otro modo, las tres tablas no se ven. Mata la cláusula de modo.</summary>
    [Theory]
    [InlineData("login")]
    [InlineData("")]
    [InlineData("ninguno")]
    public async Task ElGucPublicoSinElModoPublicoNoAbreNada(string modo)
    {
        var e = await SembrarEscenarioAsync($"rls-modo-{(modo == "" ? "vacio" : modo)}");

        await using var conexion = await fixture.AbrirConexionCrudaPublicaAsync(e.A.IdTenant, modo);

        foreach (var tabla in new[] { "vitrinas", "empresas", "puntos_venta" })
        {
            Assert.Equal(0L, await ContarAsync(conexion, tabla));
        }
    }

    [Fact]
    public async Task EnModoPublicoSinTenantNoSeVeNada()
    {
        await SembrarEscenarioAsync("rls-sin-tenant");

        await using var conexion = await fixture.AbrirConexionCrudaPublicaAsync(idTenantPublico: null);

        foreach (var tabla in new[] { "vitrinas", "empresas", "puntos_venta" })
        {
            Assert.Equal(0L, await ContarAsync(conexion, tabla));
        }
    }

    /// <summary>
    /// El modo público no ve NINGUNA otra tabla del comercio. Cada tabla tiene filas del tenant A (se
    /// comprueba antes, en modo tenant, para que el cero no sea vacío). Es la prueba de que el tenant del
    /// modo público viaja en <c>app.tenant_publico</c>: las policies <c>{tabla}_tenant</c> no miran el modo
    /// y habrían abierto estas tablas si el tenant estuviera en <c>app.tenant_id</c>.
    /// </summary>
    [Fact]
    public async Task EnModoPublicoNoSeLeeNingunaOtraTablaDelTenant()
    {
        var e = await SembrarEscenarioAsync("rls-otras");
        await SembrarArticuloYClienteAsync(e.A.IdTenant, e.PuntosVentaDeA[0]);

        // articulos, listas_precio y categorias tienen policy de modo público desde la tienda online
        // (CatalogoOnlineAislamientoTests); acá quedan las que no. stock está en la lista a propósito: es lo que
        // un catálogo de tienda tentaría leer para mostrar disponibilidad, y la tienda no lo lee.
        string[] otras = ["usuarios", "clientes", "areas", "dominios", "tenants", "stock", "codigos_barra"];

        await using (var comoTenant = await fixture.AbrirConexionCrudaAsync("tenant", e.A.IdTenant))
        {
            foreach (var tabla in otras)
            {
                Assert.True(await ContarAsync(comoTenant, tabla) > 0, $"{tabla}: el tenant A debería tener filas.");
            }
        }

        await using var comoPublico = await fixture.AbrirConexionCrudaPublicaAsync(e.A.IdTenant);
        foreach (var tabla in otras)
        {
            Assert.Equal(0L, await ContarAsync(comoPublico, tabla));
        }
    }

    [Fact]
    public async Task EnModoPublicoNoSePuedeEscribirNadaEnLasTresTablas()
    {
        var e = await SembrarEscenarioAsync("rls-escritura");
        await using var conexion = await fixture.AbrirConexionCrudaPublicaAsync(e.A.IdTenant);

        // UPDATE y DELETE: RLS filtra las filas antes de tocarlas, 0 filas y sin excepción.
        foreach (var sql in new[]
        {
            "UPDATE vitrinas SET tagline = 'hackeada'",
            "UPDATE empresas SET razon_social = 'hackeada'",
            "UPDATE puntos_venta SET nombre = 'hackeado'",
            "DELETE FROM vitrinas",
            "DELETE FROM empresas",
            "DELETE FROM puntos_venta"
        })
        {
            await using var comando = conexion.CreateCommand();
            comando.CommandText = sql;

            Assert.Equal(0, await comando.ExecuteNonQueryAsync());
        }

        // INSERT: rechazado por el WITH CHECK de la policy estándar (ninguna policy de insert aplica).
        foreach (var sql in new[]
        {
            $"INSERT INTO vitrinas (id_tenant, id_empresa, created_at, updated_at) VALUES ({e.A.IdTenant}, {e.A.IdEmpresa}, now(), now())",
            $"INSERT INTO empresas (id_tenant, razon_social, created_at, updated_at) VALUES ({e.A.IdTenant}, 'x', now(), now())",
            $"INSERT INTO puntos_venta (id_tenant, id_empresa, nombre, modo, created_at, updated_at) VALUES ({e.A.IdTenant}, {e.A.IdEmpresa}, 'x', 'web', now(), now())"
        })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                await using var comando = conexion.CreateCommand();
                comando.CommandText = sql;
                await comando.ExecuteNonQueryAsync();
            });

            Assert.Equal("42501", error.SqlState);
        }

        var intacta = (await apoyo.LeerVitrinaAsync(e.A.IdEmpresa))!;
        Assert.Equal("A publicada", intacta.Tagline);
    }

    /// <summary>Exactamente las trece tablas de la landing y la tienda con policy de modo público, todas de solo
    /// lectura: otra tabla con una policy así, o una de escritura, rompe la igualdad.</summary>
    [Fact]
    public async Task SoloLasTreceTablasDeLaLandingYLaTiendaTienenPolicyDeModoPublicoYSonDeSoloLectura()
    {
        using var _ = fixture.CreateClient();
        await using var conexion = new NpgsqlConnection(fixture.OwnerConnectionString);
        await conexion.OpenAsync();

        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            "SELECT tablename || ':' || policyname || ':' || cmd FROM pg_policies " +
            "WHERE schemaname = 'public' AND (qual ILIKE '%publico%' OR with_check ILIKE '%publico%') ORDER BY 1";

        var policies = new List<string>();
        await using (var lector = await comando.ExecuteReaderAsync())
        {
            while (await lector.ReadAsync())
            {
                policies.Add(lector.GetString(0));
            }
        }

        // El orden de la base depende del collation: se compara ordenado por código.
        Assert.Equal(
            [
                "articulos:articulos_publico_lectura:SELECT",
                "articulos_empresas:articulos_empresas_publico_lectura:SELECT",
                "categorias:categorias_publico_lectura:SELECT",
                "combos:combos_publico_lectura:SELECT",
                "combos_componentes:combos_componentes_publico_lectura:SELECT",
                "empresas:empresas_publico_lectura:SELECT",
                "imagenes_articulo:imagenes_articulo_publico_lectura:SELECT",
                "listas_precio:listas_precio_publico_lectura:SELECT",
                "ofertas:ofertas_publico_lectura:SELECT",
                "ofertas_listas:ofertas_listas_publico_lectura:SELECT",
                "precios:precios_publico_lectura:SELECT",
                "puntos_venta:puntos_venta_publico_lectura:SELECT",
                "vitrinas:vitrinas_publico_lectura:SELECT"
            ],
            policies.Order(StringComparer.Ordinal));
    }

    // ---- el contexto de EF ------------------------------------------------------------------------

    /// <summary>El interceptor deja el tenant en <c>app.tenant_publico</c> y el estándar vacío. Con el
    /// mutante (tenant también en <c>app.tenant_id</c>) las policies estándar abren stock, códigos de barra,
    /// clientes y usuarios, y las cuatro cuentas de abajo dejan de ser cero.</summary>
    [Fact]
    public async Task ElContextoPublicoDeEfLlevaElTenantEnElGucPublicoYNoVeNadaFueraDeLasTresTablas()
    {
        var e = await SembrarEscenarioAsync("rls-ef");
        await SembrarArticuloYClienteAsync(e.A.IdTenant, e.PuntosVentaDeA[0]);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Publico, e.A.IdTenant));

        var gucs = await db.Database
            .SqlQueryRaw<string>(
                "SELECT current_setting('app.acceso', true) || '|' || current_setting('app.tenant_id', true) || '|' || " +
                "current_setting('app.tenant_publico', true) AS \"Value\"")
            .SingleAsync();
        Assert.Equal($"publico||{e.A.IdTenant}", gucs);

        Assert.Equal(0, await db.Stock.IgnoreQueryFilters().CountAsync());
        Assert.Equal(0, await db.CodigosBarra.IgnoreQueryFilters().CountAsync());
        Assert.Equal(0, await db.Clientes.IgnoreQueryFilters().CountAsync());
        Assert.Equal(0, await db.Usuarios.IgnoreQueryFilters().CountAsync());

        // Las tres tablas sí, por RLS (sin el filtro de EF) y con él.
        int[] vitrinasDeA = [e.VitrinaPublicadaDeA];
        int[] empresasDeA = [.. e.EmpresasDeA.Order()];
        int[] puntosVentaDeA = [.. e.PuntosVentaDeA.Order()];

        Assert.Equal(
            vitrinasDeA,
            await db.Vitrinas.IgnoreQueryFilters(["Tenant", "BajaLogica"]).Select(v => v.Id).ToArrayAsync());
        Assert.Equal(
            empresasDeA,
            await db.Empresas.IgnoreQueryFilters(["Tenant"]).Select(x => x.Id).OrderBy(id => id).ToArrayAsync());
        Assert.Equal(
            puntosVentaDeA,
            await db.PuntosVenta.IgnoreQueryFilters(["Tenant"]).Select(x => x.Id).OrderBy(id => id).ToArrayAsync());
        Assert.Equal(vitrinasDeA, await db.Vitrinas.Select(v => v.Id).ToArrayAsync());
    }

    /// <summary>El reaplicado sobre una conexión ya abierta (<c>Suplantar</c>) usa los mismos tres valores
    /// que el interceptor.</summary>
    [Fact]
    public async Task ElReaplicadoSobreUnaConexionAbiertaLlevaElTenantPublicoEnSuGuc()
    {
        var e = await SembrarEscenarioAsync("rls-reaplicar");
        var sesion = new TenantActualDeSesion();
        sesion.Establecer(ModoDeAcceso.Publico, e.A.IdTenant);

        await using var conexion = new NpgsqlConnection(fixture.AppConnectionString);
        await conexion.OpenAsync();
        await using var transaccion = await conexion.BeginTransactionAsync();

        await sesion.ReaplicarSobreConexionAsync(conexion);

        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "SELECT current_setting('app.acceso', true) || '|' || current_setting('app.tenant_id', true) || '|' || " +
            "current_setting('app.tenant_publico', true)";

        Assert.Equal($"publico||{e.A.IdTenant}", (string)(await comando.ExecuteScalarAsync())!);
    }

    // ---- constraints de la tabla ------------------------------------------------------------------

    [Fact]
    public async Task ElIndiceDePublicadaRechazaUnaSegundaVitrinaPublicadaViva23505()
    {
        var comercio = await apoyo.SembrarComercioAsync("sql-publicada");
        var segunda = await apoyo.SembrarEmpresaAsync(comercio.IdTenant, "Segunda SA");
        await InsertarCrudoAsync(comercio.IdTenant, comercio.IdEmpresa, publicada: true);

        var error = await Assert.ThrowsAsync<PostgresException>(
            () => InsertarCrudoAsync(comercio.IdTenant, segunda, publicada: true));

        Assert.Equal("23505", error.SqlState);
        Assert.Equal("ux_vitrinas_publicada_por_tenant", error.ConstraintName);

        // Sin publicar no cuenta, y la publicada y dada de baja tampoco ocupa el lugar.
        await InsertarCrudoAsync(comercio.IdTenant, segunda, publicada: false);
    }

    [Fact]
    public async Task UnaPublicadaDadaDeBajaNoOcupaElLugarYOtroTenantPublicaLaSuya()
    {
        var a = await apoyo.SembrarComercioAsync("sql-publicada-baja-a");
        var b = await apoyo.SembrarComercioAsync("sql-publicada-baja-b");
        var segundaDeA = await apoyo.SembrarEmpresaAsync(a.IdTenant, "Segunda SA");
        await InsertarCrudoAsync(a.IdTenant, a.IdEmpresa, publicada: true, eliminada: true);

        await InsertarCrudoAsync(a.IdTenant, segundaDeA, publicada: true);
        await InsertarCrudoAsync(b.IdTenant, b.IdEmpresa, publicada: true);
    }

    [Fact]
    public async Task ElIndiceDeEmpresaRechazaUnaSegundaVitrinaViva23505YLaBajaLiberaLaEmpresa()
    {
        var comercio = await apoyo.SembrarComercioAsync("sql-empresa");
        await InsertarCrudoAsync(comercio.IdTenant, comercio.IdEmpresa);

        var error = await Assert.ThrowsAsync<PostgresException>(
            () => InsertarCrudoAsync(comercio.IdTenant, comercio.IdEmpresa));

        Assert.Equal("23505", error.SqlState);
        Assert.Equal("ux_vitrinas_empresa", error.ConstraintName);

        var otro = await apoyo.SembrarComercioAsync("sql-empresa-baja");
        await InsertarCrudoAsync(otro.IdTenant, otro.IdEmpresa, eliminada: true);
        await InsertarCrudoAsync(otro.IdTenant, otro.IdEmpresa);
    }

    [Fact]
    public async Task LaFkCompuestaRechazaUnaVitrinaDeUnTenantSobreLaEmpresaDeOtro23503()
    {
        var a = await apoyo.SembrarComercioAsync("sql-fk-a");
        var b = await apoyo.SembrarComercioAsync("sql-fk-b");

        var error = await Assert.ThrowsAsync<PostgresException>(
            () => InsertarCrudoAsync(b.IdTenant, a.IdEmpresa));

        Assert.Equal("23503", error.SqlState);
        Assert.Equal("fk_vitrinas_empresa", error.ConstraintName);
    }

    public static TheoryData<string, string> ChecksQueRechazan() => new()
    {
        { "ck_vitrinas_tema", "tema-desconocido" },
        { "ck_vitrinas_tema", "tema-mayuscula" },
        { "ck_vitrinas_color_acento", "color-mayuscula" },
        { "ck_vitrinas_color_acento", "color-sin-numeral" },
        { "ck_vitrinas_color_acento", "color-corto" },
        { "ck_vitrinas_tagline_largo", "tagline-121" },
        { "ck_vitrinas_descripcion_largo", "descripcion-2001" },
        { "ck_vitrinas_logo", "logo-sin-tipo" },
        { "ck_vitrinas_logo", "logo-sin-marca" },
        { "ck_vitrinas_logo", "tipo-sin-logo" },
        { "ck_vitrinas_logo", "marca-sin-logo" },
        { "ck_vitrinas_logo", "logo-vacio" },
        { "ck_vitrinas_logo", "logo-de-mas-de-1mb" },
        { "ck_vitrinas_logo", "logo-svg" }
    };

    [Theory]
    [MemberData(nameof(ChecksQueRechazan))]
    public async Task LosChecksDeLaTablaRechazanLoQueLaValidacionDeDominioYaRechaza(string constraint, string caso)
    {
        var comercio = await apoyo.SembrarComercioAsync($"sql-{caso}");
        var ahora = DateTimeOffset.UtcNow;
        var png = ApoyoDeVitrinas.Imagen(Vitrina.TipoPng);

        var error = await Assert.ThrowsAsync<PostgresException>(() => caso switch
        {
            "tema-desconocido" => InsertarCrudoAsync(comercio.IdTenant, comercio.IdEmpresa, tema: "gris"),
            "tema-mayuscula" => InsertarCrudoAsync(comercio.IdTenant, comercio.IdEmpresa, tema: "Claro"),
            "color-mayuscula" => InsertarCrudoAsync(comercio.IdTenant, comercio.IdEmpresa, colorAcento: "#F7991C"),
            "color-sin-numeral" => InsertarCrudoAsync(comercio.IdTenant, comercio.IdEmpresa, colorAcento: "f7991c"),
            "color-corto" => InsertarCrudoAsync(comercio.IdTenant, comercio.IdEmpresa, colorAcento: "#fff"),
            "tagline-121" => InsertarCrudoAsync(comercio.IdTenant, comercio.IdEmpresa, tagline: new string('a', 121)),
            "descripcion-2001" => InsertarCrudoAsync(comercio.IdTenant, comercio.IdEmpresa, descripcion: new string('a', 2001)),
            "logo-sin-tipo" => InsertarCrudoAsync(comercio.IdTenant, comercio.IdEmpresa, logo: png, actualizadoEn: ahora),
            "logo-sin-marca" => InsertarCrudoAsync(comercio.IdTenant, comercio.IdEmpresa, logo: png, tipoDeLogo: Vitrina.TipoPng),
            "tipo-sin-logo" => InsertarCrudoAsync(comercio.IdTenant, comercio.IdEmpresa, tipoDeLogo: Vitrina.TipoPng),
            "marca-sin-logo" => InsertarCrudoAsync(comercio.IdTenant, comercio.IdEmpresa, actualizadoEn: ahora),
            "logo-vacio" => InsertarCrudoAsync(
                comercio.IdTenant, comercio.IdEmpresa, logo: [], tipoDeLogo: Vitrina.TipoPng, actualizadoEn: ahora),
            "logo-de-mas-de-1mb" => InsertarCrudoAsync(
                comercio.IdTenant, comercio.IdEmpresa, logo: new byte[Vitrina.TamanoMaximoDelLogo + 1],
                tipoDeLogo: Vitrina.TipoPng, actualizadoEn: ahora),
            _ => InsertarCrudoAsync(
                comercio.IdTenant, comercio.IdEmpresa, logo: png, tipoDeLogo: "image/svg+xml", actualizadoEn: ahora)
        });

        Assert.Equal("23514", error.SqlState);
        Assert.Equal(constraint, error.ConstraintName);
    }

    [Fact]
    public async Task LosChecksAceptanLosValoresEnElLimiteYElLogoCompleto()
    {
        var comercio = await apoyo.SembrarComercioAsync("sql-limites");

        await InsertarCrudoAsync(
            comercio.IdTenant, comercio.IdEmpresa, tema: "claro", colorAcento: "#f7991c",
            tagline: new string('a', 120), descripcion: new string('a', 2000),
            logo: new byte[Vitrina.TamanoMaximoDelLogo], tipoDeLogo: Vitrina.TipoWebp, actualizadoEn: DateTimeOffset.UtcNow);

        Assert.Equal(Vitrina.TamanoMaximoDelLogo, (await apoyo.LeerVitrinaAsync(comercio.IdEmpresa))!.Logo!.Length);
    }

    // ---- apoyo ------------------------------------------------------------------------------------

    private async Task InsertarCrudoAsync(
        int idTenant,
        int idEmpresa,
        bool publicada = false,
        string? tagline = null,
        string? descripcion = null,
        string? colorAcento = null,
        string tema = "oscuro",
        byte[]? logo = null,
        string? tipoDeLogo = null,
        DateTimeOffset? actualizadoEn = null,
        bool eliminada = false)
    {
        await using var conexion = await fixture.AbrirConexionCrudaAsync("plataforma", idTenant: null);
        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            "INSERT INTO vitrinas (id_tenant, id_empresa, publicada, tagline, descripcion, color_acento, tema, " +
            "logo, logo_content_type, logo_actualizado_en, created_at, updated_at, deleted_at) " +
            "VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, now(), now(), CASE WHEN $11 THEN now() END)";
        comando.Parameters.Add(new NpgsqlParameter { Value = idTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = idEmpresa });
        comando.Parameters.Add(new NpgsqlParameter { Value = publicada });
        comando.Parameters.Add(new NpgsqlParameter { Value = (object?)tagline ?? DBNull.Value });
        comando.Parameters.Add(new NpgsqlParameter { Value = (object?)descripcion ?? DBNull.Value });
        comando.Parameters.Add(new NpgsqlParameter { Value = (object?)colorAcento ?? DBNull.Value });
        comando.Parameters.Add(new NpgsqlParameter { Value = tema });
        comando.Parameters.Add(
            new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bytea, Value = (object?)logo ?? DBNull.Value });
        comando.Parameters.Add(new NpgsqlParameter { Value = (object?)tipoDeLogo ?? DBNull.Value });
        comando.Parameters.Add(new NpgsqlParameter { Value = (object?)actualizadoEn?.ToUniversalTime() ?? DBNull.Value });
        comando.Parameters.Add(new NpgsqlParameter { Value = eliminada });
        await comando.ExecuteNonQueryAsync();
    }

    /// <summary>Un artículo, un cliente, un código de barras y stock del tenant: filas en tablas que el modo
    /// público no puede leer.</summary>
    private async Task SembrarArticuloYClienteAsync(int idTenant, int idPuntoVenta)
    {
        var ahora = DateTimeOffset.UtcNow;

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var area = new Area
        {
            IdTenant = idTenant,
            Nombre = $"Área {Guid.NewGuid():N}",
            Orden = 1,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        var lista = new ListaPrecio
        {
            IdTenant = idTenant,
            Nombre = $"Lista {Guid.NewGuid():N}",
            EsDefault = false,
            Modo = ModoLista.Fija,
            Activo = true,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Areas.Add(area);
        db.ListasPrecio.Add(lista);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();
        var idCondicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync();

        var articulo = new Articulo
        {
            IdTenant = idTenant,
            CodigoInterno = $"V-{Guid.NewGuid():N}",
            Nombre = "Artículo privado",
            IdArea = area.Id,
            IdAlicuotaIva = idAlicuotaIva,
            UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        db.Clientes.Add(new Cliente
        {
            IdTenant = idTenant,
            Numero = 10_000 + Random.Shared.Next(1, 1_000_000),
            Nombre = "Cliente privado",
            IdCondicionFiscal = idCondicionFiscal,
            IdListaPrecio = lista.Id,
            Activo = true,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        db.CodigosBarra.Add(new CodigoBarra
        {
            IdTenant = idTenant,
            IdArticulo = articulo.Id,
            Codigo = $"779{Random.Shared.NextInt64(1_000_000_000L, 9_999_999_999L)}",
            Activo = true,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        db.Stock.Add(new Ways.Domain.Stock.Stock
        {
            IdTenant = idTenant,
            IdArticulo = articulo.Id,
            IdPuntoVenta = idPuntoVenta,
            Cantidad = 7m
        });
        await db.SaveChangesAsync();
    }
}
