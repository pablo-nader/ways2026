using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Ways.Infrastructure.Multitenancy;

#nullable disable

namespace Ways.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class ConexionesMercadoPago : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "ak_cuentas_tesoreria_id_cuenta_tesoreria_id_empresa_id_tenant",
                table: "cuentas_tesoreria",
                columns: new[] { "id_cuenta_tesoreria", "id_empresa", "id_tenant" });

            migrationBuilder.CreateTable(
                name: "conexiones_mercadopago",
                columns: table => new
                {
                    id_conexion_mercadopago = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    id_empresa = table.Column<int>(type: "integer", nullable: false),
                    id_cuenta_tesoreria = table.Column<int>(type: "integer", nullable: false),
                    id_usuario_mercadopago = table.Column<long>(type: "bigint", nullable: false),
                    access_token_cifrado = table.Column<byte[]>(type: "bytea", nullable: true),
                    access_token_nonce = table.Column<byte[]>(type: "bytea", nullable: true),
                    access_token_tag = table.Column<byte[]>(type: "bytea", nullable: true),
                    refresh_token_cifrado = table.Column<byte[]>(type: "bytea", nullable: true),
                    refresh_token_nonce = table.Column<byte[]>(type: "bytea", nullable: true),
                    refresh_token_tag = table.Column<byte[]>(type: "bytea", nullable: true),
                    id_clave_maestra = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    access_token_expira_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    estado = table.Column<string>(type: "text", nullable: false),
                    id_usuario_conecto = table.Column<int>(type: "integer", nullable: false),
                    fecha_conexion = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    fecha_desconexion = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    fecha_ultima_renovacion = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    id_tenant = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_conexiones_mercadopago", x => x.id_conexion_mercadopago);
                    table.CheckConstraint("ck_conexiones_mercadopago_activa", "estado <> 'activa' OR (fecha_desconexion IS NULL AND access_token_cifrado IS NOT NULL AND octet_length(access_token_cifrado) > 0 AND access_token_nonce IS NOT NULL AND octet_length(access_token_nonce) = 12 AND access_token_tag IS NOT NULL AND octet_length(access_token_tag) = 16 AND refresh_token_cifrado IS NOT NULL AND octet_length(refresh_token_cifrado) > 0 AND refresh_token_nonce IS NOT NULL AND octet_length(refresh_token_nonce) = 12 AND refresh_token_tag IS NOT NULL AND octet_length(refresh_token_tag) = 16)");
                    table.CheckConstraint("ck_conexiones_mercadopago_desconectada", "estado <> 'desconectada' OR (fecha_desconexion IS NOT NULL AND access_token_cifrado IS NULL AND access_token_nonce IS NULL AND access_token_tag IS NULL AND refresh_token_cifrado IS NULL AND refresh_token_nonce IS NULL AND refresh_token_tag IS NULL)");
                    table.CheckConstraint("ck_conexiones_mercadopago_estado", "estado IN ('activa', 'desconectada')");
                    table.ForeignKey(
                        name: "fk_conexiones_mercadopago_cuenta_tesoreria",
                        columns: x => new { x.id_cuenta_tesoreria, x.id_empresa, x.id_tenant },
                        principalTable: "cuentas_tesoreria",
                        principalColumns: new[] { "id_cuenta_tesoreria", "id_empresa", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_conexiones_mercadopago_empresa",
                        columns: x => new { x.id_empresa, x.id_tenant },
                        principalTable: "empresas",
                        principalColumns: new[] { "id_empresa", "id_tenant" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_conexiones_mercadopago_tenant",
                        column: x => x.id_tenant,
                        principalTable: "tenants",
                        principalColumn: "id_tenant",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_conexiones_mercadopago_usuario_conecto",
                        column: x => x.id_usuario_conecto,
                        principalTable: "usuarios",
                        principalColumn: "id_usuario",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_conexiones_mercadopago_cuenta",
                table: "conexiones_mercadopago",
                columns: new[] { "id_cuenta_tesoreria", "id_empresa", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_conexiones_mercadopago_empresa",
                table: "conexiones_mercadopago",
                columns: new[] { "id_empresa", "id_tenant" });

            migrationBuilder.CreateIndex(
                name: "ix_conexiones_mercadopago_tenant",
                table: "conexiones_mercadopago",
                column: "id_tenant");

            migrationBuilder.CreateIndex(
                name: "ix_conexiones_mercadopago_usuario_conecto",
                table: "conexiones_mercadopago",
                column: "id_usuario_conecto");

            migrationBuilder.CreateIndex(
                name: "ux_conexiones_mercadopago_empresa_activa",
                table: "conexiones_mercadopago",
                columns: new[] { "id_tenant", "id_empresa" },
                unique: true,
                filter: "estado = 'activa'");

            migrationBuilder.CreateIndex(
                name: "ux_conexiones_mercadopago_usuario_activa",
                table: "conexiones_mercadopago",
                column: "id_usuario_mercadopago",
                unique: true,
                filter: "estado = 'activa'");

            migrationBuilder.HabilitarRlsDeTenant("conexiones_mercadopago");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "conexiones_mercadopago");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_cuentas_tesoreria_id_cuenta_tesoreria_id_empresa_id_tenant",
                table: "cuentas_tesoreria");
        }
    }
}
