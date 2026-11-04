using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Solvelt.Marketplace.Api.Data;

/// <summary>Root EF context. Entity configuration lives in <c>IEntityTypeConfiguration</c> classes next to each entity.</summary>
public sealed class MarketplaceDb(DbContextOptions<MarketplaceDb> options) : DbContext(options)
{
    public DbSet<PortalInboundEvent> PortalInboundEvents => Set<PortalInboundEvent>();
    public DbSet<ProjectionState> ProjectionStates => Set<ProjectionState>();
    public DbSet<CatalogSeller> CatalogSellers => Set<CatalogSeller>();
    public DbSet<CatalogBrand> CatalogBrands => Set<CatalogBrand>();
    public DbSet<CatalogCategory> CatalogCategories => Set<CatalogCategory>();
    public DbSet<CatalogListing> CatalogListings => Set<CatalogListing>();
    public DbSet<CatalogVariant> CatalogVariants => Set<CatalogVariant>();
    public DbSet<CatalogImage> CatalogImages => Set<CatalogImage>();
    public DbSet<CatalogAttribute> CatalogAttributes => Set<CatalogAttribute>();
    public DbSet<CatalogFaq> CatalogFaqs => Set<CatalogFaq>();
    public DbSet<CatalogSyncState> CatalogSyncStates => Set<CatalogSyncState>();
    public DbSet<CatalogSearchRow> CatalogSearch => Set<CatalogSearchRow>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<BuyerAccount> BuyerAccounts => Set<BuyerAccount>();
    public DbSet<BuyerSession> BuyerSessions => Set<BuyerSession>();
    public DbSet<EmailToken> EmailTokens => Set<EmailToken>();
    public DbSet<SignInAttempt> SignInAttempts => Set<SignInAttempt>();
    public DbSet<SignInIpBudget> SignInIpBudgets => Set<SignInIpBudget>();
    public DbSet<Cart> Carts => Set<Cart>();
    public DbSet<CartLine> CartLines => Set<CartLine>();
    public DbSet<MarketplaceOrder> Orders => Set<MarketplaceOrder>();
    public DbSet<OrderGroup> OrderGroups => Set<OrderGroup>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public DbSet<OrderRefund> OrderRefunds => Set<OrderRefund>();
    public DbSet<StripeEvent> StripeEvents => Set<StripeEvent>();
    public DbSet<DisputeState> DisputeStates => Set<DisputeState>();
    public DbSet<SellerOrderState> SellerOrderStates => Set<SellerOrderState>();
    public DbSet<OrderShipment> OrderShipments => Set<OrderShipment>();
    public DbSet<OrderShipmentLine> OrderShipmentLines => Set<OrderShipmentLine>();
    public DbSet<OrderLineFulfilment> OrderLineFulfilments => Set<OrderLineFulfilment>();
    public DbSet<OrderCancelRequest> OrderCancelRequests => Set<OrderCancelRequest>();
    public DbSet<OrderReturn> OrderReturns => Set<OrderReturn>();
    public DbSet<OrderReturnLine> OrderReturnLines => Set<OrderReturnLine>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MarketplaceDb).Assembly);

        // Listing titles sort by an ASCII key with a binary collation, so the order is the same on SQLite and PostgreSQL (whose default collation is locale based).
        modelBuilder.Entity<CatalogListing>().Property(l => l.TitleSort).UseCollation(Database.IsSqlite() ? "BINARY" : "C");

        // SQLite cannot order or compare DateTimeOffset natively. All stored times are UTC (offset 0), so the binary
        // representation orders correctly. PostgreSQL keeps native timestamptz (Npgsql requires UTC offsets).
        if (Database.IsSqlite())
            foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(e => e.GetProperties())
                         .Where(p => p.ClrType == typeof(DateTimeOffset) || p.ClrType == typeof(DateTimeOffset?)))
                property.SetValueConverter(new DateTimeOffsetToBinaryConverter());
    }
}
