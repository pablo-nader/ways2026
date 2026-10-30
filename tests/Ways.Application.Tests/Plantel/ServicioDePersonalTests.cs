using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Bajas;
using Ways.Application.Organizacion;
using Ways.Application.Plantel;
using Ways.Domain.Clientes;
using Ways.Domain.Common;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.Application.Tests.Plantel;

/// <summary>
/// <see cref="ServicioDePersonal"/> sobre el proveedor InMemory. La baja lógica por la API se cubre
/// en <c>PersonalEndpointsTests</c>; acá los datos dados de baja se siembran con
/// <see cref="MarcarComoEliminadoAsync"/>.
/// </summary>
public class ServicioDePersonalTests
{
    private static readonly DateTimeOffset Ahora = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private const int Tenant = 1;
    private const int OtroTenant = 2;

    private sealed class RelojFijo(DateTimeOffset ahora) : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = ahora;
    }

    private static WaysDbContext CrearContexto(string baseDeDatos, ITenantActual tenantActual) =>
        new(new DbContextOptionsBuilder<WaysDbContext>().UseInMemoryDatabase(baseDeDatos).Options, tenantActual);

    private static ServicioDePersonal CrearServicio(string baseDeDatos, int idTenant = Tenant)
    {
        var contexto = CrearContexto(baseDeDatos, new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        return new ServicioDePersonal(contexto, new RelojFijo(Ahora), new GuardaDeReferencias(contexto, new InspectorDeUso(contexto)));
    }

    private static DatosDePersonal Datos(
        string nombre = "Ana", decimal valorHora = 0m, int? idUsuario = null, int? idCliente = null) =>
        new(nombre, null, null, null, valorHora, null, idUsuario, idCliente);

    private static async Task<int> SembrarUsuarioAsync(string baseDeDatos, int? idTenant, string nombre = "ana")
    {
        await using var siembra = CrearContexto(baseDeDatos, TenantActualFijo.Plataforma);
        var usuario = new Usuario
        {
            IdTenant = idTenant, NombreUsuario = nombre, Mail = $"{nombre}@{idTenant}.test", RolId = 2,
            PasswordHash = "x", PasswordAlgoritmo = "x", PasswordActualizadoEl = Ahora,
            CreatedAt = Ahora, UpdatedAt = Ahora
        };
        siembra.Usuarios.Add(usuario);
        await siembra.SaveChangesAsync();
        return usuario.Id;
    }

    private static async Task<int> SembrarClienteAsync(
        string baseDeDatos, int idTenant, int numero = 2, string nombre = "Cliente")
    {
        await using var siembra = CrearContexto(baseDeDatos, TenantActualFijo.Plataforma);
        var cliente = new Cliente
        {
            IdTenant = idTenant, Numero = numero, Nombre = nombre, Apellido = "Pérez",
            IdCondicionFiscal = 1, IdListaPrecio = 1, CreatedAt = Ahora, UpdatedAt = Ahora
        };
        siembra.Clientes.Add(cliente);
        await siembra.SaveChangesAsync();
        return cliente.Id;
    }

    private static async Task MarcarComoEliminadoAsync(string baseDeDatos, params (string Tabla, int Id)[] filas)
    {
        await using var siembra = CrearContexto(baseDeDatos, TenantActualFijo.Plataforma);
        foreach (var (tabla, id) in filas)
        {
            EntidadBase entidad = tabla switch
            {
                "usuarios" => await siembra.Usuarios.FirstAsync(u => u.Id == id),
                "clientes" => await siembra.Clientes.FirstAsync(c => c.Id == id),
                _ => await siembra.Personal.FirstAsync(p => p.Id == id)
            };
            entidad.DeletedAt = Ahora;
        }
        await siembra.SaveChangesAsync();
    }

    [Fact]
    public async Task SeCreaUnPersonalSinVinculosYSeLeeDeVuelta()
    {
        var bd = nameof(SeCreaUnPersonalSinVinculosYSeLeeDeVuelta);
        var servicio = CrearServicio(bd);

        var creado = await servicio.CrearAsync(new DatosDePersonal(
            "  Ana  ", "20-1", "11-5555", "turno tarde", 1500.50m, null, null, null));

        var leido = await servicio.ObtenerAsync(creado.Id);
        Assert.Equal("Ana", leido.Nombre);
        Assert.Equal("20-1", leido.Documento);
        Assert.Equal("11-5555", leido.Telefono);
        Assert.Equal("turno tarde", leido.Observaciones);
        Assert.Equal(1500.50m, leido.ValorHora);
        Assert.Null(leido.IdUsuario);
        Assert.Null(leido.IdCliente);
    }

    private static readonly decimal[] SemanaLaboral = [8m, 8m, 8m, 8m, 8m, 4.5m, 0m];

    [Fact]
    public async Task ElHorarioHabitualSeGuardaAlCrearSeReemplazaAlEditarYSeBorraConNull()
    {
        var bd = nameof(ElHorarioHabitualSeGuardaAlCrearSeReemplazaAlEditarYSeBorraConNull);
        var servicio = CrearServicio(bd);

        var creado = await servicio.CrearAsync(Datos() with { HorarioHabitual = SemanaLaboral });
        Assert.Equal(SemanaLaboral, creado.HorarioHabitual);
        Assert.Equal(SemanaLaboral, (await servicio.ListarAsync()).Items.Single().HorarioHabitual);

        decimal[] otra = [0m, 6m, 6m, 6m, 6m, 6m, 0m];
        var editado = await servicio.ActualizarAsync(creado.Id, Datos() with { HorarioHabitual = otra });
        Assert.Equal(otra, editado.HorarioHabitual);

        var sinHorario = await servicio.ActualizarAsync(creado.Id, Datos());
        Assert.Null(sinHorario.HorarioHabitual);
        Assert.Null((await servicio.ObtenerAsync(creado.Id)).HorarioHabitual);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(8)]
    public async Task UnHorarioDeOtraLongitudSeRechazaAlCrearYAlEditar(int largo)
    {
        var bd = nameof(UnHorarioDeOtraLongitudSeRechazaAlCrearYAlEditar) + largo;
        var servicio = CrearServicio(bd);
        var invalido = Datos() with { HorarioHabitual = new decimal[largo] };

        var alCrear = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.CrearAsync(invalido));
        Assert.Equal("horario_habitual_invalido", alCrear.Codigo);

        var existente = await servicio.CrearAsync(Datos());
        var alEditar = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.ActualizarAsync(existente.Id, invalido));
        Assert.Equal("horario_habitual_invalido", alEditar.Codigo);
    }

    [Fact]
    public async Task EditarUnPersonalInexistenteResponde404AunqueElHorarioSeaInvalido()
    {
        var servicio = CrearServicio(nameof(EditarUnPersonalInexistenteResponde404AunqueElHorarioSeaInvalido));

        var error = await Assert.ThrowsAsync<ErrorDominio>(
            () => servicio.ActualizarAsync(999, Datos() with { HorarioHabitual = new decimal[3] }));

        Assert.Equal(404, error.EstadoHttp);
    }

    [Fact]
    public async Task SeCreaConUsuarioYClienteYElListadoMuestraSusNombres()
    {
        var bd = nameof(SeCreaConUsuarioYClienteYElListadoMuestraSusNombres);
        var idUsuario = await SembrarUsuarioAsync(bd, Tenant, "anita");
        var idCliente = await SembrarClienteAsync(bd, Tenant);
        var servicio = CrearServicio(bd);

        await servicio.CrearAsync(Datos(idUsuario: idUsuario, idCliente: idCliente));

        var pagina = await servicio.ListarAsync();
        var fila = Assert.Single(pagina.Items);
        Assert.Equal(idUsuario, fila.IdUsuario);
        Assert.Equal("anita", fila.NombreUsuario);
        Assert.Equal(idCliente, fila.IdCliente);
        Assert.Equal("Cliente Pérez", fila.NombreCliente);
    }

    [Theory]
    [InlineData("   ", 0, "nombre_requerido")]
    [InlineData("Ana", -1, "valor_hora_invalido")]
    [InlineData("Ana", 1000000000000, "valor_hora_invalido")]
    public async Task UnaAltaInvalidaSeRechazaConSuCodigo(string nombre, long valorHora, string codigo)
    {
        var servicio = CrearServicio(nameof(UnaAltaInvalidaSeRechazaConSuCodigo) + codigo + valorHora);

        var error = await Assert.ThrowsAsync<ErrorDominio>(
            () => servicio.CrearAsync(Datos(nombre, valorHora)));

        Assert.Equal(codigo, error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public async Task UnValorHoraConMasDeDosDecimalesSeRechaza()
    {
        var servicio = CrearServicio(nameof(UnValorHoraConMasDeDosDecimalesSeRechaza));

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.CrearAsync(Datos(valorHora: 10.005m)));

        Assert.Equal("valor_hora_invalido", error.Codigo);
    }

    [Fact]
    public async Task ElClienteConsumidorFinalSeRechaza()
    {
        var bd = nameof(ElClienteConsumidorFinalSeRechaza);
        var idConsumidorFinal = await SembrarClienteAsync(bd, Tenant, numero: ReglaDeClientes.NumeroConsumidorFinal);
        var servicio = CrearServicio(bd);

        var error = await Assert.ThrowsAsync<ErrorDominio>(
            () => servicio.CrearAsync(Datos(idCliente: idConsumidorFinal)));

        Assert.Equal("cliente_consumidor_final", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public async Task UnClienteInexistenteEliminadoOAjenoSeRechazaComoReferenciaInvalida()
    {
        var bd = nameof(UnClienteInexistenteEliminadoOAjenoSeRechazaComoReferenciaInvalida);
        var eliminado = await SembrarClienteAsync(bd, Tenant, 2, "Borrado");
        var ajeno = await SembrarClienteAsync(bd, OtroTenant, 3, "Ajeno");
        await MarcarComoEliminadoAsync(bd, ("clientes", eliminado));
        var servicio = CrearServicio(bd);

        foreach (var idCliente in new[] { 9999, eliminado, ajeno })
        {
            var error = await Assert.ThrowsAsync<ErrorDominio>(
                () => servicio.CrearAsync(Datos(idCliente: idCliente)));
            Assert.Equal("referencia_invalida", error.Codigo);
            Assert.Equal(400, error.EstadoHttp);
        }
    }

    [Fact]
    public async Task UnUsuarioInexistenteEliminadoAjenoOdePlataformaSeRechaza()
    {
        var bd = nameof(UnUsuarioInexistenteEliminadoAjenoOdePlataformaSeRechaza);
        var eliminado = await SembrarUsuarioAsync(bd, Tenant, "borrado");
        var ajeno = await SembrarUsuarioAsync(bd, OtroTenant, "ajeno");
        var deLaPlataforma = await SembrarUsuarioAsync(bd, null, "root");
        await MarcarComoEliminadoAsync(bd, ("usuarios", eliminado));
        var servicio = CrearServicio(bd);

        foreach (var idUsuario in new[] { 9999, eliminado, ajeno, deLaPlataforma })
        {
            var error = await Assert.ThrowsAsync<ErrorDominio>(
                () => servicio.CrearAsync(Datos(idUsuario: idUsuario)));
            Assert.Equal("usuario_invalido", error.Codigo);
            Assert.Equal(400, error.EstadoHttp);
        }
    }

    [Fact]
    public async Task UnUsuarioYaVinculadoDaConflicto()
    {
        var bd = nameof(UnUsuarioYaVinculadoDaConflicto);
        var idUsuario = await SembrarUsuarioAsync(bd, Tenant);
        var servicio = CrearServicio(bd);
        await servicio.CrearAsync(Datos("Ana", idUsuario: idUsuario));

        var error = await Assert.ThrowsAsync<ErrorDominio>(
            () => servicio.CrearAsync(Datos("Beto", idUsuario: idUsuario)));

        Assert.Equal("usuario_ya_vinculado", error.Codigo);
        Assert.Equal(409, error.EstadoHttp);
    }

    [Fact]
    public async Task UnClienteYaVinculadoDaConflicto()
    {
        var bd = nameof(UnClienteYaVinculadoDaConflicto);
        var idCliente = await SembrarClienteAsync(bd, Tenant);
        var servicio = CrearServicio(bd);
        await servicio.CrearAsync(Datos("Ana", idCliente: idCliente));

        var error = await Assert.ThrowsAsync<ErrorDominio>(
            () => servicio.CrearAsync(Datos("Beto", idCliente: idCliente)));

        Assert.Equal("cliente_ya_vinculado", error.Codigo);
        Assert.Equal(409, error.EstadoHttp);
    }

    [Fact]
    public async Task ElVinculoDeUnPersonalDadoDeBajaQuedaLibre()
    {
        var bd = nameof(ElVinculoDeUnPersonalDadoDeBajaQuedaLibre);
        var idUsuario = await SembrarUsuarioAsync(bd, Tenant);
        var idCliente = await SembrarClienteAsync(bd, Tenant);
        var servicio = CrearServicio(bd);
        var primero = await servicio.CrearAsync(Datos("Ana", idUsuario: idUsuario, idCliente: idCliente));
        await MarcarComoEliminadoAsync(bd, ("personal", primero.Id));

        var segundo = await servicio.CrearAsync(Datos("Beto", idUsuario: idUsuario, idCliente: idCliente));

        Assert.Equal(idUsuario, segundo.IdUsuario);
        Assert.Equal(idCliente, segundo.IdCliente);
    }

    [Fact]
    public async Task ActualizarConservaSusPropiosVinculosYPuedeCambiarlos()
    {
        var bd = nameof(ActualizarConservaSusPropiosVinculosYPuedeCambiarlos);
        var idUsuario = await SembrarUsuarioAsync(bd, Tenant, "uno");
        var otroUsuario = await SembrarUsuarioAsync(bd, Tenant, "dos");
        var idCliente = await SembrarClienteAsync(bd, Tenant);
        var servicio = CrearServicio(bd);
        var creado = await servicio.CrearAsync(Datos("Ana", idUsuario: idUsuario, idCliente: idCliente));

        var mismo = await servicio.ActualizarAsync(
            creado.Id, Datos("Ana María", 99m, idUsuario, idCliente));
        Assert.Equal("Ana María", mismo.Nombre);
        Assert.Equal(99m, mismo.ValorHora);

        var cambiado = await servicio.ActualizarAsync(creado.Id, Datos("Ana María", 99m, otroUsuario, null));
        Assert.Equal(otroUsuario, cambiado.IdUsuario);
        Assert.Null(cambiado.IdCliente);
    }

    [Fact]
    public async Task ActualizarNoPuedeTomarElVinculoDeOtroPersonal()
    {
        var bd = nameof(ActualizarNoPuedeTomarElVinculoDeOtroPersonal);
        var idUsuario = await SembrarUsuarioAsync(bd, Tenant);
        var idCliente = await SembrarClienteAsync(bd, Tenant);
        var servicio = CrearServicio(bd);
        await servicio.CrearAsync(Datos("Ana", idUsuario: idUsuario, idCliente: idCliente));
        var beto = await servicio.CrearAsync(Datos("Beto"));

        var errorUsuario = await Assert.ThrowsAsync<ErrorDominio>(
            () => servicio.ActualizarAsync(beto.Id, Datos("Beto", idUsuario: idUsuario)));
        var errorCliente = await Assert.ThrowsAsync<ErrorDominio>(
            () => servicio.ActualizarAsync(beto.Id, Datos("Beto", idCliente: idCliente)));

        Assert.Equal("usuario_ya_vinculado", errorUsuario.Codigo);
        Assert.Equal("cliente_ya_vinculado", errorCliente.Codigo);
    }

    [Fact]
    public async Task ActualizarYObtenerDeUnIdInexistenteEliminadoOAjenoDan404AntesQueCualquierValidacion()
    {
        var bd = nameof(ActualizarYObtenerDeUnIdInexistenteEliminadoOAjenoDan404AntesQueCualquierValidacion);
        var idCliente = await SembrarClienteAsync(bd, Tenant);
        var servicio = CrearServicio(bd);
        var vivo = await servicio.CrearAsync(Datos("Vivo", idCliente: idCliente));
        var eliminado = await servicio.CrearAsync(Datos("Eliminado"));
        await MarcarComoEliminadoAsync(bd, ("personal", eliminado.Id));
        var ajeno = await CrearServicio(bd, OtroTenant).CrearAsync(Datos("Ajeno"));

        // Cuerpo inválido (nombre vacío, valor negativo) y con el vínculo de otro personal.
        var invalido = Datos("   ", -5m, idCliente: idCliente);
        foreach (var id in new[] { 9999, eliminado.Id, ajeno.Id })
        {
            var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.ActualizarAsync(id, invalido));
            Assert.Equal(404, error.EstadoHttp);
            var errorLectura = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.ObtenerAsync(id));
            Assert.Equal(404, errorLectura.EstadoHttp);
        }

        var errorVivo = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.ActualizarAsync(vivo.Id, invalido));
        Assert.Equal("nombre_requerido", errorVivo.Codigo);
    }

    [Fact]
    public async Task UnVinculoConUnUsuarioOClienteDadoDeBajaSeMuestraComoSinVincularEnLaLectura()
    {
        var bd = nameof(UnVinculoConUnUsuarioOClienteDadoDeBajaSeMuestraComoSinVincularEnLaLectura);
        var idUsuario = await SembrarUsuarioAsync(bd, Tenant);
        var idCliente = await SembrarClienteAsync(bd, Tenant);
        var servicio = CrearServicio(bd);
        var creado = await servicio.CrearAsync(Datos("Ana", idUsuario: idUsuario, idCliente: idCliente));
        await servicio.CrearAsync(Datos("Beto"));
        await MarcarComoEliminadoAsync(bd, ("usuarios", idUsuario), ("clientes", idCliente));

        var pagina = await servicio.ListarAsync(tamanio: 1);
        var detalle = await servicio.ObtenerAsync(creado.Id);

        Assert.Equal(2, pagina.Total);
        Assert.Single(pagina.Items);
        foreach (var fila in new[] { pagina.Items[0], detalle })
        {
            Assert.Null(fila.IdUsuario);
            Assert.Null(fila.NombreUsuario);
            Assert.Null(fila.IdCliente);
            Assert.Null(fila.NombreCliente);
        }
    }

    [Fact]
    public async Task ElListadoNoMuestraPersonalDeOtroTenantNiDadoDeBajaYSePagina()
    {
        var bd = nameof(ElListadoNoMuestraPersonalDeOtroTenantNiDadoDeBajaYSePagina);
        var servicio = CrearServicio(bd);
        await servicio.CrearAsync(Datos("Carla"));
        await servicio.CrearAsync(Datos("Ana"));
        var baja = await servicio.CrearAsync(Datos("Baja"));
        await CrearServicio(bd, OtroTenant).CrearAsync(Datos("Ajeno"));
        await MarcarComoEliminadoAsync(bd, ("personal", baja.Id));

        var primera = await servicio.ListarAsync(tamanio: 1);
        var segunda = await servicio.ListarAsync(pagina: 2, tamanio: 1);

        Assert.Equal(2, primera.Total);
        Assert.Equal("Ana", Assert.Single(primera.Items).Nombre);
        Assert.Equal("Carla", Assert.Single(segunda.Items).Nombre);
    }
}
