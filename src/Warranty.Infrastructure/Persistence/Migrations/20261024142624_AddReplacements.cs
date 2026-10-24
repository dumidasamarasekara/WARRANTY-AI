using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Warranty.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReplacements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "issues_replacements",
                schema: "integration",
                table: "service_centers",
                type: "boolean",
                nullable: false,
                defaultValue: false); // hand-edited: existing centres issue no replacements until the seed says so (domain default, data-model.md)

            migrationBuilder.AddUniqueConstraint(
                name: "ak_repair_handovers_tenant_id_id",
                schema: "claims",
                table: "repair_handovers",
                columns: new[] { "tenant_id", "id" });

            migrationBuilder.CreateTable(
                name: "replacement_serials",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    serial_number = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    original_serial = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    claim_id = table.Column<Guid>(type: "uuid", nullable: false),
                    original_purchase_date = table.Column<DateOnly>(type: "date", nullable: false),
                    handed_over_on = table.Column<DateOnly>(type: "date", nullable: false),
                    cover_end_date = table.Column<DateOnly>(type: "date", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_replacement_serials", x => x.id);
                    table.ForeignKey(
                        name: "fk_replacement_serials_claims_tenant_id_claim_id",
                        columns: x => new { x.tenant_id, x.claim_id },
                        principalSchema: "claims",
                        principalTable: "claims",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_replacement_serials_products_tenant_id_product_id",
                        columns: x => new { x.tenant_id, x.product_id },
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "replacements",
                schema: "claims",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_rule = table.Column<string>(type: "text", nullable: false),
                    return_rule = table.Column<string>(type: "text", nullable: false),
                    minimum_cover_months = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    return_state = table.Column<string>(type: "text", nullable: false),
                    active_location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    current_unit_id = table.Column<Guid>(type: "uuid", nullable: true),
                    pending_proposal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_product_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_replacements", x => x.id);
                    table.UniqueConstraint("ak_replacements_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_replacements_claims_tenant_id_claim_id",
                        columns: x => new { x.tenant_id, x.claim_id },
                        principalSchema: "claims",
                        principalTable: "claims",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_replacements_policy_versions_tenant_id_policy_version_id",
                        columns: x => new { x.tenant_id, x.policy_version_id },
                        principalSchema: "policy",
                        principalTable: "policy_versions",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_replacements_products_tenant_id_approved_product_id",
                        columns: x => new { x.tenant_id, x.approved_product_id },
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "faulty_product_returns",
                schema: "claims",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    replacement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    returned_on = table.Column<DateOnly>(type: "date", nullable: false),
                    service_center_id = table.Column<Guid>(type: "uuid", nullable: true),
                    serial_seen = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    serial_matches = table.Column<bool>(type: "boolean", nullable: false),
                    repair_handover_id = table.Column<Guid>(type: "uuid", nullable: true),
                    note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    recorded_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_faulty_product_returns", x => x.id);
                    table.ForeignKey(
                        name: "fk_faulty_product_returns_repair_handovers_tenant_id_repair_ha~",
                        columns: x => new { x.tenant_id, x.repair_handover_id },
                        principalSchema: "claims",
                        principalTable: "repair_handovers",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_faulty_product_returns_replacements_tenant_id_replacement_id",
                        columns: x => new { x.tenant_id, x.replacement_id },
                        principalSchema: "claims",
                        principalTable: "replacements",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_faulty_product_returns_service_centers_tenant_id_service_ce~",
                        columns: x => new { x.tenant_id, x.service_center_id },
                        principalSchema: "integration",
                        principalTable: "service_centers",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "replacement_events",
                schema: "audit",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    replacement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    from_status = table.Column<string>(type: "text", nullable: true),
                    to_status = table.Column<string>(type: "text", nullable: true),
                    reported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    actor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    corrects_event_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_replacement_events", x => x.id);
                    table.UniqueConstraint("ak_replacement_events_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_replacement_events_claims_tenant_id_claim_id",
                        columns: x => new { x.tenant_id, x.claim_id },
                        principalSchema: "claims",
                        principalTable: "claims",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_replacement_events_replacement_events_tenant_id_corrects_ev~",
                        columns: x => new { x.tenant_id, x.corrects_event_id },
                        principalSchema: "audit",
                        principalTable: "replacement_events",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_replacement_events_replacements_tenant_id_replacement_id",
                        columns: x => new { x.tenant_id, x.replacement_id },
                        principalSchema: "claims",
                        principalTable: "replacements",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "replacement_locations",
                schema: "claims",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    replacement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_center_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assignment = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    recorded_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_replacement_locations", x => x.id);
                    table.ForeignKey(
                        name: "fk_replacement_locations_replacements_tenant_id_replacement_id",
                        columns: x => new { x.tenant_id, x.replacement_id },
                        principalSchema: "claims",
                        principalTable: "replacements",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_replacement_locations_service_centers_tenant_id_service_cen~",
                        columns: x => new { x.tenant_id, x.service_center_id },
                        principalSchema: "integration",
                        principalTable: "service_centers",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "replacement_model_proposals",
                schema: "claims",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    replacement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    proposed_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    proposed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decision_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_replacement_model_proposals", x => x.id);
                    table.ForeignKey(
                        name: "fk_replacement_model_proposals_products_tenant_id_product_id",
                        columns: x => new { x.tenant_id, x.product_id },
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_replacement_model_proposals_replacements_tenant_id_replacem~",
                        columns: x => new { x.tenant_id, x.replacement_id },
                        principalSchema: "claims",
                        principalTable: "replacements",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "replacement_units",
                schema: "claims",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    replacement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false),
                    service_center_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    serial_number = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    result = table.Column<string>(type: "text", nullable: false),
                    result_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    reserved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_replacement_units", x => x.id);
                    table.ForeignKey(
                        name: "fk_replacement_units_products_tenant_id_product_id",
                        columns: x => new { x.tenant_id, x.product_id },
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_replacement_units_replacements_tenant_id_replacement_id",
                        columns: x => new { x.tenant_id, x.replacement_id },
                        principalSchema: "claims",
                        principalTable: "replacements",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_replacement_units_service_centers_tenant_id_service_center_~",
                        columns: x => new { x.tenant_id, x.service_center_id },
                        principalSchema: "integration",
                        principalTable: "service_centers",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_faulty_product_returns_tenant_id_repair_handover_id",
                schema: "claims",
                table: "faulty_product_returns",
                columns: new[] { "tenant_id", "repair_handover_id" });

            migrationBuilder.CreateIndex(
                name: "ix_faulty_product_returns_tenant_id_replacement_id",
                schema: "claims",
                table: "faulty_product_returns",
                columns: new[] { "tenant_id", "replacement_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_faulty_product_returns_tenant_id_service_center_id",
                schema: "claims",
                table: "faulty_product_returns",
                columns: new[] { "tenant_id", "service_center_id" });

            migrationBuilder.CreateIndex(
                name: "ix_replacement_events_tenant_id_claim_id",
                schema: "audit",
                table: "replacement_events",
                columns: new[] { "tenant_id", "claim_id" });

            migrationBuilder.CreateIndex(
                name: "ix_replacement_events_tenant_id_corrects_event_id",
                schema: "audit",
                table: "replacement_events",
                columns: new[] { "tenant_id", "corrects_event_id" });

            migrationBuilder.CreateIndex(
                name: "ix_replacement_events_tenant_id_replacement_id_seq",
                schema: "audit",
                table: "replacement_events",
                columns: new[] { "tenant_id", "replacement_id", "seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_replacement_locations_tenant_id_service_center_id",
                schema: "claims",
                table: "replacement_locations",
                columns: new[] { "tenant_id", "service_center_id" });

            migrationBuilder.CreateIndex(
                name: "ux_replacement_locations_one_active",
                schema: "claims",
                table: "replacement_locations",
                columns: new[] { "tenant_id", "replacement_id" },
                unique: true,
                filter: "is_active");

            migrationBuilder.CreateIndex(
                name: "ix_replacement_model_proposals_tenant_id_product_id",
                schema: "claims",
                table: "replacement_model_proposals",
                columns: new[] { "tenant_id", "product_id" });

            migrationBuilder.CreateIndex(
                name: "ux_replacement_model_proposals_one_pending",
                schema: "claims",
                table: "replacement_model_proposals",
                columns: new[] { "tenant_id", "replacement_id" },
                unique: true,
                filter: "state = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ix_replacement_serials_tenant_id_claim_id",
                schema: "catalog",
                table: "replacement_serials",
                columns: new[] { "tenant_id", "claim_id" });

            migrationBuilder.CreateIndex(
                name: "ix_replacement_serials_tenant_id_original_serial",
                schema: "catalog",
                table: "replacement_serials",
                columns: new[] { "tenant_id", "original_serial" });

            migrationBuilder.CreateIndex(
                name: "ix_replacement_serials_tenant_id_product_id",
                schema: "catalog",
                table: "replacement_serials",
                columns: new[] { "tenant_id", "product_id" });

            migrationBuilder.CreateIndex(
                name: "ix_replacement_serials_tenant_id_serial_number",
                schema: "catalog",
                table: "replacement_serials",
                columns: new[] { "tenant_id", "serial_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_replacement_units_tenant_id_product_id",
                schema: "claims",
                table: "replacement_units",
                columns: new[] { "tenant_id", "product_id" });

            migrationBuilder.CreateIndex(
                name: "ix_replacement_units_tenant_id_replacement_id_seq",
                schema: "claims",
                table: "replacement_units",
                columns: new[] { "tenant_id", "replacement_id", "seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_replacement_units_tenant_id_service_center_id",
                schema: "claims",
                table: "replacement_units",
                columns: new[] { "tenant_id", "service_center_id" });

            migrationBuilder.CreateIndex(
                name: "ix_replacements_tenant_id_approved_product_id",
                schema: "claims",
                table: "replacements",
                columns: new[] { "tenant_id", "approved_product_id" });

            migrationBuilder.CreateIndex(
                name: "ix_replacements_tenant_id_claim_id",
                schema: "claims",
                table: "replacements",
                columns: new[] { "tenant_id", "claim_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_replacements_tenant_id_policy_version_id",
                schema: "claims",
                table: "replacements",
                columns: new[] { "tenant_id", "policy_version_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "faulty_product_returns",
                schema: "claims");

            migrationBuilder.DropTable(
                name: "replacement_events",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "replacement_locations",
                schema: "claims");

            migrationBuilder.DropTable(
                name: "replacement_model_proposals",
                schema: "claims");

            migrationBuilder.DropTable(
                name: "replacement_serials",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "replacement_units",
                schema: "claims");

            migrationBuilder.DropTable(
                name: "replacements",
                schema: "claims");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_repair_handovers_tenant_id_id",
                schema: "claims",
                table: "repair_handovers");

            migrationBuilder.DropColumn(
                name: "issues_replacements",
                schema: "integration",
                table: "service_centers");
        }
    }
}
