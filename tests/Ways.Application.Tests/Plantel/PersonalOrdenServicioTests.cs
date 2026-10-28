using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Bajas;
using Ways.Application.Organizacion;
using Ways.Application.Plantel;
using Ways.Application.Tests.Listados;
using Ways.Domain.Clientes;
using Ways.Domain.Common;
using Ways.Domain.Plantel;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.Application.Tests.Plantel;

/// <summary>
/// <see cref="ServicioDePersonal.ListarAsync"/> con orden y filtros que no son de texto, sobre InMemory: la secuencia
/// completa sale de recorrer las páginas de a dos (el orden se aplica antes de paginar), con las celdas vacías al final
/// en las dos direcciones y un usuario o cliente dado de baja ordenado como "sin vincular". El filtro de texto
/// (<c>Coincide</c>) y la traducción a SQL no corren en InMemory: los cubre <c>PersonalGrillaEndpointTests</c> contra
/// Postgres.
///
/// Posiciones (1 a 5, en orden de alta): nombre Dora/Ana/Eva/Beto/Cira, valor hora 100/300/200/500/400, saldo
/// 50/-10/0/-30/20, usuario zoe/-/ana-u/(baja)/bob, cliente "Acme Q"/-/razón social "Zulu SA"/(baja)/Beto.
/// </summary>
public class PersonalOrdenServicioTests
{
    private const int IdTenant = 7;
    private static readonly DateTimeOffset Ahora = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    private sealed class RelojFijo : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = PersonalOrdenServicioTests.Ahora;
    }

    private static WaysDbContext CrearContexto(string baseDeDatos, ITenantActual tenant) =>
        new(new DbContextOptionsBuilder<WaysDbContext>().UseInMemoryDatabase(baseDeDatos).Options, tenant);

    private static async Task<(ServicioDePersonal Servicio, List<int> Ids)> PrepararAsync(string baseDeDatos)
    {
        var ids = new List<int>();
        await using (var siembra = CrearContexto(baseDeDatos, TenantActualFijo.Plataforma))
        {
            Usuario NuevoUsuario(string nombre, bool baja = false) => new()
            {
                IdTenant = IdTenant, NombreUsuario = nombre, Mail = $"{nombre}@t.test", RolId = 2,
                PasswordHash = "x", PasswordAlgoritmo = "x", PasswordActualizadoEl = Ahora,
                DeletedAt = baja ? Ahora : null, CreatedAt = Ahora, UpdatedAt = Ahora
            };
            var numero = 2;
            Cliente NuevoCliente(string nombre, string? apellido, string? razonSocial = null, bool baja = false) => new()
            {
                IdTenant = IdTenant, Numero = numero++, Nombre = nombre, Apellido = apellido, RazonSocial = razonSocial,
                IdCondicionFiscal = 1, IdListaPrecio = 1, DeletedAt = baja ? Ahora : null, CreatedAt = Ahora, UpdatedAt = Ahora
            };

            var zoe = NuevoUsuario("zoe");
            var anaU = NuevoUsuario("ana-u");
            var usuarioDeBaja = NuevoUsuario("aaa-baja", baja: true);
            var bob = NuevoUsuario("bob");
            var acme = NuevoCliente("Acme", "Q");
            var zulu = NuevoCliente("Aaron", "Zz", razonSocial: "Zulu SA");
            var clienteDeBaja = NuevoCliente("Aaa", null, baja: true);
            var beto = NuevoCliente("Beto", null);
            siembra.AddRange(zoe, anaU, usuarioDeBaja, bob, acme, zulu, clienteDeBaja, beto);
            await siembra.SaveChangesAsync();

            (string Nombre, decimal ValorHora, decimal Saldo, int? Usuario, int? Cliente)[] filas =
            [
                ("Dora", 100m, 50m, zoe.Id, acme.Id),
                ("Ana", 300m, -10m, null, null),
                ("Eva", 200m, 0m, anaU.Id, zulu.Id),
                ("Beto", 500m, -30m, usuarioDeBaja.Id, clienteDeBaja.Id),
                ("Cira", 400m, 20m, bob.Id, beto.Id),
            ];

            foreach (var f in filas)
            {
                var personal = new Personal
                {
                    IdTenant = IdTenant, Nombre = f.Nombre, ValorHora = f.ValorHora, Saldo = f.Saldo,
                    IdUsuario = f.Usuario, IdCliente = f.Cliente, CreatedAt = Ahora, UpdatedAt = Ahora
                };
                siembra.Personal.Add(personal);
                await siembra.SaveChangesAsync();
                ids.Add(personal.Id);
            }
        }

        var contexto = CrearContexto(baseDeDatos, new TenantActualFijo(ModoDeAcceso.Tenant, IdTenant));
        var servicio = new ServicioDePersonal(
            contexto, new RelojFijo(), new GuardaDeReferencias(contexto, new InspectorDeUso(contexto)));

        return (servicio, ids);
    }

    private static Task<List<int>> RecorrerAsync(
        ServicioDePersonal servicio, string? orden, string? direccion, FiltrosDePersonal? filtros = null) =>
        RecorridoDePaginas.RecorrerAsync(
            async (pagina, tamanio) => await servicio.ListarAsync(
                pagina: pagina, tamanio: tamanio, filtros: filtros, orden: orden, direccion: direccion),
            (PersonalListado p) => p.Id);

    [Theory]
    [InlineData("nombre", "ascendente", "2,4,5,1,3")]
    [InlineData("nombre", "descendente", "3,1,5,4,2")]
    [InlineData("valorHora", "ascendente", "1,3,2,5,4")]
    [InlineData("valorHora", "descendente", "4,5,2,3,1")]
    [InlineData("saldo", "ascendente", "4,2,3,5,1")]
    [InlineData("saldo", "descendente", "1,5,3,2,4")]
    [InlineData("usuario", "ascendente", "3,5,1,2,4")]
    [InlineData("usuario", "descendente", "1,5,3,2,4")]
    [InlineData("cliente", "ascendente", "1,5,3,2,4")]
    [InlineData("cliente", "descendente", "3,5,1,2,4")]
    public async Task LasPaginasConsecutivasContinuanElOrdenDeTodoElConjunto(string orden, string direccion, string esperado)
    {
        var (servicio, ids) = await PrepararAsync($"personal-orden-{orden}-{direccion}");

        var obtenido = await RecorrerAsync(servicio, orden, direccion);

        Assert.Equal(RecorridoDePaginas.PorPosicion(ids, esperado), obtenido);
    }

    [Fact]
    public async Task SinOrdenSigueElOrdenDeSiempreQueEsElNombre()
    {
        var (servicio, ids) = await PrepararAsync(nameof(SinOrdenSigueElOrdenDeSiempreQueEsElNombre));

        Assert.Equal(RecorridoDePaginas.PorPosicion(ids, "2,4,5,1,3"), await RecorrerAsync(servicio, null, null));
    }

    [Fact]
    public async Task LosRangosSonInclusivosYSeCombinanConElOrdenYElTotalEsElDelFiltro()
    {
        var (servicio, ids) = await PrepararAsync(nameof(LosRangosSonInclusivosYSeCombinanConElOrdenYElTotalEsElDelFiltro));

        var porValor = await servicio.ListarAsync(filtros: new FiltrosDePersonal(ValorHoraDesde: 200m, ValorHoraHasta: 400m));
        var porSaldo = await servicio.ListarAsync(
            filtros: new FiltrosDePersonal(SaldoDesde: -10m, SaldoHasta: 20m), orden: "saldo", direccion: "descendente");
        var paginada = await servicio.ListarAsync(
            tamanio: 1, filtros: new FiltrosDePersonal(SaldoDesde: -10m), orden: "saldo", direccion: "ascendente");

        Assert.Equal(RecorridoDePaginas.PorPosicion(ids, "2,5,3"), porValor.Items.Select(p => p.Id).ToList());
        Assert.Equal(RecorridoDePaginas.PorPosicion(ids, "5,3,2"), porSaldo.Items.Select(p => p.Id).ToList());
        Assert.Equal(4, paginada.Total);
        Assert.Equal(ids[1], Assert.Single(paginada.Items).Id);
    }

    [Fact]
    public async Task ElListadoDevuelveElNombreDelVinculoVivoYNadaDelDadoDeBaja()
    {
        var (servicio, ids) = await PrepararAsync(nameof(ElListadoDevuelveElNombreDelVinculoVivoYNadaDelDadoDeBaja));

        var pagina = await servicio.ListarAsync(tamanio: 5, orden: "cliente", direccion: "ascendente");

        Assert.Equal(
            ["Acme Q", "Beto", "Zulu SA", null, null],
            pagina.Items.Select(p => p.NombreCliente).ToList());
        Assert.Equal(
            ["zoe", "bob", "ana-u", null, null],
            pagina.Items.Select(p => p.NombreUsuario).ToList());
        Assert.Equal(RecorridoDePaginas.PorPosicion(ids, "1,5,3,2,4"), pagina.Items.Select(p => p.Id).ToList());
    }

    [Theory]
    [InlineData("acciones", null, "orden_invalido")]
    [InlineData("valorhora", null, "orden_invalido")]
    [InlineData("saldo", "asc", "direccion_invalida")]
    [InlineData(null, "ascendente", "direccion_sin_orden")]
    public async Task UnOrdenInvalidoSeRechazaConCuatrocientos(string? orden, string? direccion, string codigo)
    {
        var (servicio, _) = await PrepararAsync($"personal-400-{orden}-{direccion}");

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.ListarAsync(orden: orden, direccion: direccion));

        Assert.Equal(codigo, error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void LasClavesDeOrdenSonLasColumnasDeDatosDeLaGrilla()
    {
        Assert.Equal(
            ["cliente", "nombre", "saldo", "usuario", "valorHora"],
            OrdenDePersonal.Claves.Keys.Order(StringComparer.Ordinal).ToList());
    }
}
