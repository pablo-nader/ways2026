using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Articulos;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// Stage-3-articulos-y-precios, Slice 1 (task 1.11, spec: Tenant Isolation for Articulos And
/// articulos_empresas / codigos_barra / precios): mismo patrón que
/// <c>ClientesYProveedoresRlsTests</c>/<c>CatalogosDeTenantRlsTests</c> — SQL crudo,
/// independiente de EF, 0 filas para SELECT/UPDATE cross-tenant, 42501 para el INSERT que
/// viola <c>WITH CHECK</c>, más un proof a nivel EF (LINQ) de que el filtro de tenant también
/// bloquea la lectura por el ORM.
///
/// <c>numeraciones_articulos</c> queda afuera de la tabla parametrizada (su PK ES
/// <c>id_tenant</c>, no tiene una columna de id propia como el resto) — tiene su propio test,
/// mirror de <c>ClientesYProveedoresRlsTests.NumeracionesClientesEsInvisibleParaOtroTenant</c>.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ArticulosYPreciosRlsTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    public static TheoryData<string, string> TablasDeTenant => new()
    {
        { "articulos", "id_articulo" },
        { "articulos_empresas", "id_articulo" },
        { "codigos_barra", "id_codigo_barra" },
        { "precios", "id_precio" }
    };

    private async Task<(int IdTenantA, int IdFila, int IdTenantB, int IdArticulo)> SembrarFilaDeTenantAAsync(
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

        int idFila;
        switch (tabla)
        {
            case "articulos":
                idFila = articulo.Id;
                break;

            case "articulos_empresas":
                var empresa = new Empresa { IdTenant = tenantA.Id, RazonSocial = nombre, CreatedAt = ahora, UpdatedAt = ahora };
                db.Empresas.Add(empresa);
                await db.SaveChangesAsync();

                db.ArticulosEmpresas.Add(new ArticuloEmpresa
                {
                    IdArticulo = articulo.Id, IdEmpresa = empresa.Id, IdTenant = tenantA.Id
                });
                await db.SaveChangesAsync();
                idFila = articulo.Id;
                break;

            case "codigos_barra":
                var codigoBarra = new CodigoBarra
                {
                    IdTenant = tenantA.Id, IdArticulo = articulo.Id, Codigo = $"{nombre}-barra",
                    CreatedAt = ahora, UpdatedAt = ahora
                };
                db.CodigosBarra.Add(codigoBarra);
                await db.SaveChangesAsync();
                idFila = codigoBarra.Id;
                break;

            case "precios":
                var lista = new ListaPrecio
                {
                    IdTenant = tenantA.Id, Nombre = nombre, EsDefault = false, Modo = ModoLista.Fija,
                    CreatedAt = ahora, UpdatedAt = ahora
                };
                db.ListasPrecio.Add(lista);
                await db.SaveChangesAsync();

                var precio = new Precio
                {
                    IdTenant = tenantA.Id, IdArticulo = articulo.Id, IdListaPrecio = lista.Id,
                    Monto = 100m, VigenteDesde = ahora, CreatedAt = ahora, UpdatedAt = ahora
                };
                db.Precios.Add(precio);
                await db.SaveChangesAsync();
                idFila = precio.Id;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(tabla), tabla, "Tabla no cubierta por este helper.");
        }

        return (tenantA.Id, idFila, tenantB.Id, articulo.Id);
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
        // articulos_empresas no tiene updated_at (junction PK-only, sin auditoría, task 1.4):
        // el UPDATE toca id_empresa en su lugar -- da lo mismo qué columna, lo que se prueba
        // es que USING oculta la fila antes de que el UPDATE la alcance.
        comando.CommandText = tabla == "articulos_empresas"
            ? $"UPDATE {tabla} SET id_empresa = id_empresa WHERE {columnaId} = $1"
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
        var (idTenantA, _, idTenantB, idArticuloDeA) = await SembrarFilaDeTenantAAsync(
            tabla, nameof(UnInsertConIdTenantAjenoSeRechaza) + tabla);

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", idTenantA);

        await using var comando = cruda.CreateCommand();
        comando.CommandText = tabla switch
        {
            "articulos" =>
                "INSERT INTO articulos " +
                "(id_tenant, codigo_interno, nombre, id_area, id_alicuota_iva, unidad_venta, es_producto, created_at, updated_at) " +
                "VALUES ($1, 'intruso', 'intruso', 999999, 999999, 'unidad', true, now(), now())",
            "articulos_empresas" =>
                "INSERT INTO articulos_empresas (id_articulo, id_empresa, id_tenant) VALUES ($2, 999999, $1)",
            "codigos_barra" =>
                "INSERT INTO codigos_barra (id_tenant, id_articulo, codigo, created_at, updated_at) " +
                "VALUES ($1, $2, 'intruso-barra', now(), now())",
            "precios" =>
                "INSERT INTO precios (id_tenant, id_articulo, id_lista_precio, precio, vigente_desde, created_at, updated_at) " +
                "VALUES ($1, $2, 999999, 100, now(), now(), now())",
            _ => throw new ArgumentOutOfRangeException(nameof(tabla), tabla, "Tabla no cubierta por este helper.")
        };

        comando.Parameters.Add(new NpgsqlParameter { Value = idTenantB });
        if (tabla is "articulos_empresas" or "codigos_barra" or "precios")
        {
            comando.Parameters.Add(new NpgsqlParameter { Value = idArticuloDeA });
        }

        // 42501 = insufficient_privilege (violación de WITH CHECK) -- se dispara antes de que
        // cualquier FK compuesta llegue a evaluarse, sin importar si el resto de columnas
        // referencia algo inválido (999999 nunca existe).
        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("42501", excepcion.SqlState);
    }

    [Fact]
    public async Task NumeracionesArticulosEsInvisibleParaOtroTenant()
    {
        using var _ = fixture.CreateClient();

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var tenantA = new Tenant { Nombre = nameof(NumeracionesArticulosEsInvisibleParaOtroTenant) + "-A", Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
        var tenantB = new Tenant { Nombre = nameof(NumeracionesArticulosEsInvisibleParaOtroTenant) + "-B", Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
        db.Tenants.AddRange(tenantA, tenantB);
        await db.SaveChangesAsync();

        // AsignadorDeCodigoInternoArticulo.AsegurarContadorAsync (SQL crudo), no
        // db.NumeracionesArticulos.Add: WaysDbContext.RechazarEscriturasDeNumeracionArticulo
        // rechaza cualquier Added/Modified que llegue por el ChangeTracker.
        await AsignadorDeCodigoInternoArticulo.AsegurarContadorAsync(db, tenantA.Id);

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", tenantB.Id);

        await using var comando = cruda.CreateCommand();
        comando.CommandText = "SELECT count(*) FROM numeraciones_articulos WHERE id_tenant = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = tenantA.Id });

        var total = (long)(await comando.ExecuteScalarAsync())!;

        Assert.Equal(0, total);
    }

    [Fact]
    public async Task UnInsertEnNumeracionesArticulosConIdTenantAjenoSeRechaza()
    {
        using var _ = fixture.CreateClient();

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var tenantA = new Tenant { Nombre = nameof(UnInsertEnNumeracionesArticulosConIdTenantAjenoSeRechaza) + "-A", Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
        var tenantB = new Tenant { Nombre = nameof(UnInsertEnNumeracionesArticulosConIdTenantAjenoSeRechaza) + "-B", Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
        db.Tenants.AddRange(tenantA, tenantB);
        await db.SaveChangesAsync();

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", tenantA.Id);

        await using var comando = cruda.CreateCommand();
        comando.CommandText = "INSERT INTO numeraciones_articulos (id_tenant, proximo_numero) VALUES ($1, 1)";
        comando.Parameters.Add(new NpgsqlParameter { Value = tenantB.Id });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("42501", excepcion.SqlState);
    }

    /// <summary>Proof a nivel EF (LINQ) de que el filtro de tenant también bloquea a las
    /// entidades que sí pasan por el ORM — <see cref="ArticuloEmpresa"/> queda cubierta acá
    /// también aunque use el filtro manual (no hereda <c>EntidadTenant</c>, ver el comentario
    /// de <c>WaysDbContext.AplicarFiltroDeTenantEnArticuloEmpresa</c>), <see cref="NumeracionArticulo"/>
    /// queda afuera (design decision 6: solo se escribe/lee con SQL crudo).</summary>
    [Fact]
    public async Task ElFiltroDeEfNuncaDevuelveFilasDeOtroTenantParaLasEntidadesNuevas()
    {
        var (_, idArticuloDeA, idTenantB1, _) = await SembrarFilaDeTenantAAsync(
            "articulos", nameof(ElFiltroDeEfNuncaDevuelveFilasDeOtroTenantParaLasEntidadesNuevas) + "-articulos");
        var (_, idCodigoBarraDeA, idTenantB2, _) = await SembrarFilaDeTenantAAsync(
            "codigos_barra", nameof(ElFiltroDeEfNuncaDevuelveFilasDeOtroTenantParaLasEntidadesNuevas) + "-codigos");
        var (_, idPrecioDeA, idTenantB3, _) = await SembrarFilaDeTenantAAsync(
            "precios", nameof(ElFiltroDeEfNuncaDevuelveFilasDeOtroTenantParaLasEntidadesNuevas) + "-precios");
        var (_, idArticuloDeArtEmpresa, idTenantB4, idArticuloBase) = await SembrarFilaDeTenantAAsync(
            "articulos_empresas", nameof(ElFiltroDeEfNuncaDevuelveFilasDeOtroTenantParaLasEntidadesNuevas) + "-artemp");

        await using var sesionB1 = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenantB1));
        var articulosVisibles = await sesionB1.Articulos.Where(a => a.Id == idArticuloDeA).ToListAsync();
        Assert.Empty(articulosVisibles);

        await using var sesionB2 = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenantB2));
        var codigosVisibles = await sesionB2.CodigosBarra.Where(c => c.Id == idCodigoBarraDeA).ToListAsync();
        Assert.Empty(codigosVisibles);

        await using var sesionB3 = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenantB3));
        var preciosVisibles = await sesionB3.Precios.Where(p => p.Id == idPrecioDeA).ToListAsync();
        Assert.Empty(preciosVisibles);

        await using var sesionB4 = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenantB4));
        var artEmpresasVisibles = await sesionB4.ArticulosEmpresas
            .Where(ae => ae.IdArticulo == idArticuloDeArtEmpresa).ToListAsync();
        Assert.Empty(artEmpresasVisibles);
        _ = idArticuloBase;
    }
}
