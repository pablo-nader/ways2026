using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Clientes;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Common;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;

namespace Ways.Application.Organizacion;

/// <summary>
/// Aprovisiona un tenant nuevo de punta a punta (ADR-16): tenant + empresa + punto de venta
/// + la plantilla (<see cref="PlantillaDeAprovisionamiento"/>) + el usuario admin, todo en
/// una única transacción — si algo falla a mitad de camino, no queda nada a medio crear.
/// Solo lo puede invocar la plataforma (aplicado en la capa de API, <c>Politicas.SoloPlataforma</c>).
/// </summary>
public class ServicioDeAprovisionamiento(
    IWaysDbContext db,
    ITenantActual tenantActual,
    IHasheadorDeContrasenas hasheador,
    IRelojDelSistema reloj)
{
    private const string CaracteresPassword = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";
    private const int LargoPassword = 16;

    public async Task<ResultadoAprovisionamiento> CrearTenantAsync(
        SolicitudDeAprovisionamiento solicitud, CancellationToken ct = default)
    {
        var nombreTenant = Normalizar(solicitud.NombreTenant, "nombre_tenant", "nombre del tenant", 150);
        var razonSocial = Normalizar(solicitud.RazonSocialEmpresa, "razon_social", "razón social", 150);
        var nombrePuntoVenta = Normalizar(solicitud.NombrePuntoVenta, "punto_venta", "punto de venta", 150);
        var mailAdmin = Normalizar(solicitud.MailAdmin, "mail_admin", "mail del admin", 255);

        // ADR-16: EnableRetryOnFailure ya está configurado (DependencyInjection); con una
        // estrategia de reintento, EF tira si se abre una transacción por fuera de
        // ExecuteAsync — esta trampa está documentada desde design.md y es la razón de todo
        // este wrapper.
        //
        // Sin reintento: TODAS las entidades del aprovisionamiento se construyen de cero en cada
        // intento y ninguna tiene clave de idempotencia — un reintento las duplicaría. Que hoy
        // ux_usuarios_mail (el INSERT final del admin) aborte la transacción entera es un
        // accidente del orden de inserción, no una garantía.
        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        return await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);

            var ahora = reloj.Ahora;

            // 1. tenants — todavía en modo plataforma (el modo con el que arrancó la request).
            var tenant = new Tenant
            {
                Nombre = nombreTenant,
                Estado = EstadoTenant.Activo,
                CreatedAt = ahora,
                UpdatedAt = ahora
            };
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync(ct);

            // 2. Suplantar: de acá en más, el filtro/estampado de EF y RLS ven al tenant
            // recién creado, no a la plataforma. La conexión de la transacción ya estaba
            // abierta antes de este punto — por eso el reaplicado explícito del GUC.
            using var suplantacion = tenantActual.Suplantar(tenant.Id);
            await tenantActual.ReaplicarSobreConexionAsync(db.Database.GetDbConnection(), ct);

            // 3. empresa + punto de venta + plantilla (área + medios de pago).
            var empresa = new Empresa
            {
                RazonSocial = razonSocial,
                CreatedAt = ahora,
                UpdatedAt = ahora
            };
            db.Empresas.Add(empresa);
            await db.SaveChangesAsync(ct);

            var puntoVenta = new PuntoVenta
            {
                IdEmpresa = empresa.Id,
                Nombre = nombrePuntoVenta,
                CreatedAt = ahora,
                UpdatedAt = ahora
            };
            db.PuntosVenta.Add(puntoVenta);

            db.Areas.Add(new Area
            {
                Nombre = PlantillaDeAprovisionamiento.V1.Area,
                Orden = 1,
                Activo = true,
                CreatedAt = ahora,
                UpdatedAt = ahora
            });

            var orden = 1;
            foreach (var medio in PlantillaDeAprovisionamiento.V1.MediosDePago)
            {
                db.MediosPago.Add(new MedioPago
                {
                    Nombre = medio.Nombre,
                    Orden = orden++,
                    Comportamiento = medio.Comportamiento,
                    AdmiteVuelto = medio.AdmiteVuelto,
                    RequiereReferencia = medio.RequiereReferencia,
                    Activo = true,
                    CreatedAt = ahora,
                    UpdatedAt = ahora
                });
            }

            await db.SaveChangesAsync(ct);

            // 3.5. lista de precios General (es_default) + cliente Consumidor Final
            // (design decision 5, stage-2-clientes-proveedores): misma transacción atómica
            // que el resto — si cualquiera de los dos falla, no queda ni el tenant ni la
            // empresa a medio crear (spec: Tenant Provisioning With Template Seed).
            var listaPrecioGeneral = new ListaPrecio
            {
                Nombre = PlantillaDeAprovisionamiento.V1.ListaPrecioGeneral.Nombre,
                EsDefault = true,
                Modo = ModoLista.Fija,
                Activo = true,
                CreatedAt = ahora,
                UpdatedAt = ahora
            };
            db.ListasPrecio.Add(listaPrecioGeneral);

            var condicionFiscalCf = await db.CondicionesFiscales.SingleAsync(
                c => c.Codigo == PlantillaDeAprovisionamiento.V1.ClienteConsumidorFinal.CodigoCondicionFiscal, ct);

            // SaveChanges antes de asignar el numero: listaPrecioGeneral.Id todavía no
            // existe (identity), y clientes.id_lista_precio lo necesita.
            await db.SaveChangesAsync(ct);

            await AsignadorDeNumeroCliente.AsegurarContadorAsync(db, tenant.Id, ct);
            var numeroConsumidorFinal = await AsignadorDeNumeroCliente.AsignarSiguienteAsync(db, tenant.Id, ct);

            db.Clientes.Add(new Cliente
            {
                Numero = numeroConsumidorFinal,
                Nombre = PlantillaDeAprovisionamiento.V1.ClienteConsumidorFinal.Nombre,
                IdCondicionFiscal = condicionFiscalCf.Id,
                IdListaPrecio = listaPrecioGeneral.Id,
                CreatedAt = ahora,
                UpdatedAt = ahora
            });

            await db.SaveChangesAsync(ct);

            // 4. usuario admin del tenant — password temporal, se devuelve una sola vez.
            var passwordTemporal = GenerarPasswordTemporal();
            var admin = new Usuario
            {
                IdTenant = tenant.Id,
                NombreUsuario = "admin",
                Mail = mailAdmin,
                RolId = (int)RolConocido.Admin,
                PasswordHash = hasheador.Hashear(passwordTemporal),
                PasswordAlgoritmo = hasheador.Algoritmo,
                PasswordActualizadoEl = ahora,
                CreatedAt = ahora,
                UpdatedAt = ahora
            };
            db.Usuarios.Add(admin);
            await db.SaveChangesAsync(ct);

            await transaccion.CommitAsync(ct);

            return new ResultadoAprovisionamiento(
                tenant.Id, empresa.Id, puntoVenta.Id, admin.Id, passwordTemporal);
        });
    }

    private static string GenerarPasswordTemporal() =>
        RandomNumberGenerator.GetString(CaracteresPassword, LargoPassword);

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
}
