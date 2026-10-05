using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Articulos;
using Ways.Application.Bajas;
using Ways.Application.Familias;
using Ways.Application.Organizacion;
using Ways.Application.Precios;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Proveedores;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// Siembra y observación compartidas por las pruebas de los escritores de familias de artículos (doc 10
/// §3): el tenant con sus catálogos —dos de cada uno, para poder cambiar un campo compartido a otro valor
/// válido—, las familias y sus miembros con valores compartidos explícitos, y las lecturas de la base
/// (siempre sobre un contexto nuevo, nunca sobre el que escribió) y los observadores de <c>pg_locks</c>
/// que usan las pruebas de concurrencia. Una prueba de concurrencia de familias es un rendezvous
/// determinístico: una conexión cruda sostiene un lock, la escritura queda observada esperando en
/// <c>pg_locks</c> y recién ahí se libera (<c>mutation-proof-tests</c>, regla 13).
/// </summary>
internal sealed class ApoyoDeFamilias(WaysApiFixture fixture)
{
    public const string MailRoot = "test@test.com";
    public const string PasswordRoot = "root";

    public static readonly TimeSpan EsperaMaxima = TimeSpan.FromSeconds(30);

    /// <summary>Cada tenant sembrado reserva un bloque de ids para sus catálogos. Los ids de un catálogo se
    /// escriben explícitos y DISTINTOS entre catálogos (área, categoría, grupo, proveedor, marca): con el
    /// identity de cada tabla avanzando en paralelo, el área 7 y la categoría 7 coincidirían, y un escritor
    /// que cruzara dos campos compartidos del pedido (el área en la categoría) pasaría todas las pruebas.</summary>
    private static int siguienteBloqueDeIds = 20_000_000;

    /// <summary>Opciones del cliente HTTP de las pruebas: como el navegador, el enum viaja como TEXTO
    /// (<c>"Familia"</c>, <c>"Peso"</c>), no como su ordinal.</summary>
    public static readonly JsonSerializerOptions OpcionesJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public sealed record Entorno(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, int IdActorAdmin, int IdListaGeneral, int IdListaMayorista,
        IReadOnlyList<int> Areas, IReadOnlyList<int> Categorias, IReadOnlyList<int> Grupos,
        IReadOnlyList<int> Proveedores, IReadOnlyList<int> Marcas, IReadOnlyList<int> Alicuotas,
        HttpClient Admin, string MailAdmin, string PasswordAdmin) : IDisposable
    {
        public void Dispose() => Admin.Dispose();
    }

    // =================================================================================================
    // Siembra
    // =================================================================================================

    public async Task<Entorno> PrepararAsync(string nombre)
    {
        var unico = $"{nombre}-{Guid.NewGuid().ToString("N")[..8]}".ToLowerInvariant();
        var mailAdmin = $"{unico}@ways.test";

        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var respuesta = await root.PostAsJsonAsync(
            "/api/plataforma/tenants",
            new SolicitudDeAprovisionamiento(unico, $"{unico} SA", "Local 1", mailAdmin, ModoPuntoVenta.Web));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;
        var idTenant = resultado.IdTenant;

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var bloque = Interlocked.Add(ref siguienteBloqueDeIds, 100);

        var areas = new[]
        {
            new Area { Id = bloque + 1, IdTenant = idTenant, Nombre = $"{unico}-area-1", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora },
            new Area { Id = bloque + 2, IdTenant = idTenant, Nombre = $"{unico}-area-2", Orden = 2, CreatedAt = ahora, UpdatedAt = ahora }
        };
        var categorias = new[]
        {
            new Categoria { Id = bloque + 11, IdTenant = idTenant, Nombre = $"{unico}-cat-1", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora },
            new Categoria { Id = bloque + 12, IdTenant = idTenant, Nombre = $"{unico}-cat-2", Orden = 2, CreatedAt = ahora, UpdatedAt = ahora }
        };
        var grupos = new[]
        {
            new Grupo { Id = bloque + 21, IdTenant = idTenant, Nombre = $"{unico}-grupo-1", CreatedAt = ahora, UpdatedAt = ahora },
            new Grupo { Id = bloque + 22, IdTenant = idTenant, Nombre = $"{unico}-grupo-2", CreatedAt = ahora, UpdatedAt = ahora }
        };
        var marcas = new[]
        {
            new Marca { Id = bloque + 41, IdTenant = idTenant, Nombre = $"{unico}-marca-1", CreatedAt = ahora, UpdatedAt = ahora },
            new Marca { Id = bloque + 42, IdTenant = idTenant, Nombre = $"{unico}-marca-2", CreatedAt = ahora, UpdatedAt = ahora }
        };

        db.Areas.AddRange(areas);
        db.Categorias.AddRange(categorias);
        db.Grupos.AddRange(grupos);
        db.Marcas.AddRange(marcas);
        await db.SaveChangesAsync();

        var idCondicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync();
        var proveedores = new[]
        {
            new Proveedor
            {
                Id = bloque + 31, IdTenant = idTenant, RazonSocial = $"{unico}-prov-1",
                IdCondicionFiscal = idCondicionFiscal, CreatedAt = ahora, UpdatedAt = ahora
            },
            new Proveedor
            {
                Id = bloque + 32, IdTenant = idTenant, RazonSocial = $"{unico}-prov-2",
                IdCondicionFiscal = idCondicionFiscal, CreatedAt = ahora, UpdatedAt = ahora
            }
        };
        db.Proveedores.AddRange(proveedores);

        var mayorista = new ListaPrecio
        {
            IdTenant = idTenant, Nombre = "Mayorista", EsDefault = false, Modo = ModoLista.Fija,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ListasPrecio.Add(mayorista);
        await db.SaveChangesAsync();

        var alicuotas = await db.AlicuotasIva.OrderBy(a => a.Id).Select(a => a.Id).Take(2).ToListAsync();
        Assert.Equal(2, alicuotas.Count);

        var idListaGeneral = await db.ListasPrecio
            .Where(l => l.IdTenant == idTenant && l.EsDefault)
            .Select(l => l.Id)
            .SingleAsync();

        var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        return new Entorno(
            idTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, resultado.IdUsuarioAdmin, idListaGeneral, mayorista.Id,
            [.. areas.Select(a => a.Id)], [.. categorias.Select(c => c.Id)], [.. grupos.Select(g => g.Id)],
            [.. proveedores.Select(p => p.Id)], [.. marcas.Select(m => m.Id)], alicuotas, admin, mailAdmin,
            resultado.PasswordTemporal);
    }

    /// <summary>Los trece campos compartidos de partida: todos con un valor (los nulleables también), para
    /// que cada cambio de <see cref="ValoresConUnCampoCambiado"/> sea de un valor a OTRO y no de nulo a
    /// valor.</summary>
    public static ValoresCompartidosDeFamilia ValoresBase(Entorno e) => new(
        IdArea: e.Areas[0], IdCategoria: e.Categorias[0], IdGrupo: e.Grupos[0], IdProveedorHabitual: e.Proveedores[0],
        IdAlicuotaIva: e.Alicuotas[0], UnidadVenta: UnidadVenta.Unidad, UnidadesPorBulto: 6m, EsProducto: true,
        ControlaLote: false, AcumulaEnVenta: true, CostoLista: 50m, DescuentoProveedor: 10m, CostoNominal: 40m);

    /// <summary>Los valores de <see cref="ValoresBase"/> con UN solo campo (el que nombra
    /// <paramref name="columna"/>, con el nombre de la columna de <c>articulos</c>) cambiado a otro valor
    /// válido.</summary>
    public static ValoresCompartidosDeFamilia ValoresConUnCampoCambiado(Entorno e, string columna)
    {
        var @base = ValoresBase(e);

        return columna switch
        {
            "id_area" => @base with { IdArea = e.Areas[1] },
            "id_categoria" => @base with { IdCategoria = e.Categorias[1] },
            "id_grupo" => @base with { IdGrupo = e.Grupos[1] },
            "id_proveedor_habitual" => @base with { IdProveedorHabitual = e.Proveedores[1] },
            "id_alicuota_iva" => @base with { IdAlicuotaIva = e.Alicuotas[1] },
            "unidad_venta" => @base with { UnidadVenta = UnidadVenta.Peso },
            "unidades_por_bulto" => @base with { UnidadesPorBulto = 12m },
            "es_producto" => @base with { EsProducto = false },
            "controla_lote" => @base with { ControlaLote = true },
            "acumula_en_venta" => @base with { AcumulaEnVenta = false },
            "costo_lista" => @base with { CostoLista = 60m },
            "descuento_proveedor" => @base with { DescuentoProveedor = 15m },
            "costo_nominal" => @base with { CostoNominal = 45m },
            _ => throw new ArgumentOutOfRangeException(nameof(columna), columna, "Columna compartida desconocida.")
        };
    }

    /// <summary>Las trece columnas compartidas de <c>articulos</c>, en el orden de declaración del record.</summary>
    public static readonly string[] ColumnasCompartidas =
    [
        "id_area", "id_categoria", "id_grupo", "id_proveedor_habitual", "id_alicuota_iva", "unidad_venta",
        "unidades_por_bulto", "es_producto", "controla_lote", "acumula_en_venta", "costo_lista",
        "descuento_proveedor", "costo_nominal"
    ];

    public async Task<int> SembrarFamiliaAsync(Entorno e, string nombre, bool activa = true, bool dadaDeBaja = false)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var familia = new Familia
        {
            IdTenant = e.IdTenant, Nombre = nombre, Activo = activa, CreatedAt = ahora, UpdatedAt = ahora,
            DeletedAt = dadaDeBaja ? ahora : null
        };
        db.Familias.Add(familia);
        await db.SaveChangesAsync();

        return familia.Id;
    }

    /// <summary>Un artículo con los campos compartidos de <paramref name="compartidos"/> escritos uno por uno
    /// (la siembra no usa <see cref="ValoresCompartidosDeFamilia.AplicarA"/>: es parte de lo que se prueba).
    /// Un artículo "dado de baja" lleva <c>DeletedAt</c> y sigue apuntando a su familia: no cuenta como
    /// miembro. <paramref name="id"/> fija el id en vez de dejarlo al identity. <paramref name="prefijoDelCodigo"/>
    /// reemplaza al nombre como prefijo del código interno: el índice único del código (que el planificador puede
    /// recorrer en su orden) deja de coincidir con el orden de los nombres o de los ids cuando una prueba lo necesita.</summary>
    public async Task<int> SembrarArticuloAsync(
        Entorno e, string nombre, ValoresCompartidosDeFamilia compartidos, int? idFamilia = null,
        bool dadoDeBaja = false, int? idMarca = null, string? descripcion = null, bool activo = true,
        bool disponibleParaTodas = true, int? id = null, string? prefijoDelCodigo = null)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow.AddMinutes(-10);

        var articulo = new Articulo
        {
            Id = id ?? 0,
            IdTenant = e.IdTenant,
            CodigoInterno = $"{prefijoDelCodigo ?? nombre}-{Guid.NewGuid().ToString("N")[..8]}",
            Nombre = nombre,
            Descripcion = descripcion,
            IdMarca = idMarca,
            Activo = activo,
            DisponibleParaTodas = disponibleParaTodas,
            IdFamilia = idFamilia,
            IdArea = compartidos.IdArea,
            IdCategoria = compartidos.IdCategoria,
            IdGrupo = compartidos.IdGrupo,
            IdProveedorHabitual = compartidos.IdProveedorHabitual,
            IdAlicuotaIva = compartidos.IdAlicuotaIva,
            UnidadVenta = compartidos.UnidadVenta,
            UnidadesPorBulto = compartidos.UnidadesPorBulto,
            EsProducto = compartidos.EsProducto,
            ControlaLote = compartidos.ControlaLote,
            AcumulaEnVenta = compartidos.AcumulaEnVenta,
            CostoLista = compartidos.CostoLista,
            DescuentoProveedor = compartidos.DescuentoProveedor,
            CostoNominal = compartidos.CostoNominal,
            CreatedAt = ahora,
            UpdatedAt = ahora,
            DeletedAt = dadoDeBaja ? ahora : null
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        return articulo.Id;
    }

    /// <summary>La baja lógica de una fila de catálogo que algún artículo todavía referencia: el DELETE de la API la
    /// rechazaría con 409 (<c>GuardaDeReferencias</c>), así que se estampa <c>deleted_at</c> directo sobre la fila
    /// existente, que es el dato heredado que las lecturas tienen que sobrevivir. <paramref name="tabla"/> y
    /// <paramref name="columnaId"/> son literales de la prueba.</summary>
    public async Task DarDeBajaAsync(string tabla, string columnaId, int id)
    {
        await using var cruda = await fixture.AbrirConexionCrudaAsync("plataforma", null);
        await using var comando = new NpgsqlCommand(
            $"UPDATE {tabla} SET deleted_at = now() WHERE {columnaId} = $1 AND deleted_at IS NULL", cruda);
        comando.Parameters.Add(new NpgsqlParameter { Value = id });

        Assert.Equal(1, await comando.ExecuteNonQueryAsync());
    }

    /// <summary>Un precio abierto (<c>vigente_hasta</c> nulo) que arrancó hace dos días.</summary>
    public Task SembrarPrecioVigenteAsync(Entorno e, int idArticulo, int idLista, decimal monto) =>
        SembrarPrecioAsync(e, idArticulo, idLista, monto, DateTimeOffset.UtcNow.AddDays(-2), null);

    /// <summary>El estado que deja programar un precio: el vigente hasta la fecha del pendiente y el pendiente
    /// a partir de ahí (<c>vigente_hasta</c> nulo, <c>vigente_desde</c> a futuro). Para sembrar varios artículos
    /// con la MISMA fecha de pendiente se pasa <paramref name="desdeDelPendiente"/>.</summary>
    public async Task<DateTimeOffset> SembrarPrecioPendienteAsync(
        Entorno e, int idArticulo, int idLista, decimal montoVigente, decimal montoPendiente,
        DateTimeOffset? desdeDelPendiente = null)
    {
        var desdePendiente = desdeDelPendiente ?? DateTimeOffset.UtcNow.AddDays(3);
        await SembrarPrecioAsync(e, idArticulo, idLista, montoVigente, DateTimeOffset.UtcNow.AddDays(-2), desdePendiente);
        await SembrarPrecioAsync(e, idArticulo, idLista, montoPendiente, desdePendiente, null);

        return desdePendiente;
    }

    /// <summary>Una lista de precios <c>fija</c> más del tenant, sin ningún precio.</summary>
    public async Task<int> SembrarListaFijaAsync(Entorno e, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var lista = new ListaPrecio
        {
            IdTenant = e.IdTenant, Nombre = nombre, EsDefault = false, Modo = ModoLista.Fija,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ListasPrecio.Add(lista);
        await db.SaveChangesAsync();

        return lista.Id;
    }

    /// <summary>Una lista de precios <c>derivada</c> (un porcentaje sobre <paramref name="idListaBase"/>): no
    /// guarda filas en <c>precios</c> y no forma parte del estado de precios de una familia.</summary>
    public async Task<int> SembrarListaDerivadaAsync(Entorno e, string nombre, int idListaBase)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var lista = new ListaPrecio
        {
            IdTenant = e.IdTenant, Nombre = nombre, EsDefault = false, Modo = ModoLista.Derivada,
            IdListaBase = idListaBase, Porcentaje = -10m, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ListasPrecio.Add(lista);
        await db.SaveChangesAsync();

        return lista.Id;
    }

    /// <summary>Un cliente HTTP con la sesión abierta de un usuario del tenant con el rol dado (el
    /// <see cref="Entorno.Admin"/> ya es el administrador). <paramref name="sufijo"/> distingue al usuario
    /// cuando una prueba necesita más de uno con el mismo rol.</summary>
    public async Task<HttpClient> ClienteConRolAsync(Entorno e, RolConocido rol, string sufijo = "")
    {
        const string password = "una-contraseña-larga";

        var hasheador = new HasheadorPbkdf2();
        var nombre = $"{rol}{sufijo}".ToLowerInvariant();
        var mail = $"{nombre}-{Guid.NewGuid().ToString("N")[..8]}@ways.test";

        await using (var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            var ahora = DateTimeOffset.UtcNow;

            db.Usuarios.Add(new Usuario
            {
                IdTenant = e.IdTenant, NombreUsuario = nombre, Mail = mail, RolId = (int)rol,
                PasswordHash = hasheador.Hashear(password), PasswordAlgoritmo = hasheador.Algoritmo,
                PasswordActualizadoEl = ahora, CreatedAt = ahora, UpdatedAt = ahora
            });
            await db.SaveChangesAsync();
        }

        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        return cliente;
    }

    /// <summary>Un cliente HTTP con la sesión del usuario root de la plataforma (que no opera ningún tenant).</summary>
    public async Task<HttpClient> ClienteRootAsync()
    {
        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        return cliente;
    }

    public async Task SembrarPrecioAsync(
        Entorno e, int idArticulo, int idLista, decimal monto, DateTimeOffset desde, DateTimeOffset? hasta)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        db.Precios.Add(new Precio
        {
            IdTenant = e.IdTenant, IdArticulo = idArticulo, IdListaPrecio = idLista, Monto = monto,
            VigenteDesde = desde, VigenteHasta = hasta, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    // =================================================================================================
    // Lectura de la base (contexto nuevo, nunca el que escribió)
    // =================================================================================================

    /// <summary>La fila de la familia tal como está en la base, también si está dada de baja.</summary>
    public async Task<Familia> LeerFamiliaAsync(int idFamilia)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        return await db.Familias.IgnoreQueryFilters().AsNoTracking().SingleAsync(f => f.Id == idFamilia);
    }

    public async Task<Articulo> LeerAsync(int idArticulo)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        return await db.Articulos.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.Id == idArticulo);
    }

    public async Task<List<Precio>> FilasDePrecioAsync(int idArticulo, int idLista)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        return await db.Precios.IgnoreQueryFilters()
            .Where(p => p.IdArticulo == idArticulo && p.IdListaPrecio == idLista)
            .OrderBy(p => p.VigenteDesde)
            .ThenBy(p => p.Id)
            .ToListAsync();
    }

    public async Task<List<Ways.Domain.Auditoria.Auditoria>> AuditoriaDePreciosAsync(int idTenant)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        return await db.Auditoria.IgnoreQueryFilters()
            .Where(a => a.IdTenant == idTenant && a.Accion == "precio.cambio")
            .OrderBy(a => a.Id)
            .ToListAsync();
    }

    public WaysDbContext ContextoDelTenant(Entorno e, params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptores) =>
        fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, e.IdTenant), interceptores);

    /// <summary>El servicio de familias armado a mano sobre <paramref name="db"/> —con el reloj real y las
    /// dependencias que tiene en producción—, para las pruebas que lo llaman sin pasar por HTTP.</summary>
    public static ServicioDeFamilias ServicioDe(WaysDbContext db, Entorno e) =>
        new(db, new RelojDelSistema(), new ContextoDeAdmin(e.IdTenant, e.IdActorAdmin), new GuardaDeReferencias(db, new InspectorDeUso(db)));

    /// <summary>El servicio de agrupación armado a mano sobre <paramref name="db"/>, con el reloj que pida la prueba
    /// (por defecto el real).</summary>
    public static ServicioDeAgrupacionDeFamilias ServicioDeAgrupacionDe(
        WaysDbContext db, Entorno e, IRelojDelSistema? reloj = null)
    {
        reloj ??= new RelojDelSistema();
        var contexto = new ContextoDeAdmin(e.IdTenant, e.IdActorAdmin);

        return new ServicioDeAgrupacionDeFamilias(
            db, reloj, contexto,
            new ServicioDePrecios(db, reloj, contexto, new Ways.Application.Auditoria.ServicioDeAuditoria(db, reloj, contexto)));
    }

    /// <summary>Un reloj que cuenta cuántas veces se lo lee y devuelve un instante distinto en cada lectura (un segundo
    /// más que la anterior): dos lecturas del reloj en una misma operación dan instantes distintos y se pueden
    /// distinguir.</summary>
    public sealed class RelojContador(DateTimeOffset inicio) : IRelojDelSistema
    {
        private long lecturas;

        public long Lecturas => Interlocked.Read(ref lecturas);

        public DateTimeOffset Ahora => inicio.AddSeconds(Interlocked.Increment(ref lecturas));
    }

    /// <summary>El actor de las pruebas que arman un servicio a mano: el administrador del tenant.</summary>
    private sealed class ContextoDeAdmin(int idTenant, int idUsuario) : IContextoDeUsuario
    {
        public bool EstaAutenticado => true;
        public int UsuarioId => idUsuario;
        public string NombreUsuario => "admin-de-prueba";
        public RolConocido Rol => RolConocido.Admin;
        public int? IdTenant => idTenant;
    }

    // =================================================================================================
    // Pedidos HTTP
    // =================================================================================================

    /// <summary>El pedido de edición que no cambia nada: los valores guardados de <paramref name="a"/> tal cual.</summary>
    public static EdicionArticulo EdicionIgualA(Articulo a, AlcanceDeFamilia? alcance = null) => new(
        Nombre: a.Nombre, Descripcion: a.Descripcion, IdArea: a.IdArea, IdCategoria: a.IdCategoria,
        IdMarca: a.IdMarca, IdGrupo: a.IdGrupo, IdProveedorHabitual: a.IdProveedorHabitual,
        IdAlicuotaIva: a.IdAlicuotaIva, UnidadVenta: a.UnidadVenta, UnidadesPorBulto: a.UnidadesPorBulto,
        EsProducto: a.EsProducto, CostoLista: a.CostoLista, DescuentoProveedor: a.DescuentoProveedor,
        CostoNominal: a.CostoNominal, DisponibleParaTodas: a.DisponibleParaTodas, IdsEmpresas: null, Activo: a.Activo,
        ControlaLote: a.ControlaLote, AcumulaEnVenta: a.AcumulaEnVenta, Alcance: alcance);

    /// <summary>Los trece campos compartidos de <paramref name="valores"/> puestos en el pedido de
    /// edición.</summary>
    public static EdicionArticulo ConCompartidos(EdicionArticulo edicion, ValoresCompartidosDeFamilia valores) =>
        edicion with
        {
            IdArea = valores.IdArea, IdCategoria = valores.IdCategoria, IdGrupo = valores.IdGrupo,
            IdProveedorHabitual = valores.IdProveedorHabitual, IdAlicuotaIva = valores.IdAlicuotaIva,
            UnidadVenta = valores.UnidadVenta, UnidadesPorBulto = valores.UnidadesPorBulto,
            EsProducto = valores.EsProducto, ControlaLote = valores.ControlaLote,
            AcumulaEnVenta = valores.AcumulaEnVenta, CostoLista = valores.CostoLista,
            DescuentoProveedor = valores.DescuentoProveedor, CostoNominal = valores.CostoNominal
        };

    public static Task<HttpResponseMessage> PutArticuloAsync(HttpClient admin, int id, EdicionArticulo edicion) =>
        admin.PutAsJsonAsync($"/api/articulos/{id}", edicion, OpcionesJson);

    public static async Task<(string? Codigo, string? Mensaje)> ProblemaAsync(HttpResponseMessage respuesta)
    {
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();

        return (
            problema.GetProperty("codigo").GetString(),
            problema.TryGetProperty("title", out var titulo) ? titulo.GetString() : null);
    }

    // =================================================================================================
    // Observadores de pg_locks
    // =================================================================================================

    public sealed record Pid(int Valor);

    public sealed record Candado(int Pid, long ClassId, long ObjId, int ObjSubId, string Modo, bool Concedido);

    public static async Task EjecutarAsync(
        NpgsqlConnection conexion, NpgsqlTransaction transaccion, string sql, params object[] parametros)
    {
        await using var comando = new NpgsqlCommand(sql, conexion, transaccion);
        foreach (var parametro in parametros)
        {
            comando.Parameters.Add(new NpgsqlParameter { Value = parametro });
        }

        await comando.ExecuteNonQueryAsync();
    }

    public static async Task<T> EsperarAsync<T>(Func<Task<T?>> buscar, string mensaje)
        where T : class
    {
        var limite = DateTime.UtcNow.Add(EsperaMaxima);

        while (DateTime.UtcNow < limite)
        {
            if (await buscar() is { } encontrado)
            {
                return encontrado;
            }

            await Task.Delay(25);
        }

        throw new Xunit.Sdk.XunitException(mensaje);
    }

    /// <summary>El pid del backend que espera la fila de otra transacción (un <c>transactionid</c> sin
    /// conceder), o <c>null</c> si ninguno.</summary>
    public static async Task<Pid?> EsperandoUnaFilaAsync(NpgsqlConnection poll)
    {
        await using var comando = new NpgsqlCommand(
            "SELECT pid FROM pg_locks WHERE locktype = 'transactionid' AND NOT granted", poll);

        return await comando.ExecuteScalarAsync() is int pid ? new Pid(pid) : null;
    }

    public static async Task<List<Candado>> CandadosAdvisoryAsync(
        NpgsqlConnection poll, string filtro, params object[] parametros)
    {
        await using var comando = new NpgsqlCommand(
            "SELECT pid, classid::bigint, objid::bigint, objsubid, mode, granted FROM pg_locks " +
            $"WHERE locktype = 'advisory' AND {filtro}",
            poll);
        foreach (var parametro in parametros)
        {
            comando.Parameters.Add(new NpgsqlParameter { Value = parametro });
        }

        var candados = new List<Candado>();
        await using var lector = await comando.ExecuteReaderAsync();
        while (await lector.ReadAsync())
        {
            candados.Add(new Candado(
                lector.GetInt32(0), lector.GetInt64(1), lector.GetInt64(2), lector.GetInt32(3),
                lector.GetString(4), lector.GetBoolean(5)));
        }

        return candados;
    }

    public static (long Alto, long Bajo) PartesDeLaClave(long clave) => (clave >> 32, clave & 0xFFFFFFFFL);

    /// <summary>El candado sin conceder (con el pid del backend que espera) del lock de membresía del
    /// tenant, o <c>null</c> si nadie lo espera. Una clave <c>bigint</c> se ve en <c>pg_locks</c> con
    /// <c>objsubid = 1</c>, la mitad alta en <c>classid</c> y la baja en <c>objid</c>.</summary>
    public static async Task<Candado?> EsperandoLaMembresiaAsync(NpgsqlConnection poll, int idTenant)
    {
        var (alto, bajo) = PartesDeLaClave(LockDeMembresiaDeFamilias.ClaveDe(idTenant));

        var esperando = await CandadosAdvisoryAsync(
            poll, "NOT granted AND objsubid = 1 AND classid::bigint = $1 AND objid::bigint = $2", alto, bajo);

        return esperando.SingleOrDefault();
    }

    /// <summary>Los locks advisory CONCEDIDOS al backend <paramref name="pid"/>.</summary>
    public static Task<List<Candado>> CandadosConcedidosAsync(NpgsqlConnection poll, int pid) =>
        CandadosAdvisoryAsync(poll, "granted AND pid = $1", pid);

    /// <summary>Cuántos locks de tipo <c>transactionid</c> (el de la transacción propia: se toma al escribir
    /// por primera vez) tiene el backend <paramref name="pid"/>. Cero ⇒ todavía no escribió nada.</summary>
    public static async Task<long> TransactionIdsConcedidosAsync(NpgsqlConnection poll, int pid)
    {
        await using var comando = new NpgsqlCommand(
            "SELECT count(*) FROM pg_locks WHERE locktype = 'transactionid' AND granted AND pid = $1", poll);
        comando.Parameters.Add(new NpgsqlParameter { Value = pid });

        return (long)(await comando.ExecuteScalarAsync())!;
    }

    public static (long ClassId, long ObjId) ClaveDelPar(int idTenant, int idArticulo, int idLista)
    {
        var (clave1, clave2) = ServicioDePrecios.ClaveDeLockDePar(idTenant, idArticulo, idLista);

        return (clave1, unchecked((uint)clave2));
    }

    public async Task<(NpgsqlConnection Poll, NpgsqlConnection Sostenedor, NpgsqlTransaction Transaccion)> AbrirSostenedorAsync(
        int idTenant)
    {
        var sostenedor = await fixture.AbrirConexionCrudaAsync("tenant", idTenant);
        var transaccion = await sostenedor.BeginTransactionAsync();
        var poll = await fixture.AbrirConexionCrudaAsync("plataforma", null);

        return (poll, sostenedor, transaccion);
    }
}
