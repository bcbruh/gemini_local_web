using System.Text.Json;
using AgentLocalWeb.Persistence;

namespace AgentLocalWeb.AppHost;

internal static class SseEventStream
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    public static async Task WriteAsync(HttpContext context, SqliteAppStore store)
    {
        if (!TryGetCursor(context.Request, out var cursor))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(
                new { error = "The event cursor must be a non-negative integer." },
                context.RequestAborted).ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache, no-store";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        await context.Response.StartAsync(context.RequestAborted).ConfigureAwait(false);

        while (!context.RequestAborted.IsCancellationRequested)
        {
            var events = await store.ReadEventsAfterAsync(
                cursor,
                cancellationToken: context.RequestAborted).ConfigureAwait(false);
            if (events.Count != 0)
            {
                foreach (var appEvent in events)
                {
                    using var payload = JsonDocument.Parse(appEvent.PayloadJson);
                    var envelope = JsonSerializer.Serialize(new
                    {
                        appEvent.Sequence,
                        appEvent.OccurredAt,
                        appEvent.Type,
                        Payload = payload.RootElement
                    }, SerializerOptions);
                    await context.Response.WriteAsync(
                        $"id: {appEvent.Sequence}\ndata: {envelope}\n\n",
                        context.RequestAborted).ConfigureAwait(false);
                    cursor = appEvent.Sequence;
                }

                await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
                continue;
            }

            using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(
                context.RequestAborted);
            heartbeat.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                await store.WaitForEventAfterAsync(cursor, heartbeat.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
            {
                await context.Response.WriteAsync(
                    ": keepalive\n\n",
                    context.RequestAborted).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
            }
        }
    }

    private static bool TryGetCursor(HttpRequest request, out long cursor)
    {
        var value = request.Headers["Last-Event-ID"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(value))
        {
            value = request.Query["after"].FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            cursor = 0;
            return true;
        }

        return long.TryParse(
            value,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out cursor) && cursor >= 0;
    }
}
