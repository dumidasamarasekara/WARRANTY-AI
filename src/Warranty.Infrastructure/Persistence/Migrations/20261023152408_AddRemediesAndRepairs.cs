using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Warranty.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRemediesAndRepairs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "address",
                schema: "integration",
                table: "service_centers",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "contact",
                schema: "integration",
                table: "service_centers",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_active",
                schema: "integration",
                table: "service_centers",
                type: "boolean",
                nullable: false,
                defaultValue: true); // hand-edited: existing centres stay active (data-model.md)

            migrationBuilder.AddColumn<string>(
                name: "opening_hours",
                schema: "integration",
                table: "service_centers",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "priority",
                schema: "integration",
                table: "service_centers",
                type: "integer",
                nullable: false,
                defaultValue: 100); // hand-edited: ServiceCenter.DefaultPriority

            migrationBuilder.AddColumn<string>(
                name: "remedy_rules",
                schema: "policy",
                table: "policy_versions",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "dedupe_key",
                schema: "integration",
                table: "notifications",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "has_protection_plan",
                schema: "claims",
                table: "claims",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "protection_plan_confirmed",
                schema: "claims",
                table: "claims",
                type: "boolean",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "claim_remedies",
                schema: "claims",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scenario = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    remedy = table.Column<string>(type: "text", nullable: true),
                    allowed_remedies = table.Column<string[]>(type: "text[]", nullable: false),
                    pending_reason = table.Column<string>(type: "text", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_claim_remedies", x => x.id);
                    table.ForeignKey(
                        name: "fk_claim_remedies_claims_tenant_id_claim_id",
                        columns: x => new { x.tenant_id, x.claim_id },
                        principalSchema: "claims",
                        principalTable: "claims",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_claim_remedies_policy_versions_tenant_id_policy_version_id",
                        columns: x => new { x.tenant_id, x.policy_version_id },
                        principalSchema: "policy",
                        principalTable: "policy_versions",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "remedy_completions",
                schema: "claims",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_id = table.Column<Guid>(type: "uuid", nullable: false),
                    remedy = table.Column<string>(type: "text", nullable: false),
                    completed_on = table.Column<DateOnly>(type: "date", nullable: false),
                    reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    refund_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recorded_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_remedy_completions", x => x.id);
                    table.ForeignKey(
                        name: "fk_remedy_completions_claims_tenant_id_claim_id",
                        columns: x => new { x.tenant_id, x.claim_id },
                        principalSchema: "claims",
                        principalTable: "claims",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "remedy_decisions",
                schema: "claims",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    scenario = table.Column<string>(type: "text", nullable: false),
                    policy_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    table_row = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    allowed_remedies = table.Column<string[]>(type: "text[]", nullable: false),
                    remedy = table.Column<string>(type: "text", nullable: true),
                    decided_by = table.Column<string>(type: "text", nullable: true),
                    actor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    failed_attempts = table.Column<int>(type: "integer", nullable: false),
                    estimate_amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_remedy_decisions", x => x.id);
                    table.ForeignKey(
                        name: "fk_remedy_decisions_claims_tenant_id_claim_id",
                        columns: x => new { x.tenant_id, x.claim_id },
                        principalSchema: "claims",
                        principalTable: "claims",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_remedy_decisions_policy_versions_tenant_id_policy_version_id",
                        columns: x => new { x.tenant_id, x.policy_version_id },
                        principalSchema: "policy",
                        principalTable: "policy_versions",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "repairs",
                schema: "claims",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    not_possible_reason = table.Column<string>(type: "text", nullable: true),
                    active_handover_id = table.Column<Guid>(type: "uuid", nullable: true),
                    current_attempt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    failed_attempts = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_repairs", x => x.id);
                    table.UniqueConstraint("ak_repairs_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_repairs_claims_tenant_id_claim_id",
                        columns: x => new { x.tenant_id, x.claim_id },
                        principalSchema: "claims",
                        principalTable: "claims",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "repair_attempts",
                schema: "claims",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    repair_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false),
                    service_center_id = table.Column<Guid>(type: "uuid", nullable: false),
                    result = table.Column<string>(type: "text", nullable: false),
                    failure_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    estimate_amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_repair_attempts", x => x.id);
                    table.ForeignKey(
                        name: "fk_repair_attempts_repairs_tenant_id_repair_id",
                        columns: x => new { x.tenant_id, x.repair_id },
                        principalSchema: "claims",
                        principalTable: "repairs",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_repair_attempts_service_centers_tenant_id_service_center_id",
                        columns: x => new { x.tenant_id, x.service_center_id },
                        principalSchema: "integration",
                        principalTable: "service_centers",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "repair_events",
                schema: "audit",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    repair_id = table.Column<Guid>(type: "uuid", nullable: false),
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
                    table.PrimaryKey("pk_repair_events", x => x.id);
                    table.UniqueConstraint("ak_repair_events_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_repair_events_claims_tenant_id_claim_id",
                        columns: x => new { x.tenant_id, x.claim_id },
                        principalSchema: "claims",
                        principalTable: "claims",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_repair_events_repair_events_tenant_id_corrects_event_id",
                        columns: x => new { x.tenant_id, x.corrects_event_id },
                        principalSchema: "audit",
                        principalTable: "repair_events",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_repair_events_repairs_tenant_id_repair_id",
                        columns: x => new { x.tenant_id, x.repair_id },
                        principalSchema: "claims",
                        principalTable: "repairs",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "repair_handovers",
                schema: "claims",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    repair_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_center_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assignment = table.Column<string>(type: "text", nullable: false),
                    handed_over_on = table.Column<DateOnly>(type: "date", nullable: true),
                    centre_reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    recorded_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_repair_handovers", x => x.id);
                    table.ForeignKey(
                        name: "fk_repair_handovers_repairs_tenant_id_repair_id",
                        columns: x => new { x.tenant_id, x.repair_id },
                        principalSchema: "claims",
                        principalTable: "repairs",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_repair_handovers_service_centers_tenant_id_service_center_id",
                        columns: x => new { x.tenant_id, x.service_center_id },
                        principalSchema: "integration",
                        principalTable: "service_centers",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_notifications_tenant_dedupe_key",
                schema: "integration",
                table: "notifications",
                columns: new[] { "tenant_id", "dedupe_key" },
                unique: true,
                filter: "dedupe_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_claim_remedies_tenant_id_claim_id",
                schema: "claims",
                table: "claim_remedies",
                columns: new[] { "tenant_id", "claim_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_claim_remedies_tenant_id_policy_version_id",
                schema: "claims",
                table: "claim_remedies",
                columns: new[] { "tenant_id", "policy_version_id" });

            migrationBuilder.CreateIndex(
                name: "ix_remedy_completions_tenant_id_claim_id",
                schema: "claims",
                table: "remedy_completions",
                columns: new[] { "tenant_id", "claim_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_remedy_decisions_tenant_id_claim_id_seq",
                schema: "claims",
                table: "remedy_decisions",
                columns: new[] { "tenant_id", "claim_id", "seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_remedy_decisions_tenant_id_policy_version_id",
                schema: "claims",
                table: "remedy_decisions",
                columns: new[] { "tenant_id", "policy_version_id" });

            migrationBuilder.CreateIndex(
                name: "ix_repair_attempts_tenant_id_repair_id_seq",
                schema: "claims",
                table: "repair_attempts",
                columns: new[] { "tenant_id", "repair_id", "seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_repair_attempts_tenant_id_service_center_id",
                schema: "claims",
                table: "repair_attempts",
                columns: new[] { "tenant_id", "service_center_id" });

            migrationBuilder.CreateIndex(
                name: "ix_repair_events_tenant_id_claim_id",
                schema: "audit",
                table: "repair_events",
                columns: new[] { "tenant_id", "claim_id" });

            migrationBuilder.CreateIndex(
                name: "ix_repair_events_tenant_id_corrects_event_id",
                schema: "audit",
                table: "repair_events",
                columns: new[] { "tenant_id", "corrects_event_id" });

            migrationBuilder.CreateIndex(
                name: "ix_repair_events_tenant_id_repair_id_seq",
                schema: "audit",
                table: "repair_events",
                columns: new[] { "tenant_id", "repair_id", "seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_repair_handovers_tenant_id_service_center_id",
                schema: "claims",
                table: "repair_handovers",
                columns: new[] { "tenant_id", "service_center_id" });

            migrationBuilder.CreateIndex(
                name: "ux_repair_handovers_one_active",
                schema: "claims",
                table: "repair_handovers",
                columns: new[] { "tenant_id", "repair_id" },
                unique: true,
                filter: "is_active");

            migrationBuilder.CreateIndex(
                name: "ix_repairs_tenant_id_claim_id",
                schema: "claims",
                table: "repairs",
                columns: new[] { "tenant_id", "claim_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "claim_remedies",
                schema: "claims");

            migrationBuilder.DropTable(
                name: "remedy_completions",
                schema: "claims");

            migrationBuilder.DropTable(
                name: "remedy_decisions",
                schema: "claims");

            migrationBuilder.DropTable(
                name: "repair_attempts",
                schema: "claims");

            migrationBuilder.DropTable(
                name: "repair_events",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "repair_handovers",
                schema: "claims");

            migrationBuilder.DropTable(
                name: "repairs",
                schema: "claims");

            migrationBuilder.DropIndex(
                name: "ux_notifications_tenant_dedupe_key",
                schema: "integration",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "address",
                schema: "integration",
                table: "service_centers");

            migrationBuilder.DropColumn(
                name: "contact",
                schema: "integration",
                table: "service_centers");

            migrationBuilder.DropColumn(
                name: "is_active",
                schema: "integration",
                table: "service_centers");

            migrationBuilder.DropColumn(
                name: "opening_hours",
                schema: "integration",
                table: "service_centers");

            migrationBuilder.DropColumn(
                name: "priority",
                schema: "integration",
                table: "service_centers");

            migrationBuilder.DropColumn(
                name: "remedy_rules",
                schema: "policy",
                table: "policy_versions");

            migrationBuilder.DropColumn(
                name: "dedupe_key",
                schema: "integration",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "has_protection_plan",
                schema: "claims",
                table: "claims");

            migrationBuilder.DropColumn(
                name: "protection_plan_confirmed",
                schema: "claims",
                table: "claims");
        }
    }
}
