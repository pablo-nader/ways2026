using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using OpenIddict.Abstractions;
using Ways.Api.ConectorMcp;
using static Ways.IntegrationTests.ApoyoDeConectorMcp;

namespace Ways.IntegrationTests;

/// <summary>
/// El almacén de OpenIddict vive en la base de Ways: dos hosts que comparten la base simulan un
/// redeploy, porque el segundo arranca con otro proceso de servicios, otras claves efímeras y nada en
/// memoria del primero.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ConectorMcpAlmacenPersistenteTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private readonly ApoyoDeConectorMcp _apoyo = new(fixture);

    private static string Campo(JsonElement json, string nombre) => json.GetProperty(nombre).GetString()!;

    [Fact]
    public async Task LosTokensEmitidosAntesDeUnRedeploySiguenSirviendoEnElHostNuevo()
    {
        var usuario = await _apoyo.SembrarUsuarioDeTenantAsync("conector-redeploy");
        JsonElement tokens;
        await using (var antes = _apoyo.HostConConector(usuario.Mail))
        {
            using var cliente = Cliente(antes);
            tokens = await ObtenerTokensAsync(cliente, usuario.Mail);
        }

        await using var despues = _apoyo.HostConConector(usuario.Mail);
        using var clienteNuevo = Cliente(despues);

        Assert.Equal(HttpStatusCode.OK, (await LlamarMcpAsync(clienteNuevo, Campo(tokens, "access_token"), "tools/list")).Estado);
        var refresco = await RefrescarAsync(clienteNuevo, Campo(tokens, "refresh_token"));
        Assert.Equal(HttpStatusCode.OK, refresco.StatusCode);
        var renovados = await refresco.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.OK, (await LlamarMcpAsync(clienteNuevo, Campo(renovados, "access_token"), "tools/list")).Estado);
    }

    [Fact]
    public async Task ReusarDespuesDelRedeployUnRefreshCanjeadoAntesCortaLaCadenaEnElHostNuevo()
    {
        var usuario = await _apoyo.SembrarUsuarioDeTenantAsync("conector-reuso-redeploy");
        string refreshCanjeado;
        string accessDeLaCadena;
        await using (var antes = _apoyo.HostConConector(usuario.Mail, toleranciaDeReuso: 0))
        {
            using var cliente = Cliente(antes);
            refreshCanjeado = Campo(await ObtenerTokensAsync(cliente, usuario.Mail), "refresh_token");
            var refresco = await RefrescarAsync(cliente, refreshCanjeado);
            Assert.Equal(HttpStatusCode.OK, refresco.StatusCode);
            accessDeLaCadena = Campo(await refresco.Content.ReadFromJsonAsync<JsonElement>(), "access_token");
        }

        await using var despues = _apoyo.HostConConector(usuario.Mail, toleranciaDeReuso: 0);
        using var clienteNuevo = Cliente(despues);
        Assert.Equal(HttpStatusCode.OK, (await LlamarMcpAsync(clienteNuevo, accessDeLaCadena, "tools/list")).Estado);

        var reuso = await RefrescarAsync(clienteNuevo, refreshCanjeado);

        Assert.Equal(HttpStatusCode.BadRequest, reuso.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LlamarMcpAsync(clienteNuevo, accessDeLaCadena, "tools/list")).Estado);
    }

    [Fact]
    public async Task LaMigracionDelAlmacenLaAplicaElRolDeLaAplicacionConSuPropiaTablaDeHistorial()
    {
        await using (var host = _apoyo.HostConConector(MailRoot))
        {
            using var _ = Cliente(host);
        }

        await using var conexion = new NpgsqlConnection(fixture.AppConnectionString);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            $"""
            SELECT tablename, tableowner = current_user
            FROM pg_tables
            WHERE schemaname = 'public'
              AND tablename IN ('{ContextoOAuthDelConector.TablaDeAplicaciones}', '{ContextoOAuthDelConector.TablaDeAutorizaciones}',
                                '{ContextoOAuthDelConector.TablaDeAlcances}', '{ContextoOAuthDelConector.TablaDeTokens}',
                                '{ContextoOAuthDelConector.TablaDeHistorial}')
            ORDER BY tablename
            """;

        var duenias = new Dictionary<string, bool>();
        await using (var lector = await comando.ExecuteReaderAsync())
        {
            while (await lector.ReadAsync())
            {
                duenias[lector.GetString(0)] = lector.GetBoolean(1);
            }
        }

        Assert.Equal(5, duenias.Count);
        Assert.All(duenias, tabla => Assert.True(tabla.Value, $"{tabla.Key} no es del rol de la aplicación"));

        comando.CommandText = $"SELECT count(*) FROM \"{ContextoOAuthDelConector.TablaDeHistorial}\"";
        Assert.True((long)(await comando.ExecuteScalarAsync())! >= 1);
    }

    [Fact]
    public async Task LaPodaBorraLoVencidoViejoYConservaLoVencidoRecienteYLoVigente()
    {
        await using var host = _apoyo.HostConConector(MailRoot);
        using var _ = Cliente(host);
        var ahora = DateTimeOffset.UtcNow;
        var sujeto = $"poda-{Guid.NewGuid():N}";

        string vencidoViejo, vencidoReciente, vigenteViejo;
        await using (var alcance = host.Services.CreateAsyncScope())
        {
            var tokens = alcance.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
            vencidoViejo = await CrearTokenAsync(tokens, sujeto, creado: ahora.AddDays(-30), vence: ahora.AddDays(-29));
            vencidoReciente = await CrearTokenAsync(tokens, sujeto, creado: ahora.AddDays(-2), vence: ahora.AddDays(-1));
            vigenteViejo = await CrearTokenAsync(tokens, sujeto, creado: ahora.AddDays(-30), vence: ahora.AddDays(1));
        }

        var poda = new PodaDelAlmacenDelConector(
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            EstadoDelConectorMcp.Registrado(),
            new FakeTimeProvider(ahora),
            NullLogger<PodaDelAlmacenDelConector>.Instance);
        await poda.PodarAsync(CancellationToken.None);

        await using var verificacion = host.Services.CreateAsyncScope();
        var manager = verificacion.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
        Assert.Null(await manager.FindByIdAsync(vencidoViejo));
        Assert.NotNull(await manager.FindByIdAsync(vencidoReciente));
        Assert.NotNull(await manager.FindByIdAsync(vigenteViejo));
    }

    private static async Task<string> CrearTokenAsync(
        IOpenIddictTokenManager tokens, string sujeto, DateTimeOffset creado, DateTimeOffset vence)
    {
        var token = await tokens.CreateAsync(new OpenIddictTokenDescriptor
        {
            CreationDate = creado,
            ExpirationDate = vence,
            Status = OpenIddictConstants.Statuses.Valid,
            Subject = sujeto,
            Type = OpenIddictConstants.TokenTypeHints.RefreshToken
        });

        return (await tokens.GetIdAsync(token))!;
    }
}
