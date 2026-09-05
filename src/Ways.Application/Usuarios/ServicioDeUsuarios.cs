using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Ways.Application.Abstracciones;
using Ways.Application.Auditoria;
using Ways.Application.Organizacion;
using Ways.Domain.Auditoria;
using Ways.Domain.Common;
using Ways.Domain.Usuarios;

namespace Ways.Application.Usuarios;

/// <summary>
/// ABM de usuarios. Toda decisión de "quién puede hacer qué" delega en
/// <see cref="PoliticaDeRoles"/>, que es puro dominio y se testea sin base.
///
/// Slice 2 (design "Sujeto sin tenant"): los cinco call sites de <c>usuario.*</c> escriben su
/// fila de auditoría bajo el tenant del SUJETO editado (<c>usuario.IdTenant</c>), nunca el de la
/// sesión — cuando el sujeto es una cuenta de plataforma (<c>IdTenant is null</c>),
/// <c>auditoria.id_tenant NOT NULL</c> no admite la fila, así que esas cinco acciones
/// simplemente NO escriben nada (guard <c>if (usuario.IdTenant is int idTenantSujeto)</c>) — no
/// es un error, es el límite documentado en el design (task 2.20). En <see cref="CrearAsync"/> ese
/// guard es estructuralmente inalcanzable: <see cref="PoliticaDeRoles.ValidarPuedeAsignarRol"/>
/// rechaza asignar el rol Root desde la app, y <see cref="PoliticaDeRoles.ValidarConsistenciaDeRolYAlcance"/>
/// exige tenant (<c>tenant_requerido</c>) para cualquier otro rol — mismo tratamiento que
/// <c>EliminarAsync</c> documenta en <c>PreciosYUsuariosAuditoriaTests</c>.
/// </summary>
public class ServicioDeUsuarios(
    IWaysDbContext db,
    [FromKeyedServices(ClavesDeContexto.Plataforma)] IWaysDbContext dbPlataforma,
    IHasheadorDeContrasenas hasheador,
    IRelojDelSistema reloj,
    IContextoDeUsuario contexto,
    ServicioDeAuditoria auditoria,
    InspectorDeUso inspector)
{
    private const int LargoMinimoPassword = 8;

    /// <summary>
    /// Primera clave del <c>pg_advisory_xact_lock</c> cuando el SUJETO es una cuenta de plataforma
    /// (<c>IdTenant</c> en <c>null</c>): <c>pg_advisory_xact_lock</c> no admite <c>NULL</c>, así que
    /// esas cuentas se serializan todas entre sí sobre este centinela. CERO a propósito — los ids
    /// de tenant salen de una identidad que arranca en 1, así que el centinela no puede colisionar
    /// con ningún tenant real y una baja de cuenta de plataforma nunca queda esperando la baja de
    /// un tenant ajeno.
    /// </summary>
    private const int TenantSentinelaDePlataforma = 0;

    /// <summary>Identidad tenant-aware del actor en curso, para <see cref="PoliticaDeRoles"/>
    /// (doc 09, ADR-8).</summary>
    private ActorDeGestion Actor => new(contexto.Rol, contexto.UsuarioId, contexto.IdTenant);

    public async Task<PaginaDe<UsuarioListado>> ListarAsync(
        string? busqueda = null,
        bool incluirEliminados = false,
        int pagina = 1,
        int tamanio = 25,
        CancellationToken ct = default)
    {
        ExigirPermisoDeGestion();

        pagina = Math.Max(pagina, 1);
        tamanio = Math.Clamp(tamanio, 1, 200);

        var query = db.Usuarios.Include(u => u.Rol).AsQueryable();

        if (incluirEliminados)
        {
            // Solo la baja lógica: ignorar todos los filtros de un tirón también saltearía
            // el de tenant (ADR-6) y filtraría cuentas de otros tenants al admin.
            query = query.IgnoreQueryFilters(["BajaLogica"]);
        }

        if (!string.IsNullOrWhiteSpace(busqueda))
        {
            // Las columnas son citext, así que el LIKE que genera Contains
            // ya es case-insensitive sin necesidad de ILIKE.
            var termino = busqueda.Trim();
            query = query.Where(u =>
                u.NombreUsuario.Contains(termino) || u.Mail.Contains(termino));
        }

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderBy(u => u.RolId).ThenBy(u => u.NombreUsuario)
            .Skip((pagina - 1) * tamanio)
            .Take(tamanio)
            .Select(u => new UsuarioListado(
                u.Id, u.NombreUsuario, u.Mail, u.RolId,
                u.Rol!.Nombre, u.Estado, u.UltimaConexion, u.CreatedAt,
                u.IdTenant,
                // El `DeletedAt == null` va explícito y no se apoya en el filtro ambiente:
                // `IgnoreQueryFilters` se aplica a nivel CONSULTA, así que con
                // `incluirEliminados` la subconsulta también perdería el filtro y devolvería el
                // nombre de un tenant dado de baja, discrepando del listado por defecto y de
                // ObtenerAsync. Con el predicado propio, los tres caminos coinciden (design D13:
                // el huérfano se muestra con nombre nulo).
                db.Tenants
                    .Where(t => t.Id == u.IdTenant && t.DeletedAt == null)
                    .Select(t => t.Nombre)
                    .FirstOrDefault()))
            .ToListAsync(ct);

        return new PaginaDe<UsuarioListado>(items, total, pagina, tamanio);
    }

    public async Task<UsuarioListado> ObtenerAsync(int id, CancellationToken ct = default)
    {
        ExigirPermisoDeGestion();

        var usuario = await BuscarAsync(id, ct);

        return new UsuarioListado(
            usuario.Id, usuario.NombreUsuario, usuario.Mail, usuario.RolId,
            usuario.Rol!.Nombre, usuario.Estado, usuario.UltimaConexion, usuario.CreatedAt,
            usuario.IdTenant, await NombreDeTenantAsync(usuario.IdTenant, ct));
    }

    /// <summary>El nombre del tenant de una cuenta (design D14, S1). Viene <c>null</c> en dos
    /// casos, no en uno: cuando la cuenta es de plataforma (<c>IdTenant</c> nulo) y cuando el
    /// tenant dueño está dado de baja lógicamente — ahí <c>IdTenant</c> NO es nulo y el nombre
    /// igual falta, porque D13 elige mostrar al huérfano como anomalía en vez de esconderlo. Un
    /// consumidor no puede leer el nombre nulo como "es personal de plataforma": para eso está
    /// <c>IdTenant</c>. La etiqueta <c>"Plataforma"</c> NO se fabrica acá — es copia de pantalla, la pone la web:
    /// el nombre de un tenant es texto libre y un tenant que se llamara justo "Plataforma" sería
    /// indistinguible de una cuenta de plataforma. En el listado esto viaja como subconsulta
    /// escalar correlacionada dentro del mismo <c>Select</c>; acá, sobre una sola fila, es una
    /// consulta puntual y solo cuando la cuenta pertenece a un tenant.
    ///
    /// Etapa 20 slice 4 — acá NO va un <c>DeletedAt == null</c> explícito, y es la misma regla
    /// única que documenta <c>ServicioDeOrganizacion.ProyeccionDeTenant</c> leída del otro lado:
    /// el predicado propio hace falta donde la consulta puede PERDER el filtro ambiente, o sea
    /// donde la expresión es componible por un llamador (las tres proyecciones de organización) o
    /// donde el método mismo apaga el filtro (<see cref="ListarAsync"/> con
    /// <c>incluirEliminados</c>, y por eso su subconsulta sí lo lleva). Esta consulta se arma y se
    /// ejecuta entera acá adentro y nadie puede componerle un <c>IgnoreQueryFilters</c>: el
    /// predicado explícito que traía era literalmente irrefutable —su mutación sobrevivió en la
    /// ronda 2 de la slice 1 y sigue sin tener forma de morir— y se saca en vez de arrastrarlo
    /// como deuda una tercera vuelta. El comportamiento no cambia en ningún camino: el filtro
    /// ambiente ya deja afuera al tenant dado de baja, que es lo que afirma
    /// <c>UnaCuentaCuyoTenantFueDadoDeBajaNoTraeNombreDeTenantEnNingunoDeLosTresCaminos</c>.</summary>
    private async Task<string?> NombreDeTenantAsync(int? idTenant, CancellationToken ct) =>
        idTenant is int id
            ? await db.Tenants
                .Where(t => t.Id == id)
                .Select(t => t.Nombre)
                .FirstOrDefaultAsync(ct)
            : null;

    public async Task<UsuarioListado> CrearAsync(CrearUsuario datos, CancellationToken ct = default)
    {
        PoliticaDeRoles.ValidarPuedeAsignarRol(contexto.Rol, (RolConocido)datos.RolId);

        // Un actor de tenant siempre crea dentro de su propio tenant — el valor que haya
        // llegado en `datos.IdTenant` se ignora, no se confía en lo que mande el cliente
        // (doc 09: el tenant nunca viaja como parámetro editable). Solo un actor de
        // plataforma puede (y tiene que) elegir el tenant destino explícitamente.
        var idTenantDestino = Actor.EsDePlataforma ? datos.IdTenant : Actor.IdTenant;
        PoliticaDeRoles.ValidarConsistenciaDeRolYAlcance((RolConocido)datos.RolId, idTenantDestino);

        var nombre = Normalizar(datos.Usuario, "usuario", 40);
        var mail = Normalizar(datos.Mail, "mail", 255);
        ValidarPassword(datos.Password);
        await ExigirRolExistenteAsync(datos.RolId, ct);
        await ExigirDisponibilidadAsync(nombre, mail, idTenantDestino, null, ct);

        var ahora = reloj.Ahora;

        // Judgment-day slice 2 (ronda 2, juez A, WARNING — revierte la sugerencia de ronda 1):
        // la entidad se construye ACÁ AFUERA, no dentro del lambda de ExecutionStrategy. Si
        // viviera adentro, un reintento transitorio (ADR-16) construiría una instancia NUEVA
        // mientras el ChangeTracker todavía retiene la del intento fallido: el reintento haría
        // un segundo Add de una entidad distinta y SaveChangesAsync insertaría las DOS filas
        // (doble alta). Con la instancia construida una sola vez afuera, el reintento vuelve a
        // hacer `Add` de la MISMA instancia — EF lo trata como upsert idempotente sobre esa
        // entidad — y el id explícito es válido porque la identity de la tabla es
        // `GENERATED BY DEFAULT`, no `ALWAYS`.
        var usuario = new Usuario
        {
            NombreUsuario = nombre,
            Mail = mail,
            RolId = datos.RolId,
            IdTenant = idTenantDestino,
            Estado = datos.Estado,
            PasswordHash = hasheador.Hashear(datos.Password),
            PasswordAlgoritmo = hasheador.Algoritmo,
            PasswordActualizadoEl = ahora,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };

        // Design decisión 11 / task 2.3 — el ÚNICO call site que cambia la estructura
        // transaccional de su llamador: `id_entidad` es polimórfico y sin FK, así que el id de
        // `usuario` no existe hasta el PRIMER flush. Transacción explícita (mismo patrón
        // CreateExecutionStrategy + BeginTransactionAsync que ServicioDePrecios) con DOS
        // SaveChangesAsync: alta → flush → auditoría con el id ya generado → flush → commit. Si
        // el segundo flush (el INSERT de auditoría) falla, la transacción entera revierte y el
        // alta queda sin efecto (fail-closed, task 2.13) — con dos SaveChangesAsync SUELTOS
        // (mutation target 2.12) el primero ya habría comiteado solo.
        var estrategia = db.Database.CreateExecutionStrategy();

        var usuarioCreado = await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);

            db.Usuarios.Add(usuario);
            await db.SaveChangesAsync(ct);

            if (idTenantDestino is int idTenantSujeto)
            {
                var (valorAnterior, valorNuevo) = PayloadDeAuditoria.AltaDeUsuario(
                    usuario.NombreUsuario, usuario.Mail, usuario.RolId, usuario.Estado);

                auditoria.Registrar(new RegistroDeAuditoria(
                    idTenantSujeto, idPuntoVenta: null, AccionAuditada.UsuarioAlta, usuario.Id,
                    valorAnterior, valorNuevo));

                await db.SaveChangesAsync(ct);
            }

            await transaccion.CommitAsync(ct);
            return usuario;
        });

        return await ObtenerAsync(usuarioCreado.Id, ct);
    }

    public async Task<UsuarioListado> ActualizarAsync(
        int id, ActualizarUsuario datos, CancellationToken ct = default)
    {
        var usuario = await BuscarAsync(id, ct);

        PoliticaDeRoles.ValidarPuedeIntervenirSobre(
            contexto.Rol, contexto.UsuarioId, (RolConocido)usuario.RolId, usuario.Id, esBaja: false);

        // Cambiar el rol se valida aparte: podés editar el mail de un supervisor
        // sin tener permiso para convertirlo en admin. El tenant de la cuenta no se toca
        // acá (es inmutable una vez creada), solo se revalida que el rol nuevo siga siendo
        // consistente con él.
        if (usuario.RolId != datos.RolId)
        {
            PoliticaDeRoles.ValidarPuedeAsignarRol(contexto.Rol, (RolConocido)datos.RolId);
            PoliticaDeRoles.ValidarConsistenciaDeRolYAlcance((RolConocido)datos.RolId, usuario.IdTenant);
            await ExigirRolExistenteAsync(datos.RolId, ct);
        }

        var nombre = Normalizar(datos.Usuario, "usuario", 40);
        var mail = Normalizar(datos.Mail, "mail", 255);
        await ExigirDisponibilidadAsync(nombre, mail, usuario.IdTenant, id, ct);

        var estabaBloqueado = usuario.Estado == EstadoUsuario.Bloqueado;

        // Task 2.4 / mutation target 2.14 — captura del payload ANTES de mutar la entidad,
        // mientras los valores viejos todavía están en memoria: moverla después de las cuatro
        // asignaciones de abajo deja `valorAnterior == valorNuevo` en la fila de auditoría.
        var usuarioAnterior = usuario.NombreUsuario;
        var mailAnterior = usuario.Mail;
        var idRolAnterior = usuario.RolId;
        var estadoAnterior = usuario.Estado;

        usuario.NombreUsuario = nombre;
        usuario.Mail = mail;
        usuario.RolId = datos.RolId;
        usuario.Estado = datos.Estado;
        usuario.UpdatedAt = reloj.Ahora;

        // Pasar de bloqueado a activo limpia el contador de intentos fallidos,
        // si no la cuenta se vuelve a bloquear al primer error.
        if (estabaBloqueado && datos.Estado == EstadoUsuario.Activo)
        {
            usuario.Desbloquear(reloj.Ahora);
        }

        if (usuario.IdTenant is int idTenantSujeto)
        {
            var (valorAnterior, valorNuevo) = PayloadDeAuditoria.ActualizacionDeUsuario(
                usuarioAnterior, mailAnterior, idRolAnterior, estadoAnterior,
                usuario.NombreUsuario, usuario.Mail, usuario.RolId, usuario.Estado);

            auditoria.Registrar(new RegistroDeAuditoria(
                idTenantSujeto, idPuntoVenta: null, AccionAuditada.UsuarioActualizacion, usuario.Id,
                valorAnterior, valorNuevo));
        }

        await db.SaveChangesAsync(ct);
        return await ObtenerAsync(id, ct);
    }

    public async Task CambiarPasswordAsync(
        int id, CambiarPassword datos, CancellationToken ct = default)
    {
        var usuario = await BuscarAsync(id, ct);

        // Cualquiera puede cambiar su propia contraseña; sobre la de otro rigen las reglas de rol.
        if (usuario.Id != contexto.UsuarioId)
        {
            PoliticaDeRoles.ValidarPuedeIntervenirSobre(
                contexto.Rol, contexto.UsuarioId, (RolConocido)usuario.RolId, usuario.Id, esBaja: false);
        }

        ValidarPassword(datos.PasswordNueva);

        usuario.CambiarPassword(
            hasheador.Hashear(datos.PasswordNueva), hasheador.Algoritmo, reloj.Ahora);

        if (usuario.IdTenant is int idTenantSujeto)
        {
            // Task 2.7 — JAMÁS el hash: solo el hecho de quién lo cambió.
            var (valorAnterior, valorNuevo) = PayloadDeAuditoria.CambioDePassword(
                usuario.Id == contexto.UsuarioId);

            auditoria.Registrar(new RegistroDeAuditoria(
                idTenantSujeto, idPuntoVenta: null, AccionAuditada.UsuarioPassword, usuario.Id,
                valorAnterior, valorNuevo));
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task DesbloquearAsync(int id, CancellationToken ct = default)
    {
        var usuario = await BuscarAsync(id, ct);

        PoliticaDeRoles.ValidarPuedeIntervenirSobre(
            contexto.Rol, contexto.UsuarioId, (RolConocido)usuario.RolId, usuario.Id, esBaja: false);

        // Task 2.6 — el ANTES real, leído antes de `Desbloquear` (no asumido "bloqueado": el
        // método corre igual aunque la cuenta ya esté activa).
        var estadoAnterior = usuario.Estado;

        usuario.Desbloquear(reloj.Ahora);

        if (usuario.IdTenant is int idTenantSujeto)
        {
            var (valorAnterior, valorNuevo) = PayloadDeAuditoria.DesbloqueoDeUsuario(
                estadoAnterior, usuario.Estado);

            auditoria.Registrar(new RegistroDeAuditoria(
                idTenantSujeto, idPuntoVenta: null, AccionAuditada.UsuarioDesbloqueo, usuario.Id,
                valorAnterior, valorNuevo));
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Baja lógica: escribe deleted_at, no borra la fila.
    ///
    /// Etapa 20 slice 4 (UT-R2) — el guard de uso entra acá DESPUÉS de
    /// <see cref="PoliticaDeRoles.ValidarPuedeIntervenirSobre"/> y NUNCA en su lugar: quién puede
    /// intervenir sobre quién es una decisión de dominio que no depende de los datos, y el guard
    /// es una pregunta a la base. Invertir el orden le contestaría a un actor sin permiso si la
    /// cuenta objetivo tiene movimientos, que es justo el oráculo que ADR-8 evita. Todo lo que ya
    /// regía queda igual: un objetivo Root sigue siendo indeleteable con el error de
    /// <c>PoliticaDeRoles</c> (no con <c>usuario_en_uso</c>), la autobaja sigue prohibida, el 404
    /// deliberado de <c>ValidarAlcanceDeTenant</c> sigue tapando la existencia de otro tenant y la
    /// fila de auditoría se sigue escribiendo.
    ///
    /// CON transacción y CON lock desde judgment-day ronda 1 (hallazgo C2). La versión anterior
    /// corría el guard como un SELECT suelto y estampaba <c>deleted_at</c> en otro statement, con
    /// una ventana entre los dos: una venta o un turno abiertos por el MISMO empleado en el medio
    /// eran invisibles para el guard, y la cuenta quedaba dada de baja estando en uso. Ahora el
    /// guard, el estampado y la fila de auditoría viven en UNA transacción, bajo el MISMO
    /// <c>pg_advisory_xact_lock(idTenant, <see cref="ServicioDeOrganizacion.ClaveDeLockDeBaja"/>)</c>
    /// que toman las bajas de organización — así la baja de un usuario y la del tenant que lo
    /// contiene se serializan entre sí en vez de pisarse.
    ///
    /// Y RELEE AL SUJETO BAJO EL LOCK desde judgment-day ronda 2 (hallazgo R2-2), igual que las
    /// tres bajas de organización: la lectura de arriba es de ANTES del lock, así que el perdedor
    /// de una baja concurrente —la cascada del tenant, típicamente— re-estampaba un
    /// <c>deleted_at</c> nuevo sobre una fila ya dada de baja y escribía un segundo
    /// <c>usuario.baja</c>. Con la relectura eso es un 404, y el instante compartido de la cascada
    /// queda intacto.
    ///
    /// <see cref="PoliticaDeRoles.ValidarPuedeIntervenirSobre"/> queda AFUERA de la transacción a
    /// propósito: es una decisión de dominio que no depende de los datos, no necesita el lock, y
    /// dejarla afuera mantiene el orden observable que UT-R2 afirma (un objetivo Root rinde el
    /// error de <c>PoliticaDeRoles</c>, nunca <c>usuario_en_uso</c>). Lo que SÍ tiene que estar
    /// bajo el lock es el guard, que es la pregunta a la base.
    ///
    /// Residual honesto, el mismo que la tabla G del design registra como R1 para las bajas de
    /// organización: bajo READ COMMITTED una venta puede confirmarse entre el <c>EXISTS</c> del
    /// guard y el commit de la baja. El lock serializa baja-contra-baja, no baja-contra-venta;
    /// cerrar eso pondría un lock de administración sobre el camino caliente del POS.
    /// </summary>
    public async Task EliminarAsync(int id, CancellationToken ct = default)
    {
        var usuario = await BuscarAsync(id, ct);

        PoliticaDeRoles.ValidarPuedeIntervenirSobre(
            contexto.Rol, contexto.UsuarioId, (RolConocido)usuario.RolId, usuario.Id, esBaja: true);

        // La transacción vive ADENTRO de la estrategia de ejecución, nunca al revés (ADR-16), y la
        // estrategia es la SIN REINTENTO (judgment-day ronda 2, hallazgo R2-1): mismo motivo exacto
        // que documenta ServicioDeOrganizacion.EnUnaTransaccionDeBajaAsync — `auditoria.Registrar`
        // hace `Add` de una instancia nueva por llamada y un intento fallido deja la del intento
        // anterior en `Added`, así que un reintento de EnableRetryOnFailure DUPLICA la fila de
        // `usuario.baja` en vez de rehacerla.
        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);

            await TomarLockDeBajaAsync(usuario.IdTenant ?? TenantSentinelaDePlataforma, ct);

            // RELECTURA BAJO EL LOCK (judgment-day ronda 2, hallazgo R2-2), la misma que hacen las
            // tres bajas de ServicioDeOrganizacion. La pre-lectura de arriba es de ANTES del lock:
            // si una baja concurrente —la cascada de su tenant, típicamente— ganó mientras esta
            // esperaba, la fila ya no es visible bajo "BajaLogica" y esto es un 404 limpio. Sin
            // ella, el perdedor de la carrera re-estampa un `deleted_at` NUEVO sobre una fila ya
            // dada de baja y escribe un SEGUNDO `usuario.baja`: el instante compartido que hace
            // exacto al restore de la cascada (`WHERE deleted_at = '<instante>'`) se rompe justo
            // en la cuenta que se re-estampó. `BuscarAsync` revalida además el alcance de tenant,
            // que es barato porque ya está en la misma consulta.
            var sujeto = await BuscarAsync(id, ct);

            var tablaEnUso = await inspector.PrimeraDependenciaEnUsoAsync(
                typeof(Usuario), ValoresDeAnclaDeUsuario(sujeto), sujeto.CreatedAt, ct);

            if (tablaEnUso is not null)
            {
                var descripcion = EtiquetasDeTablas.DescribirBloqueo(tablaEnUso);

                throw ErrorDominio.Conflicto(
                    "usuario_en_uso",
                    $"No se puede dar de baja el usuario porque hay {descripcion} a su nombre.");
            }

            // Task 2.5 — un único `momento` para la entidad Y el payload (Orchestrator Decision #2):
            // `{deleted_at: null, estado}` → `{deleted_at: momento, estado}`, nunca
            // `{estado:"eliminado"}` (no es un valor de EstadoUsuario).
            var momento = reloj.Ahora;

            sujeto.DeletedAt = momento;
            sujeto.UpdatedAt = momento;

            if (sujeto.IdTenant is int idTenantSujeto)
            {
                var (valorAnterior, valorNuevo) = PayloadDeAuditoria.BajaDeUsuario(sujeto.Estado, momento);

                auditoria.Registrar(new RegistroDeAuditoria(
                    idTenantSujeto, idPuntoVenta: null, AccionAuditada.UsuarioBaja, sujeto.Id,
                    valorAnterior, valorNuevo));
            }

            await db.SaveChangesAsync(ct);
            await transaccion.CommitAsync(ct);

            return true;
        });
    }

    /// <summary>
    /// Los valores de clave del ancla resueltos POR NOMBRE contra
    /// <see cref="InventarioDeDependientes.PropiedadesDeAncla"/>, nunca posicionalmente
    /// (judgment-day ronda 1, hallazgo C5). El orden posicional lo define el inventario, no el
    /// llamador: un <c>[usuario.Id]</c> escrito a mano se rompe en silencio —ligando el id al
    /// parámetro equivocado— el día que <c>Usuario</c> gane una clave compuesta. Mismo idioma
    /// exacto que <c>ServicioDeOrganizacion.ExigirSinUsoAsync</c>, y una propiedad que falte es
    /// una imposibilidad mecánica que se tira nombrándola.
    /// </summary>
    private IReadOnlyList<object> ValoresDeAnclaDeUsuario(Usuario usuario)
    {
        var valoresPorPropiedad = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [nameof(Usuario.Id)] = usuario.Id
        };

        return
        [
            .. InventarioDeDependientes.PropiedadesDeAncla(db.Model, typeof(Usuario))
                .Select(propiedad => valoresPorPropiedad.TryGetValue(propiedad, out var valor)
                    ? valor
                    : throw new InvalidOperationException(
                        $"La baja de {nameof(Usuario)} no tiene valor para la propiedad de ancla " +
                        $"{propiedad}, que alguna rama del inspector necesita."))
        ];
    }

    /// <summary>
    /// El MISMO <c>pg_advisory_xact_lock</c> de <c>ServicioDeOrganizacion.TomarLockDeBajaAsync</c>,
    /// con la misma clave y el mismo idioma de ADO crudo: la conexión se abre por
    /// <c>Database.OpenConnectionAsync</c> y nunca por <c>conexion.OpenAsync()</c>, porque ese
    /// segundo camino no dispara <c>InterceptorDeContextoDeTenant</c> y la conexión quedaría sin
    /// los GUC de RLS.
    /// </summary>
    private async Task TomarLockDeBajaAsync(int idTenant, CancellationToken ct)
    {
        var conexion = db.Database.GetDbConnection();

        if (conexion.State != System.Data.ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        await using var comando = conexion.CreateCommand();
        comando.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        comando.CommandText = "SELECT pg_advisory_xact_lock($1, $2)";

        ParametrosDeComando.Agregar(comando, idTenant);
        ParametrosDeComando.Agregar(comando, ServicioDeOrganizacion.ClaveDeLockDeBaja);

        await comando.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Roles que el usuario autenticado puede asignar. Alimenta el combo del ABM.</summary>
    public async Task<IReadOnlyList<RolListado>> RolesAsignablesAsync(CancellationToken ct = default)
    {
        ExigirPermisoDeGestion();

        var permitidos = PoliticaDeRoles.RolesAsignablesPor(contexto.Rol)
            .Select(r => (int)r)
            .ToArray();

        return await db.Roles
            .Where(r => permitidos.Contains(r.Id))
            .OrderBy(r => r.Id)
            .Select(r => new RolListado(r.Id, r.Nombre, r.Descripcion))
            .ToListAsync(ct);
    }

    private void ExigirPermisoDeGestion()
    {
        if (!PoliticaDeRoles.PuedeGestionarUsuarios(contexto.Rol))
        {
            throw ErrorDominio.Prohibido("No tenés permisos para gestionar usuarios.");
        }
    }

    private async Task<Usuario> BuscarAsync(int id, CancellationToken ct)
    {
        var usuario = await db.Usuarios.Include(u => u.Rol).FirstOrDefaultAsync(u => u.Id == id, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el usuario {id}.");

        // El filtro de EF (más RLS por debajo) ya deja invisible una cuenta de otro
        // alcance, así que en la práctica esto nunca dispara sobre una fila que sí llegó a
        // `usuario` — es la capa de defensa explícita del dominio (doc 09, ADR-8), no un
        // sustituto de las otras dos.
        PoliticaDeRoles.ValidarAlcanceDeTenant(Actor, usuario.IdTenant);

        return usuario;
    }

    private async Task ExigirRolExistenteAsync(int rolId, CancellationToken ct)
    {
        if (!await db.Roles.AnyAsync(r => r.Id == rolId, ct))
        {
            throw new ErrorDominio("rol_inexistente", $"No existe el rol {rolId}.");
        }
    }

    private async Task ExigirDisponibilidadAsync(
        string usuario, string mail, int? idTenantScope, int? excluirId, CancellationToken ct)
    {
        // `usuario` es único por tenant, no global (doc 09, ADR-7): dos tenants pueden
        // tener cada uno un "admin" sin choque. La agrupación de plataforma
        // (`id_tenant IS NULL`) es un tenant más a este efecto — `NULLS NOT DISTINCT` en el
        // índice hace que este `== idTenantScope` con `idTenantScope: null` también
        // funcione una vez que exista la columna (gate #2 pendiente).
        // citext: la comparación ya es case-insensitive en el motor.
        var tomadoUsuario = await db.Usuarios.AnyAsync(
            u => u.NombreUsuario == usuario && u.IdTenant == idTenantScope && u.Id != excluirId, ct);

        if (tomadoUsuario)
        {
            throw ErrorDominio.Conflicto("usuario_duplicado", $"El usuario '{usuario}' ya existe.");
        }

        // A diferencia de `usuario`, el mail es único GLOBAL, no por tenant (`ux_usuarios_mail`
        // no lleva id_tenant). `IgnoreQueryFilters(["Tenant"])` NO alcanza acá: solo apaga el
        // filtro de EF, pero la policy `usuarios_tenant` de RLS sigue activa por debajo bajo
        // `app.acceso='tenant'` y le sigue ocultando a un actor de tenant las filas de otro
        // tenant (judgment-day, batch 9, ronda 2) — con eso, la colisión cross-tenant nunca la
        // atrapaba este chequeo, siempre reventaba recién en el `SaveChangesAsync` (23505),
        // que el backstop de `ManejadorDeErrores` traduce igual, pero sin pasar por acá. Por
        // eso el chequeo de mail corre contra `dbPlataforma` (el mismo patrón que el chequeo
        // de suspensión de tenant en `ServicioDeAutenticacion`): esa conexión abre en modo
        // plataforma, así que RLS la deja ver cualquier tenant.
        var tomadoMail = await dbPlataforma.Usuarios.AnyAsync(
            u => u.Mail == mail && u.Id != excluirId, ct);

        if (tomadoMail)
        {
            throw ErrorDominio.Conflicto("mail_duplicado", $"El mail '{mail}' ya está en uso.");
        }
    }

    private static string Normalizar(string? valor, string campo, int largoMaximo)
    {
        var limpio = valor?.Trim() ?? string.Empty;

        if (limpio.Length == 0)
        {
            throw new ErrorDominio($"{campo}_requerido", $"El campo {campo} es obligatorio.", 400);
        }

        if (limpio.Length > largoMaximo)
        {
            throw new ErrorDominio(
                $"{campo}_muy_largo",
                $"El campo {campo} no puede superar los {largoMaximo} caracteres.", 400);
        }

        return limpio;
    }

    private static void ValidarPassword(string? password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < LargoMinimoPassword)
        {
            throw new ErrorDominio(
                "password_debil",
                $"La contraseña debe tener al menos {LargoMinimoPassword} caracteres.", 400);
        }
    }
}
