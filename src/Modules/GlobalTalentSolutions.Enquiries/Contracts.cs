using System.Net.Mail;

namespace GlobalTalentSolutions.Enquiries;

public sealed record SubmitEnquiry(
    string? EnquiryType, string? Name, string? Email, string? Phone,
    string? CompanyName, string? CountryCode, string? Service, string? Message,
    bool Consent, Guid? SubmissionKey, string? Website = null);
public sealed record NormalizedEnquiry(
    string EnquiryType, string Name, string Email, string? Phone, string? CompanyName,
    string? CountryCode, string Service, string Message, Guid SubmissionKey, string PrivacyNoticeVersion);
public sealed record EnquiryReceipt(string Reference, string Message = "Your enquiry has been received.");
public sealed record SubmissionResult(string Reference, bool Created);
public sealed record EnquiryRecord(
    Guid Id, string Reference, string EnquiryType, string Name, string Email, string? Phone,
    string? CompanyName, string? CountryCode, string Service, string Message, string Status,
    Guid? AssignedStaffId, DateTimeOffset ConsentAt, string PrivacyNoticeVersion,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record EnquiryQuery(
    string? Status = null, string? EnquiryType = null, Guid? AssignedStaffId = null,
    bool Unassigned = false, string? Search = null, int Page = 1, int PageSize = 25);
public sealed record EnquiryPage(List<EnquiryRecord> Items, long TotalCount, int Page, int PageSize)
{
    public long TotalPages => (TotalCount + PageSize - 1) / PageSize;
}
public sealed record UpdateEnquiry(
    DateTimeOffset? ExpectedUpdatedAt, string? Status = null, Guid? AssignedStaffId = null,
    bool ClearAssignment = false);
public enum UpdateOutcome { Updated, NotFound, Conflict }
public sealed record UpdateResult(UpdateOutcome Outcome, EnquiryRecord? Enquiry = null);
public sealed class EnquiryConflictException : Exception;
public sealed class EnquiryUnavailableException : Exception;

public static class EnquiryValidation
{
    public static Dictionary<string,string[]> Validate(SubmitEnquiry input)
    {
        var errors = new Dictionary<string,string[]>();
        if (input.EnquiryType is not ("business" or "candidate")) errors["enquiryType"] = ["Choose business or candidate."];
        Text(errors,"name",input.Name,2,150);
        if (input.Email is null || input.Email.Trim().Length > 320 ||
            !MailAddress.TryCreate(input.Email.Trim(),out var parsed) ||
            !parsed.Address.Equals(input.Email.Trim(),StringComparison.OrdinalIgnoreCase))
            errors["email"] = ["Enter a valid email address."];
        Text(errors,"service",input.Service,1,150);
        Text(errors,"message",input.Message,10,10000);
        Optional(errors,"phone",input.Phone,50);
        Optional(errors,"companyName",input.CompanyName,200);
        if (input.EnquiryType == "business" && string.IsNullOrWhiteSpace(input.CompanyName)) errors["companyName"] = ["Company name is required for business enquiries."];
        if (input.EnquiryType == "candidate" && !string.IsNullOrWhiteSpace(input.CompanyName)) errors["companyName"] = ["Leave company name empty for candidate enquiries."];
        if (!string.IsNullOrWhiteSpace(input.CountryCode) &&
            (input.CountryCode.Trim().Length != 2 || !input.CountryCode.Trim().All(char.IsAsciiLetter)))
            errors["countryCode"] = ["Use a two-letter country code."];
        if (!input.Consent) errors["consent"] = ["Consent to the privacy notice is required."];
        if (input.SubmissionKey is null || input.SubmissionKey == Guid.Empty) errors["submissionKey"] = ["Provide a new UUID for this submission and reuse it only for retries."];
        if (new[]{input.Name,input.Email,input.Phone,input.CompanyName,input.CountryCode,input.Service,input.Message}.Any(value=>value?.Contains('\0') == true))
            errors["form"] = ["Text cannot contain null characters."];
        return errors;
    }
    public static Dictionary<string,string[]> Validate(EnquiryQuery input)
    {
        var errors = new Dictionary<string,string[]>();
        if(input.Status is not null && !ValidStatus(input.Status)) errors["status"] = ["Choose new, in_progress or closed."];
        if(input.EnquiryType is not null && input.EnquiryType is not ("business" or "candidate")) errors["enquiryType"] = ["Choose business or candidate."];
        if(input.Page is <1 or >100000) errors["page"] = ["Page must be between 1 and 100000."];
        if(input.PageSize is <1 or >100) errors["pageSize"] = ["Page size must be between 1 and 100."];
        if(input.Search?.Length >200 || input.Search?.Contains('\0')==true) errors["search"] = ["Search must be at most 200 characters and contain no null characters."];
        if(input.AssignedStaffId == Guid.Empty || input.Unassigned && input.AssignedStaffId.HasValue) errors["assignedStaffId"] = ["Choose a staff ID or the unassigned filter, not both."];
        return errors;
    }
    public static Dictionary<string,string[]> Validate(UpdateEnquiry input)
    {
        var errors = new Dictionary<string,string[]>();
        if(input.ExpectedUpdatedAt is null) errors["expectedUpdatedAt"] = ["Send the updatedAt value from the last read to prevent overwriting another staff member's changes."];
        if(input.Status is not null && !ValidStatus(input.Status)) errors["status"] = ["Choose new, in_progress or closed."];
        if(input.AssignedStaffId == Guid.Empty || input.ClearAssignment && input.AssignedStaffId.HasValue) errors["assignedStaffId"] = ["Assign a staff member or clear the assignment, not both."];
        if(input.Status is null && input.AssignedStaffId is null && !input.ClearAssignment) errors["update"] = ["Provide a status or assignment change."];
        return errors;
    }
    public static NormalizedEnquiry Normalize(SubmitEnquiry input) => new(
        input.EnquiryType!,input.Name!.Trim(),input.Email!.Trim().ToLowerInvariant(),Empty(input.Phone),
        Empty(input.CompanyName),Empty(input.CountryCode)?.ToUpperInvariant(),input.Service!.Trim(),
        input.Message!.Trim(),input.SubmissionKey!.Value,"v1");
    public static bool ValidStatus(string value) => value is "new" or "in_progress" or "closed";
    private static string? Empty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    private static void Text(Dictionary<string,string[]> errors,string field,string? text,int min,int max)
    {
        if(string.IsNullOrWhiteSpace(text) || text.Trim().Length < min || text.Trim().Length > max)
            errors[field] = [$"Must be {min}–{max} characters."];
    }
    private static void Optional(Dictionary<string,string[]> errors,string field,string? text,int max)
    {
        if(text?.Trim().Length > max) errors[field] = [$"Must be at most {max} characters."];
    }
}
