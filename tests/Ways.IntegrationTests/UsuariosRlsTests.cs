using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// Judgment-day (batch 9, ronda 2), aprobado "usuarios RLS raw-SQL isolation tests": espejo de
/// <see cref="AislamientoDeTenantTests.RlsBloqueaUnaLecturaQueSalteaElFiltroDeEf"/> pero sobre
/// <c>usuarios</c> — la única tabla con la policy extra de login (ADR-15), así que necesita su
/// propia prueba dedicada: acá se confirma que esa tercera rama (<c>app_modo() = 'login'</c>)
/// no se cuela por accidente fuera de modo login, ni para cuentas de otro tenant ni para
/// cuentas de plataforma. Todo con conexión cruda como <c>ways_app</c>, sin pasar por EF: es
/// la prueba de que el aislamiento es RLS, no el filtro de EF (que estas mismas conexiones
/// crudas ni siquiera ejecutan).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class UsuariosRlsTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string Password = "una-contraseña-larga";

    private async Task<(int IdUsuario, int IdTenant)> SembrarUsuarioDeTenantAsync(string nombre)
    {
        using var _ = fixture.CreateClient(); // arranca el host: siembra los roles primero

        var hasheador = new HasheadorPbkdf2();
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var ahora = DateTimeOffset.UtcNow;
        var tenant = new Tenant
        {
            Nombre = nombre, Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var usuario = new Usuario
        {
            IdTenant = tenant.Id,
            NombreUsuario = "admin",
            Mail = $"{nombre.ToLowerInvariant()}@ways.test",
            RolId = (int)RolConocido.Admin,
            PasswordHash = hasheador.Hashear(Password),
            PasswordAlgoritmo = hasheador.Algoritmo,
            PasswordActualizadoEl = ahora,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Usuarios.Add(usuario);
        await db.SaveChangesAsync();

        return (usuario.Id, tenant.Id);
    }

    [Fact]
    public async Task RlsBloqueaLeerYActualizarUnUsuarioDeOtroTenant()
    {
        var (idUsuarioA, idTenantA) = await SembrarUsuarioDeTenantAsync(
            nameof(RlsBloqueaLeerYActualizarUnUsuarioDeOtroTenant) + "-A");
        var (idUsuarioB, _) = await SembrarUsuarioDeTenantAsync(
            nameof(RlsBloqueaLeerYActualizarUnUsuarioDeOtroTenant) + "-B");

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", idTenantA);

        // Lectura: la fila de B es invisible para la conexión de A.
        await using (var comando = cruda.CreateCommand())
        {
            comando.CommandText = "SELECT id_usuario FROM usuarios WHERE id_usuario IN ($1, $2)";
            comando.Parameters.Add(new NpgsqlParameter { Value = idUsuarioA });
            comando.Parameters.Add(new NpgsqlParameter { Value = idUsuarioB });

            await using var lector = await comando.ExecuteReaderAsync();
            var vistos = new List<int>();
            while (await lector.ReadAsync())
            {
                vistos.Add(lector.GetInt32(0));
            }

            Assert.Equal([idUsuarioA], vistos);
        }

        // Update: 0 filas afectadas, no una excepción — RLS filtra la fila de B del conjunto
        // que el UPDATE puede tocar antes de evaluar el WHERE, no rechaza un WITH CHECK sobre
        // una fila que sí llegó a ver.
        await using (var comando = cruda.CreateCommand())
        {
            comando.CommandText = "UPDATE usuarios SET usuario = 'hackeado' WHERE id_usuario = $1";
            comando.Parameters.Add(new NpgsqlParameter { Value = idUsuarioB });

            var filas = await comando.ExecuteNonQueryAsync();
            Assert.Equal(0, filas);
        }
    }

    [Fact]
    public async Task RlsBloqueaLeerUnaCuentaDePlataformaDesdeUnaSesionDeTenant()
    {
        var (_, idTenantA) = await SembrarUsuarioDeTenantAsync(
            nameof(RlsBloqueaLeerUnaCuentaDePlataformaDesdeUnaSesionDeTenant));

        // El seed de InicializadorDeBaseDeDatos ya dejó una cuenta root (id_tenant NULL) al
        // arrancar el host, en SembrarUsuarioDeTenantAsync.
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", idTenantA);

        await using var comando = cruda.CreateCommand();
        comando.CommandText = "SELECT count(*) FROM usuarios WHERE id_tenant IS NULL";
        var total = (long)(await comando.ExecuteScalarAsync())!;

        Assert.Equal(0, total);
    }

    [Fact]
    public async Task LasPoliciesDeLoginNoAplicanFueraDeModoLogin()
    {
        // Sin ningún GUC seteado (modo "ninguno", ni siquiera "tenant"): ni usuarios_tenant
        // (falla cerrado, id_tenant = NULL nunca iguala) ni las dos policies de login (exigen
        // literalmente app_modo() = 'login') dejan ver una sola fila.
        await SembrarUsuarioDeTenantAsync(nameof(LasPoliciesDeLoginNoAplicanFueraDeModoLogin));

        await using var cruda = await fixture.AbrirConexionCrudaAsync(string.Empty, null);

        await using var comando = cruda.CreateCommand();
        comando.CommandText = "SELECT count(*) FROM usuarios";
        var total = (long)(await comando.ExecuteScalarAsync())!;

        Assert.Equal(0, total);
    }
}
