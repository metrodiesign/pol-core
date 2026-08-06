namespace Products.Application.Ports;

/// <summary>
/// The sale code about to be bound to the upstream <c>@SaleCode</c> parameter is not a value that parameter can
/// carry unchanged (products-external-source-of-truth REQ-4.11): longer than the declared <c>varchar(20)</c>, or
/// carrying a character <c>varchar</c> cannot represent. <c>SqlParameter.Size</c> would truncate it and
/// non-unicode conversion would mangle it, both WITHOUT an error — the search would then run under a different
/// party's code and quietly return their documents, so refusing is the only safe move.
/// <para>Deliberately NOT an <see cref="ArgumentException"/>: reaching here means the edge validation on
/// <c>Merchants.Domain.Users.User.SetDetails</c> (REQ-4.10) let a bad value into the database, which is our bug,
/// not the caller's. It falls through to the default arm of the shared ProblemDetails handler as an opaque 500
/// with the detail logged server-side.</para>
/// </summary>
public sealed class SaleCodeBindingException : Exception
{
    public SaleCodeBindingException(string message) : base(message) { }
}
