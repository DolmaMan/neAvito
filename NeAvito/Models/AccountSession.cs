namespace NeAvito.Models;

internal sealed class AccountSession
{
    public Guid Id { get; init; }
    public required string UserId { get; init; }
    public DateTime ExpiresAtUtc { get; init; }
}
