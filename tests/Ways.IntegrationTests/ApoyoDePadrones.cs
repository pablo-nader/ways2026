using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>Apoyo de los tests de los cuatro padrones que ordenan, filtran y paginan en el servidor (clientes,
/// proveedores, personal y usuarios): un tenant aprovisionado con su admin, clientes por rol y un recorrido de todas las
/// páginas de a dos filas.</summary>
internal static class ApoyoDePadrones
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordSecundario = "una-contraseña-larga";

    private static readonly DateTimeOffset Momento = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    public sealed record Escenario(
        int IdTenant, int IdCondicionFiscalCf, int IdListaGeneral, int IdAdmin, string MailAdmin, string PasswordAdmin);

    public static async Task<Escenario> AprovisionarAsync(string nombre, WaysApiFixture fixture)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var respuesta = await root.PostAsJsonAsync(
            "/api/plataforma/tenants",
            new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin, ModoPuntoVenta.Web));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        return new Escenario(
            resultado.IdTenant,
            await db.CondicionesFiscales.Where(c => c.Codigo == "CF").Select(c => c.Id).SingleAsync(),
            await db.ListasPrecio.Where(l => l.IdTenant == resultado.IdTenant && l.EsDefault).Select(l => l.Id).SingleAsync(),
            await db.Usuarios.Where(u => u.Mail == mailAdmin).Select(u => u.Id).SingleAsync(),
            mailAdmin,
            resultado.PasswordTemporal);
    }

    public static async Task<HttpClient> ClienteAsync(string mail, string password, WaysApiFixture fixture)
    {
        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        return cliente;
    }

    public static Task<HttpClient> ClienteRootAsync(WaysApiFixture fixture) => ClienteAsync(MailRoot, PasswordRoot, fixture);

    public static async Task<HttpClient> ClienteConRolAsync(
        Escenario e, string sufijo, RolConocido rol, WaysApiFixture fixture)
    {
        var mail = $"{sufijo.ToLowerInvariant()}-{e.IdTenant}@ways.test";
        var hasheador = new HasheadorPbkdf2();
        await using (var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            db.Usuarios.Add(new Usuario
            {
                IdTenant = e.IdTenant,
                NombreUsuario = $"{rol.ToString().ToLowerInvariant()}-{sufijo}",
                Mail = mail,
                RolId = (int)rol,
                PasswordHash = hasheador.Hashear(PasswordSecundario),
                PasswordAlgoritmo = hasheador.Algoritmo,
                PasswordActualizadoEl = Momento,
                CreatedAt = Momento,
                UpdatedAt = Momento
            });
            await db.SaveChangesAsync();
        }

        return await ClienteAsync(mail, PasswordSecundario, fixture);
    }

    /// <summary>Pide todas las páginas de a <paramref name="tamanio"/> filas y devuelve los ids en el orden en que
    /// llegaron: si el orden se aplicara después de paginar, la secuencia no sería la del conjunto completo.</summary>
    public static async Task<List<int>> RecorrerAsync(HttpClient cliente, string ruta, string query, int tamanio = 2)
    {
        var ids = new List<int>();
        for (var pagina = 1; pagina <= 20; pagina++)
        {
            var separador = query.Length == 0 ? string.Empty : $"{query}&";
            var p = await cliente.GetFromJsonAsync<JsonElement>($"{ruta}?{separador}pagina={pagina}&tamanio={tamanio}");
            ids.AddRange(p.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt32()));
            if (pagina * tamanio >= p.GetProperty("total").GetInt32())
            {
                break;
            }
        }

        return ids;
    }

    public static async Task<JsonElement> PedirAsync(HttpClient cliente, string ruta, string query)
    {
        var respuesta = await cliente.GetAsync($"{ruta}?{query}");
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);

        return await respuesta.Content.ReadFromJsonAsync<JsonElement>();
    }

    public static List<int> IdsDe(JsonElement pagina) =>
        [.. pagina.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt32())];

    /// <summary>Los ids sembrados que indican los índices (cero es la primera fila del arreglo).</summary>
    public static List<int> PorIndice(IReadOnlyList<int> ids, string indices) =>
        [.. indices.Split(',').Select(i => ids[int.Parse(i)])];

    public static async Task<string> CodigoDeRechazoAsync(HttpResponseMessage respuesta)
    {
        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();

        return problema.GetProperty("codigo").GetString()!;
    }
}
