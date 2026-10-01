using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentLocalWeb.Agent.Core;
using AgentLocalWeb.Brain;
using AgentLocalWeb.Brain.Fake;
using AgentLocalWeb.Brain.GeminiWeb;
using AgentLocalWeb.Persistence;
using AgentLocalWeb.Tools;
using AgentLocalWeb.Workspace;

namespace AgentLocalWeb.AppHost;

internal static class LocalApplication
{
    public static async Task<WebApplication> CreateAsync(
        string[] args,
        LocalApplicationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new LocalApplicationOptions();
        var timeProvider = options.TimeProvider ?? TimeProvider.System;
        var databasePath = options.DatabasePath ?? AppStoragePaths.GetDefaultDatabasePath();

        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton(timeProvider);
        builder.Services.AddSingleton<LocalAccessSession>();
        builder.Services.AddSingleton(_ => new SqliteAppStore(databasePath, timeProvider));
        builder.Services.AddSingleton<WorkspaceManager>();
        if (options.WorkspacePicker is null)
        {
            builder.Services.AddSingleton<IWorkspacePicker, WindowsFolderPicker>();
        }
        else
        {
            builder.Services.AddSingleton<IWorkspacePicker>(_ => options.WorkspacePicker);
        }
        builder.Services.ConfigureHttpJsonOptions(json =>
            json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddSingleton<IBrain>(_ =>
            options.Brain ?? CreateConfiguredBrain(args));
        builder.Services.AddSingleton<IWorkspacePermissionModeProvider,
            SqliteWorkspacePermissionModeProvider>();
        builder.Services.AddSingleton<IToolDispatcher, WorkspaceToolDispatcher>();
        builder.Services.AddSingleton<ApprovalCoordinator>();
        builder.Services.AddSingleton<IToolApprovalGateway>(
            provider => provider.GetRequiredService<ApprovalCoordinator>());
        builder.Services.AddSingleton<SqliteAgentRunEventSink>();
        builder.Services.AddSingleton<IAgentRunEventSink>(
            provider => provider.GetRequiredService<SqliteAgentRunEventSink>());
        builder.Services.AddSingleton<IAgentToolCallSink>(
            provider => provider.GetRequiredService<SqliteAgentRunEventSink>());
        builder.Services.AddSingleton<AgentRunEngine>();
        builder.Services.AddSingleton<ConversationService>();

        var app = builder.Build();
        var accessSession = app.Services.GetRequiredService<LocalAccessSession>();
        var store = app.Services.GetRequiredService<SqliteAppStore>();
        var workspaceManager = app.Services.GetRequiredService<WorkspaceManager>();
        var conversationService = app.Services.GetRequiredService<ConversationService>();

        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var persistedState = await store.GetStateAsync(cancellationToken).ConfigureAwait(false);
        if (persistedState.LastWorkspaceRoot is not null &&
            !workspaceManager.TryRestore(persistedState.LastWorkspaceRoot, out _))
        {
            await store.ClearWorkspaceAsync(cancellationToken).ConfigureAwait(false);
        }

        await conversationService.InitializeAsync(cancellationToken).ConfigureAwait(false);

        app.Use(async (context, next) =>
        {
            context.Response.Headers["Content-Security-Policy"] =
                "default-src 'self'; base-uri 'none'; frame-ancestors 'none'; object-src 'none'; form-action 'none'";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";

            if (!LocalRequestSecurity.HasAllowedHost(context.Request) ||
                !LocalRequestSecurity.HasAllowedOrigin(context.Request))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next(context).ConfigureAwait(false);
        });

        app.UseDefaultFiles();
        app.UseStaticFiles();

        app.MapPost("/api/session/bootstrap", (BootstrapRequest request, HttpContext context) =>
        {
            var credentials = accessSession.TryBootstrap(request.Token);
            if (credentials is null)
            {
                return Results.Unauthorized();
            }

            context.Response.Cookies.Append(
                LocalAccessSession.CookieName,
                credentials.SessionToken,
                new CookieOptions
                {
                    HttpOnly = true,
                    SameSite = SameSiteMode.Strict,
                    Secure = false,
                    Path = "/",
                    IsEssential = true
                });
            return Results.Ok(new { csrfToken = credentials.CsrfToken });
        });

        var api = app.MapGroup("/api");
        api.AddEndpointFilter(async (invocation, next) =>
        {
            var context = invocation.HttpContext;
            var sessionToken = context.Request.Cookies[LocalAccessSession.CookieName];
            if (!accessSession.IsAuthenticated(sessionToken))
            {
                return Results.Unauthorized();
            }

            if (LocalRequestSecurity.RequiresCsrf(context.Request) &&
                !accessSession.IsValidCsrf(
                    context.Request.Headers[LocalAccessSession.CsrfHeaderName]))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            return await next(invocation).ConfigureAwait(false);
        });

        api.MapGet("/session", (HttpContext context) => Results.Ok(new
        {
            csrfToken = accessSession.GetCsrfToken(
                context.Request.Cookies[LocalAccessSession.CookieName])
        }));
        api.MapGet("/state", async (IBrain brain, CancellationToken requestCancellation) =>
        {
            var appState = await store.GetStateAsync(requestCancellation).ConfigureAwait(false);
            var latestRun = appState.ActiveConversationId is null
                ? null
                : await store.GetLatestRunForConversationAsync(
                    appState.ActiveConversationId,
                    requestCancellation).ConfigureAwait(false);
            var latestToolCalls = latestRun is null
                ? []
                : await store.ReadToolCallsForRunAsync(latestRun.Id, requestCancellation)
                    .ConfigureAwait(false);
            var pendingApproval = await store.GetPendingApprovalAsync(requestCancellation)
                .ConfigureAwait(false);
            return Results.Ok(new
            {
                brain = await brain.GetStatusAsync(requestCancellation).ConfigureAwait(false),
                workspace = workspaceManager.Current,
                latestRun,
                latestToolCalls = latestToolCalls.Select(call => new
                {
                    call.Tool,
                    call.Status,
                    call.CreatedAt,
                    call.CompletedAt,
                    call.ErrorCode
                }),
                pendingApproval,
                appState.PermissionMode,
                appState.LatestEventSequence
            });
        });
        api.MapGet("/conversation", async (CancellationToken requestCancellation) =>
            Results.Ok(await conversationService.GetCurrentAsync(requestCancellation)
                .ConfigureAwait(false)));
        api.MapPost("/conversation/new", async (CancellationToken requestCancellation) =>
        {
            try
            {
                return Results.Ok(await conversationService.StartNewAsync(requestCancellation)
                    .ConfigureAwait(false));
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
        });
        api.MapPost("/conversation/messages", async (
            SendMessageRequest request,
            CancellationToken requestCancellation) =>
        {
            if (string.IsNullOrWhiteSpace(request.Content))
            {
                return Results.BadRequest(new { error = "Message content is required." });
            }

            try
            {
                var conversation = await conversationService.SendAsync(
                    request.Content,
                    requestCancellation).ConfigureAwait(false);
                return Results.Ok(conversation);
            }
            catch (BrainException exception)
            {
                return Results.Json(
                    new { error = exception.Message, code = exception.Code },
                    statusCode: StatusCodes.Status409Conflict);
            }
            catch (AgentRunFailedException exception)
            {
                return Results.Json(
                    new
                    {
                        error = exception.Message,
                        code = exception.Result.Error?.Code,
                        runId = exception.Result.RunId,
                        state = exception.Result.State
                    },
                    statusCode: StatusCodes.Status409Conflict);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });
        api.MapPost("/runs/cancel", async (
            AgentRunEngine runEngine,
            CancellationToken requestCancellation) =>
        {
            var runId = runEngine.ActiveRunId;
            if (runId is null)
            {
                return Results.Conflict(new { error = "There is no active run to cancel." });
            }

            if (!await store.RequestRunCancellationAsync(runId, requestCancellation)
                    .ConfigureAwait(false) ||
                !runEngine.CancelActive())
            {
                return Results.Conflict(new { error = "The active run already finished." });
            }

            return Results.Accepted(value: new { runId });
        });
        api.MapPost("/approvals/{approvalId}/decision", async (
            string approvalId,
            ApprovalDecisionRequest request,
            ApprovalCoordinator approvals,
            CancellationToken requestCancellation) =>
        {
            if (!Enum.TryParse<ToolApprovalDecision>(request.Decision, ignoreCase: true, out var decision))
            {
                return Results.BadRequest(new { error = "Decision must be Approved or Rejected." });
            }

            try
            {
                var approval = await approvals.DecideAsync(
                    approvalId,
                    request.ActionHash,
                    decision,
                    requestCancellation).ConfigureAwait(false);
                return Results.Ok(new { approval.Id, approval.Status });
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
        });
        api.MapPost("/settings/permission-mode", async (
            PermissionModeRequest request,
            CancellationToken requestCancellation) =>
        {
            try
            {
                await store.SetPermissionModeAsync(request.PermissionMode, requestCancellation)
                    .ConfigureAwait(false);
                return Results.Ok(new { request.PermissionMode });
            }
            catch (ArgumentOutOfRangeException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });
        api.MapPost("/brain/connect", async (
            IBrain brain,
            CancellationToken requestCancellation) =>
        {
            await store.AppendEventAsync(
                "brain.connection_started",
                JsonSerializer.Serialize(new { brain.Provider }),
                cancellationToken: requestCancellation).ConfigureAwait(false);
            try
            {
                var status = await brain.ConnectAsync(
                    new BrainConnectionRequest(Interactive: false),
                    requestCancellation).ConfigureAwait(false);
                await store.AppendEventAsync(
                    "brain.connection_changed",
                    JsonSerializer.Serialize(new { status.Provider, status.State }),
                    cancellationToken: requestCancellation).ConfigureAwait(false);
                return Results.Ok(new { brain = status });
            }
            catch (BrainException exception)
            {
                await store.AppendEventAsync(
                    "brain.connection_failed",
                    JsonSerializer.Serialize(new
                    {
                        brain.Provider,
                        Code = exception.Code.ToString()
                    }),
                    cancellationToken: requestCancellation).ConfigureAwait(false);
                return Results.Json(
                    new { error = exception.Message, code = exception.Code },
                    statusCode: StatusCodes.Status409Conflict);
            }
        });
        api.MapPost("/workspace/pick", async (
            IWorkspacePicker picker,
            CancellationToken requestCancellation) =>
        {
            var selectedPath = await picker.PickAsync(
                workspaceManager.Current?.CanonicalRoot,
                requestCancellation).ConfigureAwait(false);
            if (selectedPath is null)
            {
                return Results.NoContent();
            }

            try
            {
                var selection = workspaceManager.Select(selectedPath);
                await store.SaveWorkspaceSelectionAsync(
                    selection.CanonicalRoot,
                    JsonSerializer.Serialize(new
                    {
                        selection.CanonicalRoot,
                        selection.DisplayName
                    }),
                    requestCancellation).ConfigureAwait(false);
                return Results.Ok(new { workspace = selection });
            }
            catch (WorkspaceSelectionException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });
        api.MapGet("/events", SseEventStream.WriteAsync);
        app.MapFallbackToFile("index.html");

        return app;
    }

    private static IBrain CreateConfiguredBrain(string[] args)
    {
        if (args.Any(
                argument => string.Equals(
                    argument,
                    "--brain=gemini",
                    StringComparison.OrdinalIgnoreCase)))
        {
            return new GeminiWebBrain();
        }

        return new FakeBrain(
            responseFactory: request => new BrainResponse.Final(
                $"fake-{request.Messages.Count}",
                JsonSerializer.Serialize(new
                {
                    protocol = ProtocolV1.Version,
                    type = "final",
                    message = $"Fake Brain đã nhận: {request.Messages.Last().Content}"
                })));
    }
}

internal sealed record LocalApplicationOptions(
    string? DatabasePath = null,
    TimeProvider? TimeProvider = null,
    IBrain? Brain = null,
    IWorkspacePicker? WorkspacePicker = null);

internal sealed record BootstrapRequest(string Token);

internal sealed record SendMessageRequest(string Content);

internal sealed record ApprovalDecisionRequest(string ActionHash, string Decision);

internal sealed record PermissionModeRequest(string PermissionMode);
