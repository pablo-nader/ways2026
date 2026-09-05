using System.Data;
using System.Data.Common;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Domain.Common;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;

namespace Ways.Application.Organizacion;

/// <summary>
/// Lectura y edición de datos descriptivos de la organización (doc 09): tenants
/// (plataforma-only, <c>Politicas.SoloPlataforma</c> en la API), empresas y puntos de venta
/// (plataforma ve/edita cualquiera; un admin de tenant ve/edita solo los propios). El filtro
/// de EF (+ RLS por debajo) ya deja invisible una fila de otro tenant — la llamada a
/// <see cref="PoliticaDeRoles.ValidarAlcanceDeTenant"/> es la capa explícita de dominio
/// (ADR-8), mismo patrón que <c>ServicioDeUsuarios.BuscarAsync</c>.
///
/// El ALTA sigue siendo plataforma-only vía <see cref="ServicioDeAprovisionamiento"/> (ADR-16).
/// La BAJA vive acá desde la etapa 20 (slice 4): es lógica (nunca borra la fila), pasa por
/// <see cref="InspectorDeUso"/> —que es la única línea de defensa, porque ninguna constraint de
/// Postgres puede dispararse contra un <c>UPDATE ... SET deleted_at</c>— y arrastra en cascada
/// la proyección de organización compartiendo UN solo instante.
/// </summary>
public class ServicioDeOrganizacion(
    IWaysDbContext db, IRelojDelSistema reloj, IContextoDeUsuario contexto, InspectorDeUso inspector)
{
    /// <summary>
    /// Segunda clave del <c>pg_advisory_xact_lock</c> de las bajas de organización (design D11).
    /// NEGATIVA a propósito: <c>ServicioDeOfertas</c> usa <c>(idTenant, idOferta)</c> y
    /// <c>ServicioDePrecios</c> usa <c>(idTenant, hash(articulo, lista))</c>, los dos con claves
    /// que en la práctica son positivas, así que una constante negativa no puede colisionar con
    /// ninguna de las dos y una baja nunca se serializa contra una oferta o un precio de otro
    /// tenant por accidente de clave. La primera clave es siempre el TENANT dueño: dar de baja un
    /// tenant y dar de baja una de sus empresas toman el MISMO lock, que es justo lo que cierra la
    /// carrera de la tabla G del design.
    /// </summary>
    private const int ClaveDeLockDeBaja = -20;

    private ActorDeGestion Actor => new(contexto.Rol, contexto.UsuarioId, contexto.IdTenant);

    // --- Tenants ---

    /// <summary>
    /// LA REGLA, ÚNICA (etapa 20 slice 4, entrada de judgment-day de la slice 1): dentro de una
    /// proyección, TODA subconsulta correlacionada sobre una entidad con baja lógica declara su
    /// propio <c>DeletedAt == null</c>. El filtro ambiente <c>"BajaLogica"</c> NO es
    /// load-bearing acá, porque <c>IgnoreQueryFilters</c> se aplica a nivel CONSULTA: la consulta
    /// externa que lo pidiera se lo sacaría también a la subconsulta, y el listado empezaría a
    /// contar hijos dados de baja o a mostrar el nombre de un dueño dado de baja. Una proyección
    /// es además una expresión COMPONIBLE: cualquier llamador puede montarla sobre una consulta
    /// que apague los filtros, y es exactamente lo que hacen las pruebas que matan estas
    /// cláusulas.
    ///
    /// La regla anterior era contradictoria consigo misma —el comentario de los contadores decía
    /// que el predicado explícito no hacía falta y el de los nombres de dueño decía lo contrario,
    /// para el MISMO riesgo—; queda una sola regla y los siete predicados (tres contadores +
    /// cuatro nombres de dueño) la cumplen.
    ///
    /// El otro motivo de forma sigue igual (design D13): los tres contadores son subconsultas
    /// correlacionadas dentro del MISMO <c>Select</c>, así que el listado cuesta una sola ida a la
    /// base, sin N+1 por tenant. Y <c>u.IdTenant == t.Id</c> sobre un <c>int?</c> no matchea nunca
    /// contra <c>NULL</c>: el personal de plataforma no se cuenta bajo ningún tenant.
    ///
    /// PÚBLICA Y ESTÁTICA por el mismo criterio que <c>InspectorDeUso.Renderizar</c>: es la
    /// superficie que las pruebas componen CON los filtros apagados, que es el único lugar desde
    /// donde estas cláusulas se pueden matar de verdad (el repo no usa <c>InternalsVisibleTo</c>).
    /// </summary>
    public static Expression<Func<Tenant, TenantListado>> ProyeccionDeTenant(IWaysDbContext db) =>
        t => new TenantListado(
            t.Id,
            t.Nombre,
            t.Estado,
            t.CreatedAt,
            db.Empresas.Count(e => e.IdTenant == t.Id && e.DeletedAt == null),
            db.PuntosVenta.Count(p => p.IdTenant == t.Id && p.DeletedAt == null),
            db.Usuarios.Count(u => u.IdTenant == t.Id && u.DeletedAt == null));

    public async Task<IReadOnlyList<TenantListado>> ListarTenantsAsync(CancellationToken ct = default) =>
        await db.Tenants
            .OrderBy(t => t.Nombre)
            .Select(ProyeccionDeTenant(db))
            .ToListAsync(ct);

    /// <summary>Es también la relectura que usan las escrituras: los contadores no viven en la
    /// entidad, así que el único lugar donde están es la consulta. Es una ida extra sobre una
    /// acción de plataforma puntual — el presupuesto de "una sola consulta" es el de los LISTADOS,
    /// que son los que escalan con la cantidad de filas. Sobre un camino de escritura la relectura
    /// va DENTRO de la misma transacción (<see cref="EnUnaTransaccionAsync"/>), así que no puede
    /// devolver 404 por una fila que la escritura acaba de persistir.</summary>
    public async Task<TenantListado> ObtenerTenantAsync(int id, CancellationToken ct = default) =>
        await db.Tenants.Where(t => t.Id == id).Select(ProyeccionDeTenant(db)).FirstOrDefaultAsync(ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el tenant {id}.");

    public async Task<TenantListado> ActualizarTenantAsync(
        int id, TenantEdicion datos, CancellationToken ct = default)
    {
        var tenant = await BuscarTenantAsync(id, ct);
        var nombre = Normalizar(datos.Nombre, "nombre_tenant", "nombre del tenant", 150);

        return await EnUnaTransaccionAsync(async () =>
        {
            tenant.Nombre = nombre;
            tenant.UpdatedAt = reloj.Ahora;

            await db.SaveChangesAsync(ct);
            return await ObtenerTenantAsync(tenant.Id, ct);
        }, ct);
    }

    public Task<TenantListado> SuspenderTenantAsync(int id, CancellationToken ct = default) =>
        CambiarEstadoTenantAsync(id, EstadoTenant.Suspendido, ct);

    public Task<TenantListado> ReactivarTenantAsync(int id, CancellationToken ct = default) =>
        CambiarEstadoTenantAsync(id, EstadoTenant.Activo, ct);

    /// <summary>Idempotente a propósito: un doble clic en "suspender" sobre un tenant ya
    /// suspendido no debería ser un error, el estado resultante es el que pedía el operador
    /// de todos modos. <see cref="EstadoTenant.Baja"/> no lo toca ninguna de las dos acciones
    /// — alternar entre Activo/Suspendido no reactiva un tenant dado de baja por error.</summary>
    private async Task<TenantListado> CambiarEstadoTenantAsync(
        int id, EstadoTenant estado, CancellationToken ct)
    {
        var tenant = await BuscarTenantAsync(id, ct);

        if (tenant.Estado == EstadoTenant.Baja)
        {
            throw new ErrorDominio(
                "tenant_dado_de_baja",
                "Un tenant dado de baja no se puede suspender ni reactivar.", 409);
        }

        if (tenant.Estado == estado)
        {
            return await ObtenerTenantAsync(tenant.Id, ct);
        }

        return await EnUnaTransaccionAsync(async () =>
        {
            tenant.Estado = estado;
            tenant.UpdatedAt = reloj.Ahora;

            await db.SaveChangesAsync(ct);
            return await ObtenerTenantAsync(tenant.Id, ct);
        }, ct);
    }

    private async Task<Tenant> BuscarTenantAsync(int id, CancellationToken ct) =>
        await db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el tenant {id}.");

    /// <summary>
    /// Baja lógica del tenant, con su cascada (BO-R9, TO-R4, design F/D7/D9/D10/D11).
    ///
    /// Orden EXACTO adentro de la transacción, y cada paso está donde está por un motivo:
    /// <list type="number">
    /// <item>el <c>pg_advisory_xact_lock</c> (D11), que serializa esta baja contra cualquier otra
    /// del mismo tenant —incluida la de una de sus empresas, que toma la MISMA clave—;</item>
    /// <item>la RELECTURA del ancla bajo el lock: si una baja concurrente ganó, la fila ya no es
    /// visible bajo <c>"BajaLogica"</c> y esto es un 404 limpio en vez de un segundo
    /// <c>deleted_at</c> pisando el primero;</item>
    /// <item>el guard de uso, evaluado UNA SOLA VEZ y sin ningún pre-chequeo afuera. Un pre-chequeo
    /// que espejara al guard es el confound más común de este repo (<c>mutation-proof-tests</c>
    /// regla 3): correrlo una sola vez lo elimina en vez de escribir pruebas que lo esquiven;</item>
    /// <item>UNA sola lectura del reloj para el padre y todos los hijos, y UN solo
    /// <c>SaveChangesAsync</c> — el orden de los statements lo elige EF y este diseño no pretende
    /// lo contrario (D9): la propiedad es la atomicidad, y la da la transacción.</item>
    /// </list>
    ///
    /// <c>db-error-backstops</c> es N/A: la baja es un <c>UPDATE ... SET deleted_at</c> y ninguna
    /// FK <c>Restrict</c> puede dispararse contra eso, así que el guard de aplicación es la única
    /// línea de defensa y no hay SQLSTATE que clasificar.
    /// </summary>
    public async Task EliminarTenantAsync(int id, CancellationToken ct = default)
    {
        // 404 antes de abrir nada: el caso normal de "ese id no existe" no paga transacción.
        await BuscarTenantAsync(id, ct);

        await EnUnaTransaccionAsync(async () =>
        {
            await TomarLockDeBajaAsync(id, ct);

            var tenant = await BuscarTenantAsync(id, ct);

            await ExigirSinUsoAsync(
                typeof(Tenant),
                tenant.CreatedAt,
                new Dictionary<string, object>(StringComparer.Ordinal) { ["Id"] = tenant.Id },
                "tenant_en_uso",
                "el tenant",
                ct);

            var momento = reloj.Ahora;

            // La cascada es genérica (`IdTenant == id`) aunque el conjunto sea demostrablemente de
            // tres filas: un tenant con cualquier uso murió en el guard, así que el que llega acá
            // es prístino y un prístino tiene exactamente lo que creó el aprovisionamiento. Escrita
            // así no puede perderse una fila que el razonamiento no anticipó.
            //
            // S3 — solo hijos VIVOS. No hace falta ningún `DeletedAt == null` explícito y no se
            // agrega uno: acá no hay subconsulta correlacionada que un `IgnoreQueryFilters` externo
            // pueda descubrir, la consulta se arma y se ejecuta entera adentro de este método, así
            // que el filtro ambiente "BajaLogica" es garantía y no supuesto. Un hijo ya dado de
            // baja conserva su instante ORIGINAL, que es lo que mantiene exacto el restore
            // `UPDATE ... SET deleted_at = NULL WHERE deleted_at = '<instante>'`.
            Marcar(await db.Usuarios.Where(u => u.IdTenant == id).ToListAsync(ct), momento);
            Marcar(await db.PuntosVenta.Where(p => p.IdTenant == id).ToListAsync(ct), momento);
            Marcar(await db.Empresas.Where(e => e.IdTenant == id).ToListAsync(ct), momento);

            // D10 — el ÚNICO escritor de EstadoTenant.Baja, y va en el MISMO SaveChanges que el
            // deleted_at: en dos statements existiría un intervalo donde la fila está dada de baja
            // y sigue diciendo "activo".
            Marcar(tenant, momento);
            tenant.Estado = EstadoTenant.Baja;

            await db.SaveChangesAsync(ct);
            return true;
        }, ct);
    }

    // --- Empresas ---

    /// <summary>El nombre del tenant se proyecta como subconsulta escalar correlacionada porque
    /// <see cref="Empresa"/> no tiene propiedad de navegación al tenant (design D13, hecho 1): no
    /// hay nada a lo que hacerle punto. De paso evita el INNER JOIN que borraría la fila cuando el
    /// tenant está dado de baja — ahí el nombre queda en <c>null</c> y la empresa se sigue
    /// listando como anomalía en vez de desaparecer.
    ///
    /// El <c>t.DeletedAt == null</c> explícito es LA REGLA ÚNICA documentada en
    /// <see cref="ProyeccionDeTenant"/>, y vale igual para las dos subconsultas de
    /// <see cref="ProyeccionDePuntoVenta"/>. Pública y estática por el mismo motivo.</summary>
    public static Expression<Func<Empresa, EmpresaListado>> ProyeccionDeEmpresa(IWaysDbContext db) =>
        e => new EmpresaListado(
            e.Id,
            e.IdTenant,
            e.RazonSocial,
            e.NombreFantasia,
            e.Cuit,
            db.Tenants
                .Where(t => t.Id == e.IdTenant && t.DeletedAt == null)
                .Select(t => t.Nombre)
                .FirstOrDefault());

    public async Task<IReadOnlyList<EmpresaListado>> ListarEmpresasAsync(CancellationToken ct = default) =>
        await db.Empresas
            .OrderBy(e => e.RazonSocial)
            .Select(ProyeccionDeEmpresa(db))
            .ToListAsync(ct);

    /// <summary>Proyecta y recién después valida el alcance, mismo orden que
    /// <see cref="BuscarEmpresaAsync"/>: primero 404 si no existe (o si el filtro de tenant la
    /// deja invisible), después la capa explícita de dominio (ADR-8).</summary>
    public async Task<EmpresaListado> ObtenerEmpresaAsync(int id, CancellationToken ct = default)
    {
        var empresa = await db.Empresas
            .Where(e => e.Id == id)
            .Select(ProyeccionDeEmpresa(db))
            .FirstOrDefaultAsync(ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe la empresa {id}.");

        PoliticaDeRoles.ValidarAlcanceDeTenant(Actor, empresa.IdTenant);

        return empresa;
    }

    public async Task<EmpresaListado> ActualizarEmpresaAsync(
        int id, EmpresaEdicion datos, CancellationToken ct = default)
    {
        var empresa = await BuscarEmpresaAsync(id, ct);

        var razonSocial = Normalizar(datos.RazonSocial, "razon_social", "razón social", 150);
        var nombreFantasia = NormalizarOpcional(datos.NombreFantasia, "nombre_fantasia", "nombre de fantasía", 150);
        var cuit = NormalizarOpcional(datos.Cuit, "cuit", "CUIT", 13);

        return await EnUnaTransaccionAsync(async () =>
        {
            empresa.RazonSocial = razonSocial;
            empresa.NombreFantasia = nombreFantasia;
            empresa.Cuit = cuit;
            empresa.UpdatedAt = reloj.Ahora;

            await db.SaveChangesAsync(ct);

            // La relectura vive DENTRO de la transacción de la escritura: el UPDATE ya tiene
            // tomado el lock de fila, así que ninguna baja concurrente puede volverla invisible
            // en el medio y este 404 dejó de ser alcanzable para una escritura que persistió.
            return await db.Empresas
                .Where(e => e.Id == empresa.Id)
                .Select(ProyeccionDeEmpresa(db))
                .FirstOrDefaultAsync(ct)
                ?? throw ErrorDominio.NoEncontrado($"No existe la empresa {id}.");
        }, ct);
    }

    /// <summary>
    /// Baja lógica de la empresa. Misma forma de transacción y lock que
    /// <see cref="EliminarTenantAsync"/> —el lock se toma sobre el TENANT dueño, así que esta baja
    /// y la del tenant se serializan entre sí—, con dos diferencias:
    ///
    /// <list type="bullet">
    /// <item>el MÍNIMO ESTRUCTURAL va PRIMERO (S6, BO-R10): un tenant siempre conserva al menos una
    /// empresa. Si además hay uso, la respuesta es igual la estructural, porque las dos le dicen
    /// cosas opuestas al operador ("hay datos acá" vs. "dá de baja el padre");</item>
    /// <item>la cascada llega solo a los puntos de venta de ESTA empresa: el tenant y sus usuarios
    /// quedan intactos.</item>
    /// </list>
    ///
    /// OD5 — hoy ninguna ruta de la API crea una segunda empresa, así que a través de la API el
    /// mínimo estructural dispara SIEMPRE y <c>empresa_en_uso</c> es alcanzable únicamente por
    /// debajo de la API. Es una latencia aceptada y registrada, no un olvido.
    /// </summary>
    public async Task EliminarEmpresaAsync(int id, CancellationToken ct = default)
    {
        var empresa = await BuscarEmpresaAsync(id, ct);

        await EnUnaTransaccionAsync(async () =>
        {
            await TomarLockDeBajaAsync(empresa.IdTenant, ct);

            var bajo = await BuscarEmpresaAsync(id, ct);

            // S2 — el mínimo cuenta HERMANAS VIVAS. Una hermana ya dada de baja no es una
            // sobreviviente, así que la última empresa viva sigue siendo la última.
            var vivas = await db.Empresas.CountAsync(e => e.IdTenant == bajo.IdTenant, ct);

            if (vivas <= 1)
            {
                throw ErrorDominio.Conflicto(
                    "ultima_empresa_del_tenant",
                    "Es la única empresa del tenant: si querés eliminarla, dá de baja el tenant.");
            }

            await ExigirSinUsoAsync(
                typeof(Empresa),
                bajo.CreatedAt,
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["Id"] = bajo.Id,
                    ["IdTenant"] = bajo.IdTenant
                },
                "empresa_en_uso",
                "la empresa",
                ct);

            var momento = reloj.Ahora;

            Marcar(await db.PuntosVenta.Where(p => p.IdEmpresa == id).ToListAsync(ct), momento);
            Marcar(bajo, momento);

            await db.SaveChangesAsync(ct);
            return true;
        }, ct);
    }

    private async Task<Empresa> BuscarEmpresaAsync(int id, CancellationToken ct)
    {
        var empresa = await db.Empresas.FirstOrDefaultAsync(e => e.Id == id, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe la empresa {id}.");

        PoliticaDeRoles.ValidarAlcanceDeTenant(Actor, empresa.IdTenant);

        return empresa;
    }

    // --- Puntos de venta ---

    /// <summary>Dos subconsultas escalares correlacionadas, por el mismo motivo que en
    /// <see cref="ProyeccionDeEmpresa"/>: <see cref="PuntoVenta"/> tampoco tiene navegaciones
    /// (design D13, hecho 1). Los dos <c>DeletedAt == null</c> explícitos y el <c>public
    /// static</c>: misma regla única de <see cref="ProyeccionDeTenant"/>.</summary>
    public static Expression<Func<PuntoVenta, PuntoVentaListado>> ProyeccionDePuntoVenta(IWaysDbContext db) =>
        p => new PuntoVentaListado(
            p.Id,
            p.IdTenant,
            p.IdEmpresa,
            p.Nombre,
            p.Domicilio,
            p.Horario,
            p.Whatsapp,
            p.Instagram,
            p.Facebook,
            p.Web,
            db.Tenants
                .Where(t => t.Id == p.IdTenant && t.DeletedAt == null)
                .Select(t => t.Nombre)
                .FirstOrDefault(),
            db.Empresas
                .Where(e => e.Id == p.IdEmpresa && e.DeletedAt == null)
                .Select(e => e.RazonSocial)
                .FirstOrDefault());

    public async Task<IReadOnlyList<PuntoVentaListado>> ListarPuntosVentaAsync(CancellationToken ct = default) =>
        await db.PuntosVenta
            .OrderBy(p => p.Nombre)
            .Select(ProyeccionDePuntoVenta(db))
            .ToListAsync(ct);

    public async Task<PuntoVentaListado> ObtenerPuntoVentaAsync(int id, CancellationToken ct = default)
    {
        var puntoVenta = await db.PuntosVenta
            .Where(p => p.Id == id)
            .Select(ProyeccionDePuntoVenta(db))
            .FirstOrDefaultAsync(ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el punto de venta {id}.");

        PoliticaDeRoles.ValidarAlcanceDeTenant(Actor, puntoVenta.IdTenant);

        return puntoVenta;
    }

    public async Task<PuntoVentaListado> ActualizarPuntoVentaAsync(
        int id, PuntoVentaEdicion datos, CancellationToken ct = default)
    {
        var puntoVenta = await BuscarPuntoVentaAsync(id, ct);

        var nombre = Normalizar(datos.Nombre, "nombre_punto_venta", "nombre del punto de venta", 150);
        var domicilio = NormalizarOpcional(datos.Domicilio, "domicilio", "domicilio", 255);
        var horario = NormalizarOpcional(datos.Horario, "horario", "horario", 255);
        var whatsapp = NormalizarOpcional(datos.Whatsapp, "whatsapp", "WhatsApp", 30);
        var instagram = NormalizarOpcional(datos.Instagram, "instagram", "Instagram", 150);
        var facebook = NormalizarOpcional(datos.Facebook, "facebook", "Facebook", 150);
        var web = NormalizarOpcional(datos.Web, "sitio_web", "sitio web", 255);

        return await EnUnaTransaccionAsync(async () =>
        {
            puntoVenta.Nombre = nombre;
            puntoVenta.Domicilio = domicilio;
            puntoVenta.Horario = horario;
            puntoVenta.Whatsapp = whatsapp;
            puntoVenta.Instagram = instagram;
            puntoVenta.Facebook = facebook;
            puntoVenta.Web = web;
            puntoVenta.UpdatedAt = reloj.Ahora;

            await db.SaveChangesAsync(ct);

            // Misma relectura dentro de la misma transacción que ActualizarEmpresaAsync.
            return await db.PuntosVenta
                .Where(p => p.Id == puntoVenta.Id)
                .Select(ProyeccionDePuntoVenta(db))
                .FirstOrDefaultAsync(ct)
                ?? throw ErrorDominio.NoEncontrado($"No existe el punto de venta {id}.");
        }, ct);
    }

    /// <summary>
    /// Baja lógica del punto de venta: mínimo estructural primero
    /// (<c>ultimo_punto_venta_de_la_empresa</c>), después el guard de uso. No tiene hijos
    /// estructurales, así que no hay cascada — sigue adentro de la transacción y del lock igual,
    /// porque el mínimo cuenta hermanos y sin el lock dos bajas simultáneas podrían leer dos y
    /// dejar la empresa en cero. *(BO-R10, TO-R4)*
    /// </summary>
    public async Task EliminarPuntoVentaAsync(int id, CancellationToken ct = default)
    {
        var puntoVenta = await BuscarPuntoVentaAsync(id, ct);

        await EnUnaTransaccionAsync(async () =>
        {
            await TomarLockDeBajaAsync(puntoVenta.IdTenant, ct);

            var bajo = await BuscarPuntoVentaAsync(id, ct);

            var vivos = await db.PuntosVenta.CountAsync(p => p.IdEmpresa == bajo.IdEmpresa, ct);

            if (vivos <= 1)
            {
                throw ErrorDominio.Conflicto(
                    "ultimo_punto_venta_de_la_empresa",
                    "Es el único punto de venta de la empresa: si querés eliminarlo, dá de baja la empresa.");
            }

            await ExigirSinUsoAsync(
                typeof(PuntoVenta),
                bajo.CreatedAt,
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["Id"] = bajo.Id,
                    ["IdTenant"] = bajo.IdTenant
                },
                "punto_venta_en_uso",
                "el punto de venta",
                ct);

            Marcar(bajo, reloj.Ahora);

            await db.SaveChangesAsync(ct);
            return true;
        }, ct);
    }

    private async Task<PuntoVenta> BuscarPuntoVentaAsync(int id, CancellationToken ct)
    {
        var puntoVenta = await db.PuntosVenta.FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el punto de venta {id}.");

        PoliticaDeRoles.ValidarAlcanceDeTenant(Actor, puntoVenta.IdTenant);

        return puntoVenta;
    }

    // --- Común ---

    /// <summary>
    /// Una escritura y su relectura, en UNA sola transacción, envueltas en
    /// <c>CreateExecutionStrategy().ExecuteAsync</c> — nunca <c>BeginTransactionAsync</c> por
    /// afuera, que es la trampa que ADR-16 documenta: con reintentos habilitados
    /// (<c>EnableRetryOnFailure</c>) EF exige que la transacción viva ADENTRO de la estrategia,
    /// porque un reintento tiene que rehacer la unidad completa.
    ///
    /// Es también la respuesta a la entrada de judgment-day de la slice 1 (opción (a) de las dos
    /// que estaban sobre la mesa): las cuatro escrituras releían DESPUÉS del commit y podían
    /// devolver 404 por una escritura que sí había persistido, en cuanto esta slice agregara los
    /// escritores de baja. Adentro de una transacción el UPDATE ya dejó tomado el lock de fila,
    /// así que ninguna baja concurrente puede volver invisible a la fila entre la escritura y la
    /// relectura. Se eligió (a) y no (b) —devolver la entidad que ya se tiene en la mano— porque
    /// los contadores y los nombres de dueño NO viven en la entidad: (b) habría necesitado una
    /// consulta igual.
    /// </summary>
    private async Task<T> EnUnaTransaccionAsync<T>(Func<Task<T>> cuerpo, CancellationToken ct) =>
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);

            var resultado = await cuerpo();

            await transaccion.CommitAsync(ct);
            return resultado;
        });

    /// <summary>
    /// <c>pg_advisory_xact_lock</c> con alcance de TRANSACCIÓN sobre el TENANT dueño (design D11).
    /// Se suelta solo en el COMMIT/ROLLBACK. Mismo idioma de ADO crudo que
    /// <c>ServicioDePrecios.TomarLockDelParAsync</c> y <c>ServicioDeOfertas.TomarLockDeOfertaAsync</c>:
    /// la conexión se abre por <c>Database.OpenConnectionAsync</c> y nunca por
    /// <c>conexion.OpenAsync()</c>, porque ese segundo camino no dispara
    /// <c>InterceptorDeContextoDeTenant</c> y la conexión quedaría sin los GUC de RLS.
    /// </summary>
    private async Task TomarLockDeBajaAsync(int idTenant, CancellationToken ct)
    {
        var conexion = await ObtenerConexionAbiertaAsync(ct);

        await using var comando = conexion.CreateCommand();
        comando.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        comando.CommandText = "SELECT pg_advisory_xact_lock($1, $2)";

        ParametrosDeComando.Agregar(comando, idTenant);
        ParametrosDeComando.Agregar(comando, ClaveDeLockDeBaja);

        await comando.ExecuteNonQueryAsync(ct);
    }

    private async Task<DbConnection> ObtenerConexionAbiertaAsync(CancellationToken ct)
    {
        var conexion = db.Database.GetDbConnection();

        if (conexion.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        return conexion;
    }

    /// <summary>
    /// El guard de uso, evaluado UNA sola vez sobre el ancla ya releída bajo el lock. Traduce el
    /// nombre de tabla que devuelve <see cref="InspectorDeUso"/> a la frase que ve el operador
    /// (<see cref="EtiquetasDeTablas"/>) y lo levanta como <c>409</c> con el código exacto.
    ///
    /// <paramref name="valoresPorPropiedad"/> es por NOMBRE y no posicional a propósito: el orden
    /// real lo define <c>InventarioDeDependientes.PropiedadesDeAncla</c> (ordinal), y hacer que el
    /// llamador lo replique a mano sería exactamente el tipo de acoplamiento que se rompe en
    /// silencio el día que una clave compuesta cambie. Una propiedad que falte es una
    /// imposibilidad mecánica y se tira nombrándola, nunca se liga un valor equivocado.
    /// </summary>
    private async Task ExigirSinUsoAsync(
        Type tipoAncla,
        DateTimeOffset ancla,
        IReadOnlyDictionary<string, object> valoresPorPropiedad,
        string codigo,
        string sujeto,
        CancellationToken ct)
    {
        var propiedades = InventarioDeDependientes.PropiedadesDeAncla(db.Model, tipoAncla);

        var valoresDeClave = propiedades
            .Select(propiedad => valoresPorPropiedad.TryGetValue(propiedad, out var valor)
                ? valor
                : throw new InvalidOperationException(
                    $"La baja de {tipoAncla.Name} no tiene valor para la propiedad de ancla " +
                    $"{propiedad}, que alguna rama del inspector necesita."))
            .ToList();

        var tabla = await inspector.PrimeraDependenciaEnUsoAsync(tipoAncla, valoresDeClave, ancla, ct);

        if (tabla is null)
        {
            return;
        }

        var descripcion = EtiquetasDeTablas.DescribirBloqueo(
            tabla, InventarioDeDependientes.Construir(db.Model, tipoAncla));

        throw ErrorDominio.Conflicto(codigo, $"No se puede dar de baja {sujeto} porque tiene {descripcion}.");
    }

    /// <summary>Estampa la baja lógica: <c>deleted_at</c> Y <c>updated_at</c> con el MISMO
    /// instante que comparte toda la cascada.</summary>
    private static void Marcar(EntidadBase entidad, DateTimeOffset momento)
    {
        entidad.DeletedAt = momento;
        entidad.UpdatedAt = momento;
    }

    private static void Marcar<T>(IReadOnlyList<T> entidades, DateTimeOffset momento)
        where T : EntidadBase
    {
        foreach (var entidad in entidades)
        {
            Marcar(entidad, momento);
        }
    }

    private static string Normalizar(string? valor, string codigo, string campo, int largoMaximo)
    {
        var limpio = valor?.Trim() ?? string.Empty;

        if (limpio.Length == 0)
        {
            throw new ErrorDominio($"{codigo}_requerido", $"El campo {campo} es obligatorio.", 400);
        }

        if (limpio.Length > largoMaximo)
        {
            throw new ErrorDominio(
                $"{codigo}_muy_largo", $"El campo {campo} no puede superar los {largoMaximo} caracteres.", 400);
        }

        return limpio;
    }

    private static string? NormalizarOpcional(string? valor, string codigo, string campo, int largoMaximo)
    {
        if (string.IsNullOrWhiteSpace(valor))
        {
            return null;
        }

        var limpio = valor.Trim();

        if (limpio.Length > largoMaximo)
        {
            throw new ErrorDominio(
                $"{codigo}_muy_largo", $"El campo {campo} no puede superar los {largoMaximo} caracteres.", 400);
        }

        return limpio;
    }
}
