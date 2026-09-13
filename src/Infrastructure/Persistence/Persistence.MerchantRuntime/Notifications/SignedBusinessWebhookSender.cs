using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.DataProtection;
using Notifications.Application;

namespace Persistence.MerchantRuntime.Notifications;

internal sealed class SignedBusinessWebhookSender(
    ISafeDestinationValidator destinations,
    IDataProtectionProvider protection,
    Func<HttpMessageHandler>? handlerFactory = null) : IBusinessWebhookSender
{
    private const int MaxPayloadBytes = 256 * 1024;

    public async Task<DeliveryProviderResult> SendAsync(
        BusinessWebhookDeliveryRequest request, CancellationToken cancellationToken)
    {
        if (Encoding.UTF8.GetByteCount(request.Payload) > MaxPayloadBytes)
            return new DeliveryProviderResult(
                DeliveryProviderOutcome.Failed, FailureCode: "payload_too_large");

        ValidatedDestination destination;
        try
        {
            destination = await destinations.ResolveAsync(request.Url, cancellationToken);
        }
        catch (InvalidRequestException exception)
        {
            return new DeliveryProviderResult(
                DeliveryProviderOutcome.Failed, FailureCode: exception.Code);
        }

        string secret;
        try
        {
            secret = protection.CreateProtector("pol-core/delivery-secret/v1")
                .Unprotect(request.ProtectedSecret);
        }
        catch (CryptographicException)
        {
            return new DeliveryProviderResult(
                DeliveryProviderOutcome.Failed, FailureCode: "signing_secret_unavailable");
        }

        using var handler = handlerFactory?.Invoke() ?? PinnedHandler(destination.Address);
        using var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, destination.Uri);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        httpRequest.Headers.TryAddWithoutValidation("X-POL-Delivery-Id", request.DeliveryId.ToString("D"));
        httpRequest.Headers.TryAddWithoutValidation("X-POL-Timestamp", timestamp);
        httpRequest.Headers.TryAddWithoutValidation(
            "X-POL-Signature", Sign(secret, timestamp, request.DeliveryId, request.Payload));
        httpRequest.Content = new StringContent(request.Payload, Encoding.UTF8, "application/json");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var response = await client.SendAsync(
                httpRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var status = (int)response.StatusCode;
            if (status is >= 200 and < 300)
                return new DeliveryProviderResult(DeliveryProviderOutcome.Delivered);
            if (status is >= 300 and < 400)
                return new DeliveryProviderResult(
                    DeliveryProviderOutcome.Failed, FailureCode: "redirect_rejected");
            if (status is 408 or 429 or >= 500)
                return new DeliveryProviderResult(
                    DeliveryProviderOutcome.Failed, FailureCode: "http_retryable");
            return new DeliveryProviderResult(
                DeliveryProviderOutcome.Failed, FailureCode: "http_rejected");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DeliveryProviderAmbiguousException("Business webhook response timed out.");
        }
        catch (HttpRequestException exception)
        {
            throw new DeliveryProviderAmbiguousException(
                "Business webhook response was unavailable.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(Encoding.UTF8.GetBytes(secret));
        }
    }

    internal static SocketsHttpHandler CreatePinnedHandler(IPAddress address) => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.Zero,
        ConnectCallback = async (context, cancellationToken) =>
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
            };
            try
            {
                await socket.ConnectAsync(
                    new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    private static SocketsHttpHandler PinnedHandler(IPAddress address) => CreatePinnedHandler(address);

    private static string Sign(string secret, string timestamp, Guid deliveryId, string payload)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        var body = Encoding.UTF8.GetBytes($"{timestamp}.{deliveryId:D}.{payload}");
        try
        {
            return $"sha256={Convert.ToHexString(HMACSHA256.HashData(key, body)).ToLowerInvariant()}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(body);
        }
    }
}
