namespace Warranty.Application.Abstractions.Persistence;

/// <summary>
/// A save found a row changed since it was read (optimistic concurrency on the row version); nothing
/// of the unit of work was committed. Thrown by <see cref="IUnitOfWork"/>.
/// </summary>
public sealed class ConcurrencyConflictException : InvalidOperationException
{
    public ConcurrencyConflictException()
    {
    }

    public ConcurrencyConflictException(string message)
        : base(message)
    {
    }

    public ConcurrencyConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
