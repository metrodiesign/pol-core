namespace Carts.Domain;

/// <summary>Lifecycle of a <see cref="Cart"/>: open for edits, or frozen once checkout has begun.</summary>
public enum CartStatus
{
    Open = 0,
    CheckedOut = 1,
}
