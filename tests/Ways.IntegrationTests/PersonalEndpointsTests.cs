using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Organizacion;
using Ways.Application.Plantel;
using Ways.Application.Usuarios;
using Ways.Domain.Clientes;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// Catálogo de personal punta a punta contra Postgres real: contrato del ABM, políticas
/// (lectura Supervisor y Admin, escritura solo Admin), reglas de vínculo, 404 uniforme
/// entre tenants y la carrera de los índices únicos parciales.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class PersonalEndpointsTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordOtroRol = "otro-rol-password-larga";

    private sealed record Contexto(
        int IdTenant, HttpClient Admin, HttpClient Supervisor, HttpClient Vendedor,
        int IdUsuarioSupervisor, int IdUsuarioVendedor);

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

        var (supervisor, idSupervisor) = await CrearYLoguearAsync(admin, nombre, "supervisor", RolConocido.Supervisor);
        var (vendedor, idVendedor) = await CrearYLoguearAsync(admin, nombre, "vendedor", RolConocido.Vendedor);

        return new Contexto(resultado.IdTenant, admin, supervisor, vendedor, idSupervisor, idVendedor);
    }

    private async Task<(HttpClient Cliente, int IdUsuario)> CrearYLoguearAsync(
        HttpClient admin, string nombre, string sufijo, RolConocido rol)
    {
        var corto = Guid.NewGuid().ToString("N")[..8];
        var mail = $"{nombre.ToLowerInvariant()}-{sufijo}@ways.test";
        var alta = await admin.PostAsJsonAsync(
            "/api/usuarios", new CrearUsuario($"{sufijo}-{corto}", mail, (int)rol, PasswordOtroRol));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var idCreado = (await alta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, PasswordOtroRol));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return (cliente, idCreado);
    }

    private async Task<int> SembrarClienteAsync(int idTenant, int numero, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;
        var idCondicionFiscal = await db.CondicionesFiscales.Where(c => c.Codigo == "CF").Select(c => c.Id).SingleAsync();
        var idLista = await db.ListasPrecio.Where(l => l.IdTenant == idTenant && l.EsDefault).Select(l => l.Id).SingleAsync();

        var cliente = new Cliente
        {
            IdTenant = idTenant, Numero = numero, Nombre = nombre, Apellido = "Gómez",
            IdCondicionFiscal = idCondicionFiscal, IdListaPrecio = idLista, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Clientes.Add(cliente);
        await db.SaveChangesAsync();
        return cliente.Id;
    }

    private async Task<int> IdDelConsumidorFinalAsync(int idTenant)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        return await db.Clientes
            .Where(c => c.IdTenant == idTenant && c.Numero == ReglaDeClientes.NumeroConsumidorFinal)
            .Select(c => c.Id)
            .SingleAsync();
    }

    private static DatosDePersonal Datos(
        string nombre = "Ana", decimal valorHora = 1200.50m, int? idUsuario = null, int? idCliente = null) =>
        new(nombre, "20-11111111-1", "11-5555-1111", "turno mañana", valorHora, null, idUsuario, idCliente);

    private static async Task<string?> CodigoAsync(HttpResponseMessage respuesta)
    {
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        return problema.GetProperty("codigo").GetString();
    }

    private static async Task<PersonalListado> CrearOkAsync(HttpClient admin, DatosDePersonal datos)
    {
        var respuesta = await admin.PostAsJsonAsync("/api/personal", datos);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        return (await respuesta.Content.ReadFromJsonAsync<PersonalListado>())!;
    }

    [Fact]
    public async Task ElAbmCompletoDelAdminDevuelveElContratoYLaBajaLoOcultaDeLaLectura()
    {
        var ctx = await PrepararAsync(nameof(ElAbmCompletoDelAdminDevuelveElContratoYLaBajaLoOcultaDeLaLectura));

        var alta = await ctx.Admin.PostAsJsonAsync("/api/personal", Datos());
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var creado = (await alta.Content.ReadFromJsonAsync<PersonalListado>())!;
        Assert.Equal($"/api/personal/{creado.Id}", alta.Headers.Location!.OriginalString);
        Assert.Equal("Ana", creado.Nombre);
        Assert.Equal("20-11111111-1", creado.Documento);
        Assert.Equal("11-5555-1111", creado.Telefono);
        Assert.Equal("turno mañana", creado.Observaciones);
        Assert.Equal(1200.50m, creado.ValorHora);
        Assert.Null(creado.IdUsuario);
        Assert.Null(creado.IdCliente);

        var edicion = await ctx.Admin.PutAsJsonAsync(
            $"/api/personal/{creado.Id}", new DatosDePersonal("Ana María", null, null, null, 1500m, null, null, null));
        Assert.Equal(HttpStatusCode.OK, edicion.StatusCode);
        var editado = (await edicion.Content.ReadFromJsonAsync<PersonalListado>())!;
        Assert.Equal("Ana María", editado.Nombre);
        Assert.Null(editado.Documento);
        Assert.Equal(1500m, editado.ValorHora);

        var lista = (await ctx.Admin.GetFromJsonAsync<PaginaDe<PersonalListado>>("/api/personal"))!;
        Assert.Equal(creado.Id, Assert.Single(lista.Items).Id);

        var baja = await ctx.Admin.DeleteAsync($"/api/personal/{creado.Id}");
        Assert.Equal(HttpStatusCode.NoContent, baja.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ctx.Admin.GetAsync($"/api/personal/{creado.Id}")).StatusCode);
        Assert.Empty((await ctx.Admin.GetFromJsonAsync<PaginaDe<PersonalListado>>("/api/personal"))!.Items);
        Assert.Equal(HttpStatusCode.NotFound, (await ctx.Admin.DeleteAsync($"/api/personal/{creado.Id}")).StatusCode);
    }

    [Fact]
    public async Task ElHorarioHabitualSePersisteEnAltaYEdicionYSeDevuelveEnListadoYDetalle()
    {
        var ctx = await PrepararAsync(nameof(ElHorarioHabitualSePersisteEnAltaYEdicionYSeDevuelveEnListadoYDetalle));
        decimal[] semana = [8m, 8m, 8m, 8m, 8.25m, 0m, 0m];
        decimal[] otra = [0m, 6m, 6m, 6m, 6m, 6m, 24m];

        var creado = await CrearOkAsync(ctx.Admin, Datos() with { HorarioHabitual = semana });
        Assert.Equal(semana, creado.HorarioHabitual);

        var lista = (await ctx.Admin.GetFromJsonAsync<PaginaDe<PersonalListado>>("/api/personal"))!;
        Assert.Equal(semana, Assert.Single(lista.Items).HorarioHabitual);

        var edicion = await ctx.Admin.PutAsJsonAsync(
            $"/api/personal/{creado.Id}", Datos() with { HorarioHabitual = otra });
        Assert.Equal(HttpStatusCode.OK, edicion.StatusCode);
        Assert.Equal(otra, (await edicion.Content.ReadFromJsonAsync<PersonalListado>())!.HorarioHabitual);
        Assert.Equal(otra, (await ctx.Admin.GetFromJsonAsync<PersonalListado>($"/api/personal/{creado.Id}"))!.HorarioHabitual);

        var sinHorario = await ctx.Admin.PutAsJsonAsync($"/api/personal/{creado.Id}", Datos());
        Assert.Equal(HttpStatusCode.OK, sinHorario.StatusCode);
        Assert.Null((await sinHorario.Content.ReadFromJsonAsync<PersonalListado>())!.HorarioHabitual);

        var crudo = await ctx.Admin.GetFromJsonAsync<JsonElement>($"/api/personal/{creado.Id}");
        Assert.Equal(JsonValueKind.Null, crudo.GetProperty("horarioHabitual").ValueKind);
    }

    [Fact]
    public async Task ElHorarioHabitualSeSerializaComoArregloDeSieteNumeros()
    {
        var ctx = await PrepararAsync(nameof(ElHorarioHabitualSeSerializaComoArregloDeSieteNumeros));

        var alta = await ctx.Admin.PostAsJsonAsync(
            "/api/personal", Datos() with { HorarioHabitual = [8m, 8m, 8m, 8m, 8m, 4.5m, 0m] });

        var crudo = await alta.Content.ReadFromJsonAsync<JsonElement>();
        var horario = crudo.GetProperty("horarioHabitual");
        Assert.Equal(7, horario.GetArrayLength());
        Assert.Equal(4.5m, horario[5].GetDecimal());
    }

    [Theory]
    [InlineData("6 valores")]
    [InlineData("8 valores")]
    [InlineData("24.01")]
    [InlineData("negativo")]
    [InlineData("tres decimales")]
    public async Task UnHorarioHabitualInvalidoResponde400EnAltaYEdicion(string caso)
    {
        var ctx = await PrepararAsync(nameof(UnHorarioHabitualInvalidoResponde400EnAltaYEdicion) + caso);
        decimal[] horario = caso switch
        {
            "6 valores" => [8m, 8m, 8m, 8m, 8m, 8m],
            "8 valores" => [8m, 8m, 8m, 8m, 8m, 8m, 8m, 8m],
            "24.01" => [8m, 8m, 8m, 24.01m, 8m, 0m, 0m],
            "negativo" => [8m, -1m, 8m, 8m, 8m, 0m, 0m],
            _ => [8m, 8m, 8m, 8m, 8.001m, 0m, 0m]
        };

        var alta = await ctx.Admin.PostAsJsonAsync("/api/personal", Datos() with { HorarioHabitual = horario });
        Assert.Equal(HttpStatusCode.BadRequest, alta.StatusCode);
        Assert.Equal("horario_habitual_invalido", await CodigoAsync(alta));

        var creado = await CrearOkAsync(ctx.Admin, Datos());
        var edicion = await ctx.Admin.PutAsJsonAsync($"/api/personal/{creado.Id}", Datos() with { HorarioHabitual = horario });
        Assert.Equal(HttpStatusCode.BadRequest, edicion.StatusCode);
        Assert.Equal("horario_habitual_invalido", await CodigoAsync(edicion));
    }

    [Fact]
    public async Task EditarUnPersonalInexistenteResponde404AunConHorarioInvalido()
    {
        var ctx = await PrepararAsync(nameof(EditarUnPersonalInexistenteResponde404AunConHorarioInvalido));

        var edicion = await ctx.Admin.PutAsJsonAsync(
            "/api/personal/999999", Datos() with { HorarioHabitual = [1m, 2m] });

        Assert.Equal(HttpStatusCode.NotFound, edicion.StatusCode);
    }

    [Fact]
    public async Task ElListadoMuestraLosNombresDelUsuarioYDelClienteVinculados()
    {
        var ctx = await PrepararAsync(nameof(ElListadoMuestraLosNombresDelUsuarioYDelClienteVinculados));
        var idCliente = await SembrarClienteAsync(ctx.IdTenant, 7, "Lucía");

        var creado = await CrearOkAsync(ctx.Admin, Datos(idUsuario: ctx.IdUsuarioSupervisor, idCliente: idCliente));

        var leido = (await ctx.Admin.GetFromJsonAsync<PersonalListado>($"/api/personal/{creado.Id}"))!;
        Assert.Equal(ctx.IdUsuarioSupervisor, leido.IdUsuario);
        Assert.StartsWith("supervisor-", leido.NombreUsuario);
        Assert.Equal(idCliente, leido.IdCliente);
        Assert.Equal("Lucía Gómez", leido.NombreCliente);
    }

    [Fact]
    public async Task ElSupervisorLeeYNoEscribeYElVendedorNoLee()
    {
        var ctx = await PrepararAsync(nameof(ElSupervisorLeeYNoEscribeYElVendedorNoLee));
        var creado = await CrearOkAsync(ctx.Admin, Datos());

        Assert.Equal(HttpStatusCode.OK, (await ctx.Supervisor.GetAsync("/api/personal")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ctx.Supervisor.GetAsync($"/api/personal/{creado.Id}")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await ctx.Supervisor.PostAsJsonAsync("/api/personal", Datos("Otro"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ctx.Supervisor.PutAsJsonAsync($"/api/personal/{creado.Id}", Datos("Otro"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ctx.Supervisor.DeleteAsync($"/api/personal/{creado.Id}")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await ctx.Vendedor.GetAsync("/api/personal")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ctx.Vendedor.GetAsync($"/api/personal/{creado.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ctx.Vendedor.PostAsJsonAsync("/api/personal", Datos("Otro"))).StatusCode);

        using var anonimo = fixture.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.GetAsync("/api/personal")).StatusCode);
    }

    [Fact]
    public async Task ElClienteConsumidorFinalSeRechazaConSuCodigo()
    {
        var ctx = await PrepararAsync(nameof(ElClienteConsumidorFinalSeRechazaConSuCodigo));
        var idConsumidorFinal = await IdDelConsumidorFinalAsync(ctx.IdTenant);

        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/personal", Datos(idCliente: idConsumidorFinal));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal("cliente_consumidor_final", await CodigoAsync(respuesta));
    }

    [Fact]
    public async Task UnUsuarioDeOtroTenantOdeLaPlataformaSeRechazaComoInvalido()
    {
        var ctx = await PrepararAsync(nameof(UnUsuarioDeOtroTenantOdeLaPlataformaSeRechazaComoInvalido) + "-A");
        var otro = await PrepararAsync(nameof(UnUsuarioDeOtroTenantOdeLaPlataformaSeRechazaComoInvalido) + "-B");

        int idRoot;
        await using (var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            idRoot = await db.Usuarios.Where(u => u.Mail == MailRoot).Select(u => u.Id).SingleAsync();
        }

        foreach (var idUsuario in new[] { otro.IdUsuarioSupervisor, idRoot, 999_999 })
        {
            var respuesta = await ctx.Admin.PostAsJsonAsync("/api/personal", Datos(idUsuario: idUsuario));
            Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
            Assert.Equal("usuario_invalido", await CodigoAsync(respuesta));
        }
    }

    [Fact]
    public async Task UnUsuarioOClienteYaVinculadoDa409YLaBajaDelPersonalLosLibera()
    {
        var ctx = await PrepararAsync(nameof(UnUsuarioOClienteYaVinculadoDa409YLaBajaDelPersonalLosLibera));
        var idCliente = await SembrarClienteAsync(ctx.IdTenant, 7, "Lucía");
        var primero = await CrearOkAsync(ctx.Admin, Datos("Ana", idUsuario: ctx.IdUsuarioVendedor, idCliente: idCliente));

        var porUsuario = await ctx.Admin.PostAsJsonAsync("/api/personal", Datos("Beto", idUsuario: ctx.IdUsuarioVendedor));
        Assert.Equal(HttpStatusCode.Conflict, porUsuario.StatusCode);
        Assert.Equal("usuario_ya_vinculado", await CodigoAsync(porUsuario));

        var porCliente = await ctx.Admin.PostAsJsonAsync("/api/personal", Datos("Beto", idCliente: idCliente));
        Assert.Equal(HttpStatusCode.Conflict, porCliente.StatusCode);
        Assert.Equal("cliente_ya_vinculado", await CodigoAsync(porCliente));

        Assert.Equal(HttpStatusCode.NoContent, (await ctx.Admin.DeleteAsync($"/api/personal/{primero.Id}")).StatusCode);

        var libre = await ctx.Admin.PostAsJsonAsync(
            "/api/personal", Datos("Beto", idUsuario: ctx.IdUsuarioVendedor, idCliente: idCliente));
        Assert.Equal(HttpStatusCode.Created, libre.StatusCode);
    }

    [Fact]
    public async Task ActualizarConUnVinculoDeOtroPersonalDa409YConSusPropiosVinculosDa200()
    {
        var ctx = await PrepararAsync(nameof(ActualizarConUnVinculoDeOtroPersonalDa409YConSusPropiosVinculosDa200));
        var ana = await CrearOkAsync(ctx.Admin, Datos("Ana", idUsuario: ctx.IdUsuarioVendedor));
        var beto = await CrearOkAsync(ctx.Admin, Datos("Beto"));

        var choque = await ctx.Admin.PutAsJsonAsync(
            $"/api/personal/{beto.Id}", Datos("Beto", idUsuario: ctx.IdUsuarioVendedor));
        Assert.Equal(HttpStatusCode.Conflict, choque.StatusCode);
        Assert.Equal("usuario_ya_vinculado", await CodigoAsync(choque));

        var propio = await ctx.Admin.PutAsJsonAsync(
            $"/api/personal/{ana.Id}", Datos("Ana", idUsuario: ctx.IdUsuarioVendedor));
        Assert.Equal(HttpStatusCode.OK, propio.StatusCode);
    }

    [Fact]
    public async Task UnIdInexistenteOAjenoDa404AntesQueLaValidacionDelCuerpo()
    {
        var ctx = await PrepararAsync(nameof(UnIdInexistenteOAjenoDa404AntesQueLaValidacionDelCuerpo) + "-A");
        var otro = await PrepararAsync(nameof(UnIdInexistenteOAjenoDa404AntesQueLaValidacionDelCuerpo) + "-B");
        var ajeno = await CrearOkAsync(otro.Admin, Datos("Ajeno"));
        var eliminado = await CrearOkAsync(ctx.Admin, Datos("Eliminado"));
        await ctx.Admin.DeleteAsync($"/api/personal/{eliminado.Id}");

        var cuerpoInvalido = new DatosDePersonal("   ", null, null, null, -1m, null, 999_999, 999_999);

        foreach (var id in new[] { 999_999, ajeno.Id, eliminado.Id })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await ctx.Admin.PutAsJsonAsync($"/api/personal/{id}", cuerpoInvalido)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await ctx.Admin.GetAsync($"/api/personal/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await ctx.Admin.DeleteAsync($"/api/personal/{id}")).StatusCode);
        }

        Assert.Empty((await ctx.Admin.GetFromJsonAsync<PaginaDe<PersonalListado>>("/api/personal"))!.Items);
        Assert.Equal("Ajeno", (await otro.Admin.GetFromJsonAsync<PersonalListado>($"/api/personal/{ajeno.Id}"))!.Nombre);
    }

    [Fact]
    public async Task UnVinculoConUnUsuarioOClienteDadoDeBajaSeMuestraSinVincular()
    {
        var ctx = await PrepararAsync(nameof(UnVinculoConUnUsuarioOClienteDadoDeBajaSeMuestraSinVincular));
        var idCliente = await SembrarClienteAsync(ctx.IdTenant, 7, "Lucía");
        var creado = await CrearOkAsync(ctx.Admin, Datos(idUsuario: ctx.IdUsuarioVendedor, idCliente: idCliente));

        await using (var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            var ahora = DateTimeOffset.UtcNow;
            await db.Usuarios.Where(u => u.Id == ctx.IdUsuarioVendedor)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.DeletedAt, ahora));
            await db.Clientes.Where(c => c.Id == idCliente)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.DeletedAt, ahora));
        }

        var leido = (await ctx.Admin.GetFromJsonAsync<PersonalListado>($"/api/personal/{creado.Id}"))!;
        var lista = (await ctx.Admin.GetFromJsonAsync<PaginaDe<PersonalListado>>("/api/personal"))!;

        Assert.Equal(1, lista.Total);
        foreach (var fila in new[] { leido, lista.Items.Single() })
        {
            Assert.Null(fila.IdUsuario);
            Assert.Null(fila.NombreUsuario);
            Assert.Null(fila.IdCliente);
            Assert.Null(fila.NombreCliente);
        }
    }

    [Fact]
    public async Task LaBusquedaFiltraPorNombreYDocumentoSinAcentos()
    {
        var ctx = await PrepararAsync(nameof(LaBusquedaFiltraPorNombreYDocumentoSinAcentos));
        await CrearOkAsync(ctx.Admin, new DatosDePersonal("José Pérez", "111", null, null, 0m, null, null, null));
        await CrearOkAsync(ctx.Admin, new DatosDePersonal("Marta", "222", null, null, 0m, null, null, null));

        var porNombre = (await ctx.Admin.GetFromJsonAsync<PaginaDe<PersonalListado>>("/api/personal?busqueda=jose"))!;
        var porDocumento = (await ctx.Admin.GetFromJsonAsync<PaginaDe<PersonalListado>>("/api/personal?busqueda=222"))!;

        Assert.Equal("José Pérez", Assert.Single(porNombre.Items).Nombre);
        Assert.Equal("Marta", Assert.Single(porDocumento.Items).Nombre);
    }

    [Fact]
    public async Task DosAltasConcurrentesDelMismoUsuarioDanExactamenteUnGanador()
    {
        var ctx = await PrepararAsync(nameof(DosAltasConcurrentesDelMismoUsuarioDanExactamenteUnGanador));

        var respuestas = await Task.WhenAll(
            ctx.Admin.PostAsJsonAsync("/api/personal", Datos("A", idUsuario: ctx.IdUsuarioVendedor)),
            ctx.Admin.PostAsJsonAsync("/api/personal", Datos("B", idUsuario: ctx.IdUsuarioVendedor)));

        Assert.Single(respuestas, r => r.StatusCode == HttpStatusCode.Created);
        var perdedora = Assert.Single(respuestas, r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal("usuario_ya_vinculado", await CodigoAsync(perdedora));
    }

    [Fact]
    public async Task DosAltasConcurrentesDelMismoClienteDanExactamenteUnGanador()
    {
        var ctx = await PrepararAsync(nameof(DosAltasConcurrentesDelMismoClienteDanExactamenteUnGanador));
        var idCliente = await SembrarClienteAsync(ctx.IdTenant, 7, "Lucía");

        var respuestas = await Task.WhenAll(
            ctx.Admin.PostAsJsonAsync("/api/personal", Datos("A", idCliente: idCliente)),
            ctx.Admin.PostAsJsonAsync("/api/personal", Datos("B", idCliente: idCliente)));

        Assert.Single(respuestas, r => r.StatusCode == HttpStatusCode.Created);
        var perdedora = Assert.Single(respuestas, r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal("cliente_ya_vinculado", await CodigoAsync(perdedora));
    }
}
