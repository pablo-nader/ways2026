using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Organizacion;
using Ways.Domain.Plantel;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// Aislamiento por tenant (RLS sobre <c>personal</c>) y respaldo de base de datos de las reglas
/// del catálogo, con SQL crudo sobre la conexión <c>ways_app</c> (NOBYPASSRLS) para no depender
/// del ORM ni del servicio.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class PersonalRlsTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private sealed record Siembra(
        int IdTenantA, int IdTenantB, int IdPersonalDeA, int IdClienteDeA, int IdClienteLibreDeA, int IdUsuarioDeA);

    private async Task<Siembra> SembrarAsync(string nombre)
    {
        using var _ = fixture.CreateClient();

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var tenantA = new Tenant { Nombre = $"{nombre}-A", Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
        var tenantB = new Tenant { Nombre = $"{nombre}-B", Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
        db.Tenants.AddRange(tenantA, tenantB);
        await db.SaveChangesAsync();

        var condicionFiscal = new CondicionFiscal { Codigo = $"{nombre}-CF", Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.CondicionesFiscales.Add(condicionFiscal);
        var lista = new ListaPrecio
        {
            IdTenant = tenantA.Id, Nombre = nombre, EsDefault = true, Modo = ModoLista.Fija,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ListasPrecio.Add(lista);
        await db.SaveChangesAsync();

        var cliente = new Cliente
        {
            IdTenant = tenantA.Id, Numero = 2, Nombre = nombre, IdCondicionFiscal = condicionFiscal.Id,
            IdListaPrecio = lista.Id, CreatedAt = ahora, UpdatedAt = ahora
        };
        var clienteLibre = new Cliente
        {
            IdTenant = tenantA.Id, Numero = 3, Nombre = nombre, IdCondicionFiscal = condicionFiscal.Id,
            IdListaPrecio = lista.Id, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Clientes.AddRange(cliente, clienteLibre);

        var hasheador = new HasheadorPbkdf2();
        Usuario NuevoUsuario(string sufijo) => new()
        {
            IdTenant = tenantA.Id, NombreUsuario = sufijo, Mail = $"{nombre}-{sufijo}@ways.test",
            RolId = (int)RolConocido.Vendedor, PasswordHash = hasheador.Hashear("una-contraseña-larga"),
            PasswordAlgoritmo = hasheador.Algoritmo, PasswordActualizadoEl = ahora, CreatedAt = ahora, UpdatedAt = ahora
        };
        var usuario = NuevoUsuario("uno");
        db.Usuarios.Add(usuario);
        await db.SaveChangesAsync();

        var personal = new Personal
        {
            IdTenant = tenantA.Id, Nombre = nombre, IdUsuario = usuario.Id, IdCliente = cliente.Id,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Personal.Add(personal);
        await db.SaveChangesAsync();

        return new Siembra(tenantA.Id, tenantB.Id, personal.Id, cliente.Id, clienteLibre.Id, usuario.Id);
    }

    private async Task<NpgsqlCommand> ComandoAsync(int idTenant, string sql, params object[] parametros)
    {
        var conexion = await fixture.AbrirConexionCrudaAsync("tenant", idTenant);
        var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        foreach (var valor in parametros)
        {
            comando.Parameters.Add(new NpgsqlParameter { Value = valor });
        }
        return comando;
    }

    [Fact]
    public async Task SoloElTenantDuenoVeLaFilaPorSelect()
    {
        var s = await SembrarAsync(nameof(SoloElTenantDuenoVeLaFilaPorSelect));

        await using var deA = await ComandoAsync(s.IdTenantA, "SELECT count(*) FROM personal WHERE id_personal = $1", s.IdPersonalDeA);
        await using var deB = await ComandoAsync(s.IdTenantB, "SELECT count(*) FROM personal WHERE id_personal = $1", s.IdPersonalDeA);

        Assert.Equal(1L, await deA.ExecuteScalarAsync());
        Assert.Equal(0L, await deB.ExecuteScalarAsync());
    }

    [Fact]
    public async Task UnaSesionDeOtroTenantNoPuedeActualizarNiBorrarLaFila()
    {
        var s = await SembrarAsync(nameof(UnaSesionDeOtroTenantNoPuedeActualizarNiBorrarLaFila));

        await using var actualizar = await ComandoAsync(
            s.IdTenantB, "UPDATE personal SET updated_at = now() WHERE id_personal = $1", s.IdPersonalDeA);
        await using var borrar = await ComandoAsync(
            s.IdTenantB, "DELETE FROM personal WHERE id_personal = $1", s.IdPersonalDeA);

        Assert.Equal(0, await actualizar.ExecuteNonQueryAsync());
        Assert.Equal(0, await borrar.ExecuteNonQueryAsync());

        await using var deA = await ComandoAsync(s.IdTenantA, "SELECT count(*) FROM personal WHERE id_personal = $1", s.IdPersonalDeA);
        Assert.Equal(1L, await deA.ExecuteScalarAsync());
    }

    [Fact]
    public async Task UnInsertConIdTenantAjenoSeRechazaConPermisoDenegado()
    {
        var s = await SembrarAsync(nameof(UnInsertConIdTenantAjenoSeRechazaConPermisoDenegado));

        await using var insertar = await ComandoAsync(
            s.IdTenantA,
            "INSERT INTO personal (id_tenant, nombre, created_at, updated_at) VALUES ($1, 'intruso', now(), now())",
            s.IdTenantB);

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => insertar.ExecuteNonQueryAsync());
        Assert.Equal("42501", excepcion.SqlState);
    }

    [Fact]
    public async Task ElFiltroDeEfNoDevuelveElPersonalDeOtroTenant()
    {
        var s = await SembrarAsync(nameof(ElFiltroDeEfNoDevuelveElPersonalDeOtroTenant));

        await using var sesionB = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, s.IdTenantB));
        await using var sesionA = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, s.IdTenantA));

        Assert.Empty(await sesionB.Personal.Where(p => p.Id == s.IdPersonalDeA).ToListAsync());
        Assert.Single(await sesionA.Personal.Where(p => p.Id == s.IdPersonalDeA).ToListAsync());
    }

    [Fact]
    public async Task UnUsuarioOClienteDuplicadoEntreFilasVivasViolaLosIndicesParciales()
    {
        var s = await SembrarAsync(nameof(UnUsuarioOClienteDuplicadoEntreFilasVivasViolaLosIndicesParciales));

        await using var porUsuario = await ComandoAsync(
            s.IdTenantA,
            "INSERT INTO personal (id_tenant, nombre, id_usuario, created_at, updated_at) VALUES ($1, 'x', $2, now(), now())",
            s.IdTenantA, s.IdUsuarioDeA);
        var errorUsuario = await Assert.ThrowsAsync<PostgresException>(() => porUsuario.ExecuteNonQueryAsync());
        Assert.Equal("23505", errorUsuario.SqlState);
        Assert.Equal("ux_personal_usuario", errorUsuario.ConstraintName);

        await using var porCliente = await ComandoAsync(
            s.IdTenantA,
            "INSERT INTO personal (id_tenant, nombre, id_cliente, created_at, updated_at) VALUES ($1, 'x', $2, now(), now())",
            s.IdTenantA, s.IdClienteDeA);
        var errorCliente = await Assert.ThrowsAsync<PostgresException>(() => porCliente.ExecuteNonQueryAsync());
        Assert.Equal("23505", errorCliente.SqlState);
        Assert.Equal("ux_personal_cliente", errorCliente.ConstraintName);
    }

    [Fact]
    public async Task ElVinculoDeUnaFilaDadaDeBajaQuedaLibreEnLosIndices()
    {
        var s = await SembrarAsync(nameof(ElVinculoDeUnaFilaDadaDeBajaQuedaLibreEnLosIndices));

        await using var baja = await ComandoAsync(
            s.IdTenantA, "UPDATE personal SET deleted_at = now() WHERE id_personal = $1", s.IdPersonalDeA);
        Assert.Equal(1, await baja.ExecuteNonQueryAsync());

        await using var reutiliza = await ComandoAsync(
            s.IdTenantA,
            "INSERT INTO personal (id_tenant, nombre, id_usuario, id_cliente, created_at, updated_at) " +
            "VALUES ($1, 'nuevo', $2, $3, now(), now())",
            s.IdTenantA, s.IdUsuarioDeA, s.IdClienteDeA);
        Assert.Equal(1, await reutiliza.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task VariasFilasSinVinculoConviven()
    {
        var s = await SembrarAsync(nameof(VariasFilasSinVinculoConviven));

        for (var i = 0; i < 2; i++)
        {
            await using var insertar = await ComandoAsync(
                s.IdTenantA,
                "INSERT INTO personal (id_tenant, nombre, created_at, updated_at) VALUES ($1, 'sin vínculos', now(), now())",
                s.IdTenantA);
            Assert.Equal(1, await insertar.ExecuteNonQueryAsync());
        }
    }

    [Fact]
    public async Task UnValorHoraNegativoViolaLaCheckConstraint()
    {
        var s = await SembrarAsync(nameof(UnValorHoraNegativoViolaLaCheckConstraint));

        await using var insertar = await ComandoAsync(
            s.IdTenantA,
            "INSERT INTO personal (id_tenant, nombre, valor_hora, created_at, updated_at) VALUES ($1, 'x', -0.01, now(), now())",
            s.IdTenantA);

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => insertar.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_personal_valor_hora", excepcion.ConstraintName);
    }

    [Fact]
    public async Task LasForeignKeysRechazanUsuarioInexistenteYClienteDeOtroTenant()
    {
        var s = await SembrarAsync(nameof(LasForeignKeysRechazanUsuarioInexistenteYClienteDeOtroTenant));

        await using var usuario = await ComandoAsync(
            s.IdTenantA,
            "INSERT INTO personal (id_tenant, nombre, id_usuario, created_at, updated_at) VALUES ($1, 'x', 999999, now(), now())",
            s.IdTenantA);
        var errorUsuario = await Assert.ThrowsAsync<PostgresException>(() => usuario.ExecuteNonQueryAsync());
        Assert.Equal("23503", errorUsuario.SqlState);
        Assert.Equal("fk_personal_usuario", errorUsuario.ConstraintName);

        await using var cliente = await ComandoAsync(
            s.IdTenantB,
            "INSERT INTO personal (id_tenant, nombre, id_cliente, created_at, updated_at) VALUES ($1, 'x', $2, now(), now())",
            s.IdTenantB, s.IdClienteLibreDeA);
        var errorCliente = await Assert.ThrowsAsync<PostgresException>(() => cliente.ExecuteNonQueryAsync());
        Assert.Equal("23503", errorCliente.SqlState);
        Assert.Equal("fk_personal_cliente", errorCliente.ConstraintName);
    }
}
