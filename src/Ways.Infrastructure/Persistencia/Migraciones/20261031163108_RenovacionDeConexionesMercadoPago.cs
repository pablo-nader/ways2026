using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class RenovacionDeConexionesMercadoPago : Migration
    {
        public const string SqlDeBajaDeRequiereReconexion =
            """
            SET LOCAL app.acceso = 'plataforma';
            -- rls-migration-backfills: conexiones_mercadopago corre con FORCE ROW LEVEL SECURITY; sin el GUC de plataforma el rol
            -- de la aplicación no ve ninguna fila y el UPDATE no escribe nada.
            UPDATE conexiones_mercadopago
            SET estado = 'desconectada',
                fecha_desconexion = COALESCE(fecha_requiere_reconexion, updated_at)
            WHERE estado = 'requiere_reconexion';
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_conexiones_mercadopago_activa",
                table: "conexiones_mercadopago");

            migrationBuilder.DropCheckConstraint(
                name: "ck_conexiones_mercadopago_estado",
                table: "conexiones_mercadopago");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "fecha_requiere_reconexion",
                table: "conexiones_mercadopago",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_conexiones_mercadopago_activa",
                table: "conexiones_mercadopago",
                sql: "estado <> 'activa' OR (fecha_desconexion IS NULL AND fecha_requiere_reconexion IS NULL AND access_token_cifrado IS NOT NULL AND octet_length(access_token_cifrado) > 0 AND access_token_nonce IS NOT NULL AND octet_length(access_token_nonce) = 12 AND access_token_tag IS NOT NULL AND octet_length(access_token_tag) = 16 AND refresh_token_cifrado IS NOT NULL AND octet_length(refresh_token_cifrado) > 0 AND refresh_token_nonce IS NOT NULL AND octet_length(refresh_token_nonce) = 12 AND refresh_token_tag IS NOT NULL AND octet_length(refresh_token_tag) = 16)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_conexiones_mercadopago_estado",
                table: "conexiones_mercadopago",
                sql: "estado IN ('activa', 'desconectada', 'requiere_reconexion')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_conexiones_mercadopago_requiere_reconexion",
                table: "conexiones_mercadopago",
                sql: "estado <> 'requiere_reconexion' OR (fecha_requiere_reconexion IS NOT NULL AND fecha_desconexion IS NULL AND access_token_cifrado IS NULL AND access_token_nonce IS NULL AND access_token_tag IS NULL AND refresh_token_cifrado IS NULL AND refresh_token_nonce IS NULL AND refresh_token_tag IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_conexiones_mercadopago_activa",
                table: "conexiones_mercadopago");

            migrationBuilder.DropCheckConstraint(
                name: "ck_conexiones_mercadopago_estado",
                table: "conexiones_mercadopago");

            migrationBuilder.DropCheckConstraint(
                name: "ck_conexiones_mercadopago_requiere_reconexion",
                table: "conexiones_mercadopago");

            // El estado viejo no conoce requiere_reconexion: esas filas pasan a desconectada, con la fecha en que se marcaron como
            // fecha de desconexión (ya no tienen tokens), antes de que vuelva el CHECK de dos valores y de quitar la columna.
            migrationBuilder.Sql(SqlDeBajaDeRequiereReconexion);

            migrationBuilder.DropColumn(
                name: "fecha_requiere_reconexion",
                table: "conexiones_mercadopago");

            migrationBuilder.AddCheckConstraint(
                name: "ck_conexiones_mercadopago_activa",
                table: "conexiones_mercadopago",
                sql: "estado <> 'activa' OR (fecha_desconexion IS NULL AND access_token_cifrado IS NOT NULL AND octet_length(access_token_cifrado) > 0 AND access_token_nonce IS NOT NULL AND octet_length(access_token_nonce) = 12 AND access_token_tag IS NOT NULL AND octet_length(access_token_tag) = 16 AND refresh_token_cifrado IS NOT NULL AND octet_length(refresh_token_cifrado) > 0 AND refresh_token_nonce IS NOT NULL AND octet_length(refresh_token_nonce) = 12 AND refresh_token_tag IS NOT NULL AND octet_length(refresh_token_tag) = 16)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_conexiones_mercadopago_estado",
                table: "conexiones_mercadopago",
                sql: "estado IN ('activa', 'desconectada')");
        }
    }
}
