using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Solvelt.Marketplace.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "booking_payments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    AmountCents = table.Column<long>(type: "bigint", nullable: false),
                    IntentId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CapturedCents = table.Column<long>(type: "bigint", nullable: false),
                    RefundedCents = table.Column<long>(type: "bigint", nullable: false),
                    ExternalRefundedCents = table.Column<long>(type: "bigint", nullable: false),
                    DisputedCents = table.Column<long>(type: "bigint", nullable: false),
                    FailureCode = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CapturedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReportedToPortal = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_booking_payments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "booking_refunds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    PaymentId = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: false),
                    AmountCents = table.Column<long>(type: "bigint", nullable: false),
                    Reason = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StripeRefundId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ReportToPortal = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_booking_refunds", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "bookings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IsDemo = table.Column<bool>(type: "boolean", nullable: false),
                    DemoBatchId = table.Column<Guid>(type: "uuid", nullable: true),
                    MarketplaceBookingId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PortalBookingId = table.Column<Guid>(type: "uuid", nullable: true),
                    Number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ListingId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    FailureCode = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    Location = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    StartAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EndAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DurationMinutes = table.Column<int>(type: "integer", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    PriceCents = table.Column<long>(type: "bigint", nullable: false),
                    AddOnsCents = table.Column<long>(type: "bigint", nullable: false),
                    TravelFeeCents = table.Column<long>(type: "bigint", nullable: false),
                    TotalCents = table.Column<long>(type: "bigint", nullable: false),
                    AddOnIds = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    HoldExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ApprovalDeadline = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ProposedStartAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancellationFeeCents = table.Column<long>(type: "bigint", nullable: false),
                    RefundDueCents = table.Column<long>(type: "bigint", nullable: false),
                    NoShowFeeCents = table.Column<long>(type: "bigint", nullable: false),
                    AdditionalChargeCents = table.Column<long>(type: "bigint", nullable: false),
                    RescheduleCount = table.Column<int>(type: "integer", nullable: false),
                    PaymentDueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    QuoteId = table.Column<Guid>(type: "uuid", nullable: true),
                    PolicyCancelFreeHours = table.Column<int>(type: "integer", nullable: true),
                    PolicyCancelFeePercent = table.Column<int>(type: "integer", nullable: true),
                    PolicyNoShowFeePercent = table.Column<int>(type: "integer", nullable: true),
                    PolicyMaxReschedules = table.Column<int>(type: "integer", nullable: true),
                    RequiresBuyerConsent = table.Column<bool>(type: "boolean", nullable: false),
                    RescheduleRequestedStartAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExtraChargeState = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    OnlineInstructions = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    AddrName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AddrLine1 = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AddrLine2 = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AddrCity = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    AddrRegion = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    AddrPostalCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    AddrCountry = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    AddrLat = table.Column<double>(type: "double precision", nullable: true),
                    AddrLng = table.Column<double>(type: "double precision", nullable: true),
                    Phone = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    IntakeJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RedactedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bookings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "buyer_accounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    DisplayName = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    PasswordHash = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    EmailVerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Phone = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    RelayEmail = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SessionEpoch = table.Column<long>(type: "bigint", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RedactedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_buyer_accounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "buyer_addresses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IsDemo = table.Column<bool>(type: "boolean", nullable: false),
                    DemoBatchId = table.Column<Guid>(type: "uuid", nullable: true),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    FullName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Line1 = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Line2 = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    City = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Region = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    PostalCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Country = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    Phone = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    Lat = table.Column<double>(type: "double precision", nullable: true),
                    Lng = table.Column<double>(type: "double precision", nullable: true),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_buyer_addresses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "buyer_passkeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    CredentialId = table.Column<byte[]>(type: "bytea", maxLength: 1024, nullable: false),
                    PublicKey = table.Column<byte[]>(type: "bytea", maxLength: 2048, nullable: false),
                    SignCount = table.Column<long>(type: "bigint", nullable: false),
                    AaGuid = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    BackedUp = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_buyer_passkeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "buyer_sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AuthenticatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Epoch = table.Column<long>(type: "bigint", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AbsoluteExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_buyer_sessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "carts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IsDemo = table.Column<bool>(type: "boolean", nullable: false),
                    DemoBatchId = table.Column<Guid>(type: "uuid", nullable: true),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    GuestTokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_carts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "catalog_brands",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Slug = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    LogoImageId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_catalog_brands", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "catalog_categories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Slug = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_catalog_categories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "catalog_listings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Slug = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    TitleSort = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false, collation: "C"),
                    Summary = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    BrandId = table.Column<Guid>(type: "uuid", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    PriceFromCents = table.Column<long>(type: "bigint", nullable: false),
                    PriceToCents = table.Column<long>(type: "bigint", nullable: false),
                    CompareAtFromCents = table.Column<long>(type: "bigint", nullable: true),
                    Available = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    KnownVersion = table.Column<long>(type: "bigint", nullable: false),
                    UnlistedVersion = table.Column<long>(type: "bigint", nullable: true),
                    Visible = table.Column<bool>(type: "boolean", nullable: false),
                    WasPublic = table.Column<bool>(type: "boolean", nullable: false),
                    IsStub = table.Column<bool>(type: "boolean", nullable: false),
                    IsDemoListing = table.Column<bool>(type: "boolean", nullable: false),
                    LengthMm = table.Column<int>(type: "integer", nullable: true),
                    WidthMm = table.Column<int>(type: "integer", nullable: true),
                    HeightMm = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FetchAttempts = table.Column<int>(type: "integer", nullable: false),
                    NextFetchAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Row = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_catalog_listings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "catalog_sellers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    StateAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastVisibleAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_catalog_sellers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "catalog_sync_state",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Cursor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LeaseOwner = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    LastRunAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastSuccessAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_catalog_sync_state", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "dispute_states",
                columns: table => new
                {
                    DisputeId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    LastCreated = table.Column<long>(type: "bigint", nullable: false),
                    LastEventId = table.Column<string>(type: "text", nullable: false),
                    Closed = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dispute_states", x => x.DisputeId);
                });

            migrationBuilder.CreateTable(
                name: "email_tokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Purpose = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_email_tokens", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "marketplace_orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IsDemo = table.Column<bool>(type: "boolean", nullable: false),
                    DemoBatchId = table.Column<Guid>(type: "uuid", nullable: true),
                    Number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    GuestEmail = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    ClaimedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CartId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ActiveKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    ItemsCents = table.Column<long>(type: "bigint", nullable: false),
                    ShippingCents = table.Column<long>(type: "bigint", nullable: false),
                    TaxCents = table.Column<long>(type: "bigint", nullable: false),
                    TotalCents = table.Column<long>(type: "bigint", nullable: false),
                    PaymentIntentId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    BuyerName = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    RelayEmail = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Phone = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    ShipName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    ShipLine1 = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ShipLine2 = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ShipCity = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ShipRegion = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    ShipPostalCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ShipCountry = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    ShipLat = table.Column<double>(type: "double precision", nullable: true),
                    ShipLng = table.Column<double>(type: "double precision", nullable: true),
                    GiftMessage = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PaidAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PlacedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    PaidCents = table.Column<long>(type: "bigint", nullable: false),
                    RefundedCents = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_marketplace_orders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "message_attachment_data",
                columns: table => new
                {
                    AttachmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Bytes = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_message_attachment_data", x => x.AttachmentId);
                });

            migrationBuilder.CreateTable(
                name: "message_threads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IsDemo = table.Column<bool>(type: "boolean", nullable: false),
                    DemoBatchId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConversationKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: true),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: true),
                    ListingId = table.Column<Guid>(type: "uuid", nullable: true),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: true),
                    SellerName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Subject = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastMessageAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RedactedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_message_threads", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "notification_preferences",
                columns: table => new
                {
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmailOrderUpdates = table.Column<bool>(type: "boolean", nullable: false),
                    EmailShipped = table.Column<bool>(type: "boolean", nullable: false),
                    EmailOutForDelivery = table.Column<bool>(type: "boolean", nullable: false),
                    EmailDelivered = table.Column<bool>(type: "boolean", nullable: false),
                    EmailMessages = table.Column<bool>(type: "boolean", nullable: false),
                    EmailBookings = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_preferences", x => x.AccountId);
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IsDemo = table.Column<bool>(type: "boolean", nullable: false),
                    DemoBatchId = table.Column<Guid>(type: "uuid", nullable: true),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    OrderNumber = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    SellerOrderNumber = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    ParamsJson = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    DedupeKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReadAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "order_cancel_requests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestId = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    OpenKey = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    DecisionNote = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_cancel_requests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "order_line_fulfilment",
                columns: table => new
                {
                    LineId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShippedQty = table.Column<int>(type: "integer", nullable: false),
                    CancelledQty = table.Column<int>(type: "integer", nullable: false),
                    ReturnedQty = table.Column<int>(type: "integer", nullable: false),
                    RefundedQty = table.Column<int>(type: "integer", nullable: false),
                    ReturnPendingQty = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_line_fulfilment", x => x.LineId);
                });

            migrationBuilder.CreateTable(
                name: "order_refunds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: true),
                    AmountCents = table.Column<long>(type: "bigint", nullable: false),
                    ShippingCents = table.Column<long>(type: "bigint", nullable: false),
                    TaxCents = table.Column<long>(type: "bigint", nullable: false),
                    ShippingTaxCents = table.Column<long>(type: "bigint", nullable: false),
                    PartsJson = table.Column<string>(type: "text", nullable: true),
                    Reason = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Attempt = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StripeRefundId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ReportToPortal = table.Column<bool>(type: "boolean", nullable: false),
                    PortalRefundId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_refunds", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "order_returns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReturnId = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ClientKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DecisionNote = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LabelUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    LabelUrlExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LabelCarrier = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    LabelTrackingNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    LabelTrackingUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_returns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "order_shipment_lines",
                columns: table => new
                {
                    ShipmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    MarketplaceLineId = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_shipment_lines", x => new { x.ShipmentId, x.MarketplaceLineId });
                });

            migrationBuilder.CreateTable(
                name: "order_shipments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    PortalShipmentId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Method = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Carrier = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Service = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    TrackingNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    TrackingUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ShippedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeliveredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    VoidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TrackingStatus = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    TrackingStatusDetail = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Eta = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EtaWindowStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EtaWindowEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DriverFirstName = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    TrackingEventsJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    PositionLat = table.Column<double>(type: "double precision", nullable: true),
                    PositionLng = table.Column<double>(type: "double precision", nullable: true),
                    PositionAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ProofJson = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ProofExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Placement = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_shipments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DedupeKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Payload = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LockedUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeliveredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeadAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PayloadPurgedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox_messages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "portal_inbound_events",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PortalCreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OrderingAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AggregateType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    AggregateKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    AppliedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PayloadJson = table.Column<string>(type: "text", nullable: true),
                    PayloadPurgedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastReplayAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_portal_inbound_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "projection_states",
                columns: table => new
                {
                    AggregateType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    AggregateKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    FieldGroup = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    LastAppliedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastEventId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    TerminalState = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_projection_states", x => new { x.AggregateType, x.AggregateKey, x.FieldGroup });
                });

            migrationBuilder.CreateTable(
                name: "quotes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MarketplaceQuoteId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PortalQuoteId = table.Column<Guid>(type: "uuid", nullable: true),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ListingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    FailureCode = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    Location = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Details = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    PreferredTimesJson = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    AddrName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AddrLine1 = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AddrLine2 = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AddrCity = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    AddrRegion = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    AddrPostalCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    AddrCountry = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    AddrLat = table.Column<double>(type: "double precision", nullable: true),
                    AddrLng = table.Column<double>(type: "double precision", nullable: true),
                    Phone = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    PriceCents = table.Column<long>(type: "bigint", nullable: false),
                    DurationMinutes = table.Column<int>(type: "integer", nullable: false),
                    SlotsJson = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ProviderId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    DeclineReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RedactedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quotes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "seller_order_states",
                columns: table => new
                {
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ShippedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeliveredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeliveredBy = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    CancelledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancelledBy = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    CancelReason = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    DeliveryMethodChangedTo = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    DeliveryAttemptsFailed = table.Column<int>(type: "integer", nullable: false),
                    LastAttemptFailedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastAttemptFailedReason = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    BuyerConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_seller_order_states", x => x.GroupId);
                });

            migrationBuilder.CreateTable(
                name: "signin_attempts",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Failures = table.Column<int>(type: "integer", nullable: false),
                    NextAllowedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_signin_attempts", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "signin_ip_budgets",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Count = table.Column<int>(type: "integer", nullable: false),
                    WindowStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_signin_ip_budgets", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "stripe_events",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    StripeCreated = table.Column<long>(type: "bigint", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ObjectId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stripe_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "thread_messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PortalMessageId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ThreadId = table.Column<Guid>(type: "uuid", nullable: false),
                    Direction = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReadAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReportedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    HiddenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ModerationNote = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    RedactedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_thread_messages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "webauthn_challenges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Purpose = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Options = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webauthn_challenges", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "cart_lines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CartId = table.Column<Guid>(type: "uuid", nullable: false),
                    ListingId = table.Column<Guid>(type: "uuid", nullable: false),
                    VariantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    SeenUnitPriceCents = table.Column<long>(type: "bigint", nullable: false),
                    AddedAtMs = table.Column<long>(type: "bigint", nullable: false),
                    IsDemo = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cart_lines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_cart_lines_carts_CartId",
                        column: x => x.CartId,
                        principalTable: "carts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "catalog_attributes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ListingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_catalog_attributes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_catalog_attributes_catalog_listings_ListingId",
                        column: x => x.ListingId,
                        principalTable: "catalog_listings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "catalog_faqs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ListingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Question = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Answer = table.Column<string>(type: "text", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_catalog_faqs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_catalog_faqs_catalog_listings_ListingId",
                        column: x => x.ListingId,
                        principalTable: "catalog_listings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "catalog_images",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ListingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: false),
                    Height = table.Column<int>(type: "integer", nullable: false),
                    Alt = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_catalog_images", x => new { x.ListingId, x.Id });
                    table.ForeignKey(
                        name: "FK_catalog_images_catalog_listings_ListingId",
                        column: x => x.ListingId,
                        principalTable: "catalog_listings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "catalog_search",
                columns: table => new
                {
                    listing_id = table.Column<Guid>(type: "uuid", nullable: false),
                    brand_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    brand = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    terms = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    text = table.Column<string>(type: "character varying(2800)", maxLength: 2800, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_catalog_search", x => x.listing_id);
                    table.ForeignKey(
                        name: "FK_catalog_search_catalog_listings_listing_id",
                        column: x => x.listing_id,
                        principalTable: "catalog_listings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "catalog_variants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ListingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    PriceCents = table.Column<long>(type: "bigint", nullable: false),
                    CompareAtCents = table.Column<long>(type: "bigint", nullable: true),
                    Available = table.Column<bool>(type: "boolean", nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    WeightGrams = table.Column<int>(type: "integer", nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_catalog_variants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_catalog_variants_catalog_listings_ListingId",
                        column: x => x.ListingId,
                        principalTable: "catalog_listings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "marketplace_order_groups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    BrandId = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    BrandName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DeliveryKind = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    ShippingSpeed = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    LocalWindowId = table.Column<Guid>(type: "uuid", nullable: true),
                    LocalDate = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    ItemsCents = table.Column<long>(type: "bigint", nullable: false),
                    ShippingCents = table.Column<long>(type: "bigint", nullable: false),
                    ShippingTaxCents = table.Column<long>(type: "bigint", nullable: false),
                    TaxCents = table.Column<long>(type: "bigint", nullable: false),
                    TotalCents = table.Column<long>(type: "bigint", nullable: false),
                    SellerOrderId = table.Column<Guid>(type: "uuid", nullable: true),
                    SellerOrderNumber = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    Oversold = table.Column<bool>(type: "boolean", nullable: false),
                    RefundedCents = table.Column<long>(type: "bigint", nullable: false),
                    RefundedShippingCents = table.Column<long>(type: "bigint", nullable: false),
                    RefundedShippingTaxCents = table.Column<long>(type: "bigint", nullable: false),
                    RefundedTaxCents = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_marketplace_order_groups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_marketplace_order_groups_marketplace_orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "marketplace_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "marketplace_order_lines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    MarketplaceLineId = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    ListingId = table.Column<Guid>(type: "uuid", nullable: false),
                    VariantId = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    BrandId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    VariantName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    UnitPriceCents = table.Column<long>(type: "bigint", nullable: false),
                    LineTotalCents = table.Column<long>(type: "bigint", nullable: false),
                    TaxCents = table.Column<long>(type: "bigint", nullable: false),
                    RefundedQuantity = table.Column<int>(type: "integer", nullable: false),
                    RefundedTaxCents = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_marketplace_order_lines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_marketplace_order_lines_marketplace_orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "marketplace_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "order_return_lines",
                columns: table => new
                {
                    ReturnId = table.Column<Guid>(type: "uuid", nullable: false),
                    LineId = table.Column<Guid>(type: "uuid", nullable: false),
                    MarketplaceLineId = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    Reserved = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_return_lines", x => new { x.ReturnId, x.LineId });
                    table.ForeignKey(
                        name: "FK_order_return_lines_order_returns_ReturnId",
                        column: x => x.ReturnId,
                        principalTable: "order_returns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "message_attachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    PortalAttachmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    DeclaredType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    SniffedType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_message_attachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_message_attachments_thread_messages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "thread_messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_booking_payments_BookingId",
                table: "booking_payments",
                column: "BookingId");

            migrationBuilder.CreateIndex(
                name: "IX_booking_payments_IntentId",
                table: "booking_payments",
                column: "IntentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_booking_payments_Key",
                table: "booking_payments",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_booking_refunds_Key",
                table: "booking_refunds",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_booking_refunds_PaymentId",
                table: "booking_refunds",
                column: "PaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_bookings_AccountId_IdempotencyKey",
                table: "bookings",
                columns: new[] { "AccountId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_bookings_AccountId_Status",
                table: "bookings",
                columns: new[] { "AccountId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_bookings_MarketplaceBookingId",
                table: "bookings",
                column: "MarketplaceBookingId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_bookings_PortalBookingId",
                table: "bookings",
                column: "PortalBookingId");

            migrationBuilder.CreateIndex(
                name: "IX_bookings_Status_HoldExpiresAt",
                table: "bookings",
                columns: new[] { "Status", "HoldExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_buyer_accounts_Email",
                table: "buyer_accounts",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_buyer_accounts_RelayEmail",
                table: "buyer_accounts",
                column: "RelayEmail",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_buyer_addresses_AccountId_CreatedAt",
                table: "buyer_addresses",
                columns: new[] { "AccountId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "ux_buyer_addresses_one_default",
                table: "buyer_addresses",
                column: "AccountId",
                unique: true,
                filter: "\"IsDefault\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "IX_buyer_passkeys_AccountId",
                table: "buyer_passkeys",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_buyer_passkeys_CredentialId",
                table: "buyer_passkeys",
                column: "CredentialId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_buyer_sessions_AbsoluteExpiresAt",
                table: "buyer_sessions",
                column: "AbsoluteExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_buyer_sessions_AccountId",
                table: "buyer_sessions",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_buyer_sessions_TokenHash",
                table: "buyer_sessions",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cart_lines_CartId_VariantId",
                table: "cart_lines",
                columns: new[] { "CartId", "VariantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_carts_AccountId",
                table: "carts",
                column: "AccountId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_carts_GuestTokenHash",
                table: "carts",
                column: "GuestTokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_carts_UpdatedAt",
                table: "carts",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_catalog_attributes_Key_Value",
                table: "catalog_attributes",
                columns: new[] { "Key", "Value" });

            migrationBuilder.CreateIndex(
                name: "IX_catalog_attributes_ListingId",
                table: "catalog_attributes",
                column: "ListingId");

            migrationBuilder.CreateIndex(
                name: "IX_catalog_brands_SellerId",
                table: "catalog_brands",
                column: "SellerId");

            migrationBuilder.CreateIndex(
                name: "IX_catalog_brands_Slug",
                table: "catalog_brands",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_catalog_categories_ParentId",
                table: "catalog_categories",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_catalog_categories_Slug",
                table: "catalog_categories",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_catalog_faqs_ListingId",
                table: "catalog_faqs",
                column: "ListingId");

            migrationBuilder.CreateIndex(
                name: "IX_catalog_listings_KnownVersion",
                table: "catalog_listings",
                column: "KnownVersion");

            migrationBuilder.CreateIndex(
                name: "IX_catalog_listings_NextFetchAt",
                table: "catalog_listings",
                column: "NextFetchAt");

            migrationBuilder.CreateIndex(
                name: "IX_catalog_listings_Slug",
                table: "catalog_listings",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_catalog_listings_Visible_BrandId",
                table: "catalog_listings",
                columns: new[] { "Visible", "BrandId" });

            migrationBuilder.CreateIndex(
                name: "IX_catalog_listings_Visible_CategoryId",
                table: "catalog_listings",
                columns: new[] { "Visible", "CategoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_catalog_listings_Visible_SellerId",
                table: "catalog_listings",
                columns: new[] { "Visible", "SellerId" });

            migrationBuilder.CreateIndex(
                name: "IX_catalog_search_brand_id",
                table: "catalog_search",
                column: "brand_id");

            migrationBuilder.CreateIndex(
                name: "IX_catalog_sellers_LastVisibleAt",
                table: "catalog_sellers",
                column: "LastVisibleAt");

            migrationBuilder.CreateIndex(
                name: "IX_catalog_variants_ListingId",
                table: "catalog_variants",
                column: "ListingId");

            migrationBuilder.CreateIndex(
                name: "IX_email_tokens_AccountId_Purpose",
                table: "email_tokens",
                columns: new[] { "AccountId", "Purpose" });

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_order_groups_OrderId_SellerId_BrandId",
                table: "marketplace_order_groups",
                columns: new[] { "OrderId", "SellerId", "BrandId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_order_groups_SellerOrderId",
                table: "marketplace_order_groups",
                column: "SellerOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_order_lines_MarketplaceLineId",
                table: "marketplace_order_lines",
                column: "MarketplaceLineId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_order_lines_OrderId",
                table: "marketplace_order_lines",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_orders_AccountId_CreatedAt",
                table: "marketplace_orders",
                columns: new[] { "AccountId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_orders_ActiveKey",
                table: "marketplace_orders",
                column: "ActiveKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_orders_CartId",
                table: "marketplace_orders",
                column: "CartId",
                unique: true,
                filter: "\"Status\" = 'pending_payment'");

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_orders_GuestEmail",
                table: "marketplace_orders",
                column: "GuestEmail");

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_orders_Number",
                table: "marketplace_orders",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_orders_PaymentIntentId",
                table: "marketplace_orders",
                column: "PaymentIntentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_marketplace_orders_Status_CreatedAt",
                table: "marketplace_orders",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_message_attachments_MessageId",
                table: "message_attachments",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_message_attachments_PortalAttachmentId",
                table: "message_attachments",
                column: "PortalAttachmentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_message_attachments_Status",
                table: "message_attachments",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_message_threads_AccountId_BookingId",
                table: "message_threads",
                columns: new[] { "AccountId", "BookingId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_message_threads_AccountId_GroupId",
                table: "message_threads",
                columns: new[] { "AccountId", "GroupId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_message_threads_AccountId_LastMessageAt",
                table: "message_threads",
                columns: new[] { "AccountId", "LastMessageAt" });

            migrationBuilder.CreateIndex(
                name: "IX_message_threads_AccountId_ListingId",
                table: "message_threads",
                columns: new[] { "AccountId", "ListingId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_message_threads_ConversationKey",
                table: "message_threads",
                column: "ConversationKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_notifications_AccountId_CreatedAt",
                table: "notifications",
                columns: new[] { "AccountId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_notifications_DedupeKey",
                table: "notifications",
                column: "DedupeKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_order_cancel_requests_OpenKey",
                table: "order_cancel_requests",
                column: "OpenKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_order_cancel_requests_OrderId_GroupId",
                table: "order_cancel_requests",
                columns: new[] { "OrderId", "GroupId" });

            migrationBuilder.CreateIndex(
                name: "IX_order_cancel_requests_RequestId",
                table: "order_cancel_requests",
                column: "RequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_order_refunds_Key",
                table: "order_refunds",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_order_refunds_OrderId",
                table: "order_refunds",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_order_refunds_PortalRefundId",
                table: "order_refunds",
                column: "PortalRefundId");

            migrationBuilder.CreateIndex(
                name: "IX_order_returns_AccountId_ClientKey",
                table: "order_returns",
                columns: new[] { "AccountId", "ClientKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_order_returns_OrderId_GroupId",
                table: "order_returns",
                columns: new[] { "OrderId", "GroupId" });

            migrationBuilder.CreateIndex(
                name: "IX_order_returns_ReturnId",
                table: "order_returns",
                column: "ReturnId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_order_shipments_GroupId_PortalShipmentId",
                table: "order_shipments",
                columns: new[] { "GroupId", "PortalShipmentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_DedupeKey",
                table: "outbox_messages",
                column: "DedupeKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_Status_DeliveredAt",
                table: "outbox_messages",
                columns: new[] { "Status", "DeliveredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_Status_NextAttemptAt",
                table: "outbox_messages",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_portal_inbound_events_AccountId",
                table: "portal_inbound_events",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_portal_inbound_events_ReceivedAt",
                table: "portal_inbound_events",
                column: "ReceivedAt");

            migrationBuilder.CreateIndex(
                name: "IX_portal_inbound_events_Status_AggregateType_AggregateKey_Por~",
                table: "portal_inbound_events",
                columns: new[] { "Status", "AggregateType", "AggregateKey", "PortalCreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_portal_inbound_events_Status_LastReplayAt",
                table: "portal_inbound_events",
                columns: new[] { "Status", "LastReplayAt" });

            migrationBuilder.CreateIndex(
                name: "IX_quotes_AccountId_IdempotencyKey",
                table: "quotes",
                columns: new[] { "AccountId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_quotes_AccountId_Status",
                table: "quotes",
                columns: new[] { "AccountId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_quotes_MarketplaceQuoteId",
                table: "quotes",
                column: "MarketplaceQuoteId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_quotes_PortalQuoteId",
                table: "quotes",
                column: "PortalQuoteId");

            migrationBuilder.CreateIndex(
                name: "IX_seller_order_states_OrderId",
                table: "seller_order_states",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_signin_attempts_UpdatedAt",
                table: "signin_attempts",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_signin_ip_budgets_WindowStart",
                table: "signin_ip_budgets",
                column: "WindowStart");

            migrationBuilder.CreateIndex(
                name: "IX_stripe_events_ReceivedAt",
                table: "stripe_events",
                column: "ReceivedAt");

            migrationBuilder.CreateIndex(
                name: "IX_thread_messages_MessageKey",
                table: "thread_messages",
                column: "MessageKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_thread_messages_PortalMessageId",
                table: "thread_messages",
                column: "PortalMessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_thread_messages_ThreadId_SentAt",
                table: "thread_messages",
                columns: new[] { "ThreadId", "SentAt" });

            migrationBuilder.CreateIndex(
                name: "IX_webauthn_challenges_AccountId",
                table: "webauthn_challenges",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_webauthn_challenges_ExpiresAt",
                table: "webauthn_challenges",
                column: "ExpiresAt");
        
            CreateSearchSchema(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "booking_payments");

            migrationBuilder.DropTable(
                name: "booking_refunds");

            migrationBuilder.DropTable(
                name: "bookings");

            migrationBuilder.DropTable(
                name: "buyer_accounts");

            migrationBuilder.DropTable(
                name: "buyer_addresses");

            migrationBuilder.DropTable(
                name: "buyer_passkeys");

            migrationBuilder.DropTable(
                name: "buyer_sessions");

            migrationBuilder.DropTable(
                name: "cart_lines");

            migrationBuilder.DropTable(
                name: "catalog_attributes");

            migrationBuilder.DropTable(
                name: "catalog_brands");

            migrationBuilder.DropTable(
                name: "catalog_categories");

            migrationBuilder.DropTable(
                name: "catalog_faqs");

            migrationBuilder.DropTable(
                name: "catalog_images");

            migrationBuilder.DropTable(
                name: "catalog_search");

            migrationBuilder.DropTable(
                name: "catalog_sellers");

            migrationBuilder.DropTable(
                name: "catalog_sync_state");

            migrationBuilder.DropTable(
                name: "catalog_variants");

            migrationBuilder.DropTable(
                name: "dispute_states");

            migrationBuilder.DropTable(
                name: "email_tokens");

            migrationBuilder.DropTable(
                name: "marketplace_order_groups");

            migrationBuilder.DropTable(
                name: "marketplace_order_lines");

            migrationBuilder.DropTable(
                name: "message_attachment_data");

            migrationBuilder.DropTable(
                name: "message_attachments");

            migrationBuilder.DropTable(
                name: "message_threads");

            migrationBuilder.DropTable(
                name: "notification_preferences");

            migrationBuilder.DropTable(
                name: "notifications");

            migrationBuilder.DropTable(
                name: "order_cancel_requests");

            migrationBuilder.DropTable(
                name: "order_line_fulfilment");

            migrationBuilder.DropTable(
                name: "order_refunds");

            migrationBuilder.DropTable(
                name: "order_return_lines");

            migrationBuilder.DropTable(
                name: "order_shipment_lines");

            migrationBuilder.DropTable(
                name: "order_shipments");

            migrationBuilder.DropTable(
                name: "outbox_messages");

            migrationBuilder.DropTable(
                name: "portal_inbound_events");

            migrationBuilder.DropTable(
                name: "projection_states");

            migrationBuilder.DropTable(
                name: "quotes");

            migrationBuilder.DropTable(
                name: "seller_order_states");

            migrationBuilder.DropTable(
                name: "signin_attempts");

            migrationBuilder.DropTable(
                name: "signin_ip_budgets");

            migrationBuilder.DropTable(
                name: "stripe_events");

            migrationBuilder.DropTable(
                name: "webauthn_challenges");

            migrationBuilder.DropTable(
                name: "carts");

            migrationBuilder.DropTable(
                name: "catalog_listings");

            migrationBuilder.DropTable(
                name: "marketplace_orders");

            migrationBuilder.DropTable(
                name: "thread_messages");

            migrationBuilder.DropTable(
                name: "order_returns");
        
            DropSearchSchema(migrationBuilder);
        }
    }
}
