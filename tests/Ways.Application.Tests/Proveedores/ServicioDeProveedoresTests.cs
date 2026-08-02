using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Proveedores;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Proveedores;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.Application.Tests.Proveedores;

/// <summary>
/// <see cref="ServicioDeProveedores"/> sobre el proveedor InMemory. A diferencia de
/// <c>ServicioDeClientesTests</c>, acá SÍ se cubre <see cref="ServicioDeProveedores.CrearAsync"/>
/// completo: no hay contador atómico ni <c>Database.BeginTransactionAsync</c> de por medio
/// (design.md: proveedores no reusa <c>AsignadorDeNumeroCliente</c>), así que el INSERT +
/// <c>SaveChangesAsync</c> corre sin problema contra InMemory.
/// </summary>
public class ServicioDeProveedoresTests
{
    private static readonly DateTimeOffset Ahora = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    private sealed class RelojFijo(DateTimeOffset ahora) : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = ahora;
    }

    private static WaysDbContext CrearContexto(string nombreDeBase, ITenantActual tenantActual) =>
        new(new DbContextOptionsBuilder<WaysDbContext>().UseInMemoryDatabase(nombreDeBase).Options, tenantActual);

    private static ServicioDeProveedores CrearServicio(string nombreDeBase, int idTenant) =>
        new(CrearContexto(nombreDeBase, new TenantActualFijo(ModoDeAcceso.Tenant, idTenant)), new RelojFijo(Ahora));

    private static async Task<int> SembrarCondicionFiscalAsync(string nombreDeBase)
    {
        await using var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma);

        var condicionFiscal = new CondicionFiscal
        {
            Codigo = "CF", Nombre = "Consumidor Final", CreatedAt = Ahora, UpdatedAt = Ahora
        };
        siembra.CondicionesFiscales.Add(condicionFiscal);

        await siembra.SaveChangesAsync();
        return condicionFiscal.Id;
    }

    private static AltaProveedor AltaValida(int idCondicionFiscal, string razonSocial = "Distribuidora SA", string? cuit = null) =>
        new(
            RazonSocial: razonSocial,
            NombreFantasia: null,
            Cuit: cuit,
            IdCondicionFiscal: idCondicionFiscal,
            Domicilio: null,
            Telefono: null,
            Email: null,
            Vendedor: null,
            CelularVendedor: null,
            Supervisor: null,
            CelularSupervisor: null,
            Margen: null,
            Observaciones: null);

    private static EdicionProveedor EdicionValida(int idCondicionFiscal, string razonSocial = "Editado SA", string? cuit = null) =>
        new(
            RazonSocial: razonSocial,
            NombreFantasia: null,
            Cuit: cuit,
            IdCondicionFiscal: idCondicionFiscal,
            Domicilio: null,
            Telefono: null,
            Email: null,
            Vendedor: null,
            CelularVendedor: null,
            Supervisor: null,
            CelularSupervisor: null,
            Margen: null,
            Observaciones: null,
            IdEmpresa: null,
            Activo: true);

    [Fact]
    public async Task CrearSinCuitFunciona()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idCondicionFiscal = await SembrarCondicionFiscalAsync(nombreDeBase);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var creado = await servicio.CrearAsync(AltaValida(idCondicionFiscal));

        Assert.Null(creado.Cuit);
        Assert.Equal("Distribuidora SA", creado.RazonSocial);
    }

    [Fact]
    public async Task CrearSinIdCondicionFiscalEsRechazado()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var error = await Assert.ThrowsAsync<ErrorDominio>(
            () => servicio.CrearAsync(AltaValida(idCondicionFiscal: 0)));

        Assert.Equal("id_condicion_fiscal_requerido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public async Task CrearSinRazonSocialEsRechazado()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idCondicionFiscal = await SembrarCondicionFiscalAsync(nombreDeBase);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var error = await Assert.ThrowsAsync<ErrorDominio>(
            () => servicio.CrearAsync(AltaValida(idCondicionFiscal, razonSocial: "   ")));

        Assert.Equal("razon_social_requerido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    /// <summary>Spec: Duplicate cuit within the same tenant is rejected (pre-chequeo de
    /// servicio; el backstop real de 23505 se prueba contra Postgres real en
    /// ProveedoresEndpointsTests).</summary>
    [Fact]
    public async Task CrearConCuitDuplicadoEnElMismoTenantEsRechazado()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idCondicionFiscal = await SembrarCondicionFiscalAsync(nombreDeBase);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        await servicio.CrearAsync(AltaValida(idCondicionFiscal, cuit: "30712345678"));
        var error = await Assert.ThrowsAsync<ErrorDominio>(
            () => servicio.CrearAsync(AltaValida(idCondicionFiscal, razonSocial: "Otra SA", cuit: "30712345678")));

        Assert.Equal("cuit_duplicado", error.Codigo);
        Assert.Equal(409, error.EstadoHttp);
    }

    /// <summary>Spec: Same cuit across different tenants is allowed.</summary>
    [Fact]
    public async Task CrearConElMismoCuitEnDosTenantsEsPermitido()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idCondicionFiscal = await SembrarCondicionFiscalAsync(nombreDeBase);
        var servicioTenant1 = CrearServicio(nombreDeBase, idTenant: 1);
        var servicioTenant2 = CrearServicio(nombreDeBase, idTenant: 2);

        var creadoEnTenant1 = await servicioTenant1.CrearAsync(AltaValida(idCondicionFiscal, cuit: "30712345678"));
        var creadoEnTenant2 = await servicioTenant2.CrearAsync(AltaValida(idCondicionFiscal, cuit: "30712345678"));

        Assert.Equal("30712345678", creadoEnTenant1.Cuit);
        Assert.Equal("30712345678", creadoEnTenant2.Cuit);
    }

    /// <summary>Spec: NULL cuit never collides.</summary>
    [Fact]
    public async Task CrearDosProveedoresSinCuitEnElMismoTenantEsPermitido()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idCondicionFiscal = await SembrarCondicionFiscalAsync(nombreDeBase);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var primero = await servicio.CrearAsync(AltaValida(idCondicionFiscal, razonSocial: "Primero SA"));
        var segundo = await servicio.CrearAsync(AltaValida(idCondicionFiscal, razonSocial: "Segundo SA"));

        Assert.Null(primero.Cuit);
        Assert.Null(segundo.Cuit);
    }

    /// <summary>Spec: Soft-deleted cuit is reusable.</summary>
    [Fact]
    public async Task CrearConElCuitDeUnProveedorDadoDeBajaEsPermitido()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idCondicionFiscal = await SembrarCondicionFiscalAsync(nombreDeBase);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var original = await servicio.CrearAsync(AltaValida(idCondicionFiscal, cuit: "30712345678"));
        await servicio.EliminarAsync(original.Id);

        var nuevo = await servicio.CrearAsync(
            AltaValida(idCondicionFiscal, razonSocial: "Reemplazo SA", cuit: "30712345678"));

        Assert.Equal("30712345678", nuevo.Cuit);
    }

    [Fact]
    public async Task CrearConCuitDemasiadoLargoEsRechazado()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idCondicionFiscal = await SembrarCondicionFiscalAsync(nombreDeBase);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var error = await Assert.ThrowsAsync<ErrorDominio>(
            () => servicio.CrearAsync(AltaValida(idCondicionFiscal, cuit: new string('1', 14))));

        Assert.Equal("cuit_muy_largo", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public async Task CrearConEmailDemasiadoLargoEsRechazadoConElCodigoDelCampo()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idCondicionFiscal = await SembrarCondicionFiscalAsync(nombreDeBase);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var emailDemasiadoLargo = new string('a', 256) + "@ways.test";
        var datos = AltaValida(idCondicionFiscal) with { Email = emailDemasiadoLargo };

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.CrearAsync(datos));

        Assert.Equal("email_muy_largo", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public async Task CrearConMargenNegativoEsRechazado()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idCondicionFiscal = await SembrarCondicionFiscalAsync(nombreDeBase);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var datos = AltaValida(idCondicionFiscal) with { Margen = -1 };

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.CrearAsync(datos));

        Assert.Equal("margen_invalido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    /// <summary>Spec: Invalid condicion fiscal reference maps to 400 (pre-chequeo de
    /// servicio, adelanta el mismo código que el backstop 23503).</summary>
    [Fact]
    public async Task CrearConIdCondicionFiscalInexistenteEsRechazado()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var error = await Assert.ThrowsAsync<ErrorDominio>(
            () => servicio.CrearAsync(AltaValida(idCondicionFiscal: 999_999)));

        Assert.Equal("referencia_invalida", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public async Task CrearConIdEmpresaInexistenteEsRechazado()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idCondicionFiscal = await SembrarCondicionFiscalAsync(nombreDeBase);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var datos = AltaValida(idCondicionFiscal) with { IdEmpresa = 999_999 };

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.CrearAsync(datos));

        Assert.Equal("referencia_invalida", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public async Task EditarUnProveedorFunciona()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idCondicionFiscal = await SembrarCondicionFiscalAsync(nombreDeBase);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);
        var creado = await servicio.CrearAsync(AltaValida(idCondicionFiscal));

        var editado = await servicio.ActualizarAsync(
            creado.Id, EdicionValida(idCondicionFiscal, razonSocial: "Nombre Editado"));

        Assert.Equal("Nombre Editado", editado.RazonSocial);
    }

    /// <summary>Editar sin cambiar el cuit propio no debe autocolisionar contra sí
    /// mismo (<c>excluirId</c> en <c>ExigirCuitDisponibleAsync</c>).</summary>
    [Fact]
    public async Task EditarUnProveedorConservandoSuPropioCuitFunciona()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idCondicionFiscal = await SembrarCondicionFiscalAsync(nombreDeBase);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);
        var creado = await servicio.CrearAsync(AltaValida(idCondicionFiscal, cuit: "30712345678"));

        var editado = await servicio.ActualizarAsync(
            creado.Id, EdicionValida(idCondicionFiscal, razonSocial: "Sigue igual", cuit: "30712345678"));

        Assert.Equal("30712345678", editado.Cuit);
    }

    [Fact]
    public async Task EditarConCuitYaUsadoPorOtroProveedorEsRechazado()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idCondicionFiscal = await SembrarCondicionFiscalAsync(nombreDeBase);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);
        await servicio.CrearAsync(AltaValida(idCondicionFiscal, cuit: "30712345678"));
        var segundo = await servicio.CrearAsync(AltaValida(idCondicionFiscal, razonSocial: "Segundo SA"));

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.ActualizarAsync(
            segundo.Id, EdicionValida(idCondicionFiscal, cuit: "30712345678")));

        Assert.Equal("cuit_duplicado", error.Codigo);
        Assert.Equal(409, error.EstadoHttp);
    }

    [Fact]
    public async Task EliminarUnProveedorFunciona()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idCondicionFiscal = await SembrarCondicionFiscalAsync(nombreDeBase);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);
        var creado = await servicio.CrearAsync(AltaValida(idCondicionFiscal));

        await servicio.EliminarAsync(creado.Id);

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.ObtenerAsync(creado.Id));
        Assert.Equal("no_encontrado", error.Codigo);
    }

    /// <summary>ADR-8: mismo 404 para "no existe" y "es de otro tenant" — el filtro de EF ya
    /// deja invisible la fila de otro tenant antes de que el servicio decida nada.</summary>
    [Fact]
    public async Task ObtenerUnProveedorDeOtroTenantDevuelve404()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idCondicionFiscal = await SembrarCondicionFiscalAsync(nombreDeBase);
        var servicioTenant2 = CrearServicio(nombreDeBase, idTenant: 2);
        var ajeno = await servicioTenant2.CrearAsync(AltaValida(idCondicionFiscal));
        var servicioTenant1 = CrearServicio(nombreDeBase, idTenant: 1);

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicioTenant1.ObtenerAsync(ajeno.Id));

        Assert.Equal("no_encontrado", error.Codigo);
        Assert.Equal(404, error.EstadoHttp);
    }
}
