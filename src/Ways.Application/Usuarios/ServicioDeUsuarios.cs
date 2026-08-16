using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ways.Application.Abstracciones;
using Ways.Application.Auditoria;
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
/// es un error, es el límite documentado en el design (task 2.20).
/// </summary>
public class ServicioDeUsuarios(
    IWaysDbContext db,
    [FromKeyedServices(ClavesDeContexto.Plataforma)] IWaysDbContext dbPlataforma,
    IHasheadorDeContrasenas hasheador,
    IRelojDelSistema reloj,
    IContextoDeUsuario contexto,
    ServicioDeAuditoria auditoria)
{
    private const int LargoMinimoPassword = 8;

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
                u.Rol!.Nombre, u.Estado, u.UltimaConexion, u.CreatedAt))
            .ToListAsync(ct);

        return new PaginaDe<UsuarioListado>(items, total, pagina, tamanio);
    }

    public async Task<UsuarioListado> ObtenerAsync(int id, CancellationToken ct = default)
    {
        ExigirPermisoDeGestion();

        var usuario = await BuscarAsync(id, ct);

        return new UsuarioListado(
            usuario.Id, usuario.NombreUsuario, usuario.Mail, usuario.RolId,
            usuario.Rol!.Nombre, usuario.Estado, usuario.UltimaConexion, usuario.CreatedAt);
    }

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

            // Judgment-day slice 2 (ronda 1, juez B, sugerencia aplicada): la entidad se
            // construye ACÁ ADENTRO, no antes de CreateExecutionStrategy — mismo patrón que
            // ServicioDePrecios.AbrirNuevoPrecioAsync/ServicioDeAprovisionamiento, para que un
            // reintento de la estrategia (transitorio, ADR-16) parta de una entidad fresca en
            // vez de reusar la misma instancia (ya potencialmente trackeada por un intento
            // previo) entre reintentos.
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

    /// <summary>Baja lógica: escribe deleted_at, no borra la fila.</summary>
    public async Task EliminarAsync(int id, CancellationToken ct = default)
    {
        var usuario = await BuscarAsync(id, ct);

        PoliticaDeRoles.ValidarPuedeIntervenirSobre(
            contexto.Rol, contexto.UsuarioId, (RolConocido)usuario.RolId, usuario.Id, esBaja: true);

        // Task 2.5 — un único `momento` para la entidad Y el payload (Orchestrator Decision #2):
        // `{deleted_at: null, estado}` → `{deleted_at: momento, estado}`, nunca
        // `{estado:"eliminado"}` (no es un valor de EstadoUsuario).
        var momento = reloj.Ahora;

        usuario.DeletedAt = momento;
        usuario.UpdatedAt = momento;

        if (usuario.IdTenant is int idTenantSujeto)
        {
            var (valorAnterior, valorNuevo) = PayloadDeAuditoria.BajaDeUsuario(usuario.Estado, momento);

            auditoria.Registrar(new RegistroDeAuditoria(
                idTenantSujeto, idPuntoVenta: null, AccionAuditada.UsuarioBaja, usuario.Id,
                valorAnterior, valorNuevo));
        }

        await db.SaveChangesAsync(ct);
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
