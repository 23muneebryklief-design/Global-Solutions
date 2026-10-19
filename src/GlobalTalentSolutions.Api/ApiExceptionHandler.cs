using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using GlobalTalentSolutions.Identity;
using GlobalTalentSolutions.Enquiries;
using GlobalTalentSolutions.Conversations;
using GlobalTalentSolutions.SharedKernel;
using GlobalTalentSolutions.Notifications;
using Npgsql;

public sealed class ApiExceptionHandler(
    ILogger<ApiExceptionHandler> logger, IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        var status = exception switch
        {
            IdentityConflictException => 409,
            OnboardingConflictException => 409,
            EmailUnavailableException => 503,
            EmailEventConflictException => 409,
            EmailEventForbiddenException => 403,
            EnquiryConflictException => 409,
            EnquiryUnavailableException => 503,
            ConversationConflictException => 409,
            ConversationUnavailableException => 503,
            IdentityForbiddenException => 403,
            PostgresException { SqlState: "23505" or "23503" } => 409,
            IdentityProviderException => 503,
            _ => 500
        };
        // Provider/database exceptions can contain sensitive query values; log type only.
        logger.LogError("Request failed with {ExceptionType}; TraceId {TraceId}", exception.GetType().Name, context.TraceIdentifier);
        context.Response.StatusCode = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = status switch { 403 => "Access denied.", 409 => "The requested change conflicts with the current state.", 503 => "A required service is unavailable.", _ => "An unexpected error occurred." },
                Detail = "Contact support with the trace ID if the problem continues."
            }
        });
    }
}
