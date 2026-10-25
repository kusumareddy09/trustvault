
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Mail;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

using TrustVault.Api;
using TrustVault.Api.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(
        builder.Configuration.GetConnectionString("TrustVault")
        ?? "Data Source=trustvault.db"));

// Automatically clean expired Recycle Bin documents.
builder.Services.AddHostedService<RecycleBinCleanupService>();

builder.Services.AddScoped<
    IPasswordHasher<UserAccount>,
    PasswordHasher<UserAccount>>();

var configuredKey = builder.Configuration["Jwt:Key"];

if (string.IsNullOrWhiteSpace(configuredKey))
{
    throw new InvalidOperationException(
        "Configure a JWT signing key using Jwt__Key or .NET User Secrets.");
}

var issuer = builder.Configuration["Jwt:Issuer"] ?? "TrustVault";
var audience = builder.Configuration["Jwt:Audience"]
               ?? "TrustVault.Client";

var signingKey = new SymmetricSecurityKey(
    Encoding.UTF8.GetBytes(configuredKey));

builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
    {
        policy.WithOrigins(
                "http://localhost:4200",
                "http://localhost:4201")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters =
            new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = issuer,
                ValidateAudience = true,
                ValidAudience = audience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = signingKey,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                NameClaimType = ClaimTypes.Name,
                RoleClaimType = ClaimTypes.Role
            };
    });

builder.Services.AddAuthorization();

var app = builder.Build();

// ============================================================
// DATABASE INITIALIZATION AND SAFE SQLITE SCHEMA UPGRADE
// ============================================================

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider
        .GetRequiredService<AppDbContext>();

    db.Database.EnsureCreated();

    var connection = db.Database.GetDbConnection();

    var wasClosed =
        connection.State != System.Data.ConnectionState.Open;

    if (wasClosed)
        await connection.OpenAsync();

    try
    {
        var columns = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info('Documents');";

            await using var reader =
                await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(1));
            }
        }

        if (!columns.Contains("IsDeleted"))
        {
            await using var command = connection.CreateCommand();

            command.CommandText =
                "ALTER TABLE Documents ADD COLUMN IsDeleted INTEGER NOT NULL DEFAULT 0;";

            await command.ExecuteNonQueryAsync();
        }

        if (!columns.Contains("DeletedAtUtc"))
        {
            await using var command = connection.CreateCommand();

            command.CommandText =
                "ALTER TABLE Documents ADD COLUMN DeletedAtUtc TEXT NULL;";

            await command.ExecuteNonQueryAsync();
        }
    }
    finally
    {
        if (wasClosed)
            await connection.CloseAsync();
    }

    if (app.Environment.IsDevelopment())
    {
        var hasher = scope.ServiceProvider
            .GetRequiredService<IPasswordHasher<UserAccount>>();

        var demoAccounts = new[]
        {
            (
                Email: "admin@trustvault.local",
                Password: "AdminDemo!2026",
                Role: "Admin"
            ),
            (
                Email: "owner@trustvault.local",
                Password: "OwnerDemo!2026",
                Role: "Owner"
            ),
            (
                Email: "viewer@trustvault.local",
                Password: "ViewerDemo!2026",
                Role: "Viewer"
            )
        };

        foreach (var account in demoAccounts)
        {
            var email = account.Email.ToLowerInvariant();

            if (!db.Users.Any(u => u.Email == email))
            {
                var user = new UserAccount
                {
                    Email = email,
                    Role = account.Role
                };

                user.PasswordHash = hasher.HashPassword(
                    user,
                    account.Password);

                db.Users.Add(user);
            }
        }

        db.SaveChanges();
    }
    else
    {
        var email = builder.Configuration["Bootstrap:OwnerEmail"]
            ?.Trim()
            .ToLowerInvariant();
        var password = builder.Configuration["Bootstrap:OwnerPassword"];

        if (string.IsNullOrWhiteSpace(email) &&
            string.IsNullOrWhiteSpace(password))
        {
            app.Logger.LogWarning(
                "Initial owner provisioning was skipped because " +
                "bootstrap credentials are not configured.");
        }
        else if (string.IsNullOrWhiteSpace(email) ||
                 string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(
                "Set both Bootstrap__OwnerEmail and " +
                "Bootstrap__OwnerPassword to provision the initial owner.");
        }
        else
        {
            if (email.Length > 254 || password.Length < 12 ||
                password.Length > 128)
            {
                throw new InvalidOperationException(
                    "The initial owner email or password does not meet " +
                    "the required length constraints.");
            }

            try
            {
                var parsedEmail = new MailAddress(email);

                if (!string.Equals(
                        parsedEmail.Address,
                        email,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Bootstrap__OwnerEmail must be a valid email address.");
                }
            }
            catch (FormatException exception)
            {
                throw new InvalidOperationException(
                    "Bootstrap__OwnerEmail must be a valid email address.",
                    exception);
            }

            if (!db.Users.Any(u => u.Email == email))
            {
                var user = new UserAccount
                {
                    Email = email,
                    Role = "Owner"
                };

                user.PasswordHash = scope.ServiceProvider
                    .GetRequiredService<IPasswordHasher<UserAccount>>()
                    .HashPassword(user, password);

                db.Users.Add(user);
                db.AuditEvents.Add(new AuditEvent
                {
                    ActorId = user.Id,
                    Action = "InitialOwnerProvisioned",
                    Outcome = "Allowed",
                    Details =
                        "Initial owner provisioned from deployment configuration"
                });
                db.SaveChanges();
            }
        }
    }
}

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseHttpsRedirection();
app.UseCors("Frontend");
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsProduction())
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
}

// ============================================================
// HEALTH
// ============================================================

app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    application = "TrustVault API"
}));

// ============================================================
// REGISTER
// ============================================================

app.MapPost("/api/auth/register",
    (RegisterRequest request,
     AppDbContext db,
     IPasswordHasher<UserAccount> hasher,
     HttpContext context) =>
{
    var isDevelopment = app.Environment.IsDevelopment();

    var email = request.Email?.Trim().ToLowerInvariant() ?? "";
    var password = request.Password ?? "";

    if (string.IsNullOrWhiteSpace(email) ||
        email.Length > 254 ||
        string.IsNullOrWhiteSpace(password))
    {
        return Results.BadRequest(new
        {
            message = "Enter a valid email and password."
        });
    }

    if (password.Length < 8 || password.Length > 128)
    {
        return Results.BadRequest(new
        {
            message = "Password must contain 8 to 128 characters."
        });
    }

    try
    {
        var parsedEmail = new MailAddress(email);

        if (!string.Equals(
                parsedEmail.Address,
                email,
                StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new
            {
                message = "Enter a valid email address."
            });
        }
    }
    catch (FormatException)
    {
        return Results.BadRequest(new
        {
            message = "Enter a valid email address."
        });
    }

    if (db.Users.Any(u => u.Email == email))
    {
        return Results.Conflict(new
        {
            message =
                "An account with this email already exists. Please sign in."
        });
    }

    var user = new UserAccount
    {
        Email = email,
        Role = "Owner"
    };

    user.PasswordHash = hasher.HashPassword(user, password);

    db.Users.Add(user);

    db.AuditEvents.Add(new AuditEvent
    {
        ActorId = user.Id,
        Action = "Register",
        Outcome = "Allowed",
        RequestId = context.TraceIdentifier,
        Details = isDevelopment
            ? "Local account registered"
            : "Production account registered"
    });

    try
    {
        db.SaveChanges();
    }
    catch (DbUpdateException)
    {
        return Results.Conflict(new
        {
            message =
                "The account could not be created. It may already exist."
        });
    }

    return CreateAuthResponse(
        user, issuer, audience, signingKey);
})
.AllowAnonymous();

// ============================================================
// LOGIN
// ============================================================

app.MapPost("/api/auth/login",
    (LoginRequest request,
     AppDbContext db,
     IPasswordHasher<UserAccount> hasher,
     HttpContext context) =>
{
    var email = request.Email?.Trim().ToLowerInvariant() ?? "";
    var password = request.Password;

    if (string.IsNullOrWhiteSpace(email) ||
        string.IsNullOrEmpty(password))
    {
        db.AuditEvents.Add(new AuditEvent
        {
            Action = "Login",
            Outcome = "Denied",
            RequestId = context.TraceIdentifier,
            Details = "Email or password missing"
        });

        db.SaveChanges();

        return Results.BadRequest(new
        {
            message = "Email and password are required."
        });
    }

    if (email.Length > 254 || password.Length > 256)
        return Results.Unauthorized();

    var user = db.Users.SingleOrDefault(u => u.Email == email);

    var validPassword = user != null &&
        !string.IsNullOrWhiteSpace(user.PasswordHash) &&
        hasher.VerifyHashedPassword(
            user, user.PasswordHash, password)
        != PasswordVerificationResult.Failed;

    if (!validPassword || user == null)
    {
        db.AuditEvents.Add(new AuditEvent
        {
            Action = "Login",
            Outcome = "Denied",
            RequestId = context.TraceIdentifier,
            Details = "Invalid credentials"
        });

        db.SaveChanges();

        return Results.Unauthorized();
    }

    db.AuditEvents.Add(new AuditEvent
    {
        ActorId = user.Id,
        Action = "Login",
        Outcome = "Allowed",
        RequestId = context.TraceIdentifier,
        Details = $"role={user.Role}"
    });

    db.SaveChanges();

    return CreateAuthResponse(
        user, issuer, audience, signingKey);
})
.AllowAnonymous();

// ============================================================
// FORGOT PASSWORD + RESET PASSWORD WITH EMAIL OTP
// Implemented in PasswordResetEndpoints.cs
// ============================================================

app.MapPasswordResetEndpoints();

// ============================================================
// CURRENT USER
// ============================================================

app.MapGet("/api/auth/me", (ClaimsPrincipal principal) =>
{
    return Results.Ok(new
    {
        id = principal.FindFirst(
            ClaimTypes.NameIdentifier)?.Value,

        email = principal.FindFirst(
            ClaimTypes.Email)?.Value,

        role = principal.FindFirst(
            ClaimTypes.Role)?.Value
    });
})
.RequireAuthorization();

// ============================================================
// ACTIVE DOCUMENTS - OWNED OR SHARED
// ============================================================

app.MapGet("/api/documents",
    (ClaimsPrincipal principal, AppDbContext db) =>
{
    var userId = principal.FindFirst(
        ClaimTypes.NameIdentifier)?.Value;

    if (string.IsNullOrWhiteSpace(userId))
        return Results.Unauthorized();

    var documents = db.Documents
        .Where(d => !d.IsDeleted &&
            (d.OwnerId == userId ||
             db.DocumentGrants.Any(g =>
                 g.DocumentId == d.Id &&
                 g.UserId == userId &&
                 g.RevokedAtUtc == null &&
                 (g.Permission == "Read" ||
                  g.Permission == "Edit"))))
        .OrderByDescending(d => d.CreatedAtUtc)
        .Select(d => new
        {
            d.Id,
            d.Name,
            d.OriginalFileName,
            d.Category,
            d.ContentType,
            d.SizeBytes,
            d.CreatedAtUtc,
            d.UpdatedAtUtc,
            d.IsDeleted,
            d.DeletedAtUtc,
            Access = d.OwnerId == userId ? "Owner" : "Shared"
        })
        .ToList();

    return Results.Ok(documents);
})
.RequireAuthorization();

// ============================================================
// RECYCLE BIN
// ============================================================

app.MapGet("/api/documents/trash",
    (ClaimsPrincipal principal, AppDbContext db) =>
{
    var userId = principal.FindFirst(
        ClaimTypes.NameIdentifier)?.Value;

    if (string.IsNullOrWhiteSpace(userId))
        return Results.Unauthorized();

    var deletedDocuments = db.Documents
        .Where(d => d.OwnerId == userId && d.IsDeleted)
        .OrderByDescending(d => d.DeletedAtUtc)
        .Select(d => new
        {
            d.Id,
            d.Name,
            d.OriginalFileName,
            d.Category,
            d.ContentType,
            d.SizeBytes,
            d.CreatedAtUtc,
            d.UpdatedAtUtc,
            d.IsDeleted,
            d.DeletedAtUtc
        })
        .ToList();

    return Results.Ok(deletedDocuments);
})
.RequireAuthorization();

// ============================================================
// UPLOAD DOCUMENT
// ============================================================

app.MapPost("/api/documents",
    async (HttpRequest request,
           ClaimsPrincipal principal,
           AppDbContext db,
           IWebHostEnvironment environment,
           IConfiguration configuration,
           CancellationToken cancellationToken) =>
{
    var userId = principal.FindFirst(
        ClaimTypes.NameIdentifier)?.Value;

    if (string.IsNullOrWhiteSpace(userId))
        return Results.Unauthorized();

    if (!principal.IsInRole("Owner") &&
        !principal.IsInRole("Admin"))
        return Results.Forbid();

    if (!db.Users.Any(u => u.Id == userId))
        return Results.Unauthorized();

    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new
        {
            message = "Upload must use multipart/form-data."
        });
    }

    var form = await request.ReadFormAsync(cancellationToken);
    var file = form.Files.GetFile("file");

    if (file is null || file.Length == 0)
    {
        return Results.BadRequest(new
        {
            message = "Choose a file to upload."
        });
    }

    const long maxFileSize = 10 * 1024 * 1024;

    if (file.Length > maxFileSize)
    {
        return Results.BadRequest(new
        {
            message = "Maximum file size is 10 MB."
        });
    }

    var originalName = Path.GetFileName(file.FileName);
    var extension = Path.GetExtension(originalName).ToLowerInvariant();

    var allowedExtensions = new HashSet<string>
    {
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".txt"
    };

    if (!allowedExtensions.Contains(extension))
    {
        return Results.BadRequest(new
        {
            message = "This file type is not supported."
        });
    }

    var contentType = extension switch
    {
        ".pdf" => "application/pdf",
        ".doc" => "application/msword",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xls" => "application/vnd.ms-excel",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".txt" => "text/plain",
        _ => "application/octet-stream"
    };

    var storageDirectory = PrivateDocumentStorage.GetDirectory(
        environment, configuration);

    Directory.CreateDirectory(storageDirectory);

    var storageKey = Guid.NewGuid().ToString("N");
    var storagePath = Path.Combine(storageDirectory, storageKey);

    await using (var stream = new FileStream(
        storagePath,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.None,
        81920,
        useAsync: true))
    {
        await file.CopyToAsync(stream, cancellationToken);
    }

    var document = new DocumentRecord
    {
        OwnerId = userId,
        Name = Path.GetFileNameWithoutExtension(originalName),
        OriginalFileName = originalName,
        Category = "General",
        ContentType = contentType,
        SizeBytes = file.Length,
        StorageKey = storageKey,
        CreatedAtUtc = DateTime.UtcNow,
        UpdatedAtUtc = DateTime.UtcNow,
        IsDeleted = false,
        DeletedAtUtc = null
    };

    db.Documents.Add(document);

    db.AuditEvents.Add(new AuditEvent
    {
        ActorId = userId,
        DocumentId = document.Id,
        Action = "DocumentUploaded",
        Outcome = "Allowed",
        RequestId = request.HttpContext.TraceIdentifier,
        Details = "Document uploaded"
    });

    try
    {
        await db.SaveChangesAsync(cancellationToken);
    }
    catch
    {
        if (File.Exists(storagePath))
            File.Delete(storagePath);

        throw;
    }

    return Results.Ok(new
    {
        message = "Document uploaded successfully.",
        document.Id,
        document.OriginalFileName,
        document.SizeBytes,
        document.CreatedAtUtc
    });
})
.RequireAuthorization();

// ============================================================
// DOWNLOAD DOCUMENT
// ============================================================

app.MapGet("/api/documents/{id}/download",
    async (string id,
           ClaimsPrincipal principal,
           AppDbContext db,
           IWebHostEnvironment environment,
           IConfiguration configuration,
           HttpContext context,
           CancellationToken cancellationToken) =>
{
    var userId = principal.FindFirst(
        ClaimTypes.NameIdentifier)?.Value;

    if (string.IsNullOrWhiteSpace(userId))
        return Results.Unauthorized();

    var document = await db.Documents.SingleOrDefaultAsync(
        d => d.Id == id, cancellationToken);

    if (document is null || document.IsDeleted)
        return Results.NotFound();

    var isOwner = document.OwnerId == userId;

    var hasReadPermission = await db.DocumentGrants.AnyAsync(
        g => g.DocumentId == document.Id &&
             g.UserId == userId &&
             g.RevokedAtUtc == null &&
             (g.Permission == "Read" || g.Permission == "Edit"),
        cancellationToken);

    if (!isOwner && !hasReadPermission)
    {
        db.AuditEvents.Add(new AuditEvent
        {
            ActorId = userId,
            DocumentId = document.Id,
            Action = "DocumentDownload",
            Outcome = "Denied",
            RequestId = context.TraceIdentifier,
            Details = "Read permission required"
        });

        await db.SaveChangesAsync(cancellationToken);

        return Results.NotFound();
    }

    var storageDirectory = PrivateDocumentStorage.GetDirectory(
        environment, configuration);

    var filePath = Path.GetFullPath(
        Path.Combine(storageDirectory, document.StorageKey));

    var expectedPrefix = storageDirectory.TrimEnd(
        Path.DirectorySeparatorChar,
        Path.AltDirectorySeparatorChar)
        + Path.DirectorySeparatorChar;

    if (!filePath.StartsWith(
            expectedPrefix, StringComparison.OrdinalIgnoreCase))
        return Results.NotFound();

    if (!File.Exists(filePath))
        return Results.NotFound();

    db.AuditEvents.Add(new AuditEvent
    {
        ActorId = userId,
        DocumentId = document.Id,
        Action = "DocumentDownload",
        Outcome = "Allowed",
        RequestId = context.TraceIdentifier,
        Details = "Download requested"
    });

    await db.SaveChangesAsync(cancellationToken);

    return Results.File(
        filePath,
        document.ContentType,
        document.OriginalFileName,
        enableRangeProcessing: true);
})
.RequireAuthorization();

// ============================================================
// SHARE DOCUMENT
// ============================================================

app.MapPost("/api/documents/{id}/shares",
    (string id,
     ShareDocumentRequest request,
     ClaimsPrincipal principal,
     AppDbContext db,
     HttpContext context) =>
{
    var ownerId = principal.FindFirst(
        ClaimTypes.NameIdentifier)?.Value;

    if (string.IsNullOrWhiteSpace(ownerId))
        return Results.Unauthorized();

    var document = db.Documents.SingleOrDefault(
        d => d.Id == id && !d.IsDeleted);

    if (document is null)
        return Results.NotFound(new
        {
            message = "Active document not found."
        });

    if (document.OwnerId != ownerId)
        return Results.Forbid();

    var email = request.Email?.Trim().ToLowerInvariant() ?? "";

    if (string.IsNullOrWhiteSpace(email) || email.Length > 254)
        return Results.BadRequest(new
        {
            message = "Enter a valid recipient email."
        });

    try
    {
        var parsed = new MailAddress(email);

        if (!string.Equals(
                parsed.Address, email,
                StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new
            {
                message = "Enter a valid email address."
            });
    }
    catch (FormatException)
    {
        return Results.BadRequest(new
        {
            message = "Enter a valid email address."
        });
    }

    var permissionInput = request.Permission?.Trim();

    var permission =
        string.Equals(
            permissionInput, "Read",
            StringComparison.OrdinalIgnoreCase)
            ? "Read"
        : string.Equals(
            permissionInput, "Edit",
            StringComparison.OrdinalIgnoreCase)
            ? "Edit"
        : null;

    if (permission is null)
        return Results.BadRequest(new
        {
            message = "Permission must be Read or Edit."
        });

    var recipient = db.Users.SingleOrDefault(u => u.Email == email);

    if (recipient is null)
        return Results.NotFound(new
        {
            message =
                "Recipient account not found. Ask the user to register first."
        });

    if (recipient.Id == ownerId)
        return Results.BadRequest(new
        {
            message =
                "You cannot share a document with your own account."
        });

    var grant = db.DocumentGrants.SingleOrDefault(
        g => g.DocumentId == document.Id &&
             g.UserId == recipient.Id);

    var now = DateTime.UtcNow;

    if (grant is null)
    {
        grant = new DocumentGrant
        {
            DocumentId = document.Id,
            UserId = recipient.Id,
            Permission = permission,
            GrantedAtUtc = now,
            RevokedAtUtc = null
        };

        db.DocumentGrants.Add(grant);
    }
    else
    {
        grant.Permission = permission;
        grant.GrantedAtUtc = now;
        grant.RevokedAtUtc = null;
    }

    db.AuditEvents.Add(new AuditEvent
    {
        ActorId = ownerId,
        DocumentId = document.Id,
        Action = "DocumentShared",
        Outcome = "Allowed",
        RequestId = context.TraceIdentifier,
        Details = $"Recipient={email}; Permission={permission}"
    });

    db.SaveChanges();

    return Results.Ok(new
    {
        message = "Document shared successfully.",
        share = new
        {
            id = grant.Id,
            documentId = grant.DocumentId,
            recipientEmail = recipient.Email,
            permission = grant.Permission,
            grantedAtUtc = grant.GrantedAtUtc
        }
    });
})
.RequireAuthorization();

// ============================================================
// VIEW ACTIVE SHARES
// ============================================================

app.MapGet("/api/documents/{id}/shares",
    (string id, ClaimsPrincipal principal, AppDbContext db) =>
{
    var ownerId = principal.FindFirst(
        ClaimTypes.NameIdentifier)?.Value;

    if (string.IsNullOrWhiteSpace(ownerId))
        return Results.Unauthorized();

    var document = db.Documents.SingleOrDefault(
        d => d.Id == id && !d.IsDeleted);

    if (document is null)
        return Results.NotFound(new
        {
            message = "Document not found."
        });

    if (document.OwnerId != ownerId)
        return Results.Forbid();

    var shares =
        (from grant in db.DocumentGrants
         join user in db.Users on grant.UserId equals user.Id
         where grant.DocumentId == id &&
               grant.RevokedAtUtc == null
         select new
         {
             grant.Id,
             recipientEmail = user.Email,
             grant.Permission,
             grant.GrantedAtUtc
         }).ToList();

    return Results.Ok(shares);
})
.RequireAuthorization();

// ============================================================
// REVOKE SHARE
// ============================================================

app.MapDelete("/api/documents/{id}/shares/{grantId}",
    (string id,
     string grantId,
     ClaimsPrincipal principal,
     AppDbContext db,
     HttpContext context) =>
{
    var ownerId = principal.FindFirst(
        ClaimTypes.NameIdentifier)?.Value;

    if (string.IsNullOrWhiteSpace(ownerId))
        return Results.Unauthorized();

    var document = db.Documents.SingleOrDefault(d => d.Id == id);

    if (document is null)
        return Results.NotFound(new
        {
            message = "Document not found."
        });

    if (document.OwnerId != ownerId)
        return Results.Forbid();

    var grant = db.DocumentGrants.SingleOrDefault(
        g => g.Id == grantId && g.DocumentId == id);

    if (grant is null)
        return Results.NotFound(new
        {
            message = "Share record not found."
        });

    if (grant.RevokedAtUtc is not null)
        return Results.Ok(new
        {
            message = "Access has already been revoked."
        });

    grant.RevokedAtUtc = DateTime.UtcNow;

    db.AuditEvents.Add(new AuditEvent
    {
        ActorId = ownerId,
        DocumentId = document.Id,
        Action = "ShareRevoked",
        Outcome = "Allowed",
        RequestId = context.TraceIdentifier,
        Details = $"Permission={grant.Permission}"
    });

    db.SaveChanges();

    return Results.Ok(new
    {
        message = "Document access revoked successfully."
    });
})
.RequireAuthorization();

// ============================================================
// ACCESS PERMISSIONS
// ============================================================

app.MapGet("/api/access/permissions",
    (ClaimsPrincipal principal, AppDbContext db) =>
{
    var userId = principal.FindFirst(
        ClaimTypes.NameIdentifier)?.Value;

    if (string.IsNullOrWhiteSpace(userId))
        return Results.Unauthorized();

    var isAdmin = principal.IsInRole("Admin");

    var permissions =
        (from grant in db.DocumentGrants.AsNoTracking()
         join document in db.Documents.AsNoTracking()
             on grant.DocumentId equals document.Id
         join recipient in db.Users.AsNoTracking()
             on grant.UserId equals recipient.Id
         where isAdmin || document.OwnerId == userId
         orderby grant.GrantedAtUtc descending
         select new
         {
             id = grant.Id,
             documentId = document.Id,
             documentName = document.Name,
             recipientEmail = recipient.Email,
             permission = grant.Permission,
             grantedAtUtc = grant.GrantedAtUtc,
             revokedAtUtc = grant.RevokedAtUtc,
             status = grant.RevokedAtUtc == null
                 ? "Active" : "Revoked",
             canRevoke = document.OwnerId == userId
         }).Take(500).ToList();

    return Results.Ok(permissions);
})
.RequireAuthorization();

// ============================================================
// AUDIT ACTIVITY
// ============================================================

app.MapGet("/api/audit",
    (ClaimsPrincipal principal, AppDbContext db) =>
{
    var userId = principal.FindFirst(
        ClaimTypes.NameIdentifier)?.Value;

    if (string.IsNullOrWhiteSpace(userId))
        return Results.Unauthorized();

    var isAdmin = principal.IsInRole("Admin");

    var events = db.AuditEvents
        .AsNoTracking()
        .Where(a =>
            isAdmin ||
            a.ActorId == userId ||
            (a.DocumentId != null &&
             db.Documents.Any(d =>
                 d.Id == a.DocumentId &&
                 d.OwnerId == userId)))
        .OrderByDescending(a => a.CreatedAtUtc)
        .Take(200)
        .Select(a => new
        {
            a.Id,
            a.Action,
            a.Outcome,
            a.CreatedAtUtc,
            a.RequestId,
            a.Details,
            actorEmail = db.Users
                .Where(u => u.Id == a.ActorId)
                .Select(u => u.Email)
                .FirstOrDefault(),
            documentName = db.Documents
                .Where(d => d.Id == a.DocumentId)
                .Select(d => d.Name)
                .FirstOrDefault()
        }).ToList();

    return Results.Ok(events);
})
.RequireAuthorization();

// ============================================================
// SOFT DELETE - MOVE TO RECYCLE BIN
// ============================================================

app.MapDelete("/api/documents/{id}",
    async (string id,
           ClaimsPrincipal principal,
           AppDbContext db,
           HttpContext context,
           CancellationToken cancellationToken) =>
{
    var userId = principal.FindFirst(
        ClaimTypes.NameIdentifier)?.Value;

    if (string.IsNullOrWhiteSpace(userId))
        return Results.Unauthorized();

    var document = await db.Documents.SingleOrDefaultAsync(
        d => d.Id == id, cancellationToken);

    if (document is null)
        return Results.NotFound(new
        {
            message = "Document not found."
        });

    if (document.OwnerId != userId)
        return Results.Forbid();

    if (document.IsDeleted)
    {
        return Results.Ok(new
        {
            message = "Document is already in the Recycle Bin.",
            documentId = document.Id,
            document.IsDeleted,
            document.DeletedAtUtc
        });
    }

    document.IsDeleted = true;
    document.DeletedAtUtc = DateTime.UtcNow;
    document.UpdatedAtUtc = DateTime.UtcNow;

    db.AuditEvents.Add(new AuditEvent
    {
        ActorId = userId,
        DocumentId = document.Id,
        Action = "DocumentMovedToRecycleBin",
        Outcome = "Allowed",
        RequestId = context.TraceIdentifier,
        Details = $"Document={document.OriginalFileName}"
    });

    await db.SaveChangesAsync(cancellationToken);

    return Results.Ok(new
    {
        message = "Document moved to Recycle Bin. You can restore it within 30 days.",
        documentId = document.Id,
        document.IsDeleted,
        document.DeletedAtUtc
    });
})
.RequireAuthorization();

// ============================================================
// RESTORE DOCUMENT - ALLOWED ONLY WITHIN 30 DAYS
// ============================================================
app.MapPost("/api/documents/{id}/restore",
    async (string id,
           ClaimsPrincipal principal,
           AppDbContext db,
           HttpContext context,
           CancellationToken cancellationToken) =>
{
    var userId = principal.FindFirst(
        ClaimTypes.NameIdentifier)?.Value;

    if (string.IsNullOrWhiteSpace(userId))
        return Results.Unauthorized();

    var document = await db.Documents.SingleOrDefaultAsync(
        d => d.Id == id, cancellationToken);

    if (document is null)
        return Results.NotFound(new
        {
            message = "Document not found."
        });

    if (document.OwnerId != userId)
        return Results.Forbid();

    if (!document.IsDeleted)
    {
        return Results.BadRequest(new
        {
            message = "This document is not in the Recycle Bin."
        });
    }

    var deletedAt = document.DeletedAtUtc
        ?? document.UpdatedAtUtc;

    if (DateTime.UtcNow - deletedAt >= TimeSpan.FromDays(30))
    {
        return Results.StatusCode(StatusCodes.Status410Gone);
    }

    document.IsDeleted = false;
    document.DeletedAtUtc = null;
    document.UpdatedAtUtc = DateTime.UtcNow;

    db.AuditEvents.Add(new AuditEvent
    {
        ActorId = userId,
        DocumentId = document.Id,
        Action = "DocumentRestored",
        Outcome = "Allowed",
        RequestId = context.TraceIdentifier,
        Details = $"Document={document.OriginalFileName}"
    });

    await db.SaveChangesAsync(cancellationToken);

    return Results.Ok(new
    {
        message = "Document restored successfully.",
        documentId = document.Id,
        document.IsDeleted,
        document.UpdatedAtUtc
    });
})
.RequireAuthorization();

// ============================================================
// PERMANENT DELETE - MANUAL DELETE FROM RECYCLE BIN
// ============================================================

app.MapDelete("/api/documents/{id}/permanent",
    async (string id,
           ClaimsPrincipal principal,
           AppDbContext db,
           IWebHostEnvironment environment,
           IConfiguration configuration,
           HttpContext context,
           CancellationToken cancellationToken) =>
{
    var userId = principal.FindFirst(
        ClaimTypes.NameIdentifier)?.Value;

    if (string.IsNullOrWhiteSpace(userId))
        return Results.Unauthorized();

    var document = await db.Documents.SingleOrDefaultAsync(
        d => d.Id == id, cancellationToken);

    if (document is null)
        return Results.NotFound(new
        {
            message = "Document not found."
        });

    if (document.OwnerId != userId)
        return Results.Forbid();

    if (!document.IsDeleted)
    {
        return Results.BadRequest(new
        {
            message =
                "Move the document to Recycle Bin before permanently deleting it."
        });
    }

    if (string.IsNullOrWhiteSpace(document.StorageKey))
    {
        return Results.Problem(
            "The document storage reference is invalid.",
            statusCode: StatusCodes.Status500InternalServerError);
    }

    var storageDirectory = PrivateDocumentStorage.GetDirectory(
        environment, configuration);

    var filePath = Path.GetFullPath(
        Path.Combine(storageDirectory, document.StorageKey));

    var expectedPrefix = storageDirectory.TrimEnd(
        Path.DirectorySeparatorChar,
        Path.AltDirectorySeparatorChar)
        + Path.DirectorySeparatorChar;

    if (!filePath.StartsWith(
            expectedPrefix, StringComparison.OrdinalIgnoreCase))
    {
        return Results.Problem(
            "Invalid document storage path.",
            statusCode: StatusCodes.Status500InternalServerError);
    }

    var grants = await db.DocumentGrants
        .Where(g => g.DocumentId == document.Id)
        .ToListAsync(cancellationToken);

    db.DocumentGrants.RemoveRange(grants);

    var auditEvents = await db.AuditEvents
        .Where(a => a.DocumentId == document.Id)
        .ToListAsync(cancellationToken);

    foreach (var auditEvent in auditEvents)
    {
        auditEvent.DocumentId = null;

        var fileContext =
            $"Document={document.OriginalFileName}; DocumentId={document.Id}";

        auditEvent.Details =
            string.IsNullOrWhiteSpace(auditEvent.Details)
                ? fileContext
                : $"{auditEvent.Details}; {fileContext}";
    }

    var documentId = document.Id;
    var fileName = document.OriginalFileName;

    db.Documents.Remove(document);

    db.AuditEvents.Add(new AuditEvent
    {
        ActorId = userId,
        DocumentId = null,
        Action = "DocumentPermanentlyDeleted",
        Outcome = "Allowed",
        RequestId = context.TraceIdentifier,
        Details = $"DocumentId={documentId}; FileName={fileName}"
    });

    await db.SaveChangesAsync(cancellationToken);

    try
    {
        if (File.Exists(filePath))
            File.Delete(filePath);
    }
    catch (IOException)
    {
        return Results.Ok(new
        {
            message =
                "Database record deleted, but stored-file cleanup failed.",
            fileCleanupCompleted = false
        });
    }
    catch (UnauthorizedAccessException)
    {
        return Results.Ok(new
        {
            message =
                "Database record deleted, but stored-file cleanup failed.",
            fileCleanupCompleted = false
        });
    }

    return Results.Ok(new
    {
        message = "Document permanently deleted.",
        fileCleanupCompleted = true
    });
})
.RequireAuthorization();

if (app.Environment.IsProduction())
{
    app.MapFallback("/{*path:nonfile}",
        (HttpContext context, IWebHostEnvironment environment) =>
        {
            if (context.Request.Path.StartsWithSegments("/api") ||
                context.Request.Path.StartsWithSegments("/health"))
            {
                return Results.NotFound();
            }

            return Results.File(
                Path.Combine(environment.WebRootPath, "index.html"),
                "text/html");
        });
}

// ============================================================
// START API
// ============================================================

app.Run();

// ============================================================
// HELPERS
// ============================================================

static IResult CreateAuthResponse(
    UserAccount user,
    string issuer,
    string audience,
    SymmetricSecurityKey signingKey)
{
    var expiresAt = DateTime.UtcNow.AddMinutes(30);

    var claims = new[]
    {
        new Claim("sub", user.Id),
        new Claim(ClaimTypes.NameIdentifier, user.Id),
        new Claim(ClaimTypes.Name, user.Email),
        new Claim(ClaimTypes.Email, user.Email),
        new Claim(ClaimTypes.Role, user.Role)
    };

    var token = new JsonWebTokenHandler().CreateToken(
        new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Subject = new ClaimsIdentity(claims),
            NotBefore = DateTime.UtcNow,
            Expires = expiresAt,
            SigningCredentials = new SigningCredentials(
                signingKey, SecurityAlgorithms.HmacSha256)
        });

    return Results.Ok(new
    {
        accessToken = token,
        tokenType = "Bearer",
        expiresAtUtc = expiresAt,
        user = new
        {
            id = user.Id,
            email = user.Email,
            role = user.Role
        }
    });
}

// ============================================================
// REQUEST MODELS
// ============================================================

public sealed record RegisterRequest(
    string? Email,
    string? Password);

public sealed record LoginRequest(
    string? Email,
    string? Password);

public sealed record ShareDocumentRequest(
    string? Email,
    string? Permission);

public partial class Program
{
}
