namespace TrustVault.Api;

public static class PrivateDocumentStorage
{
    public static string GetDirectory(
        IWebHostEnvironment environment,
        IConfiguration configuration)
    {
        var configuredPath = configuration["Storage:DocumentsPath"];

        return Path.GetFullPath(
            string.IsNullOrWhiteSpace(configuredPath)
                ? Path.Combine(
                    environment.ContentRootPath,
                    "PrivateDocuments")
                : configuredPath);
    }
}
