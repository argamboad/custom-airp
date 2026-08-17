using Microsoft.EntityFrameworkCore;

namespace Airp.Infrastructure.Storage.Local;

/// <summary>The local conversation store.</summary>
/// <remarks>
/// SQLite, one file, one user. The interesting part is not the schema but what
/// <see cref="SaveChangesAsync(CancellationToken)"/> refuses to do — see
/// <see cref="GuardAppendOnly"/>.
/// </remarks>
public sealed class AirpDbContext : DbContext
{
    /// <summary>Initialises the context.</summary>
    /// <param name="options">Configured options, carrying the connection string.</param>
    public AirpDbContext(DbContextOptions<AirpDbContext> options) : base(options) { }

    /// <summary>The conversations.</summary>
    public DbSet<ConversationRecord> Conversations => Set<ConversationRecord>();

    /// <summary>The messages.</summary>
    public DbSet<MessageRecord> Messages => Set<MessageRecord>();

    /// <summary>Summaries of turns too old to send whole. Derived; safe to delete.</summary>
    public DbSet<SummaryRecord> Summaries => Set<SummaryRecord>();

    /// <summary>What the conversation established, with the stretch it held for. Derived.</summary>
    public DbSet<FactRecord> Facts => Set<FactRecord>();

    /// <summary>Named meters the story keeps. Not derived: the reader defines these.</summary>
    public DbSet<TrackerRecord> Trackers => Set<TrackerRecord>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<ConversationRecord>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Id).HasMaxLength(64);
            entity.Property(c => c.Name).HasMaxLength(200).IsRequired();
            entity.Property(c => c.Speaker).HasMaxLength(200);
            entity.Property(c => c.Model).HasMaxLength(200);

            // Live conversations are the common read; the index keeps the list cheap once
            // hidden ones start accumulating, since none of them are ever removed.
            entity.HasIndex(c => c.DeletedAtUtc);
        });

        modelBuilder.Entity<MessageRecord>(entity =>
        {
            entity.HasKey(m => m.Id);
            entity.Property(m => m.Id).HasMaxLength(64);
            entity.Property(m => m.ConversationId).HasMaxLength(64).IsRequired();
            entity.Property(m => m.Text).IsRequired();
            entity.Property(m => m.RequestHash).HasMaxLength(64);
            entity.Property(m => m.Model).HasMaxLength(200);

            // The transcript is always read in order, for one conversation at a time.
            entity.HasIndex(m => new { m.ConversationId, m.Sequence }).IsUnique();

            // Idempotency, enforced by the database rather than by a check-then-insert that
            // two processes could both pass. Filtered, because most rows carry no hash.
            entity.HasIndex(m => new { m.ConversationId, m.RequestHash })
                .IsUnique()
                .HasFilter("\"RequestHash\" IS NOT NULL");

            entity.HasOne(m => m.Conversation)
                .WithMany(c => c.Messages)
                .HasForeignKey(m => m.ConversationId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SummaryRecord>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Id).HasMaxLength(64);
            entity.Property(s => s.ConversationId).HasMaxLength(64).IsRequired();
            entity.Property(s => s.Text).IsRequired();
            entity.Property(s => s.Model).HasMaxLength(200);

            // Read in order, for one conversation, every turn. Unique on the range because a
            // stretch of the transcript summarised twice would be sent to the model twice.
            entity.HasIndex(s => new { s.ConversationId, s.FromSequence }).IsUnique();
        });

        modelBuilder.Entity<FactRecord>(entity =>
        {
            entity.HasKey(f => f.Id);
            entity.Property(f => f.Id).HasMaxLength(64);
            entity.Property(f => f.ConversationId).HasMaxLength(64).IsRequired();
            entity.Property(f => f.Subject).HasMaxLength(200).IsRequired();
            entity.Property(f => f.Text).IsRequired();
            entity.Property(f => f.SupersededById).HasMaxLength(64);
            entity.Property(f => f.Model).HasMaxLength(200);

            // The common read by a long way: what is true right now, for this conversation.
            entity.HasIndex(f => new { f.ConversationId, f.ValidToSequence });
        });

        modelBuilder.Entity<TrackerRecord>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.Property(t => t.Id).HasMaxLength(64);
            entity.Property(t => t.ConversationId).HasMaxLength(64).IsRequired();
            entity.Property(t => t.Name).HasMaxLength(120).IsRequired();
            entity.Property(t => t.Note).HasMaxLength(200);
            entity.Property(t => t.Rule).HasMaxLength(400);

            // One meter per name per conversation: two called the same thing would render
            // twice and the model would have no way to tell which one it just moved.
            entity.HasIndex(t => new { t.ConversationId, t.Name }).IsUnique();
        });
    }

    /// <inheritdoc />
    public override int SaveChanges()
    {
        GuardAppendOnly();
        return base.SaveChanges();
    }

    /// <inheritdoc />
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        GuardAppendOnly();
        return base.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Refuses to persist anything that would lose a message.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Invariant 1 of the project says <c>Messages</c> is append-only. That is the sort of rule
    /// that holds for a year and then quietly stops holding, because deleting a row is the
    /// obvious way to implement a feature that asks for a message to go away. Making the
    /// context refuse turns a silent loss of history into a failing test on the day the
    /// mistake is written.
    /// </para>
    /// <para>
    /// Editing an existing message's text is refused for the same reason. Both operations have
    /// a supported shape: hide it with <c>DeletedAtUtc</c>, or append a new turn.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">A delete or a text edit was pending.</exception>
    private void GuardAppendOnly()
    {
        foreach (var entry in ChangeTracker.Entries<MessageRecord>())
        {
            if (entry.State == EntityState.Deleted)
            {
                throw new InvalidOperationException(
                    $"Messages are append-only; message '{entry.Entity.Id}' cannot be deleted. "
                    + "Set DeletedAtUtc to hide it instead.");
            }

            if (entry.State == EntityState.Modified
                && entry.Property(nameof(MessageRecord.Text)).IsModified)
            {
                throw new InvalidOperationException(
                    $"Messages are append-only; the text of message '{entry.Entity.Id}' cannot be "
                    + "rewritten. Append a new message instead.");
            }
        }

        foreach (var entry in ChangeTracker.Entries<ConversationRecord>())
        {
            if (entry.State == EntityState.Deleted)
            {
                throw new InvalidOperationException(
                    $"Conversation '{entry.Entity.Id}' cannot be deleted; it would take its "
                    + "messages with it. Set DeletedAtUtc to hide it instead.");
            }
        }
    }
}
