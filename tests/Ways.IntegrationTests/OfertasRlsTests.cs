using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Ofertas;
using Ways.Domain.Organizacion;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-4-ofertas, Slice 1 (task 1.8, spec: ofertas / Tenant Isolation for ofertas and
/// ofertas_listas, ambos escenarios): mismo patrón que <c>ArticulosYPreciosRlsTests</c> — SQL
/// crudo, independiente de EF, 0 filas para SELECT/UPDATE cross-tenant, 42501 para el INSERT
/// que viola <c>WITH CHECK</c>, más un proof a nivel EF (LINQ) de que el filtro de tenant
/// también bloquea la lectura por el ORM.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class OfertasRlsTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    public static TheoryData<string, string> TablasDeTenant => new()
    {
        { "ofertas", "id_oferta" },
        { "ofertas_listas", "id_oferta" }
    };

    private async Task<(int IdTenantA, int IdFila, int IdTenantB, int IdOferta)> SembrarFilaDeTenantAAsync(
        string tabla, string nombre)
    {
        using var _ = fixture.CreateClient(); // arranca el host (siembra alicuotas_iva)

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var tenantA = new Tenant { Nombre = $"{nombre}-A", Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
        var tenantB = new Tenant { Nombre = $"{nombre}-B", Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
        db.Tenants.AddRange(tenantA, tenantB);
        await db.SaveChangesAsync();

        var area = new Area { IdTenant = tenantA.Id, Nombre = nombre, Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();

        var articulo = new Articulo
        {
            IdTenant = tenantA.Id,
            CodigoInterno = $"{nombre}-cod",
            Nombre = nombre,
            IdArea = area.Id,
            IdAlicuotaIva = idAlicuotaIva,
            UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        var oferta = new Oferta
        {
            IdTenant = tenantA.Id,
            Nombre = nombre,
            IdArticulo = articulo.Id,
            Porcentaje = 10m,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Ofertas.Add(oferta);
        await db.SaveChangesAsync();

        int idFila;
        switch (tabla)
        {
            case "ofertas":
                idFila = oferta.Id;
                break;

            case "ofertas_listas":
                var lista = new ListaPrecio
                {
                    IdTenant = tenantA.Id, Nombre = nombre, EsDefault = false, Modo = ModoLista.Fija,
                    CreatedAt = ahora, UpdatedAt = ahora
                };
                db.ListasPrecio.Add(lista);
                await db.SaveChangesAsync();

                db.OfertasListas.Add(new OfertaLista
                {
                    IdOferta = oferta.Id, IdListaPrecio = lista.Id, IdTenant = tenantA.Id
                });
                await db.SaveChangesAsync();
                idFila = oferta.Id;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(tabla), tabla, "Tabla no cubierta por este helper.");
        }

        return (tenantA.Id, idFila, tenantB.Id, oferta.Id);
    }

    [Theory]
    [MemberData(nameof(TablasDeTenant))]
    public async Task UnaSesionDeOtroTenantNoVeLaFilaPorSelect(string tabla, string columnaId)
    {
        var (idTenantA, idFila, idTenantB, _) = await SembrarFilaDeTenantAAsync(tabla, nameof(UnaSesionDeOtroTenantNoVeLaFilaPorSelect) + tabla);

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", idTenantB);

        await using var comando = cruda.CreateCommand();
        comando.CommandText = $"SELECT count(*) FROM {tabla} WHERE {columnaId} = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = idFila });

        var total = (long)(await comando.ExecuteScalarAsync())!;

        Assert.Equal(0, total);
        Assert.NotEqual(idTenantA, idTenantB);
    }

    [Theory]
    [MemberData(nameof(TablasDeTenant))]
    public async Task UnaSesionDeOtroTenantNoPuedeActualizarLaFila(string tabla, string columnaId)
    {
        var (_, idFila, idTenantB, _) = await SembrarFilaDeTenantAAsync(tabla, nameof(UnaSesionDeOtroTenantNoPuedeActualizarLaFila) + tabla);

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", idTenantB);

        await using var comando = cruda.CreateCommand();
        // ofertas_listas no tiene updated_at (junction PK-only, sin auditoría): el UPDATE toca
        // id_lista_precio en su lugar -- da lo mismo qué columna, lo que se prueba es que
        // USING oculta la fila antes de que el UPDATE la alcance.
        comando.CommandText = tabla == "ofertas_listas"
            ? $"UPDATE {tabla} SET id_lista_precio = id_lista_precio WHERE {columnaId} = $1"
            : $"UPDATE {tabla} SET updated_at = now() WHERE {columnaId} = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = idFila });

        var filas = await comando.ExecuteNonQueryAsync();
        Assert.Equal(0, filas);
    }

    [Theory]
    [MemberData(nameof(TablasDeTenant))]
    public async Task UnInsertConIdTenantAjenoSeRechaza(string tabla, string columnaId)
    {
        _ = columnaId;
        var (idTenantA, _, idTenantB, idOfertaDeA) = await SembrarFilaDeTenantAAsync(
            tabla, nameof(UnInsertConIdTenantAjenoSeRechaza) + tabla);

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", idTenantA);

        await using var comando = cruda.CreateCommand();
        comando.CommandText = tabla switch
        {
            "ofertas" =>
                "INSERT INTO ofertas (id_tenant, nombre, id_articulo, porcentaje, created_at, updated_at) " +
                "VALUES ($1, 'intruso', 999999, 10, now(), now())",
            "ofertas_listas" =>
                "INSERT INTO ofertas_listas (id_oferta, id_lista_precio, id_tenant) VALUES ($2, 999999, $1)",
            _ => throw new ArgumentOutOfRangeException(nameof(tabla), tabla, "Tabla no cubierta por este helper.")
        };

        comando.Parameters.Add(new NpgsqlParameter { Value = idTenantB });
        if (tabla is "ofertas_listas")
        {
            comando.Parameters.Add(new NpgsqlParameter { Value = idOfertaDeA });
        }

        // 42501 = insufficient_privilege (violación de WITH CHECK) -- se dispara antes de que
        // cualquier FK compuesta llegue a evaluarse, sin importar si el resto de columnas
        // referencia algo inválido (999999 nunca existe).
        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("42501", excepcion.SqlState);
    }

    /// <summary>Proof a nivel EF (LINQ) de que el filtro de tenant también bloquea a las
    /// entidades que sí pasan por el ORM — <see cref="OfertaLista"/> queda cubierta acá
    /// también aunque use el filtro manual (no hereda <c>EntidadTenant</c>, ver el comentario
    /// de <c>WaysDbContext.AplicarFiltroDeTenantEnOfertaLista</c>).</summary>
    [Fact]
    public async Task ElFiltroDeEfNuncaDevuelveFilasDeOtroTenantParaLasEntidadesNuevas()
    {
        var (_, idOfertaDeA, idTenantB1, _) = await SembrarFilaDeTenantAAsync(
            "ofertas", nameof(ElFiltroDeEfNuncaDevuelveFilasDeOtroTenantParaLasEntidadesNuevas) + "-ofertas");
        var (_, idOfertaDeOfertaListaDeA, idTenantB2, _) = await SembrarFilaDeTenantAAsync(
            "ofertas_listas", nameof(ElFiltroDeEfNuncaDevuelveFilasDeOtroTenantParaLasEntidadesNuevas) + "-ofertaslistas");

        await using var sesionB1 = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenantB1));
        var ofertasVisibles = await sesionB1.Ofertas.Where(o => o.Id == idOfertaDeA).ToListAsync();
        Assert.Empty(ofertasVisibles);

        await using var sesionB2 = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenantB2));
        var ofertasListasVisibles = await sesionB2.OfertasListas
            .Where(ol => ol.IdOferta == idOfertaDeOfertaListaDeA).ToListAsync();
        Assert.Empty(ofertasListasVisibles);
    }
}
