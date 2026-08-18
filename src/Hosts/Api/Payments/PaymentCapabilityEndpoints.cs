using Api.Iam;
using BuildingBlocks.Application;
using Iam.Domain.Permissions;
using Payments.Application.Capabilities;

namespace Api.PaymentCapabilities;

internal static class PaymentCapabilityEndpoints
{
    public static void MapPaymentCapabilityEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/payments/methods", async (
            IActorContext actor,
            IEffectivePaymentCapabilityResolver resolver,
            CancellationToken ct) =>
        {
            if (!actor.HasActor || actor.UserId is null)
                return Results.Problem(statusCode: StatusCodes.Status404NotFound);
            var subject = new PaymentCapabilitySubject(
                actor.MerchantId, PaymentAudience.User, actor.UserId);
            var methods = await resolver.ListMethodsAsync(subject, ct);
            return methods.Count == 0
                ? Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                    extensions: new Dictionary<string, object?>
                    {
                        ["code"] = "payment_method_not_allowed",
                    })
                : Results.Ok(new EffectivePaymentMethodsResponse(methods.Select(x => x.Method).ToList()));
        }).RequireAuthorization("merchant-user").RequirePermission(Keys.PaymentView)
            .WithTags("การชำระเงิน").WithName("ListMyEffectivePaymentMethods")
            .WithSummary("Payment methods ที่ผู้ใช้ปัจจุบันใช้ได้")
            .WithDescription("resolve MerchantId และ UserId จาก session ฝั่ง server แล้วคืนเฉพาะ canonical effective methods")
            .Produces<EffectivePaymentMethodsResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/payments/methods/{method}/options", async (
            string method,
            string provider,
            IActorContext actor,
            IEffectivePaymentCapabilityResolver resolver,
            CancellationToken ct) =>
        {
            if (!actor.HasActor || actor.UserId is null)
                return Results.Problem(statusCode: StatusCodes.Status404NotFound);
            var options = await resolver.ResolveOptionsAsync(new ResolvePaymentMethod(
                new PaymentCapabilitySubject(actor.MerchantId, PaymentAudience.User, actor.UserId),
                method, provider), ct);
            return Results.Ok(new EffectivePaymentOptionsResponse(
                global::Payments.Domain.PaymentMethods.Normalize(method), provider.Trim().ToLowerInvariant(), options));
        }).RequireAuthorization("merchant-user").RequirePermission(Keys.PaymentView)
            .WithTags("การชำระเงิน").WithName("ListMyEffectivePaymentOptions")
            .WithSummary("Payment options ที่ผู้ใช้ปัจจุบันใช้ได้")
            .WithDescription("ใช้ identity จาก session และ selected provider เท่านั้น; method ไม่ effective คืน options ว่าง")
            .Produces<EffectivePaymentOptionsResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }
}

internal sealed record EffectivePaymentMethodsResponse(IReadOnlyList<string> Methods);
internal sealed record EffectivePaymentOptionsResponse(
    string Method,
    string Provider,
    IReadOnlyList<EffectivePaymentOption> Options);
