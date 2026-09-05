using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Ways.Application.Abstracciones;
using Ways.Application.Auditoria;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// Etapa 20, slice 1 (tasks 1.7-1.13): los cuatro listados raíz proyectan NOMBRES de dueño y
/// contadores de hijos, como subconsultas correlacionadas dentro del mismo <c>Select</c>
/// (design D13/D14), sin agregar una sola ida más a la base. Corre contra Postgres real: la
/// forma de la proyección es exactamente lo que se está probando, así que un doble en memoria
/// no probaría el SQL que efectivamente se genera.
///
/// Cada fixture siembra DOS tenants (<c>mutation-proof-tests</c> regla 12c): una proyección que
/// ignorara la correlación —o que devolviera "el primer tenant"— pasaría con un solo tenant
/// sembrado y muere acá.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ProyeccionDeOrganizacionTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string Password = "una-contraseña-larga";
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    /// <summary>Los tres contadores que siembran las dos pruebas de readback sobre el tenant que
    /// leen: 2 empresas, 3 puntos de venta y 4 usuarios. Distintos entre sí para que un
    /// intercambio posicional entre ellos muera (<c>mutation-proof-tests</c> regla 12b).</summary>
    private const int EmpresasDelTenantLeido = 2;
    private const int PuntosVentaDelTenantLeido = 3;
    private const int UsuariosDelTenantLeido = 4;

    private static readonly int[] ContadoresSembrados =
        [EmpresasDelTenantLeido, PuntosVentaDelTenantLeido, UsuariosDelTenantLeido];

    /// <summary>Tenants de relleno que se siembran ANTES del tenant que se lee: como la secuencia
    /// de identidad es monótona, el tenant bajo prueba queda con un id estrictamente mayor que el
    /// más grande de los tres contadores, por construcción y sin depender de ninguna fila que la
    /// prueba no siembre. Eso es lo que hace que un intercambio posicional entre el id y un
    /// contador muera.
    ///
    /// Etapa 20 slice 4 (entrada de judgment-day de la slice 1, item 3): se DERIVA del máximo real
    /// en vez de estar escrito como <c>UsuariosDelTenantLeido</c>. Esa igualdad era una
    /// coincidencia sin quien la sostuviera —valía solo porque 4 era el máximo de {2, 3, 4}—, así
    /// que bajar ese contador o subir un hermano rompía la cota en silencio, sin error de
    /// compilación ni prueba en rojo.</summary>
    private static readonly int TenantsDeRelleno = ContadoresSembrados.Max();

    /// <summary>El servidor serializa enums como texto (<c>JsonStringEnumConverter</c>) y el
    /// <c>HttpClient</c> de prueba no hereda esa configuración — mismo criterio, y misma
    /// trampa de <c>PropertyNameCaseInsensitive</c>, que <c>OrganizacionTests.OpcionesJson</c>
    /// documenta en detalle.</summary>
    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private sealed record Cuenta(int Id, string Mail);

    private sealed record TenantSembrado(
        Tenant Tenant, Empresa Empresa, PuntoVenta PuntoVenta, Cuenta Admin);

    // ---- siembra ---------------------------------------------------------------------------

    /// <summary>Siembra un tenant con una empresa, un punto de venta y un admin propio, en modo
    /// plataforma y con hash real — mismo criterio que <c>OrganizacionTests.SembrarTenantAsync</c>:
    /// la API bajo prueba acá es la de LECTURA, no la de alta.</summary>
    private async Task<TenantSembrado> SembrarTenantAsync(
        string nombre,
        string? razonSocialEmpresa = null,
        string? nombrePuntoVenta = null,
        string? nombreUsuarioAdmin = null)
    {
        using var _ = fixture.CreateClient(); // arranca el host: siembra roles y el root

        var hasheador = new HasheadorPbkdf2();
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var ahora = DateTimeOffset.UtcNow;
        var tenant = new Tenant
        {
            Nombre = nombre,
            Estado = EstadoTenant.Activo,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var empresa = new Empresa
        {
            IdTenant = tenant.Id,
            RazonSocial = razonSocialEmpresa ?? $"{nombre} SRL",
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Empresas.Add(empresa);
        await db.SaveChangesAsync();

        var puntoVenta = new PuntoVenta
        {
            IdTenant = tenant.Id,
            IdEmpresa = empresa.Id,
            Nombre = nombrePuntoVenta ?? $"{nombre} - Local 1",
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.PuntosVenta.Add(puntoVenta);
        await db.SaveChangesAsync();

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var admin = new Usuario
        {
            IdTenant = tenant.Id,
            NombreUsuario = nombreUsuarioAdmin ?? "admin",
            Mail = mailAdmin,
            RolId = (int)RolConocido.Admin,
            PasswordHash = hasheador.Hashear(Password),
            PasswordAlgoritmo = hasheador.Algoritmo,
            PasswordActualizadoEl = ahora,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Usuarios.Add(admin);
        await db.SaveChangesAsync();

        return new TenantSembrado(tenant, empresa, puntoVenta, new Cuenta(admin.Id, mailAdmin));
    }

    /// <summary>Agrega un usuario más a un tenant ya sembrado.</summary>
    private async Task<Cuenta> SembrarUsuarioAsync(int idTenant, string nombreUsuario, RolConocido rol)
    {
        var hasheador = new HasheadorPbkdf2();
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var ahora = DateTimeOffset.UtcNow;
        var mail = $"{nombreUsuario}-{Guid.NewGuid():N}@ways.test";
        var usuario = new Usuario
        {
            IdTenant = idTenant,
            NombreUsuario = nombreUsuario,
            Mail = mail,
            RolId = (int)rol,
            PasswordHash = hasheador.Hashear(Password),
            PasswordAlgoritmo = hasheador.Algoritmo,
            PasswordActualizadoEl = ahora,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Usuarios.Add(usuario);
        await db.SaveChangesAsync();

        return new Cuenta(usuario.Id, mail);
    }

    private async Task<Empresa> SembrarEmpresaAsync(int idTenant, string razonSocial)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var ahora = DateTimeOffset.UtcNow;
        var empresa = new Empresa
        {
            IdTenant = idTenant,
            RazonSocial = razonSocial,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Empresas.Add(empresa);
        await db.SaveChangesAsync();

        return empresa;
    }

    private async Task<PuntoVenta> SembrarPuntoVentaAsync(int idTenant, int idEmpresa, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var ahora = DateTimeOffset.UtcNow;
        var puntoVenta = new PuntoVenta
        {
            IdTenant = idTenant,
            IdEmpresa = idEmpresa,
            Nombre = nombre,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.PuntosVenta.Add(puntoVenta);
        await db.SaveChangesAsync();

        return puntoVenta;
    }

    /// <summary>Baja LÓGICA (B1: en esta etapa nada se borra físicamente): escribe
    /// <c>deleted_at</c> sobre la fila, que es exactamente lo que el slice 4 va a hacer desde el
    /// servicio. Acá se escribe a mano justamente porque el escritor todavía no existe.</summary>
    private async Task DarDeBajaAlTenantAsync(int id) =>
        await DarDeBajaAsync(db => db.Tenants.FirstAsync(t => t.Id == id));

    private async Task DarDeBajaALaEmpresaAsync(int id) =>
        await DarDeBajaAsync(db => db.Empresas.FirstAsync(e => e.Id == id));

    private async Task DarDeBajaAlUsuarioAsync(int id) =>
        await DarDeBajaAsync(db => db.Usuarios.FirstAsync(u => u.Id == id));

    private async Task DarDeBajaAlPuntoDeVentaAsync(int id) =>
        await DarDeBajaAsync(db => db.PuntosVenta.FirstAsync(p => p.Id == id));

    private async Task DarDeBajaAsync<T>(Func<WaysDbContext, Task<T>> buscar)
        where T : Ways.Domain.Common.EntidadBase
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var fila = await buscar(db);
        var ahora = DateTimeOffset.UtcNow;
        fila.DeletedAt = ahora;
        fila.UpdatedAt = ahora;
        await db.SaveChangesAsync();
    }

    /// <summary>Una cuenta de plataforma (<c>id_tenant IS NULL</c>) sembrada a mano: no hay
    /// endpoint que las cree, y es la única forma de probar que ningún tenant la cuenta.</summary>
    private async Task SembrarUsuarioDePlataformaAsync(string nombreUsuario)
    {
        var hasheador = new HasheadorPbkdf2();
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var ahora = DateTimeOffset.UtcNow;
        db.Usuarios.Add(new Usuario
        {
            IdTenant = null,
            NombreUsuario = nombreUsuario,
            Mail = $"{nombreUsuario}@ways.test",
            RolId = (int)RolConocido.Root,
            PasswordHash = hasheador.Hashear(Password),
            PasswordAlgoritmo = hasheador.Algoritmo,
            PasswordActualizadoEl = ahora,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    private async Task<HttpClient> ClienteComoRootAsync()
    {
        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    private async Task<HttpClient> ClienteComoAdminAsync(string mailAdmin)
    {
        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mailAdmin, Password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    // ---- task 1.7: exactamente una ida a la base por listado ---------------------------------

    /// <summary>Cuenta cada comando que dispara <c>ReaderExecuting</c> — mismo mecanismo que
    /// <c>VentasCheckoutTests.ContadorDeComandos</c>.</summary>
    private sealed class ContadorDeComandos : DbCommandInterceptor
    {
        public int Consultas { get; private set; }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Consultas++;
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Consultas++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class ContextoFijo(int? idTenant, int usuarioId, RolConocido rol) : IContextoDeUsuario
    {
        public bool EstaAutenticado => true;
        public int UsuarioId => usuarioId;
        public string NombreUsuario => "contexto-fijo";
        public RolConocido Rol => rol;
        public int? IdTenant => idTenant;
    }

    private sealed class RelojFijo(DateTimeOffset ahora) : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = ahora;
    }

    /// <summary>
    /// Task 1.7 (criterio V9) — la cláusula bajo prueba es la PROYECCIÓN: los nombres de dueño y
    /// los contadores viajan DENTRO del mismo <c>Select</c>, nunca en una segunda consulta ni en
    /// un lookup por fila.
    ///
    /// La medición se toma sobre la llamada de servicio, no sobre el request HTTP, y eso es
    /// deliberado (<c>mutation-proof-tests</c> regla 3, "ruteá por debajo del confound"): el
    /// pipeline de la API revalida la cookie contra <c>usuarios</c> y <c>tenants</c> en CADA
    /// request (ADR-2), así que un contador colgado del host mediría esas consultas mezcladas con
    /// la del listado y el número dejaría de discriminar. Los cuatro endpoints delegan de forma
    /// directa en estos cuatro métodos (<c>OrganizacionEndpoints</c>/<c>UsuariosEndpoints</c>), y
    /// que efectivamente sirven esta proyección lo prueban las tasks 1.8-1.13, que sí van por HTTP.
    ///
    /// <c>ListarAsync</c> de usuarios vale 2 y no 1 por una razón preexistente a esta etapa: es el
    /// único listado paginado, así que emite su <c>CountAsync</c> del total además de la página.
    /// La proyección de tenant no agrega ninguna: si lo hiciera, serían 3.
    ///
    /// MUTACIÓN registrada: sacar la subconsulta de <c>ProyeccionDeEmpresa</c> y resolver el
    /// nombre con una segunda consulta lleva el conteo de empresas de 1 a 2 y la prueba se cae.
    /// </summary>
    [Fact]
    public async Task CadaListadoCuestaExactamenteUnaIdaALaBase()
    {
        var a = await SembrarTenantAsync(nameof(CadaListadoCuestaExactamenteUnaIdaALaBase) + "-A");
        await SembrarTenantAsync(nameof(CadaListadoCuestaExactamenteUnaIdaALaBase) + "-B");
        await SembrarEmpresaAsync(a.Tenant.Id, "Segunda empresa SRL");

        var reloj = new RelojFijo(DateTimeOffset.UtcNow);
        var contextoRoot = new ContextoFijo(null, a.Admin.Id, RolConocido.Root);

        Assert.Equal(1, await ContarAsync(async servicio =>
        {
            var tenants = await servicio.ListarTenantsAsync();
            Assert.Contains(tenants, t => t.Id == a.Tenant.Id);
        }));

        Assert.Equal(1, await ContarAsync(async servicio =>
        {
            var empresas = await servicio.ListarEmpresasAsync();
            Assert.Contains(empresas, e => e.Id == a.Empresa.Id);
        }));

        Assert.Equal(1, await ContarAsync(async servicio =>
        {
            var puntos = await servicio.ListarPuntosVentaAsync();
            Assert.Contains(puntos, p => p.Id == a.PuntoVenta.Id);
        }));

        var contadorUsuarios = new ContadorDeComandos();
        await using (var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma, contadorUsuarios))
        await using (var dbPlataforma = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma, contadorUsuarios))
        {
            var servicio = new ServicioDeUsuarios(
                db, dbPlataforma, new HasheadorPbkdf2(), reloj, contextoRoot,
                new ServicioDeAuditoria(db, reloj, contextoRoot),
                new InspectorDeUso(db));

            var pagina = await servicio.ListarAsync(tamanio: 200);
            Assert.Contains(pagina.Items, u => u.Id == a.Admin.Id);
        }

        Assert.Equal(2, contadorUsuarios.Consultas);

        async Task<int> ContarAsync(Func<ServicioDeOrganizacion, Task> accion)
        {
            var contador = new ContadorDeComandos();
            await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma, contador);
            await accion(new ServicioDeOrganizacion(db, reloj, contextoRoot, new InspectorDeUso(db)));
            return contador.Consultas;
        }
    }

    // ---- task 1.8: los listados llevan los nombres de los dueños ------------------------------

    /// <summary>
    /// Task 1.8 (TO-R1) — la cláusula bajo prueba es la CORRELACIÓN de las subconsultas escalares
    /// (<c>t.Id == e.IdTenant</c>, <c>e.Id == p.IdEmpresa</c>). Por eso el fixture siembra dos
    /// tenants con dos empresas y dos puntos de venta de nombres distintos
    /// (<c>mutation-proof-tests</c> regla 12c): una proyección que devolviera "el primer tenant"
    /// —o que se olvidara del <c>Where</c>— sobreviviría con un solo tenant sembrado.
    ///
    /// MUTACIÓN registrada: reemplazar el <c>Where</c> de <c>ProyeccionDeEmpresa</c> por un
    /// <c>db.Tenants.Select(t => t.Nombre).FirstOrDefault()</c> sin correlación hace que ambas
    /// empresas reporten el mismo nombre y la prueba se cae.
    ///
    /// Para <c>RazonSocialEmpresa</c> el hermano tiene que ser del MISMO dueño (regla 12c): con
    /// una sola empresa por tenant, un mutante que correlacionara por <c>e.IdTenant == p.IdTenant</c>
    /// devolvería la misma razón social y sobreviviría. Por eso el tenant A lleva una SEGUNDA
    /// empresa con su propio punto de venta. MUTACIÓN registrada: correlacionar por
    /// <c>e.IdTenant == p.IdTenant</c> hace que los dos puntos de venta de A reporten una razón
    /// social que no es la suya y la prueba se cae — observado ROJO al correrla. La muerte no se
    /// afirma como universal: un <c>FirstOrDefault</c> sin <c>OrderBy</c> no le debe a nadie
    /// devolver la MISMA fila en cada evaluación correlacionada, así que en teoría el mutante
    /// podría acertarle a las dos empresas a la vez.
    /// </summary>
    [Fact]
    public async Task LosListadosDeEmpresasYPuntosDeVentaLlevanLosNombresDeSusDuenios()
    {
        var a = await SembrarTenantAsync(
            nameof(LosListadosDeEmpresasYPuntosDeVentaLlevanLosNombresDeSusDuenios) + "-A",
            razonSocialEmpresa: "Norte SRL",
            nombrePuntoVenta: "Norte - Local 1");
        var b = await SembrarTenantAsync(
            nameof(LosListadosDeEmpresasYPuntosDeVentaLlevanLosNombresDeSusDuenios) + "-B",
            razonSocialEmpresa: "Sur SA",
            nombrePuntoVenta: "Sur - Local 1");

        var anexoDeA = await SembrarEmpresaAsync(a.Tenant.Id, "Norte Anexo SRL");
        var puntoDelAnexo = await SembrarPuntoVentaAsync(a.Tenant.Id, anexoDeA.Id, "Norte - Local 2");

        using var cliente = await ClienteComoRootAsync();

        var empresas = await cliente.GetFromJsonAsync<List<EmpresaListado>>("/api/empresas", OpcionesJson);
        Assert.NotNull(empresas);

        var empresaA = Assert.Single(empresas!, e => e.Id == a.Empresa.Id);
        var empresaB = Assert.Single(empresas!, e => e.Id == b.Empresa.Id);
        Assert.Equal(a.Tenant.Nombre, empresaA.NombreTenant);
        Assert.Equal(b.Tenant.Nombre, empresaB.NombreTenant);
        Assert.NotEqual(empresaA.NombreTenant, empresaB.NombreTenant);

        var puntos = await cliente.GetFromJsonAsync<List<PuntoVentaListado>>("/api/puntos-venta", OpcionesJson);
        Assert.NotNull(puntos);

        var puntoA = Assert.Single(puntos!, p => p.Id == a.PuntoVenta.Id);
        var puntoB = Assert.Single(puntos!, p => p.Id == b.PuntoVenta.Id);
        Assert.Equal(a.Tenant.Nombre, puntoA.NombreTenant);
        Assert.Equal("Norte SRL", puntoA.RazonSocialEmpresa);
        Assert.Equal(b.Tenant.Nombre, puntoB.NombreTenant);
        Assert.Equal("Sur SA", puntoB.RazonSocialEmpresa);

        // El hermano del MISMO dueño: mismo tenant, otra empresa. Un mutante que correlacionara
        // por tenant tiene que elegir una de las dos razones sociales y romper la otra aserción.
        var puntoAnexo = Assert.Single(puntos!, p => p.Id == puntoDelAnexo.Id);
        Assert.Equal(a.Tenant.Nombre, puntoAnexo.NombreTenant);
        Assert.Equal("Norte Anexo SRL", puntoAnexo.RazonSocialEmpresa);
        Assert.Equal(a.Tenant.Id, puntoAnexo.IdTenant);
        Assert.Equal(anexoDeA.Id, puntoAnexo.IdEmpresa);
        Assert.NotEqual(puntoA.RazonSocialEmpresa, puntoAnexo.RazonSocialEmpresa);
    }

    // ---- task 1.9: los contadores solo cuentan hijos vivos ------------------------------------

    /// <summary>
    /// Task 1.9 (TO-R2) — dos cláusulas bajo prueba: (a) el filtro <c>"BajaLogica"</c> que corre
    /// DENTRO de cada <c>Count</c> correlacionado, y (b) el <c>u.IdTenant == t.Id</c> sobre un
    /// <c>int?</c>, que no matchea contra <c>NULL</c> y por eso deja al personal de plataforma
    /// fuera de todo tenant. Se afirma que los contadores BAJAN, no que valen algo: un contador
    /// que ignorara la baja lógica devolvería los valores originales.
    ///
    /// Los tres contadores arrancan con valores distintos entre sí (2 empresas, 3 puntos de
    /// venta, 4 usuarios) para que un intercambio de argumentos posicionales muera acá.
    ///
    /// MUTACIÓN registrada: cambiar <c>db.Usuarios.Count(u => u.IdTenant == t.Id)</c> por
    /// <c>db.Usuarios.IgnoreQueryFilters(["BajaLogica"]).Count(...)</c> deja el contador de
    /// usuarios en 4 después de la baja y la prueba se cae.
    /// </summary>
    [Fact]
    public async Task LosContadoresDelTenantCuentanSoloHijosVivosYNuncaAlPersonalDePlataforma()
    {
        var a = await SembrarTenantAsync(
            nameof(LosContadoresDelTenantCuentanSoloHijosVivosYNuncaAlPersonalDePlataforma) + "-A");
        var b = await SembrarTenantAsync(
            nameof(LosContadoresDelTenantCuentanSoloHijosVivosYNuncaAlPersonalDePlataforma) + "-B");

        var segundaEmpresa = await SembrarEmpresaAsync(a.Tenant.Id, "Segunda empresa SRL");
        await SembrarPuntoVentaAsync(a.Tenant.Id, a.Empresa.Id, "Local 2");
        await SembrarPuntoVentaAsync(a.Tenant.Id, segundaEmpresa.Id, "Local 3");
        var vendedor = await SembrarUsuarioAsync(a.Tenant.Id, "vendedor", RolConocido.Vendedor);
        await SembrarUsuarioAsync(a.Tenant.Id, "supervisor", RolConocido.Supervisor);
        await SembrarUsuarioAsync(a.Tenant.Id, "cajero", RolConocido.Vendedor);

        using var cliente = await ClienteComoRootAsync();

        var antes = await LeerTenantAsync(cliente, a.Tenant.Id);
        Assert.Equal(2, antes.CantidadEmpresas);
        Assert.Equal(3, antes.CantidadPuntosVenta);
        Assert.Equal(4, antes.CantidadUsuarios);

        // El tenant hermano queda intacto: si algún Count perdiera su correlación, sus números
        // se contaminarían con los del tenant A.
        var hermano = await LeerTenantAsync(cliente, b.Tenant.Id);
        Assert.Equal(1, hermano.CantidadEmpresas);
        Assert.Equal(1, hermano.CantidadPuntosVenta);
        Assert.Equal(1, hermano.CantidadUsuarios);

        await DarDeBajaALaEmpresaAsync(segundaEmpresa.Id);
        await DarDeBajaAlUsuarioAsync(vendedor.Id);

        var despues = await LeerTenantAsync(cliente, a.Tenant.Id);
        Assert.Equal(1, despues.CantidadEmpresas);
        Assert.Equal(3, despues.CantidadPuntosVenta);
        Assert.Equal(3, despues.CantidadUsuarios);

        // Personal de plataforma: se siembra UNA cuenta con id_tenant IS NULL y se afirma que la
        // suma de cantidadUsuarios sobre TODO el listado no se movió. Una proyección que perdiera
        // la correlación (db.Usuarios.Count() a secas) subiría el contador de cada tenant y la
        // suma se movería en tantas unidades como tenants haya.
        var sumaAntes = await SumarUsuariosDeTodosLosTenantsAsync(cliente);

        await SembrarUsuarioDePlataformaAsync($"plataforma-{Guid.NewGuid():N}"[..38]);

        var sumaDespues = await SumarUsuariosDeTodosLosTenantsAsync(cliente);
        Assert.Equal(sumaAntes, sumaDespues);
        Assert.Equal(3, (await LeerTenantAsync(cliente, a.Tenant.Id)).CantidadUsuarios);
    }

    private static async Task<int> SumarUsuariosDeTodosLosTenantsAsync(HttpClient cliente)
    {
        var tenants = await cliente.GetFromJsonAsync<List<TenantListado>>("/api/plataforma/tenants", OpcionesJson);
        Assert.NotNull(tenants);
        Assert.NotEmpty(tenants!);
        return tenants!.Sum(t => t.CantidadUsuarios);
    }

    private static async Task<TenantListado> LeerTenantAsync(HttpClient cliente, int id)
    {
        var tenants = await cliente.GetFromJsonAsync<List<TenantListado>>("/api/plataforma/tenants", OpcionesJson);
        Assert.NotNull(tenants);
        return Assert.Single(tenants!, t => t.Id == id);
    }

    // ---- task 1.10: el caso huérfano ---------------------------------------------------------

    /// <summary>
    /// Task 1.10 (TO-R1, design D13) — la cláusula bajo prueba es que el nombre del dueño viaja
    /// como subconsulta escalar y NO como un JOIN: con el tenant dado de baja lógicamente, un
    /// INNER JOIN se llevaría puesta la fila de la empresa y el listado la escondería. Acá la
    /// empresa se sigue viendo, con <c>nombreTenant = null</c> — una anomalía visible en vez de
    /// una desaparición silenciosa, que es lo que hace que este slice sea correcto SIN el cascade
    /// del slice 4 y por lo tanto mergeable por su cuenta.
    ///
    /// MUTACIÓN registrada: reescribir la proyección como un <c>join</c> de LINQ contra
    /// <c>db.Tenants</c> hace desaparecer la empresa del listado y la prueba se cae en el
    /// <c>Assert.Single</c>.
    /// </summary>
    [Fact]
    public async Task UnaEmpresaCuyoTenantFueDadoDeBajaSigueApareciendoConNombreDeTenantNulo()
    {
        var huerfana = await SembrarTenantAsync(
            nameof(UnaEmpresaCuyoTenantFueDadoDeBajaSigueApareciendoConNombreDeTenantNulo) + "-A");
        var viva = await SembrarTenantAsync(
            nameof(UnaEmpresaCuyoTenantFueDadoDeBajaSigueApareciendoConNombreDeTenantNulo) + "-B");

        await DarDeBajaAlTenantAsync(huerfana.Tenant.Id);

        using var cliente = await ClienteComoRootAsync();

        var empresas = await cliente.GetFromJsonAsync<List<EmpresaListado>>("/api/empresas", OpcionesJson);
        Assert.NotNull(empresas);

        var empresaHuerfana = Assert.Single(empresas!, e => e.Id == huerfana.Empresa.Id);
        Assert.Equal(huerfana.Tenant.Id, empresaHuerfana.IdTenant);
        Assert.Null(empresaHuerfana.NombreTenant);

        // El hermano vivo sigue mostrando su nombre: la baja del otro tenant no apagó la
        // proyección entera.
        Assert.Equal(
            viva.Tenant.Nombre,
            Assert.Single(empresas!, e => e.Id == viva.Empresa.Id).NombreTenant);

        var puntos = await cliente.GetFromJsonAsync<List<PuntoVentaListado>>("/api/puntos-venta", OpcionesJson);
        Assert.NotNull(puntos);

        var puntoHuerfano = Assert.Single(puntos!, p => p.Id == huerfana.PuntoVenta.Id);
        Assert.Null(puntoHuerfano.NombreTenant);
        Assert.Equal(huerfana.Empresa.RazonSocial, puntoHuerfano.RazonSocialEmpresa);

        // Y el tenant dado de baja ya no está en su propio listado.
        var tenants = await cliente.GetFromJsonAsync<List<TenantListado>>("/api/plataforma/tenants", OpcionesJson);
        Assert.NotNull(tenants);
        Assert.DoesNotContain(tenants!, t => t.Id == huerfana.Tenant.Id);
    }

    // ---- task 1.11: readback posicional completo ---------------------------------------------

    /// <summary>
    /// Task 1.11 (<c>mutation-proof-tests</c> regla 12b) — cada campo POSICIONAL de los cuatro
    /// DTOs de listado se lee de vuelta con valores distintos entre sí, para que un argumento
    /// intercambiado en el constructor posicional muera acá en vez de sobrevivir sobre valores
    /// iguales. Los tres contadores del tenant son <see cref="EmpresasDelTenantLeido"/>,
    /// <see cref="PuntosVentaDelTenantLeido"/> y <see cref="UsuariosDelTenantLeido"/>, y se afirma
    /// que el <c>Id</c> no coincide con ninguno de los tres para que un swap contra un contador
    /// tampoco pueda pasar. La cota la garantiza el relleno que siembra la propia prueba
    /// (<see cref="TenantsDeRelleno"/>), no ninguna fila del seed de producción.
    /// </summary>
    [Fact]
    public async Task CadaCampoPosicionalDeLosCuatroListadosSeLeeDeVueltaConValoresDistintos()
    {
        var sufijo = Guid.NewGuid().ToString("N")[..8];

        // Relleno DESBALANCEADO antes del tenant que se lee: cada tenant de relleno se lleva una
        // cantidad distinta de empresas, puntos de venta y usuarios, así las cuatro secuencias de
        // identidad se desincronizan entre sí. Sin esto, id, idTenant e idEmpresa avanzarían en
        // lockstep y podrían COINCIDIR, que es exactamente el caso en el que un intercambio de
        // argumentos posicionales sobrevive (mutation-proof-tests regla 12b). Además empuja el id
        // del tenant bajo prueba por encima de los tres contadores, de forma determinística y sin
        // depender del orden en que xUnit haya corrido el resto de la clase.
        for (var i = 0; i < TenantsDeRelleno; i++)
        {
            var relleno = await SembrarTenantAsync($"Readback-relleno-{i}-{sufijo}");

            for (var j = 0; j < 2; j++)
            {
                var empresaRelleno = await SembrarEmpresaAsync(
                    relleno.Tenant.Id, $"Relleno {i}-{j} {sufijo} SRL");
                await SembrarPuntoVentaAsync(
                    relleno.Tenant.Id, empresaRelleno.Id, $"Relleno local {i}-{j} {sufijo}");
                await SembrarUsuarioAsync(
                    relleno.Tenant.Id, $"relleno-{i}-{j}-{sufijo}", RolConocido.Vendedor);
            }

            await SembrarPuntoVentaAsync(
                relleno.Tenant.Id, relleno.Empresa.Id, $"Relleno local extra {i} {sufijo}");
        }

        var a = await SembrarTenantAsync(
            $"Readback-A-{sufijo}",
            razonSocialEmpresa: $"Readback empresa uno {sufijo} SRL",
            nombrePuntoVenta: $"Readback local uno {sufijo}",
            nombreUsuarioAdmin: $"readback-admin-{sufijo}");
        await SembrarTenantAsync(
            $"Readback-B-{sufijo}",
            razonSocialEmpresa: $"Readback empresa dos {sufijo} SA",
            nombrePuntoVenta: $"Readback local dos {sufijo}");

        var segundaEmpresa = await SembrarEmpresaAsync(a.Tenant.Id, $"Readback empresa tres {sufijo} SRL");
        await SembrarPuntoVentaAsync(a.Tenant.Id, a.Empresa.Id, $"Readback local tres {sufijo}");
        await SembrarPuntoVentaAsync(a.Tenant.Id, segundaEmpresa.Id, $"Readback local cuatro {sufijo}");
        await SembrarUsuarioAsync(a.Tenant.Id, $"readback-uno-{sufijo}", RolConocido.Vendedor);
        await SembrarUsuarioAsync(a.Tenant.Id, $"readback-dos-{sufijo}", RolConocido.Supervisor);
        await SembrarUsuarioAsync(a.Tenant.Id, $"readback-tres-{sufijo}", RolConocido.Vendedor);

        using var cliente = await ClienteComoRootAsync();

        // Datos descriptivos distintos campo a campo, por PUT, para que ninguno quede en null.
        var puntoEditado = await cliente.PutAsJsonAsync(
            $"/api/puntos-venta/{a.PuntoVenta.Id}",
            new PuntoVentaEdicion(
                $"Readback local uno {sufijo}",
                $"Domicilio {sufijo}",
                $"Horario {sufijo}",
                $"Whatsapp {sufijo}",
                $"Instagram {sufijo}",
                $"Facebook {sufijo}",
                $"Web {sufijo}"));
        Assert.Equal(HttpStatusCode.OK, puntoEditado.StatusCode);

        var empresaEditada = await cliente.PutAsJsonAsync(
            $"/api/empresas/{a.Empresa.Id}",
            new EmpresaEdicion($"Readback empresa uno {sufijo} SRL", $"Fantasia {sufijo}", "20-11111111-1"));
        Assert.Equal(HttpStatusCode.OK, empresaEditada.StatusCode);

        // --- TenantListado: 7 campos ---
        var tenant = await LeerTenantAsync(cliente, a.Tenant.Id);
        Assert.Equal(a.Tenant.Id, tenant.Id);
        Assert.DoesNotContain(tenant.Id, ContadoresSembrados);
        Assert.Equal($"Readback-A-{sufijo}", tenant.Nombre);
        Assert.Equal(EstadoTenant.Activo, tenant.Estado);
        // timestamptz redondea a microsegundos, así que la igualdad exacta contra el valor en
        // memoria sería frágil; lo que este campo tiene que probar es que NO se cruzó con otro.
        Assert.True(
            (tenant.CreatedAt - a.Tenant.CreatedAt).Duration() < TimeSpan.FromSeconds(1),
            "createdAt tiene que ser el del tenant sembrado");
        Assert.Equal(EmpresasDelTenantLeido, tenant.CantidadEmpresas);
        Assert.Equal(PuntosVentaDelTenantLeido, tenant.CantidadPuntosVenta);
        Assert.Equal(UsuariosDelTenantLeido, tenant.CantidadUsuarios);

        // --- EmpresaListado: 6 campos ---
        var empresas = await cliente.GetFromJsonAsync<List<EmpresaListado>>("/api/empresas", OpcionesJson);
        Assert.NotNull(empresas);
        var empresa = Assert.Single(empresas!, e => e.Id == a.Empresa.Id);
        Assert.Equal(a.Empresa.Id, empresa.Id);
        Assert.Equal(a.Tenant.Id, empresa.IdTenant);
        Assert.NotEqual(empresa.Id, empresa.IdTenant);
        Assert.Equal($"Readback empresa uno {sufijo} SRL", empresa.RazonSocial);
        Assert.Equal($"Fantasia {sufijo}", empresa.NombreFantasia);
        Assert.Equal("20-11111111-1", empresa.Cuit);
        Assert.Equal($"Readback-A-{sufijo}", empresa.NombreTenant);

        // --- PuntoVentaListado: 12 campos ---
        var puntos = await cliente.GetFromJsonAsync<List<PuntoVentaListado>>("/api/puntos-venta", OpcionesJson);
        Assert.NotNull(puntos);
        var punto = Assert.Single(puntos!, p => p.Id == a.PuntoVenta.Id);
        Assert.Equal(a.PuntoVenta.Id, punto.Id);
        Assert.Equal(a.Tenant.Id, punto.IdTenant);
        Assert.Equal(a.Empresa.Id, punto.IdEmpresa);
        Assert.NotEqual(punto.Id, punto.IdTenant);
        Assert.NotEqual(punto.Id, punto.IdEmpresa);
        Assert.NotEqual(punto.IdTenant, punto.IdEmpresa);
        Assert.Equal($"Readback local uno {sufijo}", punto.Nombre);
        Assert.Equal($"Domicilio {sufijo}", punto.Domicilio);
        Assert.Equal($"Horario {sufijo}", punto.Horario);
        Assert.Equal($"Whatsapp {sufijo}", punto.Whatsapp);
        Assert.Equal($"Instagram {sufijo}", punto.Instagram);
        Assert.Equal($"Facebook {sufijo}", punto.Facebook);
        Assert.Equal($"Web {sufijo}", punto.Web);
        Assert.Equal($"Readback-A-{sufijo}", punto.NombreTenant);
        Assert.Equal($"Readback empresa uno {sufijo} SRL", punto.RazonSocialEmpresa);

        // --- UsuarioListado: 10 campos ---
        var pagina = await cliente.GetFromJsonAsync<PaginaDe<UsuarioListado>>(
            $"/api/usuarios?busqueda=readback-admin-{sufijo}&tamanio=200", OpcionesJson);
        Assert.NotNull(pagina);
        var usuario = Assert.Single(pagina!.Items, u => u.Id == a.Admin.Id);
        Assert.Equal(a.Admin.Id, usuario.Id);
        Assert.NotEqual(usuario.Id, usuario.IdTenant);
        Assert.NotEqual(usuario.Id, usuario.RolId);
        Assert.Equal($"readback-admin-{sufijo}", usuario.Usuario);
        Assert.Equal(a.Admin.Mail, usuario.Mail);
        Assert.Equal((int)RolConocido.Admin, usuario.RolId);
        Assert.Equal("admin", usuario.Rol);
        Assert.Equal(EstadoUsuario.Activo, usuario.Estado);
        Assert.Null(usuario.UltimaConexion);
        Assert.NotEqual(default, usuario.CreatedAt);
        Assert.Equal(a.Tenant.Id, usuario.IdTenant);
        Assert.Equal($"Readback-A-{sufijo}", usuario.NombreTenant);
    }

    // ---- task 1.12: identidad de tenant en el listado de usuarios ----------------------------

    /// <summary>
    /// Task 1.12 (UT-R1, S1) — la cláusula bajo prueba es que la API NUNCA fabrica la etiqueta
    /// <c>"Plataforma"</c> (design D14): esa copia es de pantalla y la pone la web en el slice 2.
    /// El nombre de un tenant es texto libre, así que un tenant llamado justo "Plataforma" sería
    /// indistinguible de una cuenta de plataforma si el servidor la inventara. Se afirma el valor
    /// discriminante (<c>mutation-proof-tests</c> regla 4): la cuenta de plataforma trae los dos
    /// campos en <c>null</c>, ninguna fila trae el literal, y ninguna cuenta sin tenant trae
    /// nombre. La recíproca de S1 queda deliberadamente sin afirmar — ver el comentario al pie.
    /// </summary>
    [Fact]
    public async Task ElListadoDeUsuariosLlevaElTenantDeCadaCuentaYNuncaFabricaLaEtiquetaPlataforma()
    {
        var a = await SembrarTenantAsync(
            nameof(ElListadoDeUsuariosLlevaElTenantDeCadaCuentaYNuncaFabricaLaEtiquetaPlataforma) + "-A");
        var b = await SembrarTenantAsync(
            nameof(ElListadoDeUsuariosLlevaElTenantDeCadaCuentaYNuncaFabricaLaEtiquetaPlataforma) + "-B");

        using var cliente = await ClienteComoRootAsync();

        var respuesta = await cliente.GetAsync("/api/usuarios?tamanio=200");
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);

        var pagina = await respuesta.Content.ReadFromJsonAsync<PaginaDe<UsuarioListado>>(OpcionesJson);
        Assert.NotNull(pagina);

        var adminA = Assert.Single(pagina!.Items, u => u.Id == a.Admin.Id);
        Assert.Equal(a.Tenant.Id, adminA.IdTenant);
        Assert.Equal(a.Tenant.Nombre, adminA.NombreTenant);

        var adminB = Assert.Single(pagina.Items, u => u.Id == b.Admin.Id);
        Assert.Equal(b.Tenant.Id, adminB.IdTenant);
        Assert.Equal(b.Tenant.Nombre, adminB.NombreTenant);
        Assert.NotEqual(adminA.NombreTenant, adminB.NombreTenant);

        var root = Assert.Single(pagina.Items, u => u.Mail == MailRoot);
        Assert.Null(root.IdTenant);
        Assert.Null(root.NombreTenant);

        // El valor discriminante: la API no manda la etiqueta, la manda la web (D14).
        Assert.DoesNotContain(pagina.Items, u => u.NombreTenant == "Plataforma");

        // La dirección que S1 le exige a la API: NINGUNA cuenta de plataforma trae nombre. La
        // recíproca ("nombre nulo implica cuenta de plataforma") NO se afirma a propósito: D13
        // reserva el nombre nulo también para el huérfano —una cuenta cuyo tenant quedó dado de
        // baja—, que es justamente la anomalía que este slice elige mostrar en vez de esconder.
        Assert.All(
            pagina.Items.Where(u => u.IdTenant is null),
            u => Assert.Null(u.NombreTenant));
    }

    // ---- task 1.13: regresión de alcance -----------------------------------------------------

    /// <summary>
    /// Task 1.13 (UT-R1) — regresión: agregar el nombre del tenant a la proyección no abrió un
    /// canal de enumeración. Un admin de tenant sigue viendo solo sus propias cuentas y el nombre
    /// del otro tenant no aparece NI en el listado NI en el JSON crudo. El nombre del tenant
    /// vecino se elige único (un GUID) para que la aserción sobre el cuerpo crudo discrimine de
    /// verdad.
    /// </summary>
    [Fact]
    public async Task UnAdminDeTenantSoloVeSusCuentasYNuncaElNombreDeOtroTenant()
    {
        var sufijo = Guid.NewGuid().ToString("N")[..8];
        var a = await SembrarTenantAsync($"Alcance-A-{sufijo}");
        var b = await SembrarTenantAsync($"Alcance-vecino-{sufijo}");

        using var cliente = await ClienteComoAdminAsync(a.Admin.Mail);

        var respuesta = await cliente.GetAsync("/api/usuarios?tamanio=200");
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);

        var crudo = await respuesta.Content.ReadAsStringAsync();
        var pagina = JsonSerializer.Deserialize<PaginaDe<UsuarioListado>>(crudo, OpcionesJson);
        Assert.NotNull(pagina);

        Assert.NotEmpty(pagina!.Items);
        Assert.All(pagina.Items, u =>
        {
            Assert.Equal(a.Tenant.Id, u.IdTenant);
            Assert.Equal(a.Tenant.Nombre, u.NombreTenant);
        });

        Assert.DoesNotContain(pagina.Items, u => u.Id == b.Admin.Id);
        Assert.DoesNotContain($"Alcance-vecino-{sufijo}", crudo, StringComparison.Ordinal);
    }

    // ---- ronda 1 de judgment-day: el huérfano en los TRES caminos de usuarios ----------------

    /// <summary>
    /// La cláusula bajo prueba es el <c>t.DeletedAt == null</c> EXPLÍCITO de la subconsulta de
    /// <c>ServicioDeUsuarios.ListarAsync</c>. EF aplica <c>IgnoreQueryFilters</c> a nivel CONSULTA,
    /// así que con <c>incluirEliminados=true</c> la subconsulta correlacionada perdía también el
    /// filtro de baja lógica: la MISMA cuenta traía el nombre del tenant dado de baja en ese
    /// camino y <c>null</c> en el listado por defecto y en el detalle. Se afirman los tres.
    ///
    /// El hermano vivo se afirma en el mismo request: un predicado que apagara la proyección
    /// entera también pasaría la aserción de nulidad, así que sola no discrimina.
    ///
    /// MUTACIÓN registrada: sacar <c>&amp;&amp; t.DeletedAt == null</c> de la subconsulta deja el
    /// camino <c>incluirEliminados=true</c> devolviendo el nombre del tenant y la prueba se cae.
    /// </summary>
    [Fact]
    public async Task UnaCuentaCuyoTenantFueDadoDeBajaNoTraeNombreDeTenantEnNingunoDeLosTresCaminos()
    {
        var sufijo = Guid.NewGuid().ToString("N")[..8];

        var huerfana = await SembrarTenantAsync(
            $"Huerfano-A-{sufijo}", nombreUsuarioAdmin: $"huerfano-a-{sufijo}");
        var viva = await SembrarTenantAsync(
            $"Huerfano-B-{sufijo}", nombreUsuarioAdmin: $"huerfano-b-{sufijo}");

        await DarDeBajaAlTenantAsync(huerfana.Tenant.Id);

        using var cliente = await ClienteComoRootAsync();

        var porDefecto = await LeerCuerpoAsync<PaginaDe<UsuarioListado>>(
            await cliente.GetAsync($"/api/usuarios?busqueda={sufijo}&tamanio=200"));
        AfirmarHuerfano(Assert.Single(porDefecto.Items, u => u.Id == huerfana.Admin.Id));
        AfirmarVivo(Assert.Single(porDefecto.Items, u => u.Id == viva.Admin.Id));

        var conEliminados = await LeerCuerpoAsync<PaginaDe<UsuarioListado>>(
            await cliente.GetAsync($"/api/usuarios?busqueda={sufijo}&incluirEliminados=true&tamanio=200"));
        AfirmarHuerfano(Assert.Single(conEliminados.Items, u => u.Id == huerfana.Admin.Id));
        AfirmarVivo(Assert.Single(conEliminados.Items, u => u.Id == viva.Admin.Id));

        AfirmarHuerfano(await LeerCuerpoAsync<UsuarioListado>(
            await cliente.GetAsync($"/api/usuarios/{huerfana.Admin.Id}")));

        void AfirmarHuerfano(UsuarioListado cuenta)
        {
            Assert.Equal(huerfana.Tenant.Id, cuenta.IdTenant);
            Assert.Null(cuenta.NombreTenant);
        }

        void AfirmarVivo(UsuarioListado cuenta)
        {
            Assert.Equal(viva.Tenant.Id, cuenta.IdTenant);
            Assert.Equal($"Huerfano-B-{sufijo}", cuenta.NombreTenant);
        }
    }

    // ---- ronda 2 de judgment-day: el huérfano en TODOS los caminos de organización ------------

    /// <summary>
    /// Espejo del lado de organización de
    /// <see cref="UnaCuentaCuyoTenantFueDadoDeBajaNoTraeNombreDeTenantEnNingunoDeLosTresCaminos"/>:
    /// las cláusulas bajo prueba son los <c>DeletedAt == null</c> EXPLÍCITOS de las tres
    /// subconsultas de dueño (tenant de empresa, tenant de punto de venta, empresa de punto de
    /// venta). Se afirma que el huérfano se rinde igual por el LISTADO y por el DETALLE —la fila
    /// se sigue viendo con el nombre del dueño en <c>null</c>, design D13— y que el hermano vivo
    /// trae su nombre en el mismo request: un predicado que apagara la proyección entera también
    /// pasaría la aserción de nulidad, así que sola no discrimina.
    ///
    /// SIN evidencia de mutación, y se dice en vez de fingirla. Las tres mutaciones se CORRIERON,
    /// no se razonaron: borrando un predicado por vez y corriendo la clase entera, las tres
    /// SOBREVIVIERON (12/12 en verde cada una). Es el resultado esperado: hoy ningún camino de
    /// <c>ServicioDeOrganizacion</c> apaga el filtro ambiente <c>"BajaLogica"</c> —no hay
    /// <c>incluirEliminados</c> ni <c>IgnoreQueryFilters</c> en el servicio—, así que el filtro
    /// produce el mismo resultado y la cláusula todavía no tiene nada propio que probar. Los
    /// predicados son defensa en profundidad para el slice 4, que agrega los escritores de baja
    /// sobre estas mismas entidades; lo que esta prueba fija hoy es el COMPORTAMIENTO esperado,
    /// para que ese slice tenga contra qué mutar.
    /// </summary>
    [Fact]
    public async Task LosHuerfanosDeOrganizacionSeRindenIgualPorElListadoYPorElDetalle()
    {
        var sufijo = Guid.NewGuid().ToString("N")[..8];

        var huerfana = await SembrarTenantAsync($"Org-huerfano-A-{sufijo}");
        var viva = await SembrarTenantAsync($"Org-huerfano-B-{sufijo}");

        // Empresa dada de baja con su punto de venta vivo: es el único huérfano que prueba la
        // tercera subconsulta (empresa de punto de venta), que no depende de la baja del tenant.
        var empresaSinDuenio = await SembrarEmpresaAsync(viva.Tenant.Id, $"Org anexo {sufijo} SRL");
        var puntoSinEmpresa = await SembrarPuntoVentaAsync(
            viva.Tenant.Id, empresaSinDuenio.Id, $"Org local sin empresa {sufijo}");

        await DarDeBajaAlTenantAsync(huerfana.Tenant.Id);
        await DarDeBajaALaEmpresaAsync(empresaSinDuenio.Id);

        using var cliente = await ClienteComoRootAsync();

        var empresas = await cliente.GetFromJsonAsync<List<EmpresaListado>>("/api/empresas", OpcionesJson);
        Assert.NotNull(empresas);
        AfirmarEmpresaHuerfana(Assert.Single(empresas!, e => e.Id == huerfana.Empresa.Id));
        Assert.Equal(
            viva.Tenant.Nombre,
            Assert.Single(empresas!, e => e.Id == viva.Empresa.Id).NombreTenant);

        AfirmarEmpresaHuerfana(await LeerCuerpoAsync<EmpresaListado>(
            await cliente.GetAsync($"/api/empresas/{huerfana.Empresa.Id}")));

        var puntos = await cliente.GetFromJsonAsync<List<PuntoVentaListado>>("/api/puntos-venta", OpcionesJson);
        Assert.NotNull(puntos);
        AfirmarPuntoSinTenant(Assert.Single(puntos!, p => p.Id == huerfana.PuntoVenta.Id));
        AfirmarPuntoSinEmpresa(Assert.Single(puntos!, p => p.Id == puntoSinEmpresa.Id));
        Assert.Equal(
            viva.Tenant.Nombre,
            Assert.Single(puntos!, p => p.Id == viva.PuntoVenta.Id).NombreTenant);

        AfirmarPuntoSinTenant(await LeerCuerpoAsync<PuntoVentaListado>(
            await cliente.GetAsync($"/api/puntos-venta/{huerfana.PuntoVenta.Id}")));
        AfirmarPuntoSinEmpresa(await LeerCuerpoAsync<PuntoVentaListado>(
            await cliente.GetAsync($"/api/puntos-venta/{puntoSinEmpresa.Id}")));

        void AfirmarEmpresaHuerfana(EmpresaListado empresa)
        {
            Assert.Equal(huerfana.Tenant.Id, empresa.IdTenant);
            Assert.Null(empresa.NombreTenant);
            Assert.Equal(huerfana.Empresa.RazonSocial, empresa.RazonSocial);
        }

        void AfirmarPuntoSinTenant(PuntoVentaListado punto)
        {
            Assert.Equal(huerfana.Tenant.Id, punto.IdTenant);
            Assert.Null(punto.NombreTenant);
            // La empresa sigue viva: el nombre que falta es solo el del tenant.
            Assert.Equal(huerfana.Empresa.RazonSocial, punto.RazonSocialEmpresa);
        }

        void AfirmarPuntoSinEmpresa(PuntoVentaListado punto)
        {
            Assert.Equal(empresaSinDuenio.Id, punto.IdEmpresa);
            Assert.Null(punto.RazonSocialEmpresa);
            // El tenant sigue vivo: el nombre que falta es solo el de la empresa.
            Assert.Equal(viva.Tenant.Nombre, punto.NombreTenant);
        }
    }

    // ---- ronda 1 de judgment-day: readback de los caminos que NO son el listado ---------------

    /// <summary>
    /// Lo que agrega esta prueba es el CABLEADO DE LOS ENDPOINTS que no son el listado. El cuerpo
    /// de <c>ProyeccionDeTenant</c> ya lo lee de vuelta campo a campo la task 1.11 a través de
    /// <c>GET /api/plataforma/tenants</c>, así que vaciarlo NO sobrevive a la suite. Lo que ningún
    /// test cubría es que <c>GET {id}</c>, <c>PUT</c>, <c>suspender</c> y <c>reactivar</c> devuelvan
    /// ESE record proyectado y no uno armado aparte —con los contadores en cero, el nombre viejo o
    /// el estado anterior—: acá se afirman los siete campos por los cuatro caminos.
    ///
    /// SIN evidencia de mutación, y se dice en vez de fingirla: la tabla M no le adjudica ninguna.
    /// Los tres contadores valen 2, 3 y 4 —distintos entre sí y distintos del id— para que un
    /// intercambio posicional (<c>mutation-proof-tests</c> regla 12b) muera acá igual.
    /// </summary>
    [Fact]
    public async Task ElDetalleYLasEscriturasDeTenantDevuelvenLosSieteCamposProyectados()
    {
        var sufijo = Guid.NewGuid().ToString("N")[..8];
        await SembrarRellenoDesbalanceadoAsync(sufijo);

        var a = await SembrarTenantAsync(
            $"Detalle-tenant-{sufijo}", nombreUsuarioAdmin: $"det-admin-{sufijo}");
        var segundaEmpresa = await SembrarEmpresaAsync(a.Tenant.Id, $"Detalle empresa dos {sufijo} SRL");
        await SembrarPuntoVentaAsync(a.Tenant.Id, a.Empresa.Id, $"Detalle local dos {sufijo}");
        await SembrarPuntoVentaAsync(a.Tenant.Id, segundaEmpresa.Id, $"Detalle local tres {sufijo}");
        await SembrarUsuarioAsync(a.Tenant.Id, $"det-uno-{sufijo}", RolConocido.Vendedor);
        await SembrarUsuarioAsync(a.Tenant.Id, $"det-dos-{sufijo}", RolConocido.Supervisor);
        await SembrarUsuarioAsync(a.Tenant.Id, $"det-tres-{sufijo}", RolConocido.Vendedor);

        using var cliente = await ClienteComoRootAsync();

        AfirmarTenant(
            await LeerCuerpoAsync<TenantListado>(
                await cliente.GetAsync($"/api/plataforma/tenants/{a.Tenant.Id}")),
            $"Detalle-tenant-{sufijo}",
            EstadoTenant.Activo);

        AfirmarTenant(
            await LeerCuerpoAsync<TenantListado>(
                await cliente.PutAsJsonAsync(
                    $"/api/plataforma/tenants/{a.Tenant.Id}",
                    new TenantEdicion($"Detalle-tenant-editado-{sufijo}"))),
            $"Detalle-tenant-editado-{sufijo}",
            EstadoTenant.Activo);

        AfirmarTenant(
            await LeerCuerpoAsync<TenantListado>(
                await cliente.PostAsync($"/api/plataforma/tenants/{a.Tenant.Id}/suspender", null)),
            $"Detalle-tenant-editado-{sufijo}",
            EstadoTenant.Suspendido);

        AfirmarTenant(
            await LeerCuerpoAsync<TenantListado>(
                await cliente.PostAsync($"/api/plataforma/tenants/{a.Tenant.Id}/reactivar", null)),
            $"Detalle-tenant-editado-{sufijo}",
            EstadoTenant.Activo);

        void AfirmarTenant(TenantListado tenant, string nombreEsperado, EstadoTenant estadoEsperado)
        {
            Assert.Equal(a.Tenant.Id, tenant.Id);
            Assert.DoesNotContain(tenant.Id, ContadoresSembrados);
            Assert.Equal(nombreEsperado, tenant.Nombre);
            Assert.Equal(estadoEsperado, tenant.Estado);
            Assert.True(
                (tenant.CreatedAt - a.Tenant.CreatedAt).Duration() < TimeSpan.FromSeconds(1),
                "createdAt tiene que ser el del tenant sembrado");
            Assert.Equal(EmpresasDelTenantLeido, tenant.CantidadEmpresas);
            Assert.Equal(PuntosVentaDelTenantLeido, tenant.CantidadPuntosVenta);
            Assert.Equal(UsuariosDelTenantLeido, tenant.CantidadUsuarios);
        }
    }

    /// <summary>Mismo criterio que la prueba anterior, sobre <c>EmpresaListado</c> (6 campos) y
    /// <c>PuntoVentaListado</c> (12): detalle y edición devuelven todos los campos con su verdad,
    /// con <c>id</c>, <c>idTenant</c> e <c>idEmpresa</c> distintos entre sí por construcción.</summary>
    [Fact]
    public async Task ElDetalleYLaEdicionDeEmpresaYPuntoDeVentaDevuelvenTodosLosCamposProyectados()
    {
        var sufijo = Guid.NewGuid().ToString("N")[..8];
        await SembrarRellenoDesbalanceadoAsync(sufijo);

        var a = await SembrarTenantAsync(
            $"Detalle-org-{sufijo}",
            razonSocialEmpresa: $"Detalle org empresa {sufijo} SRL",
            nombrePuntoVenta: $"Detalle org local {sufijo}");

        using var cliente = await ClienteComoRootAsync();

        AfirmarEmpresa(await LeerCuerpoAsync<EmpresaListado>(
            await cliente.PutAsJsonAsync(
                $"/api/empresas/{a.Empresa.Id}",
                new EmpresaEdicion(
                    $"Detalle org empresa {sufijo} SRL", $"Fantasia {sufijo}", "20-22222222-2"))));

        AfirmarEmpresa(await LeerCuerpoAsync<EmpresaListado>(
            await cliente.GetAsync($"/api/empresas/{a.Empresa.Id}")));

        AfirmarPuntoVenta(await LeerCuerpoAsync<PuntoVentaListado>(
            await cliente.PutAsJsonAsync(
                $"/api/puntos-venta/{a.PuntoVenta.Id}",
                new PuntoVentaEdicion(
                    $"Detalle org local {sufijo}",
                    $"Domicilio {sufijo}",
                    $"Horario {sufijo}",
                    $"Whatsapp {sufijo}",
                    $"Instagram {sufijo}",
                    $"Facebook {sufijo}",
                    $"Web {sufijo}"))));

        AfirmarPuntoVenta(await LeerCuerpoAsync<PuntoVentaListado>(
            await cliente.GetAsync($"/api/puntos-venta/{a.PuntoVenta.Id}")));

        void AfirmarEmpresa(EmpresaListado empresa)
        {
            Assert.Equal(a.Empresa.Id, empresa.Id);
            Assert.Equal(a.Tenant.Id, empresa.IdTenant);
            Assert.NotEqual(empresa.Id, empresa.IdTenant);
            Assert.Equal($"Detalle org empresa {sufijo} SRL", empresa.RazonSocial);
            Assert.Equal($"Fantasia {sufijo}", empresa.NombreFantasia);
            Assert.Equal("20-22222222-2", empresa.Cuit);
            Assert.Equal($"Detalle-org-{sufijo}", empresa.NombreTenant);
        }

        void AfirmarPuntoVenta(PuntoVentaListado punto)
        {
            Assert.Equal(a.PuntoVenta.Id, punto.Id);
            Assert.Equal(a.Tenant.Id, punto.IdTenant);
            Assert.Equal(a.Empresa.Id, punto.IdEmpresa);
            Assert.NotEqual(punto.Id, punto.IdTenant);
            Assert.NotEqual(punto.Id, punto.IdEmpresa);
            Assert.NotEqual(punto.IdTenant, punto.IdEmpresa);
            Assert.Equal($"Detalle org local {sufijo}", punto.Nombre);
            Assert.Equal($"Domicilio {sufijo}", punto.Domicilio);
            Assert.Equal($"Horario {sufijo}", punto.Horario);
            Assert.Equal($"Whatsapp {sufijo}", punto.Whatsapp);
            Assert.Equal($"Instagram {sufijo}", punto.Instagram);
            Assert.Equal($"Facebook {sufijo}", punto.Facebook);
            Assert.Equal($"Web {sufijo}", punto.Web);
            Assert.Equal($"Detalle-org-{sufijo}", punto.NombreTenant);
            Assert.Equal($"Detalle org empresa {sufijo} SRL", punto.RazonSocialEmpresa);
        }
    }

    /// <summary>Los tres caminos de <c>UsuarioListado</c> que no son el listado: el 201 del alta,
    /// el detalle y el cuerpo del PUT — los tres devuelven el record de
    /// <c>ServicioDeUsuarios.ObtenerAsync</c>, que era el que sobrevivía a un
    /// <c>NombreDeTenantAsync</c> reemplazado por <c>return null;</c>.</summary>
    [Fact]
    public async Task ElAltaElDetalleYLaEdicionDeUsuarioDevuelvenLosDiezCamposProyectados()
    {
        var sufijo = Guid.NewGuid().ToString("N")[..8];
        await SembrarRellenoDesbalanceadoAsync(sufijo);

        var a = await SembrarTenantAsync($"Detalle-usuario-{sufijo}");

        using var cliente = await ClienteComoRootAsync();

        var mailAlta = $"alta-{sufijo}@ways.test";
        var creado = await LeerCuerpoAsync<UsuarioListado>(
            await cliente.PostAsJsonAsync(
                "/api/usuarios",
                new CrearUsuario(
                    $"alta-{sufijo}", mailAlta, (int)RolConocido.Supervisor, Password,
                    EstadoUsuario.Activo, a.Tenant.Id)),
            HttpStatusCode.Created);

        AfirmarUsuario(creado, $"alta-{sufijo}", mailAlta, RolConocido.Supervisor, "supervisor", EstadoUsuario.Activo);

        AfirmarUsuario(
            await LeerCuerpoAsync<UsuarioListado>(await cliente.GetAsync($"/api/usuarios/{creado.Id}")),
            $"alta-{sufijo}", mailAlta, RolConocido.Supervisor, "supervisor", EstadoUsuario.Activo);

        var mailEditado = $"editado-{sufijo}@ways.test";
        AfirmarUsuario(
            await LeerCuerpoAsync<UsuarioListado>(
                await cliente.PutAsJsonAsync(
                    $"/api/usuarios/{creado.Id}",
                    new ActualizarUsuario(
                        $"editado-{sufijo}", mailEditado, (int)RolConocido.Vendedor,
                        EstadoUsuario.Inactivo))),
            $"editado-{sufijo}", mailEditado, RolConocido.Vendedor, "vendedor", EstadoUsuario.Inactivo);

        void AfirmarUsuario(
            UsuarioListado usuario,
            string nombre,
            string mail,
            RolConocido rol,
            string nombreRol,
            EstadoUsuario estado)
        {
            Assert.Equal(creado.Id, usuario.Id);
            Assert.Equal(nombre, usuario.Usuario);
            Assert.Equal(mail, usuario.Mail);
            Assert.Equal((int)rol, usuario.RolId);
            Assert.Equal(nombreRol, usuario.Rol);
            Assert.Equal(estado, usuario.Estado);
            Assert.Null(usuario.UltimaConexion);
            Assert.NotEqual(default, usuario.CreatedAt);
            Assert.Equal(a.Tenant.Id, usuario.IdTenant);
            Assert.Equal($"Detalle-usuario-{sufijo}", usuario.NombreTenant);
            Assert.NotEqual(usuario.Id, usuario.IdTenant);
            Assert.NotEqual(usuario.Id, usuario.RolId);
        }
    }

    /// <summary>Relleno DESBALANCEADO, mismo criterio que la task 1.11: cada tenant de relleno se
    /// lleva una cantidad distinta de empresas, puntos de venta y usuarios, así las cuatro
    /// secuencias de identidad se desincronizan y <c>id</c>, <c>idTenant</c> e <c>idEmpresa</c>
    /// quedan distintos POR CONSTRUCCIÓN, no por el orden en que xUnit corrió la clase. Son
    /// <see cref="TenantsDeRelleno"/> tenants para que además el id del tenant que se lee quede
    /// por encima del mayor de los tres contadores.</summary>
    private async Task SembrarRellenoDesbalanceadoAsync(string sufijo)
    {
        for (var i = 0; i < TenantsDeRelleno; i++)
        {
            var relleno = await SembrarTenantAsync($"Relleno-{i}-{sufijo}");

            for (var j = 0; j < 2; j++)
            {
                var empresaRelleno = await SembrarEmpresaAsync(
                    relleno.Tenant.Id, $"Relleno {i}-{j} {sufijo} SRL");
                await SembrarPuntoVentaAsync(
                    relleno.Tenant.Id, empresaRelleno.Id, $"Relleno local {i}-{j} {sufijo}");
                await SembrarUsuarioAsync(
                    relleno.Tenant.Id, $"relleno-{i}-{j}-{sufijo}", RolConocido.Vendedor);
            }

            await SembrarPuntoVentaAsync(
                relleno.Tenant.Id, relleno.Empresa.Id, $"Relleno local extra {i} {sufijo}");
        }
    }

    private static async Task<T> LeerCuerpoAsync<T>(
        HttpResponseMessage respuesta, HttpStatusCode esperado = HttpStatusCode.OK)
    {
        Assert.Equal(esperado, respuesta.StatusCode);

        var cuerpo = await respuesta.Content.ReadFromJsonAsync<T>(OpcionesJson);
        Assert.NotNull(cuerpo);

        return cuerpo!;
    }

    // ---- etapa 20 slice 4: los siete predicados, MUERTOS por debajo del confound ---------------

    /// <summary>
    /// LA REGLA ÚNICA de <c>ServicioDeOrganizacion</c>, probada donde de verdad decide: dentro de
    /// una proyección, toda subconsulta correlacionada declara su propio <c>DeletedAt == null</c>,
    /// porque <c>IgnoreQueryFilters</c> se aplica a nivel CONSULTA y una proyección es una
    /// expresión COMPONIBLE.
    ///
    /// Por qué esta prueba existe (entrada de judgment-day de la slice 1, items 1 y 2): en la
    /// ronda 2 los tres predicados de nombre de dueño se agregaron y sus tres mutaciones
    /// SOBREVIVIERON — no porque estuvieran mal, sino porque ningún llamador de producción apagaba
    /// el filtro ambiente, así que el rojo no era alcanzable. La respuesta de
    /// <c>mutation-proof-tests</c> a un mutante sobreviviente es RE-RUTEAR POR DEBAJO DEL
    /// CONFOUND, no declararlo cerrado: el confound es el filtro ambiente, y se lo saca componiendo
    /// la MISMA expresión de producción con <c>IgnoreQueryFilters(["BajaLogica"])</c>. Esa es la
    /// razón por la que las tres proyecciones son <c>public static</c> (mismo criterio que
    /// <c>InspectorDeUso.Renderizar</c>; el repo no usa <c>InternalsVisibleTo</c>).
    ///
    /// Las SEIS cláusulas mueren acá, cada una por su lado:
    /// <list type="bullet">
    /// <item>los tres <c>Count</c> de <c>ProyeccionDeTenant</c> — sin su predicado contarían
    /// también al hijo dado de baja y el listado de tenants mentiría;</item>
    /// <item>el nombre de tenant de <c>ProyeccionDeEmpresa</c> y los dos de
    /// <c>ProyeccionDePuntoVenta</c> — sin su predicado mostrarían el nombre de un dueño dado de
    /// baja, en vez del <c>null</c> que D13 pide para la anomalía.</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task LosPredicadosDeLasProyeccionesMuerenConElFiltroAmbienteApagado()
    {
        var sufijo = Guid.NewGuid().ToString("N")[..8];

        var vivo = await SembrarTenantAsync($"Predicados-vivo-{sufijo}", nombreUsuarioAdmin: $"pred-a-{sufijo}");
        var huerfano = await SembrarTenantAsync($"Predicados-baja-{sufijo}", nombreUsuarioAdmin: $"pred-b-{sufijo}");

        // Hijos del tenant vivo que se dan de baja: son los que los tres contadores NO tienen que
        // contar aunque la consulta externa apague el filtro.
        var empresaDeBaja = await SembrarEmpresaAsync(vivo.Tenant.Id, $"Predicados anexo {sufijo} SRL");
        var puntoDeBaja = await SembrarPuntoVentaAsync(
            vivo.Tenant.Id, vivo.Empresa.Id, $"Predicados local {sufijo}");
        var usuarioDeBaja = await SembrarUsuarioAsync(
            vivo.Tenant.Id, $"pred-baja-{sufijo}", RolConocido.Vendedor);

        await DarDeBajaALaEmpresaAsync(empresaDeBaja.Id);
        await DarDeBajaAlUsuarioAsync(usuarioDeBaja.Id);
        await DarDeBajaAlPuntoDeVentaAsync(puntoDeBaja.Id);

        // El dueño dado de baja: su empresa y su punto de venta siguen vivos, así que son los
        // huérfanos que D13 pide rendir con el nombre en null.
        await DarDeBajaAlTenantAsync(huerfano.Tenant.Id);

        // Y una empresa dada de baja con su punto de venta VIVO: es el único huérfano que prueba la
        // tercera subconsulta (razón social de la empresa del punto de venta).
        var puntoSinEmpresa = await SembrarPuntoVentaAsync(
            vivo.Tenant.Id, empresaDeBaja.Id, $"Predicados sin empresa {sufijo}");

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var tenantProyectado = await db.Tenants
            .IgnoreQueryFilters(["BajaLogica"])
            .Where(t => t.Id == vivo.Tenant.Id)
            .Select(ServicioDeOrganizacion.ProyeccionDeTenant(db))
            .SingleAsync();

        // Una empresa viva y una dada de baja; un punto de venta vivo, uno dado de baja y el que
        // cuelga de la empresa dada de baja (que sigue vivo y sí cuenta); dos usuarios, uno de baja.
        Assert.Equal(1, tenantProyectado.CantidadEmpresas);
        Assert.Equal(2, tenantProyectado.CantidadPuntosVenta);
        Assert.Equal(1, tenantProyectado.CantidadUsuarios);

        var empresaHuerfana = await db.Empresas
            .IgnoreQueryFilters(["BajaLogica"])
            .Where(e => e.Id == huerfano.Empresa.Id)
            .Select(ServicioDeOrganizacion.ProyeccionDeEmpresa(db))
            .SingleAsync();

        Assert.Null(empresaHuerfana.NombreTenant);

        var puntoHuerfano = await db.PuntosVenta
            .IgnoreQueryFilters(["BajaLogica"])
            .Where(p => p.Id == huerfano.PuntoVenta.Id)
            .Select(ServicioDeOrganizacion.ProyeccionDePuntoVenta(db))
            .SingleAsync();

        Assert.Null(puntoHuerfano.NombreTenant);

        var puntoConEmpresaDeBaja = await db.PuntosVenta
            .IgnoreQueryFilters(["BajaLogica"])
            .Where(p => p.Id == puntoSinEmpresa.Id)
            .Select(ServicioDeOrganizacion.ProyeccionDePuntoVenta(db))
            .SingleAsync();

        Assert.Null(puntoConEmpresaDeBaja.RazonSocialEmpresa);

        // Control: con dueños VIVOS las mismas expresiones sí traen el nombre. Sin esto, un
        // predicado que apagara la proyección entera pasaría las cuatro aserciones de nulidad.
        var empresaViva = await db.Empresas
            .IgnoreQueryFilters(["BajaLogica"])
            .Where(e => e.Id == vivo.Empresa.Id)
            .Select(ServicioDeOrganizacion.ProyeccionDeEmpresa(db))
            .SingleAsync();

        Assert.Equal(vivo.Tenant.Nombre, empresaViva.NombreTenant);

        var puntoVivo = await db.PuntosVenta
            .IgnoreQueryFilters(["BajaLogica"])
            .Where(p => p.Id == vivo.PuntoVenta.Id)
            .Select(ServicioDeOrganizacion.ProyeccionDePuntoVenta(db))
            .SingleAsync();

        Assert.Equal(vivo.Tenant.Nombre, puntoVivo.NombreTenant);
        Assert.Equal(vivo.Empresa.RazonSocial, puntoVivo.RazonSocialEmpresa);
    }
}
