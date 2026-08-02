using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Clientes;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios; // PaginaDe<T>, SolicitudDeLogin
using Ways.Domain.Clientes;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// Slice 2 (tasks 2.4-2.5, db-error-backstops skill): <c>ServicioDeClientes</c>/
/// <c>ClientesEndpoints</c> punta a punta contra Postgres real — atomicidad del contador bajo
/// concurrencia genuina, <c>numero_documento</c> sin unicidad (spec), ABM completo con la
/// policy <c>GestionDeCatalogo</c> (admin-only), y el 404 uniforme cross-tenant (ADR-8).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ClientesEndpointsTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordVendedor = "una-contraseña-larga";

    private async Task<(int IdTenant, int IdCondicionFiscalCf, int IdListaPrecioGeneral, string MailAdmin, string PasswordAdmin)>
        AprovisionarTenantAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var solicitud = new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin);

        var respuesta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>();
        Assert.NotNull(resultado);

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var idCondicionFiscalCf = await db.CondicionesFiscales.Where(c => c.Codigo == "CF").Select(c => c.Id).SingleAsync();
        var idListaPrecioGeneral = await db.ListasPrecio
            .Where(l => l.IdTenant == resultado!.IdTenant && l.EsDefault)
            .Select(l => l.Id)
            .SingleAsync();

        return (resultado!.IdTenant, idCondicionFiscalCf, idListaPrecioGeneral, mailAdmin, resultado.PasswordTemporal);
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

    /// <summary>Spec: Atomic Per-Tenant Numero Assignment / Concurrent creation produces no
    /// gaps or duplicates. Sin interceptor de rendezvous (a diferencia de
    /// <c>ParametrosTests</c>): el camino de <c>AsignadorDeNumeroCliente</c> es un
    /// <c>UPDATE ... RETURNING</c> incondicional sobre la fila del contador, que Postgres ya
    /// serializa con su propio lock de fila — mismo hallazgo confirmado sin forzar nada en
    /// <c>AsignadorDeNumeroClienteConcurrenciaTests</c> (Slice 1, batch 3). Dos POST lanzados
    /// con <c>Task.WhenAll</c> alcanzan para probar la concurrencia real.</summary>
    [Fact]
    public async Task LaCreacionConcurrenteAsignaNumerosSecuencialesSinExponerElBackstop()
    {
        var (_, idCondicionFiscalCf, idListaPrecioGeneral, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(LaCreacionConcurrenteAsignaNumerosSecuencialesSinExponerElBackstop));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var altaA = new AltaCliente(
            "Cliente A", null, null, null, null, idCondicionFiscalCf, null, null, null, null, null, null,
            idListaPrecioGeneral);
        var altaB = altaA with { Nombre = "Cliente B" };

        var tareaA = admin.PostAsJsonAsync("/api/clientes", altaA);
        var tareaB = admin.PostAsJsonAsync("/api/clientes", altaB);

        var respuestas = await Task.WhenAll(tareaA, tareaB);

        Assert.All(respuestas, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));

        var creados = await Task.WhenAll(respuestas.Select(r => r.Content.ReadFromJsonAsync<ClienteListado>()));
        var numeros = creados.Select(c => c!.Numero).OrderBy(n => n).ToList();

        // El Consumidor Final del aprovisionamiento ya tomó el numero 1 — las dos altas
        // concurrentes tienen que ser exactamente 2 y 3, sin huecos ni duplicados.
        Assert.Equal([2, 3], numeros);
    }

    /// <summary>Spec: numero_documento Has No Uniqueness Constraint.</summary>
    [Fact]
    public async Task NumeroDocumentoDuplicadoYNuloSonAceptados()
    {
        var (_, idCondicionFiscalCf, idListaPrecioGeneral, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(NumeroDocumentoDuplicadoYNuloSonAceptados));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var primero = new AltaCliente(
            "Primero", null, null, null, "30712345678", idCondicionFiscalCf, null, null, null, null, null, null,
            idListaPrecioGeneral);
        var segundo = primero with { Nombre = "Segundo" };
        var tercero = primero with { Nombre = "Tercero", NumeroDocumento = null };

        var respuestaPrimero = await admin.PostAsJsonAsync("/api/clientes", primero);
        var respuestaSegundo = await admin.PostAsJsonAsync("/api/clientes", segundo);
        var respuestaTercero = await admin.PostAsJsonAsync("/api/clientes", tercero);

        Assert.Equal(HttpStatusCode.Created, respuestaPrimero.StatusCode);
        Assert.Equal(HttpStatusCode.Created, respuestaSegundo.StatusCode);
        Assert.Equal(HttpStatusCode.Created, respuestaTercero.StatusCode);
    }

    /// <summary>Spec: Create a cliente with default credit fields.</summary>
    [Fact]
    public async Task CrearSinCamposDeCreditoUsaLosDefaults()
    {
        var (_, idCondicionFiscalCf, idListaPrecioGeneral, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(CrearSinCamposDeCreditoUsaLosDefaults));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var alta = new AltaCliente(
            "Sin crédito", null, null, null, null, idCondicionFiscalCf, null, null, null, null, null, null,
            idListaPrecioGeneral);

        var respuesta = await admin.PostAsJsonAsync("/api/clientes", alta);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);

        var creado = await respuesta.Content.ReadFromJsonAsync<ClienteListado>();
        Assert.Equal(0m, creado!.LimiteCredito);
        Assert.False(creado.CreditoIlimitado);
        Assert.Equal(0m, creado.Saldo);
    }

    /// <summary>Judgment-day ronda 1 (item 2): <c>LimiteCredito</c> negativo se rechaza con
    /// 400 <c>limite_credito_invalido</c> a través del pipeline HTTP completo.</summary>
    [Fact]
    public async Task CrearConLimiteCreditoNegativoDevuelve400()
    {
        var (_, idCondicionFiscalCf, idListaPrecioGeneral, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(CrearConLimiteCreditoNegativoDevuelve400));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var alta = new AltaCliente(
            "Crédito inválido", null, null, null, null, idCondicionFiscalCf, null, null, null, null, null, null,
            idListaPrecioGeneral, LimiteCredito: -100);

        var respuesta = await admin.PostAsJsonAsync("/api/clientes", alta);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
    }

    /// <summary>Spec: Admin creates and soft-deletes a cliente.</summary>
    [Fact]
    public async Task UnAdminCreaYDaDeBajaUnCliente()
    {
        var (_, idCondicionFiscalCf, idListaPrecioGeneral, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(UnAdminCreaYDaDeBajaUnCliente));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        var alta = new AltaCliente(
            "De alta y baja", null, null, null, null, idCondicionFiscalCf, null, null, null, null, null, null,
            idListaPrecioGeneral);

        var respuestaAlta = await admin.PostAsJsonAsync("/api/clientes", alta);
        Assert.Equal(HttpStatusCode.Created, respuestaAlta.StatusCode);
        var creado = await respuestaAlta.Content.ReadFromJsonAsync<ClienteListado>();

        var respuestaBaja = await admin.DeleteAsync($"/api/clientes/{creado!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, respuestaBaja.StatusCode);

        var listado = await admin.GetFromJsonAsync<PaginaDe<ClienteListado>>("/api/clientes?busqueda=De+alta+y+baja");
        Assert.DoesNotContain(listado!.Items, c => c.Id == creado.Id);
    }

    [Fact]
    public async Task UnVendedorNoPuedeCrearClientes()
    {
        var (idTenant, idCondicionFiscalCf, idListaPrecioGeneral, _, _) =
            await AprovisionarTenantAsync(nameof(UnVendedorNoPuedeCrearClientes));
        var mailVendedor = await SembrarVendedorAsync(idTenant, nameof(UnVendedorNoPuedeCrearClientes));
        using var vendedor = await ClienteLogueadoAsync(mailVendedor, PasswordVendedor);

        var alta = new AltaCliente(
            "Intento de vendedor", null, null, null, null, idCondicionFiscalCf, null, null, null, null, null, null,
            idListaPrecioGeneral);

        var respuesta = await vendedor.PostAsJsonAsync("/api/clientes", alta);

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    /// <summary>ADR-8: mismo 404 para "no existe" y "es de otro tenant".</summary>
    [Fact]
    public async Task UnClienteDeOtroTenantDevuelve404()
    {
        var (_, idCondicionFiscalCfA, idListaPrecioGeneralA, mailAdminA, passwordAdminA) =
            await AprovisionarTenantAsync(nameof(UnClienteDeOtroTenantDevuelve404) + "-A");
        var (_, _, _, mailAdminB, passwordAdminB) =
            await AprovisionarTenantAsync(nameof(UnClienteDeOtroTenantDevuelve404) + "-B");

        using var adminA = await ClienteLogueadoAsync(mailAdminA, passwordAdminA);
        var alta = new AltaCliente(
            "De tenant A", null, null, null, null, idCondicionFiscalCfA, null, null, null, null, null, null,
            idListaPrecioGeneralA);
        var respuestaAlta = await adminA.PostAsJsonAsync("/api/clientes", alta);
        var clienteDeA = await respuestaAlta.Content.ReadFromJsonAsync<ClienteListado>();

        using var adminB = await ClienteLogueadoAsync(mailAdminB, passwordAdminB);
        var respuesta = await adminB.GetAsync($"/api/clientes/{clienteDeA!.Id}");

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    /// <summary>Judgment-day ronda 1 (item 3): ADR-8 aplica igual a los caminos de
    /// escritura — mismo 404 uniforme, no solo en el GET de arriba.</summary>
    [Fact]
    public async Task UnPutSobreUnClienteDeOtroTenantDevuelve404()
    {
        var (_, idCondicionFiscalCfA, idListaPrecioGeneralA, mailAdminA, passwordAdminA) =
            await AprovisionarTenantAsync(nameof(UnPutSobreUnClienteDeOtroTenantDevuelve404) + "-A");
        var (_, idCondicionFiscalCfB, idListaPrecioGeneralB, mailAdminB, passwordAdminB) =
            await AprovisionarTenantAsync(nameof(UnPutSobreUnClienteDeOtroTenantDevuelve404) + "-B");

        using var adminA = await ClienteLogueadoAsync(mailAdminA, passwordAdminA);
        var alta = new AltaCliente(
            "De tenant A", null, null, null, null, idCondicionFiscalCfA, null, null, null, null, null, null,
            idListaPrecioGeneralA);
        var respuestaAlta = await adminA.PostAsJsonAsync("/api/clientes", alta);
        var clienteDeA = await respuestaAlta.Content.ReadFromJsonAsync<ClienteListado>();

        using var adminB = await ClienteLogueadoAsync(mailAdminB, passwordAdminB);
        var edicion = new EdicionCliente(
            "Intento de edición", null, null, null, null, idCondicionFiscalCfB, null, null, null, null, null, null,
            idListaPrecioGeneralB, 0, false, null, true);
        var respuesta = await adminB.PutAsJsonAsync($"/api/clientes/{clienteDeA!.Id}", edicion);

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    /// <summary>Judgment-day ronda 1 (item 3): mismo criterio que el PUT de arriba, para el
    /// DELETE.</summary>
    [Fact]
    public async Task UnDeleteSobreUnClienteDeOtroTenantDevuelve404()
    {
        var (_, idCondicionFiscalCfA, idListaPrecioGeneralA, mailAdminA, passwordAdminA) =
            await AprovisionarTenantAsync(nameof(UnDeleteSobreUnClienteDeOtroTenantDevuelve404) + "-A");
        var (_, _, _, mailAdminB, passwordAdminB) =
            await AprovisionarTenantAsync(nameof(UnDeleteSobreUnClienteDeOtroTenantDevuelve404) + "-B");

        using var adminA = await ClienteLogueadoAsync(mailAdminA, passwordAdminA);
        var alta = new AltaCliente(
            "De tenant A", null, null, null, null, idCondicionFiscalCfA, null, null, null, null, null, null,
            idListaPrecioGeneralA);
        var respuestaAlta = await adminA.PostAsJsonAsync("/api/clientes", alta);
        var clienteDeA = await respuestaAlta.Content.ReadFromJsonAsync<ClienteListado>();

        using var adminB = await ClienteLogueadoAsync(mailAdminB, passwordAdminB);
        var respuesta = await adminB.DeleteAsync($"/api/clientes/{clienteDeA!.Id}");

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    /// <summary>Judgment-day ronda 1 (item 5): cobertura faltante de <c>GET /api/listas-precio</c>
    /// (design decision 1, spec: listas_precio ABM Is Out of Scope This Stage) — un admin solo
    /// ve las listas de SU tenant, nunca las de otro (aislamiento cross-tenant, ADR-6/ADR-17).</summary>
    [Fact]
    public async Task UnAdminSoloVeLasListasDePrecioDeSuPropioTenant()
    {
        var (_, _, idListaPrecioGeneralA, mailAdminA, passwordAdminA) =
            await AprovisionarTenantAsync(nameof(UnAdminSoloVeLasListasDePrecioDeSuPropioTenant) + "-A");
        await AprovisionarTenantAsync(nameof(UnAdminSoloVeLasListasDePrecioDeSuPropioTenant) + "-B");

        using var adminA = await ClienteLogueadoAsync(mailAdminA, passwordAdminA);
        var respuesta = await adminA.GetAsync("/api/listas-precio");

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var listas = await respuesta.Content.ReadFromJsonAsync<List<ListaPrecioAsignable>>();

        Assert.NotNull(listas);
        Assert.Single(listas!);
        Assert.Equal(idListaPrecioGeneralA, listas![0].Id);
    }

    [Fact]
    public async Task UnVendedorNoPuedeListarListasDePrecio()
    {
        var (idTenant, _, _, _, _) =
            await AprovisionarTenantAsync(nameof(UnVendedorNoPuedeListarListasDePrecio));
        var mailVendedor = await SembrarVendedorAsync(idTenant, nameof(UnVendedorNoPuedeListarListasDePrecio));
        using var vendedor = await ClienteLogueadoAsync(mailVendedor, PasswordVendedor);

        var respuesta = await vendedor.GetAsync("/api/listas-precio");

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    /// <summary>Judgment-day ronda 1 (item 4): <see cref="Ways.Domain.Clientes.ReglaDeClientes.ValidarNoConsumidorFinal"/>
    /// de punta a punta, a través del pipeline HTTP real — no solo a nivel de
    /// <c>ServicioDeClientesTests</c> (InMemory).</summary>
    [Fact]
    public async Task UnPutSobreElConsumidorFinalDevuelve409()
    {
        var (idTenant, idCondicionFiscalCf, idListaPrecioGeneral, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(UnPutSobreElConsumidorFinalDevuelve409));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var consumidorFinal = await db.Clientes.SingleAsync(
            c => c.IdTenant == idTenant && c.Numero == ReglaDeClientes.NumeroConsumidorFinal);

        var edicion = new EdicionCliente(
            "Intento de edición del CF", null, null, null, null, idCondicionFiscalCf, null, null, null, null, null,
            null, idListaPrecioGeneral, 0, false, null, true);

        var respuesta = await admin.PutAsJsonAsync($"/api/clientes/{consumidorFinal.Id}", edicion);

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("consumidor_final_protegido", problema.GetProperty("codigo").GetString());
    }

    /// <summary>Mismo criterio que el PUT de arriba, para el DELETE.</summary>
    [Fact]
    public async Task UnDeleteSobreElConsumidorFinalDevuelve409()
    {
        var (idTenant, _, _, mailAdmin, passwordAdmin) =
            await AprovisionarTenantAsync(nameof(UnDeleteSobreElConsumidorFinalDevuelve409));
        using var admin = await ClienteLogueadoAsync(mailAdmin, passwordAdmin);

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var consumidorFinal = await db.Clientes.SingleAsync(
            c => c.IdTenant == idTenant && c.Numero == ReglaDeClientes.NumeroConsumidorFinal);

        var respuesta = await admin.DeleteAsync($"/api/clientes/{consumidorFinal.Id}");

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("consumidor_final_protegido", problema.GetProperty("codigo").GetString());
    }
}
