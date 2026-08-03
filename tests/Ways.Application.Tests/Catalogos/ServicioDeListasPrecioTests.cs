using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Catalogos;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.Application.Tests.Catalogos;

/// <summary>
/// <see cref="ServicioDeListasPrecio"/> sobre el proveedor InMemory (stage-3-articulos-y-precios,
/// Slice 4, task 4.4).
///
/// El camino de <c>EsDefault: true</c> (intercambio, <c>Database.BeginTransactionAsync</c>) NO
/// se cubre acá a propósito — mismo "transaction-blocked-provider caveat" que
/// <c>ServicioDeArticulosTests</c>/<c>ServicioDeClientesTests</c>; se prueba de punta a punta
/// contra Postgres real en <c>ListasPrecioEndpointsTests</c> (Ways.IntegrationTests). Todas las
/// validaciones de este archivo corren ANTES de esa transacción (o nunca la abren, porque
/// rechazan antes de llegar a la rama de intercambio), así que sí son alcanzables acá.
/// </summary>
public class ServicioDeListasPrecioTests
{
    private static readonly DateTimeOffset Ahora = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);

    private sealed class RelojFijo(DateTimeOffset ahora) : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = ahora;
    }

    private static WaysDbContext CrearContexto(string nombreDeBase, ITenantActual tenantActual) =>
        new(new DbContextOptionsBuilder<WaysDbContext>().UseInMemoryDatabase(nombreDeBase).Options, tenantActual);

    private static ServicioDeListasPrecio CrearServicio(string nombreDeBase, int idTenant) =>
        new(CrearContexto(nombreDeBase, new TenantActualFijo(ModoDeAcceso.Tenant, idTenant)), new RelojFijo(Ahora));

    private static async Task<int> SembrarListaFijaAsync(
        string nombreDeBase, int idTenant, string nombre = "General", bool esDefault = false, bool activo = true)
    {
        await using var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma);

        var lista = new ListaPrecio
        {
            IdTenant = idTenant,
            Nombre = nombre,
            EsDefault = esDefault,
            Modo = ModoLista.Fija,
            Activo = activo,
            CreatedAt = Ahora,
            UpdatedAt = Ahora
        };
        siembra.ListasPrecio.Add(lista);
        await siembra.SaveChangesAsync();
        return lista.Id;
    }

    private static async Task<int> SembrarListaDerivadaAsync(
        string nombreDeBase, int idTenant, int idListaBase, decimal porcentaje = -10m)
    {
        await using var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma);

        var lista = new ListaPrecio
        {
            IdTenant = idTenant,
            Nombre = "Derivada",
            EsDefault = false,
            Modo = ModoLista.Derivada,
            IdListaBase = idListaBase,
            Porcentaje = porcentaje,
            Activo = true,
            CreatedAt = Ahora,
            UpdatedAt = Ahora
        };
        siembra.ListasPrecio.Add(lista);
        await siembra.SaveChangesAsync();
        return lista.Id;
    }

    private static async Task SembrarPrecioAsync(string nombreDeBase, int idTenant, int idListaPrecio)
    {
        await using var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma);

        var area = new Area { IdTenant = idTenant, Nombre = "Área", Orden = 1, CreatedAt = Ahora, UpdatedAt = Ahora };
        var alicuota = new AlicuotaIva { Nombre = "21%", Porcentaje = 21m, CreatedAt = Ahora, UpdatedAt = Ahora };
        siembra.Areas.Add(area);
        siembra.AlicuotasIva.Add(alicuota);
        await siembra.SaveChangesAsync();

        var articulo = new Articulo
        {
            IdTenant = idTenant,
            CodigoInterno = "1",
            Nombre = "Artículo de prueba",
            IdArea = area.Id,
            IdAlicuotaIva = alicuota.Id,
            UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true,
            DisponibleParaTodas = true,
            CreatedAt = Ahora,
            UpdatedAt = Ahora
        };
        siembra.Articulos.Add(articulo);
        await siembra.SaveChangesAsync();

        siembra.Precios.Add(new Precio
        {
            IdTenant = idTenant,
            IdArticulo = articulo.Id,
            IdListaPrecio = idListaPrecio,
            Monto = 100m,
            VigenteDesde = Ahora,
            VigenteHasta = null,
            CreatedAt = Ahora,
            UpdatedAt = Ahora
        });
        await siembra.SaveChangesAsync();
    }

    private static ListaPrecioAlta AltaFijaValida(string nombre = "Mayorista") =>
        new(nombre, IdEmpresa: null, EsDefault: false, Modo: ModoLista.Fija, IdListaBase: null, Porcentaje: null);

    private static ListaPrecioAlta AltaDerivadaValida(int idListaBase, decimal porcentaje = -10m, string nombre = "Derivada") =>
        new(nombre, IdEmpresa: null, EsDefault: false, Modo: ModoLista.Derivada, IdListaBase: idListaBase, Porcentaje: porcentaje);

    // ---- task 4.4: derivada requiere base + porcentaje --------------------------------------

    [Fact]
    public async Task CrearDerivadaSinIdListaBaseEsRechazada()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var datos = AltaDerivadaValida(idListaBase: 1) with { IdListaBase = null };

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.CrearAsync(datos));

        Assert.Equal("lista_derivada_requiere_base", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public async Task CrearDerivadaSinPorcentajeEsRechazada()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idBase = await SembrarListaFijaAsync(nombreDeBase, idTenant: 1);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var datos = AltaDerivadaValida(idBase) with { Porcentaje = null };

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.CrearAsync(datos));

        Assert.Equal("lista_derivada_requiere_base", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public async Task CrearFijaConIdListaBaseOPorcentajeEsRechazada()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idBase = await SembrarListaFijaAsync(nombreDeBase, idTenant: 1);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var datos = AltaFijaValida() with { IdListaBase = idBase };

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.CrearAsync(datos));

        Assert.Equal("lista_fija_no_admite_base", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    // ---- orchestrator decision 2: profundidad 1 ----------------------------------------------

    [Fact]
    public async Task CrearDerivadaConBaseQueEsASuVezDerivadaEsRechazada()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idFija = await SembrarListaFijaAsync(nombreDeBase, idTenant: 1);
        var idDerivadaExistente = await SembrarListaDerivadaAsync(nombreDeBase, idTenant: 1, idFija);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var datos = AltaDerivadaValida(idDerivadaExistente);

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.CrearAsync(datos));

        Assert.Equal("lista_base_invalida", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public async Task CrearDerivadaConIdListaBaseInexistenteEsRechazada()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var datos = AltaDerivadaValida(idListaBase: 999_999);

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.CrearAsync(datos));

        Assert.Equal("referencia_invalida", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public async Task CrearDerivadaConIdListaBaseDeOtroTenantEsRechazada()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idBaseDeOtroTenant = await SembrarListaFijaAsync(nombreDeBase, idTenant: 2);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var datos = AltaDerivadaValida(idBaseDeOtroTenant);

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.CrearAsync(datos));

        Assert.Equal("referencia_invalida", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    // ---- state.yaml (obligación heredada de la Slice 3): porcentaje > -100 ------------------

    [Theory]
    [InlineData(-100)]
    [InlineData(-150)]
    public async Task CrearDerivadaConPorcentajeMenorOIgualAMenos100EsRechazada(decimal porcentaje)
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idBase = await SembrarListaFijaAsync(nombreDeBase, idTenant: 1);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var datos = AltaDerivadaValida(idBase, porcentaje);

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.CrearAsync(datos));

        Assert.Equal("porcentaje_invalido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(1500)]
    public async Task CrearDerivadaConPorcentajeMayorOIgualA1000EsRechazada(decimal porcentaje)
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idBase = await SembrarListaFijaAsync(nombreDeBase, idTenant: 1);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var datos = AltaDerivadaValida(idBase, porcentaje);

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.CrearAsync(datos));

        Assert.Equal("porcentaje_invalido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public async Task CrearDerivadaConPorcentajeValidoEsAceptada()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idBase = await SembrarListaFijaAsync(nombreDeBase, idTenant: 1);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var datos = AltaDerivadaValida(idBase, porcentaje: -99.99m);
        var creada = await servicio.CrearAsync(datos);

        Assert.Equal(ModoLista.Derivada, creada.Modo);
        Assert.Equal(idBase, creada.IdListaBase);
    }

    // ---- spec: Blocked Mode Switch Once History Exists ---------------------------------------

    [Fact]
    public async Task ActualizarCambioDeModoConHistorialDePreciosEsRechazado()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idLista = await SembrarListaFijaAsync(nombreDeBase, idTenant: 1);
        await SembrarPrecioAsync(nombreDeBase, idTenant: 1, idLista);
        var idBase = await SembrarListaFijaAsync(nombreDeBase, idTenant: 1, nombre: "Otra base");
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var datos = AltaDerivadaValida(idBase);

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.ActualizarAsync(idLista, datos));

        Assert.Equal("lista_modo_bloqueado_por_historial", error.Codigo);
        Assert.Equal(409, error.EstadoHttp);
    }

    [Fact]
    public async Task ActualizarCambioDeModoSinHistorialEsPermitido()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idLista = await SembrarListaFijaAsync(nombreDeBase, idTenant: 1);
        var idBase = await SembrarListaFijaAsync(nombreDeBase, idTenant: 1, nombre: "Otra base");
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var datos = AltaDerivadaValida(idBase);
        var actualizada = await servicio.ActualizarAsync(idLista, datos);

        Assert.Equal(ModoLista.Derivada, actualizada.Modo);
    }

    // ---- spec: Blocked Deactivation While Referenced As Base ---------------------------------

    [Fact]
    public async Task ActualizarDesactivacionConDependienteActivoEsRechazada()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idBase = await SembrarListaFijaAsync(nombreDeBase, idTenant: 1);
        await SembrarListaDerivadaAsync(nombreDeBase, idTenant: 1, idBase);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var datos = AltaFijaValida("General") with { Activo = false };

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.ActualizarAsync(idBase, datos));

        Assert.Equal("lista_referenciada_como_base", error.Codigo);
        Assert.Equal(409, error.EstadoHttp);
    }

    [Fact]
    public async Task ActualizarDesactivacionSinDependienteActivoEsPermitida()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idBase = await SembrarListaFijaAsync(nombreDeBase, idTenant: 1);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var datos = AltaFijaValida("General") with { Activo = false };
        var actualizada = await servicio.ActualizarAsync(idBase, datos);

        Assert.False(actualizada.Activo);
    }

    // ---- es_default: consistencia e intercambio (rama que NO abre transacción) --------------

    [Fact]
    public async Task ActualizarQuitarEsDefaultSinReemplazoEsRechazado()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idLista = await SembrarListaFijaAsync(nombreDeBase, idTenant: 1, esDefault: true);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var datos = AltaFijaValida("General") with { EsDefault = false };

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.ActualizarAsync(idLista, datos));

        Assert.Equal("lista_default_requiere_reemplazo", error.Codigo);
        Assert.Equal(409, error.EstadoHttp);
    }

    [Fact]
    public async Task CrearListaDefaultInactivaEsRechazada()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var datos = AltaFijaValida() with { EsDefault = true, Activo = false };

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.CrearAsync(datos));

        Assert.Equal("lista_default_debe_estar_activa", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    // ---- baja lógica: fila default protegida --------------------------------------------------

    [Fact]
    public async Task EliminarListaDefaultEsRechazada()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idLista = await SembrarListaFijaAsync(nombreDeBase, idTenant: 1, esDefault: true);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.EliminarAsync(idLista));

        Assert.Equal("lista_default_no_se_puede_eliminar", error.Codigo);
        Assert.Equal(409, error.EstadoHttp);
    }

    [Fact]
    public async Task EliminarConDependienteActivoEsRechazada()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idBase = await SembrarListaFijaAsync(nombreDeBase, idTenant: 1);
        await SembrarListaDerivadaAsync(nombreDeBase, idTenant: 1, idBase);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.EliminarAsync(idBase));

        Assert.Equal("lista_referenciada_como_base", error.Codigo);
        Assert.Equal(409, error.EstadoHttp);
    }

    [Fact]
    public async Task EliminarSinDependientesEsPermitida()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idLista = await SembrarListaFijaAsync(nombreDeBase, idTenant: 1);
        var servicio = CrearServicio(nombreDeBase, idTenant: 1);

        await servicio.EliminarAsync(idLista);

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.ObtenerAsync(idLista));
        Assert.Equal("no_encontrado", error.Codigo);
    }
}
