using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Parametros;
using Ways.Domain.Common;
using Ways.Domain.Organizacion;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.Application.Tests.Parametros;

/// <summary>
/// Judgment-day (slice 3, ronda 1): sin FK de <c>parametros.id_punto_venta</c> a
/// <c>empresas</c> en el esquema (decisión del usuario, sin cambio de esquema), un punto de
/// venta real pero de otra empresa del mismo tenant pasaba sin chequeo — <see
/// cref="ServicioDeParametros"/> es el único lugar que lo valida.
/// </summary>
public class ServicioDeParametrosTests
{
    private static readonly DateTimeOffset Ahora = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    private sealed class RelojFijo(DateTimeOffset ahora) : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = ahora;
    }

    private static WaysDbContext CrearContexto(string nombreDeBase) =>
        new(
            new DbContextOptionsBuilder<WaysDbContext>().UseInMemoryDatabase(nombreDeBase).Options,
            new TenantActualFijo(ModoDeAcceso.Tenant, 1));

    private static async Task<(int IdEmpresaA, int IdPuntoVentaDeA, int IdEmpresaB, int IdPuntoVentaDeB)>
        SembrarDosEmpresasConSuPuntoDeVentaAsync(string nombreDeBase)
    {
        await using var siembra = CrearContexto(nombreDeBase);

        var empresaA = new Empresa { IdTenant = 1, RazonSocial = "A", CreatedAt = Ahora, UpdatedAt = Ahora };
        var empresaB = new Empresa { IdTenant = 1, RazonSocial = "B", CreatedAt = Ahora, UpdatedAt = Ahora };
        siembra.Empresas.AddRange(empresaA, empresaB);
        await siembra.SaveChangesAsync();

        var puntoVentaDeA = new PuntoVenta
        {
            IdTenant = 1, IdEmpresa = empresaA.Id, Nombre = "Local A", CreatedAt = Ahora, UpdatedAt = Ahora
        };
        var puntoVentaDeB = new PuntoVenta
        {
            IdTenant = 1, IdEmpresa = empresaB.Id, Nombre = "Local B", CreatedAt = Ahora, UpdatedAt = Ahora
        };
        siembra.PuntosVenta.AddRange(puntoVentaDeA, puntoVentaDeB);
        await siembra.SaveChangesAsync();

        return (empresaA.Id, puntoVentaDeA.Id, empresaB.Id, puntoVentaDeB.Id);
    }

    [Fact]
    public async Task EstablecerAsyncRechazaUnPuntoDeVentaDeOtraEmpresa()
    {
        var nombreDeBase = nameof(EstablecerAsyncRechazaUnPuntoDeVentaDeOtraEmpresa);
        var (idEmpresaA, _, _, idPuntoVentaDeB) = await SembrarDosEmpresasConSuPuntoDeVentaAsync(nombreDeBase);

        var servicio = new ServicioDeParametros(CrearContexto(nombreDeBase), new RelojFijo(Ahora));

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.EstablecerAsync(
            idEmpresaA, new ParametroAlta("tolerancia_pago", "15", idPuntoVentaDeB)));

        Assert.Equal("punto_venta_no_pertenece_a_la_empresa", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public async Task EstablecerAsyncAceptaUnPuntoDeVentaDeLaMismaEmpresa()
    {
        var nombreDeBase = nameof(EstablecerAsyncAceptaUnPuntoDeVentaDeLaMismaEmpresa);
        var (idEmpresaA, idPuntoVentaDeA, _, _) = await SembrarDosEmpresasConSuPuntoDeVentaAsync(nombreDeBase);

        var servicio = new ServicioDeParametros(CrearContexto(nombreDeBase), new RelojFijo(Ahora));

        var resultado = await servicio.EstablecerAsync(
            idEmpresaA, new ParametroAlta("tolerancia_pago", "15", idPuntoVentaDeA));

        Assert.Equal("15", resultado.Valor);
    }

    [Fact]
    public async Task ResolverAsyncRechazaUnPuntoDeVentaDeOtraEmpresa()
    {
        var nombreDeBase = nameof(ResolverAsyncRechazaUnPuntoDeVentaDeOtraEmpresa);
        var (idEmpresaA, _, _, idPuntoVentaDeB) = await SembrarDosEmpresasConSuPuntoDeVentaAsync(nombreDeBase);

        var servicio = new ServicioDeParametros(CrearContexto(nombreDeBase), new RelojFijo(Ahora));

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.ResolverAsync(
            "tolerancia_pago", idEmpresaA, idPuntoVentaDeB));

        Assert.Equal("punto_venta_no_pertenece_a_la_empresa", error.Codigo);
    }

    /// <summary>Stage-10 (design decisión 12): <c>zona_horaria</c> es el primer parámetro
    /// string-tipado, y su valor tiene que guardarse JSON-quoteado — un identificador sin
    /// comillas no es JSON válido para un <c>string</c>.</summary>
    [Fact]
    public async Task EstablecerAsyncAceptaZonaHorariaQuoteadaYLaDevuelveVerbatim()
    {
        var nombreDeBase = nameof(EstablecerAsyncAceptaZonaHorariaQuoteadaYLaDevuelveVerbatim);
        var (idEmpresaA, _, _, _) = await SembrarDosEmpresasConSuPuntoDeVentaAsync(nombreDeBase);

        var servicio = new ServicioDeParametros(CrearContexto(nombreDeBase), new RelojFijo(Ahora));

        var resultado = await servicio.EstablecerAsync(
            idEmpresaA, new ParametroAlta("zona_horaria", "\"America/Argentina/Cordoba\"", null));

        Assert.Equal("\"America/Argentina/Cordoba\"", resultado.Valor);
    }

    [Fact]
    public async Task EstablecerAsyncRechazaZonaHorariaSinComillas()
    {
        var nombreDeBase = nameof(EstablecerAsyncRechazaZonaHorariaSinComillas);
        var (idEmpresaA, _, _, _) = await SembrarDosEmpresasConSuPuntoDeVentaAsync(nombreDeBase);

        var servicio = new ServicioDeParametros(CrearContexto(nombreDeBase), new RelojFijo(Ahora));

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.EstablecerAsync(
            idEmpresaA, new ParametroAlta("zona_horaria", "America/Argentina/Cordoba", null)));

        Assert.Equal("parametro_tipo_invalido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    /// <summary>Judgment-day trap que este endurecimiento cierra: <c>JsonSerializer.Deserialize
    /// ("null", typeof(string))</c> devuelve <c>null</c> sin tirar excepción, así que
    /// <c>ValidarTipo</c> lo aceptaba antes de este endurecimiento.</summary>
    [Fact]
    public async Task EstablecerAsyncRechazaUnaDeserializacionNull()
    {
        var nombreDeBase = nameof(EstablecerAsyncRechazaUnaDeserializacionNull);
        var (idEmpresaA, _, _, _) = await SembrarDosEmpresasConSuPuntoDeVentaAsync(nombreDeBase);

        var servicio = new ServicioDeParametros(CrearContexto(nombreDeBase), new RelojFijo(Ahora));

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.EstablecerAsync(
            idEmpresaA, new ParametroAlta("zona_horaria", "null", null)));

        Assert.Equal("parametro_tipo_invalido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public async Task EstablecerAsyncRechazaUnaZonaHorariaQueNoEsUnIdIana()
    {
        var nombreDeBase = nameof(EstablecerAsyncRechazaUnaZonaHorariaQueNoEsUnIdIana);
        var (idEmpresaA, _, _, _) = await SembrarDosEmpresasConSuPuntoDeVentaAsync(nombreDeBase);

        var servicio = new ServicioDeParametros(CrearContexto(nombreDeBase), new RelojFijo(Ahora));

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.EstablecerAsync(
            idEmpresaA, new ParametroAlta("zona_horaria", "\"No/Existe\"", null)));

        Assert.Equal("parametro_zona_horaria_invalida", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public async Task EstablecerAsyncRechazaUnIdDeZonaNativoDeWindows()
    {
        var nombreDeBase = nameof(EstablecerAsyncRechazaUnIdDeZonaNativoDeWindows);
        var (idEmpresaA, _, _, _) = await SembrarDosEmpresasConSuPuntoDeVentaAsync(nombreDeBase);

        var servicio = new ServicioDeParametros(CrearContexto(nombreDeBase), new RelojFijo(Ahora));

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.EstablecerAsync(
            idEmpresaA, new ParametroAlta("zona_horaria", "\"Argentina Standard Time\"", null)));

        Assert.Equal("parametro_zona_horaria_invalida", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public async Task EstablecerAsyncAceptaUnaZonaIanaFueraDeLaListaCuradaDelFront()
    {
        var nombreDeBase = nameof(EstablecerAsyncAceptaUnaZonaIanaFueraDeLaListaCuradaDelFront);
        var (idEmpresaA, _, _, _) = await SembrarDosEmpresasConSuPuntoDeVentaAsync(nombreDeBase);

        var servicio = new ServicioDeParametros(CrearContexto(nombreDeBase), new RelojFijo(Ahora));

        var parametro = await servicio.EstablecerAsync(
            idEmpresaA, new ParametroAlta("zona_horaria", "\"America/Santiago\"", null));

        Assert.Equal("\"America/Santiago\"", parametro.Valor);
    }

    [Fact]
    public async Task EstablecerAsyncRechazaUnaZonaHorariaVacia()
    {
        var nombreDeBase = nameof(EstablecerAsyncRechazaUnaZonaHorariaVacia);
        var (idEmpresaA, _, _, _) = await SembrarDosEmpresasConSuPuntoDeVentaAsync(nombreDeBase);

        var servicio = new ServicioDeParametros(CrearContexto(nombreDeBase), new RelojFijo(Ahora));

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.EstablecerAsync(
            idEmpresaA, new ParametroAlta("zona_horaria", "\"\"", null)));

        Assert.Equal("parametro_zona_horaria_invalida", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }
}
