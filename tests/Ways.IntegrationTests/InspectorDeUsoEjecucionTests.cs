using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-20-organizacion-relaciones-y-bajas, Slice 3 (judgment-day ronda 1, jueces A y B): la
/// mitad de EJECUCIÓN de <see cref="InspectorDeUso"/> contra Postgres real. Las pruebas de
/// rendering (<c>tests/Ways.Application.Tests/Organizacion/InspectorDeUsoTests.cs</c>) prueban el
/// TEXTO del statement; nada probaba que ese texto se LIGARA y se EJECUTARA — el orden de bind
/// (<c>valoresDeClave</c> primero, el instante del ancla último), el gate
/// <c>ramas.Any(UsaAncla)</c>, la transacción del llamador y el <c>ExecuteScalarAsync as string</c>
/// no tenían ninguna red: borrar el gate deja el SQL referenciando <c>$n</c> con n-1 parámetros
/// ligados —un error de bind de Postgres, o sea un 500— y toda la suite seguía verde.
///
/// El ancla compuesta (<see cref="Empresa"/>: <c>Id</c> + <c>IdTenant</c> + el instante = TRES
/// parámetros) es la que hace observable el orden: intercambiar los dos bloques de
/// <c>Agregar</c> pone un <c>timestamptz</c> en <c>$1</c> contra <c>id_empresa</c>.
///
/// <c>mutation-proof-tests</c> regla 12c: cada fixture siembra un SEGUNDO hermano del mismo
/// tenant y un SEGUNDO tenant, así que un predicado que ignore una de las dos posiciones de la
/// clave compuesta muere acá.
///
/// Ronda 2 (R2-1) agregó la mitad que faltaba del ancla <see cref="Empresa"/>: sus ramas
/// PUENTEADAS por <c>puntos_venta</c>. El uso de una empresa vive en los hijos estructurales de
/// sus puntos de venta, así que sin ellas una empresa con historia operativa completa leía
/// PRÍSTINA.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class InspectorDeUsoEjecucionTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    private sealed record TenantAprovisionado(
        Tenant Tenant, Empresa Empresa, PuntoVenta PuntoVenta, int IdUsuarioAdmin);

    private async Task<HttpClient> ClienteComoRootAsync()
    {
        var cliente = fixture.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return cliente;
    }

    /// <summary>Aprovisiona por el camino REAL (el endpoint de plataforma): lo que hace válida la
    /// línea base "prístino" es que el instante del ancla sea el mismo que
    /// <c>ServicioDeAprovisionamiento</c> leyó una sola vez, no uno que la prueba se invente.</summary>
    private async Task<TenantAprovisionado> AprovisionarAsync(string nombre)
    {
        using var cliente = await ClienteComoRootAsync();

        var respuesta = await cliente.PostAsJsonAsync(
            "/api/plataforma/tenants",
            new SolicitudDeAprovisionamiento(nombre, $"{nombre} SRL", $"{nombre} - Local 1", $"{nombre}@ways.test"));

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);

        var resultado = await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>();
        Assert.NotNull(resultado);

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        return new TenantAprovisionado(
            await db.Tenants.SingleAsync(t => t.Id == resultado!.IdTenant),
            await db.Empresas.SingleAsync(e => e.Id == resultado!.IdEmpresa),
            await db.PuntosVenta.SingleAsync(p => p.Id == resultado!.IdPuntoVenta),
            resultado!.IdUsuarioAdmin);
    }

    private static Task<string?> PreguntarAsync(
        WaysDbContext db, Type ancla, DateTimeOffset instante, params object[] valoresDeClave) =>
        new InspectorDeUso(db).PrimeraDependenciaEnUsoAsync(ancla, valoresDeClave, instante);

    // ---------------------------------------------------------------------------------------
    // Línea base: un tenant recién aprovisionado está PRÍSTINO en las cuatro anclas (N4).
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Cláusula: el <c>&gt;</c> ESTRICTO contra el instante del ancla, ejecutado de verdad. Todo lo
    /// que crea el aprovisionamiento comparte ese instante (<c>ServicioDeAprovisionamiento</c> lee
    /// el reloj una sola vez), así que ninguna de las ~46 ramas del ancla <c>Tenant</c> devuelve
    /// fila. Un <c>&gt;=</c> —o una fila sin marca que el aprovisionamiento creara sin darse
    /// cuenta— volvería indeleteable a todo tenant nuevo, y esta prueba es lo único que lo ve.
    /// </summary>
    [Fact]
    public async Task UnTenantReciennAprovisionadoEstaPristinoEnLasCuatroAnclas()
    {
        var sembrado = await AprovisionarAsync(nameof(UnTenantReciennAprovisionadoEstaPristinoEnLasCuatroAnclas));

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        Assert.Null(await PreguntarAsync(
            db, typeof(Tenant), sembrado.Tenant.CreatedAt, sembrado.Tenant.Id));

        Assert.Null(await PreguntarAsync(
            db, typeof(Empresa), sembrado.Empresa.CreatedAt, sembrado.Empresa.Id, sembrado.Tenant.Id));

        Assert.Null(await PreguntarAsync(
            db, typeof(PuntoVenta), sembrado.PuntoVenta.CreatedAt, sembrado.PuntoVenta.Id, sembrado.Tenant.Id));

        var admin = await db.Usuarios.SingleAsync(u => u.Id == sembrado.IdUsuarioAdmin);
        Assert.Null(await PreguntarAsync(db, typeof(Usuario), admin.CreatedAt, admin.Id));
    }

    // ---------------------------------------------------------------------------------------
    // C1 — puntos_venta bajo el ancla Tenant (la rama que el recorrido de FKs NO ve).
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Cláusula: la rama <c>Tenant | puntos_venta | id_tenant</c>, sintetizada por
    /// <c>InventarioDeDependientes</c> desde <c>EntidadTenant</c> porque <c>puntos_venta</c>
    /// declara su FK compuesta contra <c>empresas</c> y NINGUNA contra <c>tenants</c>.
    ///
    /// Sin esa rama, un tenant cuyo cliente abrió un segundo local leía PRÍSTINO y la plataforma
    /// lo daba de baja: falla ABIERTA, en la dirección de pérdida de datos.
    /// </summary>
    [Fact]
    public async Task UnSegundoPuntoDeVentaBloqueaLaBajaDelTenant()
    {
        var sembrado = await AprovisionarAsync(nameof(UnSegundoPuntoDeVentaBloqueaLaBajaDelTenant));

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        Assert.Null(await PreguntarAsync(
            db, typeof(Tenant), sembrado.Tenant.CreatedAt, sembrado.Tenant.Id));

        var despues = sembrado.Tenant.CreatedAt.AddMinutes(1);
        db.PuntosVenta.Add(new PuntoVenta
        {
            IdTenant = sembrado.Tenant.Id,
            IdEmpresa = sembrado.Empresa.Id,
            Nombre = "Local 2",
            CreatedAt = despues,
            UpdatedAt = despues
        });
        await db.SaveChangesAsync();

        Assert.Equal(
            "puntos_venta",
            await PreguntarAsync(db, typeof(Tenant), sembrado.Tenant.CreatedAt, sembrado.Tenant.Id));
    }

    // ---------------------------------------------------------------------------------------
    // R2-1 — el uso sube por la jerarquía: las ramas de Empresa PUENTEADAS por puntos_venta.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Cláusula: la familia de ramas <c>Empresa | &lt;hoja&gt; via puntos_venta</c>, o sea el
    /// <c>JOIN "puntos_venta" pv ON d."id_punto_venta" = pv."id_punto_venta" AND d."id_tenant" =
    /// pv."id_tenant"</c> con <c>pv."id_empresa" = $1 AND pv."id_tenant" = $2</c>.
    ///
    /// NINGUNA tabla operativa lleva <c>id_empresa</c> — comprobantes, items, pagos, movimientos de
    /// stock/caja/tesorería/cuenta corriente, turnos, presupuestos, remitos, órdenes y gastos se
    /// clavan todos en <c>id_punto_venta</c> —, así que los referenciantes DIRECTOS de una empresa
    /// son solo estructura y catálogo. Sin esta familia, una empresa con historia operativa
    /// completa lee PRÍSTINA: la plataforma la da de baja junto con su punto de venta. Falla
    /// ABIERTA, la dirección de pérdida de datos, y de la misma clase que C1 (la ronda 0) por otro
    /// mecanismo.
    ///
    /// La línea base va PRIMERO: la empresa recién aprovisionada lee <c>null</c>, así que lo que
    /// cambia el resultado es la fila del cliente y no la familia de ramas por sí sola.
    /// </summary>
    [Fact]
    public async Task UnTurnoDeCajaEnSuPuntoDeVentaBloqueaLaBajaDeLaEmpresa()
    {
        var sembrado = await AprovisionarAsync(nameof(UnTurnoDeCajaEnSuPuntoDeVentaBloqueaLaBajaDeLaEmpresa));

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        Assert.Null(await PreguntarAsync(
            db, typeof(Empresa), sembrado.Empresa.CreatedAt, sembrado.Empresa.Id, sembrado.Tenant.Id));

        var despues = sembrado.PuntoVenta.CreatedAt.AddMinutes(1);

        db.TurnosCaja.Add(new Ways.Domain.Caja.TurnoCaja
        {
            IdTenant = sembrado.Tenant.Id,
            IdPuntoVenta = sembrado.PuntoVenta.Id,
            IdEmpleadoApertura = sembrado.IdUsuarioAdmin,
            FechaApertura = despues,
            FondoInicial = 0m,
            Estado = Ways.Domain.Caja.EstadoTurno.Abierto,
            CreatedAt = despues,
            UpdatedAt = despues
        });
        await db.SaveChangesAsync();

        // La etiqueta identifica la RAMA, no solo la hoja (judgment-day ronda 2, hallazgo R2-6):
        // el hit vino por el puente y la etiqueta lo dice, así que `DescribirBloqueo` puede
        // redactar "turnos de caja en sus puntos de venta" sin adivinar.
        Assert.Equal(
            "turnos_caja via puntos_venta",
            await PreguntarAsync(
                db, typeof(Empresa), sembrado.Empresa.CreatedAt,
                sembrado.Empresa.Id, sembrado.Tenant.Id));
    }

    /// <summary>
    /// Cláusula: el conjunto <c>pv."id_tenant" = $2</c> de la rama puenteada, aislado. Se pregunta
    /// por el id de ESTA empresa contra el tenant de OTRO: mismo statement, mismos tres parámetros,
    /// solo cambia el segundo. Sin ese conjunto, <c>pv."id_empresa" = $1</c> matchea igual y el
    /// turno de un tenant bloquearía la baja de otro — filtración de uso a través del puente.
    ///
    /// El aprovisionamiento de plataforma numera las empresas globalmente, así que el aislamiento
    /// se prueba donde de verdad decide: en el conjunto, no en la aritmética de los ids.
    /// </summary>
    [Fact]
    public async Task ElTurnoDeOtroTenantNoBloqueaLaBajaDeLaEmpresaPorElPuente()
    {
        var sembrado = await AprovisionarAsync(nameof(ElTurnoDeOtroTenantNoBloqueaLaBajaDeLaEmpresaPorElPuente));
        var otro = await AprovisionarAsync($"{nameof(ElTurnoDeOtroTenantNoBloqueaLaBajaDeLaEmpresaPorElPuente)}-otro");

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var despues = sembrado.PuntoVenta.CreatedAt.AddMinutes(1);

        db.TurnosCaja.Add(new Ways.Domain.Caja.TurnoCaja
        {
            IdTenant = sembrado.Tenant.Id,
            IdPuntoVenta = sembrado.PuntoVenta.Id,
            IdEmpleadoApertura = sembrado.IdUsuarioAdmin,
            FechaApertura = despues,
            FondoInicial = 0m,
            Estado = Ways.Domain.Caja.EstadoTurno.Abierto,
            CreatedAt = despues,
            UpdatedAt = despues
        });
        await db.SaveChangesAsync();

        Assert.Equal(
            "turnos_caja via puntos_venta",
            await PreguntarAsync(
                db, typeof(Empresa), sembrado.Empresa.CreatedAt,
                sembrado.Empresa.Id, sembrado.Tenant.Id));

        Assert.Null(await PreguntarAsync(
            db, typeof(Empresa), sembrado.Empresa.CreatedAt,
            sembrado.Empresa.Id, otro.Tenant.Id));

        // Y la empresa del otro tenant, que solo tiene lo que creó el aprovisionamiento, sigue
        // PRÍSTINA: la familia puenteada no sobre-bloquea a quien no operó.
        Assert.Null(await PreguntarAsync(
            db, typeof(Empresa), otro.Empresa.CreatedAt, otro.Empresa.Id, otro.Tenant.Id));
    }

    // ---------------------------------------------------------------------------------------
    // C2 — el orden de ligado sobre un ancla de clave COMPUESTA.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Cláusula: el orden de ligado de <c>PrimeraDependenciaEnUsoAsync</c> — primero
    /// <c>valoresDeClave</c> EN EL ORDEN de <c>PropiedadesDeAncla</c> (<c>Id</c>, <c>IdTenant</c>),
    /// después el instante del ancla, y solo si alguna rama lo usa.
    ///
    /// Las tres preguntas son el mismo statement con los mismos tres parámetros y distinta
    /// asignación posicional, así que ninguna otra capa puede explicar la diferencia:
    /// <list type="bullet">
    /// <item>la empresa CON marca nueva bloquea nombrando <c>marcas</c>;</item>
    /// <item>su hermana del MISMO tenant, sin marcas propias, no bloquea — <c>$1</c> es
    /// <c>id_empresa</c> de verdad;</item>
    /// <item>la misma empresa contra OTRO tenant no bloquea — <c>$2</c> es <c>id_tenant</c> de
    /// verdad.</item>
    /// </list>
    /// Y como el ancla es compuesta, el statement referencia <c>$3</c>: borrar el gate
    /// <c>ramas.Any(UsaAncla)</c> deja dos parámetros ligados contra tres pedidos y Postgres
    /// rechaza el bind; intercambiar los dos bloques de <c>Agregar</c> pone el
    /// <c>timestamptz</c> en <c>$1</c> contra una columna entera.
    /// </summary>
    [Fact]
    public async Task ElOrdenDeLigadoDeUnAnclaCompuestaEsPosicionalYElInstanteVaUltimo()
    {
        var sembrado = await AprovisionarAsync(nameof(ElOrdenDeLigadoDeUnAnclaCompuestaEsPosicionalYElInstanteVaUltimo));
        var otro = await AprovisionarAsync($"{nameof(ElOrdenDeLigadoDeUnAnclaCompuestaEsPosicionalYElInstanteVaUltimo)}-otro");

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var despues = sembrado.Empresa.CreatedAt.AddMinutes(1);

        var hermana = new Empresa
        {
            IdTenant = sembrado.Tenant.Id,
            RazonSocial = "Hermana SRL",
            CreatedAt = despues,
            UpdatedAt = despues
        };
        db.Empresas.Add(hermana);
        await db.SaveChangesAsync();

        db.Marcas.Add(new Marca
        {
            IdTenant = sembrado.Tenant.Id,
            IdEmpresa = sembrado.Empresa.Id,
            Nombre = "Marca del cliente",
            CreatedAt = despues,
            UpdatedAt = despues
        });
        await db.SaveChangesAsync();

        Assert.Equal(
            "marcas",
            await PreguntarAsync(
                db, typeof(Empresa), sembrado.Empresa.CreatedAt, sembrado.Empresa.Id, sembrado.Tenant.Id));

        Assert.Null(await PreguntarAsync(
            db, typeof(Empresa), hermana.CreatedAt, hermana.Id, sembrado.Tenant.Id));

        Assert.Null(await PreguntarAsync(
            db, typeof(Empresa), sembrado.Empresa.CreatedAt, sembrado.Empresa.Id, otro.Tenant.Id));
    }

    // ---------------------------------------------------------------------------------------
    // Las otras dos anclas, ejecutadas de verdad.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Cláusula: el ancla <see cref="PuntoVenta"/> también es compuesta (clave alternativa
    /// <c>(Id, IdTenant)</c>) y su rama <c>turnos_caja</c> lleva el conjunto del instante. Se
    /// siembra un SEGUNDO punto de venta del mismo tenant, sin turnos, para que un predicado que
    /// ignorara <c>id_punto_venta</c> muera (<c>mutation-proof-tests</c> regla 12c).
    /// </summary>
    [Fact]
    public async Task UnTurnoDeCajaBloqueaSoloAlPuntoDeVentaQueLoAbrio()
    {
        var sembrado = await AprovisionarAsync(nameof(UnTurnoDeCajaBloqueaSoloAlPuntoDeVentaQueLoAbrio));

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var despues = sembrado.PuntoVenta.CreatedAt.AddMinutes(1);

        var segundo = new PuntoVenta
        {
            IdTenant = sembrado.Tenant.Id,
            IdEmpresa = sembrado.Empresa.Id,
            Nombre = "Local 2",
            CreatedAt = despues,
            UpdatedAt = despues
        };
        db.PuntosVenta.Add(segundo);
        await db.SaveChangesAsync();

        db.TurnosCaja.Add(new Ways.Domain.Caja.TurnoCaja
        {
            IdTenant = sembrado.Tenant.Id,
            IdPuntoVenta = sembrado.PuntoVenta.Id,
            IdEmpleadoApertura = sembrado.IdUsuarioAdmin,
            FechaApertura = despues,
            FondoInicial = 0m,
            Estado = Ways.Domain.Caja.EstadoTurno.Abierto,
            CreatedAt = despues,
            UpdatedAt = despues
        });
        await db.SaveChangesAsync();

        Assert.Equal(
            "turnos_caja",
            await PreguntarAsync(
                db, typeof(PuntoVenta), sembrado.PuntoVenta.CreatedAt,
                sembrado.PuntoVenta.Id, sembrado.Tenant.Id));

        Assert.Null(await PreguntarAsync(
            db, typeof(PuntoVenta), segundo.CreatedAt, segundo.Id, sembrado.Tenant.Id));

        Assert.Equal(
            "turnos_caja",
            await PreguntarAsync(
                db, typeof(Usuario), (await db.Usuarios.SingleAsync(u => u.Id == sembrado.IdUsuarioAdmin)).CreatedAt,
                sembrado.IdUsuarioAdmin));
    }
}
