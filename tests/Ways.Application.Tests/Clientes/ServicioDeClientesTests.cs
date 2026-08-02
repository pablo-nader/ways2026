using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Clientes;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Common;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.Application.Tests.Clientes;

/// <summary>
/// <see cref="ServicioDeClientes"/> sobre el proveedor InMemory: validación de campos
/// requeridos, referencias inválidas (400 <c>referencia_invalida</c>) y el guard del
/// Consumidor Final en edición/baja.
///
/// <see cref="ServicioDeClientes.CrearAsync"/> completo (alta real con contador atómico +
/// defaults de crédito) NO se cubre acá a propósito: envuelve el INSERT en
/// <c>Database.BeginTransactionAsync</c> + <see cref="AsignadorDeNumeroCliente"/> (ADO.NET
/// crudo sobre <c>Database.GetDbConnection()</c>) — ninguno de los dos lo soporta el
/// proveedor InMemory, mismo motivo por el que
/// <see cref="Organizacion.ServicioDeAprovisionamiento"/> (el otro consumidor de
/// <see cref="AsignadorDeNumeroCliente"/>) tampoco tiene batería de Application.Tests, solo
/// integración. Los chequeos de validación de este archivo corren ANTES de abrir esa
/// transacción, así que sí son alcanzables acá; el alta de punta a punta (incl. defaults de
/// crédito 0/false/0) se prueba contra Postgres real en <c>ClientesEndpointsTests</c>
/// (Ways.IntegrationTests, task 2.5).
/// </summary>
public class ServicioDeClientesTests
{
    private static readonly DateTimeOffset Ahora = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    private sealed class RelojFijo(DateTimeOffset ahora) : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = ahora;
    }

    private sealed class ContextoFijo(int? idTenant) : IContextoDeUsuario
    {
        public bool EstaAutenticado => true;
        public int UsuarioId => 999;
        public string NombreUsuario => "actor-de-prueba";
        public RolConocido Rol => RolConocido.Admin;
        public int? IdTenant { get; } = idTenant;
    }

    private static WaysDbContext CrearContexto(string nombreDeBase, ITenantActual tenantActual) =>
        new(new DbContextOptionsBuilder<WaysDbContext>().UseInMemoryDatabase(nombreDeBase).Options, tenantActual);

    private static ServicioDeClientes CrearServicio(string nombreDeBase, int idTenant) =>
        new(
            CrearContexto(nombreDeBase, new TenantActualFijo(ModoDeAcceso.Tenant, idTenant)),
            new RelojFijo(Ahora),
            new ContextoFijo(idTenant));

    private static async Task<(int IdCondicionFiscal, int IdListaPrecio)> SembrarCatalogosAsync(
        string nombreDeBase, int idTenant)
    {
        await using var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma);

        var condicionFiscal = new CondicionFiscal
        {
            Codigo = "CF", Nombre = "Consumidor Final", CreatedAt = Ahora, UpdatedAt = Ahora
        };
        siembra.CondicionesFiscales.Add(condicionFiscal);

        var lista = new ListaPrecio
        {
            IdTenant = idTenant,
            Nombre = "General",
            EsDefault = true,
            Modo = ModoLista.Fija,
            CreatedAt = Ahora,
            UpdatedAt = Ahora
        };
        siembra.ListasPrecio.Add(lista);

        await siembra.SaveChangesAsync();
        return (condicionFiscal.Id, lista.Id);
    }

    private static async Task<Cliente> SembrarClienteAsync(
        string nombreDeBase, int idTenant, int numero, int idCondicionFiscal, int idListaPrecio)
    {
        await using var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma);

        var cliente = new Cliente
        {
            IdTenant = idTenant,
            Numero = numero,
            Nombre = numero == ReglaDeClientes.NumeroConsumidorFinal ? "Consumidor Final" : $"Cliente {numero}",
            IdCondicionFiscal = idCondicionFiscal,
            IdListaPrecio = idListaPrecio,
            CreatedAt = Ahora,
            UpdatedAt = Ahora
        };

        siembra.Clientes.Add(cliente);
        await siembra.SaveChangesAsync();
        return cliente;
    }

    private static async Task<int> SembrarEmpresaAsync(string nombreDeBase, int idTenant)
    {
        await using var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma);

        var empresa = new Empresa
        {
            IdTenant = idTenant, RazonSocial = "Empresa de prueba", CreatedAt = Ahora, UpdatedAt = Ahora
        };
        siembra.Empresas.Add(empresa);

        await siembra.SaveChangesAsync();
        return empresa.Id;
    }

    private static AltaCliente AltaValida(int idCondicionFiscal, int idListaPrecio, string nombre = "Juan Pérez") =>
        new(
            nombre, null, null, null, null, idCondicionFiscal, null, null, null, null, null, null,
            idListaPrecio);

    private static EdicionCliente EdicionValida(int idCondicionFiscal, int idListaPrecio, string nombre = "Editado") =>
        new(
            nombre, null, null, null, null, idCondicionFiscal, null, null, null, null, null, null,
            idListaPrecio, 0, false, null, true);

    [Fact]
    public async Task CrearSinIdCondicionFiscalEsRechazado()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var (_, idListaPrecio) = await SembrarCatalogosAsync(nombreDeBase, idTenant: 1);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var datos = AltaValida(idCondicionFiscal: 0, idListaPrecio);

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.CrearAsync(datos));

        Assert.Equal("id_condicion_fiscal_requerido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public async Task CrearSinIdListaPrecioEsRechazado()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var (idCondicionFiscal, _) = await SembrarCatalogosAsync(nombreDeBase, idTenant: 1);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var datos = AltaValida(idCondicionFiscal, idListaPrecio: 0);

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.CrearAsync(datos));

        Assert.Equal("id_lista_precio_requerido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public async Task CrearSinNombreEsRechazado()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var (idCondicionFiscal, idListaPrecio) = await SembrarCatalogosAsync(nombreDeBase, idTenant: 1);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var datos = AltaValida(idCondicionFiscal, idListaPrecio, nombre: "   ");

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.CrearAsync(datos));

        Assert.Equal("nombre_requerido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    /// <summary>Judgment-day ronda 1 (item 6): el código de error de un campo opcional
    /// demasiado largo identifica el campo específico (<c>email_muy_largo</c>), no un
    /// <c>campo_muy_largo</c> genérico compartido entre los ocho campos opcionales.</summary>
    [Fact]
    public async Task CrearConEmailDemasiadoLargoEsRechazadoConElCodigoDelCampo()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var (idCondicionFiscal, idListaPrecio) = await SembrarCatalogosAsync(nombreDeBase, idTenant: 1);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var emailDemasiadoLargo = new string('a', 256) + "@ways.test";
        var datos = AltaValida(idCondicionFiscal, idListaPrecio) with { Email = emailDemasiadoLargo };

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.CrearAsync(datos));

        Assert.Equal("email_muy_largo", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    /// <summary>Judgment-day ronda 1 (item 2): <c>LimiteCredito</c> negativo se rechaza a
    /// nivel de servicio (sin CHECK de esquema, ver el comentario de
    /// <see cref="ServicioDeClientes"/> junto a <c>ExigirLimiteCreditoValido</c>).</summary>
    [Fact]
    public async Task CrearConLimiteCreditoNegativoEsRechazado()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var (idCondicionFiscal, idListaPrecio) = await SembrarCatalogosAsync(nombreDeBase, idTenant: 1);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var datos = AltaValida(idCondicionFiscal, idListaPrecio) with { LimiteCredito = -1 };

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.CrearAsync(datos));

        Assert.Equal("limite_credito_invalido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    /// <summary>Spec: "Invalid FK reference maps to 400" — el pre-chequeo de
    /// <see cref="ServicioDeClientes"/> adelanta el mismo código/estado que el backstop de
    /// <c>fk_clientes_condicion_fiscal</c> (23503), sin esperar la carrera con Postgres.</summary>
    [Fact]
    public async Task CrearConIdCondicionFiscalInexistenteEsRechazado()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var (_, idListaPrecio) = await SembrarCatalogosAsync(nombreDeBase, idTenant: 1);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var datos = AltaValida(idCondicionFiscal: 999_999, idListaPrecio);

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.CrearAsync(datos));

        Assert.Equal("referencia_invalida", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    /// <summary>Mismo criterio que arriba, para <c>fk_clientes_lista_precio</c> (compuesta,
    /// judgment-day ronda 1) — acá además cubre el caso "existe pero es de otro tenant": el
    /// filtro de EF ya la deja afuera de <c>db.ListasPrecio</c>, así que da el mismo 400.</summary>
    [Fact]
    public async Task CrearConIdListaPrecioDeOtroTenantEsRechazado()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var (idCondicionFiscal, _) = await SembrarCatalogosAsync(nombreDeBase, idTenant: 1);
        var (_, idListaPrecioDeOtroTenant) = await SembrarCatalogosAsync(nombreDeBase, idTenant: 2);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var datos = AltaValida(idCondicionFiscal, idListaPrecioDeOtroTenant);

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.CrearAsync(datos));

        Assert.Equal("referencia_invalida", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    /// <summary>Judgment-day ronda 1 (item 8): mismo criterio que
    /// <see cref="CrearConIdCondicionFiscalInexistenteEsRechazado"/>, para
    /// <c>fk_clientes_empresa</c> — pre-chequeo de servicio, sin esperar el backstop 23503.</summary>
    [Fact]
    public async Task CrearConIdEmpresaInexistenteEsRechazado()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var (idCondicionFiscal, idListaPrecio) = await SembrarCatalogosAsync(nombreDeBase, idTenant: 1);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var datos = AltaValida(idCondicionFiscal, idListaPrecio) with { IdEmpresa = 999_999 };

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.CrearAsync(datos));

        Assert.Equal("referencia_invalida", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    /// <summary>Slice 2 INFO carried into Slice 3 (state.yaml, closed here for symmetry): mismo
    /// criterio que <see cref="CrearConIdEmpresaInexistenteEsRechazado"/>, pero con una empresa
    /// que EXISTE de verdad y pertenece a OTRO tenant — el filtro de EF ya la deja afuera de
    /// <c>db.Empresas</c> para el tenant actual, así que da el mismo 400 (correcto por
    /// construcción, misma paridad que <see cref="CrearConIdListaPrecioDeOtroTenantEsRechazado"/>).</summary>
    [Fact]
    public async Task CrearConIdEmpresaDeOtroTenantEsRechazado()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var (idCondicionFiscal, idListaPrecio) = await SembrarCatalogosAsync(nombreDeBase, idTenant: 1);
        var idEmpresaDeOtroTenant = await SembrarEmpresaAsync(nombreDeBase, idTenant: 2);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var datos = AltaValida(idCondicionFiscal, idListaPrecio) with { IdEmpresa = idEmpresaDeOtroTenant };

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.CrearAsync(datos));

        Assert.Equal("referencia_invalida", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public async Task EditarElConsumidorFinalEsRechazado()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var (idCondicionFiscal, idListaPrecio) = await SembrarCatalogosAsync(nombreDeBase, idTenant: 1);
        var cf = await SembrarClienteAsync(
            nombreDeBase, idTenant: 1, ReglaDeClientes.NumeroConsumidorFinal, idCondicionFiscal, idListaPrecio);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var error = await Assert.ThrowsAsync<ErrorDominio>(
            () => servicio.ActualizarAsync(cf.Id, EdicionValida(idCondicionFiscal, idListaPrecio)));

        Assert.Equal("consumidor_final_protegido", error.Codigo);
        Assert.Equal(409, error.EstadoHttp);
    }

    [Fact]
    public async Task EliminarElConsumidorFinalEsRechazado()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var (idCondicionFiscal, idListaPrecio) = await SembrarCatalogosAsync(nombreDeBase, idTenant: 1);
        var cf = await SembrarClienteAsync(
            nombreDeBase, idTenant: 1, ReglaDeClientes.NumeroConsumidorFinal, idCondicionFiscal, idListaPrecio);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.EliminarAsync(cf.Id));

        Assert.Equal("consumidor_final_protegido", error.Codigo);
        Assert.Equal(409, error.EstadoHttp);
    }

    [Fact]
    public async Task EditarUnClienteNoConsumidorFinalFunciona()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var (idCondicionFiscal, idListaPrecio) = await SembrarCatalogosAsync(nombreDeBase, idTenant: 1);
        var cliente = await SembrarClienteAsync(nombreDeBase, idTenant: 1, numero: 2, idCondicionFiscal, idListaPrecio);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var editado = await servicio.ActualizarAsync(
            cliente.Id, EdicionValida(idCondicionFiscal, idListaPrecio, nombre: "Cliente Editado"));

        Assert.Equal("Cliente Editado", editado.Nombre);
    }

    [Fact]
    public async Task EliminarUnClienteNoConsumidorFinalFunciona()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var (idCondicionFiscal, idListaPrecio) = await SembrarCatalogosAsync(nombreDeBase, idTenant: 1);
        var cliente = await SembrarClienteAsync(nombreDeBase, idTenant: 1, numero: 2, idCondicionFiscal, idListaPrecio);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        await servicio.EliminarAsync(cliente.Id);

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.ObtenerAsync(cliente.Id));
        Assert.Equal("no_encontrado", error.Codigo);
    }

    /// <summary>ADR-8: mismo 404 para "no existe" y "es de otro tenant" — el filtro de EF ya
    /// deja invisible la fila de otro tenant antes de que el servicio decida nada.</summary>
    [Fact]
    public async Task ObtenerUnClienteDeOtroTenantDevuelve404()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var (idCondicionFiscal, idListaPrecio) = await SembrarCatalogosAsync(nombreDeBase, idTenant: 2);
        var ajeno = await SembrarClienteAsync(nombreDeBase, idTenant: 2, numero: 2, idCondicionFiscal, idListaPrecio);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.ObtenerAsync(ajeno.Id));

        Assert.Equal("no_encontrado", error.Codigo);
        Assert.Equal(404, error.EstadoHttp);
    }
}
