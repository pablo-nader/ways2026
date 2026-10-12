using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Bajas;
using Ways.Application.Organizacion;
using Ways.Application.Vitrinas;
using Ways.Domain.Common;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// Administración de la vitrina de una empresa: upsert con los cinco campos persistidos, 404 antes que
/// cualquier validación, quién puede, la unicidad de "una publicada por tenant" (con su carrera), y el
/// logo (tipo por contenido, tamaño y borrado).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class VitrinaAdminTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private readonly ApoyoDeVitrinas apoyo = new(fixture);

    private static string Ruta(int idEmpresa) => $"/api/empresas/{idEmpresa}/vitrina";

    private static string RutaLogo(int idEmpresa) => $"{Ruta(idEmpresa)}/logo";

    private static Dictionary<string, object?> Cuerpo(
        bool? publicada = false,
        string? tagline = null,
        string? descripcion = null,
        string? colorAcento = null,
        string? tema = "oscuro")
    {
        var cuerpo = new Dictionary<string, object?>
        {
            ["tagline"] = tagline,
            ["descripcion"] = descripcion,
            ["colorAcento"] = colorAcento,
            ["tema"] = tema
        };

        if (publicada is not null)
        {
            cuerpo["publicada"] = publicada;
        }

        return cuerpo;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage respuesta) =>
        await ApoyoDeDominios.LeerJsonAsync(respuesta);

    // ---- leer y guardar ---------------------------------------------------------------------------

    [Fact]
    public async Task UnaEmpresaSinVitrinaDevuelveLaFormaPorDefectoConExisteEnFalseYLeerNoCreaNada()
    {
        var comercio = await apoyo.SembrarComercioAsync("admin-defecto");
        using var admin = await apoyo.ClienteAdminAsync(comercio);

        var respuesta = await admin.GetAsync(Ruta(comercio.IdEmpresa));

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var vitrina = await JsonAsync(respuesta);
        Assert.False(vitrina.GetProperty("existe").GetBoolean());
        Assert.Equal(comercio.IdEmpresa, vitrina.GetProperty("idEmpresa").GetInt32());
        Assert.False(vitrina.GetProperty("publicada").GetBoolean());
        Assert.Equal(JsonValueKind.Null, vitrina.GetProperty("tagline").ValueKind);
        Assert.Equal(JsonValueKind.Null, vitrina.GetProperty("descripcion").ValueKind);
        Assert.Equal(JsonValueKind.Null, vitrina.GetProperty("colorAcento").ValueKind);
        Assert.Equal("oscuro", vitrina.GetProperty("tema").GetString());
        Assert.False(vitrina.GetProperty("tieneLogo").GetBoolean());
        Assert.Equal(JsonValueKind.Null, vitrina.GetProperty("logoVersion").ValueKind);

        Assert.Equal(0, await apoyo.ContarVitrinasAsync(comercio.IdTenant));
    }

    [Fact]
    public async Task ElPutCreaLaVitrinaYElSiguienteLaReemplazaConLosCincoCamposPersistidos()
    {
        var comercio = await apoyo.SembrarComercioAsync("admin-upsert");
        using var admin = await apoyo.ClienteAdminAsync(comercio);

        var alta = await admin.PutAsJsonAsync(
            Ruta(comercio.IdEmpresa),
            Cuerpo(
                publicada: true, tagline: "  Todo cerca tuyo  ", descripcion: "Primera línea\nSegunda línea",
                colorAcento: "#AABBCC", tema: "CLARO"));

        Assert.Equal(HttpStatusCode.OK, alta.StatusCode);
        var creada = await JsonAsync(alta);
        Assert.True(creada.GetProperty("existe").GetBoolean());
        Assert.True(creada.GetProperty("publicada").GetBoolean());
        Assert.Equal("Todo cerca tuyo", creada.GetProperty("tagline").GetString());
        Assert.Equal("Primera línea\nSegunda línea", creada.GetProperty("descripcion").GetString());
        Assert.Equal("#aabbcc", creada.GetProperty("colorAcento").GetString());
        Assert.Equal("claro", creada.GetProperty("tema").GetString());

        var enBase = (await apoyo.LeerVitrinaAsync(comercio.IdEmpresa))!;
        Assert.Equal(comercio.IdTenant, enBase.IdTenant);
        Assert.True(enBase.Publicada);
        Assert.Equal("Todo cerca tuyo", enBase.Tagline);
        Assert.Equal("Primera línea\nSegunda línea", enBase.Descripcion);
        Assert.Equal("#aabbcc", enBase.ColorAcento);
        Assert.Equal("claro", enBase.Tema);
        Assert.Null(enBase.Logo);

        // El reemplazo cambia los cinco campos, uno por uno y todos distintos de antes.
        var reemplazo = await admin.PutAsJsonAsync(
            Ruta(comercio.IdEmpresa),
            Cuerpo(
                publicada: false, tagline: "Otro slogan", descripcion: "Otra descripción",
                colorAcento: "#010203", tema: "oscuro"));

        Assert.Equal(HttpStatusCode.OK, reemplazo.StatusCode);
        var leida = await JsonAsync(await admin.GetAsync(Ruta(comercio.IdEmpresa)));
        Assert.True(leida.GetProperty("existe").GetBoolean());
        Assert.False(leida.GetProperty("publicada").GetBoolean());
        Assert.Equal("Otro slogan", leida.GetProperty("tagline").GetString());
        Assert.Equal("Otra descripción", leida.GetProperty("descripcion").GetString());
        Assert.Equal("#010203", leida.GetProperty("colorAcento").GetString());
        Assert.Equal("oscuro", leida.GetProperty("tema").GetString());

        var reemplazada = (await apoyo.LeerVitrinaAsync(comercio.IdEmpresa))!;
        Assert.Equal(enBase.Id, reemplazada.Id);
        Assert.Equal(1, await apoyo.ContarVitrinasAsync(comercio.IdTenant));
        Assert.False(reemplazada.Publicada);
        Assert.Equal("Otro slogan", reemplazada.Tagline);
        Assert.Equal("Otra descripción", reemplazada.Descripcion);
        Assert.Equal("#010203", reemplazada.ColorAcento);
        Assert.Equal("oscuro", reemplazada.Tema);
        Assert.Equal(enBase.CreatedAt, reemplazada.CreatedAt);
        Assert.True(reemplazada.UpdatedAt > enBase.UpdatedAt);
    }

    [Fact]
    public async Task UnTextoVacioOSoloEspaciosQuedaSinValor()
    {
        var comercio = await apoyo.SembrarComercioAsync("admin-vacios");
        await apoyo.SembrarVitrinaAsync(
            comercio.IdTenant, comercio.IdEmpresa, publicada: false, tagline: "Antes", descripcion: "Antes",
            colorAcento: "#ffffff");
        using var admin = await apoyo.ClienteAdminAsync(comercio);

        var respuesta = await admin.PutAsJsonAsync(
            Ruta(comercio.IdEmpresa), Cuerpo(tagline: "   ", descripcion: "", colorAcento: "  "));

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var enBase = (await apoyo.LeerVitrinaAsync(comercio.IdEmpresa))!;
        Assert.Null(enBase.Tagline);
        Assert.Null(enBase.Descripcion);
        Assert.Null(enBase.ColorAcento);
    }

    [Fact]
    public async Task LosTextosDeExactamenteElLargoMaximoSeAceptan()
    {
        var comercio = await apoyo.SembrarComercioAsync("admin-maximos");
        using var admin = await apoyo.ClienteAdminAsync(comercio);
        var tagline = new string('t', Vitrina.LargoMaximoDelTagline);
        var descripcion = new string('d', Vitrina.LargoMaximoDeLaDescripcion);

        var respuesta = await admin.PutAsJsonAsync(Ruta(comercio.IdEmpresa), Cuerpo(tagline: tagline, descripcion: descripcion));

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var enBase = (await apoyo.LeerVitrinaAsync(comercio.IdEmpresa))!;
        Assert.Equal(tagline, enBase.Tagline);
        Assert.Equal(descripcion, enBase.Descripcion);
    }

    [Fact]
    public async Task GuardarLosTextosNoTocaElLogoYSubirElLogoNoTocaLosTextos()
    {
        var comercio = await apoyo.SembrarComercioAsync("admin-logo-aparte");
        var logo = ApoyoDeVitrinas.Imagen(Vitrina.TipoPng, relleno: 5);
        await apoyo.SembrarVitrinaAsync(
            comercio.IdTenant, comercio.IdEmpresa, publicada: false, tagline: "Con logo", logo: logo);
        var antes = (await apoyo.LeerVitrinaAsync(comercio.IdEmpresa))!;
        using var admin = await apoyo.ClienteAdminAsync(comercio);

        var guardar = await admin.PutAsJsonAsync(Ruta(comercio.IdEmpresa), Cuerpo(tagline: "Texto nuevo"));
        Assert.Equal(HttpStatusCode.OK, guardar.StatusCode);
        Assert.True((await JsonAsync(guardar)).GetProperty("tieneLogo").GetBoolean());

        var despuesDelTexto = (await apoyo.LeerVitrinaAsync(comercio.IdEmpresa))!;
        Assert.Equal(logo, despuesDelTexto.Logo);
        Assert.Equal(antes.LogoActualizadoEn, despuesDelTexto.LogoActualizadoEn);
        Assert.Equal(Vitrina.TipoPng, despuesDelTexto.LogoContentType);

        var nuevoLogo = ApoyoDeVitrinas.Imagen(Vitrina.TipoJpeg, relleno: 6);
        var subir = await admin.PutAsync(RutaLogo(comercio.IdEmpresa), ApoyoDeVitrinas.Contenido(nuevoLogo, Vitrina.TipoJpeg));
        Assert.Equal(HttpStatusCode.OK, subir.StatusCode);

        var despuesDelLogo = (await apoyo.LeerVitrinaAsync(comercio.IdEmpresa))!;
        Assert.Equal("Texto nuevo", despuesDelLogo.Tagline);
        Assert.Equal(nuevoLogo, despuesDelLogo.Logo);
    }

    // ---- 404 primero ------------------------------------------------------------------------------

    public static TheoryData<string, string> OperacionesPorEmpresaQueNoSeVe()
    {
        var datos = new TheoryData<string, string>();

        foreach (var empresa in new[] { "inexistente", "dada-de-baja", "de-otro-tenant" })
        {
            foreach (var operacion in new[] { "get", "put", "put-logo", "delete-logo", "get-logo", "delete" })
            {
                datos.Add(empresa, operacion);
            }
        }

        return datos;
    }

    /// <summary>Cada operación apunta a una empresa que el admin no ve (no existe, está dada de baja o es de
    /// otro tenant) con el cuerpo MÁS inválido posible: tiene que ganar el 404. Un PUT con cuerpo válido
    /// no probaría la precedencia, y el del logo manda un tipo no admitido y más de 1 MB.</summary>
    [Theory]
    [MemberData(nameof(OperacionesPorEmpresaQueNoSeVe))]
    public async Task UnaEmpresaQueElAdminNoVeDa404NoEncontradoAntesQueCualquierValidacion(string empresa, string operacion)
    {
        var propio = await apoyo.SembrarComercioAsync("admin-404");
        var ajeno = await apoyo.SembrarComercioAsync("admin-404-ajeno");

        var idEmpresa = empresa switch
        {
            "inexistente" => 987_654_321,
            "dada-de-baja" => await apoyo.SembrarEmpresaAsync(propio.IdTenant, "Eliminada SA", eliminada: true),
            _ => ajeno.IdEmpresa
        };

        // La vitrina de la empresa ajena existe y está publicada: el 404 no puede delatarla ni tocarla.
        if (empresa == "de-otro-tenant")
        {
            await apoyo.SembrarVitrinaAsync(
                ajeno.IdTenant, ajeno.IdEmpresa, publicada: true, tagline: "Ajena", logo: ApoyoDeVitrinas.Imagen(Vitrina.TipoPng));
        }

        using var admin = await apoyo.ClienteAdminAsync(propio);

        var invalido = Cuerpo(publicada: null, tagline: new string('x', 500), colorAcento: "no-es-un-color", tema: "gris");
        var grande = new byte[Vitrina.TamanoMaximoDelLogo + 1000];

        var respuesta = operacion switch
        {
            "get" => await admin.GetAsync(Ruta(idEmpresa)),
            "put" => await admin.PutAsJsonAsync(Ruta(idEmpresa), invalido),
            "put-logo" => await admin.PutAsync(RutaLogo(idEmpresa), ApoyoDeVitrinas.Contenido(grande, "text/plain")),
            "get-logo" => await admin.GetAsync(RutaLogo(idEmpresa)),
            "delete" => await admin.DeleteAsync(Ruta(idEmpresa)),
            _ => await admin.DeleteAsync(RutaLogo(idEmpresa))
        };

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        Assert.Equal("no_encontrado", await ApoyoDeDominios.CodigoDeErrorAsync(respuesta));

        if (empresa == "de-otro-tenant")
        {
            var ajena = (await apoyo.LeerVitrinaAsync(ajeno.IdEmpresa))!;
            Assert.Equal("Ajena", ajena.Tagline);
            Assert.Null(ajena.DeletedAt);
        }
        else
        {
            Assert.Null(await apoyo.LeerVitrinaAsync(idEmpresa));
        }
    }

    // ---- validación del cuerpo --------------------------------------------------------------------

    public static TheoryData<string, string> CuerposInvalidos() => new()
    {
        { "color_invalido", "color-sin-numeral" },
        { "color_invalido", "color-corto" },
        { "tema_invalido", "tema-desconocido" },
        { "tema_invalido", "tema-ausente" },
        { "texto_demasiado_largo", "tagline-largo" },
        { "texto_demasiado_largo", "descripcion-larga" },
        { "publicada_requerida", "publicada-ausente" }
    };

    [Theory]
    [MemberData(nameof(CuerposInvalidos))]
    public async Task UnCuerpoInvalidoDa400ConSuCodigoYNoCambiaNada(string codigo, string caso)
    {
        var comercio = await apoyo.SembrarComercioAsync("admin-400");
        await apoyo.SembrarVitrinaAsync(
            comercio.IdTenant, comercio.IdEmpresa, publicada: false, tagline: "Intacta", tema: Vitrina.TemaClaro);
        var antes = (await apoyo.LeerVitrinaAsync(comercio.IdEmpresa))!;
        using var admin = await apoyo.ClienteAdminAsync(comercio);

        var cuerpo = caso switch
        {
            "color-sin-numeral" => Cuerpo(publicada: true, colorAcento: "f7991c"),
            "color-corto" => Cuerpo(publicada: true, colorAcento: "#fff"),
            "tema-desconocido" => Cuerpo(publicada: true, tema: "gris"),
            "tema-ausente" => Cuerpo(publicada: true, tema: null),
            "tagline-largo" => Cuerpo(publicada: true, tagline: new string('a', Vitrina.LargoMaximoDelTagline + 1)),
            "descripcion-larga" => Cuerpo(
                publicada: true, descripcion: new string('a', Vitrina.LargoMaximoDeLaDescripcion + 1)),
            _ => Cuerpo(publicada: null)
        };

        var respuesta = await admin.PutAsJsonAsync(Ruta(comercio.IdEmpresa), cuerpo);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal(codigo, await ApoyoDeDominios.CodigoDeErrorAsync(respuesta));

        var despues = (await apoyo.LeerVitrinaAsync(comercio.IdEmpresa))!;
        Assert.False(despues.Publicada);
        Assert.Equal("Intacta", despues.Tagline);
        Assert.Equal(Vitrina.TemaClaro, despues.Tema);
        Assert.Equal(antes.UpdatedAt, despues.UpdatedAt);
    }

    // ---- quién puede ------------------------------------------------------------------------------

    [Fact]
    public async Task SinSesionDa401YSoloElAdminYLaPlataformaPuedenAdministrarLaVitrina()
    {
        var comercio = await apoyo.SembrarComercioAsync("admin-roles");
        await apoyo.SembrarVitrinaAsync(
            comercio.IdTenant, comercio.IdEmpresa, publicada: false, tagline: "Intacta",
            logo: ApoyoDeVitrinas.Imagen(Vitrina.TipoPng));
        var supervisor = await apoyo.SembrarUsuarioAsync(comercio.IdTenant, RolConocido.Supervisor, "supervisor");
        var vendedor = await apoyo.SembrarUsuarioAsync(comercio.IdTenant, RolConocido.Vendedor, "vendedor");

        HttpRequestMessage[] Solicitudes() =>
        [
            new(HttpMethod.Get, Ruta(comercio.IdEmpresa)),
            new(HttpMethod.Put, Ruta(comercio.IdEmpresa)) { Content = JsonContent.Create(Cuerpo(publicada: true, tagline: "Hackeada")) },
            new(HttpMethod.Put, RutaLogo(comercio.IdEmpresa))
            {
                Content = ApoyoDeVitrinas.Contenido(ApoyoDeVitrinas.Imagen(Vitrina.TipoPng, relleno: 99), Vitrina.TipoPng)
            },
            new(HttpMethod.Delete, RutaLogo(comercio.IdEmpresa))
        ];

        using var anonimo = fixture.CreateClient();
        foreach (var solicitud in Solicitudes())
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.SendAsync(solicitud)).StatusCode);
        }

        foreach (var (mail, password) in new[] { supervisor, vendedor })
        {
            using var sinPermiso = await apoyo.ClienteConSesionAsync(mail, password);

            foreach (var solicitud in Solicitudes())
            {
                var respuesta = await sinPermiso.SendAsync(solicitud);

                Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
            }
        }

        var intacta = (await apoyo.LeerVitrinaAsync(comercio.IdEmpresa))!;
        Assert.False(intacta.Publicada);
        Assert.Equal("Intacta", intacta.Tagline);
        Assert.Equal(ApoyoDeVitrinas.Imagen(Vitrina.TipoPng), intacta.Logo);

        // El admin hace las cuatro operaciones: es el rol para el que existen estas rutas.
        using var admin = await apoyo.ClienteAdminAsync(comercio);
        foreach (var solicitud in Solicitudes())
        {
            Assert.True((await admin.SendAsync(solicitud)).IsSuccessStatusCode, solicitud.RequestUri!.ToString());
        }

        Assert.Equal("Hackeada", (await apoyo.LeerVitrinaAsync(comercio.IdEmpresa))!.Tagline);
    }

    [Fact]
    public async Task LaPlataformaTambienAdministraLaVitrinaYLaFilaQuedaEnElTenantDeLaEmpresa()
    {
        var comercio = await apoyo.SembrarComercioAsync("admin-root");
        using var root = await apoyo.Dominios.ClienteRootAsync();

        var guardar = await root.PutAsJsonAsync(Ruta(comercio.IdEmpresa), Cuerpo(publicada: true, tagline: "Desde root"));
        Assert.Equal(HttpStatusCode.OK, guardar.StatusCode);

        var logo = await root.PutAsync(
            RutaLogo(comercio.IdEmpresa), ApoyoDeVitrinas.Contenido(ApoyoDeVitrinas.Imagen(Vitrina.TipoWebp), Vitrina.TipoWebp));
        Assert.Equal(HttpStatusCode.OK, logo.StatusCode);

        var enBase = (await apoyo.LeerVitrinaAsync(comercio.IdEmpresa))!;
        Assert.Equal(comercio.IdTenant, enBase.IdTenant);
        Assert.Equal("Desde root", enBase.Tagline);
        Assert.Equal(Vitrina.TipoWebp, enBase.LogoContentType);

        Assert.Equal(HttpStatusCode.NoContent, (await root.DeleteAsync(RutaLogo(comercio.IdEmpresa))).StatusCode);
        Assert.Null((await apoyo.LeerVitrinaAsync(comercio.IdEmpresa))!.Logo);
    }

    // ---- una publicada por tenant -----------------------------------------------------------------

    [Fact]
    public async Task PublicarUnaSegundaEmpresaDelTenantDa409VitrinaYaPublicadaYNoDejaNingunaFila()
    {
        var comercio = await apoyo.SembrarComercioAsync("admin-409");
        var segunda = await apoyo.SembrarEmpresaAsync(comercio.IdTenant, "Segunda SA", "Segunda");
        var otro = await apoyo.SembrarComercioAsync("admin-409-otro");
        using var admin = await apoyo.ClienteAdminAsync(comercio);
        using var adminOtro = await apoyo.ClienteAdminAsync(otro);

        Assert.Equal(
            HttpStatusCode.OK,
            (await admin.PutAsJsonAsync(Ruta(comercio.IdEmpresa), Cuerpo(publicada: true, tagline: "Primera"))).StatusCode);

        var conflicto = await admin.PutAsJsonAsync(Ruta(segunda), Cuerpo(publicada: true, tagline: "Segunda"));

        Assert.Equal(HttpStatusCode.Conflict, conflicto.StatusCode);
        Assert.Equal("vitrina_ya_publicada", await ApoyoDeDominios.CodigoDeErrorAsync(conflicto));
        Assert.Null(await apoyo.LeerVitrinaAsync(segunda));
        Assert.True((await apoyo.LeerVitrinaAsync(comercio.IdEmpresa))!.Publicada);

        // Guardar la segunda SIN publicar sí se puede, y publicarla ya publicada la primera sigue chocando.
        Assert.Equal(
            HttpStatusCode.OK,
            (await admin.PutAsJsonAsync(Ruta(segunda), Cuerpo(publicada: false, tagline: "Segunda"))).StatusCode);
        var otraVez = await admin.PutAsJsonAsync(Ruta(segunda), Cuerpo(publicada: true, tagline: "Segunda"));
        Assert.Equal(HttpStatusCode.Conflict, otraVez.StatusCode);
        Assert.False((await apoyo.LeerVitrinaAsync(segunda))!.Publicada);

        // Otro tenant publica la suya sin problema: el límite es por tenant.
        Assert.Equal(
            HttpStatusCode.OK,
            (await adminOtro.PutAsJsonAsync(Ruta(otro.IdEmpresa), Cuerpo(publicada: true, tagline: "De otro"))).StatusCode);

        // Despublicar la primera libera el lugar, y volver a guardarla publicada no choca consigo misma.
        Assert.Equal(
            HttpStatusCode.OK,
            (await admin.PutAsJsonAsync(Ruta(comercio.IdEmpresa), Cuerpo(publicada: false, tagline: "Primera"))).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await admin.PutAsJsonAsync(Ruta(segunda), Cuerpo(publicada: true, tagline: "Segunda"))).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await admin.PutAsJsonAsync(Ruta(segunda), Cuerpo(publicada: true, tagline: "Segunda editada"))).StatusCode);
    }

    [Fact]
    public async Task UnaVitrinaPublicadaYDadaDeBajaNoOcupaElLugarDeLaPublicada()
    {
        var comercio = await apoyo.SembrarComercioAsync("admin-409-baja");
        var segunda = await apoyo.SembrarEmpresaAsync(comercio.IdTenant, "Segunda SA");
        await apoyo.SembrarVitrinaAsync(comercio.IdTenant, comercio.IdEmpresa, publicada: true, eliminada: true);
        using var admin = await apoyo.ClienteAdminAsync(comercio);

        var respuesta = await admin.PutAsJsonAsync(Ruta(segunda), Cuerpo(publicada: true));

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }

    /// <summary>
    /// Dos publicaciones de empresas distintas del mismo tenant en la misma ventana. Un tercero tiene el
    /// lock de tenant y las dos requests quedan esperándolo (se espera a verlas en <c>pg_locks</c>, sin
    /// dormir); al soltarlo corren una detrás de la otra y la segunda choca contra
    /// <c>ux_vitrinas_publicada_por_tenant</c>. Sin la traducción de <c>ManejadorDeErrores</c> sería un 500.
    /// </summary>
    [Fact]
    public async Task DosPublicacionesSimultaneasDelMismoTenantDanUnOkYUn409VitrinaYaPublicada()
    {
        var comercio = await apoyo.SembrarComercioAsync("admin-carrera");
        var segunda = await apoyo.SembrarEmpresaAsync(comercio.IdTenant, "Segunda SA");
        using var adminA = await apoyo.ClienteAdminAsync(comercio);
        using var adminB = await apoyo.ClienteAdminAsync(comercio);

        await using var bloqueo = new NpgsqlConnection(fixture.OwnerConnectionString);
        await bloqueo.OpenAsync();
        await using var transaccion = await bloqueo.BeginTransactionAsync();
        await using (var tomar = bloqueo.CreateCommand())
        {
            tomar.Transaction = transaccion;
            tomar.CommandText = "SELECT pg_advisory_xact_lock($1, $2)";
            tomar.Parameters.Add(new NpgsqlParameter { Value = comercio.IdTenant });
            tomar.Parameters.Add(new NpgsqlParameter { Value = ClaveDeLockDeBaja });
            await tomar.ExecuteNonQueryAsync();
        }

        var primera = adminA.PutAsJsonAsync(Ruta(comercio.IdEmpresa), Cuerpo(publicada: true, tagline: "Primera"));
        var segundaPeticion = adminB.PutAsJsonAsync(Ruta(segunda), Cuerpo(publicada: true, tagline: "Segunda"));

        await EsperarEsperasPorElLockAsync(comercio.IdTenant, cantidad: 2);
        await transaccion.CommitAsync();

        var respuestas = await Task.WhenAll(primera, segundaPeticion);

        Assert.Equal(
            [HttpStatusCode.OK, HttpStatusCode.Conflict],
            respuestas.Select(r => r.StatusCode).Order().ToArray());
        var perdedora = respuestas.Single(r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal("vitrina_ya_publicada", await ApoyoDeDominios.CodigoDeErrorAsync(perdedora));

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        Assert.Equal(1, await db.Vitrinas.CountAsync(v => v.IdTenant == comercio.IdTenant && v.Publicada));
        Assert.Equal(1, await db.Vitrinas.CountAsync(v => v.IdTenant == comercio.IdTenant));
    }

    // ---- logo -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(Vitrina.TipoPng)]
    [InlineData(Vitrina.TipoJpeg)]
    [InlineData(Vitrina.TipoWebp)]
    public async Task UnLogoValidoSeGuardaConElTipoDelContenidoYLaMarcaDeTiempoYLaVitrinaNaceSinPublicar(string tipo)
    {
        var comercio = await apoyo.SembrarComercioAsync($"logo-{tipo[6..]}");
        using var admin = await apoyo.ClienteAdminAsync(comercio);
        var logo = ApoyoDeVitrinas.Imagen(tipo, largo: 2048, relleno: 42);

        var respuesta = await admin.PutAsync(RutaLogo(comercio.IdEmpresa), ApoyoDeVitrinas.Contenido(logo, tipo));

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var vitrina = await JsonAsync(respuesta);
        Assert.True(vitrina.GetProperty("existe").GetBoolean());
        Assert.False(vitrina.GetProperty("publicada").GetBoolean());
        Assert.True(vitrina.GetProperty("tieneLogo").GetBoolean());

        var enBase = (await apoyo.LeerVitrinaAsync(comercio.IdEmpresa))!;
        Assert.Equal(logo, enBase.Logo);
        Assert.Equal(tipo, enBase.LogoContentType);
        Assert.NotNull(enBase.LogoActualizadoEn);
        Assert.Equal(enBase.LogoActualizadoEn!.Value.ToUnixTimeSeconds(), vitrina.GetProperty("logoVersion").GetInt64());
        Assert.Equal(comercio.IdTenant, enBase.IdTenant);
    }

    [Fact]
    public async Task ElTipoDeclaradoPuedeLlevarParametrosYElLogoNuevoReemplazaAlAnteriorConOtraMarcaDeTiempo()
    {
        var comercio = await apoyo.SembrarComercioAsync("logo-reemplazo");
        using var admin = await apoyo.ClienteAdminAsync(comercio);

        var primero = ApoyoDeVitrinas.Imagen(Vitrina.TipoPng, relleno: 1);
        Assert.Equal(
            HttpStatusCode.OK,
            (await admin.PutAsync(
                RutaLogo(comercio.IdEmpresa), ApoyoDeVitrinas.Contenido(primero, "IMAGE/PNG; charset=binary"))).StatusCode);
        var antes = (await apoyo.LeerVitrinaAsync(comercio.IdEmpresa))!;

        var segundo = ApoyoDeVitrinas.Imagen(Vitrina.TipoJpeg, relleno: 2);
        Assert.Equal(
            HttpStatusCode.OK,
            (await admin.PutAsync(RutaLogo(comercio.IdEmpresa), ApoyoDeVitrinas.Contenido(segundo, Vitrina.TipoJpeg))).StatusCode);
        var despues = (await apoyo.LeerVitrinaAsync(comercio.IdEmpresa))!;

        Assert.Equal(primero, antes.Logo);
        Assert.Equal(segundo, despues.Logo);
        Assert.Equal(Vitrina.TipoJpeg, despues.LogoContentType);
        Assert.True(despues.LogoActualizadoEn > antes.LogoActualizadoEn);
        Assert.Equal(1, await apoyo.ContarVitrinasAsync(comercio.IdTenant));
    }

    public static TheoryData<string, string?, string> LogosNoAdmitidos() => new()
    {
        { "texto-plano", "text/plain", "png" },
        { "svg-declarado", "image/svg+xml", "svg" },
        { "gif-declarado", "image/gif", "gif" },
        { "json-declarado", "application/json", "png" },
        { "sin-tipo", null, "png" },
        { "png-declarado-con-jpeg", "image/png", "jpeg" },
        { "jpeg-declarado-con-webp", "image/jpeg", "webp" },
        { "png-declarado-con-svg", "image/png", "svg" },
        { "png-declarado-vacio", "image/png", "vacio" }
    };

    [Theory]
    [MemberData(nameof(LogosNoAdmitidos))]
    public async Task UnLogoConTipoDeclaradoOContenidoNoAdmitidoDa415YNoCambiaElLogoGuardado(
        string caso, string? declarado, string contenido)
    {
        var comercio = await apoyo.SembrarComercioAsync($"415-{caso}");
        var guardado = ApoyoDeVitrinas.Imagen(Vitrina.TipoPng, relleno: 3);
        await apoyo.SembrarVitrinaAsync(comercio.IdTenant, comercio.IdEmpresa, publicada: false, logo: guardado);
        var antes = (await apoyo.LeerVitrinaAsync(comercio.IdEmpresa))!;
        using var admin = await apoyo.ClienteAdminAsync(comercio);

        var bytes = contenido switch
        {
            "png" => ApoyoDeVitrinas.Imagen(Vitrina.TipoPng),
            "jpeg" => ApoyoDeVitrinas.Imagen(Vitrina.TipoJpeg),
            "webp" => ApoyoDeVitrinas.Imagen(Vitrina.TipoWebp),
            "gif" => [.. "GIF89a"u8, 0x01, 0x00, 0x01, 0x00],
            "svg" => "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"u8.ToArray(),
            _ => []
        };

        var respuesta = await admin.PutAsync(RutaLogo(comercio.IdEmpresa), ApoyoDeVitrinas.Contenido(bytes, declarado));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, respuesta.StatusCode);
        Assert.Equal("logo_no_admitido", await ApoyoDeDominios.CodigoDeErrorAsync(respuesta));

        var despues = (await apoyo.LeerVitrinaAsync(comercio.IdEmpresa))!;
        Assert.Equal(guardado, despues.Logo);
        Assert.Equal(antes.LogoActualizadoEn, despues.LogoActualizadoEn);
    }

    [Fact]
    public async Task UnLogoDeExactamenteUnMegabyteSeAceptaYUnoMasGrandeDa413SinCambiarElGuardado()
    {
        var comercio = await apoyo.SembrarComercioAsync("logo-413");
        using var admin = await apoyo.ClienteAdminAsync(comercio);

        var enElLimite = ApoyoDeVitrinas.Imagen(Vitrina.TipoPng, largo: Vitrina.TamanoMaximoDelLogo, relleno: 8);
        Assert.Equal(
            HttpStatusCode.OK,
            (await admin.PutAsync(RutaLogo(comercio.IdEmpresa), ApoyoDeVitrinas.Contenido(enElLimite, Vitrina.TipoPng))).StatusCode);
        Assert.Equal(enElLimite, (await apoyo.LeerVitrinaAsync(comercio.IdEmpresa))!.Logo);

        foreach (var largo in new[] { Vitrina.TamanoMaximoDelLogo + 1, 3 * Vitrina.TamanoMaximoDelLogo })
        {
            var grande = ApoyoDeVitrinas.Imagen(Vitrina.TipoPng, largo: largo, relleno: 9);

            var respuesta = await admin.PutAsync(
                RutaLogo(comercio.IdEmpresa), ApoyoDeVitrinas.Contenido(grande, Vitrina.TipoPng));

            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, respuesta.StatusCode);
            Assert.Equal("logo_demasiado_grande", await ApoyoDeDominios.CodigoDeErrorAsync(respuesta));
            Assert.Equal(enElLimite, (await apoyo.LeerVitrinaAsync(comercio.IdEmpresa))!.Logo);
        }
    }

    [Fact]
    public async Task QuitarElLogoLoBorraEnteroEsIdempotenteYNoCreaUnaVitrina()
    {
        var comercio = await apoyo.SembrarComercioAsync("logo-quitar");
        var sinVitrina = await apoyo.SembrarComercioAsync("logo-quitar-sin-vitrina");
        await apoyo.SembrarVitrinaAsync(
            comercio.IdTenant, comercio.IdEmpresa, publicada: true, tagline: "Se queda", logo: ApoyoDeVitrinas.Imagen(Vitrina.TipoPng));
        using var admin = await apoyo.ClienteAdminAsync(comercio);
        using var adminSinVitrina = await apoyo.ClienteAdminAsync(sinVitrina);
        using var publico = apoyo.Dominios.ClienteEnHost(comercio.Host);

        Assert.Equal(HttpStatusCode.OK, (await publico.GetAsync("/api/publico/vitrina/logo")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync(RutaLogo(comercio.IdEmpresa))).StatusCode);

        var enBase = (await apoyo.LeerVitrinaAsync(comercio.IdEmpresa))!;
        Assert.Null(enBase.Logo);
        Assert.Null(enBase.LogoContentType);
        Assert.Null(enBase.LogoActualizadoEn);
        Assert.Equal("Se queda", enBase.Tagline);
        Assert.True(enBase.Publicada);
        Assert.Equal(HttpStatusCode.NotFound, (await publico.GetAsync("/api/publico/vitrina/logo")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync(RutaLogo(comercio.IdEmpresa))).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await adminSinVitrina.DeleteAsync(RutaLogo(sinVitrina.IdEmpresa))).StatusCode);
        Assert.Null(await apoyo.LeerVitrinaAsync(sinVitrina.IdEmpresa));
    }

    // ---- dar de baja la vitrina -------------------------------------------------------------------

    [Fact]
    public async Task DarDeBajaLaVitrinaLaDespublicaEsIdempotenteYUnPutPosteriorCreaUnaNueva()
    {
        var comercio = await apoyo.SembrarComercioAsync("baja-vitrina");
        var sinVitrina = await apoyo.SembrarComercioAsync("baja-vitrina-sin-nada");
        await apoyo.SembrarVitrinaAsync(
            comercio.IdTenant, comercio.IdEmpresa, publicada: true, tagline: "Visible",
            logo: ApoyoDeVitrinas.Imagen(Vitrina.TipoPng));
        using var admin = await apoyo.ClienteAdminAsync(comercio);
        using var adminSinVitrina = await apoyo.ClienteAdminAsync(sinVitrina);
        using var publico = apoyo.Dominios.ClienteEnHost(comercio.Host);

        Assert.Equal(HttpStatusCode.OK, (await publico.GetAsync("/api/publico/vitrina")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await publico.GetAsync("/api/publico/vitrina/logo")).StatusCode);
        var idOriginal = (await apoyo.LeerVitrinaAsync(comercio.IdEmpresa))!.Id;

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync(Ruta(comercio.IdEmpresa))).StatusCode);

        // La fila queda como baja lógica, no se borra.
        var enBase = (await apoyo.LeerVitrinaAsync(comercio.IdEmpresa))!;
        Assert.Equal(idOriginal, enBase.Id);
        Assert.NotNull(enBase.DeletedAt);
        Assert.Equal("Visible", enBase.Tagline);

        // Se despublica: la lectura pública y la del admin ya no la ven.
        var vitrinaPublica = await publico.GetAsync("/api/publico/vitrina");
        Assert.Equal(HttpStatusCode.NotFound, vitrinaPublica.StatusCode);
        Assert.Equal("vitrina_no_publicada", await ApoyoDeDominios.CodigoDeErrorAsync(vitrinaPublica));
        Assert.Equal(HttpStatusCode.NotFound, (await publico.GetAsync("/api/publico/vitrina/logo")).StatusCode);
        Assert.False((await JsonAsync(await admin.GetAsync(Ruta(comercio.IdEmpresa)))).GetProperty("existe").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(RutaLogo(comercio.IdEmpresa))).StatusCode);

        // Idempotente: repetirla, o darla de baja en una empresa que nunca tuvo vitrina, es un 204 que no crea nada.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync(Ruta(comercio.IdEmpresa))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await adminSinVitrina.DeleteAsync(Ruta(sinVitrina.IdEmpresa))).StatusCode);
        Assert.Null(await apoyo.LeerVitrinaAsync(sinVitrina.IdEmpresa));
        Assert.Equal(1, await apoyo.ContarVitrinasAsync(comercio.IdTenant));

        // La baja libera a la empresa: un PUT crea una vitrina nueva, y la dada de baja queda al lado.
        Assert.Equal(
            HttpStatusCode.OK,
            (await admin.PutAsJsonAsync(Ruta(comercio.IdEmpresa), Cuerpo(publicada: true, tagline: "Nueva"))).StatusCode);
        Assert.Equal(2, await apoyo.ContarVitrinasAsync(comercio.IdTenant));
        var nueva = await JsonAsync(await publico.GetAsync("/api/publico/vitrina"));
        Assert.Equal("Nueva", nueva.GetProperty("tagline").GetString());
        Assert.False(nueva.GetProperty("tieneLogo").GetBoolean());
    }

    // ---- vista previa del logo --------------------------------------------------------------------

    [Fact]
    public async Task LaVistaPreviaDelLogoSirveLosBytesDeUnaVitrinaSinPublicarSinCacheYConEtagYCondicional()
    {
        var comercio = await apoyo.SembrarComercioAsync("preview-logo");
        var bytes = ApoyoDeVitrinas.Imagen(Vitrina.TipoPng, largo: 400, relleno: 13);
        await apoyo.SembrarVitrinaAsync(comercio.IdTenant, comercio.IdEmpresa, publicada: false, logo: bytes);
        var guardada = (await apoyo.LeerVitrinaAsync(comercio.IdEmpresa))!;
        var etagEsperado = $"\"{guardada.LogoActualizadoEn!.Value.UtcTicks:x}\"";
        using var admin = await apoyo.ClienteAdminAsync(comercio);

        var respuesta = await admin.GetAsync(RutaLogo(comercio.IdEmpresa));

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Equal(bytes, await respuesta.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/png", respuesta.Content.Headers.ContentType?.MediaType);
        Assert.True(respuesta.Headers.CacheControl is { Private: true, NoStore: true, Public: false });
        Assert.Equal("nosniff", respuesta.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(etagEsperado, respuesta.Headers.ETag?.Tag);

        using var condicional = new HttpRequestMessage(HttpMethod.Get, RutaLogo(comercio.IdEmpresa));
        condicional.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etagEsperado));
        var noModificado = await admin.SendAsync(condicional);

        Assert.Equal(HttpStatusCode.NotModified, noModificado.StatusCode);
        Assert.Empty(await noModificado.Content.ReadAsByteArrayAsync());
        Assert.Equal(etagEsperado, noModificado.Headers.ETag?.Tag);
        Assert.True(noModificado.Headers.CacheControl is { Private: true, NoStore: true, Public: false });

        // Un logo nuevo cambia el ETag y la misma condicional ya no coincide.
        var nuevo = ApoyoDeVitrinas.Imagen(Vitrina.TipoJpeg, relleno: 14);
        Assert.Equal(
            HttpStatusCode.OK,
            (await admin.PutAsync(RutaLogo(comercio.IdEmpresa), ApoyoDeVitrinas.Contenido(nuevo, Vitrina.TipoJpeg))).StatusCode);
        using var vieja = new HttpRequestMessage(HttpMethod.Get, RutaLogo(comercio.IdEmpresa));
        vieja.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etagEsperado));
        var cambiado = await admin.SendAsync(vieja);

        Assert.Equal(HttpStatusCode.OK, cambiado.StatusCode);
        Assert.Equal(nuevo, await cambiado.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/jpeg", cambiado.Content.Headers.ContentType?.MediaType);
        Assert.NotEqual(etagEsperado, cambiado.Headers.ETag?.Tag);

        // Y la vista previa no abre lo que la lectura pública no sirve: la vitrina sigue sin publicar.
        using var publico = apoyo.Dominios.ClienteEnHost(comercio.Host);
        Assert.Equal(HttpStatusCode.NotFound, (await publico.GetAsync("/api/publico/vitrina/logo")).StatusCode);
    }

    [Theory]
    [InlineData("sin-vitrina")]
    [InlineData("sin-logo")]
    [InlineData("logo-quitado")]
    public async Task LaVistaPreviaDeUnaEmpresaSinLogoDa404LogoNoEncontrado(string caso)
    {
        var comercio = await apoyo.SembrarComercioAsync($"preview-404-{caso}");

        if (caso != "sin-vitrina")
        {
            await apoyo.SembrarVitrinaAsync(
                comercio.IdTenant, comercio.IdEmpresa, publicada: false,
                logo: caso == "logo-quitado" ? ApoyoDeVitrinas.Imagen(Vitrina.TipoPng) : null);
        }

        using var admin = await apoyo.ClienteAdminAsync(comercio);

        if (caso == "logo-quitado")
        {
            Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync(RutaLogo(comercio.IdEmpresa))).StatusCode);
        }

        var respuesta = await admin.GetAsync(RutaLogo(comercio.IdEmpresa));

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        Assert.Equal("logo_no_encontrado", await ApoyoDeDominios.CodigoDeErrorAsync(respuesta));
        Assert.False(respuesta.Headers.CacheControl?.Public ?? false);
    }

    /// <summary>Por HTTP la versión siempre corre antes que los bytes, así que la ruta no distingue si el
    /// 404 de la empresa lo da una o las dos; el servicio sí: cada lectura del logo es 404-first por sí
    /// sola, y no depende de que quien la llama haya preguntado antes por la versión.</summary>
    [Fact]
    public async Task CadaLecturaDelLogoDelServicioDa404NoEncontradoParaUnaEmpresaQueNoVe()
    {
        var propio = await apoyo.SembrarComercioAsync("servicio-404");
        var ajeno = await apoyo.SembrarComercioAsync("servicio-404-ajeno");
        await apoyo.SembrarVitrinaAsync(
            ajeno.IdTenant, ajeno.IdEmpresa, publicada: true, logo: ApoyoDeVitrinas.Imagen(Vitrina.TipoPng));

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, propio.IdTenant));
        var servicio = new ServicioDeVitrinas(db, new RelojDelSistema(), new GuardaDeReferencias(db, new InspectorDeUso(db)));

        foreach (var idEmpresa in new[] { ajeno.IdEmpresa, 987_654_321 })
        {
            var enVersion = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.ObtenerVersionDelLogoAsync(idEmpresa));
            var enBytes = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.ObtenerLogoAsync(idEmpresa));

            Assert.Equal("no_encontrado", enVersion.Codigo);
            Assert.Equal("no_encontrado", enBytes.Codigo);
        }
    }

    [Fact]
    public async Task ElBorradoDeLaVitrinaYLaVistaPreviaDelLogoSoloLosHacenElAdminYLaPlataforma()
    {
        var comercio = await apoyo.SembrarComercioAsync("admin-roles-nuevas");
        var logo = ApoyoDeVitrinas.Imagen(Vitrina.TipoPng, relleno: 21);
        await apoyo.SembrarVitrinaAsync(
            comercio.IdTenant, comercio.IdEmpresa, publicada: false, tagline: "Intacta", logo: logo);
        var supervisor = await apoyo.SembrarUsuarioAsync(comercio.IdTenant, RolConocido.Supervisor, "supervisor");
        var vendedor = await apoyo.SembrarUsuarioAsync(comercio.IdTenant, RolConocido.Vendedor, "vendedor");

        HttpRequestMessage[] Solicitudes() =>
        [
            new(HttpMethod.Get, RutaLogo(comercio.IdEmpresa)),
            new(HttpMethod.Delete, Ruta(comercio.IdEmpresa))
        ];

        using var anonimo = fixture.CreateClient();
        foreach (var solicitud in Solicitudes())
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.SendAsync(solicitud)).StatusCode);
        }

        foreach (var (mail, password) in new[] { supervisor, vendedor })
        {
            using var sinPermiso = await apoyo.ClienteConSesionAsync(mail, password);

            foreach (var solicitud in Solicitudes())
            {
                Assert.Equal(HttpStatusCode.Forbidden, (await sinPermiso.SendAsync(solicitud)).StatusCode);
            }
        }

        var intacta = (await apoyo.LeerVitrinaAsync(comercio.IdEmpresa))!;
        Assert.Null(intacta.DeletedAt);
        Assert.Equal(logo, intacta.Logo);

        using var root = await apoyo.Dominios.ClienteRootAsync();
        var vistaDeRoot = await root.GetAsync(RutaLogo(comercio.IdEmpresa));
        Assert.Equal(HttpStatusCode.OK, vistaDeRoot.StatusCode);
        Assert.Equal(logo, await vistaDeRoot.Content.ReadAsByteArrayAsync());

        using var admin = await apoyo.ClienteAdminAsync(comercio);
        foreach (var solicitud in Solicitudes())
        {
            Assert.True((await admin.SendAsync(solicitud)).IsSuccessStatusCode, solicitud.RequestUri!.ToString());
        }

        Assert.NotNull((await apoyo.LeerVitrinaAsync(comercio.IdEmpresa))!.DeletedAt);
    }

    // ---- reintentos -------------------------------------------------------------------------------

    /// <summary>Un fallo transitorio (40001) en el INSERT no se reintenta: guardar corre bajo
    /// <c>FabricaDeEstrategiaSinReintento</c>. <c>Intentos</c> es el valor discriminante: con la estrategia
    /// reintentable serían dos y el segundo intento comitearía la fila.</summary>
    [Fact]
    public async Task UnaFallaTransitoriaAlCrearLaVitrinaNoSeReintentaNiGuardaNada()
    {
        var comercio = await apoyo.SembrarComercioAsync("reintento-alta");
        var interceptor = new InterceptorQueRompeLaPrimeraEscritura("vitrinas", "40001");

        await using (var db = fixture.CrearContextoDeAplicacionConReintentos(TenantActualFijo.Plataforma, interceptor))
        {
            var servicio = new ServicioDeVitrinas(db, new RelojDelSistema(), new GuardaDeReferencias(db, new InspectorDeUso(db)));

            var error = await Assert.ThrowsAnyAsync<Exception>(() => servicio.GuardarAsync(
                comercio.IdEmpresa,
                new SolicitudDeVitrina(Publicada: true, Tagline: "Hola", Descripcion: null, ColorAcento: null, Tema: "oscuro")));

            var postgres = error as PostgresException ?? error.InnerException as PostgresException;
            Assert.Equal("40001", Assert.IsType<PostgresException>(postgres).SqlState);
        }

        Assert.Equal(1, interceptor.Intentos);
        Assert.Equal(0, await apoyo.ContarVitrinasAsync(comercio.IdTenant));

        // La misma operación, sin la falla inyectada, escribe exactamente una fila.
        await using (var db = fixture.CrearContextoDeAplicacionConReintentos(TenantActualFijo.Plataforma))
        {
            await new ServicioDeVitrinas(db, new RelojDelSistema(), new GuardaDeReferencias(db, new InspectorDeUso(db))).GuardarAsync(
                comercio.IdEmpresa,
                new SolicitudDeVitrina(Publicada: true, Tagline: "Hola", Descripcion: null, ColorAcento: null, Tema: "oscuro"));
        }

        Assert.Equal(1, await apoyo.ContarVitrinasAsync(comercio.IdTenant));
    }

    [Fact]
    public async Task UnaFallaTransitoriaAlSubirElLogoNoSeReintentaNiGuardaNada()
    {
        var comercio = await apoyo.SembrarComercioAsync("reintento-logo");
        var interceptor = new InterceptorQueRompeLaPrimeraEscritura("vitrinas", "40001");

        await using (var db = fixture.CrearContextoDeAplicacionConReintentos(TenantActualFijo.Plataforma, interceptor))
        {
            var servicio = new ServicioDeVitrinas(db, new RelojDelSistema(), new GuardaDeReferencias(db, new InspectorDeUso(db)));
            await using var cuerpo = new MemoryStream(ApoyoDeVitrinas.Imagen(Vitrina.TipoPng));

            var error = await Assert.ThrowsAnyAsync<Exception>(
                () => servicio.GuardarLogoAsync(comercio.IdEmpresa, Vitrina.TipoPng, cuerpo));

            var postgres = error as PostgresException ?? error.InnerException as PostgresException;
            Assert.Equal("40001", Assert.IsType<PostgresException>(postgres).SqlState);
        }

        Assert.Equal(1, interceptor.Intentos);
        Assert.Equal(0, await apoyo.ContarVitrinasAsync(comercio.IdTenant));
    }

    [Fact]
    public async Task UnaFallaTransitoriaAlQuitarElLogoNoSeReintentaYElLogoSigueEntero()
    {
        var comercio = await apoyo.SembrarComercioAsync("reintento-quitar");
        var logo = ApoyoDeVitrinas.Imagen(Vitrina.TipoPng, relleno: 11);
        await apoyo.SembrarVitrinaAsync(comercio.IdTenant, comercio.IdEmpresa, publicada: false, logo: logo);
        var interceptor = new InterceptorQueRompeLaPrimeraEscritura("vitrinas", "40001", ClaseDeSentencia.Update);

        await using (var db = fixture.CrearContextoDeAplicacionConReintentos(TenantActualFijo.Plataforma, interceptor))
        {
            var servicio = new ServicioDeVitrinas(db, new RelojDelSistema(), new GuardaDeReferencias(db, new InspectorDeUso(db)));

            var error = await Assert.ThrowsAnyAsync<Exception>(() => servicio.QuitarLogoAsync(comercio.IdEmpresa));

            var postgres = error as PostgresException ?? error.InnerException as PostgresException;
            Assert.Equal("40001", Assert.IsType<PostgresException>(postgres).SqlState);
        }

        Assert.Equal(1, interceptor.Intentos);
        Assert.Equal(logo, (await apoyo.LeerVitrinaAsync(comercio.IdEmpresa))!.Logo);
    }

    [Fact]
    public async Task UnaFallaTransitoriaAlDarDeBajaLaVitrinaNoSeReintentaYLaVitrinaSigueViva()
    {
        var comercio = await apoyo.SembrarComercioAsync("reintento-baja");
        await apoyo.SembrarVitrinaAsync(comercio.IdTenant, comercio.IdEmpresa, publicada: true, tagline: "Sigue");
        var interceptor = new InterceptorQueRompeLaPrimeraEscritura("vitrinas", "40001", ClaseDeSentencia.Update);

        await using (var db = fixture.CrearContextoDeAplicacionConReintentos(TenantActualFijo.Plataforma, interceptor))
        {
            var servicio = new ServicioDeVitrinas(db, new RelojDelSistema(), new GuardaDeReferencias(db, new InspectorDeUso(db)));

            var error = await Assert.ThrowsAnyAsync<Exception>(() => servicio.EliminarAsync(comercio.IdEmpresa));

            var postgres = error as PostgresException ?? error.InnerException as PostgresException;
            Assert.Equal("40001", Assert.IsType<PostgresException>(postgres).SqlState);
        }

        Assert.Equal(1, interceptor.Intentos);
        var enBase = (await apoyo.LeerVitrinaAsync(comercio.IdEmpresa))!;
        Assert.Null(enBase.DeletedAt);
        Assert.True(enBase.Publicada);
    }

    // ---- apoyo ------------------------------------------------------------------------------------

    /// <summary>La clave de <c>ServicioDeOrganizacion.ClaveDeLockDeBaja</c>, que es interna a Application:
    /// si cambiara, la carrera de arriba deja de esperar el lock y esta prueba falla por timeout.</summary>
    private static int ClaveDeLockDeBaja => -20;

    /// <summary>Espera, mirando <c>pg_locks</c> desde otra conexión, a que <paramref name="cantidad"/>
    /// sesiones estén esperando el lock de tenant. Si no llegan en 15 segundos falla.</summary>
    private async Task EsperarEsperasPorElLockAsync(int idTenant, int cantidad)
    {
        await using var conexion = new NpgsqlConnection(fixture.OwnerConnectionString);
        await conexion.OpenAsync();

        var limite = DateTime.UtcNow.AddSeconds(15);

        while (DateTime.UtcNow < limite)
        {
            await using var consulta = conexion.CreateCommand();
            consulta.CommandText =
                "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND NOT granted AND classid::bigint = $1";
            consulta.Parameters.Add(new NpgsqlParameter { Value = (long)idTenant });

            if ((long)(await consulta.ExecuteScalarAsync())! >= cantidad)
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"No llegaron {cantidad} sesiones a esperar el lock del tenant {idTenant}.");
    }
}
