using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Warranty.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "adjudication");

            migrationBuilder.EnsureSchema(
                name: "claims");

            migrationBuilder.EnsureSchema(
                name: "crm");

            migrationBuilder.EnsureSchema(
                name: "audit");

            migrationBuilder.EnsureSchema(
                name: "aiops");

            migrationBuilder.EnsureSchema(
                name: "integration");

            migrationBuilder.EnsureSchema(
                name: "policy");

            migrationBuilder.EnsureSchema(
                name: "catalog");

            migrationBuilder.EnsureSchema(
                name: "review");

            migrationBuilder.EnsureSchema(
                name: "tenancy");

            migrationBuilder.CreateTable(
                name: "tenants",
                schema: "tenancy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    slug = table.Column<string>(type: "character varying(31)", maxLength: 31, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenants", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "customers",
                schema: "crm",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    full_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    phone = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    address_line = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    city = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    postal_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    country = table.Column<string>(type: "char(2)", nullable: false),
                    region = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customers", x => x.id);
                    table.UniqueConstraint("ak_customers_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_customers_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "tenancy",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "model_calls",
                schema: "aiops",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_id = table.Column<Guid>(type: "uuid", nullable: true),
                    run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    agent = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    route = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    prompt_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    prompt_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    input_tokens = table.Column<int>(type: "integer", nullable: false),
                    output_tokens = table.Column<int>(type: "integer", nullable: false),
                    cache_read_tokens = table.Column<int>(type: "integer", nullable: false),
                    cache_write_tokens = table.Column<int>(type: "integer", nullable: false),
                    latency_ms = table.Column<long>(type: "bigint", nullable: false),
                    estimated_cost = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: false),
                    stop_reason = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_model_calls", x => x.id);
                    table.ForeignKey(
                        name: "fk_model_calls_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "tenancy",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "products",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    model_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    category = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    claim_value = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_products", x => x.id);
                    table.UniqueConstraint("ak_products_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_products_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "tenancy",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "rag_queries",
                schema: "aiops",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    agent = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    namespaces = table.Column<string[]>(type: "text[]", nullable: false),
                    filters = table.Column<string>(type: "jsonb", nullable: false),
                    query_text = table.Column<string>(type: "text", nullable: false),
                    top_k = table.Column<int>(type: "integer", nullable: false),
                    results = table.Column<string>(type: "jsonb", nullable: false),
                    latency_ms = table.Column<long>(type: "bigint", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rag_queries", x => x.id);
                    table.ForeignKey(
                        name: "fk_rag_queries_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "tenancy",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "security_events",
                schema: "audit",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    actor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    target = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    source_ip = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    details = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_security_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_security_events_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "tenancy",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "service_centers",
                schema: "integration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    region = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    capabilities = table.Column<string[]>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_service_centers", x => x.id);
                    table.UniqueConstraint("ak_service_centers_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_service_centers_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "tenancy",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tenant_channels",
                schema: "tenancy",
                columns: table => new
                {
                    hostname = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_channels", x => x.hostname);
                    table.ForeignKey(
                        name: "fk_tenant_channels_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "tenancy",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tenant_settings",
                schema: "tenancy",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    auto_approval_limit = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    min_confidence = table.Column<int>(type: "integer", nullable: false),
                    auto_approve_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    auto_reject_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    always_review_categories = table.Column<string[]>(type: "text[]", nullable: false),
                    risk_high_threshold = table.Column<int>(type: "integer", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_settings", x => x.tenant_id);
                    table.ForeignKey(
                        name: "fk_tenant_settings_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "tenancy",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tool_calls",
                schema: "aiops",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    agent = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    tool = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    arguments = table.Column<string>(type: "jsonb", nullable: false),
                    allowed = table.Column<bool>(type: "boolean", nullable: false),
                    denial_reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    result_summary = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    latency_ms = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tool_calls", x => x.id);
                    table.ForeignKey(
                        name: "fk_tool_calls_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "tenancy",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "warranty_policies",
                schema: "policy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_warranty_policies", x => x.id);
                    table.UniqueConstraint("ak_warranty_policies_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_warranty_policies_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "tenancy",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "claims",
                schema: "claims",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reference = table.Column<string>(type: "char(10)", nullable: false),
                    channel = table.Column<string>(type: "text", nullable: false),
                    submitted_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contact_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    contact_phone = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    product_model_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: true),
                    serial_number = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    purchase_date = table.Column<DateOnly>(type: "date", nullable: false),
                    purchase_place = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    purchase_price = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    region = table.Column<string>(type: "text", nullable: true),
                    problem_description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    claim_date = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    final_outcome = table.Column<string>(type: "text", nullable: true),
                    final_decided_by = table.Column<string>(type: "text", nullable: true),
                    final_explanation = table.Column<string>(type: "text", nullable: true),
                    finalized_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    requested_items = table.Column<string>(type: "jsonb", nullable: false),
                    current_round = table.Column<int>(type: "integer", nullable: false),
                    auto_info_request_count = table.Column<int>(type: "integer", nullable: false),
                    reviewer_info_requested = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_claims", x => x.id);
                    table.UniqueConstraint("ak_claims_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_claims_customers_tenant_id_customer_id",
                        columns: x => new { x.tenant_id, x.customer_id },
                        principalSchema: "crm",
                        principalTable: "customers",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_claims_products_tenant_id_product_id",
                        columns: x => new { x.tenant_id, x.product_id },
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_claims_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "tenancy",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "product_serials",
                schema: "catalog",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    serial_number = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    manufactured_on = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product_serials", x => new { x.tenant_id, x.serial_number });
                    table.ForeignKey(
                        name: "fk_product_serials_products_tenant_id_product_id",
                        columns: x => new { x.tenant_id, x.product_id },
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "policy_versions",
                schema: "policy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    effective_to = table.Column<DateOnly>(type: "date", nullable: true),
                    regions = table.Column<string[]>(type: "text[]", nullable: false),
                    product_categories = table.Column<string[]>(type: "text[]", nullable: false),
                    terms = table.Column<string>(type: "jsonb", nullable: false),
                    source_blob_path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    checksum = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_policy_versions", x => x.id);
                    table.UniqueConstraint("ak_policy_versions_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_policy_versions_warranty_policies_tenant_id_policy_id",
                        columns: x => new { x.tenant_id, x.policy_id },
                        principalSchema: "policy",
                        principalTable: "warranty_policies",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "adjudication_runs",
                schema: "adjudication",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_id = table.Column<Guid>(type: "uuid", nullable: false),
                    round = table.Column<int>(type: "integer", nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    current_step = table.Column<string>(type: "text", nullable: false),
                    disposition = table.Column<string>(type: "text", nullable: true),
                    failure_reason = table.Column<string>(type: "text", nullable: true),
                    reference_map = table.Column<string>(type: "jsonb", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_adjudication_runs", x => x.id);
                    table.UniqueConstraint("ak_adjudication_runs_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_adjudication_runs_claims_tenant_id_claim_id",
                        columns: x => new { x.tenant_id, x.claim_id },
                        principalSchema: "claims",
                        principalTable: "claims",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "claim_evidence",
                schema: "claims",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_id = table.Column<Guid>(type: "uuid", nullable: false),
                    round = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    file_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    content_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "char(64)", nullable: false),
                    blob_path = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    uploaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_claim_evidence", x => x.id);
                    table.UniqueConstraint("ak_claim_evidence_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_claim_evidence_claims_tenant_id_claim_id",
                        columns: x => new { x.tenant_id, x.claim_id },
                        principalSchema: "claims",
                        principalTable: "claims",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "claim_jobs",
                schema: "claims",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_id = table.Column<Guid>(type: "uuid", nullable: false),
                    round = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    available_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_claim_jobs", x => x.id);
                    table.ForeignKey(
                        name: "fk_claim_jobs_claims_tenant_id_claim_id",
                        columns: x => new { x.tenant_id, x.claim_id },
                        principalSchema: "claims",
                        principalTable: "claims",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "decision_trail_entries",
                schema: "audit",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    step = table.Column<string>(type: "text", nullable: false),
                    actor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    prev_hash = table.Column<string>(type: "char(64)", nullable: false),
                    hash = table.Column<string>(type: "char(64)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_decision_trail_entries", x => x.id);
                    table.ForeignKey(
                        name: "fk_decision_trail_entries_claims_tenant_id_claim_id",
                        columns: x => new { x.tenant_id, x.claim_id },
                        principalSchema: "claims",
                        principalTable: "claims",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                schema: "integration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "text", nullable: false),
                    template = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notifications", x => x.id);
                    table.ForeignKey(
                        name: "fk_notifications_claims_tenant_id_claim_id",
                        columns: x => new { x.tenant_id, x.claim_id },
                        principalSchema: "claims",
                        principalTable: "claims",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "repair_requests",
                schema: "integration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_center_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_repair_requests", x => x.id);
                    table.ForeignKey(
                        name: "fk_repair_requests_claims_tenant_id_claim_id",
                        columns: x => new { x.tenant_id, x.claim_id },
                        principalSchema: "claims",
                        principalTable: "claims",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_repair_requests_service_centers_tenant_id_service_center_id",
                        columns: x => new { x.tenant_id, x.service_center_id },
                        principalSchema: "integration",
                        principalTable: "service_centers",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "policy_clauses",
                schema: "policy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    clause_key = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    clause_type = table.Column<string>(type: "text", nullable: false),
                    exclusion_code = table.Column<string>(type: "text", nullable: true),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    text = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_policy_clauses", x => x.id);
                    table.ForeignKey(
                        name: "fk_policy_clauses_policy_versions_tenant_id_policy_version_id",
                        columns: x => new { x.tenant_id, x.policy_version_id },
                        principalSchema: "policy",
                        principalTable: "policy_versions",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "guardrail_evaluations",
                schema: "adjudication",
                columns: table => new
                {
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    checks = table.Column<string>(type: "jsonb", nullable: false),
                    disposition = table.Column<string>(type: "text", nullable: false),
                    reasons = table.Column<string>(type: "jsonb", nullable: false),
                    approved_action = table.Column<string>(type: "jsonb", nullable: true),
                    evaluated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_guardrail_evaluations", x => x.run_id);
                    table.ForeignKey(
                        name: "fk_guardrail_evaluations_adjudication_runs_tenant_id_run_id",
                        columns: x => new { x.tenant_id, x.run_id },
                        principalSchema: "adjudication",
                        principalTable: "adjudication_runs",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "intake_results",
                schema: "adjudication",
                columns: table => new
                {
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    validation = table.Column<string>(type: "jsonb", nullable: false),
                    extraction = table.Column<string>(type: "jsonb", nullable: false),
                    missing_items = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_intake_results", x => x.run_id);
                    table.ForeignKey(
                        name: "fk_intake_results_adjudication_runs_tenant_id_run_id",
                        columns: x => new { x.tenant_id, x.run_id },
                        principalSchema: "adjudication",
                        principalTable: "adjudication_runs",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "policy_assessments",
                schema: "adjudication",
                columns: table => new
                {
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_outcome = table.Column<string>(type: "text", nullable: false),
                    assessment = table.Column<string>(type: "jsonb", nullable: false),
                    confidence = table.Column<int>(type: "integer", nullable: true),
                    model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    prompt_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_policy_assessments", x => x.run_id);
                    table.ForeignKey(
                        name: "fk_policy_assessments_adjudication_runs_tenant_id_run_id",
                        columns: x => new { x.tenant_id, x.run_id },
                        principalSchema: "adjudication",
                        principalTable: "adjudication_runs",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "recommendations",
                schema: "adjudication",
                columns: table => new
                {
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    raw_output = table.Column<string>(type: "jsonb", nullable: false),
                    is_valid = table.Column<bool>(type: "boolean", nullable: false),
                    validation_errors = table.Column<string>(type: "jsonb", nullable: false),
                    decision = table.Column<string>(type: "text", nullable: true),
                    coverage = table.Column<string>(type: "text", nullable: true),
                    confidence = table.Column<int>(type: "integer", nullable: true),
                    reasoning_summary = table.Column<string>(type: "text", nullable: false),
                    claimant_explanation = table.Column<string>(type: "text", nullable: false),
                    evidence_refs = table.Column<string>(type: "jsonb", nullable: false),
                    policy_refs = table.Column<string>(type: "jsonb", nullable: false),
                    missing_information = table.Column<string>(type: "jsonb", nullable: false),
                    manipulation_detected = table.Column<bool>(type: "boolean", nullable: false),
                    model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    prompt_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    prompt_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recommendations", x => x.run_id);
                    table.ForeignKey(
                        name: "fk_recommendations_adjudication_runs_tenant_id_run_id",
                        columns: x => new { x.tenant_id, x.run_id },
                        principalSchema: "adjudication",
                        principalTable: "adjudication_runs",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "retrieved_policy_refs",
                schema: "adjudication",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ref_id = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    knowledge_chunk_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    clause_key = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    clause_type = table.Column<string>(type: "text", nullable: false),
                    exclusion_code = table.Column<string>(type: "text", nullable: true),
                    document_title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    effective_to = table.Column<DateOnly>(type: "date", nullable: true),
                    score = table.Column<float>(type: "real", nullable: false),
                    cited = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_retrieved_policy_refs", x => x.id);
                    table.ForeignKey(
                        name: "fk_retrieved_policy_refs_adjudication_runs_tenant_id_run_id",
                        columns: x => new { x.tenant_id, x.run_id },
                        principalSchema: "adjudication",
                        principalTable: "adjudication_runs",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "review_decisions",
                schema: "review",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reviewer_sub = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    reviewer_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    decision = table.Column<string>(type: "text", nullable: false),
                    justification = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    claimant_explanation = table.Column<string>(type: "character varying(1500)", maxLength: 1500, nullable: true),
                    requested_items = table.Column<string>(type: "jsonb", nullable: false),
                    overrides_ai = table.Column<bool>(type: "boolean", nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_review_decisions", x => x.id);
                    table.ForeignKey(
                        name: "fk_review_decisions_adjudication_runs_tenant_id_run_id",
                        columns: x => new { x.tenant_id, x.run_id },
                        principalSchema: "adjudication",
                        principalTable: "adjudication_runs",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_review_decisions_claims_tenant_id_claim_id",
                        columns: x => new { x.tenant_id, x.claim_id },
                        principalSchema: "claims",
                        principalTable: "claims",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "risk_assessments",
                schema: "adjudication",
                columns: table => new
                {
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stage = table.Column<string>(type: "text", nullable: false),
                    score = table.Column<int>(type: "integer", nullable: false),
                    level = table.Column<string>(type: "text", nullable: false),
                    signals = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_risk_assessments", x => x.run_id);
                    table.ForeignKey(
                        name: "fk_risk_assessments_adjudication_runs_tenant_id_run_id",
                        columns: x => new { x.tenant_id, x.run_id },
                        principalSchema: "adjudication",
                        principalTable: "adjudication_runs",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "evidence_findings",
                schema: "adjudication",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    evidence_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    result = table.Column<string>(type: "jsonb", nullable: false),
                    consistency = table.Column<string>(type: "jsonb", nullable: false),
                    confidence = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_evidence_findings", x => x.id);
                    table.ForeignKey(
                        name: "fk_evidence_findings_adjudication_runs_tenant_id_run_id",
                        columns: x => new { x.tenant_id, x.run_id },
                        principalSchema: "adjudication",
                        principalTable: "adjudication_runs",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_evidence_findings_claim_evidence_tenant_id_evidence_id",
                        columns: x => new { x.tenant_id, x.evidence_id },
                        principalSchema: "claims",
                        principalTable: "claim_evidence",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_adjudication_runs_claim_id_round",
                schema: "adjudication",
                table: "adjudication_runs",
                columns: new[] { "claim_id", "round" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_adjudication_runs_tenant_id_claim_id",
                schema: "adjudication",
                table: "adjudication_runs",
                columns: new[] { "tenant_id", "claim_id" });

            migrationBuilder.CreateIndex(
                name: "ix_claim_evidence_tenant_id_claim_id",
                schema: "claims",
                table: "claim_evidence",
                columns: new[] { "tenant_id", "claim_id" });

            migrationBuilder.CreateIndex(
                name: "ix_claim_evidence_tenant_id_sha256",
                schema: "claims",
                table: "claim_evidence",
                columns: new[] { "tenant_id", "sha256" });

            migrationBuilder.CreateIndex(
                name: "ix_claim_jobs_claim_id_round",
                schema: "claims",
                table: "claim_jobs",
                columns: new[] { "claim_id", "round" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_claim_jobs_status_available_at",
                schema: "claims",
                table: "claim_jobs",
                columns: new[] { "status", "available_at" });

            migrationBuilder.CreateIndex(
                name: "ix_claim_jobs_tenant_id_claim_id",
                schema: "claims",
                table: "claim_jobs",
                columns: new[] { "tenant_id", "claim_id" });

            migrationBuilder.CreateIndex(
                name: "ix_claims_tenant_id_customer_id",
                schema: "claims",
                table: "claims",
                columns: new[] { "tenant_id", "customer_id" });

            migrationBuilder.CreateIndex(
                name: "ix_claims_tenant_id_product_id",
                schema: "claims",
                table: "claims",
                columns: new[] { "tenant_id", "product_id" });

            migrationBuilder.CreateIndex(
                name: "ix_claims_tenant_id_reference",
                schema: "claims",
                table: "claims",
                columns: new[] { "tenant_id", "reference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_claims_tenant_id_serial_number",
                schema: "claims",
                table: "claims",
                columns: new[] { "tenant_id", "serial_number" });

            migrationBuilder.CreateIndex(
                name: "ix_claims_tenant_id_status",
                schema: "claims",
                table: "claims",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_customers_tenant_id_email",
                schema: "crm",
                table: "customers",
                columns: new[] { "tenant_id", "email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_decision_trail_entries_claim_id_seq",
                schema: "audit",
                table: "decision_trail_entries",
                columns: new[] { "claim_id", "seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_decision_trail_entries_tenant_id_claim_id",
                schema: "audit",
                table: "decision_trail_entries",
                columns: new[] { "tenant_id", "claim_id" });

            migrationBuilder.CreateIndex(
                name: "ix_evidence_findings_tenant_id_evidence_id",
                schema: "adjudication",
                table: "evidence_findings",
                columns: new[] { "tenant_id", "evidence_id" });

            migrationBuilder.CreateIndex(
                name: "ix_evidence_findings_tenant_id_run_id",
                schema: "adjudication",
                table: "evidence_findings",
                columns: new[] { "tenant_id", "run_id" });

            migrationBuilder.CreateIndex(
                name: "ix_guardrail_evaluations_tenant_id_run_id",
                schema: "adjudication",
                table: "guardrail_evaluations",
                columns: new[] { "tenant_id", "run_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_intake_results_tenant_id_run_id",
                schema: "adjudication",
                table: "intake_results",
                columns: new[] { "tenant_id", "run_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_model_calls_tenant_id_run_id",
                schema: "aiops",
                table: "model_calls",
                columns: new[] { "tenant_id", "run_id" });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_tenant_id_claim_id",
                schema: "integration",
                table: "notifications",
                columns: new[] { "tenant_id", "claim_id" });

            migrationBuilder.CreateIndex(
                name: "ix_policy_assessments_tenant_id_run_id",
                schema: "adjudication",
                table: "policy_assessments",
                columns: new[] { "tenant_id", "run_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_policy_clauses_policy_version_id_clause_key",
                schema: "policy",
                table: "policy_clauses",
                columns: new[] { "policy_version_id", "clause_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_policy_clauses_tenant_id_policy_version_id",
                schema: "policy",
                table: "policy_clauses",
                columns: new[] { "tenant_id", "policy_version_id" });

            migrationBuilder.CreateIndex(
                name: "ix_policy_versions_policy_id_version",
                schema: "policy",
                table: "policy_versions",
                columns: new[] { "policy_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_policy_versions_tenant_id_policy_id",
                schema: "policy",
                table: "policy_versions",
                columns: new[] { "tenant_id", "policy_id" });

            migrationBuilder.CreateIndex(
                name: "ix_product_serials_tenant_id_product_id",
                schema: "catalog",
                table: "product_serials",
                columns: new[] { "tenant_id", "product_id" });

            migrationBuilder.CreateIndex(
                name: "ix_products_tenant_id_model_code",
                schema: "catalog",
                table: "products",
                columns: new[] { "tenant_id", "model_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_rag_queries_tenant_id_run_id",
                schema: "aiops",
                table: "rag_queries",
                columns: new[] { "tenant_id", "run_id" });

            migrationBuilder.CreateIndex(
                name: "ix_recommendations_tenant_id_run_id",
                schema: "adjudication",
                table: "recommendations",
                columns: new[] { "tenant_id", "run_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_repair_requests_tenant_id_claim_id",
                schema: "integration",
                table: "repair_requests",
                columns: new[] { "tenant_id", "claim_id" });

            migrationBuilder.CreateIndex(
                name: "ix_repair_requests_tenant_id_service_center_id",
                schema: "integration",
                table: "repair_requests",
                columns: new[] { "tenant_id", "service_center_id" });

            migrationBuilder.CreateIndex(
                name: "ix_retrieved_policy_refs_run_id_ref_id",
                schema: "adjudication",
                table: "retrieved_policy_refs",
                columns: new[] { "run_id", "ref_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_retrieved_policy_refs_tenant_id_run_id",
                schema: "adjudication",
                table: "retrieved_policy_refs",
                columns: new[] { "tenant_id", "run_id" });

            migrationBuilder.CreateIndex(
                name: "ix_review_decisions_tenant_id_claim_id",
                schema: "review",
                table: "review_decisions",
                columns: new[] { "tenant_id", "claim_id" });

            migrationBuilder.CreateIndex(
                name: "ix_review_decisions_tenant_id_run_id",
                schema: "review",
                table: "review_decisions",
                columns: new[] { "tenant_id", "run_id" });

            migrationBuilder.CreateIndex(
                name: "ix_risk_assessments_tenant_id_run_id",
                schema: "adjudication",
                table: "risk_assessments",
                columns: new[] { "tenant_id", "run_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_security_events_tenant_id_occurred_at",
                schema: "audit",
                table: "security_events",
                columns: new[] { "tenant_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_tenant_channels_tenant_id",
                schema: "tenancy",
                table: "tenant_channels",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_tenants_slug",
                schema: "tenancy",
                table: "tenants",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tool_calls_tenant_id_run_id",
                schema: "aiops",
                table: "tool_calls",
                columns: new[] { "tenant_id", "run_id" });

            migrationBuilder.CreateIndex(
                name: "ix_warranty_policies_tenant_id_code",
                schema: "policy",
                table: "warranty_policies",
                columns: new[] { "tenant_id", "code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "claim_jobs",
                schema: "claims");

            migrationBuilder.DropTable(
                name: "decision_trail_entries",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "evidence_findings",
                schema: "adjudication");

            migrationBuilder.DropTable(
                name: "guardrail_evaluations",
                schema: "adjudication");

            migrationBuilder.DropTable(
                name: "intake_results",
                schema: "adjudication");

            migrationBuilder.DropTable(
                name: "model_calls",
                schema: "aiops");

            migrationBuilder.DropTable(
                name: "notifications",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "policy_assessments",
                schema: "adjudication");

            migrationBuilder.DropTable(
                name: "policy_clauses",
                schema: "policy");

            migrationBuilder.DropTable(
                name: "product_serials",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "rag_queries",
                schema: "aiops");

            migrationBuilder.DropTable(
                name: "recommendations",
                schema: "adjudication");

            migrationBuilder.DropTable(
                name: "repair_requests",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "retrieved_policy_refs",
                schema: "adjudication");

            migrationBuilder.DropTable(
                name: "review_decisions",
                schema: "review");

            migrationBuilder.DropTable(
                name: "risk_assessments",
                schema: "adjudication");

            migrationBuilder.DropTable(
                name: "security_events",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "tenant_channels",
                schema: "tenancy");

            migrationBuilder.DropTable(
                name: "tenant_settings",
                schema: "tenancy");

            migrationBuilder.DropTable(
                name: "tool_calls",
                schema: "aiops");

            migrationBuilder.DropTable(
                name: "claim_evidence",
                schema: "claims");

            migrationBuilder.DropTable(
                name: "policy_versions",
                schema: "policy");

            migrationBuilder.DropTable(
                name: "service_centers",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "adjudication_runs",
                schema: "adjudication");

            migrationBuilder.DropTable(
                name: "warranty_policies",
                schema: "policy");

            migrationBuilder.DropTable(
                name: "claims",
                schema: "claims");

            migrationBuilder.DropTable(
                name: "customers",
                schema: "crm");

            migrationBuilder.DropTable(
                name: "products",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "tenants",
                schema: "tenancy");
        }
    }
}
