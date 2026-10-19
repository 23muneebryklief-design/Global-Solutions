using System.Data.Common;

namespace GlobalTalentSolutions.SharedKernel;

public interface IEmailNotifications
{
    Task EnquiryCreated(DbConnection connection,DbTransaction transaction,Guid enquiryId,string reference,string email,CancellationToken ct);
    Task InvitationCreated(DbConnection connection,DbTransaction transaction,Guid invitationId,string email,string acceptanceUrl,CancellationToken ct);
    Task CancelInvitation(DbConnection connection,DbTransaction transaction,Guid invitationId,CancellationToken ct);
    Task StaffReplied(DbConnection connection,DbTransaction transaction,Guid messageId,Guid enquiryId,string reference,string email,string body,CancellationToken ct);
}
public sealed record EnquiryMailDetails(string Reference,string Email,Guid? AccountId=null);
public interface IEnquiryMailDetails
{
    Task<EnquiryMailDetails?> MailDetails(DbConnection connection,DbTransaction transaction,Guid enquiryId,CancellationToken ct);
}
public interface IInvitationEmailValidity
{
    Task<bool> CanSend(DbConnection connection,DbTransaction transaction,Guid invitationId,CancellationToken ct);
}
public interface IEmailContact
{
    Task<string?> Recipient(DbConnection connection,DbTransaction transaction,Guid accountId,Guid? companyId,CancellationToken ct);
}
