using Microsoft.Extensions.DependencyInjection;

namespace GlobalTalentSolutions.Documents;

public static class DocumentsModule
{
    public static IServiceCollection AddDocumentsModule(this IServiceCollection services)
    {
        // Register this module's services as its feature tickets are implemented.
        return services;
    }
}
