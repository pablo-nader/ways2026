using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Clientes;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// Stage-2-clientes-proveedores, Slice 1 (task 1.15, spec: Tenant Provisioning With Template
/// Seed / Backfill for Pre-Existing Tenants / listas-precio-minimal One Default List): el
/// Consumidor Final + la lista General nacen con el tenant (provisioning) o se completan para
/// un tenant preexistente (backfill), y el backfill es idempotente.
///
/// GATED — doble motivo: (1) la migración <c>ClientesYProveedoresEtapa2</c> está bloqueada por
/// el DB CHANGE GATE (task 1.7); (2) el cableado de <c>ServicioDeAprovisionamiento</c> (task
/// 1.10) e <c>InicializadorDeBaseDeDatos.BackfillDeClientesYListasPrecioAsync</c> (task 1.11)
/// queda comentado hasta ese mismo lote — cablearlos ahora rompe el arranque del host en TODAS
/// las pruebas de integración (las tablas todavía no existen). <c>Skip</c> se saca junto con
/// la migración y el cableado, en el mismo lote.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ClientesProvisioningYBackfillTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    private const string RazonDeGate =
        "Gated: clientes/listas_precio no existen (DB CHANGE GATE, task 1.7) y el cableado de " +
        "ServicioDeAprovisionamiento/InicializadorDeBaseDeDatos queda comentado hasta ese lote (tasks 1.10/1.11).";

    private async Task<HttpClient> ClienteComoRootAsync()
    {
        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    [Fact(Skip = RazonDeGate)]
    public async Task ProvisionarUnTenantCreaElConsumidorFinalYLaListaGeneral()
    {
        using var cliente = await ClienteComoRootAsync();

        var solicitud = new SolicitudDeAprovisionamiento(
            NombreTenant: nameof(ProvisionarUnTenantCreaElConsumidorFinalYLaListaGeneral),
            RazonSocialEmpresa: "Empresa de prueba",
            NombrePuntoVenta: "Local 1",
            MailAdmin: $"{nameof(ProvisionarUnTenantCreaElConsumidorFinalYLaListaGeneral)}@ways.test");

        var respuesta = await cliente.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);

        var resultado = await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>();
        Assert.NotNull(resultado);

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var listaGeneral = await db.ListasPrecio.SingleAsync(l => l.IdTenant == resultado!.IdTenant && l.EsDefault);
        Assert.Equal("General", listaGeneral.Nombre);

        var consumidorFinal = await db.Clientes.SingleAsync(
            c => c.IdTenant == resultado!.IdTenant && c.Numero == ReglaDeClientes.NumeroConsumidorFinal);
        Assert.Equal("Consumidor Final", consumidorFinal.Nombre);
        Assert.Equal(listaGeneral.Id, consumidorFinal.IdListaPrecio);
    }

    [Fact(Skip = RazonDeGate)]
    public async Task UnTenantPreexistenteSinClientesNiListasGanaAmbosPorBackfillYElBackfillEsIdempotente()
    {
        // Sembrado ANTES del primer CreateClient() de este fixture (mismo trámite que
        // InicializadorDeBaseDeDatosTests): así el tenant existe sin clientes/listas_precio
        // cuando InicializadorDeBaseDeDatos.EjecutarAsync corre por primera vez en esta
        // prueba, forzando el camino real de backfill en vez de provisioning.
        int idTenant;
        await using (var siembra = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            var ahora = DateTimeOffset.UtcNow;
            var tenant = new Tenant
            {
                Nombre = nameof(UnTenantPreexistenteSinClientesNiListasGanaAmbosPorBackfillYElBackfillEsIdempotente),
                Estado = EstadoTenant.Activo,
                CreatedAt = ahora,
                UpdatedAt = ahora
            };
            siembra.Tenants.Add(tenant);
            await siembra.SaveChangesAsync();
            idTenant = tenant.Id;
        }

        // Arranca el host: dispara InicializadorDeBaseDeDatos.EjecutarAsync, que corre el
        // backfill para el tenant recién sembrado.
        using var _ = fixture.CreateClient();

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var listaGeneral = await db.ListasPrecio.SingleAsync(l => l.IdTenant == idTenant && l.EsDefault);
        var consumidorFinal = await db.Clientes.SingleAsync(
            c => c.IdTenant == idTenant && c.Numero == ReglaDeClientes.NumeroConsumidorFinal);
        Assert.Equal(listaGeneral.Id, consumidorFinal.IdListaPrecio);

        // Un segundo arranque (nuevo cliente HTTP, mismo fixture/contenedor) no duplica nada:
        // BackfillDeClientesYListasPrecioAsync se salta el tenant que ya tiene sus dos filas.
        using var _segundoArranque = fixture.CreateClient();

        var cantidadClientes = await db.Clientes.CountAsync(c => c.IdTenant == idTenant);
        var cantidadListas = await db.ListasPrecio.CountAsync(l => l.IdTenant == idTenant);
        Assert.Equal(1, cantidadClientes);
        Assert.Equal(1, cantidadListas);
    }
}
