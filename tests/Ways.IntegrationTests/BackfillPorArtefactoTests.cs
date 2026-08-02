using Microsoft.EntityFrameworkCore;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Organizacion;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// Judgment-day ronda 1 (item CRITICAL confirmado): antes del fix, <c>InicializadorDeBaseDeDatos.
/// BackfillDeClientesYListasPrecioAsync</c> calculaba "cubierto" como la UNIÓN de
/// tenants-con-CF y tenants-con-lista-default, así que un tenant con solo UNA de las dos filas
/// quedaba "cubierto" y nunca ganaba la mitad faltante. Esta prueba sembra, ANTES del primer
/// <c>CreateClient()</c> de un fixture propio, un tenant con solo la lista (sin CF) y otro con
/// solo el CF (sin lista default) — el fix tiene que completar exactamente la mitad que falta
/// en cada uno, sin duplicar la mitad que ya tenían.
///
/// Clase propia con su propio <see cref="WaysApiFixture"/> (a diferencia de agregar el método a
/// <c>ClientesProvisioningYBackfillTests</c>): el truco de "sembrar antes del primer
/// CreateClient()" solo es válido para el primer método que arranca el host de ESE fixture —
/// una clase dedicada, con un solo <c>[Fact]</c>, elimina cualquier dependencia del orden de
/// ejecución entre métodos de prueba.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class BackfillPorArtefactoTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    [Fact]
    public async Task UnTenantConSoloListaYOtroConSoloClienteCfGananLaMitadFaltantePorBackfill()
    {
        int idTenantSoloLista;
        int idTenantSoloCf;
        int idListaNoDefaultDelTenantSoloCf;

        // Sembrado ANTES del primer CreateClient() de este fixture (mismo trámite que
        // ClientesProvisioningYBackfillTests): así los dos tenants existen, con su cobertura
        // parcial, cuando InicializadorDeBaseDeDatos.EjecutarAsync corre por primera vez.
        await using (var siembra = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            var ahora = DateTimeOffset.UtcNow;

            var tenantSoloLista = new Tenant
            {
                Nombre = nameof(UnTenantConSoloListaYOtroConSoloClienteCfGananLaMitadFaltantePorBackfill) + "-SoloLista",
                Estado = EstadoTenant.Activo,
                CreatedAt = ahora,
                UpdatedAt = ahora
            };
            var tenantSoloCf = new Tenant
            {
                Nombre = nameof(UnTenantConSoloListaYOtroConSoloClienteCfGananLaMitadFaltantePorBackfill) + "-SoloCf",
                Estado = EstadoTenant.Activo,
                CreatedAt = ahora,
                UpdatedAt = ahora
            };
            siembra.Tenants.AddRange(tenantSoloLista, tenantSoloCf);
            await siembra.SaveChangesAsync();

            // Tenant con solo la lista General (sin CF): el backfill le tiene que agregar el
            // cliente Consumidor Final, reusando esta lista existente.
            var listaDefaultDelTenantSoloLista = new ListaPrecio
            {
                IdTenant = tenantSoloLista.Id,
                Nombre = "General",
                EsDefault = true,
                Modo = ModoLista.Fija,
                CreatedAt = ahora,
                UpdatedAt = ahora
            };
            siembra.ListasPrecio.Add(listaDefaultDelTenantSoloLista);

            // Tenant con solo el cliente CF (sin lista default): el CF preexistente necesita
            // una lista_precio para su FK NOT NULL — una que a propósito NO sea la default,
            // para no tapar por accidente la falta de la lista General.
            var listaNoDefaultDelTenantSoloCf = new ListaPrecio
            {
                IdTenant = tenantSoloCf.Id,
                Nombre = "No Default",
                EsDefault = false,
                Modo = ModoLista.Fija,
                CreatedAt = ahora,
                UpdatedAt = ahora
            };
            siembra.ListasPrecio.Add(listaNoDefaultDelTenantSoloCf);

            // Código "CF" a propósito, no uno inventado: SembrarCatalogosFiscalesAsync
            // decide si siembra las 5 condiciones fiscales base mirando si la tabla YA tiene
            // alguna fila (AnyAsync sobre toda la tabla, no los códigos puntuales) — un código
            // distinto la dejaría con una fila, saltearía la siembra de las 5 base, y el
            // propio backfill fallaría más abajo buscando "CF" (mismo hallazgo incidental que
            // documenta el batch 2 de apply-progress.md para CatalogosGlobalesRlsTests).
            var condicionFiscalCf = new CondicionFiscal
            {
                Codigo = PlantillaDeAprovisionamiento.V1.ClienteConsumidorFinal.CodigoCondicionFiscal,
                Nombre = "Consumidor Final",
                CreatedAt = ahora,
                UpdatedAt = ahora
            };
            siembra.CondicionesFiscales.Add(condicionFiscalCf);
            await siembra.SaveChangesAsync();

            siembra.Clientes.Add(new Cliente
            {
                IdTenant = tenantSoloCf.Id,
                Numero = ReglaDeClientes.NumeroConsumidorFinal,
                Nombre = "Consumidor Final preexistente",
                IdCondicionFiscal = condicionFiscalCf.Id,
                IdListaPrecio = listaNoDefaultDelTenantSoloCf.Id,
                CreatedAt = ahora,
                UpdatedAt = ahora
            });
            await siembra.SaveChangesAsync();

            idTenantSoloLista = tenantSoloLista.Id;
            idTenantSoloCf = tenantSoloCf.Id;
            idListaNoDefaultDelTenantSoloCf = listaNoDefaultDelTenantSoloCf.Id;
        }

        // Arranca el host: dispara el backfill para los dos tenants, cada uno completando
        // solo la mitad que le falta.
        using var _ = fixture.CreateClient();

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        // Tenant con solo lista: gana el Consumidor Final, sin duplicar la lista existente.
        var clientesDelTenantSoloLista = await db.Clientes
            .Where(c => c.IdTenant == idTenantSoloLista)
            .ToListAsync();
        var listasDelTenantSoloLista = await db.ListasPrecio
            .Where(l => l.IdTenant == idTenantSoloLista)
            .ToListAsync();

        var consumidorFinalNuevo = Assert.Single(clientesDelTenantSoloLista);
        Assert.Equal(ReglaDeClientes.NumeroConsumidorFinal, consumidorFinalNuevo.Numero);
        var listaExistente = Assert.Single(listasDelTenantSoloLista);
        Assert.Equal(listaExistente.Id, consumidorFinalNuevo.IdListaPrecio);

        // Tenant con solo CF: gana la lista General default, sin duplicar el CF existente.
        var clientesDelTenantSoloCf = await db.Clientes
            .Where(c => c.IdTenant == idTenantSoloCf)
            .ToListAsync();
        var listasDelTenantSoloCf = await db.ListasPrecio
            .Where(l => l.IdTenant == idTenantSoloCf)
            .ToListAsync();

        Assert.Single(clientesDelTenantSoloCf);
        Assert.Equal(2, listasDelTenantSoloCf.Count);
        var listaDefaultNueva = Assert.Single(listasDelTenantSoloCf, l => l.EsDefault);
        Assert.Equal("General", listaDefaultNueva.Nombre);
        Assert.Contains(listasDelTenantSoloCf, l => l.Id == idListaNoDefaultDelTenantSoloCf && !l.EsDefault);
    }
}
