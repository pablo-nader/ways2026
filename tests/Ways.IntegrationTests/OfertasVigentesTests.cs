using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Ofertas;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// Ofertas por artículo: el filtro <c>GET /api/ofertas?idArticulo=</c> (directas + de su familia) y
/// <c>POST /api/ofertas/vigentes</c> (todas las ofertas vivas de un lote de artículos, sin mirar la cantidad),
/// punta a punta contra Postgres real. La hora se controla con <c>momento</c> y se descompone con la misma
/// zona local del servidor que usa la resolución.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class OfertasVigentesTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordVendedor = "una-contraseña-larga";

    private static readonly DateTimeOffset Momento = new(2026, 6, 17, 15, 30, 0, TimeSpan.Zero);

    private static (DateOnly Fecha, TimeOnly Hora, int DiaIso) Local(DateTimeOffset momento)
    {
        var local = TimeZoneInfo.ConvertTime(momento, TimeZoneInfo.Local);
        var dia = (int)local.DayOfWeek;
        return (DateOnly.FromDateTime(local.DateTime), TimeOnly.FromDateTime(local.DateTime), dia == 0 ? 7 : dia);
    }

    private async Task<(int IdTenant, string Mail, string Password)> AprovisionarTenantAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mail = $"{nombre.ToLowerInvariant()}@ways.test";
        var solicitud = new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mail, ModoPuntoVenta.Web);
        var respuesta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>();
        Assert.NotNull(resultado);

        return (resultado!.IdTenant, mail, resultado.PasswordTemporal);
    }

    private async Task<HttpClient> ClienteLogueadoAsync(string mail, string password)
    {
        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    private async Task<string> SembrarVendedorAsync(int idTenant, string nombre)
    {
        var hasheador = new HasheadorPbkdf2();
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;
        var mail = $"{nombre.ToLowerInvariant()}-vendedor@ways.test";

        db.Usuarios.Add(new Usuario
        {
            IdTenant = idTenant,
            NombreUsuario = "vendedor",
            Mail = mail,
            RolId = (int)RolConocido.Vendedor,
            PasswordHash = hasheador.Hashear(PasswordVendedor),
            PasswordAlgoritmo = hasheador.Algoritmo,
            PasswordActualizadoEl = ahora,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        return mail;
    }

    private async Task<int> SembrarArticuloAsync(
        int idTenant, string nombre, int? idCategoria = null, int? idGrupo = null, int? idFamilia = null,
        bool dadoDeBaja = false)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var area = new Area { IdTenant = idTenant, Nombre = $"{nombre}-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();

        var articulo = new Articulo
        {
            IdTenant = idTenant,
            CodigoInterno = $"{nombre}-cod",
            Nombre = nombre,
            IdArea = area.Id,
            IdCategoria = idCategoria,
            IdGrupo = idGrupo,
            IdFamilia = idFamilia,
            IdAlicuotaIva = idAlicuotaIva,
            UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true,
            CreatedAt = ahora,
            UpdatedAt = ahora,
            DeletedAt = dadoDeBaja ? ahora : null
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        return articulo.Id;
    }

    private async Task<int> SembrarCategoriaAsync(int idTenant, string nombre, int? idPadre = null)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;
        var categoria = new Categoria
        {
            IdTenant = idTenant, Nombre = nombre, Orden = 1, IdCategoriaPadre = idPadre, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Categorias.Add(categoria);
        await db.SaveChangesAsync();
        return categoria.Id;
    }

    private async Task<int> SembrarGrupoAsync(int idTenant, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;
        var grupo = new Grupo { IdTenant = idTenant, Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.Grupos.Add(grupo);
        await db.SaveChangesAsync();
        return grupo.Id;
    }

    private async Task<int> SembrarFamiliaAsync(int idTenant, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;
        var familia = new Familia { IdTenant = idTenant, Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.Familias.Add(familia);
        await db.SaveChangesAsync();
        return familia.Id;
    }

    private async Task<int> SembrarEmpresaAsync(int idTenant, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;
        var empresa = new Empresa { IdTenant = idTenant, RazonSocial = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.Empresas.Add(empresa);
        await db.SaveChangesAsync();
        return empresa.Id;
    }

    private async Task<int> SembrarListaAsync(int idTenant, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;
        var lista = new ListaPrecio
        {
            IdTenant = idTenant, Nombre = nombre, EsDefault = false, Modo = ModoLista.Fija, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ListasPrecio.Add(lista);
        await db.SaveChangesAsync();
        return lista.Id;
    }

    private static AltaOferta Alta(
        string nombre, int? idArticulo = null, int? idGrupo = null, int? idCategoria = null, int? idFamilia = null,
        int? idEmpresa = null, decimal porcentaje = 10m, decimal? cantidadMinima = null, bool soloMultiplos = false,
        int prioridad = 0, bool acumulable = false, bool activo = true, IReadOnlyList<int>? idsListas = null,
        DateOnly? fechaDesde = null, DateOnly? fechaHasta = null, TimeOnly? horaDesde = null, TimeOnly? horaHasta = null,
        IReadOnlyList<int>? diasSemana = null) => new(
        Nombre: nombre, IdEmpresa: idEmpresa, IdArticulo: idArticulo, IdGrupo: idGrupo, IdCategoria: idCategoria,
        FechaDesde: fechaDesde, FechaHasta: fechaHasta, HoraDesde: horaDesde, HoraHasta: horaHasta, DiasSemana: diasSemana,
        CantidadMinima: cantidadMinima, PrecioUnitario: null, Porcentaje: porcentaje, ImporteFijo: null,
        Prioridad: prioridad, Acumulable: acumulable, IdsListas: idsListas, Activo: activo, IdFamilia: idFamilia,
        SoloMultiplos: soloMultiplos);

    private static async Task<OfertaListado> CrearAsync(HttpClient admin, AltaOferta alta)
    {
        var respuesta = await admin.PostAsJsonAsync("/api/ofertas", alta);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        return (await respuesta.Content.ReadFromJsonAsync<OfertaListado>())!;
    }

    private static async Task<List<OfertasVigentesDeArticulo>> VigentesAsync(
        HttpClient cliente, SolicitudDeVigentes solicitud)
    {
        var respuesta = await cliente.PostAsJsonAsync("/api/ofertas/vigentes", solicitud);
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        return (await respuesta.Content.ReadFromJsonAsync<List<OfertasVigentesDeArticulo>>())!;
    }

    private static SolicitudDeVigentes Pedido(
        int idEmpresa, int[] ids, int? idLista = null, DateTimeOffset? momento = null) =>
        new(ids, idEmpresa, idLista, momento ?? Momento);

    // ---- GET /api/ofertas?idArticulo= ---------------------------------------------------------

    [Fact]
    public async Task ElFiltroPorArticuloDevuelveLasDirectasYLasDeSuFamiliaYNadaMas()
    {
        var (idTenant, mail, password) =
            await AprovisionarTenantAsync(nameof(ElFiltroPorArticuloDevuelveLasDirectasYLasDeSuFamiliaYNadaMas));
        using var admin = await ClienteLogueadoAsync(mail, password);

        var idFamilia = await SembrarFamiliaAsync(idTenant, "familia");
        var idGrupo = await SembrarGrupoAsync(idTenant, "grupo");
        var idA = await SembrarArticuloAsync(idTenant, "articulo-a", idFamilia: idFamilia, idGrupo: idGrupo);
        var idB = await SembrarArticuloAsync(idTenant, "articulo-b");

        var directaA = await CrearAsync(admin, Alta("directa A", idArticulo: idA));
        var inactivaA = await CrearAsync(admin, Alta("inactiva A", idArticulo: idA, activo: false));
        var dadaDeBajaA = await CrearAsync(admin, Alta("baja A", idArticulo: idA));
        var directaB = await CrearAsync(admin, Alta("directa B", idArticulo: idB));
        var deFamilia = await CrearAsync(admin, Alta("de familia", idFamilia: idFamilia));
        var deGrupo = await CrearAsync(admin, Alta("de grupo", idGrupo: idGrupo));
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/ofertas/{dadaDeBajaA.Id}")).StatusCode);

        var deA = await admin.GetFromJsonAsync<List<OfertaListado>>($"/api/ofertas?idArticulo={idA}");
        Assert.Equal(
            new[] { directaA.Id, deFamilia.Id, inactivaA.Id }.Order(), deA!.Select(o => o.Id).Order());
        Assert.Equal("articulo-a", deA!.Single(o => o.Id == directaA.Id).NombreArticulo);
        Assert.Null(deA.Single(o => o.Id == deFamilia.Id).NombreArticulo);

        var deB = await admin.GetFromJsonAsync<List<OfertaListado>>($"/api/ofertas?idArticulo={idB}");
        Assert.Equal(directaB.Id, Assert.Single(deB!).Id);

        var todas = await admin.GetFromJsonAsync<List<OfertaListado>>("/api/ofertas");
        Assert.Equal(
            new[] { directaA.Id, inactivaA.Id, directaB.Id, deFamilia.Id, deGrupo.Id }.Order(),
            todas!.Select(o => o.Id).Order());
    }

    [Fact]
    public async Task ElFiltroPorArticuloInexistenteDadoDeBajaOAjenoDevuelveListaVacia()
    {
        var (idTenant, mail, password) = await AprovisionarTenantAsync(
            nameof(ElFiltroPorArticuloInexistenteDadoDeBajaOAjenoDevuelveListaVacia));
        using var admin = await ClienteLogueadoAsync(mail, password);
        var (idOtroTenant, mailOtro, passwordOtro) = await AprovisionarTenantAsync(
            nameof(ElFiltroPorArticuloInexistenteDadoDeBajaOAjenoDevuelveListaVacia) + "Otro");
        using var adminOtro = await ClienteLogueadoAsync(mailOtro, passwordOtro);

        var idPropio = await SembrarArticuloAsync(idTenant, "articulo-propio");
        await CrearAsync(admin, Alta("oferta propia", idArticulo: idPropio));

        var idDeBaja = await SembrarArticuloAsync(idTenant, "articulo-baja", dadoDeBaja: true);
        await SembrarOfertaCrudaAsync(idTenant, idDeBaja);
        _ = idOtroTenant;

        Assert.Empty((await admin.GetFromJsonAsync<List<OfertaListado>>("/api/ofertas?idArticulo=987654"))!);
        Assert.Empty((await admin.GetFromJsonAsync<List<OfertaListado>>($"/api/ofertas?idArticulo={idDeBaja}"))!);
        Assert.Empty((await adminOtro.GetFromJsonAsync<List<OfertaListado>>($"/api/ofertas?idArticulo={idPropio}"))!);
    }

    private async Task SembrarOfertaCrudaAsync(int idTenant, int idArticulo)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;
        db.Ofertas.Add(new Domain.Ofertas.Oferta
        {
            IdTenant = idTenant, Nombre = "de articulo de baja", IdArticulo = idArticulo, Porcentaje = 10m,
            Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    // ---- POST /api/ofertas/vigentes -----------------------------------------------------------

    [Fact]
    public async Task VigentesIncluyeLaOfertaPorCantidadConSuCondicionYOrdenaPorPrioridad()
    {
        var (idTenant, mail, password) = await AprovisionarTenantAsync(
            nameof(VigentesIncluyeLaOfertaPorCantidadConSuCondicionYOrdenaPorPrioridad));
        using var admin = await ClienteLogueadoAsync(mail, password);
        var idEmpresa = await SembrarEmpresaAsync(idTenant, "empresa");
        var idArticulo = await SembrarArticuloAsync(idTenant, "con-ofertas");
        var idSinOfertas = await SembrarArticuloAsync(idTenant, "sin-ofertas");
        var hasta = new DateOnly(2026, 12, 31);

        var porCantidad = await CrearAsync(admin, Alta(
            "llevando 3", idArticulo: idArticulo, cantidadMinima: 3m, soloMultiplos: true, porcentaje: 15m,
            prioridad: 1, acumulable: true, fechaHasta: hasta));
        var directa = await CrearAsync(admin, Alta("directa", idArticulo: idArticulo, porcentaje: 5m, prioridad: 9));

        var respuesta = await VigentesAsync(
            admin, Pedido(idEmpresa, [idArticulo, idSinOfertas, 987654]));

        var item = Assert.Single(respuesta);
        Assert.Equal(idArticulo, item.IdArticulo);
        Assert.Equal(new[] { directa.Id, porCantidad.Id }, item.Ofertas.Select(o => o.IdOferta));

        var cantidad = item.Ofertas[1];
        Assert.Equal("llevando 3", cantidad.Nombre);
        Assert.Equal("Articulo", cantidad.Alcance);
        Assert.Equal(3m, cantidad.CantidadMinima);
        Assert.True(cantidad.SoloMultiplos);
        Assert.Equal(15m, cantidad.Porcentaje);
        Assert.Null(cantidad.PrecioUnitario);
        Assert.Null(cantidad.ImporteFijo);
        Assert.Equal(hasta, cantidad.FechaHasta);
        Assert.True(cantidad.Acumulable);

        Assert.Null(item.Ofertas[0].CantidadMinima);
        Assert.False(item.Ofertas[0].SoloMultiplos);
    }

    [Fact]
    public async Task VigentesDevuelveElAlcanceDeCadaOfertaPorArticuloGrupoCategoriaYFamilia()
    {
        var (idTenant, mail, password) = await AprovisionarTenantAsync(
            nameof(VigentesDevuelveElAlcanceDeCadaOfertaPorArticuloGrupoCategoriaYFamilia));
        using var admin = await ClienteLogueadoAsync(mail, password);
        var idEmpresa = await SembrarEmpresaAsync(idTenant, "empresa");

        var idPadre = await SembrarCategoriaAsync(idTenant, "Bebidas");
        var idHija = await SembrarCategoriaAsync(idTenant, "Gaseosas", idPadre);
        var idGrupo = await SembrarGrupoAsync(idTenant, "grupo");
        var idFamilia = await SembrarFamiliaAsync(idTenant, "familia");

        var idCompleto = await SembrarArticuloAsync(
            idTenant, "completo", idCategoria: idHija, idGrupo: idGrupo, idFamilia: idFamilia);
        var idAjeno = await SembrarArticuloAsync(idTenant, "ajeno");

        var deArticulo = await CrearAsync(admin, Alta("art", idArticulo: idCompleto, prioridad: 4));
        var deGrupo = await CrearAsync(admin, Alta("grp", idGrupo: idGrupo, prioridad: 3));
        var deCategoria = await CrearAsync(admin, Alta("cat", idCategoria: idPadre, prioridad: 2));
        var deFamilia = await CrearAsync(admin, Alta("fam", idFamilia: idFamilia, prioridad: 1, cantidadMinima: 6m));

        var respuesta = await VigentesAsync(admin, Pedido(idEmpresa, [idCompleto, idAjeno]));

        var item = Assert.Single(respuesta);
        Assert.Equal(idCompleto, item.IdArticulo);
        Assert.Equal(
            new[]
            {
                (deArticulo.Id, "Articulo"), (deGrupo.Id, "Grupo"), (deCategoria.Id, "Categoria"), (deFamilia.Id, "Familia")
            },
            item.Ofertas.Select(o => (o.IdOferta, o.Alcance)));
        Assert.Equal(6m, item.Ofertas[3].CantidadMinima);
    }

    [Fact]
    public async Task VigentesExcluyeInactivasDadasDeBajaYDeOtraEmpresaPeroIncluyeLasDeTodoElTenant()
    {
        var (idTenant, mail, password) = await AprovisionarTenantAsync(
            nameof(VigentesExcluyeInactivasDadasDeBajaYDeOtraEmpresaPeroIncluyeLasDeTodoElTenant));
        using var admin = await ClienteLogueadoAsync(mail, password);
        var idEmpresa = await SembrarEmpresaAsync(idTenant, "empresa");
        var idOtraEmpresa = await SembrarEmpresaAsync(idTenant, "otra empresa");
        var idArticulo = await SembrarArticuloAsync(idTenant, "articulo");

        var todoElTenant = await CrearAsync(admin, Alta("todo el tenant", idArticulo: idArticulo));
        var deLaEmpresa = await CrearAsync(admin, Alta("de la empresa", idArticulo: idArticulo, idEmpresa: idEmpresa));
        await CrearAsync(admin, Alta("de otra empresa", idArticulo: idArticulo, idEmpresa: idOtraEmpresa));
        await CrearAsync(admin, Alta("inactiva", idArticulo: idArticulo, activo: false));
        var dadaDeBaja = await CrearAsync(admin, Alta("dada de baja", idArticulo: idArticulo));
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/ofertas/{dadaDeBaja.Id}")).StatusCode);

        var item = Assert.Single(await VigentesAsync(admin, Pedido(idEmpresa, [idArticulo])));

        Assert.Equal(
            new[] { todoElTenant.Id, deLaEmpresa.Id }.Order(), item.Ofertas.Select(o => o.IdOferta).Order());
    }

    [Fact]
    public async Task VigentesFiltraPorListaSoloCuandoSePideUnaYAceptaLasQueApuntanATodas()
    {
        var (idTenant, mail, password) = await AprovisionarTenantAsync(
            nameof(VigentesFiltraPorListaSoloCuandoSePideUnaYAceptaLasQueApuntanATodas));
        using var admin = await ClienteLogueadoAsync(mail, password);
        var idEmpresa = await SembrarEmpresaAsync(idTenant, "empresa");
        var idLista = await SembrarListaAsync(idTenant, "lista");
        var idOtraLista = await SembrarListaAsync(idTenant, "otra lista");
        var idArticulo = await SembrarArticuloAsync(idTenant, "articulo");

        var paraTodas = await CrearAsync(admin, Alta("para todas", idArticulo: idArticulo));
        var deLaLista = await CrearAsync(admin, Alta("de la lista", idArticulo: idArticulo, idsListas: [idLista]));
        var deOtraLista = await CrearAsync(admin, Alta("de otra lista", idArticulo: idArticulo, idsListas: [idOtraLista]));

        var conLista = Assert.Single(await VigentesAsync(admin, Pedido(idEmpresa, [idArticulo], idLista)));
        Assert.Equal(
            new[] { paraTodas.Id, deLaLista.Id }.Order(), conLista.Ofertas.Select(o => o.IdOferta).Order());

        var sinLista = Assert.Single(await VigentesAsync(admin, Pedido(idEmpresa, [idArticulo])));
        Assert.Equal(
            new[] { paraTodas.Id, deLaLista.Id, deOtraLista.Id }.Order(), sinLista.Ofertas.Select(o => o.IdOferta).Order());
    }

    [Fact]
    public async Task VigentesRespetaLaVentanaDeFechasHorasYDiasEnLaHoraLocalDelMomento()
    {
        var (idTenant, mail, password) = await AprovisionarTenantAsync(
            nameof(VigentesRespetaLaVentanaDeFechasHorasYDiasEnLaHoraLocalDelMomento));
        using var admin = await ClienteLogueadoAsync(mail, password);
        var idEmpresa = await SembrarEmpresaAsync(idTenant, "empresa");
        var idArticulo = await SembrarArticuloAsync(idTenant, "articulo");

        var (fecha, hora, dia) = Local(Momento);
        var otroDia = dia == 1 ? 2 : 1;

        var vigente = await CrearAsync(admin, Alta(
            "vigente", idArticulo: idArticulo, fechaDesde: fecha.AddDays(-1), fechaHasta: fecha.AddDays(1),
            horaDesde: new TimeOnly(0, 0), horaHasta: new TimeOnly(23, 59), diasSemana: [dia]));

        // Una hora de ventana alrededor del momento: la hora del día no puede cruzar la medianoche.
        var horaAntes = hora.Hour >= 2 ? hora.AddHours(-2) : new TimeOnly(0, 0);
        var horaDespues = hora.Hour <= 20 ? hora.AddHours(2) : new TimeOnly(23, 59);
        var enHorario = await CrearAsync(admin, Alta(
            "en horario", idArticulo: idArticulo, horaDesde: horaAntes, horaHasta: horaDespues));

        await CrearAsync(admin, Alta(
            "vencida", idArticulo: idArticulo, fechaDesde: fecha.AddDays(-10), fechaHasta: fecha.AddDays(-1)));
        await CrearAsync(admin, Alta("futura", idArticulo: idArticulo, fechaDesde: fecha.AddDays(1)));
        await CrearAsync(admin, Alta("otro dia", idArticulo: idArticulo, diasSemana: [otroDia]));

        if (hora.Hour >= 2)
        {
            await CrearAsync(admin, Alta(
                "antes de hora", idArticulo: idArticulo, horaDesde: new TimeOnly(0, 0), horaHasta: hora.AddHours(-1)));
        }

        if (hora.Hour <= 20)
        {
            await CrearAsync(admin, Alta(
                "despues de hora", idArticulo: idArticulo, horaDesde: hora.AddHours(1), horaHasta: new TimeOnly(23, 59)));
        }

        var item = Assert.Single(await VigentesAsync(admin, Pedido(idEmpresa, [idArticulo])));
        Assert.Equal(
            new[] { vigente.Id, enHorario.Id }.Order(), item.Ofertas.Select(o => o.IdOferta).Order());

        // Un día después la acotada a un día de la semana sale y la de solo horario sigue.
        var manana = await VigentesAsync(admin, Pedido(idEmpresa, [idArticulo], momento: Momento.AddDays(1)));
        var ids = manana.SelectMany(i => i.Ofertas).Select(o => o.IdOferta).ToList();
        Assert.Contains(enHorario.Id, ids);
        Assert.DoesNotContain(vigente.Id, ids);
    }

    [Theory]
    [InlineData("vacio", "ids_articulos_requeridos")]
    [InlineData("sinClave", "ids_articulos_requeridos")]
    [InlineData("masDe200", "ids_articulos_excedidos")]
    [InlineData("repetidos", "ids_articulos_invalidos")]
    [InlineData("noPositivo", "ids_articulos_invalidos")]
    [InlineData("sinEmpresa", "empresa_requerida")]
    public async Task VigentesRechazaUnCuerpoInvalidoConSuCodigo(string caso, string codigoEsperado)
    {
        var (_, mail, password) = await AprovisionarTenantAsync(
            nameof(VigentesRechazaUnCuerpoInvalidoConSuCodigo) + caso);
        using var admin = await ClienteLogueadoAsync(mail, password);

        var cuerpo = caso switch
        {
            "vacio" => """{"idsArticulos":[],"idEmpresa":1}""",
            "sinClave" => """{"idEmpresa":1}""",
            "masDe200" => $$"""{"idsArticulos":[{{string.Join(',', Enumerable.Range(1, 201))}}],"idEmpresa":1}""",
            "repetidos" => """{"idsArticulos":[5,5],"idEmpresa":1}""",
            "noPositivo" => """{"idsArticulos":[5,0],"idEmpresa":1}""",
            _ => """{"idsArticulos":[5]}"""
        };

        var respuesta = await admin.PostAsync(
            "/api/ofertas/vigentes", new StringContent(cuerpo, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        using var documento = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync());
        Assert.Equal(codigoEsperado, documento.RootElement.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task VigentesAceptaExactamente200Articulos()
    {
        var (_, mail, password) = await AprovisionarTenantAsync(nameof(VigentesAceptaExactamente200Articulos));
        using var admin = await ClienteLogueadoAsync(mail, password);

        var respuesta = await VigentesAsync(admin, Pedido(1, [.. Enumerable.Range(1, 200)]));

        Assert.Empty(respuesta);
    }

    [Fact]
    public async Task VigentesNoMuestraLasOfertasDeOtroTenantNiSusArticulos()
    {
        var (idTenant, mail, password) = await AprovisionarTenantAsync(
            nameof(VigentesNoMuestraLasOfertasDeOtroTenantNiSusArticulos));
        using var admin = await ClienteLogueadoAsync(mail, password);
        var (idOtro, mailOtro, passwordOtro) = await AprovisionarTenantAsync(
            nameof(VigentesNoMuestraLasOfertasDeOtroTenantNiSusArticulos) + "Otro");
        using var adminOtro = await ClienteLogueadoAsync(mailOtro, passwordOtro);

        var idEmpresa = await SembrarEmpresaAsync(idTenant, "empresa");
        var idEmpresaOtro = await SembrarEmpresaAsync(idOtro, "empresa otro");
        var idArticulo = await SembrarArticuloAsync(idTenant, "articulo");
        var idArticuloOtro = await SembrarArticuloAsync(idOtro, "articulo otro");
        await CrearAsync(admin, Alta("propia", idArticulo: idArticulo));
        await CrearAsync(adminOtro, Alta("ajena", idArticulo: idArticuloOtro));

        // Ids ajenos (artículo y empresa) no filtran nada del otro tenant: el artículo ajeno es inexistente para mí.
        Assert.Empty(await VigentesAsync(admin, Pedido(idEmpresa, [idArticuloOtro])));
        Assert.Empty(await VigentesAsync(admin, Pedido(idEmpresaOtro, [idArticuloOtro])));
        Assert.Empty(await VigentesAsync(adminOtro, Pedido(idEmpresaOtro, [idArticulo])));

        var propia = Assert.Single(await VigentesAsync(admin, Pedido(idEmpresa, [idArticulo, idArticuloOtro])));
        Assert.Equal("propia", Assert.Single(propia.Ofertas).Nombre);

        // Una empresa ajena se comporta como una inexistente: no rompe, solo quedan las ofertas de todo el tenant.
        var conEmpresaAjena = Assert.Single(await VigentesAsync(admin, Pedido(idEmpresaOtro, [idArticulo])));
        Assert.Equal("propia", Assert.Single(conEmpresaAjena.Ofertas).Nombre);
    }

    [Fact]
    public async Task UnVendedorConsultaLasOfertasVigentesYUnAnonimoNo()
    {
        var (idTenant, mail, password) = await AprovisionarTenantAsync(
            nameof(UnVendedorConsultaLasOfertasVigentesYUnAnonimoNo));
        using var admin = await ClienteLogueadoAsync(mail, password);
        var mailVendedor = await SembrarVendedorAsync(idTenant, nameof(UnVendedorConsultaLasOfertasVigentesYUnAnonimoNo));
        using var vendedor = await ClienteLogueadoAsync(mailVendedor, PasswordVendedor);
        var idEmpresa = await SembrarEmpresaAsync(idTenant, "empresa");
        var idArticulo = await SembrarArticuloAsync(idTenant, "articulo");
        await CrearAsync(admin, Alta("oferta", idArticulo: idArticulo));

        var item = Assert.Single(await VigentesAsync(vendedor, Pedido(idEmpresa, [idArticulo])));
        Assert.Single(item.Ofertas);

        var filtradas = await vendedor.GetFromJsonAsync<List<OfertaListado>>($"/api/ofertas?idArticulo={idArticulo}");
        Assert.Single(filtradas!);

        using var anonimo = fixture.CreateClient();
        var sinSesion = await anonimo.PostAsJsonAsync("/api/ofertas/vigentes", Pedido(idEmpresa, [idArticulo]));
        Assert.Equal(HttpStatusCode.Unauthorized, sinSesion.StatusCode);
    }
}
