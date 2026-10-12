using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Bajas;
using Ways.Application.CatalogoOnline;
using Ways.Application.Catalogos;
using Ways.Application.Organizacion;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// La administración de la tienda online de cada artículo: <c>PUT /api/articulos/{id}/web</c> (publicar) y
/// <c>GET/PUT/DELETE /api/articulos/{id}/imagen</c>, más lo que expone el resto de la API del artículo
/// (<c>publicadoWeb</c> y <c>tieneImagen</c>) y la lista de precios de la tienda en la vitrina
/// (<c>idListaPrecio</c>). Todo contra Postgres real y la API completa.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class CatalogoOnlineAdminTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private readonly ApoyoDeCatalogoOnline apoyo = new(fixture);

    private static string RutaWeb(int id) => $"/api/articulos/{id}/web";

    private static string RutaImagen(int id) => $"/api/articulos/{id}/imagen";

    private static HttpRequestMessage PutImagen(int id, byte[] bytes, string? tipo = "image/png") =>
        new(HttpMethod.Put, RutaImagen(id)) { Content = ApoyoDeVitrinas.Contenido(bytes, tipo) };

    private static StringContent Json(string json) => new(json, System.Text.Encoding.UTF8, "application/json");

    private static async Task<JsonElement> LeerAsync(HttpResponseMessage respuesta) =>
        await respuesta.Content.ReadFromJsonAsync<JsonElement>();

    private async Task<(ApoyoDeCatalogoOnline.Tienda Tienda, HttpClient Admin)> TiendaConAdminAsync(
        string prefijo, bool vitrinaPublicada = true)
    {
        var tienda = await apoyo.SembrarTiendaAsync(prefijo, vitrinaPublicada);

        return (tienda, await apoyo.Vitrinas.ClienteAdminAsync(tienda.Comercio));
    }

    // ---- roles ------------------------------------------------------------------------------------

    /// <summary>Sin sesión, 401 en las cuatro rutas. Escribir exige <c>GestionDeCatalogo</c> (solo Admin), igual
    /// que editar el artículo: ni Supervisor ni Vendedor. Leer la imagen es de <c>OperacionDePos</c> como el resto
    /// de las lecturas del artículo, así que un Vendedor la ve.</summary>
    [Fact]
    public async Task SinSesionDa401SoloElAdminEscribeYUnVendedorPuedeVerLaImagen()
    {
        var (tienda, admin) = await TiendaConAdminAsync("adm-roles");
        var id = await apoyo.SembrarArticuloAsync(tienda, "Roles", publicado: false);
        var bytes = ApoyoDeVitrinas.Imagen(ValidadorDeImagen.TipoPng);
        Assert.Equal(HttpStatusCode.OK, (await admin.SendAsync(PutImagen(id, bytes))).StatusCode);

        using var anonimo = fixture.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.PutAsJsonAsync(RutaWeb(id), new { publicado = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.SendAsync(PutImagen(id, bytes))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.DeleteAsync(RutaImagen(id))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.GetAsync(RutaImagen(id))).StatusCode);

        foreach (var rol in new[] { RolConocido.Supervisor, RolConocido.Vendedor })
        {
            var (mail, password) = await apoyo.Vitrinas.SembrarUsuarioAsync(tienda.IdTenant, rol, rol.ToString().ToLowerInvariant());
            var cliente = await apoyo.Vitrinas.ClienteConSesionAsync(mail, password);

            Assert.Equal(HttpStatusCode.Forbidden, (await cliente.PutAsJsonAsync(RutaWeb(id), new { publicado = true })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await cliente.SendAsync(PutImagen(id, bytes))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await cliente.DeleteAsync(RutaImagen(id))).StatusCode);

            var lectura = await cliente.GetAsync(RutaImagen(id));
            Assert.Equal(HttpStatusCode.OK, lectura.StatusCode);
            Assert.Equal(bytes, await lectura.Content.ReadAsByteArrayAsync());
        }

        Assert.False((await apoyo.LeerArticuloAsync(id)).PublicadoWeb);
        Assert.Single(await apoyo.LeerImagenesAsync(id));
    }

    // ---- publicar ---------------------------------------------------------------------------------

    [Fact]
    public async Task PublicarYDespublicarCambiaLaBaseYSeVeEnElDetalleElListadoYLaGrilla()
    {
        var (tienda, admin) = await TiendaConAdminAsync("adm-publicar");
        var a = await apoyo.SembrarVendibleAsync(tienda, "Aaa publicable", 10m, publicado: false);
        var b = await apoyo.SembrarVendibleAsync(tienda, "Bbb vecino", 20m, publicado: false);
        await apoyo.SembrarImagenAsync(tienda.IdTenant, b, ApoyoDeVitrinas.Imagen(ValidadorDeImagen.TipoJpeg));
        Assert.False((await apoyo.LeerArticuloAsync(a)).PublicadoWeb);

        var publicar = await admin.PutAsJsonAsync(RutaWeb(a), new { publicado = true });

        Assert.Equal(HttpStatusCode.OK, publicar.StatusCode);
        var cuerpo = await LeerAsync(publicar);
        Assert.Equal(a, cuerpo.GetProperty("idArticulo").GetInt32());
        Assert.True(cuerpo.GetProperty("publicado").GetBoolean());
        Assert.True((await apoyo.LeerArticuloAsync(a)).PublicadoWeb);
        Assert.False((await apoyo.LeerArticuloAsync(b)).PublicadoWeb);

        // Detalle, listado y grilla, con valores distintos por artículo: el vecino no se publicó pero tiene imagen.
        var detalleA = await LeerAsync(await admin.GetAsync($"/api/articulos/{a}"));
        var detalleB = await LeerAsync(await admin.GetAsync($"/api/articulos/{b}"));
        Assert.True(detalleA.GetProperty("publicadoWeb").GetBoolean());
        Assert.False(detalleA.GetProperty("tieneImagen").GetBoolean());
        Assert.False(detalleB.GetProperty("publicadoWeb").GetBoolean());
        Assert.True(detalleB.GetProperty("tieneImagen").GetBoolean());

        var listado = (await LeerAsync(await admin.GetAsync("/api/articulos?tamanio=50")))
            .GetProperty("items").EnumerateArray().ToDictionary(x => x.GetProperty("id").GetInt32());
        Assert.True(listado[a].GetProperty("publicadoWeb").GetBoolean());
        Assert.False(listado[a].GetProperty("tieneImagen").GetBoolean());
        Assert.False(listado[b].GetProperty("publicadoWeb").GetBoolean());
        Assert.True(listado[b].GetProperty("tieneImagen").GetBoolean());

        var grilla = (await LeerAsync(await admin.GetAsync("/api/articulos/grilla?tamanio=50")))
            .GetProperty("items").EnumerateArray().ToDictionary(x => x.GetProperty("id").GetInt32());
        Assert.True(grilla[a].GetProperty("publicadoWeb").GetBoolean());
        Assert.False(grilla[a].GetProperty("tieneImagen").GetBoolean());
        Assert.False(grilla[b].GetProperty("publicadoWeb").GetBoolean());
        Assert.True(grilla[b].GetProperty("tieneImagen").GetBoolean());

        var despublicar = await admin.PutAsJsonAsync(RutaWeb(a), new { publicado = false });

        Assert.Equal(HttpStatusCode.OK, despublicar.StatusCode);
        Assert.False((await LeerAsync(despublicar)).GetProperty("publicado").GetBoolean());
        Assert.False((await apoyo.LeerArticuloAsync(a)).PublicadoWeb);
    }

    /// <summary>Pedir el estado que ya tiene contesta lo mismo y no escribe: <c>updated_at</c> no se mueve.</summary>
    [Fact]
    public async Task PublicarDosVecesEsIdempotenteYLaSegundaNoEscribe()
    {
        var (tienda, admin) = await TiendaConAdminAsync("adm-idempotente");
        var id = await apoyo.SembrarArticuloAsync(tienda, "Idempotente", publicado: false);

        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync(RutaWeb(id), new { publicado = true })).StatusCode);
        var tras1 = await apoyo.LeerArticuloAsync(id);

        var segunda = await admin.PutAsJsonAsync(RutaWeb(id), new { publicado = true });

        Assert.Equal(HttpStatusCode.OK, segunda.StatusCode);
        Assert.True((await LeerAsync(segunda)).GetProperty("publicado").GetBoolean());
        var tras2 = await apoyo.LeerArticuloAsync(id);
        Assert.True(tras2.PublicadoWeb);
        Assert.Equal(tras1.UpdatedAt, tras2.UpdatedAt);

        var yaDespublicado = await apoyo.SembrarArticuloAsync(tienda, "Ya despublicado", publicado: false);
        var antes = await apoyo.LeerArticuloAsync(yaDespublicado);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync(RutaWeb(yaDespublicado), new { publicado = false })).StatusCode);
        Assert.Equal(antes.UpdatedAt, (await apoyo.LeerArticuloAsync(yaDespublicado)).UpdatedAt);
    }

    /// <summary>Editar el artículo por el PUT grande no toca la publicación (no es un campo de esa edición) ni la
    /// imagen, y su respuesta los informa.</summary>
    [Fact]
    public async Task EditarElArticuloNoCambiaSuPublicacionNiSuImagenYLaRespuestaLosInforma()
    {
        var (tienda, admin) = await TiendaConAdminAsync("adm-edicion");
        var id = await apoyo.SembrarVendibleAsync(tienda, "Para editar", 10m, publicado: false);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync(RutaWeb(id), new { publicado = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.SendAsync(PutImagen(id, ApoyoDeVitrinas.Imagen(ValidadorDeImagen.TipoPng)))).StatusCode);

        var edicion = await admin.PutAsJsonAsync($"/api/articulos/{id}", new
        {
            nombre = "Editado",
            descripcion = (string?)null,
            idArea = tienda.IdArea,
            idCategoria = (int?)null,
            idMarca = (int?)null,
            idGrupo = (int?)null,
            idProveedorHabitual = (int?)null,
            idAlicuotaIva = tienda.IdAlicuotaIva,
            unidadVenta = "Unidad",
            unidadesPorBulto = (decimal?)null,
            esProducto = true,
            costoLista = (decimal?)null,
            descuentoProveedor = (decimal?)null,
            disponibleParaTodas = true,
            idsEmpresas = (int[]?)null,
            activo = true,
            controlaLote = false
        });

        Assert.Equal(HttpStatusCode.OK, edicion.StatusCode);
        var cuerpo = await LeerAsync(edicion);
        Assert.Equal("Editado", cuerpo.GetProperty("nombre").GetString());
        Assert.True(cuerpo.GetProperty("publicadoWeb").GetBoolean());
        Assert.True(cuerpo.GetProperty("tieneImagen").GetBoolean());

        var enBase = await apoyo.LeerArticuloAsync(id);
        Assert.Equal("Editado", enBase.Nombre);
        Assert.True(enBase.PublicadoWeb);
        Assert.Single(await apoyo.LeerImagenesAsync(id));
    }

    [Fact]
    public async Task UnArticuloNuevoNaceSinPublicarNiImagenYLaRespuestaDelAltaLoDice()
    {
        var (tienda, admin) = await TiendaConAdminAsync("adm-alta");

        var alta = await admin.PostAsJsonAsync("/api/articulos", new
        {
            nombre = "Recién creado",
            idArea = tienda.IdArea,
            idAlicuotaIva = tienda.IdAlicuotaIva,
            unidadVenta = "Unidad",
            esProducto = true
        });

        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var cuerpo = await LeerAsync(alta);
        Assert.False(cuerpo.GetProperty("publicadoWeb").GetBoolean());
        Assert.False(cuerpo.GetProperty("tieneImagen").GetBoolean());
        Assert.False((await apoyo.LeerArticuloAsync(cuerpo.GetProperty("id").GetInt32())).PublicadoWeb);
    }

    /// <summary>Un artículo que no se ve (no existe, dado de baja, de otro tenant) contesta 404 ANTES de mirar el
    /// cuerpo, así que un cuerpo sin <c>publicado</c> no lo convierte en 400. Para uno que se ve, el cuerpo sin
    /// <c>publicado</c> es 400 <c>publicado_requerido</c> y no escribe.</summary>
    [Fact]
    public async Task UnArticuloQueNoSeVeDa404AntesQueLaValidacionDelCuerpo()
    {
        var (tienda, admin) = await TiendaConAdminAsync("adm-404-web");
        var ajeno = await apoyo.SembrarTiendaAsync("adm-404-web-ajeno");
        var propio = await apoyo.SembrarArticuloAsync(tienda, "Propio", publicado: false);
        var eliminado = await apoyo.SembrarArticuloAsync(tienda, "Eliminado", publicado: false, eliminado: true);
        var deOtro = await apoyo.SembrarArticuloAsync(ajeno, "De otro", publicado: false);

        foreach (var id in new[] { 987_654_321, eliminado, deOtro })
        {
            foreach (var cuerpo in new[] { "{}", "{\"publicado\":true}", "{\"publicado\":null}" })
            {
                var respuesta = await admin.PutAsync(RutaWeb(id), Json(cuerpo));

                Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
            }
        }

        Assert.False((await apoyo.LeerArticuloAsync(eliminado)).PublicadoWeb);
        Assert.False((await apoyo.LeerArticuloAsync(deOtro)).PublicadoWeb);

        var sinCampo = await admin.PutAsync(RutaWeb(propio), Json("{}"));
        Assert.Equal(HttpStatusCode.BadRequest, sinCampo.StatusCode);
        Assert.Equal("publicado_requerido", await ApoyoDeDominios.CodigoDeErrorAsync(sinCampo));
        Assert.False((await apoyo.LeerArticuloAsync(propio)).PublicadoWeb);
    }

    // ---- imagen: ida y vuelta ---------------------------------------------------------------------

    [Fact]
    public async Task LaImagenSeSubeSeLeeConEtagYSeReemplazaYSeQuitaYSeVuelveASubir()
    {
        var (tienda, admin) = await TiendaConAdminAsync("adm-imagen");
        var id = await apoyo.SembrarArticuloAsync(tienda, "Con imagen");
        var otro = await apoyo.SembrarArticuloAsync(tienda, "Vecino con imagen");
        var vecina = ApoyoDeVitrinas.Imagen(ValidadorDeImagen.TipoWebp, largo: 90, relleno: 3);
        await apoyo.SembrarImagenAsync(tienda.IdTenant, otro, vecina, ValidadorDeImagen.TipoWebp);

        var png = ApoyoDeVitrinas.Imagen(ValidadorDeImagen.TipoPng, largo: 300, relleno: 9);
        var subida = await admin.SendAsync(PutImagen(id, png));

        Assert.Equal(HttpStatusCode.OK, subida.StatusCode);
        var cuerpo = await LeerAsync(subida);
        Assert.Equal(id, cuerpo.GetProperty("idArticulo").GetInt32());
        Assert.True(cuerpo.GetProperty("tieneImagen").GetBoolean());
        var guardada = Assert.Single(await apoyo.LeerImagenesAsync(id));
        Assert.Equal(png, guardada.Contenido);
        Assert.Equal("image/png", guardada.ContentType);
        Assert.Equal(guardada.ActualizadoEn.ToUnixTimeSeconds(), cuerpo.GetProperty("imagenVersion").GetInt64());

        // Vista previa: bytes, tipo, ETag de la marca de carga, sin caché compartida ni del navegador.
        var lectura = await admin.GetAsync(RutaImagen(id));
        Assert.Equal(HttpStatusCode.OK, lectura.StatusCode);
        Assert.Equal(png, await lectura.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/png", lectura.Content.Headers.ContentType?.MediaType);
        Assert.True(lectura.Headers.CacheControl is { NoStore: true, Private: true, Public: false });
        Assert.Equal("nosniff", lectura.Headers.GetValues("X-Content-Type-Options").Single());
        var etag = $"\"{guardada.ActualizadoEn.UtcTicks:x}\"";
        Assert.Equal(etag, lectura.Headers.ETag?.Tag);

        using var condicional = new HttpRequestMessage(HttpMethod.Get, RutaImagen(id));
        condicional.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag));
        var noModificado = await admin.SendAsync(condicional);
        Assert.Equal(HttpStatusCode.NotModified, noModificado.StatusCode);
        Assert.Empty(await noModificado.Content.ReadAsByteArrayAsync());
        Assert.Equal(etag, noModificado.Headers.ETag?.Tag);

        // Reemplazo: otro tipo, otros bytes, otro ETag, una sola fila viva.
        var jpeg = ApoyoDeVitrinas.Imagen(ValidadorDeImagen.TipoJpeg, largo: 120, relleno: 5);
        Assert.Equal(HttpStatusCode.OK, (await admin.SendAsync(PutImagen(id, jpeg, "image/jpeg"))).StatusCode);
        var reemplazada = Assert.Single(await apoyo.LeerImagenesAsync(id));
        Assert.Equal(jpeg, reemplazada.Contenido);
        Assert.Equal("image/jpeg", reemplazada.ContentType);
        Assert.NotEqual(guardada.ActualizadoEn, reemplazada.ActualizadoEn);
        Assert.Equal(guardada.Id, reemplazada.Id);

        using var viejaCondicional = new HttpRequestMessage(HttpMethod.Get, RutaImagen(id));
        viejaCondicional.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag));
        var nueva = await admin.SendAsync(viejaCondicional);
        Assert.Equal(HttpStatusCode.OK, nueva.StatusCode);
        Assert.Equal(jpeg, await nueva.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/jpeg", nueva.Content.Headers.ContentType?.MediaType);

        // Quitar: la fila se borra de verdad (nada que conservar de un derivado), 404 imagen_no_encontrada,
        // idempotente, y la del vecino sigue entera.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync(RutaImagen(id))).StatusCode);
        var tras = await admin.GetAsync(RutaImagen(id));
        Assert.Equal(HttpStatusCode.NotFound, tras.StatusCode);
        Assert.Equal("imagen_no_encontrada", await ApoyoDeDominios.CodigoDeErrorAsync(tras));
        Assert.Empty(await apoyo.LeerImagenesAsync(id));
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync(RutaImagen(id))).StatusCode);
        Assert.Equal(vecina, Assert.Single(await apoyo.LeerImagenesAsync(otro)).Contenido);

        using (var publico = apoyo.Dominios.ClienteEnHost(tienda.Host))
        {
            var publica = await publico.GetAsync(ApoyoDeCatalogoOnline.RutaImagenPublica(id));
            Assert.Equal(HttpStatusCode.NotFound, publica.StatusCode);
            Assert.Equal("imagen_no_encontrada", await ApoyoDeDominios.CodigoDeErrorAsync(publica));
        }

        // Volver a subir después de quitar inserta una fila nueva: no queda ninguna anterior.
        Assert.Equal(HttpStatusCode.OK, (await admin.SendAsync(PutImagen(id, png))).StatusCode);
        var nuevaFila = Assert.Single(await apoyo.LeerImagenesAsync(id));
        Assert.Null(nuevaFila.DeletedAt);
        Assert.NotEqual(guardada.Id, nuevaFila.Id);
        Assert.Equal(png, nuevaFila.Contenido);
    }

    /// <summary>Dar de baja el artículo borra su imagen en la misma transacción: no queda una fila con hasta 1 MB
    /// colgando de un artículo que ya no se ve. La imagen de otro artículo no se toca.</summary>
    [Fact]
    public async Task DarDeBajaUnArticuloBorraSuImagenYNoLaDeOtro()
    {
        var (tienda, admin) = await TiendaConAdminAsync("adm-baja-borra-imagen");
        var id = await apoyo.SembrarArticuloAsync(tienda, "Con imagen y se da de baja");
        var otro = await apoyo.SembrarArticuloAsync(tienda, "Vecino con imagen");
        await apoyo.SembrarImagenAsync(tienda.IdTenant, id, ApoyoDeVitrinas.Imagen(ValidadorDeImagen.TipoPng));
        var deOtro = ApoyoDeVitrinas.Imagen(ValidadorDeImagen.TipoWebp, largo: 90, relleno: 3);
        await apoyo.SembrarImagenAsync(tienda.IdTenant, otro, deOtro, ValidadorDeImagen.TipoWebp);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/articulos/{id}")).StatusCode);

        Assert.NotNull((await apoyo.LeerArticuloAsync(id)).DeletedAt);
        Assert.Empty(await apoyo.LeerImagenesAsync(id));
        Assert.Equal(deOtro, Assert.Single(await apoyo.LeerImagenesAsync(otro)).Contenido);
    }

    [Fact]
    public async Task UnaCondicionalQueCoincideDa304SinLeerLosBytesDeLaImagen()
    {
        var (tienda, admin) = await TiendaConAdminAsync("adm-imagen-304");
        var id = await apoyo.SembrarArticuloAsync(tienda, "Sin leer bytes");
        var bytes = ApoyoDeVitrinas.Imagen(ValidadorDeImagen.TipoPng, largo: 200, relleno: 4);
        await apoyo.SembrarImagenAsync(tienda.IdTenant, id, bytes);
        var etag = $"\"{Assert.Single(await apoyo.LeerImagenesAsync(id)).ActualizadoEn.UtcTicks:x}\"";

        var comandos = new RegistroDeComandos();
        using var _ = fixture.ConInterceptorEnElHost(comandos);
        var cliente = await apoyo.Vitrinas.ClienteConSesionAsync(tienda.Comercio.Tenant.Mail, tienda.Comercio.Tenant.Password);

        comandos.Limpiar();
        using var condicional = new HttpRequestMessage(HttpMethod.Get, RutaImagen(id));
        condicional.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag));
        var noModificado = await cliente.SendAsync(condicional);

        Assert.Equal(HttpStatusCode.NotModified, noModificado.StatusCode);
        Assert.DoesNotContain(comandos.Textos(), t => t.Contains("contenido", StringComparison.Ordinal));
        Assert.Contains(comandos.Textos(), t => t.Contains("imagenes_articulo", StringComparison.Ordinal));

        comandos.Limpiar();
        var completa = await cliente.GetAsync(RutaImagen(id));

        Assert.Equal(HttpStatusCode.OK, completa.StatusCode);
        Assert.Equal(bytes, await completa.Content.ReadAsByteArrayAsync());
        Assert.Contains(comandos.Textos(), t => t.Contains("contenido", StringComparison.Ordinal));
    }

    // ---- imagen: rechazos -------------------------------------------------------------------------

    [Fact]
    public async Task UnaImagenDeMasDeUnMegabyteDa413ImagenDemasiadoGrandeYUnaDeExactamenteUnoSeAcepta()
    {
        var (tienda, admin) = await TiendaConAdminAsync("adm-413");
        var id = await apoyo.SembrarArticuloAsync(tienda, "Tope");

        var justa = new byte[ValidadorDeImagen.TamanoMaximo];
        ApoyoDeVitrinas.Imagen(ValidadorDeImagen.TipoPng).CopyTo(justa, 0);
        Assert.Equal(HttpStatusCode.OK, (await admin.SendAsync(PutImagen(id, justa))).StatusCode);
        Assert.Equal(ValidadorDeImagen.TamanoMaximo, Assert.Single(await apoyo.LeerImagenesAsync(id)).Contenido.Length);

        var grande = new byte[ValidadorDeImagen.TamanoMaximo + 1];
        ApoyoDeVitrinas.Imagen(ValidadorDeImagen.TipoPng).CopyTo(grande, 0);
        var rechazada = await admin.SendAsync(PutImagen(id, grande));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, rechazada.StatusCode);
        Assert.Equal("imagen_demasiado_grande", await ApoyoDeDominios.CodigoDeErrorAsync(rechazada));
        Assert.Equal(ValidadorDeImagen.TamanoMaximo, Assert.Single(await apoyo.LeerImagenesAsync(id)).Contenido.Length);
    }

    [Theory]
    [InlineData("image/gif", false)]
    [InlineData("image/svg+xml", false)]
    [InlineData("text/plain", false)]
    [InlineData(null, false)]
    [InlineData("image/jpeg", true)]
    [InlineData("image/png", false)]
    public async Task UnaImagenQueNoEsPngJpegNiWebpODeclaraOtroTipoDa415ImagenNoAdmitida(string? declarado, bool contenidoPng)
    {
        var (tienda, admin) = await TiendaConAdminAsync("adm-415");
        var id = await apoyo.SembrarArticuloAsync(tienda, "Tipo");
        var contenido = contenidoPng
            ? ApoyoDeVitrinas.Imagen(ValidadorDeImagen.TipoPng)
            : "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"u8.ToArray();

        var respuesta = await admin.SendAsync(PutImagen(id, contenido, declarado));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, respuesta.StatusCode);
        Assert.Equal("imagen_no_admitida", await ApoyoDeDominios.CodigoDeErrorAsync(respuesta));
        Assert.Empty(await apoyo.LeerImagenesAsync(id));
    }

    /// <summary>Un artículo que no se ve contesta 404 aunque el cuerpo sea enorme o de un tipo que no se admite:
    /// el 404 va antes de tocar el cuerpo.</summary>
    [Fact]
    public async Task UnArticuloQueNoSeVeDa404EnLasCuatroOperacionesDeImagenAntesQueElCuerpo()
    {
        var (tienda, admin) = await TiendaConAdminAsync("adm-404-imagen");
        var ajeno = await apoyo.SembrarTiendaAsync("adm-404-imagen-ajeno");
        var eliminado = await apoyo.SembrarArticuloAsync(tienda, "Eliminado", eliminado: true);
        var deOtro = await apoyo.SembrarArticuloAsync(ajeno, "De otro");
        var bytesDeOtro = ApoyoDeVitrinas.Imagen(ValidadorDeImagen.TipoPng, relleno: 8);
        await apoyo.SembrarImagenAsync(ajeno.IdTenant, deOtro, bytesDeOtro);
        var enorme = new byte[ValidadorDeImagen.TamanoMaximo + 1];

        foreach (var id in new[] { 987_654_321, eliminado, deOtro })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(RutaImagen(id))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync(RutaImagen(id))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await admin.SendAsync(PutImagen(id, enorme))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await admin.SendAsync(PutImagen(id, enorme, "image/gif"))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await admin.SendAsync(PutImagen(id, bytesDeOtro))).StatusCode);
        }

        Assert.Equal(bytesDeOtro, Assert.Single(await apoyo.LeerImagenesAsync(deOtro)).Contenido);
        Assert.Empty(await apoyo.LeerImagenesAsync(eliminado));
    }

    // ---- locks y carreras -------------------------------------------------------------------------

    /// <summary>
    /// Dos subidas del mismo artículo sin imagen, las dos esperando el lock de su fila: al soltarlo se
    /// serializan, la segunda ve la fila de la primera y la reemplaza, y quedan 200 y 200 con UNA fila viva. Sin
    /// el <c>FOR UPDATE</c> las dos leerían "sin imagen" y la segunda chocaría con el índice único (409
    /// <c>imagen_duplicada</c>). Que las dos estén esperando se verifica en <c>pg_stat_activity</c>, no con una pausa.
    /// </summary>
    [Fact]
    public async Task DosSubidasConcurrentesDelMismoArticuloSeSerializanYDejanUnaSolaImagenViva()
    {
        var (tienda, admin) = await TiendaConAdminAsync("adm-carrera-imagen");
        var id = await apoyo.SembrarArticuloAsync(tienda, "Carrera");
        var primera = ApoyoDeVitrinas.Imagen(ValidadorDeImagen.TipoPng, largo: 100, relleno: 1);
        var segunda = ApoyoDeVitrinas.Imagen(ValidadorDeImagen.TipoPng, largo: 100, relleno: 2);

        await using var sostenedor = await TomarLockDelArticuloAsync(id);
        var a = admin.SendAsync(PutImagen(id, primera));
        var b = admin.SendAsync(PutImagen(id, segunda));
        await EsperarBloqueadosAsync(esperados: 2);

        await sostenedor.SoltarAsync();
        var respuestas = await Task.WhenAll(a, b);

        Assert.All(respuestas, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var viva = Assert.Single(await apoyo.LeerImagenesAsync(id));
        Assert.Contains(viva.Contenido, [primera, segunda], new IgualdadDeBytes());
    }

    /// <summary>
    /// El artículo se da de baja mientras la subida espera su lock: al despertar, la subida lo ve bajo el lock y
    /// contesta 404 sin dejar una imagen sobre un artículo borrado. Mata la comprobación posterior al lock.
    /// </summary>
    [Fact]
    public async Task UnaSubidaQuePierdeContraLaBajaDelArticuloDa404YNoDejaImagen()
    {
        var (tienda, admin) = await TiendaConAdminAsync("adm-baja-imagen");
        var id = await apoyo.SembrarArticuloAsync(tienda, "Se da de baja");

        await using var sostenedor = await TomarLockDelArticuloAsync(id);
        var subida = admin.SendAsync(PutImagen(id, ApoyoDeVitrinas.Imagen(ValidadorDeImagen.TipoPng)));
        await EsperarBloqueadosAsync(esperados: 1);

        await sostenedor.DarDeBajaYSoltarAsync();
        var respuesta = await subida;

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        Assert.Empty(await apoyo.LeerImagenesAsync(id));
    }

    [Fact]
    public async Task QuitarLaImagenDeUnArticuloQuePierdeContraLaBajaDa404()
    {
        var (tienda, admin) = await TiendaConAdminAsync("adm-baja-quitar");
        var id = await apoyo.SembrarArticuloAsync(tienda, "Se da de baja y se quita");
        await apoyo.SembrarImagenAsync(tienda.IdTenant, id, ApoyoDeVitrinas.Imagen(ValidadorDeImagen.TipoPng));

        await using var sostenedor = await TomarLockDelArticuloAsync(id);
        var quitar = admin.DeleteAsync(RutaImagen(id));
        await EsperarBloqueadosAsync(esperados: 1);

        await sostenedor.DarDeBajaYSoltarAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await quitar).StatusCode);
        Assert.Null(Assert.Single(await apoyo.LeerImagenesAsync(id)).DeletedAt);
    }

    /// <summary>
    /// Publicar lee el artículo BAJO el lock: si la baja gana la carrera, la publicación contesta 404 y no
    /// escribe <c>publicado_web</c> sobre un artículo borrado. Con la lectura antes del lock (o sin lock), EF
    /// vería el artículo vivo, esperaría recién en el <c>UPDATE</c> y escribiría igual.
    /// </summary>
    [Fact]
    public async Task UnaPublicacionQuePierdeContraLaBajaDelArticuloDa404YNoEscribe()
    {
        var (tienda, admin) = await TiendaConAdminAsync("adm-baja-publicar");
        var id = await apoyo.SembrarArticuloAsync(tienda, "Se da de baja y se publica", publicado: false);

        await using var sostenedor = await TomarLockDelArticuloAsync(id);
        var publicar = admin.PutAsJsonAsync(RutaWeb(id), new { publicado = true });
        await EsperarBloqueadosAsync(esperados: 1);

        await sostenedor.DarDeBajaYSoltarAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await publicar).StatusCode);
        Assert.False((await apoyo.LeerArticuloAsync(id)).PublicadoWeb);
    }

    private Task<SostenedorDeLock> TomarLockDelArticuloAsync(int idArticulo) =>
        TomarLockAsync("articulos", "id_articulo", idArticulo);

    /// <summary>Una sesión aparte (el dueño, que no pasa por RLS) con <c>FOR UPDATE</c> sobre la fila: es lo que
    /// toma la baja de un artículo o de una lista antes de contar sus referencias.</summary>
    private async Task<SostenedorDeLock> TomarLockAsync(string tabla, string columnaPk, int id)
    {
        using var _ = fixture.CreateClient();
        var conexion = new NpgsqlConnection(fixture.OwnerConnectionString);
        await conexion.OpenAsync();

        var transaccion = await conexion.BeginTransactionAsync();
        await using (var comando = conexion.CreateCommand())
        {
            comando.Transaction = transaccion;
            comando.CommandText = $"SELECT 1 FROM {tabla} WHERE {columnaPk} = $1 FOR UPDATE";
            comando.Parameters.Add(new NpgsqlParameter { Value = id });
            await comando.ExecuteScalarAsync();
        }

        return new SostenedorDeLock(conexion, transaccion, tabla, columnaPk, id);
    }

    /// <summary>Espera, con tope, a que haya exactamente <paramref name="esperados"/> sesiones de la aplicación
    /// bloqueadas esperando un lock de fila: con eso la carrera está armada, sin depender de pausas.</summary>
    private async Task EsperarBloqueadosAsync(int esperados)
    {
        await using var conexion = new NpgsqlConnection(fixture.OwnerConnectionString);
        await conexion.OpenAsync();

        var limite = DateTime.UtcNow.AddSeconds(15);
        long bloqueados = 0;

        while (DateTime.UtcNow < limite)
        {
            await using var comando = conexion.CreateCommand();
            comando.CommandText =
                "SELECT count(*) FROM pg_stat_activity WHERE usename = 'ways_app' AND wait_event_type = 'Lock' " +
                "AND state = 'active'";
            bloqueados = (long)(await comando.ExecuteScalarAsync())!;

            if (bloqueados >= esperados)
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"Se esperaban {esperados} sesiones bloqueadas y hay {bloqueados}.");
    }

    private sealed class SostenedorDeLock(
        NpgsqlConnection conexion, NpgsqlTransaction transaccion, string tabla, string columnaPk, int id)
        : IAsyncDisposable
    {
        private bool terminada;

        public async Task SoltarAsync()
        {
            await transaccion.CommitAsync();
            terminada = true;
        }

        public async Task DarDeBajaYSoltarAsync()
        {
            await using (var comando = conexion.CreateCommand())
            {
                comando.Transaction = transaccion;
                comando.CommandText = $"UPDATE {tabla} SET deleted_at = now() WHERE {columnaPk} = $1";
                comando.Parameters.Add(new NpgsqlParameter { Value = id });
                await comando.ExecuteNonQueryAsync();
            }

            await SoltarAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (!terminada)
            {
                await transaccion.RollbackAsync();
            }

            await transaccion.DisposeAsync();
            await conexion.DisposeAsync();
        }
    }

    /// <summary>Guarda el texto de cada comando que el host de la API manda a la base.</summary>
    private sealed class RegistroDeComandos : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<string> textos = new();

        public void Limpiar() => textos.Clear();

        public string[] Textos() => [.. textos];

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            textos.Enqueue(command.CommandText);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            textos.Enqueue(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class IgualdadDeBytes : IEqualityComparer<byte[]>
    {
        public bool Equals(byte[]? x, byte[]? y) => x is not null && y is not null && x.AsSpan().SequenceEqual(y);

        public int GetHashCode(byte[] obj) => obj.Length;
    }

    // ---- reintentos -------------------------------------------------------------------------------

    /// <summary>Un fallo transitorio (40001) en el INSERT de la imagen no se reintenta: la subida corre bajo
    /// <c>FabricaDeEstrategiaSinReintento</c>. <c>Intentos</c> es el valor discriminante: con la estrategia
    /// reintentable serían dos y el segundo intento comitearía la fila.</summary>
    [Fact]
    public async Task UnaFallaTransitoriaAlSubirLaImagenNoSeReintentaNiGuardaNada()
    {
        var tienda = await apoyo.SembrarTiendaAsync("adm-reintento-imagen");
        var id = await apoyo.SembrarArticuloAsync(tienda, "Reintento");
        var interceptor = new InterceptorQueRompeLaPrimeraEscritura("imagenes_articulo", "40001");

        await using (var db = fixture.CrearContextoDeAplicacionConReintentos(TenantActualFijo.Plataforma, interceptor))
        {
            var servicio = ServicioDe(db);
            await using var cuerpo = new MemoryStream(ApoyoDeVitrinas.Imagen(ValidadorDeImagen.TipoPng));

            var error = await Assert.ThrowsAnyAsync<Exception>(
                () => servicio.GuardarImagenAsync(id, ValidadorDeImagen.TipoPng, cuerpo));

            var postgres = error as PostgresException ?? error.InnerException as PostgresException;
            Assert.Equal("40001", Assert.IsType<PostgresException>(postgres).SqlState);
        }

        Assert.Equal(1, interceptor.Intentos);
        Assert.Empty(await apoyo.LeerImagenesAsync(id));
    }

    [Fact]
    public async Task UnaFallaTransitoriaAlPublicarNoSeReintentaYElArticuloSigueSinPublicar()
    {
        var tienda = await apoyo.SembrarTiendaAsync("adm-reintento-publicar");
        var id = await apoyo.SembrarArticuloAsync(tienda, "Reintento publicar", publicado: false);
        var interceptor = new InterceptorQueRompeLaPrimeraEscritura("articulos", "40001", ClaseDeSentencia.Update);

        await using (var db = fixture.CrearContextoDeAplicacionConReintentos(TenantActualFijo.Plataforma, interceptor))
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(
                () => ServicioDe(db).PublicarAsync(id, new SolicitudDePublicacionWeb(true)));

            var postgres = error as PostgresException ?? error.InnerException as PostgresException;
            Assert.Equal("40001", Assert.IsType<PostgresException>(postgres).SqlState);
        }

        Assert.Equal(1, interceptor.Intentos);
        Assert.False((await apoyo.LeerArticuloAsync(id)).PublicadoWeb);
    }

    [Fact]
    public async Task UnaFallaTransitoriaAlQuitarLaImagenNoSeReintentaYLaImagenSigueViva()
    {
        var tienda = await apoyo.SembrarTiendaAsync("adm-reintento-quitar");
        var id = await apoyo.SembrarArticuloAsync(tienda, "Reintento quitar");
        await apoyo.SembrarImagenAsync(tienda.IdTenant, id, ApoyoDeVitrinas.Imagen(ValidadorDeImagen.TipoPng));
        var interceptor = new InterceptorQueRompeLaPrimeraEscritura("imagenes_articulo", "40001", ClaseDeSentencia.Delete);

        await using (var db = fixture.CrearContextoDeAplicacionConReintentos(TenantActualFijo.Plataforma, interceptor))
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(() => ServicioDe(db).QuitarImagenAsync(id));

            var postgres = error as PostgresException ?? error.InnerException as PostgresException;
            Assert.Equal("40001", Assert.IsType<PostgresException>(postgres).SqlState);
        }

        Assert.Equal(1, interceptor.Intentos);
        Assert.Null(Assert.Single(await apoyo.LeerImagenesAsync(id)).DeletedAt);
    }

    private static ServicioDeCatalogoWeb ServicioDe(IWaysDbContext db) =>
        new(db, new RelojDelSistema(), new GuardaDeReferencias(db, new InspectorDeUso(db)));

    // ---- lista de precios de la tienda en la vitrina ----------------------------------------------

    [Fact]
    public async Task LaVitrinaGuardaLaListaDeLaTiendaYSinListaVuelveALaDefault()
    {
        var (tienda, admin) = await TiendaConAdminAsync("adm-lista", vitrinaPublicada: false);
        var propia = await apoyo.SembrarListaAsync(tienda.IdTenant, "Mayorista");
        var deLaEmpresa = await apoyo.SembrarListaAsync(tienda.IdTenant, "Solo esta empresa", idEmpresa: tienda.IdEmpresa);
        var ruta = $"/api/empresas/{tienda.IdEmpresa}/vitrina";

        var sinGuardar = await LeerAsync(await admin.GetAsync(ruta));
        Assert.False(sinGuardar.GetProperty("existe").GetBoolean());
        Assert.Equal(JsonValueKind.Null, sinGuardar.GetProperty("idListaPrecio").ValueKind);

        foreach (var idLista in new[] { propia, deLaEmpresa })
        {
            var guardada = await admin.PutAsJsonAsync(ruta, Solicitud(idLista));

            Assert.Equal(HttpStatusCode.OK, guardada.StatusCode);
            Assert.Equal(idLista, (await LeerAsync(guardada)).GetProperty("idListaPrecio").GetInt32());
            Assert.Equal(idLista, (await LeerAsync(await admin.GetAsync(ruta))).GetProperty("idListaPrecio").GetInt32());
            Assert.Equal(idLista, (await apoyo.Vitrinas.LeerVitrinaAsync(tienda.IdEmpresa))!.IdListaPrecio);
        }

        // El PUT reemplaza todo: sin idListaPrecio vuelve a la default.
        var sinLista = await admin.PutAsJsonAsync(ruta, Solicitud(null));
        Assert.Equal(HttpStatusCode.OK, sinLista.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await LeerAsync(sinLista)).GetProperty("idListaPrecio").ValueKind);
        Assert.Null((await apoyo.Vitrinas.LeerVitrinaAsync(tienda.IdEmpresa))!.IdListaPrecio);
    }

    /// <summary>Una lista que falta, está inactiva, está dada de baja, es de otro tenant o es de OTRA empresa del
    /// mismo tenant es 400 <c>lista_invalida</c> y no cambia la vitrina. Cada caso mata una cláusula de la
    /// validación (existencia/baja, activa, tenant, alcance de empresa).</summary>
    [Fact]
    public async Task UnaListaInvalidaDa400ListaInvalidaYNoCambiaLaVitrina()
    {
        var (tienda, admin) = await TiendaConAdminAsync("adm-lista-invalida", vitrinaPublicada: false);
        var ajena = await apoyo.SembrarTiendaAsync("adm-lista-invalida-ajena");
        var otraEmpresa = await apoyo.Vitrinas.SembrarEmpresaAsync(tienda.IdTenant, "Otra empresa SA");
        var buena = await apoyo.SembrarListaAsync(tienda.IdTenant, "Buena");
        var inactiva = await apoyo.SembrarListaAsync(tienda.IdTenant, "Inactiva", activo: false);
        var eliminada = await apoyo.SembrarListaAsync(tienda.IdTenant, "Eliminada", eliminada: true);
        var deOtroTenant = await apoyo.SembrarListaAsync(ajena.IdTenant, "De otro tenant");
        var deOtraEmpresa = await apoyo.SembrarListaAsync(tienda.IdTenant, "De otra empresa", idEmpresa: otraEmpresa);
        var ruta = $"/api/empresas/{tienda.IdEmpresa}/vitrina";
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync(ruta, Solicitud(buena))).StatusCode);

        foreach (var invalida in new[] { 987_654_321, inactiva, eliminada, deOtroTenant, deOtraEmpresa })
        {
            var respuesta = await admin.PutAsJsonAsync(ruta, Solicitud(invalida, tagline: "no se guarda"));

            Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
            Assert.Equal("lista_invalida", await ApoyoDeDominios.CodigoDeErrorAsync(respuesta));
        }

        var enBase = (await apoyo.Vitrinas.LeerVitrinaAsync(tienda.IdEmpresa))!;
        Assert.Equal(buena, enBase.IdListaPrecio);
        Assert.Null(enBase.Tagline);
    }

    /// <summary>La empresa que no se ve da 404 antes que cualquier validación, incluida la de la lista.</summary>
    [Fact]
    public async Task UnaEmpresaQueNoSeVeDa404AunConUnaListaInvalida()
    {
        var (_, admin) = await TiendaConAdminAsync("adm-lista-404");
        var ajena = await apoyo.SembrarTiendaAsync("adm-lista-404-ajena");

        foreach (var idEmpresa in new[] { 987_654_321, ajena.IdEmpresa })
        {
            var respuesta = await admin.PutAsJsonAsync($"/api/empresas/{idEmpresa}/vitrina", Solicitud(987_654_321));

            Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        }
    }

    /// <summary>La lista que usa una vitrina no se puede dar de baja: la FK entra al inventario de dependientes.</summary>
    [Fact]
    public async Task UnaListaQueUsaUnaVitrinaNoSePuedeDarDeBaja()
    {
        var (tienda, admin) = await TiendaConAdminAsync("adm-lista-en-uso");
        var lista = await apoyo.SembrarListaAsync(tienda.IdTenant, "En uso");
        Assert.Equal(
            HttpStatusCode.OK,
            (await admin.PutAsJsonAsync($"/api/empresas/{tienda.IdEmpresa}/vitrina", Solicitud(lista))).StatusCode);

        var baja = await admin.DeleteAsync($"/api/catalogos/listas-precio/{lista}");

        Assert.Equal(HttpStatusCode.Conflict, baja.StatusCode);
        var error = await LeerAsync(baja);
        Assert.EndsWith("_en_uso", error.GetProperty("codigo").GetString());
        Assert.Contains("vitrinas", error.GetProperty("title").GetString());
    }

    /// <summary>
    /// La lista se da de baja mientras la vitrina espera su lock: la baja toma <c>FOR UPDATE</c> sobre la lista, así
    /// que guardar la vitrina (que toma <c>FOR KEY SHARE</c> sobre ella) espera; al despertar la lista ya no está
    /// viva y la respuesta es 400 <c>lista_invalida</c>. Sin ese lock el guardado leería la lista viva, esperaría
    /// recién en el <c>UPDATE</c> (la FK) y dejaría la vitrina apuntando a una lista dada de baja.
    /// </summary>
    [Fact]
    public async Task UnaListaQueSeDaDeBajaMientrasSeGuardaLaVitrinaDa400YLaVitrinaNoLaReferencia()
    {
        var (tienda, admin) = await TiendaConAdminAsync("adm-lista-baja", vitrinaPublicada: false);
        var lista = await apoyo.SembrarListaAsync(tienda.IdTenant, "Se da de baja");
        var ruta = $"/api/empresas/{tienda.IdEmpresa}/vitrina";

        await using var sostenedor = await TomarLockAsync("listas_precio", "id_lista_precio", lista);
        var guardar = admin.PutAsJsonAsync(ruta, Solicitud(lista));
        await EsperarBloqueadosAsync(esperados: 1);

        await sostenedor.DarDeBajaYSoltarAsync();
        var respuesta = await guardar;

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal("lista_invalida", await ApoyoDeDominios.CodigoDeErrorAsync(respuesta));
        Assert.Null(await apoyo.Vitrinas.LeerVitrinaAsync(tienda.IdEmpresa));
    }

    /// <summary>
    /// Una vitrina cuya lista se desactivó después (acá por una escritura cruda, que no pasa por la guarda de
    /// <c>ServicioDeListasPrecio</c>) sigue siendo editable mientras el <c>PUT</c> mande la MISMA lista: la lista
    /// solo se valida cuando cambia. Mandar otra lista inactiva sigue siendo 400 <c>lista_invalida</c>.
    /// </summary>
    [Fact]
    public async Task UnaVitrinaConLaListaDesactivadaSeSigueEditandoSiLaListaNoCambia()
    {
        var (tienda, admin) = await TiendaConAdminAsync("adm-lista-sin-cambio", vitrinaPublicada: false);
        var lista = await apoyo.SembrarListaAsync(tienda.IdTenant, "Se desactiva");
        var otraInactiva = await apoyo.SembrarListaAsync(tienda.IdTenant, "Otra inactiva", activo: false);
        var ruta = $"/api/empresas/{tienda.IdEmpresa}/vitrina";
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync(ruta, Solicitud(lista))).StatusCode);

        await apoyo.ModificarListaAsync(lista, l => l.Activo = false);

        var editada = await admin.PutAsJsonAsync(ruta, Solicitud(lista, tagline: "slogan nuevo"));

        Assert.Equal(HttpStatusCode.OK, editada.StatusCode);
        var enBase = (await apoyo.Vitrinas.LeerVitrinaAsync(tienda.IdEmpresa))!;
        Assert.Equal((lista, "slogan nuevo"), (enBase.IdListaPrecio, enBase.Tagline));

        var otra = await admin.PutAsJsonAsync(ruta, Solicitud(otraInactiva, tagline: "no se guarda"));

        Assert.Equal(HttpStatusCode.BadRequest, otra.StatusCode);
        Assert.Equal("lista_invalida", await ApoyoDeDominios.CodigoDeErrorAsync(otra));
        enBase = (await apoyo.Vitrinas.LeerVitrinaAsync(tienda.IdEmpresa))!;
        Assert.Equal((lista, "slogan nuevo"), (enBase.IdListaPrecio, enBase.Tagline));
    }

    /// <summary>La plataforma ve las listas de todos los tenants, así que el tenant de la lista se compara a mano:
    /// una escritura de la plataforma con la lista de OTRO tenant es 400 <c>lista_invalida</c>. Un actor de tenant
    /// no puede llegar a esto (RLS y el filtro de EF le esconden la lista), por eso el caso va directo al servicio.</summary>
    [Fact]
    public async Task UnaEscrituraDeLaPlataformaConLaListaDeOtroTenantDa400ListaInvalida()
    {
        var tienda = await apoyo.SembrarTiendaAsync("adm-lista-plataforma", vitrinaPublicada: false);
        var ajena = await apoyo.SembrarTiendaAsync("adm-lista-plataforma-ajena", vitrinaPublicada: false);
        var propia = await apoyo.SembrarListaAsync(tienda.IdTenant, "Propia");
        var deOtro = await apoyo.SembrarListaAsync(ajena.IdTenant, "Ajena");

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var servicio = new Ways.Application.Vitrinas.ServicioDeVitrinas(
            db, new RelojDelSistema(), new GuardaDeReferencias(db, new InspectorDeUso(db)));
        Ways.Application.Vitrinas.SolicitudDeVitrina Pedido(int idLista) =>
            new(Publicada: false, Tagline: null, Descripcion: null, ColorAcento: null, Tema: "oscuro", IdListaPrecio: idLista);

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.GuardarAsync(tienda.IdEmpresa, Pedido(deOtro)));
        Assert.Equal("lista_invalida", error.Codigo);
        Assert.Null(await apoyo.Vitrinas.LeerVitrinaAsync(tienda.IdEmpresa));

        var guardada = await servicio.GuardarAsync(tienda.IdEmpresa, Pedido(propia));
        Assert.Equal(propia, guardada.IdListaPrecio);
    }

    // ---- una lista que usa una vitrina no se desactiva ni se mueve --------------------------------

    private static ListaPrecioAlta AltaDeLista(string nombre, bool activo = true, int? idEmpresa = null) =>
        new(nombre, idEmpresa, EsDefault: false, ModoLista.Fija, IdListaBase: null, Porcentaje: null, Activo: activo);

    private static string RutaLista(int id) => $"/api/catalogos/listas-precio/{id}";

    private async Task<(bool Activo, int? IdEmpresa)> LeerListaAsync(int idLista)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var lista = await db.ListasPrecio.AsNoTracking().SingleAsync(l => l.Id == idLista);
        return (lista.Activo, lista.IdEmpresa);
    }

    /// <summary>
    /// Desactivar la lista de una vitrina, o cambiarle el alcance (<c>id_empresa</c>), es 409
    /// <c>lista_en_uso_por_vitrina</c> con el nombre de la empresa en el mensaje, y la lista queda como estaba.
    /// Cada línea mata una cláusula: la desactivación, el cambio de alcance, que el predicado sea la lista de ESA
    /// vitrina (una lista que no usa ninguna vitrina viva sí se desactiva y se mueve), que una edición que no la
    /// quita (renombrar) no se bloquee, y que una vitrina dada de baja no cuente.
    /// </summary>
    [Fact]
    public async Task UnaListaQueUsaUnaVitrinaNoSePuedeDesactivarNiMoverDeEmpresaPeroSiRenombrarlaNiSiLaUsaUnaVitrinaDadaDeBaja()
    {
        var (tienda, admin) = await TiendaConAdminAsync("adm-lista-vitrina");
        var otraEmpresa = await apoyo.Vitrinas.SembrarEmpresaAsync(tienda.IdTenant, "Otra empresa SA");
        var enUso = await apoyo.SembrarListaAsync(tienda.IdTenant, "En uso por la tienda");
        var libre = await apoyo.SembrarListaAsync(tienda.IdTenant, "Nadie la usa");
        var deLaBaja = await apoyo.SembrarListaAsync(tienda.IdTenant, "Solo la usaba una vitrina dada de baja");
        await apoyo.FijarListaDeLaVitrinaAsync(tienda.IdEmpresa, enUso);
        await apoyo.Vitrinas.SembrarVitrinaAsync(tienda.IdTenant, otraEmpresa, publicada: true, eliminada: true);
        await apoyo.FijarListaDeLaVitrinaAsync(otraEmpresa, deLaBaja);

        var desactivar = await admin.PutAsJsonAsync(RutaLista(enUso), AltaDeLista("En uso por la tienda", activo: false));
        Assert.Equal(HttpStatusCode.Conflict, desactivar.StatusCode);
        var errorDeDesactivar = await LeerAsync(desactivar);
        Assert.Equal("lista_en_uso_por_vitrina", errorDeDesactivar.GetProperty("codigo").GetString());
        Assert.Contains("Fantasía", errorDeDesactivar.GetProperty("title").GetString());

        var mover = await admin.PutAsJsonAsync(RutaLista(enUso), AltaDeLista("En uso por la tienda", idEmpresa: otraEmpresa));
        Assert.Equal(HttpStatusCode.Conflict, mover.StatusCode);
        Assert.Equal("lista_en_uso_por_vitrina", await ApoyoDeDominios.CodigoDeErrorAsync(mover));

        Assert.Equal((true, (int?)null), await LeerListaAsync(enUso));

        // Lo que no le quita la lista a la tienda no se bloquea.
        var renombrar = await admin.PutAsJsonAsync(RutaLista(enUso), AltaDeLista("Renombrada"));
        Assert.Equal(HttpStatusCode.OK, renombrar.StatusCode);

        // Una lista que ninguna vitrina viva usa se desactiva y se mueve, aunque otra lista sí esté en uso.
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync(RutaLista(libre), AltaDeLista("Nadie la usa", activo: false))).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await admin.PutAsJsonAsync(RutaLista(libre), AltaDeLista("Nadie la usa", idEmpresa: tienda.IdEmpresa))).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await admin.PutAsJsonAsync(RutaLista(deLaBaja), AltaDeLista("Solo la usaba una vitrina dada de baja", activo: false))).StatusCode);
        Assert.False((await LeerListaAsync(deLaBaja)).Activo);

        // Con la tienda en otra lista, la que usaba se puede desactivar.
        await apoyo.FijarListaDeLaVitrinaAsync(tienda.IdEmpresa, null);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync(RutaLista(enUso), AltaDeLista("Renombrada", activo: false))).StatusCode);
        Assert.False((await LeerListaAsync(enUso)).Activo);
    }

    /// <summary>
    /// La vitrina se asigna a la lista (<c>FOR KEY SHARE</c> sobre ella y <c>UPDATE</c> de la vitrina) y todavía no
    /// comiteó cuando llega la desactivación: la edición toma <c>FOR UPDATE</c>, espera, y al despertar ve la
    /// vitrina comiteada y contesta 409. Sin ese lock vería las vitrinas SIN la nueva, y su <c>UPDATE</c> de una
    /// columna que no es clave no choca con un <c>KEY SHARE</c>: la lista quedaría inactiva bajo una vitrina que
    /// la usa.
    /// </summary>
    [Fact]
    public async Task DesactivarUnaListaMientrasUnaVitrinaSeAsignaEsperaYDa409()
    {
        var (tienda, admin) = await TiendaConAdminAsync("adm-lista-carrera");
        var lista = await apoyo.SembrarListaAsync(tienda.IdTenant, "Carrera");

        await using var conexion = new NpgsqlConnection(fixture.OwnerConnectionString);
        await conexion.OpenAsync();
        await using var transaccion = await conexion.BeginTransactionAsync();
        await using (var comando = conexion.CreateCommand())
        {
            comando.Transaction = transaccion;
            comando.CommandText =
                $"SELECT 1 FROM listas_precio WHERE id_lista_precio = {lista} FOR KEY SHARE; " +
                $"UPDATE vitrinas SET id_lista_precio = {lista} WHERE id_empresa = {tienda.IdEmpresa}";
            await comando.ExecuteNonQueryAsync();
        }

        var desactivar = admin.PutAsJsonAsync(RutaLista(lista), AltaDeLista("Carrera", activo: false));
        await EsperarBloqueadosAsync(esperados: 1);

        await transaccion.CommitAsync();
        var respuesta = await desactivar;

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal("lista_en_uso_por_vitrina", await ApoyoDeDominios.CodigoDeErrorAsync(respuesta));
        Assert.Equal((true, (int?)null), await LeerListaAsync(lista));
        Assert.Equal(lista, (await apoyo.Vitrinas.LeerVitrinaAsync(tienda.IdEmpresa))!.IdListaPrecio);
    }

    private static object Solicitud(int? idListaPrecio, string? tagline = null) => new
    {
        publicada = false,
        tagline,
        descripcion = (string?)null,
        colorAcento = (string?)null,
        tema = "oscuro",
        idListaPrecio
    };
}
