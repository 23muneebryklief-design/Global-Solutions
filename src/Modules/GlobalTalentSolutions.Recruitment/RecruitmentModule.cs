using Microsoft.Extensions.DependencyInjection;

namespace GlobalTalentSolutions.Recruitment;

public static class RecruitmentModule
{
    public static IServiceCollection AddRecruitmentModule(this IServiceCollection services)
    {
        // Register this module's services as its feature tickets are implemented.
        return services;
    }
}
