using Microsoft.AspNetCore.Diagnostics;
using Octopushy.Server.Git;
using Octopushy.Server.Orchestration;
using Octopushy.Server.Store;

namespace Octopushy.Server.Api;

/// <summary>Turns the domain's expected refusals into problem responses; anything else is a 500.</summary>
public sealed class ApiExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        var status = exception switch
        {
            RunException => StatusCodes.Status409Conflict,
            StoreConflictException => StatusCodes.Status409Conflict,
            StoreException => StatusCodes.Status400BadRequest,
            GitException => StatusCodes.Status502BadGateway,
            BadHttpRequestException => StatusCodes.Status400BadRequest,
            _ => 0,
        };
        if (status == 0)
        {
            return false;
        }

        context.Response.StatusCode = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            Exception = exception,
            ProblemDetails = { Status = status, Title = exception.Message },
        });
    }
}
