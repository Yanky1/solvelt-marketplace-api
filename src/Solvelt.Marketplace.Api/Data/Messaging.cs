using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Solvelt.Marketplace.Api.Data;

public static class ThreadKinds
{
    public const string Order = "order", Product = "product", Booking = "booking";
}

/// <summary>
/// One buyer ⇄ seller conversation (M5 messaging relay): about one seller order, about a listing (a pre-sale question) or, later, about a booking. <see cref="ConversationKey"/> is OUR
/// conversation id towards the portal (<c>conversationId</c>), created once. Both sides only ever see relay identities: the seller sees the buyer's display name, the buyer sees the seller's.
/// </summary>
public sealed class MessageThread : Solvelt.Marketplace.Api.Demo.IDemoMarked
{
    /// <summary>Created by "Fill with demo data" (M9): removed by "Remove demo data", never sent to the portal, a payment provider or a mailbox.</summary>
    public bool IsDemo { get; set; }
    /// <summary>Which press of the button created it.</summary>
    public Guid? DemoBatchId { get; set; }
    public Guid Id { get; set; }
    public string ConversationKey { get; set; } = "";
    public Guid AccountId { get; set; }
    public string Kind { get; set; } = ThreadKinds.Order;
    /// <summary>Order threads: the seller order this is about (one thread per seller order and buyer).</summary>
    public Guid? OrderId { get; set; }
    public Guid? GroupId { get; set; }
    /// <summary>Product threads: our catalog listing id (one thread per listing and buyer).</summary>
    public Guid? ListingId { get; set; }
    /// <summary>Booking threads: our booking (one thread per buyer and booking).</summary>
    public Guid? BookingId { get; set; }
    public string? SellerName { get; set; }
    /// <summary>"Order MP-…" / the listing title: what the thread is about, shown in the inbox.</summary>
    public string Subject { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastMessageAt { get; set; }
    /// <summary>The thread's texts and files were redacted (retention); nothing is shown any more.</summary>
    public DateTimeOffset? RedactedAt { get; set; }
}

public static class MessageDirections
{
    public const string Buyer = "buyer", Seller = "seller";
}

public static class MessageStatuses
{
    /// <summary>Buyer message stored, not yet delivered to the portal (the outbox sends it).</summary>
    public const string Pending = "pending";
    public const string Sent = "sent", Failed = "failed", Received = "received";
}

public sealed class ThreadMessage
{
    public Guid Id { get; set; }
    /// <summary>Our id towards the portal for a buyer message (<c>messageId</c>, ≤ 64), stored before the call; null for a seller message.</summary>
    public string? MessageKey { get; set; }
    /// <summary>The portal's id of a seller message (from <c>message.sent</c>), unique: a message is stored once however many events carry it.</summary>
    public string? PortalMessageId { get; set; }
    public Guid ThreadId { get; set; }
    public string Direction { get; set; } = MessageDirections.Buyer;
    public string Text { get; set; } = "";
    public string Status { get; set; } = MessageStatuses.Pending;
    public DateTimeOffset SentAt { get; set; }
    /// <summary>Seller messages: when the buyer read them (a <c>read</c> receipt goes to the portal).</summary>
    public DateTimeOffset? ReadAt { get; set; }
    public DateTimeOffset? ReportedAt { get; set; }
    /// <summary>Staff hid or removed it (<c>content.report_decided</c>): the text is no longer shown.</summary>
    public DateTimeOffset? HiddenAt { get; set; }
    /// <summary>What staff wrote for the reporter (<c>reasonShownToUser</c>), shown instead of the hidden text. The internal staff reason never reaches us.</summary>
    public string? ModerationNote { get; set; }
    public DateTimeOffset? RedactedAt { get; set; }
    public List<MessageAttachment> Attachments { get; set; } = [];
}

public static class AttachmentStatuses
{
    public const string Pending = "pending", Stored = "stored", Rejected = "rejected";
}

/// <summary>A file a seller attached. Listed from the portal event; the bytes are fetched, checked and stored by us (see the messaging attachments decision).</summary>
public sealed class MessageAttachment
{
    public Guid Id { get; set; }
    public Guid MessageId { get; set; }
    /// <summary>The portal's attachment id (what its signed GET takes).</summary>
    public Guid PortalAttachmentId { get; set; }
    public string FileName { get; set; } = "";
    /// <summary>The type the portal declared. What is SERVED is the type found by looking at the bytes (<see cref="SniffedType"/>).</summary>
    public string? DeclaredType { get; set; }
    public string? SniffedType { get; set; }
    public long Size { get; set; }
    public string Status { get; set; } = AttachmentStatuses.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>The checked, metadata-stripped bytes of a stored attachment. A separate table, so listing a thread never loads files. Deleted with the text (retention) or when staff hide the message.</summary>
public sealed class MessageAttachmentData
{
    public Guid AttachmentId { get; set; }
    public byte[] Bytes { get; set; } = [];
}

internal sealed class MessagingConfiguration : IEntityTypeConfiguration<MessageThread>, IEntityTypeConfiguration<ThreadMessage>, IEntityTypeConfiguration<MessageAttachment>, IEntityTypeConfiguration<MessageAttachmentData>
{
    public void Configure(EntityTypeBuilder<MessageThread> b)
    {
        b.ToTable("message_threads");
        b.HasKey(t => t.Id);
        b.Property(t => t.Id).ValueGeneratedNever();
        b.Property(t => t.ConversationKey).HasMaxLength(64).IsRequired();
        b.Property(t => t.Kind).HasMaxLength(10).IsRequired();
        b.Property(t => t.SellerName).HasMaxLength(200);
        b.Property(t => t.Subject).HasMaxLength(300).IsRequired();
        b.HasIndex(t => t.ConversationKey).IsUnique();
        b.HasIndex(t => new { t.AccountId, t.GroupId }).IsUnique();       // one thread per buyer and seller order (NULLs are allowed)
        b.HasIndex(t => new { t.AccountId, t.ListingId }).IsUnique();     // one thread per buyer and listing
        b.HasIndex(t => new { t.AccountId, t.BookingId }).IsUnique();     // one thread per buyer and booking
        b.HasIndex(t => new { t.AccountId, t.LastMessageAt });
    }

    public void Configure(EntityTypeBuilder<ThreadMessage> b)
    {
        b.ToTable("thread_messages");
        b.HasKey(m => m.Id);
        b.Property(m => m.Id).ValueGeneratedNever();
        b.Property(m => m.MessageKey).HasMaxLength(64);
        b.Property(m => m.PortalMessageId).HasMaxLength(100);
        b.Property(m => m.Direction).HasMaxLength(10).IsRequired();
        b.Property(m => m.Text).HasMaxLength(4000).IsRequired();
        b.Property(m => m.Status).HasMaxLength(10).IsRequired();
        b.Property(m => m.ModerationNote).HasMaxLength(300);
        b.HasIndex(m => m.MessageKey).IsUnique();
        b.HasIndex(m => m.PortalMessageId).IsUnique();
        b.HasIndex(m => new { m.ThreadId, m.SentAt });
        b.HasMany(m => m.Attachments).WithOne().HasForeignKey(a => a.MessageId).OnDelete(DeleteBehavior.Cascade);
    }

    public void Configure(EntityTypeBuilder<MessageAttachment> b)
    {
        b.ToTable("message_attachments");
        b.HasKey(a => a.Id);
        b.Property(a => a.Id).ValueGeneratedNever();
        b.Property(a => a.FileName).HasMaxLength(120).IsRequired();
        b.Property(a => a.DeclaredType).HasMaxLength(100);
        b.Property(a => a.SniffedType).HasMaxLength(100);
        b.Property(a => a.Status).HasMaxLength(10).IsRequired();
        b.HasIndex(a => a.PortalAttachmentId).IsUnique();
        b.HasIndex(a => a.Status);
    }

    public void Configure(EntityTypeBuilder<MessageAttachmentData> b)
    {
        b.ToTable("message_attachment_data");
        b.HasKey(d => d.AttachmentId);
        b.Property(d => d.AttachmentId).ValueGeneratedNever();
    }
}
