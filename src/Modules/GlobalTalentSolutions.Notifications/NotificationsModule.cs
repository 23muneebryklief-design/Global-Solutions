using Microsoft.Extensions.DependencyInjection;

namespace GlobalTalentSolutions.Notifications;

public static class NotificationsModule
{
    public static IServiceCollection AddNotificationsModule(this IServiceCollection services)
    {
        // Register this module's services as its feature tickets are implemented.
        return services;
    }
}
