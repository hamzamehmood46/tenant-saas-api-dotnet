namespace TenantSaas.Api;

public enum Plan { Free, Pro }

/// <summary>Ordered so that a higher role can do everything a lower one can.</summary>
public enum Role { ReadOnly = 0, Member = 1, Admin = 2 }

public interface ITenantOwned
{
    Guid TenantId { get; set; }
}

public class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public Plan Plan { get; set; }
}

public class ApiKey : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Label { get; set; } = "";
    public string Prefix { get; set; } = "";
    public string KeyHash { get; set; } = "";
    public Role Role { get; set; }
    public bool Revoked { get; set; }
}

public class Project : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class TaskItem : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ProjectId { get; set; }
    public string Title { get; set; } = "";
    public bool Done { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class AuditEntry : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid KeyId { get; set; }
    public string Action { get; set; } = "";
    public string Entity { get; set; } = "";
    public Guid EntityId { get; set; }
    public DateTime At { get; set; }
}

/// <summary>Who is calling, resolved once per request from the API key. Guid.Empty means nobody.</summary>
public class TenantContext
{
    public Guid TenantId { get; set; }
    public Guid KeyId { get; set; }
    public Role Role { get; set; }
    public Plan Plan { get; set; }
    public bool IsAuthenticated => TenantId != Guid.Empty;
}
