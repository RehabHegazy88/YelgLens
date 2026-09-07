using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace YelgLens.Intake.DAL.Migrations
{
    /// <inheritdoc />
    public partial class InitialPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BranchMappings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SourceLabel = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    MatchKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    OdooDatabase = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    OdooCustomer = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    AutoCaptured = table.Column<bool>(type: "boolean", nullable: false),
                    FirstSeenDocumentId = table.Column<long>(type: "bigint", nullable: true),
                    OdooInvoiceAddress = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    OdooDeliveryAddress = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    OdooPricelist = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Note = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    AddedDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LastModifiedDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Deleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeleteDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    AddedBy_UserId = table.Column<long>(type: "bigint", nullable: false),
                    ModifiedBy_UserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BranchMappings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Clients",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    Code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    ContactName = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    ContactEmail = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    ContactPhone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Note = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    AddedDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LastModifiedDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Deleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeleteDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    AddedBy_UserId = table.Column<long>(type: "bigint", nullable: false),
                    ModifiedBy_UserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Clients", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IdentityRoles",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IdentityUsers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FirstName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    LastName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    BranchCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CurrentClientId = table.Column<long>(type: "bigint", nullable: true),
                    AddedDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LastLoginDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Deleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeleteDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    UserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: true),
                    SecurityStamp = table.Column<string>(type: "text", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "text", nullable: true),
                    PhoneNumber = table.Column<string>(type: "text", nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OdooCustomers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    ParentName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SearchKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    StoreNumber = table.Column<int>(type: "integer", nullable: true),
                    Source = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    LastSeenDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    AddedDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LastModifiedDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Deleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeleteDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OdooCustomers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Permissions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Permissions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OdooConnections",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClientId = table.Column<long>(type: "bigint", nullable: true),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Url = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Database = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ServiceUser = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    ApiKeyProtected = table.Column<string>(type: "character varying(800)", maxLength: 800, nullable: true),
                    AllowWrites = table.Column<bool>(type: "boolean", nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "integer", nullable: false),
                    AttachSourceOnPublish = table.Column<bool>(type: "boolean", nullable: false),
                    DeleteLocalAfterAttach = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsProduction = table.Column<bool>(type: "boolean", nullable: false),
                    Note = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    LastTestedDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    LastTestResult = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    AddedDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LastModifiedDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Deleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeleteDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    AddedBy_UserId = table.Column<long>(type: "bigint", nullable: false),
                    ModifiedBy_UserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OdooConnections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OdooConnections_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IdentityRoleClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RoleId = table.Column<long>(type: "bigint", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityRoleClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IdentityRoleClaims_IdentityRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "IdentityRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClientUsers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClientId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    AddedDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LastModifiedDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Deleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeleteDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    AddedBy_UserId = table.Column<long>(type: "bigint", nullable: false),
                    ModifiedBy_UserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientUsers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientUsers_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClientUsers_IdentityUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "IdentityUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IdentityUserClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityUserClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IdentityUserClaims_IdentityUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "IdentityUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IdentityUserLogins",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    ProviderKey = table.Column<string>(type: "text", nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "text", nullable: true),
                    UserId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityUserLogins", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name: "FK_IdentityUserLogins_IdentityUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "IdentityUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IdentityUserRoles",
                columns: table => new
                {
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    RoleId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityUserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_IdentityUserRoles_IdentityRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "IdentityRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_IdentityUserRoles_IdentityUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "IdentityUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IdentityUserTokens",
                columns: table => new
                {
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityUserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name: "FK_IdentityUserTokens_IdentityUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "IdentityUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IntakeDocuments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SourceFileName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    StoredFileName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    SourceSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Strategy = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    OwnerDatabase = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CustomerPoNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CustomerPoNumber_Origin = table.Column<int>(type: "integer", nullable: false),
                    CustomerPoNumber_Conf = table.Column<double>(type: "double precision", nullable: false),
                    CustomerPoNumber_Raw = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    CustomerPoNumber_Has = table.Column<bool>(type: "boolean", nullable: false),
                    CustomerName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CustomerName_Origin = table.Column<int>(type: "integer", nullable: false),
                    CustomerName_Conf = table.Column<double>(type: "double precision", nullable: false),
                    CustomerName_Raw = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    CustomerName_Has = table.Column<bool>(type: "boolean", nullable: false),
                    BranchLabel = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    BranchLabel_Origin = table.Column<int>(type: "integer", nullable: false),
                    BranchLabel_Conf = table.Column<double>(type: "double precision", nullable: false),
                    BranchLabel_Raw = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    BranchLabel_Has = table.Column<bool>(type: "boolean", nullable: false),
                    OrderDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    OrderDate_Origin = table.Column<int>(type: "integer", nullable: false),
                    OrderDate_Conf = table.Column<double>(type: "double precision", nullable: false),
                    OrderDate_Raw = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    OrderDate_Has = table.Column<bool>(type: "boolean", nullable: false),
                    DeliveryDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    DeliveryDate_Origin = table.Column<int>(type: "integer", nullable: false),
                    DeliveryDate_Conf = table.Column<double>(type: "double precision", nullable: false),
                    DeliveryDate_Raw = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    DeliveryDate_Has = table.Column<bool>(type: "boolean", nullable: false),
                    UploadedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    UploadedDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ReviewedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    ReviewedDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ReviewNote = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ReopenedDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ReopenedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    ReopenNote = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ReopenCount = table.Column<int>(type: "integer", nullable: false),
                    ReplacesDocumentId = table.Column<long>(type: "bigint", nullable: true),
                    OdooCustomerOverride = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    OdooOrderId = table.Column<long>(type: "bigint", nullable: true),
                    OdooOrderName = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    OdooDatabase = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    PublishedDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    PublishedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    OdooAttachmentId = table.Column<long>(type: "bigint", nullable: true),
                    SourceSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    FileRemovedDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    AttachError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    PublishError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ArchivedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    AddedDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LastModifiedDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Deleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeleteDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntakeDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IntakeDocuments_IdentityUsers_ArchivedByUserId",
                        column: x => x.ArchivedByUserId,
                        principalTable: "IdentityUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IntakeDocuments_IdentityUsers_PublishedByUserId",
                        column: x => x.PublishedByUserId,
                        principalTable: "IdentityUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IntakeDocuments_IdentityUsers_ReopenedByUserId",
                        column: x => x.ReopenedByUserId,
                        principalTable: "IdentityUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IntakeDocuments_IdentityUsers_ReviewedByUserId",
                        column: x => x.ReviewedByUserId,
                        principalTable: "IdentityUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IntakeDocuments_IdentityUsers_UploadedByUserId",
                        column: x => x.UploadedByUserId,
                        principalTable: "IdentityUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IntakeDocuments_IntakeDocuments_ReplacesDocumentId",
                        column: x => x.ReplacesDocumentId,
                        principalTable: "IntakeDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RefreshTokens",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Token = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Expires = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    RemoteIpAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RevokedDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    AddedDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LastModifiedDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Deleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeleteDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefreshTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RefreshTokens_IdentityUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "IdentityUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RolePermissions",
                columns: table => new
                {
                    RoleId = table.Column<long>(type: "bigint", nullable: false),
                    PermissionId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RolePermissions", x => new { x.RoleId, x.PermissionId });
                    table.ForeignKey(
                        name: "FK_RolePermissions_IdentityRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "IdentityRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RolePermissions_Permissions_PermissionId",
                        column: x => x.PermissionId,
                        principalTable: "Permissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IntakeIssues",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    IntakeDocumentId = table.Column<long>(type: "bigint", nullable: false),
                    Code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Message = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: false),
                    Severity = table.Column<int>(type: "integer", nullable: false),
                    LineSequence = table.Column<int>(type: "integer", nullable: true),
                    Resolved = table.Column<bool>(type: "boolean", nullable: false),
                    AddedDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LastModifiedDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Deleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeleteDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntakeIssues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IntakeIssues_IntakeDocuments_IntakeDocumentId",
                        column: x => x.IntakeDocumentId,
                        principalTable: "IntakeDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IntakeLines",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    IntakeDocumentId = table.Column<long>(type: "bigint", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Barcode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Barcode_Origin = table.Column<int>(type: "integer", nullable: false),
                    Barcode_Conf = table.Column<double>(type: "double precision", nullable: false),
                    Barcode_Raw = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    Barcode_Has = table.Column<bool>(type: "boolean", nullable: false),
                    SupplierSku = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    SupplierSku_Origin = table.Column<int>(type: "integer", nullable: false),
                    SupplierSku_Conf = table.Column<double>(type: "double precision", nullable: false),
                    SupplierSku_Raw = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    SupplierSku_Has = table.Column<bool>(type: "boolean", nullable: false),
                    Description = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    Description_Origin = table.Column<int>(type: "integer", nullable: false),
                    Description_Conf = table.Column<double>(type: "double precision", nullable: false),
                    Description_Raw = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    Description_Has = table.Column<bool>(type: "boolean", nullable: false),
                    OrderedQty = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    OrderedQty_Origin = table.Column<int>(type: "integer", nullable: false),
                    OrderedQty_Conf = table.Column<double>(type: "double precision", nullable: false),
                    OrderedQty_Raw = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    OrderedQty_Has = table.Column<bool>(type: "boolean", nullable: false),
                    ReceivedQty = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    ReceivedQty_Origin = table.Column<int>(type: "integer", nullable: false),
                    ReceivedQty_Conf = table.Column<double>(type: "double precision", nullable: false),
                    ReceivedQty_Raw = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    ReceivedQty_Has = table.Column<bool>(type: "boolean", nullable: false),
                    DocumentUnitPrice = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    DocumentUnitPrice_Origin = table.Column<int>(type: "integer", nullable: false),
                    DocumentUnitPrice_Conf = table.Column<double>(type: "double precision", nullable: false),
                    DocumentUnitPrice_Raw = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    DocumentUnitPrice_Has = table.Column<bool>(type: "boolean", nullable: false),
                    VatPercent = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    VatPercent_Origin = table.Column<int>(type: "integer", nullable: false),
                    VatPercent_Conf = table.Column<double>(type: "double precision", nullable: false),
                    VatPercent_Raw = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    VatPercent_Has = table.Column<bool>(type: "boolean", nullable: false),
                    AddedDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LastModifiedDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Deleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeleteDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntakeLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IntakeLines_IntakeDocuments_IntakeDocumentId",
                        column: x => x.IntakeDocumentId,
                        principalTable: "IntakeDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IntakePages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    IntakeDocumentId = table.Column<long>(type: "bigint", nullable: false),
                    PageNumber = table.Column<int>(type: "integer", nullable: false),
                    SourceFileName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    StoredFileName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Strategy = table.Column<int>(type: "integer", nullable: false),
                    LineCount = table.Column<int>(type: "integer", nullable: false),
                    OdooAttachmentId = table.Column<long>(type: "bigint", nullable: true),
                    AddedDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LastModifiedDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Deleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeleteDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntakePages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IntakePages_IntakeDocuments_IntakeDocumentId",
                        column: x => x.IntakeDocumentId,
                        principalTable: "IntakeDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BranchMappings_OdooDatabase_MatchKey",
                table: "BranchMappings",
                columns: new[] { "OdooDatabase", "MatchKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Clients_Name",
                table: "Clients",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientUsers_ClientId_UserId",
                table: "ClientUsers",
                columns: new[] { "ClientId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientUsers_UserId",
                table: "ClientUsers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityRoleClaims_RoleId",
                table: "IdentityRoleClaims",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                table: "IdentityRoles",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IdentityUserClaims_UserId",
                table: "IdentityUserClaims",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityUserLogins_UserId",
                table: "IdentityUserLogins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityUserRoles_RoleId",
                table: "IdentityUserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "IdentityUsers",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityUsers_Email",
                table: "IdentityUsers",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "IdentityUsers",
                column: "NormalizedUserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IntakeDocuments_ArchivedByUserId",
                table: "IntakeDocuments",
                column: "ArchivedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_IntakeDocuments_OwnerDatabase",
                table: "IntakeDocuments",
                column: "OwnerDatabase");

            migrationBuilder.CreateIndex(
                name: "IX_IntakeDocuments_PublishedByUserId",
                table: "IntakeDocuments",
                column: "PublishedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_IntakeDocuments_ReopenedByUserId",
                table: "IntakeDocuments",
                column: "ReopenedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_IntakeDocuments_ReplacesDocumentId",
                table: "IntakeDocuments",
                column: "ReplacesDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_IntakeDocuments_ReviewedByUserId",
                table: "IntakeDocuments",
                column: "ReviewedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_IntakeDocuments_SourceSha256",
                table: "IntakeDocuments",
                column: "SourceSha256");

            migrationBuilder.CreateIndex(
                name: "IX_IntakeDocuments_Status",
                table: "IntakeDocuments",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_IntakeDocuments_UploadedByUserId",
                table: "IntakeDocuments",
                column: "UploadedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_IntakeIssues_IntakeDocumentId",
                table: "IntakeIssues",
                column: "IntakeDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_IntakeLines_IntakeDocumentId_Sequence",
                table: "IntakeLines",
                columns: new[] { "IntakeDocumentId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_IntakePages_IntakeDocumentId_PageNumber",
                table: "IntakePages",
                columns: new[] { "IntakeDocumentId", "PageNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_IntakePages_Sha256",
                table: "IntakePages",
                column: "Sha256");

            migrationBuilder.CreateIndex(
                name: "IX_OdooConnections_ClientId_Name",
                table: "OdooConnections",
                columns: new[] { "ClientId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OdooCustomers_DisplayName",
                table: "OdooCustomers",
                column: "DisplayName");

            migrationBuilder.CreateIndex(
                name: "IX_OdooCustomers_SearchKey",
                table: "OdooCustomers",
                column: "SearchKey");

            migrationBuilder.CreateIndex(
                name: "IX_OdooCustomers_Source_Name",
                table: "OdooCustomers",
                columns: new[] { "Source", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OdooCustomers_StoreNumber",
                table: "OdooCustomers",
                column: "StoreNumber");

            migrationBuilder.CreateIndex(
                name: "IX_Permissions_Code",
                table: "Permissions",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_Token",
                table: "RefreshTokens",
                column: "Token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_UserId",
                table: "RefreshTokens",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_RolePermissions_PermissionId",
                table: "RolePermissions",
                column: "PermissionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BranchMappings");

            migrationBuilder.DropTable(
                name: "ClientUsers");

            migrationBuilder.DropTable(
                name: "IdentityRoleClaims");

            migrationBuilder.DropTable(
                name: "IdentityUserClaims");

            migrationBuilder.DropTable(
                name: "IdentityUserLogins");

            migrationBuilder.DropTable(
                name: "IdentityUserRoles");

            migrationBuilder.DropTable(
                name: "IdentityUserTokens");

            migrationBuilder.DropTable(
                name: "IntakeIssues");

            migrationBuilder.DropTable(
                name: "IntakeLines");

            migrationBuilder.DropTable(
                name: "IntakePages");

            migrationBuilder.DropTable(
                name: "OdooConnections");

            migrationBuilder.DropTable(
                name: "OdooCustomers");

            migrationBuilder.DropTable(
                name: "RefreshTokens");

            migrationBuilder.DropTable(
                name: "RolePermissions");

            migrationBuilder.DropTable(
                name: "IntakeDocuments");

            migrationBuilder.DropTable(
                name: "Clients");

            migrationBuilder.DropTable(
                name: "IdentityRoles");

            migrationBuilder.DropTable(
                name: "Permissions");

            migrationBuilder.DropTable(
                name: "IdentityUsers");
        }
    }
}
